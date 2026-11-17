using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AspectGenerator.Cascade;
using AspectGenerator.FilterV2;
using AspectGenerator.TargetFilterBinding;
using AspectGenerator.TargetFilterFacts;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilterBinding
{
	/// <summary>
	/// A filter's text into a block of the cascade: the declarations its lines make, the facts they read, and the errors
	/// that need the whole block, each at its place in the text.
	/// </summary>
	[TestClass]
	public class FilterBlockBuilderTests
	{
		/// <summary>The diagnostics of a filter as <c>id [item] 'the text they point at'</c>.</summary>
		static List<string> Errors(CascadeLevel level, params string[] items)
		{
			var block = FilterBlockBuilder.Build(level, items);

			// The cascade finds nothing the builder has not found.
			Assert.IsTrue(block.Block.Diagnostics.All(static d => d.Source is FilterBlockDiagnostic), string.Join("; ", block.Block.Diagnostics));
			Assert.AreEqual(block.Diagnostics.Length, block.Block.Diagnostics.Length);
			Assert.AreEqual(block.Diagnostics.Length != 0, block.HasErrors);

			return [..block.Diagnostics.Select(d => $"{d.Id} [{d.Item}] '{items[d.Item].Substring(d.Span.Start, d.Span.Length)}'")];
		}

		static void AssertErrors(CascadeLevel level, string[] items, params string[] expected)
		{
			Assert.AreEqual(string.Join(Environment.NewLine, expected), string.Join(Environment.NewLine, Errors(level, items)), string.Join(" ¦ ", items));
		}

		[TestMethod]
		public void APropertyIsIncludedOnceTest()
		{
			AssertErrors(CascadeLevel.Type, ["method: Save*\nmethod: Get*"],                   "AG0225 [0] 'method: Get*'");
			AssertErrors(CascadeLevel.Type, ["method: Save*\nmethod: Get*\n- method: *Internal"], "AG0225 [0] 'method: Get*'");
			AssertErrors(CascadeLevel.Type, ["method: Save*\n  Get*  # the same property"],    "AG0225 [0] 'Get*'");
			AssertErrors(CascadeLevel.Type, ["method: Save* and public\ninternal"],            "AG0225 [0] 'internal'");
			AssertErrors(CascadeLevel.Type, ["public static\nprivate"],                        "AG0225 [0] 'private'");
			AssertErrors(CascadeLevel.Type, ["namespace: Shop\ntype: Order"],                  "AG0225 [0] 'type: Order'");
			AssertErrors(CascadeLevel.Type, ["method: Save*\nnot assembly.method"],            "AG0225 [0] 'not assembly.method'");
			AssertErrors(CascadeLevel.Type, ["method: Save* and assembly.method\nmethod: Get*"], "AG0225 [0] 'method: Get*'");

			// A compound line includes each of its properties.
			AssertErrors(CascadeLevel.Type, ["method: Save* or public\nmethod: Get*"],         "AG0225 [0] 'method: Get*'");
			AssertErrors(CascadeLevel.Type, ["method: Get*\nmethod: Save* or public"],         "AG0225 [0] 'method: Save* or public'");
			AssertErrors(CascadeLevel.Type, ["method: Save* or public\ninternal"],             "AG0225 [0] 'internal'");

			// Only the part of a line that repeats is pointed at.
			AssertErrors(CascadeLevel.Type, ["method: Save*\nstatic and method: Get* and public"], "AG0225 [0] 'method: Get*'");
			AssertErrors(CascadeLevel.Type, ["method: Save*\nSave*(int): Task"],               "AG0225 [0] 'Save*'");

			// Every later include is reported, once.
			AssertErrors(CascadeLevel.Type, ["method: A\nmethod: B\nmethod: C"],               "AG0225 [0] 'method: B'", "AG0225 [0] 'method: C'");

			// All items of one attribute are one filter.
			AssertErrors(CascadeLevel.Type, ["method: Save*", "public", "method: Get*"],       "AG0225 [2] 'method: Get*'");

			// What is no repetition.
			AssertErrors(CascadeLevel.Type, ["method: Save* or Get*"]);
			AssertErrors(CascadeLevel.Type, ["method: Save* and method: *Async"]);
			AssertErrors(CascadeLevel.Type, ["method: Save* and (method: *A or public)"]);
			AssertErrors(CascadeLevel.Type, ["namespace: Shop and type: Service"]);
			AssertErrors(CascadeLevel.Type, ["method: Save*\n- method: *Internal\n- method: *Draft"]);
			AssertErrors(CascadeLevel.Type, ["method: Save*\n- method: X or public\n- public"]);
			AssertErrors(CascadeLevel.Type, ["- (method: Save* or public)\nmethod: Get*\npublic"]);
			AssertErrors(CascadeLevel.Type, ["public **.*Service.Save*(.., CancellationToken) : Task*\nstatic\nattribute: Audited"]);
		}

		[TestMethod]
		public void TheRepeatedIncludeSaysHowToWriteItTest()
		{
			var block = FilterBlockBuilder.Build(CascadeLevel.Type, ["method: Save*\nmethod: Get*"]);

			StringAssert.Contains(block.Diagnostics.Single().Message, "'method' is included by 'method: Save*' and again by 'method: Get*'");
			StringAssert.Contains(block.Diagnostics.Single().Message, "'method: Save* or method: Get*' (either)");
			StringAssert.Contains(block.Diagnostics.Single().Message, "'method: Save* and method: Get*' (both)");

			// A part that is an `or` goes into an `and` in parentheses, and what the rewrite joins is read on one line.
			block = FilterBlockBuilder.Build(CascadeLevel.Type, ["method: Save*\n   or public\nmethod: Get*"]);

			StringAssert.Contains(block.Diagnostics.Single().Message, "'method: Save* or public or method: Get*' (either)");
			StringAssert.Contains(block.Diagnostics.Single().Message, "'(method: Save* or public) and method: Get*' (both)");
		}

		[TestMethod]
		public void AnyIsAWholeLineAndTheOnlyIncludeTest()
		{
			AssertErrors(CascadeLevel.Type, ["method: any\nmethod: Save*"],              "AG0234 [0] 'method: Save*'");
			AssertErrors(CascadeLevel.Type, ["method: Save*\nmethod: any"],              "AG0234 [0] 'method: any'");
			AssertErrors(CascadeLevel.Type, ["method: any", "method: any"],              "AG0234 [1] 'method: any'");
			AssertErrors(CascadeLevel.Type, ["location: any\ntype: Order"],              "AG0234 [0] 'type: Order'");
			AssertErrors(CascadeLevel.Type, ["public and method: any"],                  "AG0234 [0] 'any'");
			AssertErrors(CascadeLevel.Type, ["public and (static or method: any)"],      "AG0234 [0] 'any'");
			AssertErrors(CascadeLevel.Type, ["(method: any) or (accessibility: any)"],   "AG0234 [0] 'any'", "AG0234 [0] 'any'");
			AssertErrors(CascadeLevel.Type, ["method: Save*\nand method: any"],          "AG0234 [0] 'any'");

			// The reader's own: a subtraction of any, any under not, any without a key. Each is reported once.
			AssertErrors(CascadeLevel.Type, ["- method: any"],                           "AG0234 [0] 'any'");
			AssertErrors(CascadeLevel.Type, ["not method: any"],                         "AG0234 [0] 'any'");
			AssertErrors(CascadeLevel.Type, ["method: Save* or any"],                    "AG0234 [0] 'any'");

			AssertErrors(CascadeLevel.Type, ["method: any\naccessibility: any\n- method: Internal*"]);
			AssertErrors(CascadeLevel.Assembly, ["origin: any\npublic"]);
		}

		[TestMethod]
		public void AReferenceNamesALevelAboveItsBlockTest()
		{
			AssertErrors(CascadeLevel.Aspect,   ["aspect.method"],                 "AG0233 [0] 'aspect.method'");
			AssertErrors(CascadeLevel.Aspect,   ["- assembly.attribute"],          "AG0233 [0] 'assembly.attribute'");
			AssertErrors(CascadeLevel.Aspect,   ["method: Save* and aspect.method"], "AG0233 [0] 'aspect.method'");
			AssertErrors(CascadeLevel.Assembly, ["assembly.method"],               "AG0233 [0] 'assembly.method'");
			AssertErrors(CascadeLevel.Assembly, ["method: assembly.method"],       "AG0233 [0] 'assembly.method'");
			AssertErrors(CascadeLevel.Assembly, ["aspect.method"]);
			AssertErrors(CascadeLevel.Assembly, ["attribute: Audited and aspect.attribute"]);
			AssertErrors(CascadeLevel.Type,     ["assembly.method\nattribute: aspect.attribute"]);
			AssertErrors(CascadeLevel.Method,   ["method: Save* or assembly.method\n- aspect.attribute"]);

			var block = FilterBlockBuilder.Build(CascadeLevel.Assembly, ["method: Save* or assembly.method"]);

			StringAssert.Contains(block.Diagnostics.Single().Message, "write 'aspect.method'");
		}

		[TestMethod]
		public void AReferenceStandsInADeclarationOfItsOwnPropertyTest()
		{
			AssertErrors(CascadeLevel.Type, ["assembly.method or public"],                      "AG0233 [0] 'assembly.method'");
			AssertErrors(CascadeLevel.Type, ["method: assembly.method or public"],              "AG0233 [0] 'assembly.method'");
			AssertErrors(CascadeLevel.Type, ["method: Save* and (assembly.method or public)"],  "AG0233 [0] 'assembly.method'");
			AssertErrors(CascadeLevel.Type, ["- assembly.method and public"],                   "AG0233 [0] 'assembly.method'");
			AssertErrors(CascadeLevel.Type, ["assembly.method or aspect.attribute"],            "AG0233 [0] 'assembly.method'", "AG0233 [0] 'aspect.attribute'");

			// The reader's own: a reference to another property, to no property.
			AssertErrors(CascadeLevel.Type, ["method: assembly.accessibility"],                 "AG0233 [0] 'accessibility'");
			AssertErrors(CascadeLevel.Type, ["assembly.Level"],                                 "AG0233 [0] 'Level'");

			AssertErrors(CascadeLevel.Type, ["assembly.method and public"]);
			AssertErrors(CascadeLevel.Type, ["method: Save* and assembly.method and not aspect.method\npublic"]);
			AssertErrors(CascadeLevel.Type, ["- assembly.method or public"]);
			AssertErrors(CascadeLevel.Type, ["method: assembly.method and not method: Get*"]);
		}

		[TestMethod]
		public void TheReadersErrorsKeepTheirItemAndPlaceTest()
		{
			AssertErrors(CascadeLevel.Assembly, ["public", "methd: Save*"],                  "AG0221 [1] 'methd'");
			AssertErrors(CascadeLevel.Assembly, ["public", "and static"],                    "AG0231 [1] 'and'");
			AssertErrors(CascadeLevel.Assembly, ["public\nnot (static or async)"],           "AG0220 [0] 'not (static or async)'");
			AssertErrors(CascadeLevel.Assembly, ["contains: Save"],                          "AG0225 [0] 'contains: Save'");

			// A line with an error is no declaration: the property it names is not included by it.
			AssertErrors(CascadeLevel.Assembly, ["method: Save**\nmethod: Get*"],            "AG0224 [0] '**'");

			// A line the reader refuses and a block error on another line are both reported, in the order of the text.
			AssertErrors(CascadeLevel.Assembly, ["method: A\nmethod: B", "type: Task<>", "method: C"],
				"AG0225 [0] 'method: B'", "AG0220 [1] '<>'", "AG0225 [2] 'method: C'");
		}

		[TestMethod]
		public void AnEmptyFilterDeclaresOnlyWhatItsLevelImpliesTest()
		{
			static string Declared(CascadeLevel level, params string[] items)
			{
				var block = FilterBlockBuilder.Build(level, items);

				Assert.AreEqual(0, block.Diagnostics.Length);

				return string.Join("; ", block.Block.Declarations);
			}

			Assert.AreEqual("",                                                           Declared(CascadeLevel.Aspect));
			Assert.AreEqual("",                                                           Declared(CascadeLevel.Aspect, ""));
			Assert.AreEqual("project (implicit)",                                         Declared(CascadeLevel.Assembly, ""));
			Assert.AreEqual("project (implicit)",                                         Declared(CascadeLevel.Assembly, "  # nothing\n\n"));
			Assert.AreEqual("location: here (implicit)",                                  Declared(CascadeLevel.Type));
			Assert.AreEqual("location: here (implicit); method: here (implicit)",         Declared(CascadeLevel.Method, "", ""));
		}

		[TestMethod]
		public void ALineDeclaresThePropertiesItMentionsTest()
		{
			static string Declared(string filter, CascadeLevel level = CascadeLevel.Aspect)
			{
				var block = FilterBlockBuilder.Build(level, [filter]);

				Assert.AreEqual(0, block.Diagnostics.Length, filter);

				return string.Join("; ", block.Block.Declarations.Select(static d => $"{(d.IsSubtraction ? "- " : "")}{d.Properties} = {d}"));
			}

			Assert.AreEqual("Method = method: Save*; Accessibility = public", Declared("method: Save* and public"));

			Assert.AreEqual("- Method = - method: X; - Accessibility = - public", Declared("- method: X or public"));

			Assert.AreEqual("Location = (namespace: Shop and type: Service)", Declared("namespace: Shop and type: Service"));

			Assert.AreEqual("Method, Accessibility = (method: Save* and (method: *A or public))", Declared("method: Save* and (method: *A or public)"));

			Assert.AreEqual("Method, Accessibility = (method: Save* or public)", Declared("method: Save* or public"));

			Assert.AreEqual("- Method, Accessibility = - (method: *Internal* and public)", Declared("- method: *Internal* and public"));

			// Parentheses do not hold a line together; the conditions on one property are joined in their order.
			Assert.AreEqual("Accessibility = (public or internal); Method = (method: Save* and not method: *Draft)", Declared("(public or internal) and (method: Save* and not method: *Draft)"));

			Assert.AreEqual("Accessibility = public; Static = static; Method = (method: A and method: B)", Declared("public and method: A and static and method: B"));

			// A run of words and a compact signature are the conditions they stand for.
			Assert.AreEqual("Accessibility = public; Static = static", Declared("public static"));

			Assert.AreEqual(
				"Accessibility = public; Method = **.*Service.Save*; Params = (.., CancellationToken); Returns = Task*",
				Declared("public **.*Service.Save*(.., CancellationToken) : Task*"));

			Assert.AreEqual("Method = Save; Returns = Task", Declared("Save: Task"));

			// A subtraction splits at `or` only: a signature in it is one compound part.
			Assert.AreEqual("- Method, Params = - (Save and (int))", Declared("- Save(int)"));

			// `namespace:` and `type:` are both the location; `any` and a reference declare their property.
			Assert.AreEqual("Location = location: any", Declared("location: any"));

			Assert.AreEqual("Method = (method: Save* or assembly.method); Attribute = aspect.attribute; Location = location: here (implicit)",
				Declared("(method: Save* or assembly.method) and aspect.attribute", CascadeLevel.Type));

			Assert.AreEqual("Accessibility = public; Origin = project (implicit)", Declared("public", CascadeLevel.Assembly));

			Assert.AreEqual("Method = method: Save*", Declared("method: Save*", CascadeLevel.Assembly));

			Assert.AreEqual("Origin = not project", Declared("not project", CascadeLevel.Assembly));

			Assert.AreEqual("Static = static; Location = location: here (implicit); Method = method: here (implicit)", Declared("static", CascadeLevel.Method));
		}

		[TestMethod]
		public void TheBlockNamesTheFactsItsConditionsReadTest()
		{
			static FactProperty Facts(string filter, CascadeLevel level = CascadeLevel.Aspect, bool? overridesIsKey = null)
			{
				var block = overridesIsKey is { } isKey ? FilterBlockBuilder.Build(level, [filter], overridesIsKey: isKey) : FilterBlockBuilder.Build(level, [filter]);

				Assert.AreEqual(0, block.Diagnostics.Length, filter);

				return block.Facts;
			}

			Assert.AreEqual(FactProperty.None,                                  Facts(""));
			Assert.AreEqual(FactProperty.Location,                              Facts("namespace: Shop"));
			Assert.AreEqual(FactProperty.Location,                              Facts("type: Order"));
			Assert.AreEqual(FactProperty.Method,                                Facts("method: Save"));
			Assert.AreEqual(FactProperty.Returns,                               Facts("returns: int"));
			Assert.AreEqual(FactProperty.Params,                                Facts("params: ()"));
			Assert.AreEqual(FactProperty.Param,                                 Facts("param: int"));
			Assert.AreEqual(FactProperty.Attribute,                             Facts("- attribute: NoLog"));
			Assert.AreEqual(FactProperty.Inherits,                              Facts("inherits: Base"));
			Assert.AreEqual(FactProperty.Implements | FactProperty.Overrides,   Facts("implements: Base.Save", overridesIsKey: false));
			Assert.AreEqual(FactProperty.Implements,                            Facts("implements: Base.Save", overridesIsKey: true));
			Assert.AreEqual(FactProperty.Overrides,                             Facts("overrides: Base.Save",  overridesIsKey: true));
			Assert.AreEqual(FactProperty.Accessibility | FactProperty.Static,   Facts("public static"));
			Assert.AreEqual(FactProperty.Static,                                Facts("instance"));
			Assert.AreEqual(FactProperty.Async | FactProperty.Virtuality,       Facts("async or sealed"));
			Assert.AreEqual(FactProperty.Extern | FactProperty.Extension | FactProperty.Generated | FactProperty.Origin, Facts("extern\nextension\ngenerated\nproject"));
			Assert.AreEqual(FactProperty.Method | FactProperty.Params | FactProperty.Returns | FactProperty.Accessibility, Facts("public Save*(int): Task"));

			// `any` and a reference read nothing of the method here; the implicit `project` of an assembly filter does.
			Assert.AreEqual(FactProperty.None,                                  Facts("method: any\nattribute: aspect.attribute", CascadeLevel.Type));
			Assert.AreEqual(FactProperty.Accessibility | FactProperty.Origin,   Facts("public", CascadeLevel.Assembly));
			Assert.AreEqual(FactProperty.Method,                                Facts("method: Save*", CascadeLevel.Assembly));
		}

		[TestMethod]
		public void ThePropertiesHaveOneNameInEveryPartTest()
		{
			foreach (FilterProperty property in Enum.GetValues<FilterProperty>())
			{
				var cascade = FilterBlockBuilder.ToCascade(property);

				Assert.AreEqual(property.ToString(), cascade.ToString());
				Assert.AreEqual(property.ToString(), ((FactProperty)(int)cascade).ToString());
				Assert.AreEqual(FilterV2Language.Name(property), cascade.ToFilterName());
			}
		}

		/// <summary>
		/// Every input the grammar's corpus holds, as the filter of a block of each level: the builder reads it without
		/// failing, the cascade finds no error the builder has not found, and a filter without errors answers for a
		/// method in both phases.
		/// </summary>
		[TestMethod]
		public void TheGrammarsCorpusBuildsTest()
		{
			var path = "";

			for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && path.Length == 0; dir = dir.Parent)
				if (File.Exists(Path.Combine(dir.FullName, "AspectGenerator.slnx")))
					path = Path.Combine(dir.FullName, "UnitTests", "Tests", "FilterV2", "corpus.txt");

			var inputs = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n')
				.Where(static l => l.Length != 0 && !l.StartsWith("//", StringComparison.Ordinal))
				.Select(static l => Tests.FilterV2.FilterV2CorpusTests.Unescape(l.Split('\t')[1]))
				.ToList();

			Assert.IsTrue(inputs.Count > 200, $"Only {inputs.Count} inputs.");

			var facts = new RecordFacts("Shop.Services.OrderService", "Save<int>", "System.Threading.Tasks.Task<!T>", "int", "this System.Collections.Generic.List<!T>");
			var clean = 0;

			foreach (var input in inputs)
			{
				foreach (var level in Enum.GetValues<CascadeLevel>())
				{
					var block = FilterBlockBuilder.Build(level, [input]);

					Assert.IsTrue(block.Block.Diagnostics.All(static d => d.Source is FilterBlockDiagnostic), $"{level} '{input}': {string.Join("; ", block.Block.Diagnostics)}");

					foreach (var diagnostic in block.Diagnostics)
						Assert.IsTrue(diagnostic.Span.End <= input.Length, $"{level} '{input}': {diagnostic}");

					if (block.HasErrors)
						continue;

					clean++;

					var evaluator = new CascadeEvaluator<FilterTarget>(
						level == CascadeLevel.Aspect ? block.Block : FilterBlockBuilder.Build(CascadeLevel.Aspect, []).Block,
						[level == CascadeLevel.Assembly ? block.Block : FilterBlockBuilder.Build(CascadeLevel.Assembly, ["origin: any"]).Block],
						[],
						static t => t.Facts.IsProject);

					foreach (var target in new[] { FilterTarget.Definition(facts), FilterTarget.Call(facts) })
					{
						evaluator.Decide(
							target,
							level == CascadeLevel.Type ? [block.Block] : [],
							level == CascadeLevel.Method ? [block.Block] : []);
					}
				}
			}

			Assert.IsTrue(clean > 300, $"Only {clean} filters without errors.");
		}
	}
}
