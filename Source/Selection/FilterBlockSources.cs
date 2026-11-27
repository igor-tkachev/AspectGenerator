using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using AspectGenerator.Cascade;
using AspectGenerator.TargetFilterBinding;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspectGenerator.Selection
{
	/// <summary>
	/// Where a block of the cascade was written: the <see cref="CascadeBlock{TFact}.Source"/> of every block
	/// <see cref="FilterBlockSources"/> builds. It keeps what is needed to place a diagnostic of the block and to build the
	/// aspect from its settings; it computes no locations itself.
	/// </summary>
	sealed class FilterBlockSource
	{
		public FilterBlockSource(
			CascadeLevel                      level,
			INamedTypeSymbol                  aspectClass,
			ISymbol                           target,
			AttributeData?                    attribute,
			AttributeSyntax?                  syntax,
			ImmutableArray<string>            texts,
			ImmutableArray<ExpressionSyntax?> items)
		{
			Level       = level;
			AspectClass = aspectClass;
			Target      = target;
			Attribute   = attribute;
			Syntax      = syntax;
			Texts       = texts;
			Items       = items;
		}

		public CascadeLevel     Level       { get; }
		public INamedTypeSymbol AspectClass { get; }

		/// <summary>What the attribute stands on: the assembly, the type or the method; the aspect class for the aspect's default.</summary>
		public ISymbol Target { get; }

		/// <summary>The assembly the attribute was written in: for a block read from a library, the library.</summary>
		public IAssemblySymbol Assembly => Target as IAssemblySymbol ?? Target.ContainingAssembly;

		/// <summary>
		/// The application of the aspect, or for the aspect's default its <c>[Aspect]</c> attribute. <see langword="null"/>
		/// only for the default of an aspect whose <c>[Aspect]</c> attribute the compiler could not bind: such a default has
		/// no lines.
		/// </summary>
		public AttributeData? Attribute { get; }

		/// <summary>The attribute as written in the source; <see langword="null"/> for one read from a referenced assembly.</summary>
		public AttributeSyntax? Syntax { get; }

		public bool IsFromMetadata => Syntax is null;

		/// <summary>The items of the filter as the block read them: the one string, or the elements of a <c>string[]</c>.</summary>
		public ImmutableArray<string> Texts { get; }

		/// <summary>
		/// For each item of <see cref="Texts"/>, the expression it is written as: the argument of a filter written as one
		/// string, the element of an array; <see langword="null"/> where there is none (an attribute read from metadata, an
		/// array that is not written element by element).
		/// </summary>
		public ImmutableArray<ExpressionSyntax?> Items { get; }

		public override string ToString()
		{
			return Attribute?.ToString() ?? AspectClass.ToDisplayString();
		}
	}

	/// <summary>
	/// Turns the attributes of an aspect into blocks of the cascade, one block per attribute, on each level:
	/// <list type="bullet">
	/// <item>the aspect's default: the <c>DefaultTargetFilter</c> of its <c>[Aspect]</c> attribute, declared in the source or in
	/// a referenced assembly;</item>
	/// <item>the assembly: each <c>[assembly: X]</c>;</item>
	/// <item>the type: each attribute of the aspect on the type, on any of its <c>partial</c> declarations;</item>
	/// <item>the method: each attribute of the aspect on a method or a local function. Without a <c>TargetFilter</c> the block
	/// declares only what its level declares implicitly (<c>location</c> and <c>method</c>); with one it is an ordinary method
	/// block.</item>
	/// </list>
	/// The same reading serves the attributes of a referenced library, as its own assembly and type levels, whether the
	/// reference is an implementation or a reference assembly.
	/// <para>
	/// A filter is <c>TargetFilter</c> as a <c>string</c> (one item) or a <c>string[]</c> (an item per element; a
	/// <see langword="null"/> element is an empty item), with its constants as the compiler folded them. The other arguments
	/// the attribute writes are the block's settings (<see cref="WrittenSettings"/>).
	/// </para>
	/// </summary>
	static class FilterBlockSources
	{
		public const string TargetFilterArgument        = "TargetFilter";
		public const string DefaultTargetFilterArgument = "DefaultTargetFilter";

		/// <summary>
		/// The name all constructor arguments of an attribute share as one setting. No named argument can have it: it is not
		/// an identifier.
		/// </summary>
		public const string ConstructorSetting = ".ctor";

		/// <summary>The aspect's default, from the definition the registry found for the aspect.</summary>
		public static FilterBlock AspectDefault(AttributeInfo definition)
		{
			var data = definition.AspectDefinitionData ??
				FindApplication(definition.AspectDefinitionSyntax, definition.AspectDefinitionSemanticModel);

			// The generated aspect API is output of this generator, so the compilation it reads does not hold it and an [Aspect]
			// written in the source does not bind: its attribute data has no arguments. Its default is then read as the
			// registry read it, from the written argument.
			if ((data is null || data.AttributeClass is null or { TypeKind: TypeKind.Error }) &&
				definition is { AspectDefinitionSyntax: {} written, DefaultTargetFilter: {} text })
			{
				var label  = $"DefaultTargetFilter of {AspectName(definition.AttributeClass)}";
				var items  = ImmutableArray.Create(NamedArgumentExpression(written, DefaultTargetFilterArgument));
				var source = new FilterBlockSource(CascadeLevel.Aspect, definition.AttributeClass, definition.AttributeClass.OriginalDefinition, null, written, [text], items);

				return FilterBlockBuilder.Build(CascadeLevel.Aspect, [text], null, label, source);
			}

			return AspectDefault(definition.AttributeClass, data);
		}

		/// <param name="definition">The <c>[Aspect]</c> attribute of <paramref name="aspectClass"/>.</param>
		public static FilterBlock AspectDefault(INamedTypeSymbol aspectClass, AttributeData? definition)
		{
			var syntax = Syntax(definition);
			var texts  = ImmutableArray<string>.Empty;
			var items  = ImmutableArray<ExpressionSyntax?>.Empty;

			if (definition is not null)
				(texts, items) = ReadFilter(definition, syntax, DefaultTargetFilterArgument);

			var label  = $"DefaultTargetFilter of {AspectName(aspectClass)}{(definition is null ? "" : Where(aspectClass, syntax))}";
			var source = new FilterBlockSource(CascadeLevel.Aspect, aspectClass, aspectClass.OriginalDefinition, definition, syntax, texts, items);

			return FilterBlockBuilder.Build(CascadeLevel.Aspect, texts, null, label, source);
		}

		/// <summary>
		/// The blocks of every <c>[assembly: X]</c> of the aspect: of the compilation for its own assembly, of a library read
		/// from its metadata.
		/// </summary>
		/// <param name="compilation">The compilation being built: it decides whether a conditional aspect written in it is on.</param>
		public static ImmutableArray<FilterBlock> Assembly(IAssemblySymbol assembly, INamedTypeSymbol aspectClass, Compilation compilation)
		{
			return Applications(CascadeLevel.Assembly, assembly, assembly.GetAttributes(), aspectClass, compilation);
		}

		/// <summary>The blocks of the attributes of the aspect on the type, of all its <c>partial</c> declarations.</summary>
		public static ImmutableArray<FilterBlock> Type(INamedTypeSymbol type, INamedTypeSymbol aspectClass, Compilation compilation)
		{
			return Applications(CascadeLevel.Type, type, type.GetAttributes(), aspectClass, compilation);
		}

		/// <summary>The blocks of the attributes of the aspect on a method or a local function.</summary>
		public static ImmutableArray<FilterBlock> Method(IMethodSymbol method, INamedTypeSymbol aspectClass, Compilation compilation)
		{
			return Applications(CascadeLevel.Method, method, method.GetAttributes(), aspectClass, compilation);
		}

		static ImmutableArray<FilterBlock> Applications(
			CascadeLevel                  level,
			ISymbol                       target,
			ImmutableArray<AttributeData> attributes,
			INamedTypeSymbol              aspectClass,
			Compilation                   compilation)
		{
			var applications = attributes
				.Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, aspectClass))
				// A conditional aspect written in the source is off where its symbol is not defined. One read from metadata is
				// on: the compiler of the library left out the applications that were off there.
				.Where(a => a.ApplicationSyntaxReference is not {} reference ||
					AspectSymbols.IsConditionalAspectEnabled(aspectClass, compilation, reference.SyntaxTree))
				.ToList();

			if (applications.Count == 0)
				return [];

			var blocks = ImmutableArray.CreateBuilder<FilterBlock>(applications.Count);

			for (var i = 0; i < applications.Count; i++)
				blocks.Add(Application(level, target, applications[i], applications.Count == 1 ? null : i + 1));

			return blocks.MoveToImmutable();
		}

		/// <summary>The block of one application of an aspect.</summary>
		/// <param name="target">What the attribute stands on: the assembly, the type or the method.</param>
		/// <param name="number">Its number among the applications of the aspect on <paramref name="target"/>, when there are several.</param>
		public static FilterBlock Application(CascadeLevel level, ISymbol target, AttributeData attribute, int? number = null)
		{
			var aspectClass    = attribute.AttributeClass!;
			var syntax         = Syntax(attribute);
			var (texts, items) = ReadFilter(attribute, syntax, TargetFilterArgument);
			var settings       = WrittenSettings(attribute, syntax);
			var source         = new FilterBlockSource(level, aspectClass, target, attribute, syntax, texts, items);
			var name           = AspectName(aspectClass);
			var on             = level == CascadeLevel.Assembly ? $"[assembly: {name}]" : $"[{name}] on {target.ToDisplayString()}";
			var label          = on + (number is {} n ? $" #{n}" : "") + Where(target, syntax);

			return FilterBlockBuilder.Build(level, texts, settings, label, source);
		}

		/// <summary>
		/// The settings an attribute writes, by name, for the cascade: all constructor arguments together under
		/// <see cref="ConstructorSetting"/>, each named argument except <c>TargetFilter</c> under its own name; their values
		/// are <see cref="AttributeSetting"/>s. An argument not written is not in it.
		/// <list type="bullet">
		/// <item>In the source, what the argument list writes: constructor arguments when it has any positional argument (or
		/// one named after a parameter), a named argument when it assigns it. A constructor argument that only takes its
		/// default is not written.</item>
		/// <item>From metadata, every named argument stored, and the constructor arguments whenever there are any: a stored
		/// default cannot be told from a written value. A parameterless constructor writes nothing either way.</item>
		/// </list>
		/// </summary>
		/// <param name="syntax">The attribute as written; <see langword="null"/> for one read from metadata.</param>
		public static ImmutableDictionary<string,object?> WrittenSettings(AttributeData attribute, AttributeSyntax? syntax)
		{
			var settings           = ImmutableDictionary.CreateBuilder<string,object?>(StringComparer.Ordinal);
			var constructorWritten = attribute.ConstructorArguments.Length > 0;
			var namedWritten       = default(HashSet<string>);

			if (syntax is not null)
			{
				constructorWritten = false;
				namedWritten       = new HashSet<string>(StringComparer.Ordinal);

				foreach (var argument in syntax.ArgumentList?.Arguments ?? default)
				{
					if (argument.NameEquals is null)
						constructorWritten = true;
					else
						namedWritten.Add(argument.NameEquals.Name.Identifier.ValueText);
				}
			}

			if (constructorWritten && attribute.AttributeConstructor is {} constructor)
				settings[ConstructorSetting] = AttributeSetting.ForConstructor(constructor, attribute.ConstructorArguments);

			foreach (var argument in attribute.NamedArguments)
				if (argument.Key != TargetFilterArgument && (namedWritten is null || namedWritten.Contains(argument.Key)))
					settings[argument.Key] = AttributeSetting.ForNamedArgument(argument.Value);

			return settings.ToImmutable();
		}

		/// <summary>
		/// The aspect a method gets from the settings the cascade decided for it (<see cref="CascadeDecision.Settings"/>),
		/// over blocks this class built. A setting the decision does not hold is not written by any selecting block (or is in
		/// conflict): the constructor then is the one of <paramref name="reportAt"/> with its arguments, which are its
		/// defaults when it writes none, and a named argument is left out.
		/// </summary>
		/// <param name="reportAt">The most specific attribute that selects the method.</param>
		public static EffectiveApplication CreateApplication(IReadOnlyDictionary<string,CascadeSetting> settings, AttributeData reportAt)
		{
			var constructor       = reportAt.AttributeConstructor;
			var arguments         = reportAt.ConstructorArguments;
			var constructorSource = reportAt;
			var named             = new List<(CascadeLevel Level, int Index, EffectiveNamedArgument Argument)>();

			foreach (var setting in settings)
			{
				// Every value is one this class wrote into a block it built (WrittenSettings).
				if (setting.Value is not { Value: AttributeSetting value, Block: CascadeBlock<FilterTarget> { Source: FilterBlockSource { Attribute: {} attribute } source } })
					continue;

				if (setting.Key == ConstructorSetting)
				{
					constructor       = value.Constructor;
					arguments         = value.Values;
					constructorSource = attribute;
					continue;
				}

				var index = 0;

				while (index < attribute.NamedArguments.Length && attribute.NamedArguments[index].Key != setting.Key)
					index++;

				named.Add((source.Level, index, new EffectiveNamedArgument(setting.Key, value.Values[0], attribute)));
			}

			return new EffectiveApplication(
				reportAt,
				constructor,
				arguments,
				constructorSource,
				[..named
					.OrderBy(static n => n.Level)
					.ThenBy(static n => n.Index)
					.ThenBy(static n => n.Argument.Name, StringComparer.Ordinal)
					.Select(static n => n.Argument)]);
		}

		/// <summary>The items of the filter <paramref name="name"/> and the expression of each; none when it is not written or is null.</summary>
		static (ImmutableArray<string> Texts, ImmutableArray<ExpressionSyntax?> Items) ReadFilter(AttributeData attribute, AttributeSyntax? syntax, string name)
		{
			foreach (var argument in attribute.NamedArguments)
			{
				if (argument.Key != name)
					continue;

				var value      = argument.Value;
				var expression = NamedArgumentExpression(syntax, name);

				if (value.IsNull)
					return ([], []);

				if (value.Kind != TypedConstantKind.Array)
					return ([value.Value as string ?? ""], [expression]);

				var texts    = ImmutableArray.CreateRange(value.Values, static v => v.Kind == TypedConstantKind.Array ? "" : v.Value as string ?? "");
				var elements = ArrayElements(expression);

				return (texts, elements is not null && elements.Count == texts.Length
					? [..elements]
					: ImmutableArray.CreateRange(texts, static _ => (ExpressionSyntax?)null));
			}

			return ([], []);
		}

		static ExpressionSyntax? NamedArgumentExpression(AttributeSyntax? attribute, string name)
		{
			foreach (var argument in attribute?.ArgumentList?.Arguments ?? default)
				if (argument.NameEquals?.Name.Identifier.ValueText == name)
					return argument.Expression;

			return null;
		}

		/// <summary>The elements of an array argument written element by element, in their order.</summary>
		static IReadOnlyList<ExpressionSyntax?>? ArrayElements(ExpressionSyntax? expression)
		{
			return expression switch
			{
				ImplicitArrayCreationExpressionSyntax { Initializer.Expressions: var values } => values,
				ArrayCreationExpressionSyntax { Initializer.Expressions: var values }         => values,
				CollectionExpressionSyntax { Elements: var elements }                         => elements.Select(static e => (e as ExpressionElementSyntax)?.Expression).ToList(),
				_                                                                             => null,
			};
		}

		static AttributeSyntax? Syntax(AttributeData? attribute)
		{
			return attribute?.ApplicationSyntaxReference?.GetSyntax() as AttributeSyntax;
		}

		/// <summary>The application <paramref name="syntax"/> as attribute data of the type it stands on.</summary>
		static AttributeData? FindApplication(AttributeSyntax? syntax, SemanticModel? semanticModel)
		{
			if (syntax?.Parent?.Parent is not BaseTypeDeclarationSyntax declaration ||
				semanticModel?.GetDeclaredSymbol(declaration) is not {} type)
				return null;

			foreach (var attribute in type.GetAttributes())
				if (attribute.ApplicationSyntaxReference is {} reference &&
					reference.SyntaxTree == syntax.SyntaxTree &&
					reference.Span == syntax.Span)
					return attribute;

			return null;
		}

		static string AspectName(INamedTypeSymbol aspectClass)
		{
			var name = aspectClass.Name;

			return name.Length > "Attribute".Length && name.EndsWith("Attribute", StringComparison.Ordinal)
				? name.Substring(0, name.Length - "Attribute".Length)
				: name;
		}

		/// <summary>The assembly of an attribute read from metadata, for the label; nothing for one in the source.</summary>
		static string Where(ISymbol target, AttributeSyntax? syntax)
		{
			if (syntax is not null)
				return "";

			var assembly = target as IAssemblySymbol ?? target.ContainingAssembly;

			return assembly is null ? "" : $" in '{assembly.Identity.Name}'";
		}
	}
}
