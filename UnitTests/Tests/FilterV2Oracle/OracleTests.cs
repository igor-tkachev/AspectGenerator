using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.FilterV2Oracle
{
	/// <summary>
	/// A second, deliberately naive reading of what one filter selects, compared with the product on every (filter,
	/// target) pair: the inputs of the grammar's corpus, a seeded set of generated filters, and the generic calls of
	/// the reference text. Both read the filter as an assembly attribute of an aspect with no default filter.
	/// </summary>
	[TestClass]
	public class OracleTests
	{
		public TestContext TestContext { get; set; } = null!;

		/// <summary>The size of the generated set; <c>AG_ORACLE_FILTERS</c> raises it for a long run.</summary>
		static int GeneratedCount
		{
			get
			{
				return int.TryParse(Environment.GetEnvironmentVariable("AG_ORACLE_FILTERS"), out var n) && n > 0 ? n : 5_000;
			}
		}

		const ulong Seed = 0x4F5241434C450001UL;

		[TestMethod]
		public void FixtureRendersTheFrozenHeadersTest()
		{
			var expected = OracleTargets.HeaderLines().ToList();

			Assert.AreEqual(236, expected.Count, "The framework line and 235 targets.");

			foreach (var name in FrozenBaseline.FileNames)
				CollectionAssert.AreEqual(FrozenBaseline.Read(name).Targets.ToList(), expected, name + ".txt: the copy of the targets drifted from the frozen baseline.");
		}

		[TestMethod]
		public void FrozenBaselinesReadTest()
		{
			var targets = OracleTargets.Entries.Length;

			foreach (var name in FrozenBaseline.FileNames)
			{
				var file = FrozenBaseline.Read(name);

				foreach (var record in file.Records)
					Assert.AreEqual(targets, record.Decisions.Length, $"{name}.txt:{record.Line}: one decision per target.");

				TestContext.WriteLine($"{name}: {file.Records.Count} records, {file.Hashes.Count} hashed blocks, header: {string.Join(" | ", file.Header.Skip(1))}");
			}

			Assert.AreEqual(542,  FrozenBaseline.Read("curated").Records.Count);
			Assert.AreEqual(710,  FrozenBaseline.Read("escapes").Records.Count);
			Assert.AreEqual(5389, FrozenBaseline.Read("mutations").Records.Count);
			Assert.AreEqual(50,   FrozenBaseline.Read("generated").Hashes.Count);
			Assert.AreEqual(5,    FrozenBaseline.Read("spaced").Hashes.Count);
		}

		/// <summary>The inputs of <c>Baselines/FilterV2/corpus.txt</c>, unescaped.</summary>
		public static IEnumerable<string> CorpusInputs()
		{
			foreach (var line in File.ReadLines(Path.Combine(FrozenBaseline.RepositoryRoot(), "Baselines", "FilterV2", "corpus.txt")))
			{
				if (!line.StartsWith('['))
					continue;

				var close = line.IndexOf("] ", StringComparison.Ordinal);

				yield return FrozenBaseline.Unescape(line[(close + 2)..]);
			}
		}

		[TestMethod]
		public void CorpusTest()
		{
			var inputs = CorpusInputs().Distinct().ToList();

			Assert.IsTrue(inputs.Count > 200, $"The corpus is unexpectedly small: {inputs.Count} inputs.");

			var report = Compare(inputs.Select(static i => (i, (OracleGenerator.Filter?)null)), shrink: false);

			TestContext.WriteLine(report.Summary);
			Assert.AreEqual(0, report.Mismatches.Count, report.Text);
		}

		[TestMethod]
		public void GeneratedTest()
		{
			var generator = new OracleGenerator(Seed);
			var filters   = Enumerable.Range(0, GeneratedCount).Select(_ => generator.Next()).ToList();
			var report    = Compare(filters.Select(static f => (f.Render(), (OracleGenerator.Filter?)f)), shrink: true);

			TestContext.WriteLine(report.Summary);
			Assert.AreEqual(0, report.Mismatches.Count, report.Text);
		}

		/// <summary>Inputs at the edges of the rules, each decided alike by both readers.</summary>
		[TestMethod]
		[DataRow("params: (int) and origin: any")]
		[DataRow("public and method: any")]
		[DataRow("method: any and public")]
		[DataRow("(method: any)")]
		[DataRow("- aspect.method")]
		[DataRow("method: Save* and not aspect.method")]
		[DataRow("method: Save* or aspect.method")]
		[DataRow("aspect.method or public")]
		[DataRow("not Save*")]
		[DataRow("not Save*()")]
		[DataRow("not public Save*")]
		[DataRow("method: Save*\nmethod: Get*")]
		[DataRow("method: Save* or public\nmethod: Get*")]
		[DataRow("method: Save* and (method: *A or public)")]
		[DataRow("method: Save* and public\n- public")]
		[DataRow("- method: X or public\n- method: Y")]
		[DataRow("not method: Save")]
		[DataRow("- project")]
		[DataRow("not project")]
		[DataRow("")]
		[DataRow("# nothing")]
		[DataRow("public")]
		[DataRow("origin: any")]
		[DataRow("origin: any\npublic")]
		[DataRow("returns: string[]?[,]")]
		[DataRow("returns: string[,][]")]
		[DataRow("param: string?[]")]
		[DataRow("param: *?[]")]
		[DataRow("params: (ref *, ref *)")]
		[DataRow("params: (this ref int)")]
		[DataRow("param: this ref int")]
		[DataRow("param: ref this int")]
		[DataRow("method: int.Parse")]
		[DataRow("method: global::System.Int32.Parse")]
		[DataRow("type: int")]
		[DataRow("type: Int32")]
		[DataRow("attribute: NoLogAttribute")]
		[DataRow("attribute: MarkerAttribute")]
		[DataRow("attribute: Marker")]
		[DataRow("attribute: GenericMarker<int>")]
		[DataRow("attribute: GenericMarkerAttribute<*>")]
		[DataRow("implements: IJob.Run")]
		[DataRow("implements: Base.Virtual")]
		[DataRow("implements: Object.ToString")]
		[DataRow("implements: Derived.Abstract")]
		[DataRow("implements: Acme.Core.Service.Save")]
		[DataRow("inherits: Object")]
		[DataRow("returns: dynamic")]
		[DataRow("returns: \"System.Void*\"")]
		[DataRow("param: \"System.Int32*\"")]
		[DataRow("param: \"Int32*\"")]
		[DataRow("returns: \"Int32\"")]
		[DataRow("method: \"Save\"")]
		[DataRow("type: Outer<int>.Inner<string>")]
		[DataRow("method: Outer<*>.Inner<*>.Run<*>")]
		[DataRow("method: Run<*>")]
		[DataRow("method: Run<>")]
		[DataRow("namespace: \"\"")]
		[DataRow("namespace: {\"\",Acme.**}")]
		[DataRow("namespace: Acme.Core.*")]
		[DataRow("method: {global::Acme.Core.Service.Save,Other.Save}")]
		[DataRow("method: Save{global::X}")]
		[DataRow("returns: {void,Task}?")]
		[DataRow("returns: Task<*>?")]
		[DataRow("param: Nullable<*>")]
		[DataRow("param: System.Nullable<int>")]
		[DataRow("returns: (int Id, string Name)")]
		[DataRow("returns: ValueTuple<int, string>")]
		[DataRow("returns: (int, string)?")]
		[DataRow("params: (.., ..)")]
		[DataRow("params: (_, ..)")]
		[DataRow("params: (..)")]
		[DataRow("param: _")]
		[DataRow("public static async")]
		[DataRow("public static extension")]
		[DataRow("generated")]
		[DataRow("generated\norigin: any")]
		[DataRow("extern\norigin: any")]
		[DataRow("async\norigin: any")]
		[DataRow("virtual\norigin: any")]
		[DataRow("sealed\norigin: any")]
		[DataRow("abstract\norigin: any")]
		[DataRow("override\norigin: any")]
		[DataRow("protected internal\norigin: any")]
		[DataRow("protected\norigin: any")]
		[DataRow("public *")]
		[DataRow("public Save*")]
		[DataRow("Run")]
		public void EdgeInputTest(string escaped)
		{
			var filter = FrozenBaseline.Unescape(escaped);

			var difference = Difference(filter);

			Assert.IsNull(difference, filter + ": " + difference);
		}

		/// <summary>
		/// A word glued to a wildcard, standing where a compact signature may stand. In a value (<c>method: public*</c>)
		/// both readers take <c>public*</c> as one name pattern; standing alone, the product reads <c>sealed*</c> as the
		/// word <c>sealed</c> followed by the signature <c>*</c>, so it selects every sealed method. The reference text
		/// allows white space between tokens but none inside a pattern, and says that a keyword used as a name needs
		/// <c>@</c> only when the name equals the keyword: read literally, <c>sealed*</c> is one pattern, a method whose
		/// name begins with "sealed". Pinned with the oracle's reading until the text decides.
		/// </summary>
		[TestMethod]
		[Ignore("Open: the reference text does not say whether a word glued to a wildcard is one pattern; the product reads 'sealed*' as 'sealed *'.")]
		[DataRow("sealed*")]
		[DataRow("public*")]
		[DataRow("static*(..)")]
		public void WordGluedToAWildcardIsOnePatternTest(string filter)
		{
			var difference = Difference(filter);

			Assert.IsNull(difference, filter + ": " + difference);
		}

		sealed class Report
		{
			public readonly List<string> Mismatches = [];
			public readonly StringBuilder Details   = new();

			public string Summary = "";

			public string Text => Summary + Environment.NewLine + Details;
		}

		/// <summary>What the two readers say differently about one filter: its error, or the targets they decide apart.</summary>
		static string? Difference(string filter)
		{
			var oracle  = OracleFilter.Assembly(filter);
			var product = new ProductBlock(filter);

			if ((oracle.Error is not null) != product.HasErrors)
			{
				return oracle.Error is not null
					? $"the oracle finds an error ({oracle.Error}), the product none"
					: $"the product finds an error ({string.Join("; ", product.Block.Diagnostics)}), the oracle none";
			}

			if (oracle.Error is not null)
				return null;

			var apart = new List<string>();

			foreach (var target in OracleCases.All)
			{
				var o = oracle.Selects(target.Oracle);
				var p = product.Selects(target);

				if (o != p)
					apart.Add($"{target.Label}: oracle {o}, product {p}");
			}

			return apart.Count == 0 ? null : $"{apart.Count} target(s) apart: " + string.Join("; ", apart.Take(6)) + (apart.Count > 6 ? "; …" : "");
		}

		static Report Compare(IEnumerable<(string Text, OracleGenerator.Filter? Tree)> filters, bool shrink)
		{
			var report    = new Report();
			var watch     = Stopwatch.StartNew();
			var count     = 0;
			var errors    = 0;
			var selecting = 0;
			var shrunk    = new HashSet<string>(StringComparer.Ordinal);
			var targets   = OracleCases.All.ToList();

			foreach (var (text, tree) in filters)
			{
				count++;

				var difference = Difference(text);

				if (OracleFilter.Assembly(text) is { Error: null } read)
				{
					if (targets.Any(t => read.Selects(t.Oracle) == true))
						selecting++;
				}
				else
				{
					errors++;
				}

				if (difference is null)
					continue;

				report.Mismatches.Add(text);

				var smallest = text;

				if (shrink && tree is not null)
				{
					var current = tree;

					for (var changed = true; changed;)
					{
						changed = false;

						foreach (var candidate in current.Shrink())
						{
							if (Difference(candidate.Render()) is not null)
							{
								current = candidate;
								changed = true;
								break;
							}
						}
					}

					smallest = current.Render();
				}

				if (shrunk.Add(smallest) && shrunk.Count <= 40)
					report.Details.AppendLine($"- '{FrozenBaseline.Escape(smallest)}' (from '{FrozenBaseline.Escape(text)}'): {Difference(smallest)}");
			}

			report.Summary = $"{count} filters ({errors} with an error, {selecting} selecting something) x {targets.Count} targets: {report.Mismatches.Count} filters apart, {shrunk.Count} distinct after shrinking, {watch.Elapsed.TotalSeconds:0.0} s.";

			return report;
		}

		// ---------------------------------------------------------------------------------------------------- generics

		/// <summary>
		/// The cases of the reference text's "Generic Methods", each with the answer the text gives, decided by both
		/// readers: a condition describes the call with its type arguments; the caller's own type parameter matches only
		/// <c>*</c>; an extension method is judged in its static form whichever way it is called.
		/// </summary>
		[TestMethod]
		[DataRow("method: List<int>.Add",                       "li.Add(1)",                    true)]
		[DataRow("method: List<int>.Add",                       "ls.Add(\"\")",                 false)]
		[DataRow("method: List<int>.Add",                       "lt.Add(value)",                false)]
		[DataRow("method: List<*>.Add",                         "lt.Add(value)",                true)]
		[DataRow("method: List<string>.Add",                    "ls.Add(\"\")",                 true)]
		[DataRow("method: List.Add",                            "lt.Add(value)",                true)]
		[DataRow("params: (int)\norigin: any",                  "li.Add(1)",                    true)]
		[DataRow("params: (int)\norigin: any",                  "lt.Add(value)",                false)]
		[DataRow("params: (*)\norigin: any",                    "lt.Add(value)",                true)]
		[DataRow("method: Box<int>.Put",                        "bi.Put(1)",                    true)]
		[DataRow("method: Box<int>.Put",                        "bt.Put(value)",                false)]
		[DataRow("method: Box<*>.Put",                          "bt.Put(value)",                true)]
		[DataRow("param: int",                                  "bi.Put(1)",                    true)]
		[DataRow("param: int",                                  "bt.Put(value)",                false)]
		[DataRow("method: Take<string>",                        "bi.Take<string>(\"\")",        true)]
		[DataRow("method: Take<string>",                        "bi.Take(value)",               false)]
		[DataRow("method: Take<*>",                             "bi.Take(value)",               true)]
		[DataRow("method: Enumerable.Any<int>",                 "numbers.Any()",                true)]
		[DataRow("method: Enumerable.Any<int>",                 "Enumerable.Any<int>(numbers)", true)]
		[DataRow("params: (this IEnumerable<int>)\norigin: any",  "numbers.Any()",            true)]
		[DataRow("method: Has<int>",                            "numbers.Has()",                true)]
		[DataRow("method: Has<int>",                            "Ext.Has<int>(numbers)",        true)]
		[DataRow("method: Has<int>",                            "lt.Has()",                     false)]
		[DataRow("params: (this IEnumerable<*>)",               "lt.Has()",                     true)]
		[DataRow("async",                                       "Make<Task<int>>()",            true)]
		[DataRow("async",                                       "Make<int>()",                  false)]
		[DataRow("async",                                       "Make<TCaller>()",              false)]
		[DataRow("async",                                       "Make<Awaitable>()",            true)]
		[DataRow("returns: Task<int>",                          "Make<Task<int>>()",            true)]
		[DataRow("returns: int",                                "bi.Get()",                     true)]
		[DataRow("returns: int",                                "bt.Get()",                     false)]
		[DataRow("returns: *",                                  "bt.Get()",                     true)]
		[DataRow("returns: *?",                                 "bt.Get()",                     false)]
		public void GenericMethodsTest(string filter, string call, bool expected)
		{
			var target  = OracleCases.Call(call);
			var oracle  = OracleFilter.Assembly(filter);
			var product = new ProductBlock(filter);

			Assert.IsNull(oracle.Error, oracle.Error);
			Assert.IsFalse(product.HasErrors, string.Join("; ", product.Block.Diagnostics));
			Assert.AreEqual(expected, oracle.Selects(target.Oracle), "oracle");
			Assert.AreEqual(expected, product.Selects(target), "product");
		}

		[TestMethod]
		public void BothFormsOfAnExtensionCallAreOneTargetTest()
		{
			var generator = new OracleGenerator(Seed + 1);

			foreach (var filter in Enumerable.Range(0, 500).Select(_ => generator.Next().Render()).Append("method: Any<int>").Append("method: Has<*>"))
			{
				var product = new ProductBlock(filter);

				Assert.AreEqual(product.Selects(OracleCases.Call("numbers.Any()")), product.Selects(OracleCases.Call("Enumerable.Any<int>(numbers)")), filter);
				Assert.AreEqual(product.Selects(OracleCases.Call("numbers.Has()")), product.Selects(OracleCases.Call("Ext.Has<int>(numbers)")), filter);
			}
		}
	}
}
