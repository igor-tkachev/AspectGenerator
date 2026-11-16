using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

using DotGram.Grammar.Binding;
using DotGram.Grammar.Model;
using DotGram.Grammar.Parsing;
using DotGram.Language;

namespace AspectGenerator.FilterV2.Harness;

/// <summary>
/// Reads every input of a corpus with the generated v2 parser and with DotGram's editor interpreter over the same grammar
/// text, and says where they differ; then times both on the adversarial inputs.
/// </summary>
/// <remarks>
/// <c>dotnet run -- &lt;grammar&gt; &lt;corpus&gt; [--scaling] [--verbose]</c>. A corpus line is one input, with <c>\n</c>,
/// <c>\r</c>, <c>\t</c> and <c>\\</c> as escapes; <c>## name</c> starts a group, <c>//</c> a comment.
/// </remarks>
static class Program
{
	static int Main(string[] args)
	{
		if (args.Length < 2)
		{
			Console.Error.WriteLine("usage: <grammar> <corpus> [--scaling] [--verbose]");
			return 2;
		}

		if (args[0] == "--probe")
		{
			// --probe <grammar> <publication> <input>: what the editor core makes of one input, extent by extent.
			var probe  = Editor.Load(File.ReadAllText(args[1]));
			var result = DslRecognitionTrace.Recognize(probe.Graph, probe.Publications[args[2]], args[3].Replace("\\n", "\n"));

			Console.WriteLine($"{result.Status} at {result.FailurePosition}: {string.Join(" | ", result.Expected)}");

			foreach (var extent in result.Extents)
				Console.WriteLine($"  {extent.Rule.Name}{(extent.Capture is null ? "" : "." + extent.Capture)} {extent.Position}+{extent.Length}");

			return 0;
		}

		var grammarText = File.ReadAllText(args[0]);
		var verbose     = args.Contains("--verbose");
		var editor      = Editor.Load(grammarText);
		var corpus      = Corpus.Read(args[1]);

		Console.WriteLine($"grammar: {args[0]}; corpus: {corpus.Count} inputs");
		Console.WriteLine($"editor graph: {editor.Graph.Rules.Count} rules, publications: {string.Join(", ", editor.Publications.Keys)}");
		Console.WriteLine();

		var failures = 0;

		foreach (var publication in new[] { "Item", "Strict" })
			failures += Parity(editor, publication, corpus, verbose);

		if (args.Contains("--scaling"))
			Scaling.Run(editor);

		return failures == 0 ? 0 : 1;
	}

	/// <summary>The outcome classes the comparison counts.</summary>
	enum Verdict
	{
		AgreeAccepted,
		AgreeRefused,
		RecoveredByParserOnly,
		DeepForBoth,
		Disagree,
	}

	static int Parity(Editor editor, string publication, IReadOnlyList<(string Group, string Input)> corpus, bool verbose)
	{
		var counts      = new Dictionary<Verdict, int>();
		var groupCounts = new SortedDictionary<string, (int Total, int Agree, int Recovered)>(StringComparer.Ordinal);
		var disagreements = new List<string>();

		foreach (var (group, input) in corpus)
		{
			var generated = Generated.Read(publication, input);
			var edited    = editor.Read(publication, input);
			var (verdict, why) = Compare(generated, edited);

			counts[verdict] = counts.GetValueOrDefault(verdict) + 1;

			var g = groupCounts.GetValueOrDefault(group);
			groupCounts[group] = (g.Total + 1,
				g.Agree + (verdict is Verdict.AgreeAccepted or Verdict.AgreeRefused or Verdict.DeepForBoth ? 1 : 0),
				g.Recovered + (verdict == Verdict.RecoveredByParserOnly ? 1 : 0));

			if (verdict == Verdict.Disagree || verbose)
				disagreements.Add($"  [{group}] {Corpus.Escape(input)}\n    {verdict}: {why}\n    parser: {generated}\n    editor: {edited}");
		}

		Console.WriteLine($"== publication {publication}");

		foreach (var verdict in Enum.GetValues<Verdict>())
			Console.WriteLine($"  {verdict,-22} {counts.GetValueOrDefault(verdict)}");

		Console.WriteLine("  by group (total / agree / recovered by the parser only):");

		foreach (var (group, g) in groupCounts)
			Console.WriteLine($"    {group,-28} {g.Total,4} {g.Agree,4} {g.Recovered,4}");

		foreach (var line in disagreements)
			Console.WriteLine(line);

		Console.WriteLine();

		return counts.GetValueOrDefault(Verdict.Disagree);
	}

