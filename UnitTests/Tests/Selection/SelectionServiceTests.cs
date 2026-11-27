using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// The selection of targets by the cascade of version 2 filters, end to end through the generator: per-call decisions
	/// of generic methods, the library family, settings conflicts, filter errors, aspects written on methods that are not
	/// applied, and the calls the assembly filters let be bound.
	/// </summary>
	public partial class CompilerTests
	{
		const string SelectionLogAspectSource =
			"""
			using System;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver
			{
				[Aspect(OnBeforeCall = nameof(Before))]
				[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
				sealed class LogAttribute : Attribute
				{
					public string? TargetFilter { get; set; }
					public int     Level        { get; set; }

					public static void Before(InterceptInfo info)
					{
					}
				}
			}
			""";

		static GeneratorDriverRunResult RunSelection(string source, Dictionary<string,string>? properties = null, MetadataReference? reference = null)
		{
			return RunGenerator([SelectionLogAspectSource, source], properties, reference: reference);
		}

		static int CountIntercepted(GeneratorDriverRunResult result, string call)
		{
			// The comment above each interceptor names the calls it intercepts as written; the summary names the method.
			return CountOccurrences(GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs"), $"\t// Intercepts {call}.");
		}

		static string DescribeDiagnostics(GeneratorDriverRunResult result)
		{
			return string.Join(Environment.NewLine, result.Diagnostics.Select(static d => $"{d.Id}: {d.GetMessage()}"));
		}

		[TestMethod]
		public void GenericMethodIsDecidedPerCallTest()
		{
			var result = RunSelection(
				"""
				[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "method: Get and param: int")]

				namespace AspectGenerator.Tests.GeneratorDriver
				{
					static class Calls
					{
						public static T Get<T>(T value)
						{
							return value;
						}

						public static void Run()
						{
							Get(1);
							Get("text");
							Get<int>(2);
						}

						// Inside generic code the type argument is the caller's own type parameter, which only '*' matches.
						public static void Caller<U>(U value)
						{
							Get(value);
						}
					}
				}
				""");

			Assert.AreEqual(1, CountIntercepted(result, "Get(1)"),       DescribeDiagnostics(result));
			Assert.AreEqual(1, CountIntercepted(result, "Get<int>(2)"),  DescribeDiagnostics(result));
			Assert.AreEqual(0, CountIntercepted(result, "Get(\"text\")"));
			Assert.AreEqual(0, CountIntercepted(result, "Get(value)"));
		}

		[TestMethod]
		public void StarMatchesTheTypeParameterOfAGenericCallerTest()
		{
			var result = RunSelection(
				"""
				[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "method: Get<*>")]

				namespace AspectGenerator.Tests.GeneratorDriver
				{
					static class Calls
					{
						public static T Get<T>(T value)
						{
							return value;
						}

						public static void Caller<U>(U value)
						{
							Get(value);
							Get("text");
						}
					}
				}
				""");

			Assert.AreEqual(1, CountIntercepted(result, "Get(value)"),  DescribeDiagnostics(result));
			Assert.AreEqual(1, CountIntercepted(result, "Get(\"text\")"));
		}

		[TestMethod]
		public void FilterErrorTurnsTheAspectOffTest()
		{
			const string source =
				"""
				[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "method: Save*")]

				namespace AspectGenerator.Tests.GeneratorDriver
				{
					[Log(TargetFilter = "methd: Load\n- returns: Task<>")]
					static class Other
					{
						public static void Load()
						{
						}
					}

					static class Calls
					{
						public static void SaveUser()
						{
						}

						public static void Run()
						{
							SaveUser();
							Other.Load();
						}
					}
				}
				""";

			var result = RunSelection(source);
			var errors = result.Diagnostics.Where(static d => d.Severity == DiagnosticSeverity.Error && d.Id.StartsWith("AG02", StringComparison.Ordinal)).ToList();

			// Both errors of the filter are reported, the first at its characters inside the literal.
			Assert.AreEqual(2, errors.Count, DescribeDiagnostics(result));
			Assert.IsTrue(errors.Any(static d => d.Id == AspectDiagnosticID.FilterUnknownKeyOrWord), DescribeDiagnostics(result));

			var unknown = errors.First(static d => d.Id == AspectDiagnosticID.FilterUnknownKeyOrWord);

			StringAssert.StartsWith(unknown.Location.SourceTree!.GetText().ToString(unknown.Location.SourceSpan), "methd");

			Assert.AreEqual(0, CountIntercepted(result, "SaveUser()"));
			Assert.AreEqual(0, CountIntercepted(result, "Other.Load()"));

			// A design-time build reports the same errors live.
			var designTime = RunSelection(source, new() { ["build_property.DesignTimeBuild"] = "true" });

			CollectionAssert.AreEquivalent(
				errors.Select(static d => $"{d.Id} {d.Location.SourceSpan}").ToList(),
				designTime.Diagnostics.Where(static d => d.Id.StartsWith("AG02", StringComparison.Ordinal)).Select(static d => $"{d.Id} {d.Location.SourceSpan}").ToList());
		}

		[TestMethod]
		public void ConflictingSettingsAreReportedAndTheMethodKeepsTheAspectTest()
		{
			var result = RunSelection(
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
				""");

			var conflicts = result.Diagnostics.Where(static d => d.Id == AspectDiagnosticID.AspectSettingsConflict).ToList();

			Assert.AreEqual(1, conflicts.Count, DescribeDiagnostics(result));
			Assert.AreEqual(DiagnosticSeverity.Error, conflicts[0].Severity);
			StringAssert.Contains(conflicts[0].GetMessage(), "SaveOrder()");
			StringAssert.Contains(conflicts[0].GetMessage(), "'Level'");
			Assert.AreEqual(1, CountIntercepted(result, "SaveOrder()"));
			Assert.AreEqual(1, CountIntercepted(result, "SaveUser()"));
		}

		[TestMethod]
		public void AspectWrittenOnAMethodThatIsNotAppliedIsReportedTest()
		{
			var result = RunSelection(
				"""
				namespace AspectGenerator.Tests.GeneratorDriver
				{
					static class Calls
					{
						[Log(TargetFilter = "public")]
						internal static void Run()
						{
						}

						[Log]
						internal static void Ping()
						{
						}

						public static void Start()
						{
							Run();
							Ping();
						}
					}
				}
				""");

			var notApplied = result.Diagnostics.Where(static d => d.Id == AspectDiagnosticID.AspectNotAppliedToMethod).ToList();

			Assert.AreEqual(1, notApplied.Count, DescribeDiagnostics(result));
			Assert.AreEqual(DiagnosticSeverity.Warning, notApplied[0].Severity);
			StringAssert.Contains(notApplied[0].GetMessage(), "Run()");
			Assert.AreEqual(0, CountIntercepted(result, "Run()"));
			Assert.AreEqual(1, CountIntercepted(result, "Ping()"));

			// Build only: a design-time build does not select.
			var designTime = RunSelection(
				"""
				namespace AspectGenerator.Tests.GeneratorDriver
				{
					static class Calls
					{
						[Log(TargetFilter = "public")]
						static void Run()
						{
						}
					}
				}
				""",
				new() { ["build_property.DesignTimeBuild"] = "true" });

			AssertNoDiagnostic(designTime, AspectDiagnosticID.AspectNotAppliedToMethod);
		}

		[TestMethod]
		public void NameAnchorsLimitTheCallsBoundTest()
		{
			var reportFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "report.txt");

			try
			{
				var result = RunSelection(
					"""
					[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "type: *Builder and method: Append*")]

					namespace AspectGenerator.Tests.GeneratorDriver
					{
						static class Calls
						{
							public static string Run()
							{
								var builder = new System.Text.StringBuilder();
								var list    = new System.Collections.Generic.List<int>();

								builder.Append("a");
								builder.AppendLine("b");
								builder.Insert(0, "c");
								list.Add(1);
								System.Console.WriteLine("d");
								"e".Trim();

								return builder.ToString();
							}
						}
					}
					""",
					new()
					{
						[$"build_property.AspectGenerator{AspectOptionName.ReportFile}"] = reportFile,
						[$"build_property.AspectGenerator{AspectOptionName.Trace}"]      = "true",
					});

				var report = File.ReadAllText(reportFile);

				// Seven calls and the aspect's nameof(Before); only the two whose name begins with 'Append' are bound.
				StringAssert.Contains(report, "| Invocation candidates before prefilter | 8 |");
				StringAssert.Contains(report, "| Invocation candidates after prefilter | 2 |");
				StringAssert.Contains(report, "| Symbol resolution attempts | 2 |");
				StringAssert.Contains(report, "name Append*");
				Assert.AreEqual(1, CountIntercepted(result, "builder.Append(\"a\")"),     DescribeDiagnostics(result));
				Assert.AreEqual(1, CountIntercepted(result, "builder.AppendLine(\"b\")"), DescribeDiagnostics(result));
			}
			finally
			{
				DeleteReportDirectory(reportFile);
			}
		}

		[TestMethod]
		public void AssemblyFilterWithoutAPlaceBindsOnlyProjectCallsTest()
		{
			var reportFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "report.txt");

			try
			{
				var result = RunSelection(
					"""
					[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "public")]

					namespace AspectGenerator.Tests.GeneratorDriver
					{
						static class Calls
						{
							public static void Save()
							{
							}

							public static void Run()
							{
								Save();
								System.Console.WriteLine("d");
								"e".Trim();
							}
						}
					}
					""",
					new()
					{
						[$"build_property.AspectGenerator{AspectOptionName.ReportFile}"] = reportFile,
						[$"build_property.AspectGenerator{AspectOptionName.Trace}"]      = "true",
					});

				var report = File.ReadAllText(reportFile);

				StringAssert.Contains(report, "| Invocation candidates after prefilter | 1 |");
				Assert.AreEqual(1, CountIntercepted(result, "Save()"), DescribeDiagnostics(result));
				Assert.AreEqual(0, CountIntercepted(result, "System.Console.WriteLine(\"d\")"));
			}
			finally
			{
				DeleteReportDirectory(reportFile);
			}
		}

		const string SelectionLibrarySource =
			"""
			using System;
			using AspectGenerator;

			[assembly: SelectionLib.LibLog(TargetFilter = "static", Level = 7)]

			namespace SelectionLib
			{
				[Aspect(OnBeforeCall = nameof(Before))]
				[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
				public sealed class LibLogAttribute : Attribute
				{
					public string? TargetFilter { get; set; }
					public int     Level        { get; set; }

					public static void Before(InterceptInfo info)
					{
					}
				}

				public class Service
				{
					[LibLog]
					public void Write()
					{
					}

					[LibLog]
					public static void Read()
					{
					}

					public static void Plain()
					{
					}
				}
			}
			""";

		const string SelectionLibraryConsumerSource =
			"""
			/*ASSEMBLY*/

			static class Calls
			{
				public static void Run()
				{
					new SelectionLib.Service().Write();
					SelectionLib.Service.Read();
					SelectionLib.Service.Plain();
				}
			}
			""";

		static MetadataReference CompileSelectionLibrary(string source, bool metadataOnly)
		{
			var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview).WithFeatures([new("InterceptorsNamespaces", "AspectGenerator")]);
			var compilation  = CSharpCompilation.Create(
				"SelectionLib",
				[CSharpSyntaxTree.ParseText(source, parseOptions, "SelectionLib.cs")],
				GetMetadataReferences(),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

			var properties = new Dictionary<string,string>
			{
				["build_property.InterceptorsNamespaces"]       = "AspectGenerator",
				["build_property.AspectGeneratorPropsImported"] = "true",
				["build_property.AspectGeneratorPublicApi"]     = "true",
			};

			GeneratorDriver driver = CSharpGeneratorDriver.Create(
				[new AspectSourceGenerator().AsSourceGenerator()],
				parseOptions: parseOptions,
				optionsProvider: new TestAnalyzerConfigOptionsProvider(properties));

			driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

			using var stream = new MemoryStream();

			var emit = output.Emit(stream, options: new EmitOptions(metadataOnly: metadataOnly));

			Assert.IsTrue(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));

			return MetadataReference.CreateFromImage(stream.ToArray());
		}

		[TestMethod]
		public void LibraryFamilyDecidesAMethodTheLibraryMarkedTest()
		{
			foreach (var metadataOnly in new[] { false, true })
			{
				var library = CompileSelectionLibrary(SelectionLibrarySource, metadataOnly);
				var kind    = metadataOnly ? "reference assembly" : "implementation assembly";

				// The library's own chain selects the static Read; Write is left to the consumer's filters, which say nothing.
				var bare = RunGenerator([SelectionLibraryConsumerSource.Replace("/*ASSEMBLY*/", "", StringComparison.Ordinal)], reference: library);

				Assert.AreEqual(1, CountIntercepted(bare, "SelectionLib.Service.Read()"),     kind + Environment.NewLine + DescribeDiagnostics(bare));
				Assert.AreEqual(0, CountIntercepted(bare, "new SelectionLib.Service().Write()"), kind);
				Assert.AreEqual(0, CountIntercepted(bare, "SelectionLib.Service.Plain()"),      kind);

				// The consumer's assembly filter decides Write as any referenced method; it does not change Read.
				var consumer = RunGenerator(
					[SelectionLibraryConsumerSource.Replace("/*ASSEMBLY*/", "[assembly: SelectionLib.LibLog(TargetFilter = \"method: Write or Read\", Level = 3)]", StringComparison.Ordinal)],
					reference: library);

				Assert.AreEqual(1, CountIntercepted(consumer, "SelectionLib.Service.Read()"),       kind + Environment.NewLine + DescribeDiagnostics(consumer));
				Assert.AreEqual(1, CountIntercepted(consumer, "new SelectionLib.Service().Write()"), kind + Environment.NewLine + DescribeDiagnostics(consumer));
				Assert.AreEqual(0, CountIntercepted(consumer, "SelectionLib.Service.Plain()"),      kind);
				AssertNoDiagnostic(consumer, AspectDiagnosticID.AspectSettingsConflict);
			}
		}

		[TestMethod]
		public void UnreadableFilterOfAReferenceTurnsTheAspectOffTest()
		{
			foreach (var metadataOnly in new[] { false, true })
			{
				var library = CompileSelectionLibrary(SelectionLibrarySource.Replace("TargetFilter = \"static\"", "TargetFilter = \"contains: Read\"", StringComparison.Ordinal), metadataOnly);
				var kind    = metadataOnly ? "reference assembly" : "implementation assembly";

				var result = RunGenerator(
					[
						SelectionLibraryConsumerSource.Replace("/*ASSEMBLY*/", "[assembly: SelectionLib.LibLog(TargetFilter = \"method: Own\")]", StringComparison.Ordinal),
						"""
						static class Mine
						{
							public static void Own()
							{
							}

							public static void Call()
							{
								Own();
							}
						}
						""",
					],
					reference: library);

				var unreadable = result.Diagnostics.Where(static d => d.Id == AspectDiagnosticID.UnreadableReferencedFilter).ToList();

				Assert.AreEqual(1, unreadable.Count, kind + Environment.NewLine + DescribeDiagnostics(result));
				Assert.AreEqual(DiagnosticSeverity.Error, unreadable[0].Severity, kind);
				StringAssert.Contains(unreadable[0].GetMessage(), "'SelectionLib'", kind);
				Assert.AreEqual(0, CountIntercepted(result, "SelectionLib.Service.Read()"), kind);
				Assert.AreEqual(0, CountIntercepted(result, "Own()"), kind);
			}
		}
	}
}
