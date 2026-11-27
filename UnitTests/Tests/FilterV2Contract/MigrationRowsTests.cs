using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// One end-to-end test for each row of the table that tells an author of the earlier language what a filter means now:
	/// the filter as it is written today, and the calls it selects. A row that is already pinned by a test of the selection
	/// or of the filters of a method is named in the comment of its place below, and not repeated:
	/// <list type="bullet">
	/// <item>a same-key line joined the latest group of its key (<c>a and (b or c)</c>): <c>OrGroupInsideAndOnOneKeyIsSelectedByEitherBranchTest</c>;</item>
	/// <item>a filter on a method was ignored: <c>AspectWrittenOnAMethodThatIsNotAppliedIsReportedTest</c> and <c>MethodLevelFilterThatDoesNotSelectTheMethodReportsNotAppliedTest</c>;</item>
	/// <item>settings from the most specific attribute, conflicts as errors: <c>NamedArgumentOfTheMoreSpecificLevelIsTheOneBuiltTest</c>, <c>ConflictingSettingsAreReportedAndTheMethodKeepsTheAspectTest</c>;</item>
	/// <item>library type and assembly filters apply: <c>LibraryFamilyTypeAndAssemblyFiltersApplyToTheMethodsTheLibraryMarkedTest</c>;</item>
	/// <item>generic methods of the project are judged per call: <c>GenericMethodIsDecidedPerCallTest</c> and <c>GenericMethodDeclaredInTheProjectIsJudgedPerCallTest</c>.</item>
	/// </list>
	/// </summary>
	public partial class CompilerTests
	{
		const string MigrationTemplate =
			"""
			using System;
			using AspectGenerator;
			ATTRIBUTES

			namespace AspectGenerator.Tests.GeneratorDriver
			{
				DECLARATIONS

				static class Calls
				{
					public static void Run()
					{
						CALLS
					}
				}
			}
			""";

		/// <summary>An aspect with a default filter and a marker attribute, for the rows about defaults and about attributes.</summary>
		static string MigrationAspect(string defaultFilter)
		{
			return
				"""
				using System;
				using AspectGenerator;

				namespace AspectGenerator.Tests.GeneratorDriver
				{
					[Aspect(OnBeforeCall = nameof(Before), DefaultTargetFilter = DEFAULT)]
					[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
					sealed class LogAttribute : Attribute
					{
						public string? TargetFilter { get; set; }

						public static void Before(InterceptInfo info)
						{
						}
					}

					[AttributeUsage(AttributeTargets.Method)]
					sealed class NoLogAttribute : Attribute
					{
					}
				}
				""".Replace("DEFAULT", Quote(defaultFilter), StringComparison.Ordinal);
		}

		static string Quote(string text)
		{
			return "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
		}

		static string LogOnAssembly(string filter)
		{
			return "[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = " + Quote(filter) + ")]";
		}

		/// <summary>The calls, among <paramref name="calls"/>, that the aspect of <paramref name="aspect"/> is applied to, in the order given; the diagnostics of the run go to <paramref name="diagnostics"/>.</summary>
		static string Migrated(string attributes, string declarations, string[] calls, out string diagnostics, string? aspect = null)
		{
			var body   = string.Join("\n\t\t\t\t\t", calls.Select(static c => c + ";"));
			var source = MigrationTemplate
				.Replace("ATTRIBUTES", attributes, StringComparison.Ordinal)
				.Replace("DECLARATIONS", declarations, StringComparison.Ordinal)
				.Replace("CALLS", body, StringComparison.Ordinal);

			var result = RunGenerator([aspect ?? SelectionLogAspectSource, source]);

			diagnostics = DescribeDiagnostics(result);

			return string.Join(", ", calls.Where(c => CountIntercepted(result, c) > 0));
		}

		static void AssertMigrated(string filter, string declarations, string[] calls, params string[] expected)
		{
			var selected = Migrated(LogOnAssembly(filter), declarations, calls, out var diagnostics);

			Assert.AreEqual(string.Join(", ", expected), selected, filter + Environment.NewLine + diagnostics);
		}

		const string SaveLoadDeclarations =
			"""
			static class Shop
			{
				public static void Save() { }
				public static void SaveAll() { }
				public static void Get() { }
				public static void Load() { }
			}
			""";

		static readonly string[] SaveLoadCalls = ["Shop.Save()", "Shop.SaveAll()", "Shop.Get()", "Shop.Load()"];

		// ---- the removed syntax -------------------------------------------------------------------------------------

		[TestMethod]
		public void MigrationRowRemovedKeysAndOperatorsAreReportedWithTheVersion2SpellingTest()
		{
			foreach (var (text, spelling) in new[]
			{
				("contains: Save",             "'namespace:', 'type:' and 'method:' patterns"),
				("regex: ^S",                  "'namespace:', 'type:' and 'method:' patterns"),
				("signature: Save()",          "write the compact signature without it: 'Save()'"),
				("pattern: Save*",             "write the compact signature without it: 'Save*'"),
				("fulltype: A.B",              "write 'type: global::A.B'"),
				("fullmethod: A.B.C",          "write 'method: global::A.B.C'"),
				("path: A.B",                  "write 'type: global::A.B'"),
				("attributes: NoLog",          "write 'attribute: NoLog'"),
				("method: Save; public",       "write 'and'"),
				("public & static",            "write 'and'"),
				("public | static",            "write 'or'"),
				("Save(...)",                  "write '..' for any run of parameters"),
				("method: a\\*",               "write exact text in quotes"),
				("method: Save\n& method: Load", "continue the line before with 'and'"),
			})
			{
				var selected = Migrated(LogOnAssembly(text), SaveLoadDeclarations, SaveLoadCalls, out var diagnostics);

				// The aspect selects nothing, and the one error names the way to write it today.
				Assert.AreEqual("", selected, text + Environment.NewLine + diagnostics);
				StringAssert.Contains(diagnostics, AspectDiagnosticID.FilterOldSyntaxOrRepeatedProperty, text);
				StringAssert.Contains(diagnostics, spelling, text);
			}
		}

		[TestMethod]
		public void MigrationRowTwoLinesWithTheSameKeyAreOneDeclarationTest()
		{
			// Two lines of one key used to be alternatives; a property is included once now.
			var diagnostics = "";
			var selected    = Migrated(LogOnAssembly("method: Save\nmethod: Load"), SaveLoadDeclarations, SaveLoadCalls, out diagnostics);

			Assert.AreEqual("", selected, diagnostics);
			StringAssert.Contains(diagnostics, "Write them as one line: 'method: Save or method: Load'");

			AssertMigrated("method: Save* or Get*", SaveLoadDeclarations, SaveLoadCalls, "Shop.Save()", "Shop.SaveAll()", "Shop.Get()");
		}

		// ---- no order ------------------------------------------------------------------------------------------------

		[TestMethod]
		public void MigrationRowRulesHaveNoOrderTest()
		{
			// The last rule used to decide. A subtraction takes its methods out whatever its place among the lines.
			foreach (var items in new[] { "\"method: Save*\", \"- method: SaveAll\"", "\"- method: SaveAll\", \"method: Save*\"" })
			{
				var selected = Migrated(
					"[assembly: AspectGenerator.Tests.GeneratorDriver.ArrayLog(TargetFilter = new[] { " + items + " })]",
					SaveLoadDeclarations,
					SaveLoadCalls,
					out var diagnostics,
					ArrayLogAspectSource);

				Assert.AreEqual("Shop.Save()", selected, items + Environment.NewLine + diagnostics);
			}
		}

		// ---- the aspect attribute on a method ----------------------------------------------------------------------

		[TestMethod]
		public void MigrationRowAnAspectAttributeOnAMethodIsFilteredLikeAnyAttributeTest()
		{
			// [Log] [NoLog] under a default that leaves out [NoLog]: not logged, and the build says the attribute is not applied.
			var selected = Migrated(
				"",
				"""
				static class Shop
				{
					[Log]
					[NoLog]
					public static void Skipped() { }

					[Log]
					public static void Kept() { }
				}
				""",
				["Shop.Skipped()", "Shop.Kept()"],
				out var diagnostics,
				MigrationAspect("- attribute: NoLog"));

			Assert.AreEqual("Shop.Kept()", selected, diagnostics);
			StringAssert.Contains(diagnostics, AspectDiagnosticID.AspectNotAppliedToMethod);
		}

		// ---- levels --------------------------------------------------------------------------------------------------

		[TestMethod]
		public void MigrationRowATypeFilterReplacesTheAssemblyFilterForItsMethodsTest()
		{
			const string declarations =
				"""
				static class Other
				{
					public static void Save() { }
				}

				[Log(TargetFilter = FILTER)]
				static class Shop
				{
					public static void Save() { }
					public static void Load() { }
				}
				""";

			// The assembly says Save, the type says Load: the type's methods are judged by the type's own filter.
			var selected = Migrated(LogOnAssembly("method: Save"), declarations.Replace("FILTER", Quote("method: Load"), StringComparison.Ordinal), ["Other.Save()", "Shop.Save()", "Shop.Load()"], out var diagnostics);

			Assert.AreEqual("Other.Save(), Shop.Load()", selected, diagnostics);

			// Both, spelled with a reference to the level above.
			selected = Migrated(LogOnAssembly("method: Save"), declarations.Replace("FILTER", Quote("method: Load or assembly.method"), StringComparison.Ordinal), ["Other.Save()", "Shop.Save()", "Shop.Load()"], out diagnostics);

			Assert.AreEqual("Other.Save(), Shop.Save(), Shop.Load()", selected, diagnostics);
		}

		[TestMethod]
		public void MigrationRowTextThatWasReadWithoutAnErrorIsAnErrorNowTest()
		{
			foreach (var (text, id) in new[]
			{
				(";",             AspectDiagnosticID.FilterSyntaxError),
				("pub:lic",       AspectDiagnosticID.FilterUnknownKeyOrWord),
				("method: Save<", AspectDiagnosticID.FilterSyntaxError),
				("method: Sav?",  AspectDiagnosticID.FilterInvalidPattern),
			})
			{
				var selected = Migrated(LogOnAssembly(text), SaveLoadDeclarations, SaveLoadCalls, out var diagnostics);

				Assert.AreEqual("", selected, text + Environment.NewLine + diagnostics);
				StringAssert.Contains(diagnostics, id + ":", text);
			}
		}

		[TestMethod]
		public void MigrationRowSubtractionsStartFromTheDefaultOfTheLevelTest()
		{
			// A filter of subtractions only selected nothing; it selects every method of the project but what it takes out.
			var selected = Migrated(
				LogOnAssembly("- attribute: NoLog"),
				"""
				static class Shop
				{
					[NoLog]
					public static void Skipped() { }

					public static void Kept() { }
				}
				""",
				["Shop.Skipped()", "Shop.Kept()", "Console.WriteLine(\"d\")"],
				out var diagnostics,
				MigrationAspect(""));

			Assert.AreEqual("Shop.Kept()", selected, diagnostics);
		}

		[TestMethod]
		public void MigrationRowAnAssemblyFilterWithoutAPlaceSelectsOnlyTheProjectTest()
		{
			// `public` reached methods of other assemblies too; it stays in the project until a place or origin says otherwise.
			AssertMigrated("public", SaveLoadDeclarations, ["Shop.Save()", "Console.WriteLine(\"d\")"], "Shop.Save()");
			AssertMigrated("public\norigin: any", SaveLoadDeclarations, ["Shop.Save()", "Console.WriteLine(\"d\")"], "Shop.Save()", "Console.WriteLine(\"d\")");
			AssertMigrated("returns: void", SaveLoadDeclarations, ["Shop.Save()", "Console.WriteLine(\"d\")"], "Shop.Save()");
		}

		[TestMethod]
		public void MigrationRowNamesMatchTrailingWholeSegmentsTest()
		{
			const string declarations =
				"""
				class Order { public void Save() { } }
				class MyOrder { public void Save() { } }
				""";

			var calls = new[] { "new Order().Save()", "new MyOrder().Save()" };

			// `Order` is the whole last segment of the name of the first; the second is another name.
			AssertMigrated("type: Order", declarations, calls, "new Order().Save()");
			AssertMigrated("type: GeneratorDriver.Order", declarations, calls, "new Order().Save()");

			// `global::` roots the name: no type of that name stands in the global namespace.
			AssertMigrated("type: global::Order", declarations, calls);
			AssertMigrated("type: global::AspectGenerator.Tests.GeneratorDriver.Order", declarations, calls, "new Order().Save()");
		}

		[TestMethod]
		public void MigrationRowAccessibilityWordsAreExactTest()
		{
			const string declarations =
				"""
				class Access
				{
					private protected void PrivateProtected() { }
					protected internal void ProtectedInternal() { }
					private void Private() { }
					protected void Protected() { }
					internal void Internal() { }

					public void Run()
					{
						PrivateProtected();
						ProtectedInternal();
						Private();
						Protected();
						Internal();
					}
				}
				""";

			var calls = new[] { "PrivateProtected()", "ProtectedInternal()", "Private()", "Protected()", "Internal()" };

			// What a filter selects and the generated class cannot reach (private and protected members) is counted by AG0007;
			// what it can reach is intercepted.
			string Selected(string filter)
			{
				var source = MigrationTemplate
					.Replace("ATTRIBUTES", LogOnAssembly(filter), StringComparison.Ordinal)
					.Replace("DECLARATIONS", declarations, StringComparison.Ordinal)
					.Replace("CALLS", "new Access().Run();", StringComparison.Ordinal);

				var result       = RunGenerator([SelectionLogAspectSource, source]);
				var intercepted  = string.Join(", ", calls.Where(c => CountIntercepted(result, c) > 0));
				var notReachable = result.Diagnostics.Where(static d => d.Id == AspectDiagnosticID.FilterSelectedCallsNotIntercepted).Select(static d => d.GetMessage().Split(' ')[0]).SingleOrDefault() ?? "0";

				return $"{intercepted} | not reachable: {notReachable}";
			}

			// `private` used to select `private protected` too; `protected` and `internal` also selected `protected internal`.
			Assert.AreEqual(" | not reachable: 1",                     Selected("private"));
			Assert.AreEqual(" | not reachable: 1",                     Selected("protected"));
			Assert.AreEqual("Internal() | not reachable: 0",           Selected("internal"));
			Assert.AreEqual(" | not reachable: 0",                     Selected("protected and internal"));
			Assert.AreEqual("ProtectedInternal() | not reachable: 0",  Selected("protected internal"));
			Assert.AreEqual(" | not reachable: 1",                     Selected("private protected"));
		}

		[TestMethod]
		public void MigrationRowAMethodNameMatchesAnyArityAndATypeParameterNameIsOrdinaryTest()
		{
			const string declarations =
				"""
				static class Gen
				{
					public static int Load(int value) { return value; }
					public static T Load<T>(T value) { return value; }
				}
				""";

			var calls = new[] { "Gen.Load(1)", "Gen.Load(\"x\")" };

			AssertMigrated("method: Load",    declarations, calls, "Gen.Load(1)", "Gen.Load(\"x\")");
			AssertMigrated("method: Load<*>",  declarations, calls, "Gen.Load(\"x\")");
			AssertMigrated("method: Load<T>",  declarations, calls);
		}

		[TestMethod]
		public void MigrationRowTypePatternsAreStructuralTest()
		{
			const string declarations =
				"""
				static class Shapes
				{
					public static string Str() { return ""; }
					public static int[] Arr() { return null!; }
					public static int[,] Arr2() { return null!; }
					public static (int, string) Tup() { return default; }
					public static int Num() { return 0; }
					public static int? NumOrNull() { return null; }
				}
				""";

			var calls = new[] { "Shapes.Str()", "Shapes.Arr()", "Shapes.Arr2()", "Shapes.Tup()", "Shapes.Num()", "Shapes.NumOrNull()" };

			// `*` is any type; arrays and tuples are written as what they are.
			AssertMigrated("method: Shapes.* and returns: *",          declarations, calls, "Shapes.Str()", "Shapes.Arr()", "Shapes.Arr2()", "Shapes.Tup()", "Shapes.Num()", "Shapes.NumOrNull()");
			AssertMigrated("method: Shapes.* and returns: *[]",        declarations, calls, "Shapes.Arr()");
			AssertMigrated("method: Shapes.* and returns: (int, string)", declarations, calls, "Shapes.Tup()");

			// An array carries its rank.
			AssertMigrated("method: Shapes.* and returns: int[]",      declarations, calls, "Shapes.Arr()");
			AssertMigrated("method: Shapes.* and returns: int[,]",     declarations, calls, "Shapes.Arr2()");

			// `int?` is Nullable<int>, and `?` is nothing else.
			AssertMigrated("method: Shapes.* and returns: int?",       declarations, calls, "Shapes.NumOrNull()");
			AssertMigrated("method: Shapes.* and returns: int",        declarations, calls, "Shapes.Num()");
			AssertMigrated("method: Shapes.* and returns: string?",    declarations, calls, "Shapes.Str()");
		}

		[TestMethod]
		public void MigrationRowTuplesGenericAttributesAndAttributeNamesWithoutTheSuffixMatchTest()
		{
			const string declarations =
				"""
				sealed class GenericMarkerAttribute<T> : Attribute { }

				static class Marked
				{
					[Obsolete]
					public static void Old() { }

					[GenericMarker<int>]
					public static void Generic() { }

					public static void Plain() { }
				}
				""";

			var calls = new[] { "Marked.Old()", "Marked.Generic()", "Marked.Plain()" };

			AssertMigrated("attribute: System.Obsolete",     declarations, calls, "Marked.Old()");
			AssertMigrated("attribute: Obsolete",            declarations, calls, "Marked.Old()");
			AssertMigrated("attribute: ObsoleteAttribute",   declarations, calls, "Marked.Old()");
			AssertMigrated("attribute: GenericMarker<int>",  declarations, calls, "Marked.Generic()");
			AssertMigrated("attribute: GenericMarker<*>",    declarations, calls, "Marked.Generic()");
		}

		[TestMethod]
		public void MigrationRowExternCoversRuntimeImplementedMethodsTest()
		{
			// Math.Sqrt has no body to read: the runtime implements it.
			AssertMigrated("extern\norigin: any", SaveLoadDeclarations, ["Shop.Save()", "System.Math.Sqrt(4.0)"], "System.Math.Sqrt(4.0)");
		}

		[TestMethod]
		public void MigrationRowTwoParameterConditionsMayBeMetByTwoParametersTest()
		{
			const string declarations =
				"""
				static class Args
				{
					public static void Two(int a, string b) { }
					public static void OneInt(int a) { }
					public static void OneString(string b) { }
				}
				""";

			AssertMigrated("param: int and param: string", declarations, ["Args.Two(1, \"x\")", "Args.OneInt(1)", "Args.OneString(\"x\")"], "Args.Two(1, \"x\")");
		}

		[TestMethod]
		public void MigrationRowTheDefaultFilterIsTheWeakestLevelTest()
		{
			// A later rule used to be able to override a default one. Now a declaration of the same property replaces it.
			var selected = Migrated(
				LogOnAssembly("method: Load*"),
				"""
				static class Shop
				{
					public static void SaveIt() { }
					public static void LoadIt() { }
				}
				""",
				["Shop.SaveIt()", "Shop.LoadIt()"],
				out var diagnostics,
				MigrationAspect("method: Save*"));

			Assert.AreEqual("Shop.LoadIt()", selected, diagnostics);

			// Without a declaration of the property, the default stands.
			selected = Migrated(
				LogOnAssembly("static"),
				"""
				static class Shop
				{
					public static void SaveIt() { }
					public static void LoadIt() { }
				}
				""",
				["Shop.SaveIt()", "Shop.LoadIt()"],
				out diagnostics,
				MigrationAspect("method: Save*"));

			Assert.AreEqual("Shop.SaveIt()", selected, diagnostics);
		}
	}
}
