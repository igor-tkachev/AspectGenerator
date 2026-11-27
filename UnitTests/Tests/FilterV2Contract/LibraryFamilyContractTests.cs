using System;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// Aspects a library applies to its own methods, through real metadata: the library compiled twice, as an implementation
	/// assembly and as a reference assembly (metadata only), and a consumer that references each. The library's own assembly
	/// and type filters decide the methods it marked, with its own settings; a method they do not select is left to the
	/// consumer's assembly filter like any referenced method, which reaches it only by naming a place; a filter the library
	/// holds that cannot be read turns the aspect off and is reported as an error naming the library. The tests of the
	/// selection (<c>LibraryFamilyDecidesAMethodTheLibraryMarkedTest</c>, <c>UnreadableFilterOfAReferenceTurnsTheAspectOffTest</c>)
	/// hold the plain cases.
	/// </summary>
	public partial class CompilerTests
	{
		const string LibraryFamilySource =
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
				}

				// The type's own filter replaces the assembly's `static` for its methods, and so does its level.
				[LibLog(TargetFilter = "instance", Level = 5)]
				public class Typed
				{
					[LibLog]
					public void MarkInstance()
					{
					}

					[LibLog]
					public static void MarkStatic()
					{
					}

					[LibLog]
					public static void Other()
					{
					}
				}
			}
			""";

		const string LibraryFamilyConsumerSource =
			"""
			/*ASSEMBLY*/

			static class Calls
			{
				public static void Run()
				{
					new SelectionLib.Service().Write();
					SelectionLib.Service.Read();
					new SelectionLib.Typed().MarkInstance();
					SelectionLib.Typed.MarkStatic();
					SelectionLib.Typed.Other();
				}
			}

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
			""";

		static readonly string[] LibraryFamilyCalls =
		[
			"new SelectionLib.Service().Write()",
			"SelectionLib.Service.Read()",
			"new SelectionLib.Typed().MarkInstance()",
			"SelectionLib.Typed.MarkStatic()",
			"SelectionLib.Typed.Other()",
			"Own()",
		];

		[TestMethod]
		public void LibraryFamilyTypeAndAssemblyFiltersApplyToTheMethodsTheLibraryMarkedTest()
		{
			foreach (var metadataOnly in new[] { false, true })
			{
				var kind    = metadataOnly ? "reference assembly" : "implementation assembly";
				var library = CompileSelectionLibrary(LibraryFamilySource, metadataOnly);

				// The assembly filter `static` selects the static Read; the type's `instance` replaces it for Typed, so its
				// instance method is selected and its static ones are not. What is not selected is left to the consumer, whose
				// filters say nothing.
				var selected = LibraryFamilySelectedWith(library, "", out var result);

				Assert.AreEqual("SelectionLib.Service.Read(), new SelectionLib.Typed().MarkInstance()", selected, kind + Environment.NewLine + DescribeDiagnostics(result));

				// Each is built with the settings of the library's attribute that decided it: the assembly's for Read, the type's for MarkInstance.
				var interceptors = GetGeneratedSource(result, "Interceptors.g.cs");

				Assert.AreEqual(1, CountOccurrences(interceptors, "{ Level = 7 }"), kind + Environment.NewLine + interceptors);
				Assert.AreEqual(1, CountOccurrences(interceptors, "{ Level = 5 }"), kind + Environment.NewLine + interceptors);
				AssertNoDiagnostic(result, AspectDiagnosticID.AspectSettingsConflict);
			}
		}

		[TestMethod]
		public void AMethodTheLibraryChainDoesNotSelectFallsBackToTheConsumerAndTheProjectOnlyDefaultHoldsTest()
		{
			foreach (var metadataOnly in new[] { false, true })
			{
				var kind    = metadataOnly ? "reference assembly" : "implementation assembly";
				var library = CompileSelectionLibrary(LibraryFamilySource, metadataOnly);
				var library2 = "[assembly: SelectionLib.LibLog(TargetFilter = \"FILTER\", Level = 3)]";

				// A filter without a place reaches the project only: `public` does not reach the library's Write, MarkStatic or Other.
				var selected = LibraryFamilySelectedWith(library, library2.Replace("FILTER", "public", StringComparison.Ordinal), out var result);

				Assert.AreEqual("SelectionLib.Service.Read(), new SelectionLib.Typed().MarkInstance(), Own()", selected, kind + Environment.NewLine + DescribeDiagnostics(result));

				// A method pattern that names the library does.
				selected = LibraryFamilySelectedWith(library, library2.Replace("FILTER", "method: SelectionLib.**.Write", StringComparison.Ordinal), out result);

				Assert.AreEqual("new SelectionLib.Service().Write(), SelectionLib.Service.Read(), new SelectionLib.Typed().MarkInstance()", selected, kind + Environment.NewLine + DescribeDiagnostics(result));

				// So does a namespace that no other assembly declares.
				selected = LibraryFamilySelectedWith(library, library2.Replace("FILTER", "namespace: SelectionLib.**", StringComparison.Ordinal), out result);

				Assert.AreEqual("new SelectionLib.Service().Write(), SelectionLib.Service.Read(), new SelectionLib.Typed().MarkInstance(), SelectionLib.Typed.MarkStatic(), SelectionLib.Typed.Other()", selected, kind + Environment.NewLine + DescribeDiagnostics(result));

				// The consumer's level does not change what the library's chain selected: Read keeps the library's settings.
				var interceptors = GetGeneratedSource(result, "Interceptors.g.cs");

				StringAssert.Contains(interceptors, "{ Level = 7 }", kind);
				StringAssert.Contains(interceptors, "{ Level = 3 }", kind);
				StringAssert.Contains(interceptors, "{ Level = 5 }", kind);
			}
		}

		static string LibraryFamilySelectedWith(MetadataReference library, string consumerAttributes, out GeneratorDriverRunResult result)
		{
			result = RunGenerator([LibraryFamilyConsumerSource.Replace("/*ASSEMBLY*/", consumerAttributes, StringComparison.Ordinal)], reference: library);

			var run = result;

			return string.Join(", ", LibraryFamilyCalls.Where(c => CountIntercepted(run, c) > 0));
		}

		[TestMethod]
		public void ALibraryFilterInVersion11SyntaxIsReportedNamingTheLibraryAndTheAspectSelectsNothingTest()
		{
			foreach (var metadataOnly in new[] { false, true })
			{
				foreach (var (replaced, where) in new[]
				{
					("[assembly: SelectionLib.LibLog(TargetFilter = \"static\", Level = 7)]", "assembly filter"),
					("[LibLog(TargetFilter = \"instance\", Level = 5)]",                       "type filter"),
				})
				{
					var kind    = (metadataOnly ? "reference assembly" : "implementation assembly") + ", " + where;
					var broken  = replaced.Replace("\"static\"", "\"contains: Read\"", StringComparison.Ordinal).Replace("\"instance\"", "\"contains: Mark\"", StringComparison.Ordinal);
					var library = CompileSelectionLibrary(LibraryFamilySource.Replace(replaced, broken, StringComparison.Ordinal), metadataOnly);

					var selected = LibraryFamilySelectedWith(library, "[assembly: SelectionLib.LibLog(TargetFilter = \"method: Own\")]", out var result);
					var found    = result.Diagnostics.Where(static d => d.Id == AspectDiagnosticID.UnreadableReferencedFilter).ToList();

					// An error, once, on no source text, naming the assembly; and neither the library's methods nor the consumer's own are selected.
					Assert.AreEqual(1, found.Count, kind + Environment.NewLine + DescribeDiagnostics(result));
					Assert.AreEqual(DiagnosticSeverity.Error, found[0].Severity, kind);
					Assert.IsFalse(found[0].Location.IsInSource, kind);
					StringAssert.Contains(found[0].GetMessage(), "'SelectionLib'", kind);
					Assert.AreEqual("", selected, kind);
				}
			}
		}
	}
}
