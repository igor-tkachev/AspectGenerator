using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// <c>AG0210</c>, a valid rule that can match no method. It is defined by the rule (an accessibility mask no method
	/// has; white space in a namespace, type or method name), and measured here: every rule it names matches none of the
	/// targets of the golden corpus, it is silent where a filter may hold white space, and in the curated set it is reported
	/// only for the inputs listed below.
	/// </summary>
	[TestClass]
	public class TargetFilterRuleMatchesNothingTests
	{
		public TestContext TestContext { get; set; } = null!;

		/// <summary>
		/// The rules of the curated set <c>AG0210</c> is reported for. Every one of them matches nothing: the hand cases were
		/// written to pin what the parser does with such a rule, and the documentation lines are not filters at all
		/// (canonical signatures, the grammar of the language, prose); they are in the curated set because every string of
		/// the documentation is.
		/// </summary>
		static readonly string[] CuratedReported =
		[
			// Written as rules that match nothing (here and in the design-time test): contradictory accessibility, a misspelled or capitalized modifier word
			// that becomes part of the name, a second operator read as part of a method name, a word key with a space.
			"public private",
			"public protected **.*",
			"publik Save",
			"publik Invoke",
			"PUBLIC Save",
			"Public Static",
			"- & method: Save",
			"- | method: Save",
			"a b: c",

			// Documentation: canonical signatures as contains: and regex: see them, which are not method patterns.
			"public System.String MyApp.Services.UserService.GetName(System.Int32)",
			"public static System.Void MyApp.Services.Cache.Clear()",
			"public virtual System.Threading.Tasks.Task<System.String> MyApp.Services.UserService.GetNameAsync(System.Int32,System.Threading.CancellationToken)",
			"public System.Boolean MyApp.Services.UserService.TryGet(System.String,out System.Int32)",
			"public System.Void MyApp.Services.UserService.Update(ref MyApp.Models.User)",
			"public System.Int32 MyApp.Services.UserService.Peek(ref readonly System.Int32)",
			"public System.Int32[,] MyApp.Services.UserService.Matrix(System.Int32[][,])",
			"public MyApp.Models.User MyApp.Data.Repository<TEntity>.Get<TKey>(TKey)",
			"public System.Void System.Collections.Generic.List<System.Int32>.Add(System.Int32)",

			// Documentation: the grammar of the language and prose around it.
			"EffectiveTargetFilter = DefaultTargetFilter + TargetFilter",
			"unprefixed  native AspectGenerator target pattern syntax",
			"native AspectGenerator target pattern syntax",
			"[-] [pattern:|regex:|contains:] rule-body",
			"method matches Save*",
			"AND method matches (*Async OR Update*)",
		];

		[TestMethod]
		public void ReportedRuleMatchesNoTargetOfTheCorpusTest()
		{
			var curated = TargetFilterCorpus.Curated().Select(static i => i.Text).ToArray();
			var sets    = new (string Name, IEnumerable<string> Inputs)[]
			{
				("curated",   curated),
				("mutations", TargetFilterCorpus.Mutations(curated, TargetFilterCorpus.MutationsPerInput, TargetFilterCorpus.MutationSeed)),
				("generated", TargetFilterCorpus.Generate(TargetFilterCorpus.GeneratedCount)),
				("spaced",    TargetFilterSpacedInputs.Generate()),
				("names with white space and wildcards", NamesWithWhiteSpace()),
			};

			var targets  = TargetFilterTargets.Entries.Select(static e => e.Target).Concat(FunctionPointerTargets.Value).ToArray();
			var failures = new List<string>();

			foreach (var (name, inputs) in sets)
			{
				var inputsReported = 0;
				var accessibility  = 0;
				var whiteSpace     = 0;
				var total          = 0;

				foreach (var input in inputs)
				{
					total++;

					var reported = new List<AspectFilters.TargetFilterDiagnostic>();
					var set      = AspectFilters.GetFilters(input, d =>
					{
						if (d.Id == AspectDiagnosticID.AspectFilterRuleMatchesNothing)
							reported.Add(d);
					});

					if (reported.Count == 0)
						continue;

					inputsReported++;

					foreach (var diagnostic in reported)
					{
						if (diagnostic.Message.Contains("accessibility", StringComparison.Ordinal))
							accessibility++;
						else
							whiteSpace++;

						Assert.IsTrue(diagnostic.RuleIndex >= 0 && diagnostic.RuleIndex < set.Rules.Count, $"AG0210 for '{TargetFilterProbe.Escape(input)}' names no compiled rule.");

						var rule    = set.Rules[diagnostic.RuleIndex];
						var matched = targets.Where(t => rule.IsMatch(t)).Select(static t => t.Signature).FirstOrDefault();

						if (matched is not null && failures.Count < 20)
							failures.Add($"{name}: '{TargetFilterProbe.Escape(input)}', rule '{rule.RuleText}' matches {matched}");
					}
				}

				TestContext.WriteLine($"{name}: {total} inputs, {inputsReported} with AG0210; rules reported: {accessibility} for accessibility, {whiteSpace} for white space in a name");
			}

			Assert.AreEqual(0, failures.Count, "AG0210 named rules that match a target:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
		}

		/// <summary>
		/// Methods whose names hold white space: a function pointer type argument formats as
		/// <c>delegate* unmanaged&lt;System.Void&gt;</c>. The golden targets have none, so the soundness check adds these.
		/// </summary>
		static readonly Lazy<AspectFilters.MethodTarget[]> FunctionPointerTargets = new(static () =>
		{
			const string source =
				"""
				using System.Collections.Generic;

				namespace Acme.Pointers
				{
					unsafe class Box<T>
					{
						public void Put(T value) { }
						public static void Make<U>() { }
					}

					unsafe static class Calls
					{
						static void Run()
						{
							System.Array.Empty<delegate* unmanaged<void>[]>();
							new List<delegate* unmanaged<void>[]>().Clear();
							new Box<delegate*<int, string>[]>().Put(null!);
							Box<int>.Make<delegate* managed<System.Collections.Generic.List<int>, void>[]>();
						}
					}
				}
				""";

			var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
				"FunctionPointerTargets",
				[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)],
				TargetFilterTargets.Compilation.References,
				new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
			var tree  = compilation.SyntaxTrees[0];
			var model = compilation.GetSemanticModel(tree);

			return tree.GetRoot().DescendantNodes()
				.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
				.Select(i => (Microsoft.CodeAnalysis.IMethodSymbol)model.GetSymbolInfo(i).Symbol!)
				.Select(static m => AspectTargetFactory.Create(m))
				.ToArray();
		});

		/// <summary>Name segments with white space and wildcards in every arrangement, for each key that reads a name.</summary>
		static IEnumerable<string> NamesWithWhiteSpace()
		{
			string[] pieces = ["*", "?", "Empty", "Put", "Clear", "Make", "delegate", "unmanaged", "<", ">", "[]", "Box", "List", "Acme", "\\<", "\\>"];
			string[] keys   = ["method: ", "type: ", "fullmethod: **.", "fulltype: **.", "namespace: ", "Acme.Pointers.Box<*>.", ""];
			var random      = new TargetFilterCorpus.SplitMix64(0x4147_5446_5753_0001UL);

			for (var i = 0; i < 5_000; i++)
			{
				var count   = 2 + random.Next(4);
				var segment = string.Join("", Enumerable.Range(0, count).Select(_ => pieces[random.Next(pieces.Length)] + (random.Next(3) == 0 ? " " : "")));

				yield return keys[random.Next(keys.Length)] + segment.Trim();
			}
		}

		[TestMethod]
		public void FunctionPointerTypeArgumentsAreMatchedWithoutAG0210Test()
		{
			var targets = FunctionPointerTargets.Value;

			Assert.AreEqual(4, targets.Length);

			foreach (var filter in new[] { "method: * *", "type: * *", "method: Empty<delegate* unmanaged<System.Void>[]>", "type: List<delegate* unmanaged<*>[]>", "Put", "fulltype: **.Box<delegate* *>" })
			{
				var reported = false;
				var set      = AspectFilters.GetFilters(filter, d => reported |= d.Id == AspectDiagnosticID.AspectFilterRuleMatchesNothing);

				Assert.IsTrue(targets.Any(t => set.Evaluate(t).IsMatch), $"'{filter}' matches none of {string.Join(" | ", targets.Select(static t => t.Signature))}");
				Assert.IsFalse(reported, $"AG0210 for '{filter}', which matches a method.");
			}
		}

		[TestMethod]
		public void CuratedInputsReportOnlyTheListedRulesTest()
		{
			var reported = new HashSet<string>(StringComparer.Ordinal);

			foreach (var input in TargetFilterCorpus.Curated())
			{
				var diagnostics = new List<AspectFilters.TargetFilterDiagnostic>();
				var set         = AspectFilters.GetFilters(input.Text, diagnostics.Add);

				foreach (var diagnostic in diagnostics.Where(static d => d.Id == AspectDiagnosticID.AspectFilterRuleMatchesNothing))
					reported.Add(set.Rules[diagnostic.RuleIndex].RuleText);
			}

			CollectionAssert.AreEquivalent(CuratedReported, reported.ToArray(), "Curated rules with AG0210: " + string.Join(" | ", reported.Select(TargetFilterProbe.Escape)));
		}

		[TestMethod]
		public void WhiteSpaceThatFiltersMayHoldIsNotReportedTest()
		{
			var reported = TargetFilterSpacedInputs.Generate().Where(static text => Reports(text)).Take(20).ToArray();

			Assert.AreEqual(0, reported.Length, "AG0210 for white space a filter may hold:" + Environment.NewLine + string.Join(Environment.NewLine, reported.Select(TargetFilterProbe.Escape)));
		}

		[TestMethod]
		public void AccessibilityAMethodCanHaveIsNotReportedTest()
		{
			foreach (var words in new[] { "public", "protected", "internal", "private", "protected internal", "internal protected", "private protected", "protected private", "public public", "internal  protected" })
				foreach (var shape in new[] { "{0}", "{0} Save", "{0} static Save(...) : *", "{0}; method: Save", "method: Save; {0}", "pattern: {0} Save" })
				{
					var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, words);

					Assert.IsFalse(Reports(text), $"AG0210 for '{text}'.");
				}
		}

		[TestMethod]
		public void AccessibilityNoMethodCanHaveIsReportedTest()
		{
			foreach (var words in new[] { "public private", "public internal", "public protected", "internal private", "protected internal private", "public protected internal private" })
				foreach (var shape in new[] { "{0}", "{0} Save", "{0} static Save(...) : *", "{0}; method: Save", "pattern: {0} Save", "- {0} Save" })
				{
					var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, words);

					Assert.IsTrue(Reports(text), $"No AG0210 for '{text}'.");
				}

			// The words of one rule add up across the lines of a condition group.
			Assert.IsTrue(Reports("public; method: Save\nprivate"));
			Assert.IsFalse(Reports("public; method: Save\n-private"));
		}

		[TestMethod]
		public void WhiteSpaceInANameIsReportedTest()
		{
			foreach (var text in new[]
			{
				"publik Save", "Acme.Core Service.Save", "Sa ve*", "Save\\ Item", "method: Save Item", "type: My Service", "namespace: Acme Core.**",
				"fullmethod: Acme.Core.Save Item", "path: Acme.Core.My Service", "method: Save Item | Save Items", "method: Save* & Sa ve",
			})
				Assert.IsTrue(Reports(text), $"No AG0210 for '{text}'.");

			foreach (var text in new[]
			{
				"method: Save Item | Save", "Load<string, int>", "* *", "method: * *", "type: * *", "Sa*ve Item", "method: Empty<delegate* unmanaged<System.Void>[]>",
				"method: *unmanaged<System.Void>[]>", "type: List<delegate* unmanaged<System.Void>[]>", "method: Sa?ve Item", @"method: Box\<x y\>", "Acme.Core.Pair< *, * >.Get", "type: Dictionary<string, int>", "public  static  Save",
				"Save(ref readonly int, params string [])", "Save(int ?) : int ?", "returns: string []", "signature: public *", "Save( int , int )",
				"contains: Save Item", "regex: Save Item",
			})
				Assert.IsFalse(Reports(text), $"AG0210 for '{text}'.");
		}

		[TestMethod]
		public void InvalidRuleIsNotReportedAsMatchingNothingTest()
		{
			Assert.IsFalse(Reports("public private Save(..., ...)"));
			Assert.IsFalse(Reports("publik Save.**"));
		}

		[TestMethod]
		public void RuleMatchingNothingIsAWarningAboutItsRuleTest()
		{
			var diagnostics = new List<AspectFilters.TargetFilterDiagnostic>();
			var set         = AspectFilters.GetFilters("method: Save\npublik Load\npublic private", diagnostics.Add);

			CollectionAssert.AreEqual(new[] { "AG0210", "AG0210" }, diagnostics.Select(static d => d.Id).ToArray());
			Assert.IsTrue(diagnostics.All(static d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning));
			CollectionAssert.AreEqual(new[] { 1, 2 }, diagnostics.Select(static d => d.RuleIndex).ToArray());
			Assert.AreEqual("publik Load",    set.Rules[1].RuleText);
			Assert.AreEqual("public private", set.Rules[2].RuleText);
			Assert.AreEqual(
				"Target filter rule 'publik Load' can match no method: the name segment 'publik Load' contains white space outside '<...>', which no namespace, type or method name has",
				diagnostics[0].Message);
			Assert.AreEqual(
				"Target filter rule 'public private' can match no method: no method is public and private at once; a method must have every accessibility word of a rule",
				diagnostics[1].Message);
		}

		static bool Reports(string text)
		{
			var reported = false;

			AspectFilters.GetFilters(text, d => reported |= d.Id == AspectDiagnosticID.AspectFilterRuleMatchesNothing);

			return reported;
		}
	}
}
