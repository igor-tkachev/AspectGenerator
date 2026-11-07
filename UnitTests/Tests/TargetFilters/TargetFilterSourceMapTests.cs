using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// The trace from the characters of a constant string to the source characters that make it, over generated texts
	/// written as every kind of literal: every character must be traced to source text that means that character, and
	/// nothing that cannot be traced exactly may be traced at all.
	/// </summary>
	[TestClass]
	public class TargetFilterSourceMapTests
	{
		const int Count = 400;

		static readonly string[] Pieces =
		[
			"a", "Z", "0", " ", "  ", "\t", "\"", "\\", "{", "}", "{{", "}}", ":", ";", "|", "&", "*", "?", "<", ">", "\u00e9", "\u0416",
			"\U0001F600", "\0", "\a", "\u001b", "\r", "\n", "\r\n",
		];

		[TestMethod]
		public void EveryCharacterIsTracedToTheSourceThatMakesItTest()
		{
			var random = new TargetFilterCorpus.SplitMix64(0x4147_5446_534D_0001UL);
			var traced = new Dictionary<string,int>(StringComparer.Ordinal);

			for (var n = 0; n < Count; n++)
			{
				var value = new StringBuilder();

				for (var length = 1 + random.Next(12); length > 0; length--)
					value.Append(Pieces[random.Next(Pieces.Length)]);

				foreach (var (kind, literal) in Literals(value.ToString(), random))
				{
					var (expression, compilation, constant) = Parse($"const string Value = {literal};");

					Assert.AreEqual(value.ToString(), constant, $"{kind}: the test wrote a literal of another value: {literal}");

					var map = TargetFilterSourceMap.Create(expression, compilation, constant);

					Assert.IsNotNull(map, $"{kind}: not traced: {literal}");
					AssertTraced(map, expression.SyntaxTree, constant, kind, literal);

					traced[kind] = traced.GetValueOrDefault(kind) + 1;
				}
			}

			foreach (var (kind, count) in traced.OrderBy(static k => k.Key, StringComparer.Ordinal))
				Console.WriteLine($"{kind}: {count}");

			Assert.AreEqual(6, traced.Count, string.Join(", ", traced.Keys));
		}

		[TestMethod]
		public void ATextThatCannotBeTracedExactlyIsNotTracedTest()
		{
			// nameof, in a concatenation or a hole: no trace at all, rather than a partial one.
			foreach (var declaration in new[]
			{
				"const string Value = \"a\" + nameof(C);",
				"const string Value = $\"{nameof(C)}a\";",
			})
			{
				var (expression, compilation, constant) = Parse(declaration);

				Assert.IsNull(TargetFilterSourceMap.Create(expression, compilation, constant), declaration);
			}

			// A value other than the one the expression makes is not traced either.
			var (other, otherCompilation, _) = Parse("const string Value = \"abc\";");

			Assert.IsNull(TargetFilterSourceMap.Create(other, otherCompilation, "abd"));
		}

		[TestMethod]
		public void CharactersThatDoNotStandTogetherHaveNoLocationTest()
		{
			// The const stands below the text that uses it: a span over both runs backwards, and points nowhere.
			var (expression, compilation, constant) = Parse("const string Value = A + \"cd\"; const string A = \"ab\";");
			var map = TargetFilterSourceMap.Create(expression, compilation, constant)!;

			Assert.IsNotNull(map);
			Assert.IsNotNull(map.Locate(new TextSpan(0, 2)));
			Assert.IsNotNull(map.Locate(new TextSpan(2, 2)));
			Assert.IsNull(map.Locate(new TextSpan(1, 2)));
		}

		[TestMethod]
		public void OneConstantUsedTwiceIsNotOneRunOfCharactersTest()
		{
			// The two characters are the one character of A, twice: each points at it, the two together at nothing.
			var (expression, compilation, constant) = Parse("const string A = \"[\"; const string Value = \"regex: \" + A + A;");
			var map = TargetFilterSourceMap.Create(expression, compilation, constant)!;

			Assert.IsNotNull(map);
			Assert.IsNotNull(map.Locate(new TextSpan(7, 1)));
			Assert.IsNotNull(map.Locate(new TextSpan(8, 1)));
			Assert.AreEqual(map.Locate(new TextSpan(7, 1)), map.Locate(new TextSpan(8, 1)));
			Assert.IsNull(map.Locate(new TextSpan(7, 2)));

			// The two halves of one escape do make one run.
			var (pair, pairCompilation, pairConstant) = Parse("const string Value = \"a\\U0001F600\";");
			var pairMap = TargetFilterSourceMap.Create(pair, pairCompilation, pairConstant)!;

			Assert.AreEqual("\\U0001F600", pair.SyntaxTree.GetText().ToString(pairMap.Locate(new TextSpan(1, 2))!.SourceSpan));
		}

		[TestMethod]
		public void ConstantsThatDoubleAtEveryLevelAreTracedOnceEachTest()
		{
			var declaration = new StringBuilder("const string E0 = \"\";");

			for (var i = 1; i <= 30; i++)
				declaration.Append(CultureInfo.InvariantCulture, $" const string E{i} = E{i - 1} + E{i - 1};");

			declaration.Append(" const string Value = \"foo: bar\" + E30;");

			var (expression, compilation, constant) = Parse(declaration.ToString());
			var watch = System.Diagnostics.Stopwatch.StartNew();
			var map   = TargetFilterSourceMap.Create(expression, compilation, constant);

			watch.Stop();

			Assert.IsNotNull(map);
			Assert.AreEqual("foo", expression.SyntaxTree.GetText().ToString(map.Locate(new TextSpan(0, 3))!.SourceSpan));
			Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(2), $"Traced in {watch.Elapsed}.");
		}

		[TestMethod]
		public void ATraceOverItsBudgetIsNotTracedTest()
		{
			// Two thousand empty holes make a filter of three characters: past a budget of a thousand, no trace at all; within
			// one of ten thousand, a trace.
			var holes = string.Concat(Enumerable.Repeat("{\"\"}", 2_000));
			var (expression, compilation, constant) = Parse("const string Value = $\"abc" + holes + "\";");

			Assert.IsNull(TargetFilterSourceMap.Create(expression, compilation, constant, maxWork: 1_000));
			Assert.IsNotNull(TargetFilterSourceMap.Create(expression, compilation, constant, maxWork: 10_000));
		}

		static void AssertTraced(TargetFilterSourceMap map, SyntaxTree tree, string value, string kind, string literal)
		{
			var text = tree.GetText();

			for (var i = 0; i < value.Length; i++)
			{
				// A surrogate pair is made by one escape or one written character: both units point at it.
				var length   = char.IsHighSurrogate(value[i]) && i + 1 < value.Length ? 2 : 1;
				var location = map.Locate(new TextSpan(i, length));

				Assert.IsNotNull(location, $"{kind}: character {i} has no location: {literal}");

				var source = text.ToString(location.SourceSpan);
				var means  = value.Substring(i, length);

				Assert.IsTrue(Means(source, means), $"{kind}: character {i} '{TargetFilterProbe.Escape(means)}' is traced to '{TargetFilterProbe.Escape(source)}': {literal}");

				i += length - 1;
			}
		}

		/// <summary>The source text makes the characters: as written, as an escape sequence, or as a doubled quote or brace.</summary>
		static bool Means(string source, string characters)
		{
			if (source == characters)
				return true;

			if (source.Length == 2 && source[0] == source[1] && source[0] is '"' or '{' or '}')
				return characters == source[..1];

			if (source.StartsWith('\\'))
				return Parse($"const string Value = \"{source}\";").Constant == characters;

			return false;
		}

		/// <summary>The value as each kind of literal it can be written as, with escapes chosen at random.</summary>
		static IEnumerable<(string Kind, string Literal)> Literals(string value, TargetFilterCorpus.SplitMix64 random)
		{
			yield return ("regular", "\"" + Escaped(value, random, braces: false) + "\"");
			yield return ("verbatim", "@\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
			yield return ("interpolated", "$\"" + Escaped(value, random, braces: true) + "\"");
			yield return ("interpolated verbatim", "$@\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal).Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal) + "\"");

			// A raw literal holds any character but a line break on one line and quotes it cannot be closed after; a
			// multi-line one holds any line, indented.
			if (!value.Contains('\r') && !value.Contains('\n') && !value.StartsWith('"') && !value.EndsWith('"') && !value.Contains("\"\"\"\"", StringComparison.Ordinal))
				yield return ("raw", "\"\"\"\"" + value + "\"\"\"\"");

			if (!value.Contains('\r') && !value.Contains("\"\"\"", StringComparison.Ordinal) && value.Trim().Length > 0 && !value.Contains('\u001b') && !value.Contains('\a') && !value.Contains('\0'))
			{
				var indentation = random.Next(2) == 0 ? "    " : "\t";
				var lines       = value.Split('\n');

				// Every line of white space only stays as it is, since the language keeps what is beyond the indentation.
				yield return ("raw lines", "\"\"\"\n" + string.Join("\n", lines.Select(l => indentation + l)) + "\n" + indentation + "\"\"\"");
			}
		}

		static string Escaped(string value, TargetFilterCorpus.SplitMix64 random, bool braces)
		{
			var sb = new StringBuilder();

			for (var i = 0; i < value.Length; i++)
			{
				var ch = value[i];

				if (braces && ch is '{' or '}')
				{
					sb.Append(ch).Append(ch);
					continue;
				}

				if (char.IsHighSurrogate(ch) && i + 1 < value.Length && random.Next(2) == 0)
				{
					sb.Append("\\U").Append(char.ConvertToUtf32(ch, value[i + 1]).ToString("X8", CultureInfo.InvariantCulture));
					i++;
					continue;
				}

				var simple = ch switch
				{
					'"'      => "\\\"",
					'\\'     => "\\\\",
					'\0'     => "\\0",
					'\a'     => "\\a",
					'\u001b' => "\\e",
					'\t'     => "\\t",
					'\r'     => "\\r",
					'\n'     => "\\n",
					_        => null
				};

				switch (random.Next(4))
				{
					case 0 when simple is null && !char.IsSurrogate(ch) && ch >= ' ':
						sb.Append(ch);
						break;
					case 1:
						sb.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
						break;
					case 2 when i + 1 >= value.Length || !Uri.IsHexDigit(value[i + 1]):
						// \x reads up to four digits, so it is used where no hex digit follows.
						sb.Append("\\x").Append(((int)ch).ToString("x", CultureInfo.InvariantCulture));
						break;
					default:
						sb.Append(simple ?? (char.IsSurrogate(ch) || ch < ' ' ? "\\u" + ((int)ch).ToString("X4", CultureInfo.InvariantCulture) : ch.ToString()));
						break;
				}
			}

			return sb.ToString();
		}

		static (ExpressionSyntax Expression, Compilation Compilation, string Constant) Parse(string declaration)
		{
			var tree        = CSharpSyntaxTree.ParseText("class C { " + declaration + " }", CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
			var compilation = CSharpCompilation.Create("SourceMap", [tree], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
			var errors      = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();

			Assert.AreEqual(0, errors.Length, declaration + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(static e => e.ToString())));

			var declarator = tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(static d => d.Identifier.ValueText == "Value");
			var expression = declarator.Initializer!.Value;
			var constant   = (string)compilation.GetSemanticModel(tree).GetConstantValue(expression).Value!;

			return (expression, compilation, constant);
		}
	}
}
