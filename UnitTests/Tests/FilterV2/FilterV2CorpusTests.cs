using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using AspectGenerator.FilterV2;
using AspectGenerator.Tests.TargetFilters;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.FilterV2
{
	/// <summary>
	/// The corpus the grammar was first written against, held to the readings recorded then: for every input, its lines
	/// (read, or stepped over as bad), the conditions each read line holds and the kinds of error inside it. Where the
	/// grammar reads an input differently on purpose, the record says so and why. The whole reading of every input, tree
	/// and diagnostics, is pinned by a baseline beside it.
	/// </summary>
	[TestClass]
	public class FilterV2CorpusTests
	{
		static string Folder => Path.Combine(TargetFilterCuratedInputs.RepositoryRoot, "UnitTests", "Tests", "FilterV2");

		/// <summary>
		/// <c>corpus.txt</c>: one record per input, tab-separated: group, the input (with <c>\n</c>, <c>\r</c>, <c>\t</c>,
		/// <c>\\</c> escaped), the first reading, and, where the grammar now reads it otherwise, the reading now and why.
		/// </summary>
		static List<(string Group, string Input, string Escaped, string Expected, string? Why)> Records()
		{
			var result = new List<(string, string, string, string, string?)>();

			foreach (var line in File.ReadAllText(Path.Combine(Folder, "corpus.txt")).Replace("\r\n", "\n").Split('\n'))
			{
				if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
					continue;

				var fields = line.Split('\t');

				Assert.IsTrue(fields.Length is 3 or 5, $"A corpus record has 3 or 5 fields: {line}");

				result.Add(fields.Length == 3
					? (fields[0], Unescape(fields[1]), fields[1], fields[2], null)
					: (fields[0], Unescape(fields[1]), fields[1], fields[3], fields[4]));
			}

			return result;
		}

		[TestMethod]
		public void CorpusReadsAsRecordedTest()
		{
			var records     = Records();
			var differences = new List<string>();

			foreach (var (group, input, escaped, expected, _) in records)
			{
				var actual = Projection(input);

				if (actual != expected)
					differences.Add($"[{group}] {escaped}{Environment.NewLine}  expected: {expected}{Environment.NewLine}  actual:   {actual}");
			}

			Assert.IsTrue(records.Count >= 200, $"The corpus holds {records.Count} inputs.");
			Assert.AreEqual(0, differences.Count, string.Join(Environment.NewLine, differences));
		}

		[TestMethod]
		public void CorpusBaselineTest()
		{
			var lines = new List<string>();

			foreach (var (group, input, escaped, _, _) in Records())
			{
				var reading = FilterV2Reader.Read(input);

				lines.Add($"[{group}] {escaped}");
				lines.Add("  " + (reading.Item is null ? "(not read)" : FilterV2Print.Item(reading.Item)));

				foreach (var diagnostic in reading.Diagnostics)
					lines.Add($"  {diagnostic.Id} {diagnostic.Span.Start}+{diagnostic.Span.Length} {diagnostic.Message}");
			}

			TargetFilterBaseline.Check(Path.Combine("..", "FilterV2", "corpus.txt"), lines);
		}

		/// <summary>The lines, the conditions of each and the kinds of error inside it, as the first reading recorded them.</summary>
		internal static string Projection(string input)
		{
			var reading = FilterV2Reader.Read(input);

			if (reading.Item is not { } item)
				return reading.Diagnostics.Any(static d => d.Kind == FilterSyntaxErrorKind.NestedTooDeeply) ? "deep" : "refused";

			var parts = new List<string>();

			if (item.LeadingOperator is not null)
				parts.Add("lead");

			foreach (var line in item.Lines)
			{
				var nodes  = Descendants(line);
				var atoms  = nodes.Select(Atom).Where(static a => a is not null);
				var errors = nodes
					.Select(static n => n.Error)
					.Concat(nodes.OfType<AndExpressionSyntax>().SelectMany(static a => a.OperatorErrors))
					.Where(static e => e is not null)
					.Select(static e => ErrorName(e!.Kind))
					.Distinct()
					.OrderBy(static n => n, StringComparer.Ordinal);
				var bad    = line.Expression is ErrorExpressionSyntax { Error.Kind: FilterSyntaxErrorKind.Syntax };

				parts.Add($"{(bad ? "Bad" : "Line")}@{line.Span.Start}+{line.Span.Length} [{string.Join(" ", atoms)}] !{{{string.Join(",", errors)}}}");
			}

			return string.Join(" | ", parts);
		}

		static List<FilterSyntaxNode> Descendants(FilterSyntaxNode root)
		{
			var result = new List<FilterSyntaxNode>();
			var stack  = new Stack<FilterSyntaxNode>();

			stack.Push(root);

			while (stack.Count > 0)
			{
				var node = stack.Pop();

				result.Add(node);

				foreach (var child in node.Children.Reverse())
					stack.Push(child);
			}

			return result;
		}

		static string? Atom(FilterSyntaxNode node)
		{
			var name = node switch
			{
				KeyConditionSyntax                  => "Condition",
				ReferenceSyntax                     => "Reference",
				AnyValueSyntax                      => "AnyValue",
				WordRunSyntax { Words.Length: > 0 } => "Words",
				SignatureSyntax                     => "Signature",
				NotExpressionSyntax                 => "Not",
				GroupExpressionSyntax               => "Group",
				_                                   => null,
			};

			return name is null ? null : $"{name}@{node.Span.Start}+{node.Span.Length}";
		}

		/// <summary>The names the first reading gave the errors of the grammar.</summary>
		static string ErrorName(FilterSyntaxErrorKind kind)
		{
			return kind switch
			{
				FilterSyntaxErrorKind.Syntax              => "Line",
				FilterSyntaxErrorKind.Legacy              => "Legacy",
				FilterSyntaxErrorKind.Reserved            => "Reserved",
				FilterSyntaxErrorKind.UnknownKey          => "UnknownKey",
				FilterSyntaxErrorKind.InvalidAny          => "Any",
				FilterSyntaxErrorKind.NotBeforeGroup      => "NotGroup",
				FilterSyntaxErrorKind.GlobstarInSegment   => "GlobstarInSegment",
				FilterSyntaxErrorKind.EmptyAlternatives   => "EmptyAlternatives",
				FilterSyntaxErrorKind.OpenGeneric         => "OpenGeneric",
				FilterSyntaxErrorKind.LeadingOperator     => "LeadingOperator",
				_                                         => kind.ToString(),
			};
		}

		internal static string Unescape(string text)
		{
			var builder = new StringBuilder(text.Length);

			for (var i = 0; i < text.Length; i++)
			{
				if (text[i] == '\\' && i + 1 < text.Length)
				{
					var next = text[++i];

					builder.Append(next switch
					{
						'n' => '\n',
						'r' => '\r',
						't' => '\t',
						_   => next,
					});
				}
				else
				{
					builder.Append(text[i]);
				}
			}

			return builder.ToString();
		}
	}
}
