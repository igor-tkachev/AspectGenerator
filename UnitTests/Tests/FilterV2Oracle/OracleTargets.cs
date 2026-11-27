using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AspectGenerator.Tests.FilterV2Oracle
{
	/// <summary>
	/// The fixed list of target methods the frozen 1.1 baselines (<c>Baselines/TargetFilters</c>) describe, in their
	/// order: methods of a source compiled here and chosen methods of the referenced framework assemblies. It is a copy
	/// of its own, so that the frozen baselines stay readable when the 1.1 tests go; <see cref="HeaderLines"/> renders
	/// every target as the baselines' header does, and a test compares the two, so the copy cannot drift.
	/// </summary>
	static class OracleTargets
	{
		public sealed record Entry(int Index, string Label, IMethodSymbol Symbol);

		static readonly object _sync = new();
		static (CSharpCompilation Compilation, Entry[] Entries)? _cache;

		public static Entry[] Entries
		{
			get
			{
				lock (_sync)
				{
					return (_cache ??= Build()).Entries;
				}
			}
		}

		public static CSharpCompilation Compilation
		{
			get
			{
				lock (_sync)
				{
					return (_cache ??= Build()).Compilation;
				}
			}
		}

		/// <summary>The framework the chosen framework methods come from: the host runtime's assemblies.</summary>
		public static string FrameworkIdentity
		{
			get
			{
				return string.Join(", ", new[] { typeof(object).Assembly, typeof(System.Linq.Enumerable).Assembly }.Select(static a => a.GetName().Name + " " + a.GetName().Version));
			}
		}

		/// <summary>One line per target (index, label, signature), as the header of every frozen baseline has it.</summary>
		public static IEnumerable<string> HeaderLines()
		{
			var entries = Entries;

			yield return "# framework " + FrameworkIdentity;

			for (var i = 0; i < entries.Length; i++)
				yield return $"# target {i}\t{entries[i].Label}\t{FrozenBaseline.Escape(Signature(entries[i].Symbol))}";
		}

		static (CSharpCompilation, Entry[]) Build()
		{
			var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
			var compilation  = CSharpCompilation.Create(
				"TargetFilterTargets",
				[CSharpSyntaxTree.ParseText(Source, parseOptions, "Targets.cs")],
				GetReferences(),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));

			var errors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();

			if (errors.Length > 0)
				throw new InvalidOperationException("The target source does not compile:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(static e => e.ToString())));

			var entries = new List<(string, IMethodSymbol)>();

			AddSourceMethods(compilation, entries);
			AddConstructedMethods(compilation, entries);
			AddReducedExtensionMethods(compilation, entries);
			AddFrameworkMethods(compilation, entries);

			return (compilation, entries.Select(static (e, i) => new Entry(i, e.Item1, e.Item2)).ToArray());
		}

		public static IEnumerable<MetadataReference> GetReferences()
		{
			var trustedPlatformAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;

			return trustedPlatformAssemblies
				.Split(Path.PathSeparator)
				.Where(static path => path.Length > 0)
				.OrderBy(static path => path, StringComparer.Ordinal)
				.Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path));
		}

		static void AddSourceMethods(Compilation compilation, List<(string, IMethodSymbol)> entries)
		{
			foreach (var type in GetSourceTypes(compilation.Assembly.GlobalNamespace))
				foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
				{
					if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.UserDefinedOperator or MethodKind.Conversion or MethodKind.ExplicitInterfaceImplementation))
						continue;

					// Compiler-synthesized names such as <Clone>$ are not callable by name.
					if (method.Name.StartsWith('<'))
						continue;

					entries.Add(("source", method));
				}
		}

		static IEnumerable<INamedTypeSymbol> GetSourceTypes(INamespaceSymbol ns)
		{
			foreach (var member in ns.GetMembers())
			{
				if (member is INamespaceSymbol child)
				{
					foreach (var type in GetSourceTypes(child))
						yield return type;
				}
				else if (member is INamedTypeSymbol named)
				{
					foreach (var type in Flatten(named))
						yield return type;
				}
			}

			static IEnumerable<INamedTypeSymbol> Flatten(INamedTypeSymbol type)
			{
				yield return type;

				foreach (var nested in type.GetTypeMembers())
					foreach (var inner in Flatten(nested))
						yield return inner;
			}
		}

		/// <summary>Call-site shapes: generic methods with their type arguments, and members of constructed generic types.</summary>
		static void AddConstructedMethods(Compilation compilation, List<(string, IMethodSymbol)> entries)
		{
			var intType    = compilation.GetSpecialType(SpecialType.System_Int32);
			var stringType = compilation.GetSpecialType(SpecialType.System_String);
			var listOfInt  = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")!.Construct(intType);

			var service = compilation.GetTypeByMetadataName("Acme.Core.Service")!;

			foreach (var method in service.GetMembers("Load").OfType<IMethodSymbol>().OrderBy(static m => m.TypeParameters.Length))
			{
				if (method.TypeParameters.Length == 1)
					entries.Add(("constructed", method.Construct(intType)));
				else if (method.TypeParameters.Length == 2)
					entries.Add(("constructed", method.Construct(stringType, listOfInt)));
			}

			var repository = compilation.GetTypeByMetadataName("Acme.Core.Repository`1")!.Construct(stringType);

			foreach (var name in new[] { "Get", "Add", "Find" })
				foreach (var method in repository.GetMembers(name).OfType<IMethodSymbol>())
					entries.Add(("constructed type", method.TypeParameters.Length == 1 ? method.Construct(intType) : method));

			var pair = compilation.GetTypeByMetadataName("Acme.Core.Pair`2")!.Construct(stringType, listOfInt);

			foreach (var method in pair.GetMembers("TryGet").OfType<IMethodSymbol>())
				entries.Add(("constructed type", method));

			var inner = compilation.GetTypeByMetadataName("Acme.Core.Outer`1")!.Construct(intType).GetTypeMembers("Inner").Single().Construct(stringType);

			foreach (var method in inner.GetMembers("Run").OfType<IMethodSymbol>())
				entries.Add(("constructed type", method));
		}

		/// <summary>Extension methods as a call site sees them: the reduced form.</summary>
		static void AddReducedExtensionMethods(Compilation compilation, List<(string, IMethodSymbol)> entries)
		{
			var stringType = compilation.GetSpecialType(SpecialType.System_String);
			var ext        = compilation.GetTypeByMetadataName("Acme.Core.Extensions")!;

			foreach (var method in ext.GetMembers().OfType<IMethodSymbol>().Where(static m => m.IsExtensionMethod))
			{
				var receiver = method.Parameters[0].Type;

				// An interface receiver over a type parameter (IEnumerable<T>) is reduced on string, which infers T = char.
				var reduced = method.ReduceExtensionMethod(receiver.TypeKind == TypeKind.Interface ? stringType : receiver);

				if (reduced is not null)
					entries.Add(("reduced extension", reduced));
			}
		}

		static void AddFrameworkMethods(Compilation compilation, List<(string, IMethodSymbol)> entries)
		{
			foreach (var (typeName, methodName, parameters, typeArguments) in FrameworkMethods)
			{
				var type = compilation.GetTypeByMetadataName(typeName) ?? throw new InvalidOperationException($"Type {typeName} is not referenced.");

				var method = type
					.GetMembers(methodName)
					.OfType<IMethodSymbol>()
					.Where(m => m.Parameters.Select(static p => DisplayParameter(p)).SequenceEqual(parameters))
					.SingleOrDefault();

				if (method is null)
					throw new InvalidOperationException($"{typeName}.{methodName}({string.Join(", ", parameters)}) was not found.");

				if (typeArguments.Length > 0)
					method = method.Construct(typeArguments.Select(t => ResolveType(compilation, t)).ToArray());

				if (method.IsExtensionMethod && typeArguments.Length > 0 && method.Parameters[0].Type is INamedTypeSymbol receiver)
					method = method.ReduceExtensionMethod(receiver) ?? method;

				entries.Add(("framework", method));
			}
		}

		// Nullable annotations are left out of the selector: a framework release can annotate a parameter without changing the method.
		static string DisplayParameter(IParameterSymbol parameter)
		{
			var prefix = parameter.IsParams ? "params " : parameter.RefKind switch
			{
				RefKind.Ref => "ref ",
				RefKind.Out => "out ",
				RefKind.In  => "in ",
				RefKind.RefReadOnlyParameter => "ref readonly ",
				_           => ""
			};

			return prefix + parameter.Type.ToDisplayString().Replace("?", "");
		}

		static ITypeSymbol ResolveType(Compilation compilation, string name)
		{
			return name switch
			{
				"int"    => compilation.GetSpecialType(SpecialType.System_Int32),
				"string" => compilation.GetSpecialType(SpecialType.System_String),
				"object" => compilation.GetSpecialType(SpecialType.System_Object),
				_        => compilation.GetTypeByMetadataName(name) ?? throw new InvalidOperationException($"Type {name} is not referenced.")
			};
		}

		/// <summary>
		/// The 1.1 canonical signature of a target, as the header of the frozen baselines writes it: accessibility,
		/// modifiers, return type, containing type, name, type arguments and parameters, every type by its full name.
		/// </summary>
		public static string Signature(IMethodSymbol method)
		{
			var source = method.ReducedFrom ?? method;
			var words  = new List<string> { AccessibilityText(source.DeclaredAccessibility) };

			if (source.IsStatic)                        words.Add("static");
			if (source.IsAbstract)                      words.Add("abstract");
			if (source.IsVirtual && !source.IsOverride) words.Add("virtual");
			if (source.IsOverride)                      words.Add("override");
			if (source.IsSealed)                        words.Add("sealed");
			if (source.IsExtern)                        words.Add("extern");
			if (IsUnsafe(source))                       words.Add("unsafe");

			var sb = new StringBuilder();

			sb.Append(string.Join(" ", words)).Append(' ').Append(TypeText(source.ReturnType)).Append(' ').Append(TypeText(source.ContainingType)).Append('.').Append(source.Name);

			if (source.TypeArguments.Length > 0)
				sb.Append('<').Append(string.Join(",", source.TypeArguments.Select(TypeText))).Append('>');

			var parameters = new List<string>();
			var rest       = source.Parameters.AsEnumerable();

			if (method.ReducedFrom is not null || method.IsExtensionMethod || source.IsExtensionMethod)
			{
				var receiver = method.ReducedFrom is not null ? method.ReceiverType ?? source.Parameters.FirstOrDefault()?.Type : source.Parameters.FirstOrDefault()?.Type;

				if (receiver is not null)
					parameters.Add("this " + TypeText(receiver));

				if (source.Parameters.Length > 0)
					rest = rest.Skip(1);
			}

			foreach (var parameter in rest)
			{
				var modifier = parameter.RefKind switch
				{
					RefKind.Ref                  => "ref ",
					RefKind.Out                  => "out ",
					RefKind.In                   => "in ",
					RefKind.RefReadOnlyParameter => "ref readonly ",
					_                            => "",
				};

				parameters.Add((parameter.IsParams ? "params " : "") + modifier + TypeText(parameter.Type));
			}

			sb.Append('(').Append(string.Join(",", parameters)).Append(')');

			return sb.ToString();
		}

		static bool IsUnsafe(IMethodSymbol method)
		{
			foreach (var reference in method.DeclaringSyntaxReferences)
				if (reference.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.UnsafeKeyword))
					return true;

			return false;
		}

		static string AccessibilityText(Accessibility accessibility)
		{
			return accessibility switch
			{
				Accessibility.Public               => "public",
				Accessibility.Protected            => "protected",
				Accessibility.Internal             => "internal",
				Accessibility.ProtectedOrInternal  => "protected internal",
				Accessibility.ProtectedAndInternal => "private protected",
				_                                  => "private",
			};
		}

		static string TypeText(ITypeSymbol type)
		{
			switch (type)
			{
				case IArrayTypeSymbol array:
				{
					var ranks = new StringBuilder();
					ITypeSymbol element = array;

					// The rank specifiers in C# order: an array of int[,] is int[][,].
					while (element is IArrayTypeSymbol a)
					{
						ranks.Append('[').Append(',', a.Rank - 1).Append(']');
						element = a.ElementType;
					}

					return TypeText(element) + ranks;
				}

				case ITypeParameterSymbol parameter:
					return parameter.Name;

				case INamedTypeSymbol named:
				{
					var name = named.Name;
					var tick = name.IndexOf('`');

					if (tick >= 0)
						name = name[..tick];

					if (named.TypeParameters.Length > 0)
						name += "<" + string.Join(",", named.TypeArguments.Skip(named.TypeArguments.Length - named.TypeParameters.Length).Select(TypeText)) + ">";

					if (named.ContainingType is not null)
						return TypeText(named.ContainingType) + "." + name;

					return named.ContainingNamespace.IsGlobalNamespace ? name : named.ContainingNamespace.ToDisplayString() + "." + name;
				}

				default:
					return type.ToDisplayString(new SymbolDisplayFormat(
						typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
						genericsOptions:        SymbolDisplayGenericsOptions.IncludeTypeParameters,
						miscellaneousOptions:   SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers));
			}
		}

		// Selected by type, name and parameter types, never by position: the order of members in a framework assembly is not part of the contract.
		static readonly (string Type, string Method, string[] Parameters, string[] TypeArguments)[] FrameworkMethods =
		[
			("System.Math",                                   "Max",           ["int", "int"],                                                 []),
			("System.Math",                                   "Max",           ["long", "long"],                                               []),
			("System.Math",                                   "Max",           ["double", "double"],                                           []),
			("System.Math",                                   "Min",           ["int", "int"],                                                 []),
			("System.Math",                                   "Abs",           ["int"],                                                        []),
			("System.Math",                                   "Abs",           ["decimal"],                                                    []),
			("System.Math",                                   "Round",         ["double", "int"],                                              []),
			("System.Math",                                   "Sqrt",          ["double"],                                                     []),
			("System.Linq.Enumerable",                        "Count",         ["System.Collections.Generic.IEnumerable<TSource>"],            ["int"]),
			("System.Linq.Enumerable",                        "Count",         ["System.Collections.Generic.IEnumerable<TSource>", "System.Func<TSource, bool>"], ["string"]),
			("System.Linq.Enumerable",                        "Any",           ["System.Collections.Generic.IEnumerable<TSource>"],            ["int"]),
			("System.Linq.Enumerable",                        "ToList",        ["System.Collections.Generic.IEnumerable<TSource>"],            ["string"]),
			("System.Linq.Enumerable",                        "ToArray",       ["System.Collections.Generic.IEnumerable<TSource>"],            ["int"]),
			("System.Linq.Enumerable",                        "Select",        ["System.Collections.Generic.IEnumerable<TSource>", "System.Func<TSource, TResult>"], []),
			("System.Linq.Enumerable",                        "Where",         ["System.Collections.Generic.IEnumerable<TSource>", "System.Func<TSource, bool>"], []),
			("System.Linq.Enumerable",                        "Range",         ["int", "int"],                                                 []),
			("System.String",                                 "Format",        ["string", "object"],                                           []),
			("System.String",                                 "Format",        ["string", "params object[]"],                                  []),
			("System.String",                                 "Join",          ["string", "params string[]"],                                  []),
			("System.String",                                 "Concat",        ["string", "string"],                                           []),
			("System.String",                                 "IsNullOrEmpty", ["string"],                                                    []),
			("System.String",                                 "Substring",     ["int", "int"],                                                 []),
			("System.String",                                 "Split",         ["char", "System.StringSplitOptions"],                          []),
			("System.String",                                 "Contains",      ["string"],                                                     []),
			("System.Int32",                                  "TryParse",      ["string", "out int"],                                         []),
			("System.Int32",                                  "Parse",         ["string"],                                                     []),
			("System.Console",                                "WriteLine",     ["string"],                                                    []),
			("System.Console",                                "WriteLine",     ["string", "params object[]"],                                  []),
			("System.Threading.Tasks.Task",                   "Delay",         ["int"],                                                        []),
			("System.Threading.Tasks.Task",                   "FromResult",    ["TResult"],                                                    ["int"]),
			("System.Threading.Tasks.Task",                   "Run",           ["System.Action"],                                              []),
			("System.Threading.Tasks.Task",                   "WhenAll",       ["params System.Threading.Tasks.Task[]"],                       []),
			("System.Threading.Interlocked",                  "Increment",     ["ref int"],                                                    []),
			("System.Threading.Interlocked",                  "Exchange",      ["ref T", "T"],                                                 ["string"]),
			("System.Array",                                  "Resize",        ["ref T[]", "int"],                                            ["int"]),
			("System.Array",                                  "Empty",         [],                                                             ["string"]),
			("System.Collections.Generic.List`1",             "Add",           ["T"],                                                          []),
			("System.Collections.Generic.List`1",             "AddRange",      ["System.Collections.Generic.IEnumerable<T>"],                  []),
			("System.Collections.Generic.Dictionary`2",       "TryGetValue",   ["TKey", "out TValue"],                                         []),
			("System.Collections.Generic.Dictionary`2",       "Add",           ["TKey", "TValue"],                                             []),
			("System.Collections.Generic.Dictionary`2",       "ContainsKey",   ["TKey"],                                                       []),
			("System.Object",                                 "ToString",      [],                                                             []),
			("System.Object",                                 "Equals",        ["object"],                                                    []),
			("System.Object",                                 "GetHashCode",   [],                                                             []),
			("System.Environment",                            "GetEnvironmentVariable", ["string"],                                            []),
			("System.IO.File",                                "ReadAllText",   ["string"],                                                     []),
			("System.IO.Path",                                "Combine",       ["string", "string"],                                           []),
			("System.Activator",                              "CreateInstance", [],                                                            ["System.Text.StringBuilder"]),
			("System.Runtime.CompilerServices.Unsafe",        "AsPointer",     ["ref readonly T"],                                                      ["int"]),
			("System.MemoryExtensions",                       "AsSpan",        ["string"],                                                    []),
			("System.Buffers.ArrayPool`1",                    "Rent",          ["int"],                                                        []),
		];

		const string Source =
			"""
			#nullable enable
			using System;
			using System.Collections.Generic;
			using System.Runtime.InteropServices;
			using System.Threading;
			using System.Threading.Tasks;

			[AttributeUsage(AttributeTargets.All)]
			public sealed class MarkerAttribute : Attribute { }

			[AttributeUsage(AttributeTargets.All)]
			public sealed class Tag : Attribute { }

			[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
			public sealed class GenericMarkerAttribute<T> : Attribute { }

			public class GlobalService
			{
				public void Run() { }
				public static int Count() { return 0; }
				public string? Describe(string? text) { return text; }
				[Marker] public void Marked() { }
				protected internal void Shared() { }
				private protected void Hidden() { }
			}

			[Tag]
			public class TaggedGlobal
			{
				public void Work() { }
				[GenericMarker<int>] public Task WorkAsync() { return Task.CompletedTask; }
			}

			namespace Acme.Core
			{
				public readonly struct Big { public readonly long A, B, C, D; }

				public interface IJob
				{
					void Run();
					Task<int> RunAsync(CancellationToken token);
					void Log() { }
				}

				public class Impl : IJob
				{
					void IJob.Run() { }
					Task<int> IJob.RunAsync(CancellationToken token) { return Task.FromResult(0); }
					public void Run() { }
				}

				public class Service
				{
					public void Save() { }
					public void Save(int id) { }
					public void Save(string? name) { }
					public void Lookup(string key) { }
					public void Save(int? id) { }
					public void Save(string name, int? retries, bool force) { }
					public void Save(int a, int b, int c, int d, int e) { }
					public void SaveMany(List<string?> names) { }
					public void SaveMap(Dictionary<string, List<int?>> map) { }
					public string? Find(string? key) { return key; }
					public int? Maybe() { return null; }
					public bool Check(object? value) { return value is null; }

					public static void Create() { }
					public virtual void Execute() { }
					internal void Internal() { }
					protected void Protected() { }
					private void Private() { }
					protected internal void ProtectedInternal() { }
					private protected void PrivateProtected() { }

					public Task SaveAsync() { return Task.CompletedTask; }
					public Task SaveAsync(CancellationToken token) { return Task.CompletedTask; }
					public Task<string> LoadAsync() { return Task.FromResult(""); }
					public Task<List<int?>> LoadManyAsync() { return Task.FromResult(new List<int?>()); }
					public ValueTask FlushAsync() { return default; }
					public ValueTask<int> CountAsync() { return default; }
					public void Ping() { }

					public T Load<T>() { return default!; }
					public T Load<T>(T seed) { return seed; }
					public TResult Load<T, TResult>(T input, TResult seed) { return seed; }
					public static T Make<T>() where T : new() { return new T(); }
					public List<Dictionary<string, List<int>>> Group() { return new(); }
					public Dictionary<string, List<string?>> Index(IEnumerable<KeyValuePair<string, int?>> pairs) { return new(); }

					public void Swap(ref int a, ref int b) { }
					public bool TryParse(string text, out int value) { value = 0; return false; }
					public int Sum(in Big big) { return 0; }
					public int Peek(ref readonly int value) { return value; }
					public string Join(params string[] parts) { return ""; }
					public string Join(string separator, params object?[] parts) { return separator; }
					public int Total(params ReadOnlySpan<int> values) { return values.Length; }
					public void Mixed(ref string text, out int count, in Big big, params int[] rest) { count = 0; }

					public int[] ToArray() { return []; }
					public string[][] Split(string text) { return []; }
					public int[,] Matrix() { return new int[0, 0]; }
					public void Fill(int[] values, string?[] names) { }
					public (int, string) Pair() { return (0, ""); }
					public (int Id, string Name) Named() { return (0, ""); }
					public void Accept((int, string?) tuple, List<(int, int)> list) { }
					public unsafe int* Pointer(int* p) { return p; }
					public unsafe void* Raw() { return null; }
					public unsafe void Fixed(byte[] data) { }

					public void @event() { }
					public void @params() { }
					public void @class() { }
					public void Сохранить() { }
					public void Save_Item() { }
					public void Save2() { }

					[Marker] public void Marked() { }
					[Tag] public void Tagged() { }
					[Marker, Tag] public void Both() { }
					[GenericMarker<int>] public void GenericMarked() { }
					[GenericMarker<string>, GenericMarker<int>] public void ManyMarked() { }
					[Obsolete] public void Old() { }
					[System.Diagnostics.Conditional("DEBUG")] public void Debug() { }

					[DllImport("native")] public static extern int Native(int x);
					[DllImport("native")] private static extern void Poke(ref int x);

					public static Service operator +(Service a, Service b) { return a; }
					public static implicit operator int(Service s) { return 0; }
					public static explicit operator Service(int value) { return new Service(); }
				}

				public abstract class Base
				{
					public abstract void Abstract();
					protected abstract int Compute(int x);
					public virtual void Virtual() { }
					public virtual string? Name() { return null; }
					protected virtual Task OnAsync() { return Task.CompletedTask; }
					public static void Shared() { }
					public void Plain() { }
				}

				public class Derived : Base
				{
					public override void Abstract() { }
					protected override int Compute(int x) { return x; }
					public sealed override void Virtual() { }
					public override string? Name() { return ""; }
					protected override Task OnAsync() { return Task.CompletedTask; }
					public override string ToString() { return ""; }
					public override bool Equals(object? obj) { return false; }
					public override int GetHashCode() { return 0; }
				}

				public sealed class Leaf : Derived
				{
					public new void Plain() { }
				}

				public class Repository<T>
				{
					public T Get(int id) { return default!; }
					public K Get<K>(K key) { return key; }
					public void Add(T item) { }
					public List<T> Find(Func<T, bool> predicate) { return new(); }
					public T? Peek() { return default; }
					public static Repository<T> Create() { return new(); }
					public Repository<T> Clone() { return this; }
				}

				public class Pair<TKey, TValue>
				{
					public bool TryGet(TKey key, out TValue value) { value = default!; return false; }
					public void Set(TKey key, TValue value) { }
					public KeyValuePair<TKey, TValue> Current() { return default; }
					public Dictionary<TKey, List<TValue>> Expand() { return new(); }

					public class Entry<X>
					{
						public X Value() { return default!; }
						public Pair<TKey, TValue> Owner() { return null!; }
					}
				}

				public class Outer
				{
					public void Run() { }

					public class Inner
					{
						public void Run() { }

						public class Deep
						{
							public void Run() { }
							public static void Go(int[] values) { }
						}
					}

					private class Hidden
					{
						public void Run() { }
					}

					protected internal class Shared
					{
						public void Run() { }
					}
				}

				public class Outer<T>
				{
					public class Inner<U>
					{
						public void Run(T first, U second) { }
						public List<T> Run<V>(U second, V third) { return new(); }
					}
				}

				public static class Extensions
				{
					public static string Shout(this string text) { return text; }
					public static string Repeat(this string text, int times) { return text; }
					public static IEnumerable<T> Take<T>(this IEnumerable<T> source, int count) { return source; }
					public static int Total(this List<int> values, in Big big) { return 0; }
					public static void Reset(this ref int value) { }
					public static bool IsEven(this int value) { return value % 2 == 0; }
					public static void Deconstruct(this string text, out char first, out string rest) { first = ' '; rest = ""; }
					private static void Hidden(this string text) { }
				}

				public record Person(string Name, int? Age)
				{
					public string Greet() { return Name; }
				}

				public record struct Point(int X, int Y);

				public sealed record Employee(string Name, string? Title) : Person(Name, null);

				public unsafe class UnsafeHost
				{
					public void Plain() { }
					public int* Address(int* p) { return p; }
				}

				public struct Counter
				{
					public int Next() { return 0; }
					public static Counter Zero() { return default; }
					public override string ToString() { return ""; }
				}
			}

			namespace A.B.C.D.E.F.G
			{
				public class DeepService
				{
					public void Run() { }
					public static void Start() { }
					public Task<int> RunAsync() { return Task.FromResult(0); }
				}
			}

			namespace Lib.Tasks
			{
				public class Worker
				{
					public Task Start() { return Task.CompletedTask; }
					public Task<bool> TryStart() { return Task.FromResult(true); }
					public ValueTask Stop() { return default; }
					public ValueTask<string?> Drain() { return default; }
					public void Cancel(CancellationToken token = default) { }
				}
			}
			""";
	}
}