	static (Verdict, string) Compare(Reading generated, Reading edited)
	{
		if (generated.Deep && edited.Deep)
			return (Verdict.DeepForBoth, "both refuse the depth");

		if (generated.Accepted && edited.Accepted)
		{
			var onlyParser = generated.Spans.Except(edited.Spans).OrderBy(static s => s).ToArray();
			var onlyEditor = edited.Spans.Except(generated.Spans).OrderBy(static s => s).ToArray();

			if (onlyParser.Length == 0 && onlyEditor.Length == 0)
				return (Verdict.AgreeAccepted, "same spans");

			return (Verdict.Disagree, $"spans differ: parser only {string.Join(" ", onlyParser)}; editor only {string.Join(" ", onlyEditor)}");
		}

		if (!generated.Accepted && !edited.Accepted)
		{
			if (generated.FailurePosition == edited.FailurePosition)
				return (Verdict.AgreeRefused, $"both refuse at {generated.FailurePosition}");

			return (Verdict.Disagree, $"refused at {generated.FailurePosition} by the parser, at {edited.FailurePosition} by the editor");
		}

		if (generated.Accepted && generated.Recovered && !edited.Accepted)
			return (Verdict.RecoveredByParserOnly, "the parser stepped over a bad line; the editor refuses the item");

		return (Verdict.Disagree, generated.Accepted ? "the parser accepts, the editor refuses" : "the editor accepts, the parser refuses");
	}
}

/// <summary>What one reader made of one input.</summary>
sealed record Reading(bool Accepted, bool Recovered, bool Deep, int FailurePosition, HashSet<(int Position, int Length)> Spans, Func<string> Detail)
{
	public override string ToString()
	{
		return Detail();
	}
}

static class Generated
{
	/// <summary>The form that only answers whether: it reads once and builds no refusal message.</summary>
	public static Reading ReadOut(string input)
	{
		try
		{
			return FilterV2Parser.TryParseStrict(input, out _)
				? new Reading(true, false, false, -1, [], () => "accepted")
				: new Reading(false, false, false, -1, [], () => "refused");
		}
		catch (InsufficientExecutionStackException)
		{
			return new Reading(false, false, true, -1, [], () => "nested too deeply");
		}
	}

	public static Reading Read(string publication, string input)
	{
		try
		{
			var match = publication == "Item" ? FilterV2Parser.TryParseItem(input) : FilterV2Parser.TryParseStrict(input);

			if (!match.IsSuccess)
				return new Reading(false, false, false, (int)match.Position, [], () => $"refused at {match.Position}: {match.Error}");

			var nodes = match.Value!.DescendantsAndSelf().ToArray();
			var spans = nodes.Select(static n => (n.Position, n.Length)).ToHashSet();
			var recovered = nodes.Any(static n => n.Name == "ErrorLine");

			return new Reading(true, recovered, false, -1, spans, () => $"accepted: {match.Value}");
		}
		catch (InsufficientExecutionStackException)
		{
			return new Reading(false, false, true, -1, [], () => "nested too deeply");
		}
	}
}

/// <summary>The grammar as DotGram's editors load it: parsed, bound and normalized by the shared language core.</summary>
sealed class Editor
{
	Editor(RecognitionGraph graph, Dictionary<string, Publication> publications, HashSet<RuleSymbol> treeRules)
	{
		Graph        = graph;
		Publications = publications;
		TreeRules    = treeRules;
	}

	public RecognitionGraph                Graph        { get; }
	public Dictionary<string, Publication> Publications { get; }
	public HashSet<RuleSymbol>             TreeRules    { get; }

