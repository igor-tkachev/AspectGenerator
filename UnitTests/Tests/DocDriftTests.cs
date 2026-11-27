using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using AspectGenerator.Cascade;
using AspectGenerator.FilterV2;
using AspectGenerator.Tests.TargetFilters;
using AspectGenerator.TargetFilterBinding;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// Keeps the documentation and the filter language together. Every filter written in the wiki, the skill guides and the
	/// README (a fenced <c>target-filter</c> block, or a <c>TargetFilter</c> / <c>DefaultTargetFilter</c> argument in a C#
	/// block) is read by the filter reader and turned into a block, and it must give no diagnostic, unless the block says
	/// it is an example of an error: <c>```target-filter error=AG0220</c> must give exactly that id. A block names the level
	/// its filter is written at with <c>level=aspect|assembly|type|method</c> (a C# block gets it from the attribute).
	/// Every diagnostic id of the generator is described in the diagnostics pages, and the pages name no id that is neither
	/// current nor retired. Relative links of the wiki, and the headings they point at, exist.
	/// </summary>
	[TestClass]
	public class DocDriftTests
	{
		static readonly string[] _retiredIds = ["AG0201", "AG0202", "AG0204", "AG0205", "AG0206", "AG0208", "AG0210", "AG0211"];

		static string Root => TargetFilterCuratedInputs.RepositoryRoot;

		public TestContext TestContext { get; set; } = null!;

		/// <summary>The pages that teach the filter language: the wiki, the skill guides and the README.</summary>
		static IEnumerable<string> Documents(bool includeReadme = true)
		{
			foreach (var file in Directory.GetFiles(Path.Combine(Root, "docs", "wiki"), "*.md").OrderBy(static f => f, StringComparer.Ordinal))
				yield return file;

			foreach (var file in Directory.GetFiles(Path.Combine(Root, "Skills", "aspectgenerator"), "*.md", SearchOption.AllDirectories).OrderBy(static f => f, StringComparer.Ordinal))
				yield return file;

			if (includeReadme)
				yield return Path.Combine(Root, "README.md");
		}

		static string Relative(string file)
		{
			return Path.GetRelativePath(Root, file).Replace('\\', '/');
		}

		sealed record Block(string File, int Line, string Language, Dictionary<string,string> Options, string Text);

		sealed record Example(string File, int Line, CascadeLevel Level, string[] Items, string[] ExpectedIds);

		static List<Block> Blocks(string file)
		{
			var result = new List<Block>();
			var lines  = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');

			for (var i = 0; i < lines.Length; i++)
			{
				var open = Regex.Match(lines[i], @"^```(?<language>[^\s`]*)\s*(?<options>.*)$");

				if (!open.Success)
					continue;

				var start = i + 1;
				var body  = new List<string>();

				for (i++; i < lines.Length && lines[i] != "```"; i++)
					body.Add(lines[i]);

				Assert.IsLessThan(lines.Length, i, $"{Relative(file)}:{start}: a fenced block is not closed.");

				var options = new Dictionary<string,string>(StringComparer.Ordinal);

				foreach (var option in open.Groups["options"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
				{
					var equals = option.IndexOf('=');

					Assert.IsGreaterThan(0, equals, $"{Relative(file)}:{start}: an option of a fenced block is written name=value: {option}");
					options[option[..equals]] = option[(equals + 1)..];
				}

				result.Add(new Block(file, start - 1, open.Groups["language"].Value, options, string.Join("\n", body)));
			}

			return result;
		}

		static CascadeLevel Level(string name, string where)
		{
			return name switch
			{
				"aspect"   => CascadeLevel.Aspect,
				"assembly" => CascadeLevel.Assembly,
				"type"     => CascadeLevel.Type,
				"method"   => CascadeLevel.Method,
				_          => throw new AssertFailedException($"{where}: unknown level '{name}'."),
			};
		}

		static string[] ExpectedIds(Block block)
		{
			return block.Options.TryGetValue("error", out var ids)
				? [..ids.Split(',', StringSplitOptions.RemoveEmptyEntries).OrderBy(static i => i, StringComparer.Ordinal)]
				: [];
		}

		/// <summary>The filter examples of a page: fenced filter blocks, and the filters of the attributes in its C# blocks.</summary>
		static List<Example> Examples(string file)
		{
			var result = new List<Example>();

			foreach (var block in Blocks(file))
			{
				var where = $"{Relative(file)}:{block.Line}";

				switch (block.Language)
				{
					case "target-filter":
						result.Add(new Example(
							Relative(file),
							block.Line,
							Level(block.Options.GetValueOrDefault("level", "assembly"), where),
							[block.Text],
							ExpectedIds(block)));
						break;

					case "csharp":
						result.AddRange(CSharpExamples(Relative(file), block, where));
						break;
				}
			}

			return result;
		}

		static IEnumerable<Example> CSharpExamples(string file, Block block, string where)
		{
			var tree = CSharpSyntaxTree.ParseText(block.Text, new CSharpParseOptions(LanguageVersion.Preview));
			var root = tree.GetRoot();

			// A block of other documentation may be a fragment; one that holds a filter is written to parse.
			if (block.Text.Contains("TargetFilter", StringComparison.Ordinal))
			{
				var syntaxErrors = tree.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).Select(static d => d.ToString()).ToList();

				Assert.AreEqual(0, syntaxErrors.Count, $"{where}: the C# example does not parse: {string.Join("; ", syntaxErrors)}");
			}

			foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
			{
				foreach (var argument in attribute.ArgumentList?.Arguments ?? [])
				{
					var name = argument.NameEquals?.Name.Identifier.ValueText;

					if (name is not ("TargetFilter" or "DefaultTargetFilter"))
						continue;

					var at    = $"{where} (line {block.Line + 1 + tree.GetLineSpan(attribute.Span).StartLinePosition.Line})";
					var items = Items(argument.Expression, at);
					var level = name == "DefaultTargetFilter" ? CascadeLevel.Aspect : AttributeLevel(attribute, at);

					yield return new Example(file, block.Line + 1 + tree.GetLineSpan(attribute.Span).StartLinePosition.Line, level, items, ExpectedIds(block));
				}
			}
		}

		static CascadeLevel AttributeLevel(AttributeSyntax attribute, string where)
		{
			var list = (AttributeListSyntax)attribute.Parent!;

			if (list.Target?.Identifier.ValueText == "assembly")
				return CascadeLevel.Assembly;

			return list.Parent switch
			{
				TypeDeclarationSyntax                                         => CascadeLevel.Type,
				MethodDeclarationSyntax or LocalFunctionStatementSyntax       => CascadeLevel.Method,
				_                                                             => throw new AssertFailedException($"{where}: the attribute is on neither an assembly, a type nor a method."),
			};
		}

		/// <summary>The strings a filter argument holds: one string, or the elements of an array or a collection expression.</summary>
		static string[] Items(ExpressionSyntax expression, string where)
		{
			switch (expression)
			{
				case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal:
					return [literal.Token.ValueText];

				case CollectionExpressionSyntax collection:
					return [..collection.Elements.Select(e => Single(((ExpressionElementSyntax)e).Expression, where))];

				case ImplicitArrayCreationExpressionSyntax implicitArray:
					return [..implicitArray.Initializer.Expressions.Select(e => Single(e, where))];

				case ArrayCreationExpressionSyntax array when array.Initializer is not null:
					return [..array.Initializer.Expressions.Select(e => Single(e, where))];

				default:
					return [Single(expression, where)];
			}
		}

		static string Single(ExpressionSyntax expression, string where)
		{
			switch (expression)
			{
				case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal:
					return literal.Token.ValueText;

				case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } add:
					return Single(add.Left, where) + Single(add.Right, where);

				case ParenthesizedExpressionSyntax parenthesized:
					return Single(parenthesized.Expression, where);

				default:
					throw new AssertFailedException($"{where}: a filter in the documentation is written as a string literal, not as '{expression}'.");
			}
		}

		static List<string> Read(Example example)
		{
			var ids = new List<string>();

			foreach (var item in example.Items)
				ids.AddRange(FilterV2Reader.Read(item).Diagnostics.Select(static d => d.Id));

			ids.AddRange(FilterBlockBuilder.Build(example.Level, example.Items).Diagnostics.Select(static d => d.Id));

			return [..ids.Distinct().OrderBy(static i => i, StringComparer.Ordinal)];
		}

		[TestMethod]
		public void EveryFilterExampleReadsAsTheTextSays()
		{
			var problems = new List<string>();
			var read     = 0;
			var errors   = 0;

			foreach (var file in Documents())
			{
				var fileRead   = 0;
				var fileErrors = 0;

				foreach (var example in Examples(file))
				{
					fileRead++;

					var actual = Read(example);

					if (example.ExpectedIds.Length > 0)
					{
						fileErrors++;

						if (!actual.SequenceEqual(example.ExpectedIds))
							problems.Add($"{example.File}:{example.Line}: the text gives [{string.Join(", ", actual)}], expected [{string.Join(", ", example.ExpectedIds)}]: {string.Join(" | ", example.Items).Replace("\n", "\\n")}");
					}
					else if (actual.Count > 0)
					{
						problems.Add($"{example.File}:{example.Line}: [{string.Join(", ", actual)}] for an example that should read: {string.Join(" | ", example.Items).Replace("\n", "\\n")}");
					}
				}

				if (fileRead > 0)
					TestContext.WriteLine($"{Relative(file)}: {fileRead} filter examples, {fileErrors} of them examples of an error.");

				read   += fileRead;
				errors += fileErrors;
			}

			TestContext.WriteLine($"Total: {read} filter examples read, {errors} of them examples of an error.");

			Assert.AreEqual(0, problems.Count, string.Join("\n", problems));

			// A reader that found nothing to read would pass for ever.
			Assert.IsGreaterThan(40, read, "The documentation is expected to hold dozens of filter examples.");
			Assert.IsGreaterThan(0, errors);
		}

		[TestMethod]
		public void TheThreeKindsOfPageHoldExamples()
		{
			Assert.IsGreaterThan(0, Examples(Path.Combine(Root, "docs", "wiki", "Target-filters.md")).Count, "wiki");
			Assert.IsGreaterThan(0, Examples(Path.Combine(Root, "Skills", "aspectgenerator", "docs", "target-filters.md")).Count, "skill");
			Assert.IsGreaterThan(0, Examples(Path.Combine(Root, "README.md")).Count, "README");
		}

		[TestMethod]
		public void AnExampleOfAnErrorMustBeAnError()
		{
			var wrongId = new Example("x", 1, CascadeLevel.Assembly, ["not (public or static)"], ["AG0221"]);

			Assert.IsFalse(Read(wrongId).SequenceEqual(wrongId.ExpectedIds));

			var fine = new Example("x", 1, CascadeLevel.Assembly, ["public"], ["AG0220"]);

			Assert.IsFalse(Read(fine).SequenceEqual(fine.ExpectedIds));
		}

		static string DocumentText(params string[] path)
		{
			return File.ReadAllText(Path.Combine([Root, ..path]));
		}

		static IEnumerable<string> CurrentIds()
		{
			return typeof(AspectDiagnosticID)
				.GetFields(BindingFlags.Public | BindingFlags.Static)
				.Where(static f => f.IsLiteral)
				.Select(static f => (string)f.GetRawConstantValue()!)
				.OrderBy(static i => i, StringComparer.Ordinal);
		}

		[TestMethod]
		public void EveryDiagnosticIsDescribedInTheDiagnosticsPages()
		{
			var wiki  = DocumentText("docs", "wiki", "Diagnostics.md");
			var skill = DocumentText("Skills", "aspectgenerator", "docs", "diagnostics.md");
			var ids   = CurrentIds().ToList();

			Assert.IsGreaterThan(20, ids.Count);

			foreach (var id in ids)
			{
				StringAssert.Contains(wiki, $"`{id}`", $"The Diagnostics wiki page does not describe {id}.");
				StringAssert.Contains(skill, $"`{id}`", $"The skill's diagnostics guide does not describe {id}.");
			}
		}

		[TestMethod]
		public void NoPageNamesADiagnosticThatDoesNotExist()
		{
			var current = CurrentIds().ToHashSet(StringComparer.Ordinal);
			var retired = _retiredIds.ToHashSet(StringComparer.Ordinal);
			var unknown = new List<string>();

			foreach (var file in Documents())
			{
				foreach (Match match in Regex.Matches(File.ReadAllText(file), @"\bAG\d{4}\b"))
				{
					if (!current.Contains(match.Value) && !retired.Contains(match.Value))
						unknown.Add($"{Relative(file)}: {match.Value}");
				}
			}

			Assert.AreEqual(0, unknown.Count, string.Join("\n", unknown.Distinct()));

			Assert.IsFalse(current.Overlaps(retired), "A retired id is in use.");
		}

		[TestMethod]
		public void TheRetiredIdsAreListedAsRetired()
		{
			var wiki   = DocumentText("docs", "wiki", "Diagnostics.md");
			var start  = wiki.IndexOf("### Retired ids", StringComparison.Ordinal);
			var retired = wiki[start..];

			Assert.IsGreaterThanOrEqualTo(0, start);

			foreach (var id in _retiredIds)
				StringAssert.Contains(retired, $"`{id}`");

			// An id that is described as a current diagnostic is not retired.
			var table = wiki[..start];

			foreach (var id in _retiredIds)
				Assert.IsFalse(Regex.IsMatch(table, $@"^\| `{id}` \|", RegexOptions.Multiline), $"{id} is retired and stands in a table of current diagnostics.");
		}

		static string Slug(string heading)
		{
			var text = Regex.Replace(heading.Trim().ToLowerInvariant(), @"[^\p{L}\p{N} _-]", "");

			return text.Replace(' ', '-');
		}

		[TestMethod]
		public void RelativeLinksOfTheWikiResolve()
		{
			var wiki     = Path.Combine(Root, "docs", "wiki");
			var problems = new List<string>();
			var links    = 0;

			foreach (var file in Directory.GetFiles(wiki, "*.md"))
			{
				var text = File.ReadAllText(file).Replace("\r\n", "\n");

				// Code is not Markdown: links and headings inside fences and code spans are not links and headings.
				var prose = Regex.Replace(text, @"(?ms)^```[^\n]*\n.*?^```[^\n]*(?:\n|\z)", "");

				foreach (Match link in Regex.Matches(Regex.Replace(prose, @"`[^`\n]*`", ""), @"\]\((?<target>[^)\s]+)\)"))
				{
					var target = link.Groups["target"].Value;

					if (Regex.IsMatch(target, @"\A[A-Za-z][A-Za-z0-9+.-]*:"))
						continue;

					links++;

					var hash   = target.IndexOf('#');
					var page   = hash < 0 ? target : target[..hash];
					var anchor = hash < 0 ? null : target[(hash + 1)..];
					var path   = page.Length == 0 ? file : Path.Combine(wiki, page + ".md");

					if (!File.Exists(path))
					{
						problems.Add($"{Relative(file)}: no page for '{target}'.");
						continue;
					}

					if (anchor is null)
						continue;

					var targetText = File.ReadAllText(path).Replace("\r\n", "\n");
					var headings   = Regex.Matches(Regex.Replace(targetText, @"(?ms)^```[^\n]*\n.*?^```[^\n]*(?:\n|\z)", ""), @"(?m)^#{1,6} (?<heading>.+)$")
						.Select(static m => Slug(m.Groups["heading"].Value))
						.ToHashSet(StringComparer.Ordinal);

					if (!headings.Contains(anchor))
						problems.Add($"{Relative(file)}: no heading for '{target}'.");
				}
			}

			Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
			Assert.IsGreaterThan(5, links, "The wiki is expected to link between its pages.");
		}
	}
}
