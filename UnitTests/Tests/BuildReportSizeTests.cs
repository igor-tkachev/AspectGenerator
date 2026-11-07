using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// The build report lists every target method and call site; a broad target filter on a large project selects tens of
	/// thousands of them. The report is limited in size by default, keeps its summary complete and says what it left out.
	/// </summary>
	public partial class CompilerTests
	{
		static string ManyCallSitesSource(int methods, int callsPerMethod)
		{
			var sb = new StringBuilder();

			sb.AppendLine(
				"""
				using System;
				using AspectGenerator;

				[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "method: Target*")]

				namespace AspectGenerator.Tests.GeneratorDriver;

				[Aspect(OnBeforeCall = nameof(Before))]
				[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, AllowMultiple = true)]
				sealed class LogAttribute : Attribute
				{
					public string? TargetFilter { get; set; }

					public static void Before(InterceptInfo info)
					{
					}
				}

				static class Targets
				{
				""");

			for (var i = 0; i < methods; i++)
				sb.Append("\tpublic static int Target").Append(i).AppendLine("WithAFairlyLongNameSoThatTheReportGrowsQuickly(int value) { return value; }");

			sb
				.AppendLine("}")
				.AppendLine()
				.AppendLine("static class Calls")
				.AppendLine("{")
				.AppendLine("\tpublic static int Run()")
				.AppendLine("\t{")
				.AppendLine("\t\tvar sum = 0;");

			for (var i = 0; i < methods; i++)
				for (var j = 0; j < callsPerMethod; j++)
					sb.Append("\t\tsum += Targets.Target").Append(i).Append("WithAFairlyLongNameSoThatTheReportGrowsQuickly(").Append(j).AppendLine(");");

			sb
				.AppendLine("\t\treturn sum;")
				.AppendLine("\t}")
				.AppendLine("}");

			return sb.ToString();
		}

		static string WriteReport(string source, string? sizeLimit)
		{
			var reportFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "report.md");
			var properties = new Dictionary<string,string>
			{
				[$"build_property.AspectGenerator{AspectOptionName.ReportFile}"] = reportFile,
			};

			if (sizeLimit is not null)
				properties[$"build_property.AspectGenerator{AspectOptionName.ReportSizeLimit}"] = sizeLimit;

			try
			{
				RunGenerator(source, properties);

				return File.ReadAllText(reportFile);
			}
			finally
			{
				Directory.Delete(Path.GetDirectoryName(reportFile)!, recursive: true);
			}
		}

		[TestMethod]
		public void BuildReportIsLimitedToOneMegabyteByDefaultTest()
		{
			// 4,000 call sites make a report of several megabytes when every one is listed.
			var source   = ManyCallSitesSource(200, 20);
			var report   = WriteReport(source, null);
			var reportSize = Encoding.UTF8.GetByteCount(report);

			Assert.IsTrue(reportSize <= 1024 * 1024, $"The report is {reportSize} bytes.");
			StringAssert.Contains(report, "| Generated call sites | 4000 |");
			StringAssert.Contains(report, "| Target methods | 200 |");
			StringAssert.Contains(report, " of 4,000 call sites are not listed: the report is limited to 1,024 KB. Set `AspectGeneratorReportSizeLimit` to `0` to list them all");
		}

		[TestMethod]
		public void BuildReportSizeLimitIsConfigurableTest()
		{
			var source = ManyCallSitesSource(40, 5);

			var limited     = WriteReport(source, "16");
			var limitedSize = Encoding.UTF8.GetByteCount(limited);

			Assert.IsTrue(limitedSize <= 16 * 1024, $"The report is {limitedSize} bytes.");
			StringAssert.Contains(limited, "| Generated call sites | 200 |");
			StringAssert.Contains(limited, "| Target methods | 40 |");
			StringAssert.Contains(limited, "## Intercepted Call Sites");
			StringAssert.Contains(limited, " of 200 call sites are not listed: the report is limited to 16 KB.");
			Assert.IsTrue(CountOccurrences(limited, "Interceptor`") > 0, "Some rows are still listed.");

			// The target methods take at most half of what is left, so some call sites are listed as well.
			Assert.IsTrue(limited.Contains("| 1 | [", StringComparison.Ordinal), "The first call site is listed.");

			var unlimited = WriteReport(source, "0");

			Assert.IsFalse(unlimited.Contains("are not listed", StringComparison.Ordinal));
			StringAssert.Contains(unlimited, "| 200 | [");
			Assert.IsTrue(Encoding.UTF8.GetByteCount(unlimited) > 16 * 1024);

			// A value that is not a size uses the default limit, which this report does not reach.
			var invalid = WriteReport(source, "lots");

			Assert.AreEqual(Normalize(unlimited), Normalize(invalid));

			static string Normalize(string report)
			{
				return string.Join("\n", report.Split('\n').Where(static line => !line.Contains("AspectGeneratorReportSizeLimit", StringComparison.Ordinal)));
			}
		}
	}
}
