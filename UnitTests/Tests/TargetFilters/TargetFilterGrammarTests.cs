using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// The generated parser's readings, one publication at a time: what each reads and the spans it reports. What the
	/// readings mean is pinned by the golden and the differential tests; here is how a line is cut.
	/// </summary>
	[TestClass]
	public class TargetFilterGrammarTests
	{
		public TestContext TestContext { get; set; } = null!;

		static TargetFilterLineSyntax Line(string text)
		{
			var match = TargetFilterParser.TryParseLine(text);

			Assert.IsTrue(match.IsSuccess, $"'{text}' was refused: {match.Error}");

			return match.Value;
		}

		static string Text(string source, TextSpan span)
		{
			return source.Substring(span.Start, span.Length);
		}

		static string[] Texts(string source, IEnumerable<TextSpan> spans)
		{
			return spans.Select(s => Text(source, s)).ToArray();
		}

		[TestMethod]
		public void OperatorAndMatcherPrefixAreReadOffTheFrontTest()
		{
			var text = "- contains: Save Item";
			var line = Line(text);

			Assert.AreEqual(TargetFilterLinePrefix.Exclude, line.Prefix);
			Assert.AreEqual("-", Text(text, line.PrefixSpan));
			Assert.AreEqual(TargetFilterMatcher.Contains, line.Matcher);
			Assert.AreEqual("contains:", Text(text, line.MatcherSpan));
			Assert.IsInstanceOfType<TextBodySyntax>(line.Body);
			Assert.AreEqual("Save Item", Text(text, line.Body.Span));

			// Matcher names compare ignoring case, with white space before the colon.
			Assert.AreEqual(TargetFilterMatcher.Regex,   Line("REGEX :^Save").Matcher);
			Assert.AreEqual(TargetFilterMatcher.Pattern, Line("Pattern: Save").Matcher);

			// Only one operator is read; a word that is no matcher name is not a prefix.
			Assert.AreEqual(TargetFilterMatcher.Native, Line("-& method: Save").Matcher);
			Assert.AreEqual(TargetFilterMatcher.Native, Line("other: Save").Matcher);
		}

		[TestMethod]
		public void NativeRuleIsReadAsTheLanguageDecidesTest()
		{
			Assert.IsInstanceOfType<ConditionLineSyntax>(Line("method: Save").Body);         // a condition key
			Assert.IsInstanceOfType<ConditionLineSyntax>(Line("Save; Load").Body);           // a top-level semicolon
			Assert.IsInstanceOfType<ConditionLineSyntax>(Line("public  static").Body);       // only modifier words
			Assert.IsInstanceOfType<MethodPatternSyntax>(Line("Save<a;b>").Body);            // a semicolon inside <...>
			Assert.IsInstanceOfType<MethodPatternSyntax>(Line("public static Save").Body);
			Assert.IsInstanceOfType<MethodPatternSyntax>(Line("public Save* : System.Void").Body);
			Assert.IsInstanceOfType<EmptyBodySyntax>(Line("-").Body);
			Assert.IsInstanceOfType<EmptyBodySyntax>(Line("contains:").Body);

			var unknown = "Save : System.Void; method: Save";
			var body    = (UnknownKeyBodySyntax)Line(unknown).Body;

			Assert.AreEqual("Save", Text(unknown, body.Key));

			// After pattern: a word that is not a key is a method name.
			Assert.IsInstanceOfType<MethodPatternSyntax>(Line("pattern: Save : System.Void").Body);
		}

		[TestMethod]
		public void ConditionItemsAndValuesCarryTheirSpansTest()
		{
			var text  = "  method :  Save* | Load & *Async ;  ; public static ";
			var items = ((ConditionLineSyntax)TargetFilterParser.TryParseLine(text).Value.Body).Items;

			CollectionAssert.AreEqual(new[] { "  method :  Save* | Load & *Async ", "  ", " public static " }, Texts(text, items));

			// An item is read again over its trimmed span; positions stay those of the line.
			var item  = new TextSpan(2, "method :  Save* | Load & *Async".Length);
			var key   = (KeyItemSyntax)TargetFilterParser.TryParseConditionItem(text, item.Start, item.Length).Value;

			Assert.AreEqual("method", Text(text, key.Key));
			Assert.AreEqual("Save* | Load & *Async", Text(text, key.Value));

			var value = TargetFilterParser.TryParseConditionValue(text, key.Value.Start, key.Value.Length).Value;

			CollectionAssert.AreEqual(new[] { "Save* ", " Load & *Async" }, value.Alternatives.Select(a => Text(text, a.Span)).ToArray());
			CollectionAssert.AreEqual(new[] { " Load ", " *Async" }, Texts(text, value.Alternatives[1].Operands));

			var words = (WordsItemSyntax)TargetFilterParser.TryParseConditionItem(text, text.IndexOf("public", StringComparison.Ordinal), "public static".Length).Value;

			CollectionAssert.AreEqual(new[] { "public", "static" }, Texts(text, words.Words.Words));
		}

		[TestMethod]
		public void ValueOperatorsCountAngleBracketsAndParenthesesApartTest()
		{
			// Each kind of bracket keeps its own count, and a closing one with nothing open is an ordinary character.
			AssertAlternatives("List<int> | Task", "List<int> ", " Task");
			AssertAlternatives("Dictionary<string|int> | (int|string)", "Dictionary<string|int> ", " (int|string)");
			AssertAlternatives("a > b | c ) d | e", "a > b ", " c ) d ", " e");
			AssertAlternatives("(<)> | b", "(<)> ", " b");          // both counts are back at nothing
			AssertAlternatives("<(> | b", "<(> | b");               // the parenthesis is still open
			AssertAlternatives(@"a\|b | c", @"a\|b ", " c");        // an escaped operator is a character

			static void AssertAlternatives(string value, params string[] expected)
			{
				var syntax = TargetFilterParser.TryParseConditionValue(value).Value;

				CollectionAssert.AreEqual(expected, syntax.Alternatives.Select(a => value.Substring(a.Span.Start, a.Span.Length)).ToArray(), value);
			}
		}

		[TestMethod]
		public void MethodPatternIsCutAtTheFirstColonAndTheFirstParenthesisTest()
		{
			var text    = "public static Acme.Repository<int>.Get<*>(string, ..., ref readonly int) : Task<int>";
			var pattern = (MethodPatternSyntax)Line(text).Body;

			Assert.AreEqual("public static Acme.Repository<int>.Get<*>", Text(text, pattern.Name));
			Assert.IsTrue(pattern.Parameters!.EndsThePattern);
			Assert.AreEqual("string, ..., ref readonly int", Text(text, pattern.Parameters.Inner));
			Assert.AreEqual(" Task<int>", Text(text, pattern.Returns!.Value));

			// The colon of a return type comes first even inside the parameter list; then the list does not close.
			var early = (MethodPatternSyntax)Line("Save(a:b)").Body;

			Assert.IsFalse(early.Parameters!.EndsThePattern);
			Assert.AreEqual("b)", Text("Save(a:b)", early.Returns!.Value));

			// Only white space may follow the list.
			Assert.IsFalse(((MethodPatternSyntax)Line("Save(int)x").Body).Parameters!.EndsThePattern);
			Assert.IsTrue(((MethodPatternSyntax)Line("Save(int)  : int").Body).Parameters!.EndsThePattern);
		}

		[TestMethod]
		public void WordsSplitAtWhiteSpaceOutsideBracketsAndRanksTest()
		{
			AssertWords("public  static\tSave", "public", "static", "Save");
			AssertWords("Dictionary<string, int> x", "Dictionary<string, int>", "x");
			AssertWords("int[ , ] x", "int[ , ]", "x");
			AssertWords(@"a\ b c", @"a\ b", "c");

			// A word never begins with an escape: the escape goes with what it escapes, and alone it is no word.
			AssertWords(@"\method: Save", "ethod:", "Save");
			AssertWords(@"\& method", "method");
			AssertWords(@"a \x b", "a", "b");

			static void AssertWords(string text, params string[] expected)
			{
				CollectionAssert.AreEqual(expected, Texts(text, TargetFilterParser.TryParseWords(text).Value.Words), text);
			}
		}

		[TestMethod]
		public void ParameterListsAndParametersTest()
		{
			Assert.IsTrue(TargetFilterParser.TryParseParameterList("(int, <(>)").Value.Parenthesized);
			Assert.IsFalse(TargetFilterParser.TryParseParameterList("(int)(string)").Value.Parenthesized);
			Assert.IsFalse(TargetFilterParser.TryParseParameterList("(int").Value.Parenthesized);

			var parts = "int[,], Dictionary<string,int>, ...";

			CollectionAssert.AreEqual(new[] { "int[,]", " Dictionary<string,int>", " ..." }, Texts(parts, TargetFilterParser.TryParseParameterParts(parts).Value.Parts));

			AssertParameter("_",                    ParameterSyntaxKind.Any,  null,                                        "_");
			AssertParameter("ref  readonly  int",   ParameterSyntaxKind.Type, AspectFilters.ParameterModifier.RefReadOnly, "int");
			AssertParameter("ref readonly",         ParameterSyntaxKind.Type, AspectFilters.ParameterModifier.Ref,         "readonly");
			AssertParameter("ref\tint",             ParameterSyntaxKind.Type, null,                                        "ref\tint");
			AssertParameter("params string[]",      ParameterSyntaxKind.Type, AspectFilters.ParameterModifier.Params,      "string[]");
			AssertParameter("in this int",          ParameterSyntaxKind.This, AspectFilters.ParameterModifier.In,          "this int");
			AssertParameter("int",                  ParameterSyntaxKind.Type, null,                                        "int");

			static void AssertParameter(string text, ParameterSyntaxKind kind, AspectFilters.ParameterModifier? modifier, string type)
			{
				var parameter = TargetFilterParser.TryParseParameter(text).Value;

				Assert.AreEqual(kind,     parameter.Kind,     text);
				Assert.AreEqual(modifier, parameter.Modifier, text);
				Assert.AreEqual(type,     Text(text, parameter.Type), text);
			}
		}

		[TestMethod]
		public void EveryPublicationReadsEveryTextTest()
		{
			// The readings are total: whatever a filter holds, each publication answers with a value and never refuses.
			var lines = TargetFilterCorpus.Curated().Select(static i => i.Text)
				.Concat(TargetFilterCorpus.Generate(5_000))
				.SelectMany(static t => t.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
				.Select(static l => l.Trim())
				.Distinct(StringComparer.Ordinal)
				.ToArray();

			var readings = new (string Name, Func<string, int, int, bool> Read)[]
			{
				("Line",           static (t, s, l) => TargetFilterParser.TryParseLine(t, s, l).IsSuccess),
				("ConditionItem",  static (t, s, l) => TargetFilterParser.TryParseConditionItem(t, s, l).IsSuccess),
				("Words",          static (t, s, l) => TargetFilterParser.TryParseWords(t, s, l).IsSuccess),
				("ConditionValue", static (t, s, l) => TargetFilterParser.TryParseConditionValue(t, s, l).IsSuccess),
				("Dotted",         static (t, s, l) => TargetFilterParser.TryParseDotted(t, s, l).IsSuccess),
				("ParameterList",  static (t, s, l) => TargetFilterParser.TryParseParameterList(t, s, l).IsSuccess),
				("ParameterParts", static (t, s, l) => TargetFilterParser.TryParseParameterParts(t, s, l).IsSuccess),
				("Parameter",      static (t, s, l) => TargetFilterParser.TryParseParameter(t, s, l).IsSuccess),
			};

			var random = new TargetFilterCorpus.SplitMix64(0x4147_5446_5445_0001UL);

			foreach (var line in lines)
				foreach (var (name, read) in readings)
				{
					Assert.IsTrue(read(line, 0, line.Length), $"{name} refused '{TargetFilterProbe.Escape(line)}'.");

					// And over any slice of it.
					var start  = random.Next(line.Length + 1);
					var length = random.Next(line.Length - start + 1);

					Assert.IsTrue(read(line, start, length), $"{name} refused '{TargetFilterProbe.Escape(line.Substring(start, length))}'.");
				}
		}

		[TestMethod]
		public void DeepNestingIsReadOnTheCallingThreadTest()
		{
			// Nesting is read on the thread that asks, on a 1 MB stack (the default of a Windows thread), and compiles to
			// what the splitter compiled, which counted the levels instead. Two hundred levels always fit.
			foreach (var input in DeepInputs(200))
				OnThread(1024 * 1024, () => AssertReadsAsTheSplitter(input, allowTooDeep: false));

			// Two thousand fit or are reported as too deep, depending on how large the frames of the platform's JIT and the
			// build's optimization make them; nothing else may happen.
			foreach (var input in DeepInputs(2_000))
				OnThread(1024 * 1024, () => AssertReadsAsTheSplitter(input, allowTooDeep: true));

			void AssertReadsAsTheSplitter(string input, bool allowTooDeep)
			{
				var legacy = TargetFilterProbe.Probe(input, TargetFilterProbe.ReadLegacy);
				var gram   = TargetFilterProbe.Probe(input, TargetFilterProbe.Read);

				Assert.IsNull(gram.Exception, $"{input[..20]}…: {gram.Exception}");

				if (allowTooDeep && gram.Ids.Length == 1 && gram.Messages[0].EndsWith("': The rule nests '<' or '(' too deeply to be read.", StringComparison.Ordinal))
				{
					TestContext.WriteLine($"{input[..20]}… ({input.Length} characters): too deep");
					return;
				}

				CollectionAssert.AreEqual(legacy.Ids, gram.Ids, input[..20]);
				CollectionAssert.AreEqual(legacy.Messages, gram.Messages, input[..20]);
				CollectionAssert.AreEqual(legacy.RuleTexts, gram.RuleTexts, input[..20]);
				Assert.AreEqual(legacy.Kinds, gram.Kinds, input[..20]);
				Assert.AreEqual(legacy.RuleCount, gram.RuleCount, input[..20]);
				TestContext.WriteLine($"{input[..20]}… ({input.Length} characters): read, {gram.Kinds.Count(static k => k != 'n')} targets decided");
			}

			// The deep rules are not all rules that decide nothing: some select targets of the corpus.
			Assert.IsTrue(DeepInputs(200).Any(static i => TargetFilterProbe.Probe(i, TargetFilterProbe.ReadLegacy).Kinds.Contains('i')), "No deep input selects a target.");
		}

		[TestMethod]
		public void RuleTooDeepForOneThreadIsReadAgainOnAnotherTest()
		{
			// Running out of stack is the thread's, not the filter's: it is not cached, and a thread with a stack large
			// enough reads the same filter.
			var input = "Save" + new string('<', 200_000);

			OnThread(256 * 1024, () =>
			{
				var ids = new List<string>();

				var set = AspectFilters.GetFilters(input, d => ids.Add(d.Id));
				CollectionAssert.AreEqual(new[] { AspectDiagnosticID.InvalidAspectFilterRule }, ids, $"rules: {set.Rules.Count}");
			});

			OnThread(1024 * 1024 * 1024, () =>
			{
				var ids = new List<string>();
				var set = AspectFilters.GetFilters(input, d => ids.Add(d.Id));

				Assert.AreEqual(0, ids.Count, string.Join(", ", ids));
				Assert.AreEqual(1, set.Rules.Count);
			});
		}

		[TestMethod]
		public void NestingTooDeepForTheStackIsReadOrReportedTest()
		{
			// Deeper than the stack allows, a reading is not moved to a thread of its own: the rule is reported as one that
			// cannot be read and left out, and the other rules stay. (Some readings keep their own stack on the heap and
			// read any depth; those compile to what the splitter compiled.)
			var reported = 0;

			foreach (var input in DeepInputs(200_000))
				OnThread(256 * 1024, () =>
				{
					var filter      = new[] { "contains: Save", input, "contains: Load" };
					var diagnostics = new List<AspectFilters.TargetFilterDiagnostic>();
					var set         = AspectFilters.GetFilters(filter, diagnostics.Add);

					if (diagnostics.Count == 0)
					{
						var legacy = AspectFilters.LegacyTargetFilterFront.GetFilters(filter);

						CollectionAssert.AreEqual(legacy.Rules.Select(static r => r.RuleText).ToArray(), set.Rules.Select(static r => r.RuleText).ToArray(), input[..20]);
						return;
					}

					reported++;

					CollectionAssert.AreEqual(new[] { AspectDiagnosticID.InvalidAspectFilterRule }, diagnostics.Select(static d => d.Id).ToArray(), input[..20]);
					StringAssert.EndsWith(diagnostics[0].Message, "': The rule nests '<' or '(' too deeply to be read.", input[..20]);
					CollectionAssert.AreEqual(new[] { "Save", "Load" }, set.Rules.Select(static r => r.RuleText).ToArray(), input[..20]);
				});

			Assert.IsTrue(reported > 0, "No input was too deep to read.");
		}

		static IEnumerable<string> DeepInputs(int depth)
		{
			var angles = new string('<', depth);
			var parens = new string('(', depth);

			yield return "method: Save | Load" + angles;
			yield return "returns: System.Void | List" + angles;
			yield return "Save" + angles;
			yield return "Save" + angles + "(int) : void";
			yield return "returns: List" + angles;
			yield return "namespace: Acme.Core; type: Repository" + angles;
			yield return "params: (" + parens + ")";
			yield return "params: " + parens + new string(')', depth);
			yield return "Save(" + parens + ")";
		}

		static void OnThread(int stackSize, Action action)
		{
			var failure = default(Exception);
			var thread  = new Thread(() =>
			{
				try
				{
					action();
				}
				catch (Exception ex)
				{
					failure = ex;
				}
			}, stackSize);

			thread.Start();
			thread.Join();

			if (failure is not null)
				throw new AssertFailedException(failure.Message, failure);
		}
	}
}
