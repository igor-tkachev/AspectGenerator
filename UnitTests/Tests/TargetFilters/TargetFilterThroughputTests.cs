using System;
using System.Diagnostics;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// Prints how fast the generated parser reads filters against the hand-written splitter it replaced, over the
	/// generated set, and what one curated filter costs. Nothing is asserted: the figures depend on the machine.
	/// </summary>
	[TestClass]
	public class TargetFilterThroughputTests
	{
		public TestContext TestContext { get; set; } = null!;

		const int Rounds = 3;

		[TestMethod]
		public void ReadersReadTheGeneratedSetTest()
		{
			var inputs = TargetFilterCorpus.Generate(TargetFilterCorpus.GeneratedCount).ToArray();

			// Both readers once over everything first, so that neither pays for compiling its code or its regexes.
			Read(inputs, legacy: true);
			Read(inputs, legacy: false);

			var legacy = double.MaxValue;
			var gram   = double.MaxValue;

			// Alternated, the best of each, so that both see the machine alike.
			for (var round = 0; round < Rounds; round++)
			{
				legacy = Math.Min(legacy, Read(inputs, legacy: true));
				gram   = Math.Min(gram,   Read(inputs, legacy: false));
			}

			var line = $"generated set, {inputs.Length} inputs, best of {Rounds}: splitter {legacy * 1000 / inputs.Length:F1} ms per 1,000, generated parser {gram * 1000 / inputs.Length:F1} ms per 1,000, ratio {gram / legacy:F2}";

			Console.WriteLine(line);
			TestContext.WriteLine(line);
		}

		[TestMethod]
		public void CuratedFilterIsReadInUnderAMillisecondTest()
		{
			var inputs = TargetFilterCorpus.Curated().Select(static i => i.Text).ToArray();

			Read(inputs, legacy: false);

			var worst      = 0.0;
			var worstInput = "";
			var total      = 0.0;

			foreach (var input in inputs)
			{
				var best = double.MaxValue;

				for (var round = 0; round < Rounds; round++)
					best = Math.Min(best, Read([input], legacy: false));

				total += best;

				if (best > worst)
				{
					worst      = best;
					worstInput = input;
				}
			}

			var line = $"curated set, {inputs.Length} filters, best of {Rounds} each: mean {total / inputs.Length:F3} ms, slowest {worst:F3} ms ('{TargetFilterProbe.Escape(worstInput.Length > 60 ? worstInput[..60] + "…" : worstInput)}')";

			Console.WriteLine(line);
			TestContext.WriteLine(line);
		}

		/// <summary>Milliseconds to read every input once, without the filter cache.</summary>
		static double Read(string[] inputs, bool legacy)
		{
			var watch = Stopwatch.StartNew();

			foreach (var input in inputs)
			{
				if (legacy)
					AspectFilters.LegacyTargetFilterFront.GetFilters([input]);
				else
					AspectFilters.ReadFiltersUncached([input]);
			}

			return watch.Elapsed.TotalMilliseconds;
		}
	}
}
