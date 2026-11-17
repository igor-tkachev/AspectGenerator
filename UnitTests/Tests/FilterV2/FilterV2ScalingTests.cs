using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

using AspectGenerator.FilterV2;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.FilterV2
{
	/// <summary>
	/// The reading of a filter grows linearly with its text: within twice per ten times the input, on malformed text,
	/// long names, many alternatives and deep brackets, and 10 KB of malformed text reads in 10 ms or less. A reading runs
	/// on a 1 MB stack, as a generator does on the compiler's threads, and a nesting deeper than that stack is reported,
	/// never a crash.
	/// </summary>
	[TestClass]
	public class FilterV2ScalingTests
	{
		public TestContext TestContext { get; set; } = null!;

		const int StackSize = 1024 * 1024;

		/// <summary>Times below this are compared as this: a ratio of two sub-microsecond reads says nothing.</summary>
		const double FloorMs = 0.05;

		static readonly string[] _malformed =
		[
			"method: Save* and (public or",
			"- type: *Service<int,> or namespace: Shop.**",
			"public static Save*(int, ..) : Task<*",
			"attribute: Obsolete and not (public or private)",
			"method: Get*; static",
			"returns: {void,Task and param: ref",
		];

		/// <summary>Malformed lines that close what they open: each is stepped over on its own.</summary>
		static readonly string[] _malformedClosed =
		[
			"publik Save*(int) : Task",
			"method: Get*; static",
			"- type: Task<> or namespace: Shop.**",
			"not (public or private) and methd: x",
			"contains: Save and public",
			"method: Save* and",
		];

		static readonly string[] _valid =
		[
			"method: Save* and (public or internal)",
			"- type: *Service<int,*> or namespace: Shop.**",
			"public static **.*Service.Save*(int, .., CancellationToken) : Task<*>",
			"attribute: Obsolete and not public",
			"returns: {void,Task} and param: ref int",
		];

		static string Fill(string[] lines, int length)
		{
			var builder = new StringBuilder();

			for (var i = 0; builder.Length < length; i++)
				builder.Append(lines[i % lines.Length]).Append('\n');

			return builder.ToString();
		}

		/// <summary>The inputs of one shape at three sizes, ten times apart.</summary>
		static IEnumerable<(string Shape, Func<int, string> Make, int[] Sizes)> Shapes()
		{
			yield return ("malformed lines",            n => Fill(_malformed, n),                                                                         [1_000, 10_000, 100_000]);
			yield return ("malformed lines, closed",    n => Fill(_malformedClosed, n),                                                                   [1_000, 10_000, 100_000]);
			yield return ("valid lines",                n => Fill(_valid, n),                                                                             [1_000, 10_000, 100_000]);
			yield return ("one long name",              n => "method: " + new string('a', n),                                                             [1_000, 10_000, 100_000]);
			yield return ("long name, unclosed <",      n => "method: A<" + new string('a', n),                                                           [1_000, 10_000, 100_000]);
			yield return ("{…} alternatives",           n => "method: {" + string.Join(",", Enumerable.Range(0, n).Select(static i => "A" + i)) + "}",    [10, 100, 1_000]);
			yield return ("{…} alternatives, unclosed", n => "method: {" + string.Join(",", Enumerable.Range(0, n).Select(static i => "A" + i)),          [10, 100, 1_000]);
			yield return ("nested parentheses",         n => new string('(', n) + "public" + new string(')', n),                                         [20, 200, 2_000]);
			yield return ("nested parentheses, open",   n => new string('(', n) + "public",                                                              [20, 200, 2_000]);
			yield return ("nested <…>",                 n => "type: " + string.Concat(Enumerable.Repeat("A<", n)) + "B" + new string('>', n),             [20, 200, 2_000]);
		}

		/// <summary>The best of several reads on a 1 MB stack, and what the last one read to.</summary>
		static (double Ms, FilterItemReading Reading) Time(string input, int rounds)
		{
			var best    = double.MaxValue;
			FilterItemReading? reading = null;
			Exception? failure = null;

			var thread = new Thread(() =>
			{
				try
				{
					FilterV2Reader.Read(input);

					for (var i = 0; i < rounds; i++)
					{
						var watch = Stopwatch.StartNew();

						reading = FilterV2Reader.Read(input);
						best    = Math.Min(best, watch.Elapsed.TotalMilliseconds);
					}
				}
				catch (Exception ex)
				{
					failure = ex;
				}
			}, StackSize);

			thread.Start();
			thread.Join();

			if (failure is not null)
				throw new AssertFailedException($"Reading {input.Length} characters failed: {failure}");

			return (best, reading!);
		}

		static string Outcome(FilterItemReading reading)
		{
			if (reading.Item is null)
				return string.Join(", ", reading.Diagnostics.Select(static d => d.Id).Distinct());

			return reading.HasErrors ? $"read, {reading.Diagnostics.Length} errors" : "read";
		}

		[TestMethod]
		public void ReadingGrowsLinearlyTest()
		{
			var failures = new List<string>();

			foreach (var (shape, make, sizes) in Shapes())
			{
				var times = new List<double>();

				foreach (var size in sizes)
				{
					var input          = make(size);
					var (ms, reading)  = Time(input, size >= 100_000 ? 3 : 7);

					times.Add(ms);
					TestContext.WriteLine($"{shape,-28} {size,7} {input.Length,8} chars {ms,9:0.000} ms  {Outcome(reading)}");
				}

				for (var i = 1; i < times.Count; i++)
				{
					var ratio = Math.Max(times[i], FloorMs) / Math.Max(times[i - 1], FloorMs);

					if (ratio > 20)
						failures.Add($"{shape}: {sizes[i - 1]} → {sizes[i]} grew {ratio:0.0}× (bar: 20× for 10× the input)");
				}
			}

			foreach (var lines in new[] { _malformed, _malformedClosed })
			{
				var (malformed, _) = Time(Fill(lines, 10_000), 7);

				TestContext.WriteLine($"10 KB malformed ('{lines[0]}' …): {malformed:0.000} ms (bar: 10 ms)");

				if (malformed > 10)
					failures.Add($"10 KB of malformed text ('{lines[0]}' …) read in {malformed:0.000} ms (bar: 10 ms)");
			}

			Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
		}

		[TestMethod]
		public void DeepNestingIsReportedNotCrashedTest()
		{
			foreach (var depth in new[] { 2_000, 20_000, 200_000 })
			{
				foreach (var input in new[] { new string('(', depth) + "public" + new string(')', depth), "type: " + string.Concat(Enumerable.Repeat("A<", depth)) + "B" + new string('>', depth) })
				{
					var (_, reading) = Time(input, 1);

					// Either it is read, or it is too deep to read on this stack and says so.
					if (reading.Item is null)
						Assert.AreEqual(FilterV2DiagnosticID.NestedTooDeeply, reading.Diagnostics.Single().Id);
					else
						Assert.IsFalse(reading.HasErrors, string.Join("; ", reading.Diagnostics));

					TestContext.WriteLine($"{input.Substring(0, 8)}… depth {depth}: {Outcome(reading)}");
				}
			}
		}
	}
}
