using System;
using System.Diagnostics;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// Prints how fast the parser reads the generated set and what one curated filter costs. Nothing is asserted but a
	/// generous bound on inputs that must read in linear time: the figures depend on the machine.
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

			// Once over everything first, so that the timed rounds do not pay for compiling the code or the regexes.
			Read(inputs);

			var best = double.MaxValue;

			for (var round = 0; round < Rounds; round++)
				best = Math.Min(best, Read(inputs));

			var line = $"generated set, {inputs.Length} inputs, best of {Rounds}: {best * 1000 / inputs.Length:F1} ms per 1,000";

			Console.WriteLine(line);
			TestContext.WriteLine(line);
		}

		[TestMethod]
		public void CuratedFilterIsReadInUnderAMillisecondTest()
		{
			var inputs = TargetFilterCorpus.Curated().Select(static i => i.Text).ToArray();

			Read(inputs);

			var worst      = 0.0;
			var worstInput = "";
			var total      = 0.0;

			foreach (var input in inputs)
			{
				var best = double.MaxValue;

				for (var round = 0; round < Rounds; round++)
					best = Math.Min(best, Read([input]));

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

		[TestMethod]
		public void ManyOperatorsInsideBracketsAreReadInLinearTimeTest()
		{
			// Operators inside brackets are no operators, and finding that out must not cost more for each one: 128,000 of
			// them read in well under a second. The bound is generous, for slow machines.
			foreach (var input in new[]
			{
				"method: A<" + new string('|', 128_000) + ">",
				"method: A(" + new string('&', 128_000) + ")",
				"params: (" + string.Concat(Enumerable.Repeat("int|", 32_000)) + ")",
			})
			{
				var gram = Read([input]);
				var line = $"{input[..12]}… ({input.Length} characters): {gram:F1} ms";

				Console.WriteLine(line);
				TestContext.WriteLine(line);
				Assert.IsTrue(gram < 2_000, line);
			}
		}

		/// <summary>Milliseconds to read every input once, without the filter cache.</summary>
		static double Read(string[] inputs)
		{
			var watch = Stopwatch.StartNew();

			foreach (var input in inputs)
				AspectFilters.ReadFiltersUncached([input]);

			return watch.Elapsed.TotalMilliseconds;
		}
	}
}
