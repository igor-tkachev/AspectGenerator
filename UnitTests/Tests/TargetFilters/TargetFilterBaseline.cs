using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// Reads and writes the baseline files under <c>Baselines/TargetFilters</c>. Files are compared with line endings
	/// normalised, so a checkout with CRLF endings compares equal. <c>AG_UPDATE_BASELINES=1</c> rewrites them.
	/// </summary>
	static class TargetFilterBaseline
	{
		public const int MaxBytes = 3 * 1024 * 1024;

		const int MaxReportedDifferences = 20;

		public static bool Update
		{
			get
			{
				return Environment.GetEnvironmentVariable("AG_UPDATE_BASELINES") == "1";
			}
		}

		/// <summary>Writes the lines with the line ending the file already has (LF for a new file, which is how the repository stores text).</summary>
		public static void WriteLines(string path, IEnumerable<string> lines)
		{
			var eol = File.Exists(path) && File.ReadAllText(path).Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

			File.WriteAllText(path, string.Concat(lines.Select(l => l + eol)), new UTF8Encoding(false));
		}

		public static void Check(string name, IReadOnlyList<string> actual)
		{
			var path = TargetFilterCuratedInputs.BaselinePath(name);

			if (Update)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				WriteLines(path, actual);
			}

			Assert.IsTrue(File.Exists(path), $"Baseline {name} does not exist. Run the tests with AG_UPDATE_BASELINES=1 to create it.");
			Assert.IsTrue(new FileInfo(path).Length <= MaxBytes, $"Baseline {name} is larger than {MaxBytes} bytes.");

			var expected = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n').ToList();

			if (expected.Count > 0 && expected[^1].Length == 0)
				expected.RemoveAt(expected.Count - 1);

			var differences = new List<string>();
			var total       = 0;

			for (var i = 0; i < Math.Max(expected.Count, actual.Count); i++)
			{
				var e = i < expected.Count ? expected[i] : "<missing>";
				var a = i < actual.Count   ? actual[i]   : "<missing>";

				if (e == a)
					continue;

				total++;

				if (differences.Count < MaxReportedDifferences)
					differences.Add($"record {i + 1}:{Environment.NewLine}  expected: {e}{Environment.NewLine}  actual:   {a}");
			}

			if (total > 0)
				Assert.Fail($"Baseline {name} differs in {total} of {Math.Max(expected.Count, actual.Count)} records (set AG_UPDATE_BASELINES=1 to accept). First {differences.Count}:{Environment.NewLine}{string.Join(Environment.NewLine, differences)}");
		}
	}
}
