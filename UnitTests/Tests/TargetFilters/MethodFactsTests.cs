using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AspectGenerator.TargetFilterFacts;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// The fact record of a method, read from the same library as source, as an implementation assembly and as a
	/// reference assembly (no <c>InternalsVisibleTo</c> in either): every fact must read the same in all three.
	/// </summary>
	[TestClass]
	public class MethodFactsTests
	{
		const string LibrarySource =
			"""
			using System;
			using System.Collections.Generic;
			using System.Runtime.CompilerServices;
			using System.Runtime.InteropServices;
			using System.Threading.Tasks;

			namespace Lib.Shapes
			{
				[AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = true)]
				public class InhAttribute : Attribute { }

				[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
				public class WrittenAttribute : Attribute { }

				public interface IPub
				{
					void IM();
					void Dflt() { }
					static void SImpl() { }
				}

				public interface IStat { static abstract void Make(); }
				public interface IBox<T> { void Put(T item); }

				public class PubBase
				{
					public virtual void BV() { }
					public virtual void BV2() { }
					public void Plain() { }
				}

				[Inh]
				public partial class Facts : PubBase, IPub, IStat
				{
					public void Pub() { }
					internal void Int() { }
					protected void Prot() { }
					private void Priv() { }
					protected internal void ProtInt() { }
					private protected void PrivProt() { }
					public static void Stat() { }
					public virtual void Virt() { }
					public override void BV() { }
					public sealed override void BV2() { }
					public new void Plain() { }
					public void IM() { }
					static void IStat.Make() { }
					[Inh] public virtual void InhAttrM() { }
					[Written] [Written] public void Written() { }
					[DllImport("libc")] public static extern int getpid();
					[MethodImpl(MethodImplOptions.NoInlining)] public void Impl() { }
					public partial void Part();

					public async Task AsyncT() { await Task.Yield(); }
					[System.Diagnostics.DebuggerStepThrough] public async Task AsyncStep() { await Task.Yield(); }
					public Task NotAsyncT() { return Task.CompletedTask; }
					public ValueTask VT() { return default; }
					public Task<int> TI() { return Task.FromResult(1); }
					public ValueTask<int> VTI() { return default; }
					public IAsyncEnumerable<int> AE() { return null!; }
					public async IAsyncEnumerable<int> AEIter() { await Task.Yield(); yield return 1; }
					public async void AsyncVoid() { await Task.Yield(); }
					public Awaitable Custom() { return default!; }
					public ExplAw ReturnExpl() { return default!; }
					public PrivAw ReturnPriv() { return default!; }
					public IntAw ReturnInt() { return default!; }
					[MethodImpl(MethodImplOptions.InternalCall)] public static extern void Native();
					[MethodImpl(MethodImplOptions.ForwardRef)] public static extern void Fwd();
					#pragma warning disable CS0626 // A bare extern, which metadata cannot tell from a method with a body.
					public static extern void Bare();
					#pragma warning restore CS0626
					public AwG<IAwaiterT> ReturnAwG() { return default!; }
					public AwG<T> ReturnAwGen<T>() where T : IAwaiterT { return default!; }

					public void RefRO(ref readonly int x) { }
					public void InP(in int x) { }
					public void OutP(out int x) { x = 0; }
					public void RefP(ref int x) { }
					public void Scp(scoped Span<int> s) { }
					public void ParamsArr(params int[] a) { }
					public void ParamsSpan(params ReadOnlySpan<int> a) { }
					public void Ranks(int[] a, int[,] b, int[,,] c, int[][] d, int[][,] e) { }
					public (int, string) Tuple((int, string) t) { return t; }
					public int? Nullable(int? x) { return x; }
					public ref int RetRef(ref int x) { return ref x; }
					public ref readonly int RetRefRO(ref int x) { return ref x; }
					public T Gen<T>(T t) where T : class { return t; }
					public string? NullableRef(string? s) { return s; }
				}

				public partial class Facts { public partial void Part() { } }

				public static class Exts
				{
					public static void ExtMethod(this string s) { }
					public static void ExtRef(this ref int x) { }
					public static void NotExt(string s) { }
					public static int Count2<T>(this IEnumerable<T> source) { return 0; }
				}

				public record Rec(int A, string B) { public void Own() { } }

				public class Box<T> : IBox<T> { public void Put(T item) { } }

				public class Gen2<T> { public void M(T t) { } public void G<U>(U u) { } }

				public class Outer { public class Inner { public void Deep() { } } }

				public class Awaitable { public Awaiter GetAwaiter() { return default!; } }
				public class Awaiter : INotifyCompletion
				{
					public bool IsCompleted => true;
					public void GetResult() { }
					public void OnCompleted(Action a) { }
				}

				public interface IAw { Awaiter GetAwaiter(); }
				public class ExplAw : IAw { Awaiter IAw.GetAwaiter() { return default!; } }
				public class PrivAw { Awaiter GetAwaiter() { return default!; } }
				public class IntAw { public int GetAwaiter() { return 0; } }

				public abstract class AbsBase { public virtual void AV() { } public abstract void AA(); }
				public abstract class AbsDer : AbsBase { public abstract override void AV(); public sealed override void AA() { } }

				public interface IVirt { void M(); }
				public class VB : IVirt { public virtual void M() { } }
				public class VD : VB { public override void M() { } }

				public interface IG { void G<T>(T item); }
				public class GImpl : IG { public void G<T>(T item) { } }

				public class GBase<T> { public virtual void V(T item) { } public virtual void W<U>(U item) { } }
				public class GMid<T> : GBase<T> { public override void V(T item) { } public override void W<U>(U item) { } }
				public class GLeaf : GMid<string> { public override void V(string item) { } }

				public class Holder<T> { public T Get() { return default!; } }
				public class Cons { public T Con<T>() where T : Task { return default!; } }

				public class CovBase { public virtual object Cov() { return ""; } }
				public class CovDer : CovBase { public override string Cov() { return ""; } }

				public interface IRe { void M(); }
				public class ReB : IRe { public virtual void M() { } }
				public class ReD : ReB, IRe { public override void M() { } void IRe.M() { } }

				public interface IAwaiterT : INotifyCompletion { bool IsCompleted { get; } void GetResult(); }
				public class AwG<T> where T : IAwaiterT { public T GetAwaiter() { return default!; } }

				public interface IGoodA { Awaiter GetAwaiter(); }
				public interface IBadA { Awaiter GetAwaiter(); }
				public class Cc { public T Ret<T>() where T : IGoodA, IBadA { return default!; } public T One<T>() where T : IGoodA { return default!; } }

				public class AwR<T> where T : IAwaiterT, allows ref struct { public T GetAwaiter() { return default!; } }

				public class Cd
				{
					public T0 Deep<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>() where T0 : T1 where T1 : T2 where T2 : T3 where T3 : T4 where T4 : T5 where T5 : T6 where T6 : T7 where T7 : T8 where T8 : T9 where T9 : IGoodA { return default!; }
					public AwR<T> RetAllowsRef<T>() where T : IAwaiterT, allows ref struct { return default!; }
				}

				public class Derived : Facts { public override void InhAttrM() { } public override void Virt() { } }
			}
			""";

		const string ConsumerSource =
			"""
			using System;
			using System.Collections.Generic;
			using System.Linq;
			using System.Threading.Tasks;
			using Lib.Shapes;

			static class Consumer
			{
				static void Calls(IEnumerable<int> numbers, List<int> list)
				{
					var g = new Gen2<int>();
					g.G<string>("x");
					g.G<int>(1);
					g.M(1);
					new Box<int>().Put(1);
					numbers.Any();
					Enumerable.Any<int>(numbers);
					numbers.Count2();
					list.Add(1);
					new Outer.Inner().Deep();
					new GImpl().G<int>(1);
					new GMid<int>().V(1);
					new GMid<int>().W<string>("x");
					new Holder<Task>().Get();
					new Holder<int>().Get();
					new Cons().Con<Task>();
				}

				static T Caller<T>(IEnumerable<T> items, T value)
				{
					items.Count2();
					new Box<T>().Put(value);
					return value;
				}
			}
			""";

		sealed class Variant
		{
			public required string      Name        { get; init; }
			public required Compilation Consumer    { get; init; }
			public required bool        IsSource    { get; init; }

			public INamedTypeSymbol Type(string name)
			{
				return Consumer.GetTypeByMetadataName(name) ?? throw new InvalidOperationException($"{Name}: no type {name}");
			}

			public IMethodSymbol Method(string type, string name, int parameters = -1)
			{
				var methods = Type(type).GetMembers(name).OfType<IMethodSymbol>().Where(m => parameters < 0 || m.Parameters.Length == parameters).ToList();

				Assert.AreEqual(1, methods.Count, $"{Name}: {type}.{name}");

				return methods[0];
			}

			public MethodFacts Facts(IMethodSymbol method, FactProperty plan = FactProperty.All)
			{
				return MethodFacts.Create(method, plan, new FactContext(Consumer));
			}

			/// <summary>The method symbol a call in the consumer binds to, as the semantic model gives it.</summary>
			public IMethodSymbol Call(string inMethod, string text)
			{
				var tree  = Consumer.SyntaxTrees.Single(static t => t.FilePath == "Consumer.cs");
				var model = Consumer.GetSemanticModel(tree);
				var scope = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.Text == inMethod);

				var invocation = scope.DescendantNodes().OfType<InvocationExpressionSyntax>().First(i => i.ToString() == text);

				return (IMethodSymbol)model.GetSymbolInfo(invocation).Symbol!;
			}
		}

		static readonly Lazy<Variant[]> _variants = new(CreateVariants);

		static Variant[] Variants
		{
			get { return _variants.Value; }
		}

		static Variant Source
		{
			get { return Variants[0]; }
		}

		static Variant Impl
		{
			get { return Variants[1]; }
		}

		static Variant Ref
		{
			get { return Variants[2]; }
		}

		static IEnumerable<MetadataReference> Framework()
		{
			var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;

			return trusted
				.Split(Path.PathSeparator)
				.Where(static path => !Path.GetFileName(path).StartsWith("AspectGenerator.", StringComparison.OrdinalIgnoreCase))
				.Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path));
		}

		static Variant[] CreateVariants()
		{
			var parse     = new CSharpParseOptions(LanguageVersion.Preview);
			var framework = Framework().ToList();
			var options   = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);

			var library = CSharpCompilation.Create(
				"Lib",
				[CSharpSyntaxTree.ParseText(LibrarySource, parse, "Lib.cs")],
				framework,
				options);

			var implStream = new MemoryStream();
			var refStream  = new MemoryStream();
			var emit       = library.Emit(implStream, metadataPEStream: refStream, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(includePrivateMembers: false));

			Assert.IsTrue(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));

			var implReference = MetadataReference.CreateFromImage(implStream.ToArray());
			var refReference  = MetadataReference.CreateFromImage(refStream.ToArray());

			Compilation Consumer(string name, params MetadataReference[] extra)
			{
				return CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(ConsumerSource, parse, "Consumer.cs")], framework.Concat(extra), options);
			}

			// A consumer with the library's source: its tree is another file of the same compilation.
			var source = Consumer("Consumer").AddSyntaxTrees(CSharpSyntaxTree.ParseText(LibrarySource, parse, "Lib.cs"));
			var impl   = Consumer("Consumer", implReference);
			var @ref   = Consumer("Consumer", refReference);

			foreach (var compilation in new[] { source, impl, @ref })
			{
				var errors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).Select(static d => d.ToString()).ToList();

				Assert.AreEqual(0, errors.Count, string.Join(Environment.NewLine, errors));
			}

			return
			[
				new Variant { Name = "source", Consumer = source, IsSource = true },
				new Variant { Name = "impl",   Consumer = impl,   IsSource = false },
				new Variant { Name = "ref",    Consumer = @ref,   IsSource = false },
			];
		}

		static string Type(ITypeSymbol type)
		{
			return FactNames.FormatType(type);
		}

		/// <summary>Every fact of a record as text, except <see cref="MethodFacts.IsProject"/>, which differs by design.</summary>
		static string Describe(MethodFacts f)
		{
			var lines = new List<string>
			{
				$"accessibility: {f.Accessibility}",
				$"static: {f.IsStatic}",
				$"extern: {f.IsExtern}",
				$"extension: {f.IsExtension}",
				$"virtuality: {f.Virtuality}",
				$"async: {f.IsAsync}",
				$"generated: {f.IsGenerated}",
				$"attributes: {string.Join(",", f.Attributes.Select(static a => a.Name))}",
				$"namespace: {f.Namespace}",
				$"type: {f.TypeName}",
				$"path: {f.MethodPath}",
				$"returns: {f.ReturnRef} {Type(f.ReturnType)}",
				$"params: {string.Join(", ", f.Parameters.Select(static p => $"{Type(p.Type)}[{p.Modifiers}]"))}",
				$"inherits: {string.Join(",", f.InheritedTypes.Select(static t => Type(t)).OrderBy(static n => n, StringComparer.Ordinal))}",
				$"implements: {string.Join(",", f.ImplementedMembers.Select(static m => m.ToDisplayString()).OrderBy(static n => n, StringComparer.Ordinal))}",
				$"overrides: {string.Join(",", f.OverrideChain.Select(static m => m.ToDisplayString()))}",
			};

			return string.Join(Environment.NewLine, lines);
		}

		static string Key(IMethodSymbol m)
		{
			return m.ContainingType.ToDisplayString() + "." + m.Name + "(" + string.Join(",", m.Parameters.Select(static p => p.Type.ToDisplayString())) + ")";
		}

		static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceOrTypeSymbol container)
		{
			foreach (var member in container.GetMembers())
			{
				if (member is INamespaceSymbol @namespace)
				{
					foreach (var type in AllTypes(@namespace))
						yield return type;
				}
				else if (member is INamedTypeSymbol type)
				{
					// Closure and state machine classes are the compiler's own and are not in a reference assembly.
					if (type.Name.StartsWith("<", StringComparison.Ordinal))
						continue;

					yield return type;

					foreach (var nested in AllTypes(type))
						yield return nested;
				}
			}
		}

		static Dictionary<string, IMethodSymbol> LibraryMethods(Variant variant)
		{
			var @namespace = variant.Consumer.GlobalNamespace.GetNamespaceMembers().Single(static n => n.Name == "Lib").GetNamespaceMembers().Single(static n => n.Name == "Shapes");

			return AllTypes(@namespace)
				.SelectMany(static t => t.GetMembers().OfType<IMethodSymbol>())

				// A constructor is not a call target, and the compiler's default constructor is implicitly declared in source
				// and carries no [CompilerGenerated] in metadata.
				.Where(static m => m.MethodKind != MethodKind.Constructor)
				.ToDictionary(Key, static m => m);
		}

		[TestMethod]
		public void EveryFactReadsTheSameFromSourceImplementationAndReferenceAssembly()
		{
			var source  = LibraryMethods(Source);
			var impl    = LibraryMethods(Impl);
			var @ref    = LibraryMethods(Ref);
			var compare = 0;
			var failed  = new List<string>();

			// The reference assembly carries what a consumer can call; every such method must read the same in the others.
			foreach (var (key, refMethod) in @ref)
			{
				var implText   = Describe(Impl.Facts(impl[key]));
				var refText    = Describe(Ref.Facts(refMethod));
				var sourceText = Describe(Source.Facts(source[key]));

				compare++;

				if (refText != implText || refText != sourceText)
					failed.Add($"{key}{Environment.NewLine}--- source{Environment.NewLine}{sourceText}{Environment.NewLine}--- impl{Environment.NewLine}{implText}{Environment.NewLine}--- ref{Environment.NewLine}{refText}");
			}

			Assert.IsTrue(compare > 70, $"Only {compare} methods compared.");
			Assert.AreEqual(0, failed.Count, string.Join(Environment.NewLine + Environment.NewLine, failed.Take(5)));
		}

		static void ForEachVariant(Action<Variant> check)
		{
			foreach (var variant in Variants)
			{
				try
				{
					check(variant);
				}
				catch (AssertFailedException e)
				{
					throw new AssertFailedException($"[{variant.Name}] {e.Message}", e);
				}
			}
		}

		[TestMethod]
		public void AccessibilityIsAsTheAssemblyDeclaresIt()
		{
			ForEachVariant(v =>
			{
				foreach (var (name, expected) in new[]
				{
					("Pub", Accessibility.Public),
					("Prot", Accessibility.Protected),
					("ProtInt", Accessibility.ProtectedOrInternal),
				})
				{
					var method = v.Type("Lib.Shapes.Facts").GetMembers(name).OfType<IMethodSymbol>().SingleOrDefault();

					// Metadata read without InternalsVisibleTo does not carry internal members at all.
					if (method is null && !v.IsSource && expected == Accessibility.ProtectedOrInternal)
						continue;

					Assert.AreEqual(expected, v.Facts(method!, FactProperty.Accessibility).Accessibility, name);
				}
			});
		}

		[TestMethod]
		public void WordsFollowTheCompilersMeaning()
		{
			ForEachVariant(v =>
			{
				Virtuality V(string type, string name, int parameters = -1)
				{
					return v.Facts(v.Method(type, name, parameters), FactProperty.Virtuality).Virtuality;
				}

				Assert.AreEqual(Virtuality.Virtual,                       V("Lib.Shapes.Facts", "Virt"));
				Assert.AreEqual(Virtuality.Override,                      V("Lib.Shapes.Facts", "BV"));
				Assert.AreEqual(Virtuality.Override | Virtuality.Sealed,  V("Lib.Shapes.Facts", "BV2"));
				Assert.AreEqual(Virtuality.None,                          V("Lib.Shapes.Facts", "Plain"));
				Assert.AreEqual(Virtuality.None,                          V("Lib.Shapes.Facts", "IM"));
				Assert.AreEqual(Virtuality.Abstract,                      V("Lib.Shapes.IPub", "IM"));
				Assert.AreEqual(Virtuality.Virtual,                       V("Lib.Shapes.IPub", "Dflt"));
				Assert.AreEqual(Virtuality.None,                          V("Lib.Shapes.IPub", "SImpl"));
				Assert.AreEqual(Virtuality.Abstract,                      V("Lib.Shapes.IStat", "Make"));
				Assert.AreEqual(Virtuality.Override,                      V("Lib.Shapes.Derived", "Virt"));

				MethodFacts F(string type, string name)
				{
					return v.Facts(v.Method(type, name), FactProperty.Static | FactProperty.Extern | FactProperty.Extension);
				}

				Assert.IsTrue(F("Lib.Shapes.Facts", "Stat").IsStatic);
				Assert.IsFalse(F("Lib.Shapes.Facts", "Pub").IsStatic);
				Assert.IsTrue(F("Lib.Shapes.Facts", "getpid").IsExtern);
				Assert.IsFalse(F("Lib.Shapes.Facts", "Pub").IsExtern);
				Assert.IsTrue(F("Lib.Shapes.Exts", "ExtMethod").IsExtension);
				Assert.IsTrue(F("Lib.Shapes.Exts", "ExtRef").IsExtension);
				Assert.IsFalse(F("Lib.Shapes.Exts", "NotExt").IsExtension);
			});
		}

		[TestMethod]
		public void AsyncIsTheReturnTypeNotTheModifier()
		{
			ForEachVariant(v =>
			{
				foreach (var (name, expected) in new[]
				{
					("AsyncT", true), ("NotAsyncT", true), ("VT", true), ("TI", true), ("VTI", true), ("Custom", true),
					("AE", false), ("AEIter", false), ("AsyncVoid", false), ("Pub", false),
				})
				{
					Assert.AreEqual(expected, v.Facts(v.Method("Lib.Shapes.Facts", name), FactProperty.Async).IsAsync, name);
				}
			});
		}

		[TestMethod]
		public void GeneratedIsTheCompilersOwnMembers()
		{
			ForEachVariant(v =>
			{
				foreach (var name in new[] { "ToString", "Deconstruct", "<Clone>$" })
					Assert.IsTrue(v.Facts(v.Method("Lib.Shapes.Rec", name), FactProperty.Generated).IsGenerated, name);

				Assert.IsFalse(v.Facts(v.Method("Lib.Shapes.Rec", "Own"), FactProperty.Generated).IsGenerated);
				Assert.IsFalse(v.Facts(v.Method("Lib.Shapes.Facts", "Pub"), FactProperty.Generated).IsGenerated);
			});
		}

		[TestMethod]
		public void AttributesAreTheWrittenOnesOfTheMethodAndItsTypes()
		{
			ForEachVariant(v =>
			{
				string[] A(string type, string name)
				{
					return v.Facts(v.Method(type, name), FactProperty.Attribute).Attributes.Select(static a => a.Name).ToArray();
				}


				// The type's own attribute counts for every method of it, written twice because it is written twice.
				CollectionAssert.AreEqual(new[] { "WrittenAttribute", "WrittenAttribute", "InhAttribute" }, A("Lib.Shapes.Facts", "Written"));
				CollectionAssert.AreEqual(new[] { "InhAttribute", "InhAttribute" }, A("Lib.Shapes.Facts", "InhAttrM"));

				// Pseudo-attributes and what the compiler emits on its own are not written attributes.
				CollectionAssert.AreEqual(new[] { "InhAttribute" }, A("Lib.Shapes.Facts", "getpid"));
				CollectionAssert.AreEqual(new[] { "InhAttribute" }, A("Lib.Shapes.Facts", "Impl"));
				CollectionAssert.AreEqual(new[] { "InhAttribute" }, A("Lib.Shapes.Facts", "AsyncT"));
				CollectionAssert.AreEqual(new[] { "InhAttribute" }, A("Lib.Shapes.Facts", "AEIter"));
				CollectionAssert.AreEqual(new[] { "InhAttribute" }, A("Lib.Shapes.Facts", "AsyncStep"));
				CollectionAssert.AreEqual(new[] { "InhAttribute" }, A("Lib.Shapes.Facts", "NullableRef"));
				Assert.AreEqual(0, A("Lib.Shapes.Rec", "ToString").Length);

				// An attribute inherited by a type or an override does not count.
				Assert.AreEqual(0, A("Lib.Shapes.Derived", "InhAttrM").Length);
			});
		}

		[TestMethod]
		public void ParametersCarryTheirModifiersAndRanks()
		{
			ForEachVariant(v =>
			{
				string P(string name)
				{
					var facts = v.Facts(v.Method("Lib.Shapes.Facts", name), FactProperty.Params);

					return string.Join(", ", facts.Parameters.Select(static p => $"{Type(p.Type)}[{p.Modifiers}]"));
				}

				Assert.AreEqual("System.Int32[RefReadOnly]", P("RefRO"));
				Assert.AreEqual("System.Int32[In]", P("InP"));
				Assert.AreEqual("System.Int32[Out]", P("OutP"));
				Assert.AreEqual("System.Int32[Ref]", P("RefP"));
				Assert.AreEqual("System.Span<System.Int32>[None]", P("Scp"));
				Assert.AreEqual("System.Int32[][Params]", P("ParamsArr"));
				Assert.AreEqual("System.ReadOnlySpan<System.Int32>[Params]", P("ParamsSpan"));
				Assert.AreEqual("System.Int32[][None], System.Int32[,][None], System.Int32[,,][None], System.Int32[][][None], System.Int32[][,][None]", P("Ranks"));
				Assert.AreEqual("System.String[This]", v.Facts(v.Method("Lib.Shapes.Exts", "ExtMethod"), FactProperty.Param).Parameters.Select(static p => $"{Type(p.Type)}[{p.Modifiers}]").Single());
				Assert.AreEqual("System.Int32[Ref, This]", v.Facts(v.Method("Lib.Shapes.Exts", "ExtRef"), FactProperty.Param).Parameters.Select(static p => $"{Type(p.Type)}[{p.Modifiers}]").Single());
			});
		}

		[TestMethod]
		public void ReturnTypesCarryTheirRefKind()
		{
			ForEachVariant(v =>
			{
				string R(string name)
				{
					var facts = v.Facts(v.Method("Lib.Shapes.Facts", name), FactProperty.Returns);

					return $"{facts.ReturnRef} {Type(facts.ReturnType)}";
				}

				Assert.AreEqual("Ref System.Int32", R("RetRef"));
				Assert.AreEqual("RefReadOnly System.Int32", R("RetRefRO"));
				Assert.AreEqual("None System.Nullable<System.Int32>", R("Nullable"));
				Assert.AreEqual("None System.String", R("NullableRef"));
				Assert.AreEqual("None System.ValueTuple<System.Int32,System.String>", R("Tuple"));
			});
		}

		[TestMethod]
		public void HierarchyIsReadFromTheContainingTypeAndTheOverrideChain()
		{
			ForEachVariant(v =>
			{
				var bv = v.Facts(v.Method("Lib.Shapes.Facts", "BV"), FactProperty.Overrides | FactProperty.Implements | FactProperty.Inherits);

				CollectionAssert.AreEqual(new[] { "Lib.Shapes.Facts.BV()", "Lib.Shapes.PubBase.BV()" }, bv.OverrideChain.Select(static m => m.ToDisplayString()).ToArray());
				Assert.AreEqual(0, bv.ImplementedMembers.Length);

				var inherited = bv.InheritedTypes.Select(static t => Type(t)).ToList();

				CollectionAssert.IsSubsetOf(new[] { "Lib.Shapes.Facts", "Lib.Shapes.PubBase", "System.Object", "Lib.Shapes.IPub", "Lib.Shapes.IStat" }, inherited);

				var im = v.Facts(v.Method("Lib.Shapes.Facts", "IM"), FactProperty.Implements | FactProperty.Overrides);

				CollectionAssert.AreEqual(new[] { "Lib.Shapes.IPub.IM()" }, im.ImplementedMembers.Select(static m => m.ToDisplayString()).ToArray());
				CollectionAssert.AreEqual(new[] { "Lib.Shapes.Facts.IM()" }, im.OverrideChain.Select(static m => m.ToDisplayString()).ToArray());

				var ifaceMember = v.Facts(v.Method("Lib.Shapes.IPub", "IM"), FactProperty.Implements);

				CollectionAssert.AreEqual(new[] { "Lib.Shapes.IPub.IM()" }, ifaceMember.ImplementedMembers.Select(static m => m.ToDisplayString()).ToArray());

				var derived = v.Facts(v.Method("Lib.Shapes.Derived", "Virt"), FactProperty.Overrides);

				CollectionAssert.AreEqual(
					new[] { "Lib.Shapes.Derived.Virt()", "Lib.Shapes.Facts.Virt()" },
					derived.OverrideChain.Select(static m => m.ToDisplayString()).ToArray());
			});
		}

		// A public type derives from a base that is public when it is compiled; the consumer is given the base as an
		// internal type, without InternalsVisibleTo, as it is when a library's base class stops being public.
		static string HiddenBaseSource(string accessibility)
		{
			return $$"""
				namespace Lib.Hidden
				{
					public interface IShared { void Shared(); }
					public class PubBase { public virtual void BV2() { } }
					{{accessibility}} class HiddenBase : PubBase, IShared
					{
						public override void BV2() { }
						public virtual void HV() { }
						public void HPlain() { }
						public void Shared() { }
						internal void HInternal() { }
					}
				}
				""";
		}

		[TestMethod]
		public void APublicMemberOfAnInternalBaseIsReadWithoutInternalsVisibleTo()
		{
			var parse     = new CSharpParseOptions(LanguageVersion.Preview);
			var framework = Framework().ToList();
			var options   = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

			PortableExecutableReference Emit(string name, string source, bool referenceAssembly, params MetadataReference[] references)
			{
				var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source, parse)], framework.Concat(references), options);
				var impl        = new MemoryStream();
				var @ref        = new MemoryStream();
				var emit        = compilation.Emit(impl, metadataPEStream: @ref, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(includePrivateMembers: false));

				Assert.IsTrue(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));

				return MetadataReference.CreateFromImage((referenceAssembly ? @ref : impl).ToArray());
			}

			var openBase = Emit("Base", HiddenBaseSource("public"), false);
			var mid      = Emit("Mid", "namespace Lib.Hidden { public class Exposed : HiddenBase { } }", false, openBase);

			foreach (var referenceAssembly in new[] { false, true })
			{
				var hidden   = Emit("Base", HiddenBaseSource("internal"), referenceAssembly);
				var consumer = CSharpCompilation.Create(
					"Consumer",
					[CSharpSyntaxTree.ParseText("class C { void M(Lib.Hidden.Exposed e) { e.HV(); e.HPlain(); e.BV2(); e.Shared(); } }", parse, "Consumer.cs")],
					framework.Concat([hidden, mid]),
					options);

				var tree    = consumer.SyntaxTrees.Single();
				var model   = consumer.GetSemanticModel(tree);
				var context = new FactContext(consumer);
				var name    = referenceAssembly ? "ref" : "impl";

				IMethodSymbol Call(string text)
				{
					var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().First(i => i.ToString() == text);
					var info       = model.GetSymbolInfo(invocation);

					return (IMethodSymbol?)info.Symbol ?? (IMethodSymbol)info.CandidateSymbols.Single();
				}

				foreach (var text in new[] { "e.HV()", "e.HPlain()", "e.BV2()", "e.Shared()" })
				{
					var facts = MethodFacts.Create(Call(text), FactProperty.All, context);


					// A call to an override binds to the method that introduced the slot, wherever the compiler puts it.
					Assert.AreEqual(Type(Call(text).ContainingType), facts.TypeName, $"{name} {text}");
					Assert.AreEqual(Accessibility.Public, facts.Accessibility, $"{name} {text}");
					Assert.IsFalse(facts.IsProject, $"{name} {text}");
					CollectionAssert.IsSubsetOf(new[] { Type(Call(text).ContainingType), "Lib.Hidden.PubBase", "System.Object" }.Distinct().ToArray(), facts.InheritedTypes.Select(static t => Type(t)).ToList(), $"{name} {text}");
				}

				Assert.AreEqual(Virtuality.Virtual, MethodFacts.Create(Call("e.HV()"), FactProperty.Virtuality, context).Virtuality, name);
				Assert.AreEqual("Lib.Hidden.HiddenBase.HV", MethodFacts.Create(Call("e.HV()"), FactProperty.Method, context).MethodPath, name);

				// The override declared in the hidden base: its chain reaches the public method it overrides.
				var hiddenBv2 = consumer.GetTypeByMetadataName("Lib.Hidden.HiddenBase")!.GetMembers("BV2").OfType<IMethodSymbol>().Single();
				var bv2       = MethodFacts.Create(hiddenBv2, FactProperty.Overrides | FactProperty.Virtuality | FactProperty.Inherits, context);

				Assert.AreEqual(Virtuality.Override, bv2.Virtuality, name);
				CollectionAssert.AreEqual(new[] { "Lib.Hidden.HiddenBase.BV2()", "Lib.Hidden.PubBase.BV2()" }, bv2.OverrideChain.Select(static m => m.ToDisplayString()).ToArray(), name);
				CollectionAssert.IsSubsetOf(new[] { "Lib.Hidden.HiddenBase", "Lib.Hidden.PubBase", "Lib.Hidden.IShared" }, bv2.InheritedTypes.Select(static t => Type(t)).ToList(), name);

				var shared = MethodFacts.Create(Call("e.Shared()"), FactProperty.Implements, context);

				CollectionAssert.AreEqual(new[] { "Lib.Hidden.IShared.Shared()" }, shared.ImplementedMembers.Select(static m => m.ToDisplayString()).ToArray(), name);

				// The internal member is not carried by the metadata the consumer reads.
				Assert.AreEqual(0, consumer.GetTypeByMetadataName("Lib.Hidden.HiddenBase")!.GetMembers("HInternal").Length, name);
			}
		}

		[TestMethod]
		public void ADefinitionAndItsCallsShareTheWordsAndDifferInTheTypes()
		{
			ForEachVariant(v =>
			{
				var call       = v.Facts(v.Call("Calls", "g.G<string>(\"x\")"));
				var definition = call.Definition;

				Assert.IsFalse(call.IsDefinition);
				Assert.IsTrue(definition.IsDefinition);
				Assert.AreSame(definition, definition.Definition);
				Assert.AreEqual("Lib.Shapes.Gen2<System.Int32>.G<System.String>", call.MethodPath);
				Assert.AreEqual("Lib.Shapes.Gen2<T>.G<U>", definition.MethodPath);
				Assert.AreEqual("Lib.Shapes.Gen2<System.Int32>", call.TypeName);
				Assert.AreEqual("Lib.Shapes.Gen2<T>", definition.TypeName);
				Assert.AreEqual("System.String", Type(call.Parameters.Single().Type));
				Assert.AreEqual("U", Type(definition.Parameters.Single().Type));
				Assert.AreEqual("Lib.Shapes", call.Namespace);

				// A call record answers a word from the definition, and a second call reuses that definition.
				var second = definition.ForCall(v.Call("Calls", "g.G<int>(1)"));

				Assert.AreSame(definition, second.Definition);
				Assert.AreEqual("Lib.Shapes.Gen2<System.Int32>.G<System.Int32>", second.MethodPath);
				Assert.ThrowsExactly<ArgumentException>(() => definition.ForCall(v.Call("Calls", "g.M(1)")));

				// A method nothing constructs is its own definition.
				Assert.IsTrue(v.Facts(v.Method("Lib.Shapes.Facts", "Pub")).IsDefinition);
			});
		}

		[TestMethod]
		public void AnExtensionMethodIsDescribedInItsStaticFormConstructedFromTheCall()
		{
			ForEachVariant(v =>
			{
				var reduced = v.Facts(v.Call("Calls", "numbers.Any()"));
				var @static = v.Facts(v.Call("Calls", "Enumerable.Any<int>(numbers)"));

				Assert.AreEqual(Describe(@static), Describe(reduced));
				Assert.AreEqual("System.Linq.Enumerable.Any<System.Int32>", reduced.MethodPath);
				Assert.IsTrue(reduced.IsExtension);
				Assert.IsTrue(reduced.IsStatic);
				Assert.AreEqual(ParameterModifiers.This, reduced.Parameters.Single().Modifiers);
				Assert.AreEqual("System.Collections.Generic.IEnumerable<System.Int32>", Type(reduced.Parameters.Single().Type));

				var own = v.Facts(v.Call("Calls", "numbers.Count2()"));

				Assert.AreEqual("Lib.Shapes.Exts.Count2<System.Int32>", own.MethodPath);
				Assert.IsTrue(own.IsExtension);
				Assert.AreEqual("Lib.Shapes.Exts.Count2<T>", own.Definition.MethodPath);
			});
		}

		[TestMethod]
		public void ATypeArgumentOfTheCallerIsItsTypeParameter()
		{
			ForEachVariant(v =>
			{
				var count = v.Facts(v.Call("Caller", "items.Count2()"));
				var put   = v.Facts(v.Call("Caller", "new Box<T>().Put(value)"));

				Assert.AreEqual("Lib.Shapes.Exts.Count2<T>", count.MethodPath);
				Assert.AreEqual("T", Type(put.Parameters.Single().Type));
				Assert.AreEqual("Lib.Shapes.Box<T>", put.TypeName);
				Assert.IsInstanceOfType<ITypeParameterSymbol>(put.Parameters.Single().Type);
			});
		}

		[TestMethod]
		public void ConstructedHierarchyCarriesTheTypeArguments()
		{
			ForEachVariant(v =>
			{
				var put = v.Facts(v.Call("Calls", "new Box<int>().Put(1)"));

				CollectionAssert.AreEqual(new[] { "Lib.Shapes.IBox<int>.Put(int)" }, put.ImplementedMembers.Select(static m => m.ToDisplayString()).ToArray());
				CollectionAssert.IsSubsetOf(new[] { "Lib.Shapes.Box<System.Int32>", "Lib.Shapes.IBox<System.Int32>", "System.Object" }, put.InheritedTypes.Select(static t => Type(t)).ToList());

				CollectionAssert.AreEqual(new[] { "Lib.Shapes.IBox<T>.Put(T)" }, put.Definition.ImplementedMembers.Select(static m => m.ToDisplayString()).ToArray());

				var add = v.Facts(v.Call("Calls", "list.Add(1)"));

				Assert.AreEqual("System.Collections.Generic.List<System.Int32>.Add", add.MethodPath);
				CollectionAssert.IsSubsetOf(new[] { "System.Collections.Generic.ICollection<System.Int32>", "System.Collections.Generic.IList<System.Int32>" }, add.InheritedTypes.Select(static t => Type(t)).ToList());
				Assert.IsTrue(add.ImplementedMembers.Any(static m => m.ContainingType.Name == "ICollection"));
			});
		}

		[TestMethod]
		public void NestedTypesAreWrittenWithTheirOuterTypes()
		{
			ForEachVariant(v =>
			{
				var deep = v.Facts(v.Call("Calls", "new Outer.Inner().Deep()"), FactProperty.Location | FactProperty.Method);

				Assert.AreEqual("Lib.Shapes.Outer.Inner", deep.TypeName);
				Assert.AreEqual("Lib.Shapes.Outer.Inner.Deep", deep.MethodPath);
			});
		}

		[TestMethod]
		public void OriginIsTheAssemblyOfTheContext()
		{
			var libraryMethod = "Pub";

			var source = Source.Facts(Source.Method("Lib.Shapes.Facts", libraryMethod), FactProperty.Origin);
			var impl   = Impl.Facts(Impl.Method("Lib.Shapes.Facts", libraryMethod), FactProperty.Origin);
			var @ref   = Ref.Facts(Ref.Method("Lib.Shapes.Facts", libraryMethod), FactProperty.Origin);

			Assert.IsTrue(source.IsProject);
			Assert.IsFalse(impl.IsProject);
			Assert.IsFalse(@ref.IsProject);

			// A library's own filters are read with the library's assembly as the project.
			var libraryAssembly = Impl.Type("Lib.Shapes.Facts").ContainingAssembly;
			var inLibrary       = MethodFacts.Create(Impl.Method("Lib.Shapes.Facts", libraryMethod), FactProperty.Origin, new FactContext(Impl.Consumer, libraryAssembly));

			Assert.IsTrue(inLibrary.IsProject);
			Assert.IsFalse(MethodFacts.Create(Impl.Call("Calls", "list.Add(1)"), FactProperty.Origin, new FactContext(Impl.Consumer, libraryAssembly)).IsProject);
		}

		[TestMethod]
		public void AnAbstractOverrideIsBoth()
		{
			ForEachVariant(v =>
			{
				Virtuality V(string type, string name)
				{
					return v.Facts(v.Method(type, name), FactProperty.Virtuality).Virtuality;
				}

				Assert.AreEqual(Virtuality.Override | Virtuality.Abstract, V("Lib.Shapes.AbsDer", "AV"));
				Assert.AreEqual(Virtuality.Abstract, V("Lib.Shapes.AbsBase", "AA"));
				Assert.AreEqual(Virtuality.Override | Virtuality.Sealed, V("Lib.Shapes.AbsDer", "AA"));
			});
		}

		[TestMethod]
		public void AnOverrideImplementsWhatTheMethodItOverridesImplements()
		{
			ForEachVariant(v =>
			{
				var m = v.Facts(v.Method("Lib.Shapes.VD", "M"), FactProperty.Implements);

				CollectionAssert.AreEqual(new[] { "Lib.Shapes.IVirt.M()" }, m.ImplementedMembers.Select(static x => x.ToDisplayString()).ToArray());
			});
		}

		[TestMethod]
		public void AGenericMethodImplementsTheInterfaceMemberConstructedWithItsTypeArguments()
		{
			ForEachVariant(v =>
			{
				var call = v.Facts(v.Call("Calls", "new GImpl().G<int>(1)"), FactProperty.Implements);

				CollectionAssert.AreEqual(new[] { "Lib.Shapes.IG.G<int>(int)" }, call.ImplementedMembers.Select(static x => x.ToDisplayString()).ToArray());
				CollectionAssert.AreEqual(new[] { "Lib.Shapes.IG.G<T>(T)" }, call.Definition.ImplementedMembers.Select(static x => x.ToDisplayString()).ToArray());
			});
		}

		[TestMethod]
		public void OverrideChainsSurviveConstruction()
		{
			ForEachVariant(v =>
			{
				string[] Chain(MethodFacts f)
				{
					return f.OverrideChain.Select(static x => x.ToDisplayString()).ToArray();
				}

				var v1 = v.Facts(v.Call("Calls", "new GMid<int>().V(1)"), FactProperty.Overrides);

				CollectionAssert.AreEqual(new[] { "Lib.Shapes.GMid<int>.V(int)", "Lib.Shapes.GBase<int>.V(int)" }, Chain(v1));
				CollectionAssert.AreEqual(new[] { "Lib.Shapes.GMid<T>.V(T)", "Lib.Shapes.GBase<T>.V(T)" }, Chain(v1.Definition));

				var w = v.Facts(v.Call("Calls", "new GMid<int>().W<string>(\"x\")"), FactProperty.Overrides);

				CollectionAssert.AreEqual(new[] { "Lib.Shapes.GMid<int>.W<string>(string)", "Lib.Shapes.GBase<int>.W<string>(string)" }, Chain(w));

				var leaf = v.Facts(v.Method("Lib.Shapes.GLeaf", "V"), FactProperty.Overrides);

				CollectionAssert.AreEqual(new[] { "Lib.Shapes.GLeaf.V(string)", "Lib.Shapes.GMid<string>.V(string)", "Lib.Shapes.GBase<string>.V(string)" }, Chain(leaf));
			});
		}

		[TestMethod]
		public void AsyncIsDecidedOnTheCallsConstructedReturnType()
		{
			ForEachVariant(v =>
			{
				bool Async(string call)
				{
					return v.Facts(v.Call("Calls", call), FactProperty.Async).IsAsync;
				}

				Assert.IsTrue(Async("new Holder<Task>().Get()"));
				Assert.IsFalse(Async("new Holder<int>().Get()"));
				Assert.IsFalse(v.Facts(v.Call("Calls", "new Holder<Task>().Get()"), FactProperty.Async).Definition.IsAsync);

				// A type parameter constrained to an awaitable type is awaitable in the definition too.
				var con = v.Facts(v.Call("Calls", "new Cons().Con<Task>()"), FactProperty.Async);

				Assert.IsTrue(con.IsAsync);
				Assert.IsTrue(con.Definition.IsAsync);
			});
		}

		[TestMethod]
		public void AnAwaitableTypeHasAnAccessibleInstanceGetAwaiterThatReturnsAnAwaiter()
		{
			ForEachVariant(v =>
			{
				foreach (var name in new[] { "ReturnExpl", "ReturnPriv", "ReturnInt" })
					Assert.IsFalse(v.Facts(v.Method("Lib.Shapes.Facts", name), FactProperty.Async).IsAsync, name);

				Assert.IsTrue(v.Facts(v.Method("Lib.Shapes.Facts", "Custom"), FactProperty.Async).IsAsync);
			});
		}

		[TestMethod]
		public void AMethodReimplementedInTheTypeDoesNotImplementWhatTheBaseMapped()
		{
			ForEachVariant(v =>
			{
				// ReD implements IRe again, explicitly, so its override of M is not what IRe.M maps to.
				var d = v.Facts(v.Method("Lib.Shapes.ReD", "M"), FactProperty.Implements);

				Assert.AreEqual(0, d.ImplementedMembers.Length);

				var b = v.Facts(v.Method("Lib.Shapes.ReB", "M"), FactProperty.Implements);

				CollectionAssert.AreEqual(new[] { "Lib.Shapes.IRe.M()" }, b.ImplementedMembers.Select(static x => x.ToDisplayString()).ToArray());
			});
		}

		[TestMethod]
		public void TheCompilerDecidesWhatIsAwaitable()
		{
			ForEachVariant(v =>
			{
				// An awaiter that is a type parameter constrained to an awaiter interface.
				Assert.IsTrue(v.Facts(v.Method("Lib.Shapes.Facts", "ReturnAwG"), FactProperty.Async).IsAsync);
				Assert.IsTrue(v.Facts(v.Method("Lib.Shapes.Facts", "ReturnAwGen"), FactProperty.Async).IsAsync);

				// One constrained GetAwaiter is awaitable; two equally good ones are ambiguous at the await.
				Assert.IsTrue(v.Facts(v.Method("Lib.Shapes.Cc", "One"), FactProperty.Async).IsAsync);
				Assert.IsFalse(v.Facts(v.Method("Lib.Shapes.Cc", "Ret"), FactProperty.Async).IsAsync);
			});
		}

		[TestMethod]
		public void AnInternalGetAwaiterIsAwaitableOnlyForAFriendAssembly()
		{
			const string library =
				"""
				using System;
				using System.Runtime.CompilerServices;
				[assembly: InternalsVisibleTo("Friend")]
				namespace Lib.Ivt
				{
					public class Awaiter : INotifyCompletion
					{
						public bool IsCompleted { get { return true; } }
						public void GetResult() { }
						public void OnCompleted(Action a) { }
					}
					public class IvtAw { internal Awaiter GetAwaiter() { return new Awaiter(); } }
					public static class Make { public static IvtAw Get() { return new IvtAw(); } }
				}
				""";

			var parse     = new CSharpParseOptions(LanguageVersion.Preview);
			var framework = Framework().ToList();
			var lib       = CSharpCompilation.Create("IvtLib", [CSharpSyntaxTree.ParseText(library, parse)], framework, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
			var image     = new MemoryStream();

			Assert.IsTrue(lib.Emit(image).Success);

			var reference = MetadataReference.CreateFromImage(image.ToArray());

			foreach (var (assembly, expected) in new[] { ("Friend", true), ("Stranger", false) })
			{
				var consumer = CSharpCompilation.Create(assembly, [CSharpSyntaxTree.ParseText("class C { void M() { Lib.Ivt.Make.Get(); } }", parse)], framework.Append(reference), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
				var method   = consumer.GetTypeByMetadataName("Lib.Ivt.Make")!.GetMembers("Get").OfType<IMethodSymbol>().Single();

				Assert.AreEqual(expected, MethodFacts.Create(method, FactProperty.Async, new FactContext(consumer)).IsAsync, assembly);
			}
		}

		[TestMethod]
		public void TheConstraintsOfAnAwaiterAreTheCompilersWhateverTheirDepth()
		{
			ForEachVariant(v =>
			{
				// A chain of ten constrained type parameters ending in an awaitable interface.
				Assert.IsTrue(v.Facts(v.Method("Lib.Shapes.Cd", "Deep"), FactProperty.Async).IsAsync);
			});
		}

		[TestMethod]
		public void AnAwaiterThatMayBeARefStructIsNotAwaitable()
		{
			// CS4027 at a real await.
			ForEachVariant(v => Assert.IsFalse(v.Facts(v.Method("Lib.Shapes.Cd", "RetAllowsRef"), FactProperty.Async).IsAsync));
		}

		[TestMethod]
		public void ProbesOfOneContextAreSafeToRunConcurrently()
		{
			var method = Impl.Method("Lib.Shapes.Facts", "ReturnAwG");

			for (var round = 0; round < 20; round++)
			{
				var context = new FactContext(Impl.Consumer);
				var results = new bool[16];

				System.Threading.Tasks.Parallel.For(0, results.Length, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
				{
					results[i] = MethodFacts.Create(method, FactProperty.Async, context).IsAsync;
				});

				Assert.IsTrue(results.All(static r => r), $"round {round}: {string.Join(",", results)}");
			}
		}

		[TestMethod]
		public void ExternIncludesInternalCallMethods()
		{
			ForEachVariant(v =>
			{
				Assert.IsTrue(v.Facts(v.Method("Lib.Shapes.Facts", "Native"), FactProperty.Extern).IsExtern);
				Assert.IsTrue(v.Facts(v.Method("Lib.Shapes.Facts", "Fwd"), FactProperty.Extern).IsExtern);

				// Metadata keeps no trace of a bare extern, so neither form calls it extern.
				Assert.IsFalse(v.Facts(v.Method("Lib.Shapes.Facts", "Bare"), FactProperty.Extern).IsExtern);
				Assert.IsTrue(v.Facts(v.Method("Lib.Shapes.Facts", "getpid"), FactProperty.Extern).IsExtern);
			});
		}

		[TestMethod]
		public void ACovariantOverrideHasNoWrittenAttributes()
		{
			ForEachVariant(v => Assert.AreEqual(0, v.Facts(v.Method("Lib.Shapes.CovDer", "Cov"), FactProperty.Attribute).Attributes.Length));
		}

		[TestMethod]
		public void AccessibilityOfMembersTheMetadataOnlyCarriesWithImportOptionsAll()
		{
			string[] names = ["Int", "Priv", "PrivProt"];
			Accessibility[] expected = [Accessibility.Internal, Accessibility.Private, Accessibility.ProtectedAndInternal];

			for (var i = 0; i < names.Length; i++)
				Assert.AreEqual(expected[i], Source.Facts(Source.Method("Lib.Shapes.Facts", names[i]), FactProperty.Accessibility).Accessibility, names[i]);

			// Read without importing non-public members, as a consumer does, the metadata has neither.
			foreach (var variant in new[] { Impl, Ref })
				foreach (var name in new[] { "Int", "Priv" })
					Assert.AreEqual(0, variant.Type("Lib.Shapes.Facts").GetMembers(name).Length, $"{variant.Name} {name}");

			// Imported with every member, an implementation assembly reads all three; a reference assembly keeps only
			// what a consumer can call.
			foreach (var variant in new[] { Impl, Ref })
			{
				var all  = variant.Consumer.WithOptions(((CSharpCompilationOptions)variant.Consumer.Options).WithMetadataImportOptions(MetadataImportOptions.All));
				var type = all.GetTypeByMetadataName("Lib.Shapes.Facts")!;

				for (var i = 0; i < names.Length; i++)
				{
					var method = type.GetMembers(names[i]).OfType<IMethodSymbol>().SingleOrDefault();

					if (method is null)
					{
						Assert.AreSame(Ref, variant, names[i]);
						continue;
					}

					Assert.AreEqual(expected[i], MethodFacts.Create(method, FactProperty.Accessibility, new FactContext(all)).Accessibility, $"{variant.Name} {names[i]}");
				}
			}
		}

		[TestMethod]
		public void FactsAreComputedOnFirstReadNotWhenTheRecordIsMade()
		{
			var call  = Source.Call("Calls", "new Holder<Task>().Get()");
			var facts = Source.Facts(call);

			Assert.AreEqual(0, facts.ComputedFacts);
			Assert.AreEqual(0, facts.Definition.ComputedFacts);

			_ = facts.Attributes;
			_ = facts.Attributes;

			Assert.AreEqual(1, facts.Definition.ComputedFacts, "Attributes belong to the definition and are computed once.");
			Assert.AreEqual(0, facts.ComputedFacts);

			_ = facts.OverrideChain;
			_ = facts.OverrideChain;
			_ = facts.IsAsync;

			Assert.AreEqual(3, facts.ComputedFacts + facts.Definition.ComputedFacts);

			// A second call of the same definition finds the definition's facts already computed.
			var second = facts.Definition.ForCall(Source.Call("Calls", "new Holder<int>().Get()"));

			_ = second.Attributes;

			Assert.AreEqual(1, facts.Definition.ComputedFacts);
		}

		[TestMethod]
		public void AFactOutsideThePlanIsNotComputed()
		{
			var method = Source.Method("Lib.Shapes.Facts", "Pub");
			var facts  = Source.Facts(method, FactProperty.Accessibility | FactProperty.Param);

			Assert.AreEqual(Accessibility.Public, facts.Accessibility);
			Assert.AreEqual(0, facts.Parameters.Length);

			Assert.ThrowsExactly<InvalidOperationException>(() => facts.IsStatic);
			Assert.ThrowsExactly<InvalidOperationException>(() => facts.Attributes);
			Assert.ThrowsExactly<InvalidOperationException>(() => facts.MethodPath);
			Assert.ThrowsExactly<InvalidOperationException>(() => facts.ReturnType);

			// A call record keeps its definition's plan.
			var call = Source.Facts(Source.Call("Calls", "g.G<string>(\"x\")"), FactProperty.Accessibility);

			Assert.ThrowsExactly<InvalidOperationException>(() => call.Definition.IsAsync);
		}

		[TestMethod]
		public void FrameworkMethodsReadFromTheRuntimeAssemblies()
		{
			var framework = Impl.Consumer;
			var plan      = FactProperty.All;
			var context   = new FactContext(framework);

			MethodFacts Facts(string type, string name, int parameters)
			{
				var symbol = framework.GetTypeByMetadataName(type)!.GetMembers(name).OfType<IMethodSymbol>().First(m => m.Parameters.Length == parameters);

				return MethodFacts.Create(symbol, plan, context);
			}

			var delay = Facts("System.Threading.Tasks.Task", "Delay", 1);

			Assert.IsTrue(delay.IsAsync);
			Assert.IsTrue(delay.IsStatic);
			Assert.AreEqual(Accessibility.Public, delay.Accessibility);
			Assert.IsFalse(delay.IsProject);
			Assert.AreEqual("System.Threading.Tasks.Task.Delay", delay.MethodPath);

			var clone = Facts("System.Object", "MemberwiseClone", 0);

			// The runtime assembly declares it protected internal; the reference pack declares it protected, and a consumer
			// reads whichever assembly it is compiled against.
			Assert.AreEqual(Accessibility.ProtectedOrInternal, clone.Accessibility);

			var toString = Facts("System.Object", "ToString", 0);

			Assert.AreEqual(Virtuality.Virtual, toString.Virtuality);

			var stringToString = Facts("System.String", "ToString", 0);

			Assert.AreEqual(Virtuality.Override, stringToString.Virtuality);
			CollectionAssert.AreEqual(new[] { "string.ToString()", "object.ToString()" }, stringToString.OverrideChain.Select(static m => m.ToDisplayString()).ToArray());
		}

		/// <summary>
		/// The reference pack a consumer compiles against, next to the runtime this test host runs on; null when the SDK
		/// that built it is not there.
		/// </summary>
		static string? FindReferencePack()
		{
			var runtimeDirectory = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
			var dotnetRoot       = runtimeDirectory.Parent?.Parent?.Parent;
			var packs            = dotnetRoot is null ? null : Path.Combine(dotnetRoot.FullName, "packs", "Microsoft.NETCore.App.Ref");

			if (packs is null || !Directory.Exists(packs))
				return null;

			var major = Environment.Version.Major + ".";

			return Directory
				.GetDirectories(packs)
				.Where(d => Path.GetFileName(d).StartsWith(major, StringComparison.Ordinal))
				.OrderBy(static d => Path.GetFileName(d), StringComparer.Ordinal)
				.Select(d => Path.Combine(d, "ref", "net" + Environment.Version.Major + ".0"))
				.LastOrDefault(Directory.Exists);
		}

		[TestMethod]
		public void FrameworkMethodsReadFromTheReferencePack()
		{
			var pack = FindReferencePack();

			if (pack is null)
				Assert.Inconclusive("The reference pack of the running framework is not installed.");

			var references = Directory.GetFiles(pack, "*.dll").Select(static f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToList();
			var parse      = new CSharpParseOptions(LanguageVersion.Preview);
			var compilation = CSharpCompilation.Create(
				"PackConsumer",
				[CSharpSyntaxTree.ParseText("using System.Linq; class C { void M(System.Collections.Generic.IEnumerable<int> n, System.Collections.Generic.List<int> l) { n.Any(); l.Add(1); } }", parse, "C.cs")],
				references,
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

			var tree    = compilation.SyntaxTrees.Single();
			var model   = compilation.GetSemanticModel(tree);
			var context = new FactContext(compilation);

			MethodFacts Create(string type, string name, int parameters)
			{
				var symbol = compilation.GetTypeByMetadataName(type)!.GetMembers(name).OfType<IMethodSymbol>().First(m => m.Parameters.Length == parameters);

				return MethodFacts.Create(symbol, FactProperty.All, context);
			}

			// What the reference pack declares is what the consumer is compiled against: runtime members that are
			// protected internal there are protected here.
			var clone = Create("System.Object", "MemberwiseClone", 0);

			Assert.AreEqual(Accessibility.Protected, clone.Accessibility);
			Assert.IsFalse(clone.IsProject);

			Assert.AreEqual(Virtuality.Virtual, Create("System.Object", "ToString", 0).Virtuality);
			Assert.AreEqual(Virtuality.Override, Create("System.String", "ToString", 0).Virtuality);
			Assert.IsTrue(Create("System.Threading.Tasks.Task", "Delay", 1).IsAsync);
			Assert.IsTrue(Create("System.Threading.Tasks.Task", "Delay", 1).IsStatic);
			Assert.IsFalse(Create("System.String", "Join", 2).IsAsync);
			Assert.AreEqual("System.String.Join", Create("System.String", "Join", 2).MethodPath);

			var any = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().First(i => i.ToString() == "n.Any()");
			var facts = MethodFacts.Create((IMethodSymbol)model.GetSymbolInfo(any).Symbol!, FactProperty.All, context);

			Assert.IsTrue(facts.IsExtension);
			Assert.AreEqual("System.Linq.Enumerable.Any<System.Int32>", facts.MethodPath);
			Assert.AreEqual(ParameterModifiers.This, facts.Parameters.Single().Modifiers);

			var add = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().First(i => i.ToString() == "l.Add(1)");
			var call = MethodFacts.Create((IMethodSymbol)model.GetSymbolInfo(add).Symbol!, FactProperty.All, context);

			Assert.AreEqual("System.Collections.Generic.List<System.Int32>.Add", call.MethodPath);
			Assert.AreEqual("System.Collections.Generic.List<T>.Add", call.Definition.MethodPath);
			Assert.IsTrue(call.ImplementedMembers.Any(static m => m.ContainingType.Name == "ICollection"));
		}
	}
}
