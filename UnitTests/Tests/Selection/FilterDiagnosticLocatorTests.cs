using System;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using AspectGenerator.Selection;
using AspectGenerator.TargetFilterBinding;

namespace AspectGenerator.Tests.Selection
{
	/// <summary>
	/// Where a diagnostic of a filter is reported: on the characters it is about inside the literal that writes the item,
	/// or on the attribute where those cannot be traced.
	/// </summary>
	[TestClass]
	public class FilterDiagnosticLocatorTests
	{
		static readonly Location Fallback = Location.Create("fallback.cs", default, default);

		[TestMethod]
		public void RegularLiteralTest()
		{
			AssertLocated("\"method: Foo*\"", 0, "Foo*", "Foo*");
		}

		[TestMethod]
		public void VerbatimLiteralTest()
		{
			AssertLocated("@\"method: Foo*\"", 0, "Foo*", "Foo*");
		}

		[TestMethod]
		public void RawLiteralTest()
		{
			AssertLocated("\"\"\"method: Foo*\"\"\"", 0, "Foo*", "Foo*");
		}

		[TestMethod]
		public void InterpolatedConstantTest()
		{
			AssertLocated("$\"method: {Name}*\"", 0, "method", "method", "const string Name = \"Foo\";");
		}

		[TestMethod]
		public void ConcatenationTest()
		{
			AssertLocated("\"method: \" + \"Foo*\"", 0, "Foo*", "Foo*");
		}

		[TestMethod]
		public void ConstTest()
		{
			AssertLocated("Text", 0, "Foo*", "Foo*", "const string Text = \"method: Foo*\";");
		}

		[TestMethod]
		public void ArrayItemsTest()
		{
			foreach (var array in new[]
			{
				"new[] { \"method: A\", \"method: B\", \"method: C*\" }",
				"new string[] { \"method: A\", \"method: B\", \"method: C*\" }",
				"[\"method: A\", \"method: B\", \"method: C*\"]",
			})
			{
				AssertLocated(array, 0, "A", "A");
				AssertLocated(array, 2, "C*", "C*");
				AssertLocated(array, 1, "method", "method");
			}
		}

		[TestMethod]
		public void ArrayOfDifferentLiteralsTest()
		{
			AssertLocated("[\"method: A\", @\"method: B\", \"\"\"method: C*\"\"\"]", 2, "C*", "C*");
		}

		[TestMethod]
		public void EscapesBeforeTheSpanTest()
		{
			// \" is one character of the value but two of the source: the span is counted in the value.
			AssertLocated("\"a\\\"b\\\\c\\u0041 Foo\"", 0, "Foo", "Foo");
			AssertLocated("[\"x\", \"a\\\"b\\\\c\\u0041 Foo\"]", 1, "Foo", "Foo");
			AssertLocated("\"a\\\"b\"", 0, "\"", "\\\"");
		}

		[TestMethod]
		public void MultiLineRawLiteralTest()
		{
			var raw = "\"\"\"\r\n    method: A\r\n      method: Foo*\r\n    \"\"\"";

			// The indentation of the closing line is not part of the value, so the characters of the second line start
			// two characters later in the source than in the value.
			AssertLocated(raw, 0, "Foo*", "Foo*");
		}

