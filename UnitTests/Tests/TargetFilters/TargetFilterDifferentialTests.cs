using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// Reads every input of the golden corpus (the curated inputs, their mutations and the whole generated set) with the
	/// generated parser filters use and with the hand-written splitter it replaced, and compares everything observable:
	/// the diagnostics in order (id, message and severity), the plan, the compiled rules with their polarity, and the
	/// decision and the deciding rule for every target. Every difference must be one the change intends, told by a
	/// named class; any other fails the test.
	/// </summary>
	[TestClass]
	public class TargetFilterDifferentialTests
	{
		public TestContext TestContext { get; set; } = null!;

		const int MaxReported = 20;

		/// <summary>The classes a difference may belong to, each with the test that tells it.</summary>
		static readonly (string Name, Func<TargetFilterProbe.Outcome, TargetFilterProbe.Outcome, bool> Holds)[] Classes =
		[
			("identical", static (legacy, gram) => Differences(legacy, gram, ignoreRuleMatchesNothing: false).Count == 0),

			// The generated parser's front tells a valid rule that can match no method (AG0210) and a name with white space
			// where no C# name has it (AG0211); the splitter did not. Nothing else may differ: the same rules, the same
			// decisions, the other diagnostics the same and in order.
			("AG0210 or AG0211 added", static (legacy, gram) =>
				!legacy.Ids.Any(IsRuleNote) &&
				gram.Ids.Any(IsRuleNote) &&
				Differences(legacy, gram, ignoreRuleMatchesNothing: true).Count == 0),
		];

		[TestMethod]
		public void CuratedInputsReadTheSameByBothReadersTest()
		{
			Compare("curated", TargetFilterCorpus.Curated().Select(static i => i.Text));
		}

		[TestMethod]
		public void MutatedInputsReadTheSameByBothReadersTest()
		{
			var curated = TargetFilterCorpus.Curated().Select(static i => i.Text).ToArray();

			Compare("mutations", TargetFilterCorpus.Mutations(curated, TargetFilterCorpus.MutationsPerInput, TargetFilterCorpus.MutationSeed));
		}

		[TestMethod]
		public void GeneratedInputsReadTheSameByBothReadersTest()
		{
			Compare("generated", TargetFilterCorpus.Generate(TargetFilterCorpus.GeneratedCount));
		}

		[TestMethod]
		public void EscapesAroundWordsReadTheSameByBothReadersTest()
		{
			Compare("escapes around words", EscapesAroundWords());
		}

		[TestMethod]
		public void SpacedInputsReadTheSameByBothReadersTest()
		{
			Compare("spaced", TargetFilterSpacedInputs.Generate());
		}

		/// <summary>
		/// Lines whose words are split at white space (modifier lines, method patterns, condition items of words), with an
		/// escape before, after or in place of a word: the splitter drops an escape that begins a word together with what it
		/// escapes, which the mutations seldom produce next to a modifier word.
		/// </summary>
		static IEnumerable<string> EscapesAroundWords()
		{
			string[] lines =
			[
				"public", "public static", "protected internal", "public static Save", "static Acme.Core.Service.Save(int) : void",
				"public; method: Save", "method: Save; public static", "- public static", "pattern: public Save", "Save : void", "public Save*",
			];
			string[] escapes = ["\\x", "\\&", "\\ ", "\\\\", "\\", "\\:", "\\;", "\\x\\y", "\\<", "\\p"];

			foreach (var line in lines)
			{
				var words = line.Split(' ');

				foreach (var escape in escapes)
					for (var at = 0; at <= words.Length; at++)
					{
						// Standing alone between two words, and glued to the front of the next word.
						yield return string.Join(" ", words.Take(at).Append(escape).Concat(words.Skip(at)));

						if (at < words.Length)
							yield return string.Join(" ", words.Take(at).Append(escape + words[at]).Concat(words.Skip(at + 1)));
					}
			}
		}

		void Compare(string corpus, IEnumerable<string> inputs)
		{
			var counts       = Classes.ToDictionary(static c => c.Name, static _ => 0, StringComparer.Ordinal);
			var unclassified = new List<string>();
			var total        = 0;
			var failures     = 0;

			foreach (var input in inputs)
			{
				total++;

				var legacy = TargetFilterProbe.Probe(input, TargetFilterProbe.ReadLegacy);
				var gram   = TargetFilterProbe.Probe(input, TargetFilterProbe.Read);
				var found  = Classes.FirstOrDefault(c => c.Holds(legacy, gram)).Name;

				if (found is not null)
				{
					counts[found]++;
					continue;
				}

				failures++;

				if (unclassified.Count < MaxReported)
					unclassified.Add(
						$"input:  {TargetFilterProbe.Escape(input)}{Environment.NewLine}" +
						string.Concat(Differences(legacy, gram, ignoreRuleMatchesNothing: false).Select(static d => "  " + d + Environment.NewLine)));
			}

			foreach (var (name, count) in counts)
				TestContext.WriteLine($"{corpus} {name}: {count}");

			TestContext.WriteLine($"{corpus} unclassified: {failures}");
			TestContext.WriteLine($"{corpus} total: {total}");

			Assert.IsTrue(total > 0, $"The {corpus} set is empty.");
			Assert.AreEqual(
				0,
				failures,
				$"{failures} of {total} {corpus} inputs read differently by the two readers, in no intended class. First {unclassified.Count}:{Environment.NewLine}{string.Join(Environment.NewLine, unclassified)}");
		}

		/// <summary>What differs between the two readings, one line per field.</summary>
		static List<string> Differences(TargetFilterProbe.Outcome legacy, TargetFilterProbe.Outcome gram, bool ignoreRuleMatchesNothing)
		{
			var result = new List<string>();

			Field("exception", legacy.Exception ?? "-", gram.Exception ?? "-");
			Field("diagnostics", Diagnostics(legacy, ignoreRuleMatchesNothing), Diagnostics(gram, ignoreRuleMatchesNothing));
			Field("plan", TargetFilterProbe.FormatPlan(legacy.Plan), TargetFilterProbe.FormatPlan(gram.Plan));
			Field("rules", string.Join(" ;; ", legacy.RuleTexts), string.Join(" ;; ", gram.RuleTexts));
			Field("decisions", Decisions(legacy), Decisions(gram));

			return result;

			void Field(string name, string expected, string actual)
			{
				if (!string.Equals(expected, actual, StringComparison.Ordinal))
					result.Add($"{name}: legacy {TargetFilterProbe.Escape(expected)} | gram {TargetFilterProbe.Escape(actual)}");
			}
		}

		static string Diagnostics(TargetFilterProbe.Outcome outcome, bool ignoreRuleMatchesNothing)
		{
			return string.Join(
				" ;; ",
				outcome.Ids
					.Select((id, i) => (Id: id, Text: $"{id} {outcome.Severities[i]} {outcome.Messages[i]}"))
					.Where(d => !ignoreRuleMatchesNothing || !IsRuleNote(d.Id))
					.Select(static d => d.Text));
		}

		static bool IsRuleNote(string id)
		{
			return id is AspectDiagnosticID.AspectFilterRuleMatchesNothing or AspectDiagnosticID.AspectFilterNameWithWhiteSpace;
		}

		static string Decisions(TargetFilterProbe.Outcome outcome)
		{
			return string.Join(",", outcome.Decisions.Select(static d => d.Kind + d.RuleIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		}
	}
}
