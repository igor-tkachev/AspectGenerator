using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// Pins what the target-filter parser does today: a fixed list of targets, and for every input of the curated,
	/// mutated and generated sets the diagnostic ids, the plan and the decision for every target.
	/// </summary>
	[TestClass]
	public class TargetFilterGoldenTests
	{
		public TestContext TestContext { get; set; } = null!;

		const int GeneratedChunk = 1_000;

		[TestMethod]
		public void TargetListHoldsEveryRequiredFormTest()
		{
			var entries = TargetFilterTargets.Entries;

			Assert.IsTrue(entries.Length >= 200, $"Expected at least 200 targets, found {entries.Length}.");

			var missing = TargetFilterTargets.Forms.Where(f => !entries.Any(e => f.Test(e.Symbol))).Select(static f => f.Form).ToArray();

			Assert.AreEqual(0, missing.Length, "Forms without a target: " + string.Join(", ", missing));

			foreach (var (form, test) in TargetFilterTargets.Forms)
				TestContext.WriteLine($"{form}: {entries.Count(e => test(e.Symbol))}");

			foreach (var group in entries.GroupBy(static e => e.Label))
				TestContext.WriteLine($"targets {group.Key}: {group.Count()}");

			TestContext.WriteLine($"targets total: {entries.Length}");
		}

		[TestMethod]
		public void TargetListHasNoDuplicateSignaturesTest()
		{
			var duplicates = TargetFilterTargets.Entries
				.GroupBy(static e => e.Target.Signature, StringComparer.Ordinal)
				.Where(static g => g.Count() > 1)
				.Select(static g => g.Key)
				.ToArray();

			Assert.AreEqual(0, duplicates.Length, "Duplicate canonical signatures: " + string.Join(" ; ", duplicates));
		}

		[TestMethod]
		public void CuratedListCoversTheDocumentationTest()
		{
			var listed  = TargetFilterCuratedInputs.ReadList().Select(static i => i.Text).ToHashSet(StringComparer.Ordinal);
			var missing = TargetFilterCuratedInputs.ExtractDocumentation()
				.Where(i => !listed.Contains(i.Text))
				.Select(static i => i.Class + ": " + TargetFilterProbe.Escape(i.Text))
				.Distinct()
				.ToArray();

			Assert.AreEqual(
				0,
				missing.Length,
				"Filter strings in the documentation are missing from the curated list (run the tests with AG_UPDATE_BASELINES=1 to re-extract it):"
				+ Environment.NewLine + string.Join(Environment.NewLine, missing));
		}

		[TestMethod]
		public void CuratedBaselineTest()
		{
			var curated = TargetFilterCorpus.Curated();
			var lines   = Header("curated", $"inputs {curated.Count}");

			lines.AddRange(curated.Select(static i => TargetFilterProbe.FormatRecord(i.Text)));

			TargetFilterBaseline.Check("curated.txt", lines);

			foreach (var group in curated.GroupBy(static i => i.Class))
				TestContext.WriteLine($"curated {group.Key}: {group.Count()}");

			TestContext.WriteLine($"curated total: {curated.Count}");
		}

		[TestMethod]
		public void MutationBaselineTest()
		{
			var curated   = TargetFilterCorpus.Curated().Select(static i => i.Text).ToArray();
			var mutations = TargetFilterCorpus.Mutations(curated, TargetFilterCorpus.MutationsPerInput, TargetFilterCorpus.MutationSeed);
			var lines     = Header("mutations", $"seed {TargetFilterCorpus.MutationSeed:x16}", $"per input {TargetFilterCorpus.MutationsPerInput}", $"count {mutations.Count}", $"over {curated.Length} curated inputs");

			lines.AddRange(mutations.Select(static m => TargetFilterProbe.FormatRecord(m)));

			TargetFilterBaseline.Check("mutations.txt", lines);
		}

		[TestMethod]
		public void MutationsAreDeterministicAndLeaveRegexBodiesAloneTest()
		{
			var curated = TargetFilterCorpus.Curated().Select(static i => i.Text).ToArray();
			var first   = TargetFilterCorpus.MutationsWithSources(curated, TargetFilterCorpus.MutationsPerInput, TargetFilterCorpus.MutationSeed);
			var second  = TargetFilterCorpus.MutationsWithSources(curated, TargetFilterCorpus.MutationsPerInput, TargetFilterCorpus.MutationSeed);

			Assert.IsTrue(first.Count > curated.Length * 5, "The mutation sample is unexpectedly small.");
			CollectionAssert.AreEqual(first.Select(static m => m.Text).ToArray(), second.Select(static m => m.Text).ToArray());
			Assert.AreEqual(first.Count, first.Select(static m => m.Text).Distinct(StringComparer.Ordinal).Count(), "Mutations repeat.");

			var regexSources = 0;

			foreach (var (source, text) in first)
				foreach (var body in TargetFilterCorpus.RegexBodiesOf(curated[source]))
				{
					regexSources++;
					Assert.IsTrue(text.Contains(body, StringComparison.Ordinal), $"A mutation changed a regex body: {TargetFilterProbe.Escape(curated[source])} -> {TargetFilterProbe.Escape(text)}");
				}

			TestContext.WriteLine($"mutations of inputs with a regex: rule: {regexSources}");
		}

		[TestMethod]
		public void MutationsOfAnInputDoNotDependOnOtherInputsTest()
		{
			var curated  = TargetFilterCorpus.Curated().Select(static i => i.Text).ToArray();
			var baseline = TargetFilterCorpus.Mutations(curated, TargetFilterCorpus.MutationsPerInput, TargetFilterCorpus.MutationSeed).ToHashSet(StringComparer.Ordinal);

			var added    = curated.Prepend("dummy: Added; entry").Append("Another.Dummy(...)").ToArray();
			var withMore = TargetFilterCorpus.Mutations(added, TargetFilterCorpus.MutationsPerInput, TargetFilterCorpus.MutationSeed).ToHashSet(StringComparer.Ordinal);

			Assert.IsTrue(baseline.IsSubsetOf(withMore), "Adding curated inputs changed the mutations of the others.");
			Assert.IsTrue(withMore.Count > baseline.Count, "The added inputs produced no mutations.");

			var reversed = TargetFilterCorpus.Mutations(curated.Reverse().ToArray(), TargetFilterCorpus.MutationsPerInput, TargetFilterCorpus.MutationSeed).ToHashSet(StringComparer.Ordinal);

			Assert.IsTrue(baseline.SetEquals(reversed), "Reordering the curated inputs changed the mutations.");
		}

		[TestMethod]
		public void GeneratedBaselineTest()
		{
			var inputs = TargetFilterCorpus.Generate(TargetFilterCorpus.GeneratedCount).ToArray();
			var lines  = Header("generated", $"seed {TargetFilterCorpus.GeneratedSeed:x16}", $"count {inputs.Length}", $"chunk {GeneratedChunk}");

			for (var start = 0; start < inputs.Length; start += GeneratedChunk)
			{
				var records = inputs.Skip(start).Take(GeneratedChunk).Select(static i => TargetFilterProbe.FormatRecord(i));

				lines.Add($"records {start}..{Math.Min(start + GeneratedChunk, inputs.Length) - 1}\t{TargetFilterCorpus.Sha256(records)}");
			}

			TargetFilterBaseline.Check("generated.txt", lines);
		}

		[TestMethod]
		public void GeneratedInputsAreDeterministicTest()
		{
			var first  = TargetFilterCorpus.Generate(5_000).ToArray();
			var second = TargetFilterCorpus.Generate(5_000).ToArray();

			CollectionAssert.AreEqual(first, second);
			Assert.IsTrue(first.Distinct(StringComparer.Ordinal).Count() > 4_000, "The generator repeats itself too often.");

			// A prefix of a longer run is the same run.
			CollectionAssert.AreEqual(first.Take(1_000).ToArray(), TargetFilterCorpus.Generate(1_000).ToArray());

			// A different seed gives different inputs.
			Assert.AreNotEqual(first[0] + first[1] + first[2], string.Concat(TargetFilterCorpus.Generate(3, seed: 1)));
		}

		[TestMethod]
		public void GeneratedSetCoversEveryDiagnosticAndPlanShapeTest()
		{
			var ids     = new HashSet<string>(StringComparer.Ordinal);
			var classes = new HashSet<string>(StringComparer.Ordinal);
			var kinds   = new HashSet<char>();
			var decided = 0;

			foreach (var input in TargetFilterCorpus.Generate(GeneratedChunk))
			{
				var outcome = TargetFilterProbe.Probe(input);

				if (outcome.Kinds.Any(static k => k != 'n'))
					decided++;

				ids.UnionWith(outcome.Ids);
				kinds.UnionWith(outcome.Kinds);

				if (outcome.Plan is { } plan)
					classes.Add(plan.Classification);
			}

			TestContext.WriteLine($"generated inputs that decide something: {decided} of {GeneratedChunk}");
			Assert.IsTrue(decided >= GeneratedChunk / 10, $"Only {decided} of {GeneratedChunk} generated inputs select or exclude any target.");

			foreach (var id in new[] { "AG0201", "AG0202", "AG0204", "AG0205", "AG0206" })
				Assert.IsTrue(ids.Contains(id), $"The first {GeneratedChunk} generated inputs report no {id}.");

			CollectionAssert.AreEquivalent(new[] { "signature-text", "target-pattern" }, classes.ToArray());
			CollectionAssert.AreEquivalent(new[] { 'n', 'i', 'e' }, kinds.ToArray());
		}

		/// <summary>Prints how fast the parser runs over the generated set, and the evaluation over a sample. Nothing is asserted.</summary>
		[TestMethod]
		public void GeneratedSetThroughputTest()
		{
			var inputs = TargetFilterCorpus.Generate(TargetFilterCorpus.GeneratedCount).ToArray();
			var sample = inputs.Take(5_000).ToArray();

			_ = TargetFilterTargets.Entries;

			var parse = Stopwatch.StartNew();

			foreach (var input in inputs)
			{
				try
				{
					AspectFilters.GetFilters(input);
				}
				catch (NullReferenceException)
				{
					// A known parser defect (an empty dotted segment); the time is counted all the same.
				}
			}

			parse.Stop();

			var full = Stopwatch.StartNew();

			foreach (var input in sample)
				TargetFilterProbe.Probe(input);

			full.Stop();

			var parseRate = parse.Elapsed.TotalMilliseconds * 1000 / inputs.Length;
			var fullRate  = full.Elapsed.TotalMilliseconds * 1000 / sample.Length;

			Console.WriteLine($"throughput: parse {parseRate:F1} ms per 1,000 inputs ({inputs.Length} generated inputs); parse and evaluate against {TargetFilterTargets.Entries.Length} targets {fullRate:F1} ms per 1,000 inputs ({sample.Length} inputs)");
			TestContext.WriteLine($"parse only: {parseRate:F1} ms per 1,000 inputs ({inputs.Length} generated inputs)");
			TestContext.WriteLine($"parse and evaluate against {TargetFilterTargets.Entries.Length} targets: {fullRate:F1} ms per 1,000 inputs ({sample.Length} inputs)");
		}

		static List<string> Header(string kind, params string[] details)
		{
			var lines = new List<string>
			{
				"# target-filter golden baseline: " + kind,
				"# record: input TAB diagnostic ids TAB plan TAB decision per target (n none, i include, e exclude) TAB matched rule index per matched target",
			};

			lines.AddRange(details.Select(static d => "# " + d));
			lines.AddRange(TargetFilterTargets.HeaderLines());

			return lines;
		}
	}
}
