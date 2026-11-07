using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// Where a target filter diagnostic points: at the characters of the filter it is about, inside the literal that
	/// holds them, or at the attribute where they cannot be traced there. Every case runs as a build and as a design-time
	/// build, which must point at the same place. Positions are Roslyn's: zero-based lines and columns, a tab one column.
	/// </summary>
	public partial class CompilerTests
	{
		/// <summary>A filter case: the expression of <c>TargetFilter</c>, the declarations it may use, and where its diagnostic must point.</summary>
		sealed record LocatedCase(string Name, string Expression, string Id, int Line, int Column, string Text, string Declarations = "", bool LineBreaksCrLf = false);

		const string AttributeText = "FilterAspect(\n\tTargetFilter =\n\t\tEXPRESSION)";

		static readonly LocatedCase[] AssemblyCases =
		[
			// The expression starts at line 5, column 2; the attribute at line 3, column 11.
			new("regular",                    "\"method: Save; foo: bar\"",                       "AG0204", 5, 17, "foo"),
			new("regular, escapes before",    "\"method: S\\u0061ve;\\tfoo: bar\"",               "AG0204", 5, 23, "foo"),
			new("regular, escaped key",       "\"method: Save; \\x66oo: bar\"",                   "AG0204", 5, 17, "\\x66oo"),
			new("regular, surrogate pair",    "\"method: \\U0001F600; foo: bar\"",                "AG0204", 5, 23, "foo"),
			new("regular, second line",       "\"method: Save\\nfoo: bar\"",                      "AG0204", 5, 17, "foo"),
			new("regular, CR LF and blanks",  "\"method: Save\\r\\n\\r\\n   foo: bar\"",          "AG0204", 5, 26, "foo"),
			new("regular, regex",             "\"regex: [\"",                                      "AG0201", 5, 10, "["),
			new("verbatim",                   "@\"method: \"\"Save\"\"; foo: bar\"",              "AG0204", 5, 22, "foo"),
			new("verbatim, two lines",        "@\"method: Save\n\t\t\tfoo: bar\"",                "AG0204", 6, 3,  "foo"),
			new("raw, one line",              "\"\"\"method: Save; foo: bar\"\"\"",               "AG0204", 5, 19, "foo"),
			new("raw, lines",                 "\"\"\"\n\t\t\tmethod: Save\n\t\t\t  foo: bar\n\t\t\t\"\"\"", "AG0204", 7, 5, "foo"),
			new("raw, lines, CR LF",          "\"\"\"\n\t\t\tmethod: Save\n\t\t\t  foo: bar\n\t\t\t\"\"\"", "AG0204", 7, 5, "foo", LineBreaksCrLf: true),
			new("raw, a rule across lines",   "\"\"\"\n\t\t\tpublic\n\t\t\tprivate\n\t\t\t\"\"\"",   "AG0210", 6, 3,  "public\n\t\t\tprivate"),
			new("raw, a rule across CR LF",   "\"\"\"\n\t\t\tpublic\n\t\t\tprivate\n\t\t\t\"\"\"",   "AG0210", 6, 3,  "public\r\n\t\t\tprivate", LineBreaksCrLf: true),
			new("regular, a rule across lines", "\"public\\nprivate\"",                           "AG0210", 5, 3,  "public\\nprivate"),
			new("interpolated",               "$\"method: {Filters.Name}; foo: bar\"",            "AG0204", 5, 28, "foo", FiltersClass),
			new("interpolated, in the hole",  "$\"method: Save; {Filters.Bad}\"",                 "AG0204", 34, 50, "foo", FiltersClass),
			new("interpolated, braces",       "$\"method: Save{{x}}; foo: bar\"",                 "AG0204", 5, 23, "foo"),
			new("interpolated verbatim",      "$@\"method: {Filters.Name}\n\t\t\tfoo: bar\"",    "AG0204", 6, 3,  "foo", FiltersClass),
			new("interpolated raw, lines",    "$\"\"\"\n\t\t\tmethod: {Filters.Name}\n\t\t\t  foo: bar\n\t\t\t\"\"\"", "AG0204", 7, 5, "foo", FiltersClass),
			new("concatenation",              "\"method: Save; \" + \"foo: bar\"",                "AG0204", 5, 22, "foo"),
			new("concatenation, parenthesized", "(\"method: Save; \") + (string)(\"foo: bar\")",  "AG0204", 5, 33, "foo"),
			new("concatenation, one rule",    "\"public \" + \"private\"",                        "AG0210", 5, 3,  "public \" + \"private"),
			new("const",                      "Filters.Bad",                                      "AG0204", 34, 50, "foo", FiltersClass),
			new("const of consts",            "Filters.Both",                                     "AG0204", 34, 50, "foo", FiltersClass),
			new("array",                      "new[] { \"method: Save\", \"foo: bar\" }",         "AG0204", 5, 27, "foo"),
			new("collection expression",      "[\"method: Save\", \"foo: bar\"]",                 "AG0204", 5, 20, "foo"),

			// What cannot be traced exactly points at the attribute.
			new("fallback, nameof",           "\"foo: \" + nameof(Target)",                       "AG0204", 3, 11, AttributeText),
			new("fallback, a rule across filters", "new[] { \"public\", \"private\" }",           "AG0210", 3, 11, AttributeText),
		];

		/// <summary>A class of constants for the cases that use them, on the last line of the file, line 34: its first constant's text begins at column 50.</summary>
		const string FiltersClass = "static class Filters { public const string Bad = \"foo: bar\"; public const string Name = \"Save\"; public const string Both = Bad + \"\"; }";

		const string LocatedAssemblySource =
			"""
			using System;
			using AspectGenerator;
			using AspectGenerator.Tests.GeneratorDriver;
			[assembly: FilterAspect(
				TargetFilter =
					EXPRESSION)]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public PROPERTY TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info)
				{
					info.ReturnValue += " filtered";
				}
			}

			static class Target
			{
				public static void Invoke()
				{
					Save();
				}

				public static string Save()
				{
					return "save";
				}
			}

			DECLARATIONS
			""";

		[TestMethod]
		public void FilterDiagnosticsPointIntoTheLiteralTest()
		{
			foreach (var c in AssemblyCases)
				AssertLocated(c, LocatedSource(c.Expression, c.Declarations));
		}

		[TestMethod]
		public void FilterDiagnosticsOfATypeLevelAttributePointIntoTheLiteralTest()
		{
			var source = TypeLevelFilterRuleDiagnosticSource.Replace("\"FILTER_RULE\"", "\n\t\"method: Save\\n\\tfoo: bar\"", StringComparison.Ordinal);

			// [FilterAspect(TargetFilter = at line 14, the literal on the next line after a tab.
			AssertLocated(new LocatedCase("type level", "", "AG0204", 15, 18, "foo"), source);
		}

		[TestMethod]
		public void DefaultTargetFilterDiagnosticsPointIntoTheAspectDeclarationTest()
		{
			// The default filter of an aspect declared in the compilation: at its literal in the [Aspect] attribute.
			AssertLocated(new LocatedCase("default filter", "", "AG0204", 7, 24, "attribute"), DefaultTargetFilterSingularAttributeSource);
		}

		[TestMethod]
		public void FilterDiagnosticsOfReferencedFiltersPointAtTheApplicationTest()
		{
			var library = GetDesignTimeLibraryReference();

			// The default filter of an aspect of a referenced assembly: at the attribute that applies it.
			AssertLocated(new LocatedCase("library default filter", "", "AG0204", 5, 1, "LibraryLog"), LibraryDefaultTargetFilterSource, library, all: false);

			// A filter that uses a const of a referenced assembly cannot be traced into it: at the attribute.
			const string expression = "AspectGenerator.Tests.Library.LibraryFilters.Bad";

			AssertLocated(new LocatedCase("library const", expression, "AG0204", 3, 11, AttributeText), LocatedSource(expression, ""), library);
		}

		/// <summary>The assembly-level source with <paramref name="expression"/> as the filter; an array property where it is an array.</summary>
		static string LocatedSource(string expression, string declarations)
		{
			var isArray = expression.StartsWith("new[]", StringComparison.Ordinal) || expression.StartsWith("[", StringComparison.Ordinal);

			return LocatedAssemblySource
				.Replace("PROPERTY", isArray ? "string[]?" : "string?", StringComparison.Ordinal)
				.Replace("DECLARATIONS", declarations.Length > 0 ? declarations : "// no declarations", StringComparison.Ordinal)
				.Replace("EXPRESSION", expression, StringComparison.Ordinal);
		}

		void AssertLocated(LocatedCase c, string source, MetadataReference? reference = null, bool all = true)
		{
			if (c.LineBreaksCrLf)
				source = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

			var expected = c.Text == AttributeText ? AttributeText.Replace("EXPRESSION", c.Expression, StringComparison.Ordinal) : c.Text;

			if (c.LineBreaksCrLf)
				expected = expected.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

			foreach (var designTime in new[] { false, true })
			{
				var mode       = designTime ? "design time" : "build";
				var properties = designTime ? new Dictionary<string,string> { ["build_property.DesignTimeBuild"] = "true" } : null;
				var result     = RunGenerator(source, properties, reference: reference);
				var filter     = result.Diagnostics.Where(static d => d.Id.StartsWith("AG02", StringComparison.Ordinal)).ToArray();
				var described  = string.Join(" | ", filter.Select(static d => $"{d.Id} {d.Location.GetLineSpan()} '{Text(d.Location)}': {d.GetMessage()}"));

				Assert.IsFalse(result.Results.Any(static r => r.Exception is not null), $"{c.Name}, {mode}: the generator threw.");

				if (all)
					Assert.AreEqual(1, filter.Length, $"{c.Name}, {mode}: {described}");

				var diagnostic = filter.FirstOrDefault(d => d.Id == c.Id);

				Assert.IsNotNull(diagnostic, $"{c.Name}, {mode}: no {c.Id}. {described}");
				Assert.IsTrue(diagnostic.Location.IsInSource, $"{c.Name}, {mode}: not in source.");
				Assert.AreEqual(expected, Text(diagnostic.Location), $"{c.Name}, {mode}: {described}");
				Assert.AreEqual(new LinePosition(c.Line, c.Column), diagnostic.Location.GetLineSpan().StartLinePosition, $"{c.Name}, {mode}: {described}");
			}

			static string Text(Location location)
			{
				return location.SourceTree?.GetText().ToString(location.SourceSpan) ?? "";
			}
		}
	}
}
