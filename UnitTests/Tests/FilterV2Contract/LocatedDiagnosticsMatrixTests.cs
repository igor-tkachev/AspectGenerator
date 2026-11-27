using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// The located diagnostics of the filter language as a matrix: every id that points into filter text, in every way a
	/// filter can be written in source (regular, verbatim and raw literals, the items of a <c>string[]</c>, an interpolated
	/// string whose holes are constants, a concatenation, a constant of the project), and the places where the text cannot
	/// be traced back and the attribute is pointed at instead (<c>nameof</c>, a constant of a referenced assembly). The
	/// expected position is found in the source text itself. Each case runs as a build and as a design-time build.
	/// </summary>
	public partial class CompilerTests
	{
		/// <summary>An id, the items of a filter that make it, and the characters the diagnostic points at: <paramref name="Fragment"/> in item <paramref name="Item"/>, after <paramref name="Before"/>.</summary>
		sealed record MatrixCase(string Id, string[] Items, int Item, string Before, string Fragment, bool ArrayOnly = false);

		static readonly string ThreeHundredWildcards = string.Concat(Enumerable.Repeat("*a", 300));

		static readonly MatrixCase[] MatrixCases =
		[
			new(AspectDiagnosticID.FilterSyntaxError,       ["method: Save and type: Task<>"],              0, "method: Save and type: Task", "<>"),
			new(AspectDiagnosticID.FilterSyntaxError,       ["not (public)"],                               0, "",                            "not (public)"),
			new(AspectDiagnosticID.FilterUnknownKeyOrWord,   ["method: Save and foo: bar"],                  0, "method: Save and ",           "foo"),
			new(AspectDiagnosticID.FilterInvalidPattern, ["method: S**x"],                             0, "method: S",                   "**"),
			new(AspectDiagnosticID.FilterOldSyntaxOrRepeatedProperty, ["method: Save", "method: Load"],           1, "",                            "method: Load"),
			new(AspectDiagnosticID.FilterNestedTooDeeply,      ["method: " + ThreeHundredWildcards],           0, "method: ",                    ThreeHundredWildcards),
			new(AspectDiagnosticID.FilterItemStartsWithOperator, ["public", "and static"],             1, "",                            "and", ArrayOnly: true),
			new(AspectDiagnosticID.FilterInvalidLevelReference, ["method: assembly.method"],                0, "method: ",                    "assembly.method"),
			new(AspectDiagnosticID.FilterInvalidAny,   ["any and public"],                             0, "",                            "any"),
		];

		const string MatrixConstants = "static class Filters { public const string Prefix = \"PREFIX\"; public const string Whole = \"WHOLE\"; }";

		/// <summary>A filter written one way: the expression, the declarations it needs, and where in the source to look for the text.</summary>
		sealed record MatrixForm(string Name, string Expression, string Declarations, bool InDeclarations = false);

		static IEnumerable<MatrixForm> MatrixForms(MatrixCase c)
		{
			static string Regular(string text)
			{
				return text.Replace("\n", "\\n", StringComparison.Ordinal);
			}

			var joined = string.Join("\n", c.Items);
			var before = string.Join("\n", c.Items.Take(c.Item)) + (c.Item > 0 ? "\n" : "") + c.Before;
			var after  = new string(joined.AsSpan(before.Length));

			if (c.ArrayOnly)
			{
				yield return new("array", "new[] { " + string.Join(", ", c.Items.Select(static i => "\"" + i + "\"")) + " }", "");
				yield return new("collection expression", "[" + string.Join(", ", c.Items.Select(static i => "\"" + i + "\"")) + "]", "");
				yield return new("array, a verbatim and a raw item", "new[] { " + string.Join(", ", c.Items.Select(static (i, n) => n % 2 == 0 ? "@\"" + i + "\"" : "\"\"\"" + i + "\"\"\"")) + " }", "");

				yield break;
			}

			yield return new("regular", "\"" + Regular(joined) + "\"", "");
			yield return new("verbatim", "@\"" + joined + "\"", "");

			if (c.Items.Length == 1)
				yield return new("raw, one line", "\"\"\"" + joined + "\"\"\"", "");

			yield return new("raw, lines", "\"\"\"\n\t\t\t" + string.Join("\n\t\t\t", c.Items) + "\n\t\t\t\"\"\"", "");
			yield return new("array", "new[] { " + string.Join(", ", c.Items.Select(static i => "\"" + i + "\"")) + " }", "");
			yield return new("concatenation", "\"" + Regular(before) + "\" + \"" + Regular(after) + "\"", "");
			yield return new("interpolated, a constant in the hole", "$\"{Filters.Prefix}" + Regular(after) + "\"", MatrixConstants.Replace("PREFIX", Regular(before), StringComparison.Ordinal).Replace("WHOLE", "", StringComparison.Ordinal));
			yield return new("a constant of the project", "Filters.Whole", MatrixConstants.Replace("PREFIX", "", StringComparison.Ordinal).Replace("WHOLE", Regular(joined), StringComparison.Ordinal), InDeclarations: true);
		}

		/// <summary>The line and column, zero-based, of <paramref name="index"/> in <paramref name="source"/>.</summary>
		static (int Line, int Column) PositionAt(string source, int index)
		{
			var line  = source.Take(index).Count(static ch => ch == '\n');
			var start = source.LastIndexOf('\n', index - 1) + 1;

			return (line, index - start);
		}

		/// <summary>Where the fragment of the case stands in the source of the form: right after what is written before it.</summary>
		static (int Line, int Column) ExpectedPosition(MatrixCase c, MatrixForm form, string source)
		{
			var from = source.IndexOf(form.InDeclarations ? "static class Filters" : "[assembly:", StringComparison.Ordinal);
			int index;

			if (form.Name.StartsWith("interpolated", StringComparison.Ordinal))
			{
				// The text before the fragment is the constant in the hole; the literal goes on with the fragment.
				index = source.IndexOf("{Filters.Prefix}", from, StringComparison.Ordinal) + "{Filters.Prefix}".Length;
			}
			else if (form.Name == "concatenation")
			{
				// The text before the fragment is the first literal; the second begins with the fragment.
				index = source.IndexOf("\" + \"", from, StringComparison.Ordinal) + "\" + \"".Length;
			}
			else
			{
				// Right after the text before the fragment in its item, which the source holds in one piece.
				index = source.IndexOf(c.Before + c.Fragment, from, StringComparison.Ordinal);

				Assert.IsTrue(index >= 0, $"{c.Id}, {form.Name}: '{c.Before}{c.Fragment}' is not in the source.");

				index += c.Before.Length;
			}

			return PositionAt(source, index);
		}

		[TestMethod]
		public void EveryLocatedFilterIdPointsIntoEveryLiteralFormTest()
		{
			var count = 0;

			foreach (var c in MatrixCases)
			{
				foreach (var form in MatrixForms(c))
				{
					var source         = LocatedSource(form.Expression, form.Declarations);
					var (line, column) = ExpectedPosition(c, form, source);

					AssertLocated(new LocatedCase($"{c.Id}, {form.Name}", form.Expression, c.Id, line, column, c.Fragment, form.Declarations), source);
					count++;
				}
			}

			Assert.IsTrue(count >= 40, $"{count} cases.");
		}

		/// <summary>An id and a filter whose text cannot be traced back into source, the way it is written, and the ids it must still report.</summary>
		sealed record FallbackCase(string Id, string Name, string Expression);

		const string FilterConstantsLibrarySource =
			"""
			namespace FilterConstants
			{
				public static class Texts
				{
					public const string Syntax       = "method: Save and (";
					public const string UnknownKey   = "foo: bar";
					public const string BadPattern   = "method: S**x";
					public const string SecondMethod = "method: Load";
					public const string And          = "and static";
					public const string Level        = "method: assembly.method";
					public const string Any          = "any and public";
				}
			}
			""";

		static readonly FallbackCase[] FallbackCases =
		[
			new(AspectDiagnosticID.FilterSyntaxError,                  "nameof",                            "\"method: Save and (\" + nameof(Target)"),
			new(AspectDiagnosticID.FilterUnknownKeyOrWord,             "nameof",                            "\"foo: \" + nameof(Target)"),
			new(AspectDiagnosticID.FilterInvalidPattern,               "nameof",                            "\"method: S**\" + nameof(Target)"),
			new(AspectDiagnosticID.FilterOldSyntaxOrRepeatedProperty,  "nameof",                            "new[] { \"method: Save\", \"method: \" + nameof(Target) }"),
			new(AspectDiagnosticID.FilterNestedTooDeeply,              "nameof",                            "\"method: " + ThreeHundredWildcards + "\" + nameof(Target)"),
			new(AspectDiagnosticID.FilterItemStartsWithOperator,       "nameof",                            "new[] { \"public\", \"and \" + nameof(Target) }"),
			new(AspectDiagnosticID.FilterInvalidLevelReference,        "nameof",                            "\"method: assembly.\" + nameof(Target)"),
			new(AspectDiagnosticID.FilterInvalidAny,                   "nameof",                            "\"any and \" + nameof(Target)"),

			new(AspectDiagnosticID.FilterSyntaxError,                  "a constant of a referenced assembly", "FilterConstants.Texts.Syntax"),
			new(AspectDiagnosticID.FilterUnknownKeyOrWord,             "a constant of a referenced assembly", "FilterConstants.Texts.UnknownKey"),
			new(AspectDiagnosticID.FilterInvalidPattern,               "a constant of a referenced assembly", "FilterConstants.Texts.BadPattern"),
			new(AspectDiagnosticID.FilterOldSyntaxOrRepeatedProperty,  "a constant of a referenced assembly", "new[] { \"method: Save\", FilterConstants.Texts.SecondMethod }"),
			new(AspectDiagnosticID.FilterItemStartsWithOperator,       "a constant of a referenced assembly", "new[] { \"public\", FilterConstants.Texts.And }"),
			new(AspectDiagnosticID.FilterInvalidLevelReference,        "a constant of a referenced assembly", "FilterConstants.Texts.Level"),
			new(AspectDiagnosticID.FilterInvalidAny,                   "a constant of a referenced assembly", "FilterConstants.Texts.Any"),
		];

		[TestMethod]
		public void EveryLocatedFilterIdFallsBackToTheAttributeWhereTheTextCannotBeTracedTest()
		{
			var compilation = CSharpCompilation.Create(
				"FilterConstants",
				[CSharpSyntaxTree.ParseText(FilterConstantsLibrarySource)],
				GetMetadataReferences(),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

			using var stream = new MemoryStream();

			Assert.IsTrue(compilation.Emit(stream).Success);

			var library = MetadataReference.CreateFromImage(stream.ToArray());

			foreach (var c in FallbackCases)
			{
				// The attribute starts at line 3, column 11 of the template, and spans its whole text.
				AssertLocated(new LocatedCase($"{c.Id}, {c.Name}", c.Expression, c.Id, 3, 11, AttributeText), LocatedSource(c.Expression, ""), library);
			}
		}

		[TestMethod]
		public void NotAppliedAndConflictDiagnosticsPointAtTheirAttributesTest()
		{
			// AG0232 stands on the aspect attribute written on the method.
			var notApplied = RunSelection(
				"""
				namespace AspectGenerator.Tests.GeneratorDriver
				{
					static class Calls
					{
						[Log(TargetFilter = "public")]
						internal static void Run()
						{
						}

						public static void Start()
						{
							Run();
						}
					}
				}
				""");

			var warning = notApplied.Diagnostics.Single(static d => d.Id == AspectDiagnosticID.AspectNotAppliedToMethod);

			Assert.IsTrue(warning.Location.IsInSource, DescribeDiagnostics(notApplied));
			Assert.AreEqual("Log(TargetFilter = \"public\")", warning.Location.SourceTree!.GetText().ToString(warning.Location.SourceSpan));

			// AG0235 stands on the first of the attributes that disagree, in source.
			var conflict = RunSelection(
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

						public static void Run()
						{
							SaveOrder();
						}
					}
				}
				""");

			var error = conflict.Diagnostics.Single(static d => d.Id == AspectDiagnosticID.AspectSettingsConflict);

			Assert.IsTrue(error.Location.IsInSource, DescribeDiagnostics(conflict));
			Assert.AreEqual("AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = \"method: Save*\", Level = 1)", error.Location.SourceTree!.GetText().ToString(error.Location.SourceSpan));

			// A conflict inside a library's own attributes is reported in the consumer's build on no source text, naming the library.
			foreach (var metadataOnly in new[] { false, true })
			{
				var kind    = metadataOnly ? "reference assembly" : "implementation assembly";
				var library = CompileSelectionLibrary(
					SelectionLibrarySource.Replace(
						"[assembly: SelectionLib.LibLog(TargetFilter = \"static\", Level = 7)]",
						"[assembly: SelectionLib.LibLog(TargetFilter = \"static\", Level = 7)]\n[assembly: SelectionLib.LibLog(TargetFilter = \"method: Read\", Level = 8)]",
						StringComparison.Ordinal),
					metadataOnly);

				var result = RunGenerator([SelectionLibraryConsumerSource.Replace("/*ASSEMBLY*/", "", StringComparison.Ordinal)], reference: library);
				var found  = result.Diagnostics.Where(static d => d.Id == AspectDiagnosticID.AspectSettingsConflict).ToList();

				Assert.AreEqual(1, found.Count, kind + Environment.NewLine + DescribeDiagnostics(result));
				Assert.IsFalse(found[0].Location.IsInSource, kind);
				StringAssert.Contains(found[0].GetMessage(), "in 'SelectionLib'", kind);
			}
		}
	}
}