		[TestMethod]
		public void ConstantOfAReferencedAssemblyFallsBackTest()
		{
			var reference = CSharpCompilation.Create(
				"Library",
				[CSharpSyntaxTree.ParseText("public static class Library { public const string Text = \"method: Foo*\"; }")],
				[MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

			var (expression, compilation) = Parse("Library.Text", "", reference.ToMetadataReference());

			Assert.AreEqual("method: Foo*", compilation.GetSemanticModel(expression.SyntaxTree).GetConstantValue(expression).Value);
			Assert.AreSame(Fallback, FilterDiagnosticLocator.Locate(expression, compilation, Diagnostic(0, 8, 4), Fallback));
		}

		[TestMethod]
		public void NameofFallsBackTest()
		{
			var (expression, compilation) = Parse("nameof(Value)", "");

			Assert.AreSame(Fallback, FilterDiagnosticLocator.Locate(expression, compilation, Diagnostic(0, 0, 3), Fallback));

			(expression, compilation) = Parse("\"method: \" + nameof(Value)", "");

			Assert.AreSame(Fallback, FilterDiagnosticLocator.Locate(expression, compilation, Diagnostic(0, 8, 5), Fallback));
		}

		[TestMethod]
		public void ItemOutOfRangeFallsBackTest()
		{
			var (array, compilation) = Parse("[\"method: A\", \"method: B\"]", "");

			Assert.AreSame(Fallback, FilterDiagnosticLocator.Locate(array, compilation, Diagnostic(2, 0, 1), Fallback));
			Assert.AreSame(Fallback, FilterDiagnosticLocator.Locate(array, compilation, Diagnostic(-1, 0, 1), Fallback));

			var (single, singleCompilation) = Parse("\"method: A\"", "");

			Assert.AreSame(Fallback, FilterDiagnosticLocator.Locate(single, singleCompilation, Diagnostic(1, 0, 1), Fallback));
		}

		[TestMethod]
		public void SpanOutOfRangeFallsBackTest()
		{
			var (expression, compilation) = Parse("\"method: A\"", "");

			Assert.AreSame(Fallback, FilterDiagnosticLocator.Locate(expression, compilation, Diagnostic(0, 5, 20), Fallback));
		}

		[TestMethod]
		public void NoExpressionFallsBackTest()
		{
			var (_, compilation) = Parse("\"method: A\"", "");

			Assert.AreSame(Fallback, FilterDiagnosticLocator.Locate(null, compilation, Diagnostic(0, 0, 1), Fallback));
		}

		[TestMethod]
		public void SpreadInACollectionFallsBackTest()
		{
			var (expression, compilation) = Parse("[..Others, \"method: A\"]", "static readonly string[] Others = [\"method: B\"];");

			Assert.AreSame(Fallback, FilterDiagnosticLocator.Locate(expression, compilation, Diagnostic(1, 8, 1), Fallback));
		}

		[TestMethod]
		public void NoFallbackGivesNoLocationTest()
		{
			var (expression, compilation) = Parse("nameof(Value)", "");

			Assert.IsNull(FilterDiagnosticLocator.Locate(expression, compilation, Diagnostic(0, 0, 1), null));
		}

		/// <summary>
		/// Locates the diagnostic at the first occurrence of <paramref name="find"/> in the value of item <paramref name="item"/>
		/// and checks that the source text there is <paramref name="expectedSource"/>.
		/// </summary>
		static void AssertLocated(string argument, int item, string find, string expectedSource, string members = "")
		{
			var (expression, compilation) = Parse(argument, members);
			var value = GetItemValue(expression, compilation, item);
			var start = value.IndexOf(find, StringComparison.Ordinal);

			Assert.IsTrue(start >= 0, $"'{find}' is not in '{value}'");

			var location = FilterDiagnosticLocator.Locate(expression, compilation, Diagnostic(item, start, find.Length), Fallback)!;

			Assert.AreNotSame(Fallback, location, argument);
			Assert.AreSame(expression.SyntaxTree, location.SourceTree);
			Assert.AreEqual(expectedSource, location.SourceTree!.GetText().ToString(location.SourceSpan), argument);
		}

		static string GetItemValue(ExpressionSyntax expression, Compilation compilation, int item)
		{
			var element = expression switch
			{
				ImplicitArrayCreationExpressionSyntax { Initializer.Expressions: var values } => values[item],
				ArrayCreationExpressionSyntax { Initializer.Expressions: var values }         => values[item],
				CollectionExpressionSyntax { Elements: var elements }                         => ((ExpressionElementSyntax)elements[item]).Expression,
				_                                                                             => expression
			};

			return (string)compilation.GetSemanticModel(expression.SyntaxTree).GetConstantValue(element).Value!;
		}

		static FilterBlockDiagnostic Diagnostic(int item, int start, int length)
		{
			return new FilterBlockDiagnostic("AG0000", item, new TextSpan(start, length), "test");
		}

		/// <summary>The expression <paramref name="argument"/>, as the initializer of a field of a class with <paramref name="members"/>.</summary>
		static (ExpressionSyntax Expression, Compilation Compilation) Parse(string argument, string members, params MetadataReference[] references)
		{
			var tree        = CSharpSyntaxTree.ParseText("class C { " + members + " static readonly " + (argument[0] == '[' || argument[0] == 'n' && argument[1] == 'e' ? "string[]" : "string") + " Value = " + argument + "; }", CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
			var compilation = CSharpCompilation.Create(
				"Locator",
				[tree],
				references.Prepend(MetadataReference.CreateFromFile(typeof(object).Assembly.Location)),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

			var declarator = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(static d => d.Identifier.ValueText == "Value");

			return (declarator.Initializer!.Value, compilation);
		}
	}
}
