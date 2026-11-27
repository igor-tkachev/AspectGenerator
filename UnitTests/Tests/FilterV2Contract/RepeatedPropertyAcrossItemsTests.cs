using System;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// A <c>string[]</c> filter is read as one filter: a property written in two of its items is included twice, which is an
	/// error at the second item, with the one-line spelling of what the author meant; the aspect then selects nothing.
	/// </summary>
	public partial class CompilerTests
	{
		const string ArrayLogAspectSource =
			"""
			using System;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver
			{
				[Aspect(OnBeforeCall = nameof(Before))]
				[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
				sealed class ArrayLogAttribute : Attribute
				{
					public string[]? TargetFilter { get; set; }

					public static void Before(InterceptInfo info)
					{
					}
				}
			}
			""";

		static Microsoft.CodeAnalysis.GeneratorDriverRunResult RunArrayLog(string items)
		{
			return RunGenerator([ArrayLogAspectSource, RepeatedPropertySource.Replace("[ITEMS]", "new[] { " + items + " }", StringComparison.Ordinal)]);
		}

		const string RepeatedPropertySource =
			"""
			[assembly: AspectGenerator.Tests.GeneratorDriver.ArrayLog(TargetFilter = [ITEMS])]

			namespace AspectGenerator.Tests.GeneratorDriver
			{
				static class Calls
				{
					public static void Save() { }
					public static void Load() { }

					public static void Run()
					{
						Save();
						Load();
					}
				}
			}
			""";

		[TestMethod]
		public void APropertyRepeatedAcrossTheItemsOfAnArrayIsReportedAtTheSecondItemTest()
		{
			foreach (var (items, second, hint) in new[]
			{
				("\"method: Save\", \"method: Load\"",           "method: Load", "Write them as one line: 'method: Save or method: Load' (either) or 'method: Save and method: Load' (both)."),
				("\"public\", \"static\", \"public\"",           "public",       "'public or public'"),
				("\"type: Calls\", \"method: Save\", \"type: Calls\"", "type: Calls", "'type: Calls or type: Calls'"),
				("\"returns: void\", \"returns: int\"",          "returns: int", "'returns: void or returns: int'"),
			})
			{
				var source = RepeatedPropertySource.Replace("[ITEMS]", "new[] { " + items + " }", StringComparison.Ordinal);
				var result = RunArrayLog(items);
				var found  = result.Diagnostics.Where(static d => d.Id == AspectDiagnosticID.FilterOldSyntaxOrRepeatedProperty).ToList();

				Assert.AreEqual(1, found.Count, items + Environment.NewLine + DescribeDiagnostics(result));
				Assert.AreEqual(DiagnosticSeverity.Error, found[0].Severity, items);

				// At the characters of the second item, inside its literal; the first item is not reported.
				var location = found[0].Location;

				Assert.IsTrue(location.IsInSource, items);
				Assert.AreEqual(second, location.SourceTree!.GetText().ToString(location.SourceSpan), items);
				Assert.IsTrue(location.SourceSpan.Start > source.LastIndexOf('{', source.IndexOf(second, StringComparison.Ordinal)), items);

				// With the spelling that says it in one line.
				StringAssert.Contains(found[0].GetMessage(), hint, items);

				// The aspect selects nothing while a filter has an error.
				Assert.AreEqual(0, CountIntercepted(result, "Save()"), items);
				Assert.AreEqual(0, CountIntercepted(result, "Load()"), items);
			}
		}

		[TestMethod]
		public void TheRewriteTheHintSaysIsAFilterThatSelectsTest()
		{
			// `either` and `both` are the two readings of what was meant.
			var either = RunArrayLog("\"method: Save or method: Load\"");

			Assert.AreEqual(1, CountIntercepted(either, "Save()"), DescribeDiagnostics(either));
			Assert.AreEqual(1, CountIntercepted(either, "Load()"), DescribeDiagnostics(either));

			var both = RunArrayLog("\"method: Save and method: Load\"");

			Assert.AreEqual(0, CountIntercepted(both, "Save()"), DescribeDiagnostics(both));
			Assert.AreEqual(0, CountIntercepted(both, "Load()"), DescribeDiagnostics(both));
		}
	}
}
