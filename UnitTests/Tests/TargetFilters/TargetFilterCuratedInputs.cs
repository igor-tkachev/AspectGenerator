using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// The curated inputs: every filter string the repository shows, extracted mechanically, plus the hand cases.
	/// The extracted part is stored as a list file; a test compares a fresh extraction of the documentation with it.
	/// </summary>
	static class TargetFilterCuratedInputs
	{
		public const string ClassSource    = "source";     // TargetFilter / DefaultTargetFilter values in C# under UnitTests and Examples (also inside embedded test sources)
		public const string ClassDocCode   = "doc-code";   // the same, in csharp blocks of Markdown pages
		public const string ClassDocBlock  = "doc-block";  // a whole text block of a target-filter page
		public const string ClassDocLine   = "doc-line";   // one line of such a block
		public const string ClassHand      = "hand";       // written by hand, one per documented behaviour

		public sealed record Input(string Class, string Text);

		static readonly string[] _filterProperties = ["TargetFilter", "DefaultTargetFilter"];

		public static string RepositoryRoot
		{
			get
			{
				for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
					if (File.Exists(Path.Combine(dir.FullName, "AspectGenerator.slnx")))
						return dir.FullName;

				throw new InvalidOperationException("AspectGenerator.slnx was not found above " + AppContext.BaseDirectory);
			}
		}

		public static string ListPath
		{
			get
			{
				return Path.Combine(RepositoryRoot, "UnitTests", "Tests", "TargetFilters", "curated-inputs.txt");
			}
		}

		public static string BaselinePath(string name)
		{
			return Path.Combine(RepositoryRoot, "Baselines", "TargetFilters", name);
		}

		/// <summary>The extracted inputs of the C# files under UnitTests and Examples, then of the Markdown pages.</summary>
		public static List<Input> Extract()
		{
			var result = new List<Input>();
			var seen   = new HashSet<string>(StringComparer.Ordinal);
			var root   = RepositoryRoot;

			foreach (var file in EnumerateFiles(root, ["UnitTests", "Examples"], "*.cs"))
				foreach (var text in ExtractFromCSharp(File.ReadAllText(file).Replace("\r\n", "\n"), 0))
					Add(result, seen, ClassSource, text);

			foreach (var file in EnumerateMarkdown(root))
				foreach (var input in ExtractFromMarkdown(root, file))
					Add(result, seen, input.Class, input.Text);

			return result;
		}

		/// <summary>Only the Markdown part of <see cref="Extract"/>: what the drift test compares with the list.</summary>
		public static List<Input> ExtractDocumentation()
		{
			var root = RepositoryRoot;

			return EnumerateMarkdown(root).SelectMany(file => ExtractFromMarkdown(root, file)).ToList();
		}

		static void Add(List<Input> result, HashSet<string> seen, string @class, string text)
		{
			if (text.Trim().Length > 0 && seen.Add(text))
				result.Add(new Input(@class, text));
		}

		static IEnumerable<string> EnumerateFiles(string root, string[] directories, string pattern)
		{
			var files = new List<string>();

			foreach (var directory in directories)
			{
				var full = Path.Combine(root, directory);

				if (!Directory.Exists(full))
					continue;

				foreach (var file in Directory.EnumerateFiles(full, pattern, SearchOption.AllDirectories))
				{
					var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
					var parts    = relative.Split('/');

					// Build output, and this corpus itself (its hand cases are not repository content).
					if (parts.Contains("bin") || parts.Contains("obj") || relative.StartsWith("UnitTests/Tests/TargetFilters/", StringComparison.Ordinal))
						continue;

					files.Add(relative);
				}
			}

			files.Sort(StringComparer.Ordinal);

			return files.Select(f => Path.Combine(root, f));
		}

		static IEnumerable<string> EnumerateMarkdown(string root)
		{
			var files = new List<string> { "README.md" };

			foreach (var directory in new[] { "docs", "Skills" })
			{
				var full = Path.Combine(root, directory);

				if (Directory.Exists(full))
					foreach (var file in Directory.EnumerateFiles(full, "*.md", SearchOption.AllDirectories))
						files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
			}

			files.Sort(StringComparer.Ordinal);

			return files.Select(f => Path.Combine(root, f));
		}

		/// <summary>Pages that describe the filter syntax: their text blocks are filters, grammar and examples alike.</summary>
		static bool IsFilterPage(string relativePath)
		{
			return relativePath is "docs/wiki/Target-filters.md" or "Skills/aspectgenerator/docs/target-filters.md" or "README.md";
		}

		static IEnumerable<Input> ExtractFromMarkdown(string root, string file)
		{
			var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
			var lines    = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');

			for (var i = 0; i < lines.Length; i++)
			{
				var trimmed = lines[i].TrimStart();

				if (!trimmed.StartsWith("```", StringComparison.Ordinal))
					continue;

				var language = trimmed[3..].Trim();
				var body     = new List<string>();

				for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++)
					body.Add(lines[i]);

				var text = string.Join("\n", body);

				if (language == "csharp")
				{
					foreach (var value in ExtractFromCSharp(text, 0))
						yield return new Input(ClassDocCode, value);
				}
				else if (language == "text" && IsFilterPage(relative))
				{
					yield return new Input(ClassDocBlock, text);

					foreach (var line in body)
						if (line.Trim().Length > 0)
							yield return new Input(ClassDocLine, line);
				}
			}
		}

		/// <summary>
		/// The values assigned to TargetFilter or DefaultTargetFilter (a string, a concatenation of strings, or a
		/// collection of strings joined by line breaks), and, recursively, the same inside every string literal that
		/// itself holds C# source mentioning a filter property.
		/// </summary>
		static IEnumerable<string> ExtractFromCSharp(string code, int depth)
		{
			var tree = CSharpSyntaxTree.ParseText(code, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
			var root = tree.GetRoot();

			foreach (var node in root.DescendantNodes())
			{
				var value = node switch
				{
					AttributeArgumentSyntax { NameEquals.Name.Identifier.ValueText: var name, Expression: var expression } when _filterProperties.Contains(name) => expression,
					AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.ValueText: var name }, Right: var expression } when _filterProperties.Contains(name) => expression,
					_ => null
				};

				if (value is null)
					continue;

				var parts = new List<string>();

				if (TryReadStrings(value, parts) && parts.Count > 0)
					yield return string.Join("\n", parts);
			}

			foreach (var value in ExtractPlaceholderValues(root))
				yield return value;

			if (depth >= 3)
				yield break;

			foreach (var token in root.DescendantTokens())
			{
				if (!token.IsKind(SyntaxKind.StringLiteralToken) && !token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken) && !token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken))
					continue;

				var text = token.ValueText;

				if (_filterProperties.Any(p => text.Contains(p, StringComparison.Ordinal)))
					foreach (var inner in ExtractFromCSharp(text, depth + 1))
						yield return inner;
			}
		}

		/// <summary>
		/// Test sources that carry a filter placeholder and fill it in from the test: the literal given to
		/// <c>Replace("FILTER_RULE", ...)</c> or <c>Replace("/*FILTER*/", ...)</c>, the literals of a foreach that feeds
		/// such a call, and the literal given to <c>ExternalTargetSource(...)</c>.
		/// </summary>
		static IEnumerable<string> ExtractPlaceholderValues(SyntaxNode root)
		{
			var result = new List<string>();

			foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
			{
				var arguments = invocation.ArgumentList.Arguments;
				var name      = invocation.Expression switch
				{
					MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
					IdentifierNameSyntax identifier     => identifier.Identifier.ValueText,
					_                                   => ""
				};

				if (name == "ExternalTargetSource" && arguments.Count == 1)
				{
					AddLiteral(arguments[0].Expression);
				}
				else if (name == "Replace" && arguments.Count >= 2 && arguments[0].Expression is LiteralExpressionSyntax placeholder && IsPlaceholder(placeholder))
				{
					if (arguments[1].Expression is LiteralExpressionSyntax)
					{
						AddLiteral(arguments[1].Expression);
					}
					else if (arguments[1].Expression is IdentifierNameSyntax variable)
					{
						// foreach (var rule in new[] { "...", "..." }) { ... Replace("FILTER_RULE", rule) ... }
						foreach (var loop in invocation.Ancestors().OfType<ForEachStatementSyntax>())
							if (loop.Identifier.ValueText == variable.Identifier.ValueText)
							{
								var parts = new List<string>();

								if (TryReadStrings(loop.Expression, parts))
									result.AddRange(parts);

								break;
							}
					}
				}
			}

			return result;

			void AddLiteral(ExpressionSyntax expression)
			{
				if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
					result.Add((string)literal.Token.Value!);
			}

			static bool IsPlaceholder(LiteralExpressionSyntax literal)
			{
				return literal.IsKind(SyntaxKind.StringLiteralExpression) && (string)literal.Token.Value! is "FILTER_RULE" or "/*FILTER*/";
			}
		}

		static bool TryReadStrings(ExpressionSyntax expression, List<string> parts)
		{
			switch (expression)
			{
				case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
					parts.Add((string)literal.Token.Value!);
					return true;

				case ParenthesizedExpressionSyntax parenthesized:
					return TryReadStrings(parenthesized.Expression, parts);

				case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
				{
					var left  = new List<string>();
					var right = new List<string>();

					if (!TryReadStrings(binary.Left, left) || !TryReadStrings(binary.Right, right) || left.Count != 1 || right.Count != 1)
						return false;

					parts.Add(left[0] + right[0]);
					return true;
				}

				case CollectionExpressionSyntax collection:
					return TryReadElements(collection.Elements.Select(static e => e is ExpressionElementSyntax x ? x.Expression : null), parts);

				case ArrayCreationExpressionSyntax { Initializer: { } initializer }:
					return TryReadElements(initializer.Expressions, parts);

				case ImplicitArrayCreationExpressionSyntax implicitArray:
					return TryReadElements(implicitArray.Initializer.Expressions, parts);

				default:
					return false;
			}
		}

		static bool TryReadElements(IEnumerable<ExpressionSyntax?> elements, List<string> parts)
		{
			foreach (var element in elements)
			{
				var item = new List<string>();

				if (element is null || !TryReadStrings(element, item) || item.Count != 1)
					return false;

				parts.Add(item[0]);
			}

			return true;
		}

		public static void WriteList(IEnumerable<Input> inputs)
		{
			TargetFilterBaseline.WriteLines(ListPath, inputs.Select(static i => i.Class + "\t" + TargetFilterProbe.Escape(i.Text)));
		}

		static bool _listRefreshed;

		public static List<Input> ReadList()
		{
			// With AG_UPDATE_BASELINES=1 the list is re-extracted once, before the first test that reads it.
			if (TargetFilterBaseline.Update && !_listRefreshed)
			{
				_listRefreshed = true;
				WriteList(Extract());
			}

			var result = new List<Input>();

			foreach (var line in File.ReadAllText(ListPath).Replace("\r\n", "\n").Split('\n'))
			{
				if (line.Length == 0)
					continue;

				var tab = line.IndexOf('\t');

				result.Add(new Input(line[..tab], Unescape(line[(tab + 1)..])));
			}

			return result;
		}

		public static string Unescape(string text)
		{
			var sb = new StringBuilder(text.Length);

			for (var i = 0; i < text.Length; i++)
			{
				if (text[i] != '\\')
				{
					sb.Append(text[i]);
					continue;
				}

				i++;

				switch (text[i])
				{
					case '\\': sb.Append('\\'); break;
					case 't':  sb.Append('\t'); break;
					case 'r':  sb.Append('\r'); break;
					case 'n':  sb.Append('\n'); break;
					case 'u':
						sb.Append((char)Convert.ToInt32(text.Substring(i + 1, 4), 16));
						i += 4;
						break;
					default:
						throw new FormatException("Unknown escape \\" + text[i]);
				}
			}

			return sb.ToString();
		}
	}
}
