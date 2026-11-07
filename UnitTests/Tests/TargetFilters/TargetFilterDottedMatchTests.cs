using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>How a dotted pattern with <c>**</c> matches the segments of a name, and what it costs.</summary>
	[TestClass]
	public class TargetFilterDottedMatchTests
	{
		public TestContext TestContext { get; set; } = null!;

		static AspectFilters.MethodTarget Target(IEnumerable<string> segments)
		{
			return new AspectFilters.MethodTarget { FullMethodSegments = segments.ToList() };
		}

		/// <summary>The match as it was defined, by trying every number of values for each <c>**</c>.</summary>
		static bool Reference(string[] pattern, List<string> values, int p = 0, int v = 0)
		{
			while (p < pattern.Length)
			{
				if (pattern[p] == "**")
				{
					if (p == pattern.Length - 1)
						return true;

					for (var i = v; i <= values.Count; i++)
						if (Reference(pattern, values, p + 1, i))
							return true;

					return false;
				}

				if (v >= values.Count || !(pattern[p] == "*" || pattern[p] == values[v] || pattern[p].EndsWith('*') && values[v].StartsWith(pattern[p][..^1], StringComparison.Ordinal)))
					return false;

				p++;
				v++;
			}

			return v == values.Count;
		}

		[TestMethod]
		public void RecursiveWildcardMatchesAsByTryingEveryLengthTest()
		{
			// Short and long names, patterns of literal, wildcard, prefix and ** segments.
			string[] pieces = ["**", "**", "*", "a", "b", "a*", "c"];
			var random      = new TargetFilterCorpus.SplitMix64(0x4147_5446_444D_0001UL);
			var checks      = 0;

			for (var round = 0; round < 20_000; round++)
			{
				var length  = random.Next(4) == 0 ? 60 + random.Next(12) : random.Next(8);
				var values  = Enumerable.Range(0, length).Select(_ => new[] { "a", "b", "ab", "c" }[random.Next(4)]).ToList();
				var pattern = Enumerable.Range(0, 1 + random.Next(7)).Select(_ => pieces[random.Next(pieces.Length)]).ToArray();

				// A dotted pattern cannot end in a recursive wildcard that is not alone; the language allows a final ** in fullmethod.
				var filter   = "fullmethod: " + string.Join(".", pattern);
				var set      = AspectFilters.GetFilters(filter);
				var expected = Reference(pattern, values);

				Assert.AreEqual(1, set.Rules.Count, filter);
				Assert.AreEqual(expected, set.Evaluate(Target(values)).IsMatch, $"{filter} against {string.Join(".", values)}");
				checks++;
			}

			TestContext.WriteLine($"checks: {checks}");
		}

		[TestMethod]
		public void ManyRecursiveWildcardsMatchInPolynomialTimeTest()
		{
			// Each ** used to try every number of values, again for every ** after it: forty of them against a name of nine
			// segments took tens of seconds per call. The bound is generous, for slow machines.
			var name = Target(["A", "B", "C", "D", "E", "F", "G", "H", "Save"]);

			foreach (var count in new[] { 12, 20, 40, 200 })
			{
				var prefix = "fullmethod: " + string.Concat(Enumerable.Repeat("**.", count));

				AssertTime(prefix + "Nope", name, expected: false);
				AssertTime(prefix + "Save", name, expected: true);
			}
		}

		[TestMethod]
		public void MatchComparesOnlyTheSegmentsItNeedsTest()
		{
			// The first way that matches is the answer, so a name that matches early is not compared further: here each of
			// the eight later segments would be a regular expression run of 200 ms that times out.
			var segments = new[] { new string('a', 10) + "b" }.Concat(Enumerable.Repeat(new string('a', 32), 8));

			AssertTime("fullmethod: **." + string.Concat(Enumerable.Repeat("a*", 10)) + "b.**", Target(segments), expected: true);

			// A long name of literal segments and a final **: one comparison a segment.
			var literal = Enumerable.Range(0, 16_000).Select(static i => "S" + i).ToArray();

			AssertTime("fullmethod: " + string.Join(".", literal.Take(15_000)) + ".**", Target(literal), expected: true);
			AssertTime("fullmethod: " + string.Join(".", literal.Take(15_000)) + ".Nope.**", Target(literal), expected: false);
		}

		void AssertTime(string filter, AspectFilters.MethodTarget target, bool expected)
		{
			// The filter must compile to one rule, or the time measured is of nothing.
			var diagnostics = new List<AspectFilters.TargetFilterDiagnostic>();
			var set         = AspectFilters.GetFilters(filter, diagnostics.Add);

			Assert.AreEqual(1, set.Rules.Count, filter[..Math.Min(60, filter.Length)]);
			Assert.AreEqual(0, diagnostics.Count, string.Join(", ", diagnostics.Select(static d => d.Message)));

			var watch = Stopwatch.StartNew();

			Assert.AreEqual(expected, set.Evaluate(target).IsMatch, filter[..Math.Min(60, filter.Length)]);

			var line = $"{filter[..Math.Min(40, filter.Length)]}… ({filter.Length} characters) against {target.FullMethodSegments.Count} segments: {watch.Elapsed.TotalMilliseconds:F2} ms";

			TestContext.WriteLine(line);
			Assert.IsTrue(watch.Elapsed.TotalMilliseconds < 500, line);
		}
	}
}