	public static Editor Load(string grammarText)
	{
		// As DslEmbeddedSiteAnalysis.Prepare reads a language's grammar, except for the C# scanner: Prepare tokenizes
		// without one, and a grammar that builds values with @(...) is then refused with GRAM1007 and never highlighted.
		var parsed = GramParser.Parse(GramLexer.Tokenize(grammarText, RoslynCSharpScanner.Instance));
		var model  = GrammarBinder.Bind(parsed.File);
		var graph  = DslGrammarNormalizer.Normalize(model, []);

		foreach (var diagnostic in parsed.Diagnostics.Concat(model.Diagnostics).Concat(graph.Diagnostics))
			if (diagnostic.Severity == DotGram.Grammar.GramSeverity.Error)
				throw new InvalidOperationException($"the editor core refuses the grammar: {diagnostic.Id} {diagnostic.Message}");

		var publications = graph.Publications
			.Where(static p => p.Kind == PublishKind.Parse && !DslGrammarNormalizer.IsToolingPublication(p))
			.ToDictionary(static p => p.Rule.Name, StringComparer.Ordinal);

		// The rules whose value is one Tree; a line item hands its line on and covers the end of the line beside it.
		var treeRules = graph.Rules
			.Where(rule => graph.Types.TryGetValue(rule, out var type) && Regex.IsMatch(type, @"\bTree$") &&
				rule.Name is not ("LineItem" or "StrictLine"))
			.ToHashSet();

		return new Editor(graph, publications, treeRules);
	}

	public Reading Read(string publication, string input)
	{
		var result = DslRecognitionTrace.Recognize(Graph, Publications[publication], input);

		switch (result.Status)
		{
			case DslRecognitionStatus.Success:
				var spans = result.Extents
					.Where(e => e.Capture is null && TreeRules.Contains(e.Rule))
					.Select(static e => (e.Position, e.Length))
					.ToHashSet();

				return new Reading(true, false, false, -1, spans, () => $"accepted: {result.Extents.Count} extents");

			case DslRecognitionStatus.Failure:
				return new Reading(false, false, false, result.FailurePosition, [], () => $"refused at {result.FailurePosition}: expected {string.Join(" | ", result.Expected)}");

			default:
				return new Reading(false, false, true, -1, [], () => "unsupported (stack)");
		}
	}
}

static class Corpus
{
	public static IReadOnlyList<(string Group, string Input)> Read(string path)
	{
		var result = new List<(string, string)>();
		var group  = "-";

		foreach (var raw in File.ReadAllLines(path))
		{
			if (raw.StartsWith("## ", StringComparison.Ordinal))
			{
				group = raw[3..].Trim();
				continue;
			}

			if (raw.StartsWith("//", StringComparison.Ordinal) || raw.Length == 0)
				continue;

			result.Add((group, Unescape(raw)));
		}

		return result;
	}

	static string Unescape(string text)
	{
		var builder = new StringBuilder(text.Length);

		for (var i = 0; i < text.Length; i++)
		{
			if (text[i] == '\\' && i + 1 < text.Length)
			{
				var next = text[++i];

				builder.Append(next switch
				{
					'n'  => '\n',
					'r'  => '\r',
					't'  => '\t',
					'e'  => '\0',
					_    => next,
				});

				// `\e` is the empty input.
				if (next == 'e')
					builder.Length--;
			}
			else
			{
				builder.Append(text[i]);
			}
		}

		return builder.ToString();
	}

	public static string Escape(string text)
	{
		return text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
	}
}

static class Scaling
{
	const int EditorBudgetMs = 20_000;

