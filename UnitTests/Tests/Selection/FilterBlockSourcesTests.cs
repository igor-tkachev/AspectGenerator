using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;

using AspectGenerator.Cascade;
using AspectGenerator.Selection;
using AspectGenerator.TargetFilterBinding;
using AspectGenerator.Tests.TargetFilterBinding;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.Selection
{
	/// <summary>
	/// The blocks of the cascade read from the attributes of an aspect: one per attribute on every level, from the source
	/// and from referenced assemblies, with the settings each attribute writes.
	/// </summary>
	[TestClass]
	public class FilterBlockSourcesTests
	{
		/// <summary>An aspect as the tests declare it: a filter as <c>string[]</c>, a constructor with a default, settings of several types.</summary>
		const string AspectSource = """
			namespace AspectGenerator
			{
				[System.AttributeUsage(System.AttributeTargets.Class)]
				public sealed class AspectAttribute : System.Attribute
				{
					public string? DefaultTargetFilter { get; set; }
				}
			}

			namespace Shop
			{
				public sealed class NoLogAttribute : System.Attribute
				{
				}

				[AspectGenerator.Aspect(DefaultTargetFilter = "- attribute: " + nameof(NoLogAttribute))]
				[System.AttributeUsage(System.AttributeTargets.All, AllowMultiple = true)]
				public sealed class LogAttribute : System.Attribute
				{
					public LogAttribute()
					{
					}

					public LogAttribute(int code, string name = "x")
					{
					}

					public string?[]? TargetFilter { get; set; }
					public int        Level        { get; set; }
					public object?    Value        { get; set; }
					public string?    Format       { get; set; }
				}

				[AspectGenerator.Aspect]
				[System.AttributeUsage(System.AttributeTargets.All, AllowMultiple = true)]
				public sealed class TraceAttribute : System.Attribute
				{
					public TraceAttribute(int code = 7)
					{
					}

					public string? TargetFilter { get; set; }
				}
			}
			""";

		static CSharpCompilation Compile(string source, string name = "Probe", params MetadataReference[] references)
		{
			return FilterProbe.Compile(source, name, references);
		}

		static MetadataReference Emit(CSharpCompilation compilation, bool metadataOnly)
		{
			using var stream = new MemoryStream();

			var result = compilation.Emit(stream, options: new EmitOptions(metadataOnly: metadataOnly));

			Assert.IsTrue(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

			return MetadataReference.CreateFromImage(stream.ToArray());
		}

		static INamedTypeSymbol Type(Compilation compilation, string name)
		{
			return compilation.GetTypeByMetadataName(name) ?? throw new InvalidOperationException($"No type '{name}'.");
		}

		static IMethodSymbol Method(Compilation compilation, string type, string name)
		{
			return Type(compilation, type).GetMembers(name).OfType<IMethodSymbol>().Single();
		}

		static INamedTypeSymbol Log(Compilation compilation)
		{
			return Type(compilation, "Shop.LogAttribute");
		}

		static FilterBlockSource Source(FilterBlock block)
		{
			return (FilterBlockSource)block.Block.Source!;
		}

		/// <summary>A block as one line: its label, the items it read, its declarations and the settings it writes.</summary>
		static string Describe(FilterBlock block)
		{
			var source       = Source(block);
			var items        = string.Join(" | ", source.Texts.Select(static t => $"\"{t}\""));
			var declarations = string.Join("; ", block.Block.Declarations);
			var settings     = string.Join(", ", block.Block.Settings.OrderBy(static s => s.Key, StringComparer.Ordinal).Select(static s => $"{s.Key}={s.Value}"));

			Assert.AreEqual(source.Level, block.Block.Level);
			Assert.AreEqual(0, block.Diagnostics.Length, $"{block.Block.Label}: {string.Join("; ", block.Diagnostics)}");
			Assert.AreEqual(source.Texts.Length, source.Items.Length, block.Block.Label);

			return $"{block.Block.Label}: [{items}] {{{declarations}}} ({settings})";
		}

		static AspectDefinitionRegistry Registry(Compilation compilation)
		{
			AspectDefinitionRegistry.FindDeclarations(compilation, CancellationToken.None, out var aspects, out _);

			return AspectDefinitionRegistry.Create(compilation, aspects, NullDiagnosticSink.Instance, CancellationToken.None);
		}

		static FilterBlock AspectDefault(Compilation compilation)
		{
			Assert.IsTrue(Registry(compilation).TryGetDefinition(Log(compilation), out var definition));

			return FilterBlockSources.AspectDefault(definition);
		}

		[TestMethod]
		public void AspectDefaultFromSourceTest()
		{
			var compilation = Compile(AspectSource);
			var block       = AspectDefault(compilation);

			Assert.AreEqual("DefaultTargetFilter of Log: [\"- attribute: NoLogAttribute\"] {- attribute: NoLogAttribute} ()", Describe(block));
			Assert.IsFalse(Source(block).IsFromMetadata);
			Assert.IsInstanceOfType<BinaryExpressionSyntax>(Source(block).Items[0]);
		}

		[TestMethod]
		public void AspectDefaultFromReferencedAssemblyTest()
		{
			foreach (var metadataOnly in new[] { false, true })
			{
				var library     = Emit(Compile(AspectSource, "Lib"), metadataOnly);
				var compilation = Compile("class C { }", "App", library);
				var block       = AspectDefault(compilation);

				Assert.AreEqual("DefaultTargetFilter of Log in 'Lib': [\"- attribute: NoLogAttribute\"] {- attribute: NoLogAttribute} ()", Describe(block));
				Assert.IsTrue(Source(block).IsFromMetadata);
				Assert.IsNull(Source(block).Items[0]);
				Assert.AreEqual("Lib", Source(block).Assembly.Name);
			}
		}

		[TestMethod]
		public void AspectWithoutDefaultHasAnEmptyBlockTest()
		{
			var compilation = Compile(AspectSource);

			Assert.IsTrue(Registry(compilation).TryGetDefinition(Type(compilation, "Shop.TraceAttribute"), out var definition));

			var block = FilterBlockSources.AspectDefault(definition);

			Assert.AreEqual("DefaultTargetFilter of Trace: [] {} ()", Describe(block));
		}

		[TestMethod]
		public void AssemblyLevelTest()
		{
			var compilation = Compile("""
				[assembly: Shop.Log]
				[assembly: Shop.Log(TargetFilter = ["origin: any", "- static"], Level = 2)]
				[assembly: Shop.Trace(TargetFilter = "public")]
				""" + AspectSource);

			var blocks = FilterBlockSources.Assembly(compilation.Assembly, Log(compilation), compilation);

			CollectionAssert.AreEqual(
				new[]
				{
					"[assembly: Log] #1: [] {project (implicit)} ()",
					"[assembly: Log] #2: [\"origin: any\" | \"- static\"] {origin: any; - static} (Level=2)",
				},
				blocks.Select(Describe).ToArray());

			Assert.IsTrue(blocks.All(static b => b.Block.Level == CascadeLevel.Assembly));
		}

		[TestMethod]
		public void TypeLevelPartialDeclarationsTest()
		{
			var compilation = Compile(AspectSource + """

				namespace Shop
				{
					[Log]
					partial class OrderService
					{
						public void Save() { }
					}

					[Log(TargetFilter = ["public"])]
					[Trace]
					partial class OrderService
					{
						public void Load() { }
					}
				}
				""");

			var blocks = FilterBlockSources.Type(Type(compilation, "Shop.OrderService"), Log(compilation), compilation);

			CollectionAssert.AreEqual(
				new[]
				{
					"[Log] on Shop.OrderService #1: [] {location: here (implicit)} ()",
					"[Log] on Shop.OrderService #2: [\"public\"] {public; location: here (implicit)} ()",
				},
				blocks.Select(Describe).ToArray());

			// Each block stands on its own declaration.
			var declarations = blocks.Select(static b => Source(b).Syntax!.Parent!.Parent).ToArray();

			Assert.AreNotSame(declarations[0], declarations[1]);
			Assert.IsTrue(declarations.All(static d => d is ClassDeclarationSyntax));
		}

		[TestMethod]
		public void MethodLevelTest()
		{
			var compilation = Compile(AspectSource + """

				namespace Shop
				{
					class OrderService
					{
						[Log]
						public void Save() { }

						[Log(TargetFilter = ["public"])]
						void Run() { }

						[Log(TargetFilter = ["attribute: any"])]
						[NoLog]
						void Ping() { }
					}
				}
				""");

			var log = Log(compilation);

			Assert.AreEqual(
				"[Log] on Shop.OrderService.Save(): [] {location: here (implicit); method: here (implicit)} ()",
				Describe(FilterBlockSources.Method(Method(compilation, "Shop.OrderService", "Save"), log, compilation).Single()));

			// A filter on a method is an ordinary method block.
			Assert.AreEqual(
				"[Log] on Shop.OrderService.Run(): [\"public\"] {public; location: here (implicit); method: here (implicit)} ()",
				Describe(FilterBlockSources.Method(Method(compilation, "Shop.OrderService", "Run"), log, compilation).Single()));

			Assert.AreEqual(
				"[Log] on Shop.OrderService.Ping(): [\"attribute: any\"] {attribute: any; location: here (implicit); method: here (implicit)} ()",
				Describe(FilterBlockSources.Method(Method(compilation, "Shop.OrderService", "Ping"), log, compilation).Single()));
		}

		[TestMethod]
		public void LocalFunctionTest()
		{
			var compilation = Compile(AspectSource + """

				namespace Shop
				{
					class OrderService
					{
						public void Save()
						{
							Write();

							[Log]
							void Write() { }
						}
					}
				}
				""");

			var tree     = compilation.SyntaxTrees.Single();
			var syntax   = tree.GetRoot().DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single();
			var function = (IMethodSymbol)compilation.GetSemanticModel(tree).GetDeclaredSymbol(syntax)!;

			Assert.AreEqual(MethodKind.LocalFunction, function.MethodKind);
			Assert.AreEqual(
				"[Log] on Write(): [] {location: here (implicit); method: here (implicit)} ()",
				Describe(FilterBlockSources.Method(function, Log(compilation), compilation).Single()));
		}

		[TestMethod]
		public void StringArrayWithNullItemTest()
		{
			var compilation = Compile("""
				[assembly: Shop.Log(TargetFilter = new[] { "public", null, "- static" })]
				[assembly: Shop.Log(TargetFilter = ["public", null])]
				[assembly: Shop.Log(TargetFilter = null)]
				""" + AspectSource);

			var blocks = FilterBlockSources.Assembly(compilation.Assembly, Log(compilation), compilation);

			CollectionAssert.AreEqual(
				new[]
				{
					"[assembly: Log] #1: [\"public\" | \"\" | \"- static\"] {public; - static; project (implicit)} ()",
					"[assembly: Log] #2: [\"public\" | \"\"] {public; project (implicit)} ()",
					"[assembly: Log] #3: [] {project (implicit)} ()",
				},
				blocks.Select(Describe).ToArray());

			// Each item keeps the expression of its element, the null one too.
			var items = Source(blocks[0]).Items;

			CollectionAssert.AreEqual(
				new[] { "\"public\"", "null", "\"- static\"" },
				items.Select(static i => i!.ToString()).ToArray());
			CollectionAssert.AreEqual(
				new[] { "\"public\"", "null" },
				Source(blocks[1]).Items.Select(static i => i!.ToString()).ToArray());
		}

		[TestMethod]
		public void StringFilterTest()
		{
			var compilation = Compile("""
				[assembly: Shop.Trace(TargetFilter = "public")]
				""" + AspectSource);

			var block = FilterBlockSources.Assembly(compilation.Assembly, Type(compilation, "Shop.TraceAttribute"), compilation).Single();

			Assert.AreEqual("[assembly: Trace]: [\"public\"] {public; project (implicit)} ()", Describe(block));
			Assert.AreEqual("\"public\"", Source(block).Items[0]!.ToString());
		}

		[TestMethod]
		public void ConstantFromReferencedAssemblyTest()
		{
			var library = Emit(Compile("""
				namespace Filters
				{
					public static class Shop
					{
						public const string Public = "public";
						public const string Static = "static";
					}
				}
				""", "Constants"), metadataOnly: true);

			var compilation = Compile("""
				[assembly: Shop.Log(TargetFilter = [Filters.Shop.Public, "- " + Filters.Shop.Static])]
				[assembly: Shop.Trace(TargetFilter = Filters.Shop.Public)]
				""" + AspectSource, "Probe", library);

			Assert.AreEqual(
				"[assembly: Log]: [\"public\" | \"- static\"] {public; - static; project (implicit)} ()",
				Describe(FilterBlockSources.Assembly(compilation.Assembly, Log(compilation), compilation).Single()));

			var trace = FilterBlockSources.Assembly(compilation.Assembly, Type(compilation, "Shop.TraceAttribute"), compilation).Single();

			Assert.AreEqual("[assembly: Trace]: [\"public\"] {public; project (implicit)} ()", Describe(trace));
			Assert.IsInstanceOfType<MemberAccessExpressionSyntax>(Source(trace).Items[0]);
		}

		const string LibrarySource = """
			[assembly: Shop.Log(TargetFilter = ["static"], Level = 2)]
			""" + AspectSource + """

			namespace Lib
			{
				[Shop.Log(TargetFilter = ["public"], Format = "f")]
				[Shop.Log(5)]
				public class Writer
				{
					[Shop.Log]
					public void Write() { }

					[Shop.Trace]
					public void Flush() { }
				}
			}
			""";

		/// <summary>
		/// The library's own attributes of the aspect, read from its metadata: the same from an implementation and from a
		/// reference assembly.
		/// </summary>
		[TestMethod]
		public void LibraryAttributesFromImplementationAndReferenceTest()
		{
			var library = Compile(LibrarySource, "Lib");
			var results = new List<string[]>();

			foreach (var metadataOnly in new[] { false, true })
			{
				var reference   = Emit(library, metadataOnly);
				var compilation = Compile("class C { }", "App", reference);
				var assembly    = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(reference)!;
				var log         = assembly.GetTypeByMetadataName("Shop.LogAttribute")!;
				var trace       = assembly.GetTypeByMetadataName("Shop.TraceAttribute")!;
				var writer      = assembly.GetTypeByMetadataName("Lib.Writer")!;

				var blocks = FilterBlockSources.Assembly(assembly, log, compilation)
					.Concat(FilterBlockSources.Type(writer, log, compilation))
					.Concat(FilterBlockSources.Method(writer.GetMembers("Write").OfType<IMethodSymbol>().Single(), log, compilation))
					.Concat(FilterBlockSources.Method(writer.GetMembers("Flush").OfType<IMethodSymbol>().Single(), trace, compilation))
					.ToList();

				Assert.IsTrue(blocks.All(static b => Source(b).IsFromMetadata && Source(b).Items.All(static i => i is null)));
				Assert.IsTrue(blocks.All(b => SymbolEqualityComparer.Default.Equals(Source(b).Assembly, assembly)));

				results.Add([..blocks.Select(Describe)]);
			}

			CollectionAssert.AreEqual(
				new[]
				{
					"[assembly: Log] in 'Lib': [\"static\"] {static; project (implicit)} (Level=2)",
					"[Log] on Lib.Writer #1 in 'Lib': [\"public\"] {public; location: here (implicit)} (Format=\"f\")",
					"[Log] on Lib.Writer #2 in 'Lib': [] {location: here (implicit)} (.ctor=(5, \"x\"))",
					"[Log] on Lib.Writer.Write() in 'Lib': [] {location: here (implicit); method: here (implicit)} ()",
					// A defaulted constructor argument is stored as written: from metadata it cannot be told apart.
					"[Trace] on Lib.Writer.Flush() in 'Lib': [] {location: here (implicit); method: here (implicit)} (.ctor=(7))",
				},
				results[0]);
			CollectionAssert.AreEqual(results[0], results[1]);
		}

		[TestMethod]
		public void ConstructorWrittenOrNotTest()
		{
			var compilation = Compile(AspectSource + """

				namespace Shop
				{
					class OrderService
					{
						[Log] void A() { }
						[Log()] void B() { }
						[Log(1)] void C() { }
						[Log(code: 1)] void D() { }
						[Log(1, "x")] void E() { }
						[Trace] void F() { }
						[Trace(7)] void G() { }
						[Log(Level = 0)] void H() { }
					}
				}
				""");

			string Settings(string method, string aspect = "Shop.LogAttribute")
			{
				var block = FilterBlockSources.Method(Method(compilation, "Shop.OrderService", method), Type(compilation, aspect), compilation).Single();

				return string.Join(", ", block.Block.Settings.OrderBy(static s => s.Key, StringComparer.Ordinal).Select(static s => $"{s.Key}={s.Value}"));
			}

			Assert.AreEqual("",                     Settings("A"));
			Assert.AreEqual("",                     Settings("B"));
			Assert.AreEqual(".ctor=(1, \"x\")",     Settings("C"));
			Assert.AreEqual(".ctor=(1, \"x\")",     Settings("D"));
			Assert.AreEqual(".ctor=(1, \"x\")",     Settings("E"));

			// A defaulted constructor argument is not written in the source; written with the same value, it is.
			Assert.AreEqual("",                     Settings("F", "Shop.TraceAttribute"));
			Assert.AreEqual(".ctor=(7)",            Settings("G", "Shop.TraceAttribute"));

			// A named argument written with its default value is written.
			Assert.AreEqual("Level=0",              Settings("H"));
		}

		[TestMethod]
		public void SettingWrittenOnAssemblyOnlyTest()
		{
			var probe = new ProbeCompilation("""
				[assembly: Shop.Log(Level = 2)]
				""" + AspectSource + """

				namespace Shop
				{
					[Log]
					class OrderService
					{
						public void Save() { }
					}
				}
				""");

			var compilation = probe.Compilation;
			var log         = Log(compilation);
			var assembly    = FilterBlockSources.Assembly(compilation.Assembly, log, compilation).Single();
			var type        = FilterBlockSources.Type(Type(compilation, "Shop.OrderService"), log, compilation).Single();

			Assert.AreEqual("[assembly: Log]: [] {project (implicit)} (Level=2)",                     Describe(assembly));
			Assert.AreEqual("[Log] on Shop.OrderService: [] {location: here (implicit)} ()",          Describe(type));

			// The method of the type keeps the assembly's value.
			var decision = Decide(probe, log, Method(compilation, "Shop.OrderService", "Save"));

			Assert.IsTrue(decision.Selected);
			Assert.AreEqual("2", decision.Settings["Level"].Value!.ToString());
			Assert.AreEqual("[assembly: Log]", ((CascadeBlock<FilterTarget>)decision.Settings["Level"].Block).Label);
		}

		/// <summary>The decision of the cascade for a method of the compilation, from every block of the aspect there.</summary>
		static CascadeDecision Decide(ProbeCompilation probe, INamedTypeSymbol aspect, IMethodSymbol method)
		{
			var compilation = probe.Compilation;

			Assert.IsTrue(Registry(compilation).TryGetDefinition(aspect, out var definition));

			var aspectDefault = FilterBlockSources.AspectDefault(definition);
			var assembly      = FilterBlockSources.Assembly(compilation.Assembly, aspect, compilation);
			var types         = FilterBlockSources.Type(method.ContainingType, aspect, compilation);
			var methods       = FilterBlockSources.Method(method, aspect, compilation);
			var evaluator     = new CascadeEvaluator<FilterTarget>(
				aspectDefault.Block,
				assembly.Select(static b => b.Block),
				types.Concat(methods).Select(static b => b.Block),
				static t => t.Facts.IsProject);

			Assert.IsFalse(evaluator.SelectsNothing, string.Join("; ", evaluator.Diagnostics));

			return evaluator.Decide(
				FilterTarget.Definition(probe.Definition(method)),
				[..types.Select(static b => b.Block)],
				[..methods.Select(static b => b.Block)]);
		}

		static AttributeSetting Setting(Compilation compilation, string method, string name)
		{
			var block = FilterBlockSources.Method(Method(compilation, "Shop.OrderService", method), Log(compilation), compilation).Single();

			return (AttributeSetting)block.Block.Settings[name]!;
		}

		/// <summary>Setting values compare as the attribute would be built: by type and value, arrays element by element.</summary>
		[TestMethod]
		public void SettingValuesCompareByTypeTest()
		{
			var compilation = Compile(AspectSource + """

				namespace Shop
				{
					class OrderService
					{
						[Log(Value = 1)]                       void Int() { }
						[Log(Value = 1)]                       void Int2() { }
						[Log(Value = 1L)]                      void Long() { }
						[Log(Value = new int[] { 1, 2 })]      void Ints() { }
						[Log(Value = new int[] { 1, 2 })]      void Ints2() { }
						[Log(Value = new object[] { 1, 2 })]   void Objects() { }
						[Log(Value = typeof(string))]          void TypeOf() { }
						[Log(Value = typeof(string))]          void TypeOf2() { }
						[Log(Value = null)]                    void Null() { }
						[Log(1)]                               void Ctor() { }
						[Log(1, "x")]                          void Ctor2() { }
						[Log(2)]                               void Ctor3() { }
					}
				}
				""");

			var comparer = CascadeSettingComparer.Instance;

			bool Same(string a, string b, string name = "Value")
			{
				var x = Setting(compilation, a, name);
				var y = Setting(compilation, b, name);
				var equal = comparer.Equals(x, y);

				if (equal)
					Assert.AreEqual(comparer.GetHashCode(x), comparer.GetHashCode(y), $"{a}, {b}");

				return equal;
			}

			Assert.IsTrue (Same("Int",     "Int2"));
			Assert.IsFalse(Same("Int",     "Long"));
			Assert.IsTrue (Same("Ints",    "Ints2"));
			Assert.IsFalse(Same("Ints",    "Objects"));
			Assert.IsFalse(Same("Int",     "Ints"));
			Assert.IsTrue (Same("TypeOf",  "TypeOf2"));
			Assert.IsFalse(Same("TypeOf",  "Null"));
			Assert.IsTrue (Same("Ctor",    "Ctor2",  FilterBlockSources.ConstructorSetting));
			Assert.IsFalse(Same("Ctor",    "Ctor3",  FilterBlockSources.ConstructorSetting));
		}

		const string EffectiveSource = """
			[assembly: Shop.Log(Level = 2, Value = new[] { 1, 2 })]
			""" + AspectSource + """

			namespace Shop
			{
				[Log(Format = "f")]
				class OrderService
				{
					public void Save() { }
					public void Load() { }

					[Log(Level = 3)]
					public void Ping() { }

					[Log(4, Value = new[] { 1, 2 })]
					public void Other() { }
				}
			}
			""";

		static AttributeData AttributeOn(ISymbol symbol)
		{
			return symbol.GetAttributes().Single(static a => a.AttributeClass?.Name == "LogAttribute");
		}

		static string Describe(EffectiveApplication application)
		{
			var constructor = string.Join(", ", application.ConstructorArguments.Select(static a => a.ToCSharpString()));
			var named       = string.Join(", ", application.NamedArguments.Select(static a => $"{a.Name} = {a.Value.ToCSharpString()} from {a.Source.ApplicationSyntaxReference!.GetSyntax()}"));

			return $"new({constructor}) from {application.ConstructorSource.ApplicationSyntaxReference!.GetSyntax()} {{ {named} }} at {application.ReportAt.ApplicationSyntaxReference!.GetSyntax()}";
		}

		[TestMethod]
		public void EffectiveApplicationTest()
		{
			var probe       = new ProbeCompilation(EffectiveSource);
			var compilation = probe.Compilation;
			var log         = Log(compilation);
			var type        = Type(compilation, "Shop.OrderService");

			EffectiveApplication Effective(string method, ISymbol reportAt)
			{
				var decision = Decide(probe, log, Method(compilation, "Shop.OrderService", method));

				Assert.IsTrue(decision.Selected, method);
				Assert.AreEqual(0, decision.Conflicts.Length, method);

				return FilterBlockSources.CreateApplication(decision.Settings, AttributeOn(reportAt));
			}

			var save  = Effective("Save", type);
			var ping  = Effective("Ping", Method(compilation, "Shop.OrderService", "Ping"));
			var other = Effective("Other", Method(compilation, "Shop.OrderService", "Other"));

			Assert.AreEqual(
				"new() from Log(Format = \"f\") { Level = 2 from Shop.Log(Level = 2, Value = new[] { 1, 2 }), Value = {1, 2} from Shop.Log(Level = 2, Value = new[] { 1, 2 }), Format = \"f\" from Log(Format = \"f\") } at Log(Format = \"f\")",
				Describe(save));
			Assert.AreEqual(
				"new() from Log(Level = 3) { Value = {1, 2} from Shop.Log(Level = 2, Value = new[] { 1, 2 }), Format = \"f\" from Log(Format = \"f\"), Level = 3 from Log(Level = 3) } at Log(Level = 3)",
				Describe(ping));
			Assert.AreEqual(
				"new(4, \"x\") from Log(4, Value = new[] { 1, 2 }) { Level = 2 from Shop.Log(Level = 2, Value = new[] { 1, 2 }), Format = \"f\" from Log(Format = \"f\"), Value = {1, 2} from Log(4, Value = new[] { 1, 2 }) } at Log(4, Value = new[] { 1, 2 })",
				Describe(other));
		}

		/// <summary>
		/// Calls are grouped into one interceptor by the equality of their aspect lists: two applications with equal
		/// settings, built separately, are equal; different settings or a different attribute make them differ.
		/// </summary>
		[TestMethod]
		public void EffectiveApplicationEqualityTest()
		{
			var probe       = new ProbeCompilation(EffectiveSource);
			var compilation = probe.Compilation;
			var log         = Log(compilation);
			var type        = Type(compilation, "Shop.OrderService");

			AttributeInfo Info(string method, ISymbol reportAt)
			{
				var decision    = Decide(probe, log, Method(compilation, "Shop.OrderService", method));
				var application = FilterBlockSources.CreateApplication(decision.Settings, AttributeOn(reportAt));

				return new AttributeInfo(null, null, null, log, null, null, null, null) { EffectiveApplication = application };
			}

			bool Grouped(AttributeInfo x, AttributeInfo y)
			{
				var method = Method(compilation, "Shop.OrderService", "Save");

				return InterceptorGroupComparer.Instance.Equals(new AnalyzedInvocation(null!, method, [x]), new AnalyzedInvocation(null!, method, [y]));
			}

			var save = Info("Save", type);
			var load = Info("Load", type);
			var ping = Info("Ping", Method(compilation, "Shop.OrderService", "Ping"));

			Assert.AreNotSame(save.EffectiveApplication, load.EffectiveApplication);
			Assert.AreEqual(save, load);
			Assert.AreEqual(save.GetHashCode(), load.GetHashCode());
			Assert.IsTrue(Grouped(save, load));

			Assert.AreNotEqual(save, ping);
			Assert.IsFalse(Grouped(save, ping));

			// The same attributes with a value equal element by element, read from another attribute, are equal; another
			// value is not.
			var assemblyAttribute = AttributeOn(compilation.Assembly);
			var otherAttribute    = AttributeOn(Method(compilation, "Shop.OrderService", "Other"));
			var typeAttribute     = AttributeOn(type);

			EffectiveApplication Application(TypedConstant value)
			{
				return new EffectiveApplication(
					typeAttribute,
					typeAttribute.AttributeConstructor,
					typeAttribute.ConstructorArguments,
					typeAttribute,
					[new EffectiveNamedArgument("Value", value, assemblyAttribute)]);
			}

			var fromAssembly = assemblyAttribute.NamedArguments.Single(static a => a.Key == "Value").Value;
			var fromOther    = otherAttribute.NamedArguments.Single(static a => a.Key == "Value").Value;
			var level        = assemblyAttribute.NamedArguments.Single(static a => a.Key == "Level").Value;

			Assert.AreEqual(Application(fromAssembly), Application(fromOther));
			Assert.AreEqual(Application(fromAssembly).GetHashCode(), Application(fromOther).GetHashCode());
			Assert.AreNotEqual(Application(fromAssembly), Application(level));

			// Another attribute as the source of an equal value is another application.
			var fromOtherSource = new EffectiveApplication(
				typeAttribute,
				typeAttribute.AttributeConstructor,
				typeAttribute.ConstructorArguments,
				typeAttribute,
				[new EffectiveNamedArgument("Value", fromOther, otherAttribute)]);

			Assert.AreNotEqual(Application(fromAssembly), fromOtherSource);
		}

		[TestMethod]
		public void ConditionalAspectOffInSourceTest()
		{
			var compilation = Compile("""
				namespace Shop
				{
					[System.Diagnostics.Conditional("TRACE_ON")]
					[System.AttributeUsage(System.AttributeTargets.All, AllowMultiple = true)]
					public sealed class LogAttribute : System.Attribute
					{
					}

					[Log]
					class OrderService
					{
					}
				}
				""");

			Assert.AreEqual(0, FilterBlockSources.Type(Type(compilation, "Shop.OrderService"), Log(compilation), compilation).Length);
		}
	}
}
