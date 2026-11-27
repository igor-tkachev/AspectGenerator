using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.FilterV2Oracle
{
	/// <summary>
	/// The frozen 1.1 decisions against version 2's, record by record (<see cref="V1V2Differential"/>). The counts per
	/// class and every record of the classes the migration table does not explain are pinned in
	/// <c>differential-summary.txt</c> next to this file (<c>AG_UPDATE_BASELINES=1</c> rewrites it): a change in how
	/// version 2 decides any of them shows here as a changed summary. The differences themselves do not fail the build;
	/// the summary lists them for review.
	/// </summary>
	[TestClass]
	public class V1V2DifferentialTests
	{
		public TestContext TestContext { get; set; } = null!;

		static readonly string[] _files = ["curated", "escapes", "mutations"];

		static string SummaryPath => Path.Combine(FrozenBaseline.RepositoryRoot(), "UnitTests", "Tests", "FilterV2Oracle", "differential-summary.txt");

		[TestMethod]
		public void DifferentialSummaryTest()
		{
			var watch        = Stopwatch.StartNew();
			var differential = new V1V2Differential();
			var results      = _files.SelectMany(static f => FrozenBaseline.Read(f).Records).Select(differential.Classify).ToList();
			var lines        = Summary(results);

			TestContext.WriteLine($"{results.Count} records classified in {watch.Elapsed.TotalSeconds:0.0} s.");

			foreach (var line in lines.TakeWhile(static l => !l.StartsWith("# records", StringComparison.Ordinal)))
				TestContext.WriteLine(line);

			if (Environment.GetEnvironmentVariable("AG_UPDATE_BASELINES") == "1")
			{
				File.WriteAllText(SummaryPath, string.Join("\n", lines) + "\n");
				return;
			}

			Assert.IsTrue(File.Exists(SummaryPath), "differential-summary.txt does not exist; run the tests with AG_UPDATE_BASELINES=1 to create it.");

			var expected = File.ReadAllLines(SummaryPath).Select(static l => l.TrimEnd('\r')).ToList();
			var missing  = expected.Except(lines).Take(10).ToList();
			var extra    = lines.Except(expected).Take(10).ToList();

			Assert.IsTrue(missing.Count == 0 && extra.Count == 0,
				"The differential changed (set AG_UPDATE_BASELINES=1 to accept, and explain every changed record):" + Environment.NewLine
				+ string.Join(Environment.NewLine, missing.Select(static l => "- " + l).Concat(extra.Select(static l => "+ " + l))));
		}

		static List<string> Summary(List<V1V2Differential.Result> results)
		{
			var lines = new List<string>
			{
				"# The 1.1 to version 2 differential over the frozen 1.1 baselines (curated, escapes, mutations), written by",
				"# V1V2DifferentialTests; AG_UPDATE_BASELINES=1 rewrites it. Every record is rewritten into version 2 as the",
				"# migration table of the reference text says and decided as an assembly filter of an aspect without a default",
				"# filter; its class compares the 1.1 decision with version 2's on every target of the fixture.",
				"",
				$"records\t{results.Count}",
			};

			foreach (var group in results.GroupBy(static r => r.Class).OrderByDescending(static g => g.Count()).ThenBy(static g => g.Key, StringComparer.Ordinal))
				lines.Add($"class\t{group.Count()}\t{group.Key}");

			foreach (var group in results.Where(static r => r.Class != V1V2Differential.Same && r.Class != V1V2Differential.NotExpressible && r.Class != V1V2Differential.NoRules && !r.Class.StartsWith("predicted", StringComparison.Ordinal)).GroupBy(static r => r.Class).OrderBy(static g => g.Key, StringComparer.Ordinal))
			{
				lines.Add("");
				lines.Add($"# records {group.Key}: file:line TAB 1.1 input TAB version 2 rewrite TAB detail");

				foreach (var r in group)
					lines.Add($"{r.Record.File}:{r.Record.Line}\t{FrozenBaseline.Escape(r.Record.Input)}\t{FrozenBaseline.Escape(r.Rewrite ?? "")}\t{r.Detail}");
			}

			return lines;
		}
	}
}
