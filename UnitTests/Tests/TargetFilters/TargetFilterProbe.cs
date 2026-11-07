using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// Runs a filter text through <c>AspectFilters.GetFilters</c> and describes everything observable about it:
	/// the diagnostic ids (never the messages), the plan, and the decision for every target of the fixed list.
	/// </summary>
	static class TargetFilterProbe
	{
		// The compiled rules are private to the set. The matched rule is reported by its text only, which is not
		// unique (a contains: rule and a pattern rule with the same body print alike), so the index of the matched
		// rule is found by asking the compiled rules themselves.
		static readonly FieldInfo _filtersField =
			typeof(AspectFilters.TargetFilterSet).GetField("_filters", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new InvalidOperationException("TargetFilterSet no longer has a _filters field.");

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
			var decisions = new Decision[targets.Length];

			AspectFilters.TargetFilterSet set;

			try
			{
				set = AspectFilters.GetFilters(input, d => ids.Add(d.Id));
			}
			catch (Exception ex)
			{
				for (var i = 0; i < decisions.Length; i++)
					decisions[i] = new Decision { Kind = 'n', RuleIndex = -1 };

				return new Outcome { Ids = ids.ToArray(), Plan = null, Exception = ex.GetType().Name, RuleCount = 0, Decisions = decisions };
			}

			var filters = (AspectFilters.CompiledFilter[])_filtersField.GetValue(set)!;

			for (var i = 0; i < targets.Length; i++)
			{
				var target     = targets[i].Target;
				var evaluation = set.Evaluate(target);

				if (evaluation.MatchedRule is null)
				{
					decisions[i] = new Decision { Kind = 'n', RuleIndex = -1 };
					continue;
				}

				var index = filters.Length - 1;

				while (index >= 0 && !filters[index].IsMatch(target))
					index--;

				if (index < 0 || filters[index].RuleText != evaluation.MatchedRule || filters[index].IsNegative != evaluation.IsExcluded)
					throw new InvalidOperationException($"The matched rule of '{input}' for target {i} could not be located among the compiled rules.");

				decisions[i] = new Decision { Kind = evaluation.IsMatch ? 'i' : 'e', RuleIndex = index };
			}

			return new Outcome { Ids = ids.ToArray(), Plan = set.Plan, Exception = null, RuleCount = filters.Length, Decisions = decisions };
		}

		/// <summary>
		/// <c>input TAB ids TAB plan TAB decisions TAB rule indexes</c>. The rule index is the position among the
		/// compiled rules (a native condition group is one rule), written as one character per matched target.
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
				sb.Append(string.Join(",", outcome.Ids));

			sb.Append('\t').Append(FormatPlan(outcome.Plan)).Append('\t').Append(outcome.Kinds).Append('\t');

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

		public static string FormatPlan(AspectFilters.TargetFilterPlan? plan)
		{
			if (plan is not {} p)
				return "-";

			// The names and roots come out of a hash set; ordinal order keeps the line independent of its enumeration order.
			var names = string.Join(",", p.ExternalMethodNames.OrderBy(static n => n, StringComparer.Ordinal).Select(Escape));
			var roots = string.Join(",", p.ExternalRootSegments.OrderBy(static n => n, StringComparer.Ordinal).Select(Escape));

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