	public static void Run(Editor editor)
	{
		Console.WriteLine("== scaling (ms; parser = median of 5 reads, editor = one read on a 1 MB stack, abandoned after 20 s)");
		Console.WriteLine($"  {"input",-34} {"chars",8} {"parser Item",12} {"parser Strict",14} {"Strict, out",12} {"editor Strict",14}  parser result / editor result");

		var cases = new List<(string Name, Func<int, string> Make, int[] Sizes)>
		{
			("malformed lines",          Malformed,   [1_000, 10_000, 100_000]),
			("valid lines",              Valid,       [1_000, 10_000, 100_000]),
			("valid, lines of a filter", ValidLines,  [1, 2, 3, 4, 5, 6, 8]),
			("one long name",            n => "method: " + new string('a', n),           [1_000, 10_000, 100_000]),
			("name, unclosed <",         n => "method: A<" + new string('a', n),         [1_000, 10_000, 100_000]),
			("{…} alternatives",         n => "method: {" + string.Join(",", Enumerable.Range(0, n).Select(static i => "A" + i)) + "}", [10, 100, 1_000]),
			("{…} alternatives, unclosed", n => "method: {" + string.Join(",", Enumerable.Range(0, n).Select(static i => "A" + i)), [10, 100, 1_000, 2_000, 4_000]),
			("nested parentheses",       n => new string('(', n) + "public" + new string(')', n),   [20, 200, 2_000]),
			("nested parentheses, open", n => new string('(', n) + "public",                       [20, 200, 2_000]),
			("nested <…>",               n => "type: " + string.Concat(Enumerable.Repeat("A<", n)) + "B" + new string('>', n), [20, 200, 2_000]),
		};

		foreach (var (name, make, sizes) in cases)
		{
			var editorGaveUp = false;

			foreach (var size in sizes)
			{
				var input = make(size);
				var (item, itemResult)     = Time(() => Generated.Read("Item", input));
				var (strict, _)            = Time(() => Generated.Read("Strict", input));
				var (strictOut, _)         = Time(() => Generated.ReadOut(input));
				var editorText             = "skipped";
				string editorResult        = "";

				if (!editorGaveUp)
				{
					var (ms, result) = TimeEditor(editor, input);

					if (ms is null)
					{
						editorGaveUp = true;
						editorText   = ">20000";
					}
					else
					{
						editorText   = ms.Value.ToString("0.00");
						editorResult = result!.Accepted ? "accepted" : result.Deep ? "deep" : $"refused at {result.FailurePosition}";
					}
				}

				var parserResult = itemResult.Accepted ? (itemResult.Recovered ? "recovered" : "accepted") : itemResult.Deep ? "deep" : $"refused at {itemResult.FailurePosition}";

				// A generator runs on the compiler's threads: what the parser makes of the input on a 1 MB stack.
				Reading? small = null;
				var smallThread = new Thread(() => small = Generated.Read("Item", input), 1024 * 1024);

				smallThread.Start();
				smallThread.Join();

				if (small!.Deep != itemResult.Deep)
					parserResult += small.Deep ? " (deep on 1 MB)" : " (read on 1 MB)";

				Console.WriteLine($"  {name + " " + size,-34} {input.Length,8} {item,12:0.000} {strict,14:0.000} {strictOut,12:0.000} {editorText,14}  {parserResult} / {editorResult}");
			}
		}

		Console.WriteLine();
	}

	static string Malformed(int length)
	{
		string[] lines =
		[
			"method: Save* and (public or",
			"- type: *Service<int,> or namespace: Shop.**",
			"public static Save*(int, ..) : Task<*",
			"attribute: Obsolete and not (public or private)",
			"method: Get*; static",
			"returns: {void,Task and param: ref",
		];

		return Fill(lines, length);
	}

	static string Valid(int length)
	{
		string[] lines =
		[
			"method: Save* and (public or internal)",
			"- type: *Service<int,*> or namespace: Shop.**",
			"public static **.*Service.Save*(int, .., CancellationToken) : Task<*>",
			"attribute: Obsolete and not public",
			"returns: {void,Task} and param: ref int",
		];

		return Fill(lines, length);
	}

	static string ValidLines(int count)
	{
		var builder = new StringBuilder();

		for (var i = 0; i < count; i++)
			builder.Append(i % 2 == 0 ? "method: Save* and public" : "- attribute: Obsolete").Append('\n');

		return builder.ToString();
	}

	static string Fill(string[] lines, int length)
	{
		var builder = new StringBuilder();

		for (var i = 0; builder.Length < length; i++)
			builder.Append(lines[i % lines.Length]).Append('\n');

		return builder.ToString();
	}

	static (double Ms, Reading Result) Time(Func<Reading> read)
	{
		read();

		var times  = new double[5];
		Reading? last = null;

		for (var i = 0; i < times.Length; i++)
		{
			var watch = Stopwatch.StartNew();

			last     = read();
			times[i] = watch.Elapsed.TotalMilliseconds;
		}

		Array.Sort(times);

		return (times[2], last!);
	}

	static (double? Ms, Reading? Result) TimeEditor(Editor editor, string input)
	{
		Reading? result = null;
		double   ms     = 0;

		var thread = new Thread(() =>
		{
			var watch = Stopwatch.StartNew();

			result = editor.Read("Strict", input);
			ms     = watch.Elapsed.TotalMilliseconds;
		}, 1024 * 1024)
		{
			IsBackground = true,
		};

		thread.Start();

		return thread.Join(EditorBudgetMs) ? (ms, result) : (null, null);
	}
}
