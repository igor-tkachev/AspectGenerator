using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// Runs a filter text through <c>AspectFilters.GetFilters</c> and describes everything observable about it:
	/// the diagnostic ids (never the messages), the plan, and the decision for every target of the fixed list.
	/// </summary>
	static class TargetFilterProbe
	{
		const string RuleIndexDigits = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

		public sealed class Decision
		{
			public required char Kind      { get; init; } // n: no decision, i: include, e: exclude
			public required int  RuleIndex { get; init; } // -1 when Kind is n
		}

		public sealed class Outcome
		{
			public required string[]                     Ids       { get; init; }
			public required AspectFilters.TargetFilterPlan? Plan   { get; init; }
			public required string?                      Exception { get; init; }
			public required int                          RuleCount { get; init; }
			public required int[]                        DiagnosticRuleLines { get; init; } // rule line each diagnostic belongs to, -1 when it cannot be told
			public required string[]                     RuleTexts { get; init; }            // compiled rules in order, polarity first: "+ text" include, "- text" exclude
			public required Decision[]                   Decisions { get; init; }

			public string Kinds
			{
				get
				{
					return new string(Decisions.Select(static d => d.Kind).ToArray());
				}
			}

			public Decision this[int target]
			{
				get
				{
					return Decisions[target];
				}
			}
		}

		public static Outcome Probe(string input)
		{
			var targets   = TargetFilterTargets.Entries;
			var ids       = new List<string>();
			var messages  = new List<string>();
			var decisions = new Decision[targets.Length];

			AspectFilters.TargetFilterSet set;

			try
			{
				set = AspectFilters.GetFilters(input, d =>
				{
					ids.Add(d.Id);
					messages.Add(d.Message);
				});
			}
			catch (Exception ex)
			{
				for (var i = 0; i < decisions.Length; i++)
					decisions[i] = new Decision { Kind = 'n', RuleIndex = -1 };

				return new Outcome { Ids = ids.ToArray(), Plan = null, Exception = ex.GetType().Name, RuleCount = 0, DiagnosticRuleLines = [], RuleTexts = [], Decisions = decisions };
			}

			var filters = set.Rules;

			for (var i = 0; i < targets.Length; i++)
			{
				var target     = targets[i].Target;
				var evaluation = set.Evaluate(target);

				if (evaluation.MatchedRule is null)
				{
					decisions[i] = new Decision { Kind = 'n', RuleIndex = -1 };
					continue;
				}

				var index = filters.Count - 1;

				while (index >= 0 && !filters[index].IsMatch(target))
					index--;

				if (index < 0 || filters[index].RuleText != evaluation.MatchedRule || filters[index].IsNegative != evaluation.IsExcluded)
					throw new InvalidOperationException($"The matched rule of '{input}' for target {i} could not be located among the compiled rules.");

				decisions[i] = new Decision { Kind = evaluation.IsMatch ? 'i' : 'e', RuleIndex = index };
			}

			return new Outcome
			{
				Ids                 = ids.ToArray(),
				Plan                = set.Plan,
				Exception           = null,
				RuleCount           = filters.Count,
				DiagnosticRuleLines = LocateDiagnostics(input, messages),
				RuleTexts           = filters.Select(static f => f.IsNegative ? (f.RuleText.StartsWith("- ", StringComparison.Ordinal) ? f.RuleText : "- " + f.RuleText) : "+ " + f.RuleText).ToArray(),
				Decisions           = decisions
			};
		}

		/// <summary>
		/// <c>input TAB diagnostics TAB plan TAB rules TAB decisions TAB rule indexes</c>.
		/// Diagnostics are <c>id@line</c> in report order, where line is the index of the rule line (comments and blank
		/// lines not counted) the diagnostic belongs to, or <c>?</c>; no source spans are recorded.
		/// Rules are the count and the compiled rules in order, each with its polarity. The rule index is the position
		/// among the compiled rules (a native condition group is one rule), written as one character per matched target.
		/// </summary>
		public static string FormatRecord(string input)
		{
			return FormatRecord(input, Probe(input));
		}

		public static string FormatRecord(string input, Outcome outcome)
		{
			var sb = new StringBuilder();

			sb.Append(Escape(input)).Append('\t');

			if (outcome.Exception is not null)
				sb.Append('!').Append(outcome.Exception);
			else if (outcome.Ids.Length == 0)
				sb.Append('-');
			else
				sb.Append(string.Join(",", outcome.Ids.Select((id, i) => id + "@" + (outcome.DiagnosticRuleLines[i] < 0 ? "?" : outcome.DiagnosticRuleLines[i].ToString(System.Globalization.CultureInfo.InvariantCulture)))));

			sb.Append('\t').Append(FormatPlan(outcome.Plan)).Append('\t');
			sb.Append(outcome.RuleCount.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(string.Join(",", outcome.RuleTexts.Select(EscapeElement)));
			sb.Append('\t').Append(outcome.Kinds).Append('\t');

			var any = false;

			foreach (var decision in outcome.Decisions)
			{
				if (decision.RuleIndex < 0)
					continue;

				any = true;
				sb.Append(decision.RuleIndex < RuleIndexDigits.Length ? RuleIndexDigits[decision.RuleIndex] : '~');
			}

			if (!any)
				sb.Append('-');

			return sb.ToString();
		}

		/// <summary>The rule lines as the filter reader sees them: split at any line break, trimmed, comments and blanks dropped.</summary>
		static List<string> RuleLines(string input)
		{
			return input.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
				.Select(static l => l.Trim())
				.Where(static l => l.Length > 0 && !l.StartsWith('#'))
				.ToList();
		}

		/// <summary>
		/// Finds the rule line a diagnostic belongs to from the text it quotes (every filter diagnostic quotes the rule,
		/// pattern, key or regex it is about). Several diagnostics that quote the same text take the matching lines in turn.
		/// </summary>
		static int[] LocateDiagnostics(string input, List<string> messages)
		{
			var lines  = RuleLines(input);
			var result = new int[messages.Count];
			var taken  = new Dictionary<string,int>(StringComparer.Ordinal);

			for (var d = 0; d < messages.Count; d++)
			{
				var message  = messages[d];
				var start    = message.IndexOf('\'');
				var end      = message.StartsWith("Unknown target filter condition key", StringComparison.Ordinal) ? message.LastIndexOf('\'') : message.IndexOf("': ", start + 1, StringComparison.Ordinal);

				if (start < 0 || end < start)
				{
					result[d] = -1;
					continue;
				}

				var fragment = message[(start + 1)..end];
				var matches  = new List<int>();
				var isKey    = message.StartsWith("Unknown target filter condition key", StringComparison.Ordinal);
				var keyLine  = isKey ? new System.Text.RegularExpressions.Regex("(?<![A-Za-z])" + System.Text.RegularExpressions.Regex.Escape(fragment) + @"\s*:", System.Text.RegularExpressions.RegexOptions.CultureInvariant) : null;

				for (var i = 0; i < lines.Count; i++)
				{
					var hit = isKey
						? keyLine!.IsMatch(lines[i])
						: fragment.Length == 0 ? EmptyBodyLine.IsMatch(lines[i]) : lines[i].Contains(fragment, StringComparison.Ordinal);

					if (hit)
						matches.Add(i);
				}

				if (matches.Count == 0)
				{
					// With a single rule line there is nothing to tell apart.
					result[d] = lines.Count == 1 ? 0 : -1;
					continue;
				}

				taken.TryGetValue(fragment, out var used);
				taken[fragment] = used + 1;
				result[d]       = matches[Math.Min(used, matches.Count - 1)];
			}

			return result;
		}

		static readonly System.Text.RegularExpressions.Regex EmptyBodyLine = new(@"^[-&|]?\s*$|:\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

		public static string FormatPlan(AspectFilters.TargetFilterPlan? plan)
		{
			if (plan is not {} p)
				return "-";

			// The names and roots come out of a hash set; ordinal order keeps the line independent of its enumeration order.
			var names = string.Join(",", p.ExternalMethodNames.OrderBy(static n => n, StringComparer.Ordinal).Select(EscapeElement));
			var roots = string.Join(",", p.ExternalRootSegments.OrderBy(static n => n, StringComparer.Ordinal).Select(EscapeElement));

			return string.Concat(
				p.Classification, "|",
				p.CanFilterDeclarations         ? '1' : '0',
				p.CanPrefilterCallSites         ? '1' : '0',
				p.RequiresTargetSemanticModel   ? '1' : '0',
				p.RequiresCallSiteSemanticModel ? '1' : '0',
				p.MayTargetExternalMethods      ? '1' : '0',
				p.MayTargetAnyExternalMethod    ? '1' : '0',
				"|", names, "|", roots);
		}

		/// <summary>
		/// Escaping for one element of a list inside a field: <see cref="Escape"/>, and the list separators , and | too,
		/// so that no element can be read as two.
		/// </summary>
		public static string EscapeElement(string text)
		{
			return Escape(text).Replace(",", "\\,").Replace("|", "\\|");
		}

		/// <summary>Reversible escaping for one baseline field: backslash, TAB, CR, LF, other control characters and non-ASCII.</summary>
		public static string Escape(string text)
		{
			var sb = new StringBuilder(text.Length + 8);

			foreach (var ch in text)
			{
				switch (ch)
				{
					case '\\': sb.Append("\\\\"); break;
					case '\t': sb.Append("\\t");  break;
					case '\r': sb.Append("\\r");  break;
					case '\n': sb.Append("\\n");  break;
					default:
						if (ch < 0x20 || ch >= 0x7f)
							sb.Append("\\u").Append(((int)ch).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
						else
							sb.Append(ch);
						break;
				}
			}

			return sb.ToString();
		}
	}
}
