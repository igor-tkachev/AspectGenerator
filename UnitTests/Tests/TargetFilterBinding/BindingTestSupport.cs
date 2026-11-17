using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

using AspectGenerator.Cascade;
using AspectGenerator.TargetFilterBinding;
using AspectGenerator.TargetFilterFacts;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilterBinding
{
	/// <summary>
	/// Reads a type written as text into the structure the patterns match, for facts written by hand:
	/// <c>System.Collections.Generic.List&lt;int&gt;</c>, <c>Outer&lt;A&gt;.Inner[][,]</c>, <c>(int, string)</c>, and
	/// <c>!T</c> for a type parameter. C#'s names for the system types are read as those types.
	/// </summary>
	static class ShapeText
	{
		static readonly Dictionary<string,string> _aliases = new()
		{
			["bool"] = "Boolean", ["byte"] = "Byte", ["sbyte"] = "SByte", ["char"] = "Char", ["decimal"] = "Decimal",
			["double"] = "Double", ["float"] = "Single", ["int"] = "Int32", ["uint"] = "UInt32", ["nint"] = "IntPtr",
			["nuint"] = "UIntPtr", ["long"] = "Int64", ["ulong"] = "UInt64", ["short"] = "Int16", ["ushort"] = "UInt16",
			["object"] = "Object", ["string"] = "String", ["void"] = "Void",
		};

		static readonly HashSet<string> _valueTypes =
		[
			"Boolean", "Byte", "SByte", "Char", "Decimal", "Double", "Single", "Int32", "UInt32", "IntPtr", "UIntPtr",
			"Int64", "UInt64", "Int16", "UInt16", "Void", "Nullable", "ValueTuple", "Guid", "DateTime", "TimeSpan",
		];

		public static TypeShape Parse(string text)
		{
			var position = 0;
			var type     = Type(text, ref position);

			if (position != text.Length)
				throw new FormatException($"'{text}': unexpected '{text[position..]}'.");

			return type;
		}

		public static NamedTypeShape ParseNamed(string text)
		{
			return (NamedTypeShape)Parse(text);
		}

		public static ImmutableArray<NameSegment> Segments(string dotted)
		{
			return dotted.Length == 0 ? [] : [..dotted.Split('.').Select(static s => new NameSegment(s))];
		}

		static TypeShape Type(string text, ref int position)
		{
			SkipSpaces(text, ref position);

			TypeShape type;

			if (text[position] == '!')
			{
				position++;
				type = new TypeParameterShape(Identifier(text, ref position));
			}
			else if (text[position] == '(')
			{
				position++;

				var elements = List(text, ref position, ')');

				type = new NamedTypeShape([new NameSegment("System"), new NameSegment("ValueTuple", elements)], false, null, elements);
			}
			else
			{
				type = Named(text, ref position);
			}

			var ranks = new List<int>();

			while (position < text.Length && text[position] == '[')
			{
				var rank = 1;

				for (position++; text[position] == ','; position++)
					rank++;

				position++;
				ranks.Add(rank);
			}

			// The rank specifiers stand in C# order, the outermost first.
			for (var i = ranks.Count - 1; i >= 0; i--)
				type = new ArrayTypeShape(type, ranks[i]);

			return type;
		}

		static NamedTypeShape Named(string text, ref int position)
		{
			var segments = ImmutableArray.CreateBuilder<NameSegment>();

			while (true)
			{
				var name      = Identifier(text, ref position);
				var arguments = ImmutableArray<TypeShape>.Empty;

				if (position < text.Length && text[position] == '<')
				{
					position++;
					arguments = List(text, ref position, '>');
				}

				segments.Add(new NameSegment(name, arguments));

				if (position >= text.Length || text[position] != '.')
					break;

				position++;
			}

			if (segments.Count == 1 && segments[0].TypeArguments.IsEmpty && _aliases.TryGetValue(segments[0].Name, out var clr))
			{
				segments.Clear();
				segments.Add(new NameSegment("System"));
				segments.Add(new NameSegment(clr));
			}

			var last        = segments[^1];
			var isSystem    = segments.Count == 2 && segments[0].Name == "System";
			var isValueType = isSystem && _valueTypes.Contains(last.Name);
			var underlying  = isSystem && last.Name == "Nullable" && last.TypeArguments.Length == 1 ? last.TypeArguments[0] : null;
			var elements    = isSystem && last.Name == "ValueTuple" ? last.TypeArguments : default;

			return new NamedTypeShape(segments.ToImmutable(), !isValueType, underlying, elements);
		}

		static ImmutableArray<TypeShape> List(string text, ref int position, char close)
		{
			var items = ImmutableArray.CreateBuilder<TypeShape>();

			while (true)
			{
				items.Add(Type(text, ref position));
				SkipSpaces(text, ref position);

				if (text[position] == close)
				{
					position++;
					return items.ToImmutable();
				}

				if (text[position] != ',')
					throw new FormatException($"'{text}': ',' or '{close}' expected at {position}.");

				position++;
			}
		}

		static string Identifier(string text, ref int position)
		{
			var start = position;

			while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] is '_' or '$' or '`'))
				position++;

			if (position == start)
				throw new FormatException($"'{text}': a name expected at {position}.");

			return text[start..position];
		}

		static void SkipSpaces(string text, ref int position)
		{
			while (position < text.Length && text[position] == ' ')
				position++;
		}
	}

	/// <summary>The facts of a method written by hand.</summary>
	sealed class RecordFacts : IMethodFacts
	{
		public RecordFacts(string type, string method, string returns = "void", params string[] parameters)
			: this(ShapeText.ParseNamed(type), MethodSegment(method), ShapeText.Parse(returns), [..parameters.Select(ParseParameter)])
		{
		}

		public RecordFacts(NamedTypeShape type, NameSegment method, TypeShape returns, ImmutableArray<ParameterShape> parameters)
		{
			ContainingType = type;
			Method         = method;
			ReturnType     = returns;
			Parameters     = parameters;
			InheritedTypes = [ContainingType];
			OverrideChain  = [new MethodShape(ContainingType, Method)];
		}

		/// <summary>How many of the type's segments are namespaces; all but the last unless set.</summary>
		public int NamespaceSegments { get; init; } = -1;

		public ImmutableArray<NameSegment> Namespace
		{
			get
			{
				var count = NamespaceSegments < 0 ? ContainingType.Segments.Length - 1 : NamespaceSegments;

				return [..ContainingType.Segments.Take(count)];
			}
		}

		public NamedTypeShape                 ContainingType       { get; }
		public NameSegment                    Method               { get; }
		public TypeShape                      ReturnType           { get; }
		public ReturnRef                      ReturnRef            { get; init; }
		public ImmutableArray<ParameterShape> Parameters           { get; }
		public ImmutableArray<NamedTypeShape> Attributes           { get; init; } = [];
		public ImmutableArray<NamedTypeShape> InheritedTypes       { get; init; }
		public ImmutableArray<MethodShape>    ImplementedMembers   { get; init; } = [];
		public ImmutableArray<MethodShape>    OverrideChain        { get; init; }
		public Accessibility                  Accessibility        { get; init; } = Accessibility.Public;
		public Virtuality                     Virtuality           { get; init; }
		public bool                           IsStatic             { get; init; }
		public bool                           IsExtern             { get; init; }
		public bool                           IsExtension          { get; init; }
		public bool                           IsGenerated          { get; init; }
		public bool                           IsProject            { get; init; } = true;
		public bool                           IsAsync              { get; init; }
		public bool                           IsAsyncDecidedByCall { get; init; }

		/// <summary><c>Name</c> or <c>Name&lt;T1, T2&gt;</c>.</summary>
		public static NameSegment MethodSegment(string text)
		{
			var open = text.IndexOf('<');

			if (open < 0)
				return new NameSegment(text);

			// The arguments are read as those of a type.
			return new NameSegment(text[..open], ShapeText.ParseNamed("X" + text[open..]).Segments[0].TypeArguments);
		}

		public static MethodShape MethodShape(string type, string method)
		{
			return new MethodShape(ShapeText.ParseNamed(type), MethodSegment(method));
		}

		static ParameterShape ParseParameter(string text)
		{
			var modifiers = ParameterModifiers.None;

			while (true)
			{
				var (word, flag) = text switch
				{
					_ when text.StartsWith("ref readonly ", StringComparison.Ordinal) => ("ref readonly ", ParameterModifiers.RefReadOnly),
					_ when text.StartsWith("ref ",          StringComparison.Ordinal) => ("ref ",          ParameterModifiers.Ref),
					_ when text.StartsWith("out ",          StringComparison.Ordinal) => ("out ",          ParameterModifiers.Out),
					_ when text.StartsWith("in ",           StringComparison.Ordinal) => ("in ",           ParameterModifiers.In),
					_ when text.StartsWith("params ",       StringComparison.Ordinal) => ("params ",       ParameterModifiers.Params),
					_ when text.StartsWith("this ",         StringComparison.Ordinal) => ("this ",         ParameterModifiers.This),
					_                                                                 => ("",              ParameterModifiers.None),
				};

				if (word.Length == 0)
					return new ParameterShape(ShapeText.Parse(text), modifiers);

				modifiers |= flag;
				text       = text[word.Length..];
			}
		}
	}

	/// <summary>Evaluates filter text on facts, and reads the methods and calls of a piece of C# into facts.</summary>
	static class FilterProbe
	{
		/// <summary>
		/// The answer of a filter of one line and no reference on a method: every declaration of the line holds, or one
		/// does not, or one cannot say for every call of the definition.
		/// </summary>
		public static Tri Answer(string filter, IMethodFacts facts, MatchPhase phase = MatchPhase.Call, bool? overridesIsKey = null)
		{
			var block = overridesIsKey is { } isKey
				? FilterBlockBuilder.Build(CascadeLevel.Aspect, [filter], overridesIsKey: isKey)
				: FilterBlockBuilder.Build(CascadeLevel.Aspect, [filter]);

			Assert.AreEqual(0, block.Diagnostics.Length, $"'{filter}': {string.Join("; ", block.Diagnostics)}");
			Assert.AreNotEqual(0, block.Block.Declarations.Length, $"'{filter}' declares nothing.");

			var target = phase == MatchPhase.Definition ? FilterTarget.Definition(facts) : FilterTarget.Call(facts);
			var holds  = true;

			// Every declaration is asked, as nothing here stops at the first that fails: an answer that depends on the call
			// is seen wherever it stands.
			foreach (var declaration in block.Block.Declarations)
				holds &= declaration.IsSatisfied(target, null!);

			return target.DependsOnCall ? Tri.Depends : holds.ToTri();
		}

		public static IEnumerable<MetadataReference> Framework()
		{
			var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;

			return trusted
				.Split(Path.PathSeparator)
				.Where(static path => !Path.GetFileName(path).StartsWith("AspectGenerator.", StringComparison.OrdinalIgnoreCase))
				.Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path));
		}

		public static CSharpCompilation Compile(string source, string name = "Probe", params MetadataReference[] references)
		{
			var compilation = CSharpCompilation.Create(
				name,
				[CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), name + ".cs")],
				Framework().Concat(references),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: true));

			var errors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).Select(static d => d.ToString()).ToList();

			Assert.AreEqual(0, errors.Count, string.Join(Environment.NewLine, errors));

			return compilation;
		}
	}

	/// <summary>The methods and calls of one compilation as facts.</summary>
	sealed class ProbeCompilation
	{
		readonly TypeShapes _shapes = new();

		public ProbeCompilation(string source, params MetadataReference[] references)
		{
			Compilation = FilterProbe.Compile(source, "Probe", references);
			Context     = new FactContext(Compilation);
		}

		public CSharpCompilation Compilation { get; }
		public FactContext       Context     { get; }

		/// <summary>The method <c>Namespace.Type.Method</c>, by metadata name of the type (<c>Box`1</c>).</summary>
		public IMethodSymbol Method(string type, string name)
		{
			var symbol = Compilation.GetTypeByMetadataName(type) ?? throw new InvalidOperationException($"No type '{type}'.");

			return symbol.GetMembers(name).OfType<IMethodSymbol>().Single();
		}

		public SymbolMethodFacts Definition(IMethodSymbol method)
		{
			return new SymbolMethodFacts(MethodFacts.CreateDefinition(method, FactPlan.All, Context), Context, _shapes);
		}

		public SymbolMethodFacts Definition(string type, string name)
		{
			return Definition(Method(type, name));
		}

		/// <summary>Every call in the compilation's source, with the method it binds to, in text order.</summary>
		public IEnumerable<(InvocationExpressionSyntax Syntax, IMethodSymbol Method)> Calls()
		{
			foreach (var tree in Compilation.SyntaxTrees)
			{
				var model = Compilation.GetSemanticModel(tree);

				foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
					if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method)
						yield return (invocation, method);
			}
		}

		/// <summary>The facts of the call written <paramref name="text"/> (the first such call).</summary>
		public SymbolMethodFacts Call(string text)
		{
			var (syntax, method) = Calls().First(c => c.Syntax.ToString() == text);

			return Call(syntax, method);
		}

		public SymbolMethodFacts Call(InvocationExpressionSyntax syntax, IMethodSymbol method)
		{
			return new SymbolMethodFacts(MethodFacts.Create(method, FactPlan.All, Context, syntax), Context, _shapes);
		}
	}
}
