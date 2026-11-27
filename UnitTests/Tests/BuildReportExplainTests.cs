using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// The build report says why an aspect written on a method does not apply to it, and with the trace on why every method a
	/// filter considered got the aspect or not. Each case runs the generator end to end with a report file.
	/// </summary>
	public partial class CompilerTests
	{
		const string ExplainAspectSource =
			"""
			using System;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver
			{
				[Aspect(OnBeforeCall = nameof(Before)/*DEFAULT*/)]
				[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
				sealed class LogAttribute : Attribute
				{
					public string? TargetFilter { get; set; }
					public int     Level        { get; set; }

					public static void Before(InterceptInfo info)
					{
					}
				}

				[AttributeUsage(AttributeTargets.Method)]
				sealed class NoLogAttribute : Attribute
				{
				}
			}
			""";

		static string ExplainAspect(string? defaultFilter = null)
		{
			return ExplainAspectSource.Replace("/*DEFAULT*/", defaultFilter is null ? "" : $", DefaultTargetFilter = \"{defaultFilter}\"", StringComparison.Ordinal);
		}

		static string ExplainReport(string[] sources, bool trace = false, string? sizeLimit = null, MetadataReference? reference = null, Dictionary<string,string>? extra = null)
		{
			var reportFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "report.md");
			var properties = new Dictionary<string,string>
			{
				[$"build_property.AspectGenerator{AspectOptionName.ReportFile}"] = reportFile,
			};

			if (trace)
				properties[$"build_property.AspectGenerator{AspectOptionName.Trace}"] = "true";

			if (sizeLimit is not null)
				properties[$"build_property.AspectGenerator{AspectOptionName.ReportSizeLimit}"] = sizeLimit;

			if (extra is not null)
				foreach (var item in extra)
					properties[item.Key] = item.Value;

			try
			{
				RunGenerator(sources, properties, reference: reference);

				return File.Exists(reportFile) ? File.ReadAllText(reportFile) : "";
			}
			finally
			{
				if (Directory.Exists(Path.GetDirectoryName(reportFile)))
					Directory.Delete(Path.GetDirectoryName(reportFile)!, recursive: true);
			}
		}

		// The test project applies an aspect to string.Substring(int) in its own code (AspectGeneratorOptions.cs): the code below
		// takes ranges instead, and writes no one-argument Substring.

		/// <summary>The text of one section: from its heading to the next section.</summary>
		static string ReportSection(string report, string heading)
		{
			var start = report.IndexOf("## " + heading + "\n", StringComparison.Ordinal);

			if (start < 0)
				start = report.IndexOf("## " + heading + "\r\n", StringComparison.Ordinal);

			if (start < 0)
				return "";

			var end = report.IndexOf("\n## ", start + 1, StringComparison.Ordinal);

			return end < 0 ? report[start..] : report.Substring(start, end - start);
		}

		/// <summary>One table of an explanation section: the method, the aspect and the table's text.</summary>
		sealed record ExplainTable(string Method, string Aspect, string Text);

		static List<ExplainTable> ExplainTables(string section)
		{
			var tables = new List<ExplainTable>();
			var lines  = section.Replace("\r", "", StringComparison.Ordinal).Split('\n');

			for (var i = 0; i < lines.Length; i++)
			{
				if (!lines[i].StartsWith("### `", StringComparison.Ordinal))
					continue;

				var heading = lines[i][5..];
				var split   = heading.IndexOf("` - `", StringComparison.Ordinal);
				var start   = Array.FindIndex(lines, i, static l => l.StartsWith("```text", StringComparison.Ordinal));
				var end     = Array.FindIndex(lines, start + 1, static l => l.StartsWith("```", StringComparison.Ordinal));

				tables.Add(new ExplainTable(
					heading.Substring(0, split),
					heading[(split + 5)..^1],
					string.Join("\n", lines.Skip(start + 1).Take(end - start - 1))));
			}

			return tables;
		}

		/// <summary>The target methods the report lists, as it writes them.</summary>
		static List<string> ListedTargetMethods(string report)
		{
			return ReportSection(report, "Target Methods")
				.Split('\n')
				.Where(static l => l.StartsWith("| `", StringComparison.Ordinal))
				.Select(static l => l.Substring(3, l.IndexOf("` |", StringComparison.Ordinal) - 3))
				.ToList();
		}

		/// <summary>
		/// What an explanation says of a method is what the generator did with it: a table that says "selected: yes" is a method
		/// the report lists as a target, one that says "no" is not (for a method decided per call, the constructed method).
		/// </summary>
		static void AssertExplanationsAgreeWithTheSelection(string report, string section)
		{
			var targets = ListedTargetMethods(report);

			foreach (var table in ExplainTables(ReportSection(report, section)))
			{
				// A method a library marked has the library's outcome first and the compilation's last, which decides.
				var first = table.Text.Split('\n').Last(static l => l.StartsWith("family: ", StringComparison.Ordinal));

				if (first.EndsWith("selected: yes", StringComparison.Ordinal))
					CollectionAssert.Contains(targets, table.Method, $"{table.Method} is explained as selected:{Environment.NewLine}{table.Text}");
				else if (first.EndsWith("selected: no", StringComparison.Ordinal))
					CollectionAssert.DoesNotContain(targets, table.Method, $"{table.Method} is explained as not selected:{Environment.NewLine}{table.Text}");
				else
					Assert.Fail("The table does not start with the outcome: " + first);
			}
		}

		const string ExplainNoLogSource =
			"""
			namespace AspectGenerator.Tests.GeneratorDriver
			{
				static class Calls
				{
					[Log]
					[NoLog]
					public static void Quiet()
					{
					}

					[Log]
					public static void Loud()
					{
					}

					public static void Run()
					{
						Quiet();
						Loud();
					}
				}
			}
			""";

		[TestMethod]
		public void ExplainNotAppliedBySubtractionFromTheAspectDefaultTest()
		{
			var report  = ExplainReport([ExplainAspect("- attribute: NoLog"), ExplainNoLogSource]);
			var section = ReportSection(report, "Not Applied Aspects");
			var tables  = ExplainTables(section);

			// Only the method its filters do not select is explained, with no trace needed.
			Assert.AreEqual(1, tables.Count, report);
			Assert.AreEqual("AspectGenerator.Tests.GeneratorDriver.Calls.Quiet()", tables[0].Method);
			Assert.AreEqual("Log", tables[0].Aspect);
			StringAssert.Contains(section, "Attribute at [GeneratorDriverTest1.cs:5:4](");
			StringAssert.Contains(tables[0].Text, "family: consumer; selected: no");
			StringAssert.Contains(tables[0].Text, "combination 1/1:");

			// The subtraction comes from the aspect's own default; the author's attribute declares the place only.
			var attribute = tables[0].Text.Split('\n').Single(static l => l.TrimStart().StartsWith("attribute ", StringComparison.Ordinal));

			StringAssert.Contains(attribute, "DefaultTargetFilter of Log (GeneratorDriverTest.cs:6)");
			StringAssert.Contains(attribute, "aspect");
			StringAssert.Contains(attribute, "fails");
			StringAssert.Contains(attribute, "- attribute: NoLog");
			Assert.IsFalse(section.Contains("Loud()", StringComparison.Ordinal), "A method the aspect applies to is not listed.");

			// Without the trace the report has no trace section and no list of explained methods.
			Assert.IsFalse(report.Contains("## Explained Methods", StringComparison.Ordinal));
			Assert.IsFalse(report.Contains("## AspectGenerator trace", StringComparison.Ordinal));
			AssertExplanationsAgreeWithTheSelection(report, "Not Applied Aspects");
		}

		[TestMethod]
		public void ExplainAPropertyTheTypeFilterTookOverFromTheAssemblyTest()
		{
			// The type declares the accessibility, so the assembly's is not asked: the table names the type's block, and a
			// method block (which declares the method itself, and here nothing else) leaves the property to it.
			var report = ExplainReport(
			[
				ExplainAspect(),
				"""
				[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "public")]

				namespace AspectGenerator.Tests.GeneratorDriver
				{
					[Log(TargetFilter = "internal")]
					static class Calls
					{
						[Log(TargetFilter = "static")]
						public static void SaveOrder()
						{
						}

						public static void Run()
						{
							SaveOrder();
						}
					}
				}
				"""
			]);

			var table = ExplainTables(ReportSection(report, "Not Applied Aspects")).Single().Text;
			var rows  = table.Split('\n').Select(static l => l.TrimStart()).ToList();
			var row   = rows.Single(static l => l.StartsWith("accessibility ", StringComparison.Ordinal));

			StringAssert.Contains(row, "[Log] on AspectGenerator.Tests.GeneratorDriver.Calls (GeneratorDriverTest1.cs:5)");
			StringAssert.Contains(row, "type");
			StringAssert.Contains(row, "fails");
			Assert.IsFalse(rows.Any(static l => l.StartsWith("accessibility ", StringComparison.Ordinal) && l.Contains("assembly", StringComparison.Ordinal)), "The assembly's accessibility is not asked.");

			// What the type does not declare comes from the assembly: here the project the method must be in.
			Assert.IsTrue(rows.Any(static l => l.StartsWith("origin ", StringComparison.Ordinal) && l.Contains("[assembly: Log] (GeneratorDriverTest1.cs:1)", StringComparison.Ordinal)), table);
		}

		[TestMethod]
		public void ExplainTheExpansionOfAReferenceToTheAssemblyFilterTest()
		{
			var report = ExplainReport(
			[
				ExplainAspect(),
				"""
				[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "method: Save*")]

				namespace AspectGenerator.Tests.GeneratorDriver
				{
					static class Calls
					{
						[Log(TargetFilter = "method: Load or assembly.method")]
						public static void Other()
						{
						}

						public static void Run()
						{
							Other();
						}
					}
				}
				"""
			]);

			var table = ExplainTables(ReportSection(report, "Not Applied Aspects")).Single().Text;

			StringAssert.Contains(table, "method: Load or assembly.method");
			StringAssert.Contains(table, "assembly.method = false, from:");
			StringAssert.Contains(table, "[assembly: Log] (GeneratorDriverTest1.cs:1)");
			StringAssert.Contains(table, "method: Save*");
		}

		[TestMethod]
		public void ExplainWhereASettingCameFromTest()
		{
			var report = ExplainReport(
			[
				ExplainAspect(),
				"""
				[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "method: Save*", Level = 3)]

				namespace AspectGenerator.Tests.GeneratorDriver
				{
					static class Calls
					{
						[Log(TargetFilter = "method: Nothing")]
						public static void Other()
						{
						}

						public static void Run()
						{
							Other();
						}
					}
				}
				"""
			]);

			var table = ExplainTables(ReportSection(report, "Not Applied Aspects")).Single().Text;

			StringAssert.Contains(table, "setting Level = 3 from [assembly: Log] (GeneratorDriverTest1.cs:1)");
		}

		[TestMethod]
		public void ExplainAConflictOfSettingsTest()
		{
			// The method keeps the aspect, so only the trace lists it; its conflict is in the settings part of its table.
			var report = ExplainReport(
				[
					ExplainAspect(),
					"""
					[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "method: Save*", Level = 1)]
					[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "method: *Order", Level = 2)]

					namespace AspectGenerator.Tests.GeneratorDriver
					{
						static class Calls
						{
							public static void SaveOrder()
							{
							}

							public static void SaveUser()
							{
							}

							public static void Run()
							{
								SaveOrder();
								SaveUser();
							}
						}
					}
					"""
				],
				trace: true);

			var tables = ExplainTables(ReportSection(report, "Explained Methods"));
			var order  = tables.Single(static t => t.Method.EndsWith("SaveOrder()", StringComparison.Ordinal)).Text;
			var user   = tables.Single(static t => t.Method.EndsWith("SaveUser()", StringComparison.Ordinal)).Text;

			StringAssert.Contains(order, "selected: yes");
			StringAssert.Contains(order, "conflict AG0235 Level: 1 from [assembly: Log] #1 (GeneratorDriverTest1.cs:1); 2 from [assembly: Log] #2 (GeneratorDriverTest1.cs:2) (the setting takes the aspect's default)");
			StringAssert.Contains(user, "setting Level = 1 from [assembly: Log] #1");
			Assert.IsFalse(user.Contains("conflict", StringComparison.Ordinal));
			AssertExplanationsAgreeWithTheSelection(report, "Explained Methods");
		}

		[TestMethod]
		public void ExplainALibraryMethodItsLibraryBlocksDoNotSelectTest()
		{
			foreach (var metadataOnly in new[] { false, true })
			{
				var library = CompileSelectionLibrary(SelectionLibrarySource, metadataOnly);
				var kind    = metadataOnly ? "reference assembly" : "implementation assembly";
				var report  = ExplainReport(
					[SelectionLibraryConsumerSource.Replace("/*ASSEMBLY*/", "[assembly: SelectionLib.LibLog(TargetFilter = \"method: Write\", Level = 3)]", StringComparison.Ordinal)],
					trace: true,
					reference: library);

				var tables = ExplainTables(ReportSection(report, "Explained Methods"));
				var write  = tables.Single(static t => t.Method.EndsWith("Service.Write()", StringComparison.Ordinal)).Text;
				var read   = tables.Single(static t => t.Method.EndsWith("Service.Read()", StringComparison.Ordinal)).Text;

				// Write is an instance method: the library's static filter does not select it, and the compilation's decides.
				StringAssert.Contains(write, "library blocks:", kind);
				StringAssert.Contains(write, "assembly SelectionLib (metadata)", kind);
				StringAssert.Contains(write, "then the assembly blocks of the compilation:", kind);
				StringAssert.Contains(write, "selected: yes", kind);
				StringAssert.Contains(write, "setting Level = 3", kind);

				// Read is selected by the library's own blocks.
				StringAssert.Contains(read, "family: library; selected: yes", kind);
				AssertExplanationsAgreeWithTheSelection(report, "Explained Methods");
			}
		}

		[TestMethod]
		public void ExplainTraceListsSelectedMethodsAndEachConstructedMethodOnceTest()
		{
			var report = ExplainReport(
				[
					ExplainAspect(),
					"""
					[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "method: Get* and param: int or method: Plain")]

					namespace AspectGenerator.Tests.GeneratorDriver
					{
						static class Calls
						{
							public static T Get<T>(T value)
							{
								return value;
							}

							public static void Plain()
							{
							}

							public static void Run()
							{
								Get(1);
								Get(2);
								Get("text");
								Get("more");
								Plain();
							}
						}
					}
					"""
				],
				trace: true);

			var tables = ExplainTables(ReportSection(report, "Explained Methods"));
			var names  = tables.Select(static t => t.Method.Replace("AspectGenerator.Tests.GeneratorDriver.Calls.", "", StringComparison.Ordinal)).ToList();

			// One table per constructed method, not per call; the definition itself decides nothing and has none. The aspect's own
			// hook is a method like any other.
			CollectionAssert.AreEqual(new[] { "Get<int>(int)", "Get<string>(string)", "Plain()", "Run()", "AspectGenerator.Tests.GeneratorDriver.LogAttribute.Before(InterceptInfo)" }, names, report);
			Assert.IsTrue(tables[0].Text.StartsWith("family: consumer; selected: yes", StringComparison.Ordinal), tables[0].Text);
			Assert.IsTrue(tables[1].Text.StartsWith("family: consumer; selected: no",  StringComparison.Ordinal), tables[1].Text);
			Assert.IsTrue(tables[2].Text.StartsWith("family: consumer; selected: yes", StringComparison.Ordinal), tables[2].Text);
			Assert.IsTrue(tables[3].Text.StartsWith("family: consumer; selected: no",  StringComparison.Ordinal), tables[3].Text);
			Assert.IsTrue(tables[4].Text.StartsWith("family: consumer; selected: no",  StringComparison.Ordinal), tables[4].Text);
			StringAssert.Contains(ReportSection(report, "Explained Methods"), "The decision depends on the call");
			AssertExplanationsAgreeWithTheSelection(report, "Explained Methods");

			// The counter counts the tables.
			StringAssert.Contains(report, "| Explained methods | 5 |");
		}

		[TestMethod]
		public void ExplainTablesFallUnderTheSizeLimitLikeTheListsTest()
		{
			var source = ManyCallSitesSource(40, 1);

			var unlimited = ExplainReport([source], trace: true, sizeLimit: "0");
			var every     = ExplainTables(ReportSection(unlimited, "Explained Methods"));

			// The 40 targets, the method that makes the calls and the aspect's own hook: each is considered by the filter.
			Assert.AreEqual(42, every.Count, unlimited);
			Assert.IsFalse(unlimited.Contains("explanations are not listed", StringComparison.Ordinal));

			var limited = ExplainReport([source], trace: true, sizeLimit: "24");
			var some    = ExplainTables(ReportSection(limited, "Explained Methods"));

			Assert.IsTrue(some.Count < every.Count, $"{some.Count} tables");
			StringAssert.Contains(limited, $" of {every.Count} explanations are not listed: the lists get what is left of the 24 KB `AspectGeneratorReportSizeLimit`");

			// The summary, the sources and the trace stay complete.
			StringAssert.Contains(limited, "| Generated call sites | 40 |");
			StringAssert.Contains(limited, "### AGTRACE events");
			StringAssert.Contains(limited, "| Explained methods | 42 |");

			// A section that must be written is, however small the limit.
			var notApplied = ExplainReport([ExplainAspect("- attribute: NoLog"), ExplainNoLogSource], sizeLimit: "1");

			StringAssert.Contains(notApplied, "## Not Applied Aspects");
			StringAssert.Contains(notApplied, "> 1 of 1 explanations are not listed: the lists get what is left of the 1 KB");
		}

		[TestMethod]
		public void ExplainIsAskedForOnlyWhenTheReportHasAUseForItTest()
		{
			var plain = new[] { ExplainAspect(), ExplainNoLogSource.Replace("[NoLog]", "", StringComparison.Ordinal) };

			// A report with no trace and nothing not applied explains nothing, and has no section.
			var before = AspectSelectionService.ExplainCalls;
			var report = ExplainReport(plain);

			Assert.AreEqual(before, AspectSelectionService.ExplainCalls, "No explanation is asked for.");
			Assert.IsFalse(report.Contains("## Not Applied Aspects", StringComparison.Ordinal));
			Assert.IsFalse(report.Contains("## Explained Methods", StringComparison.Ordinal));
			Assert.IsTrue(report.Length > 0);

			// No report file: nothing is explained, with the trace on and with a method that is not applied.
			var noFile = new Dictionary<string,string> { ["build_property.AspectGeneratorTrace"] = "true" };

			before = AspectSelectionService.ExplainCalls;
			RunGenerator([ExplainAspect("- attribute: NoLog"), ExplainNoLogSource], noFile);

			Assert.AreEqual(before, AspectSelectionService.ExplainCalls, "No report, no explanation.");

			// A design-time build writes no report either (an editor's report is not written by the rule that decides it).
			before = AspectSelectionService.ExplainCalls;

			var designTime = ExplainReport([ExplainAspect("- attribute: NoLog"), ExplainNoLogSource], trace: true, extra: new() { ["build_property.DesignTimeBuild"] = "true" });

			Assert.AreEqual("", designTime);
			Assert.AreEqual(before, AspectSelectionService.ExplainCalls, "A design-time build explains nothing.");

			// A method that is not applied: that one is explained, the other is not.
			before = AspectSelectionService.ExplainCalls;
			report = ExplainReport([ExplainAspect("- attribute: NoLog"), ExplainNoLogSource]);

			Assert.AreEqual(before + 1, AspectSelectionService.ExplainCalls);
			Assert.IsFalse(report.Contains("## Explained Methods", StringComparison.Ordinal));

			// With the trace, every method a filter considered; the counter agrees with the calls.
			before = AspectSelectionService.ExplainCalls;
			report = ExplainReport([ExplainAspect("- attribute: NoLog"), ExplainNoLogSource], trace: true);

			var tables = ExplainTables(ReportSection(report, "Explained Methods"));

			Assert.AreEqual(before + tables.Count, AspectSelectionService.ExplainCalls);
			StringAssert.Contains(report, $"| Explained methods | {tables.Count} |");
			AssertExplanationsAgreeWithTheSelection(report, "Explained Methods");
			AssertExplanationsAgreeWithTheSelection(report, "Not Applied Aspects");
		}
	}
}
