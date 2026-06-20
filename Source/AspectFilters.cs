using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AspectGenerator
{
	static partial class AspectFilters
	{
		const int RegexTimeoutMilliseconds = 200;
		const int MaxCacheEntries          = 1024;

		static readonly ConcurrentDictionary<string,RegexCacheEntry>          _regexCache  = new();
		static readonly ConcurrentDictionary<TargetFilterKey,TargetFilterSet> _filterCache = new();

		public static TargetFilterSet GetFilters(
			string?                         filter,
			Action<TargetFilterDiagnostic>? reportDiagnostic = null)
		{
			return GetFilters([filter], reportDiagnostic);
		}

		public static TargetFilterSet GetFilters(
			IEnumerable<string?>            filters,
			Action<TargetFilterDiagnostic>? reportDiagnostic = null)
		{
			var rules = new List<string>();

			foreach (var item in filters)
				if (item is not null)
					foreach (var line in item.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
					{
						var rule = line.Trim();

						if (rule.Length > 0 && !rule.StartsWith("#", StringComparison.Ordinal))
							rules.Add(rule);
					}

			var key = new TargetFilterKey(rules);

			if (_filterCache.Count > MaxCacheEntries)
				_filterCache.Clear();

			return _filterCache
				.GetOrAdd(key, static k => TargetFilterSet.Create(k.Items))
				.ReportDiagnostics(reportDiagnostic);
		}

		static CompiledFilter[] Compile(string[] rules, List<TargetFilterDiagnostic> diagnostics)
		{
			var result               = new List<CompiledFilter>();
			var conditionGroup       = default(List<ConditionGroupLine>);
			var conditionGroupAction = false;
			var previousGroupAction  = default(bool?);
			var previousWasConditionGroup = false;

			foreach (var rule in rules)
			{
				if (!TryParseRule(rule, out var prefix, out var matcher, out var body))
					continue;

				if (prefix is RulePrefix.And or RulePrefix.Or && matcher != FilterMatcher.Pattern)
				{
					diagnostics.Add(TargetFilterDiagnostic.InvalidRule(rule, $"Line prefix '{(prefix == RulePrefix.And ? "&" : "|")}' can only be used with native condition rules."));
					continue;
				}

				if (matcher == FilterMatcher.Contains)
				{
					FlushConditionGroup();
					result.Add(new ContainsFilter(prefix == RulePrefix.Exclude, FormatRule(prefix, body), body));
					previousWasConditionGroup = false;
					continue;
				}

				if (matcher == FilterMatcher.Regex)
				{
					FlushConditionGroup();

					var regex = GetRegex(body);

					if (regex.Regex is null)
						diagnostics.Add(TargetFilterDiagnostic.InvalidRegex(body, regex.ErrorMessage ?? "Invalid regex pattern."));
					else
						result.Add(new RegexFilter(prefix == RulePrefix.Exclude, FormatRule(prefix, body), regex.Regex));

					previousWasConditionGroup = false;
					continue;
				}

				if (matcher == FilterMatcher.ExplicitPattern)
				{
					FlushConditionGroup();
					AddPattern(prefix == RulePrefix.Exclude, body);
					previousWasConditionGroup = false;
					continue;
				}

				if (CompiledPatternMatcher.TryGetUnknownSimpleConditionKey(body, out var unknownKey))
				{
					FlushConditionGroup();
					diagnostics.Add(TargetFilterDiagnostic.UnknownConditionKey(unknownKey));
					previousWasConditionGroup = false;
					continue;
				}

				var isConditionLine = CompiledPatternMatcher.IsConditionRule(body);

				if (prefix is RulePrefix.And or RulePrefix.Or && !isConditionLine)
				{
					diagnostics.Add(TargetFilterDiagnostic.InvalidRule(rule, $"Line prefix '{(prefix == RulePrefix.And ? "&" : "|")}' can only be used with native condition rules."));
					continue;
				}

				if (!isConditionLine)
				{
					FlushConditionGroup();
					AddPattern(prefix == RulePrefix.Exclude, body);
					previousWasConditionGroup = false;
					continue;
				}

				if (prefix == RulePrefix.And)
				{
					if (conditionGroup is null)
					{
						diagnostics.Add(TargetFilterDiagnostic.InvalidRule(rule, "Leading '&' requires an active condition group."));
						continue;
					}

					conditionGroup.Add(new ConditionGroupLine(body, forceAnd: true));
					continue;
				}

				if (prefix == RulePrefix.Or)
				{
					if (conditionGroup is null && !previousWasConditionGroup)
					{
						diagnostics.Add(TargetFilterDiagnostic.InvalidRule(rule, "Leading '|' requires a previous native condition group."));
						continue;
					}

					var action = conditionGroup is null ? previousGroupAction!.Value : conditionGroupAction;
					FlushConditionGroup();

					conditionGroup       = new List<ConditionGroupLine> { new(body, forceAnd: false) };
					conditionGroupAction = action;
					previousGroupAction  = action;
					continue;
				}

				var isNegative = prefix == RulePrefix.Exclude;

				if (conditionGroup is not null && conditionGroupAction != isNegative)
					FlushConditionGroup();

				if (conditionGroup is null)
				{
					conditionGroup       = new List<ConditionGroupLine>();
					conditionGroupAction = isNegative;
					previousGroupAction  = isNegative;
				}

				conditionGroup.Add(new ConditionGroupLine(body, forceAnd: false));
				previousWasConditionGroup = false;
			}

			FlushConditionGroup();
			return result.ToArray();

			void AddPattern(bool isNegative, string text)
			{
				if (CompiledPatternMatcher.TryCompile(text, diagnostics, out var pattern))
					result.Add(new PatternFilter(isNegative, FormatRule(isNegative ? RulePrefix.Exclude : RulePrefix.None, text), pattern));
			}

			void FlushConditionGroup()
			{
				if (conditionGroup is null)
					return;

				if (CompiledPatternMatcher.TryCompileConditionGroup(conditionGroup, diagnostics, out var pattern))
					result.Add(new PatternFilter(conditionGroupAction, FormatConditionGroup(conditionGroupAction, conditionGroup), pattern));

				previousWasConditionGroup = true;
				previousGroupAction       = conditionGroupAction;
				conditionGroup = null;
			}

			bool TryParseRule(string rule, out RulePrefix prefix, out FilterMatcher matcher, out string body)
			{
				prefix  = RulePrefix.None;
				matcher = FilterMatcher.Pattern;
				body    = "";

				rule = rule.Trim();

				if (rule.Length == 0 || rule.StartsWith("#", StringComparison.Ordinal))
					return false;

				if (rule[0] == '-')
				{
					prefix = RulePrefix.Exclude;
					rule   = rule[1..].TrimStart();
				}
				else if (rule[0] == '&')
				{
					prefix = RulePrefix.And;
					rule   = rule[1..].TrimStart();
				}
				else if (rule[0] == '|')
				{
					prefix = RulePrefix.Or;
					rule   = rule[1..].TrimStart();
				}

				if (TryReadMatcherPrefix(rule, out var matcherName, out var matcherBody))
				{
					if (string.Equals(matcherName, "pattern", StringComparison.OrdinalIgnoreCase))
					{
						matcher = FilterMatcher.ExplicitPattern;
						body    = matcherBody;
					}
					else if (string.Equals(matcherName, "regex", StringComparison.OrdinalIgnoreCase))
					{
						matcher = FilterMatcher.Regex;
						body    = matcherBody;
					}
					else if (string.Equals(matcherName, "contains", StringComparison.OrdinalIgnoreCase))
					{
						matcher = FilterMatcher.Contains;
						body    = matcherBody;
					}
					else
					{
						body = rule;
					}
				}
				else
					body = rule;

				if (body.Length > 0)
					return true;

				diagnostics.Add(TargetFilterDiagnostic.InvalidRule(rule, "Target filter rule body cannot be empty."));
				return false;
			}

			static string FormatRule(RulePrefix prefix, string body)
			{
				return prefix switch
				{
					RulePrefix.Exclude => "- " + body,
					RulePrefix.And     => "& " + body,
					RulePrefix.Or      => "| " + body,
					_                  => body
				};
			}

			static string FormatConditionGroup(bool isNegative, List<ConditionGroupLine> lines)
			{
				var sb = new StringBuilder();

				if (isNegative)
					sb.Append("- ");

				for (var i = 0; i < lines.Count; i++)
				{
					if (i > 0)
						sb.Append("; ");

					if (i > 0 && lines[i].ForceAnd)
						sb.Append("& ");

					sb.Append(lines[i].Text);
				}

				return sb.ToString();
			}
		}

		static RegexCacheEntry GetRegex(string pattern)
		{
			if (_regexCache.Count > MaxCacheEntries)
				_regexCache.Clear();

			return _regexCache.GetOrAdd(
				pattern,
				static p =>
				{
					try
					{
						return new RegexCacheEntry(
							new Regex(p, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(RegexTimeoutMilliseconds)),
							null);
					}
					catch (ArgumentException ex)
					{
						return new RegexCacheEntry(null, ex.Message);
					}
				});
		}

		public readonly struct TargetFilterSet
		{
			readonly TargetFilterDiagnostic[] _diagnostics;
			readonly CompiledFilter[]         _filters;

			TargetFilterSet(CompiledFilter[] filters, TargetFilterDiagnostic[] diagnostics)
			{
				_filters     = filters;
				_diagnostics = diagnostics;
			}

			public bool IsEmpty => _filters.Length == 0;

			public static TargetFilterSet Create(string[] rules)
			{
				var diagnostics = new List<TargetFilterDiagnostic>();

				return new TargetFilterSet(Compile(rules, diagnostics), diagnostics.ToArray());
			}

			public TargetFilterEvaluation Evaluate(in MethodTarget target)
			{
				var included = false;
				var matchedRule = default(string);

				foreach (var filter in _filters)
				{
					var isMatch = filter.IsMatch(target);

					if (!isMatch)
						continue;

					included = !filter.IsNegative;
					matchedRule = filter.RuleText;
				}

				return new TargetFilterEvaluation(included, matchedRule is not null && !included, matchedRule);
			}

			public TargetFilterSet ReportDiagnostics(Action<TargetFilterDiagnostic>? reportDiagnostic)
			{
				if (reportDiagnostic is null)
					return this;

				foreach (var diagnostic in _diagnostics)
					reportDiagnostic(diagnostic);

				return this;
			}
		}

		public readonly struct MethodTarget
		{
			public AccessibilityMask     Accessibility      { get; init; }
			public ModifierMask          Modifiers          { get; init; }
			public string                Namespace          { get; init; }
			public string                TypeName           { get; init; }
			public string                FullTypeName       { get; init; }
			public string                MethodName         { get; init; }
			public string                FullMethodName     { get; init; }
			public string                ReturnType         { get; init; }
			public string                Signature          { get; init; }
			public List<string>          Attributes         { get; init; }
			public List<string>          NamespaceSegments  { get; init; }
			public List<string>          FullTypeSegments   { get; init; }
			public List<string>          FullMethodSegments { get; init; }
			public List<ParameterTarget> Parameters         { get; init; }

		}

		public readonly struct TargetFilterEvaluation
		{
			public TargetFilterEvaluation(bool isMatch, bool isExcluded, string? matchedRule)
			{
				IsMatch     = isMatch;
				IsExcluded  = isExcluded;
				MatchedRule = matchedRule;
			}

			public bool    IsMatch     { get; }
			public bool    IsExcluded  { get; }
			public string? MatchedRule { get; }
		}

		public readonly struct ParameterTarget
		{
			public ParameterModifier Modifier { get; init; }
			public string            Type     { get; init; }
		}

		public readonly struct TargetFilterDiagnostic
		{
			public TargetFilterDiagnostic(string id, string message)
			{
				Id      = id;
				Message = message;
			}

			public string Id      { get; }
			public string Message { get; }

			public static TargetFilterDiagnostic InvalidRegex(string pattern, string error)
			{
				return new TargetFilterDiagnostic("AG0201", $"Invalid aspect filter regex '{pattern}': {error}");
			}

			public static TargetFilterDiagnostic InvalidRule(string rule, string message)
			{
				return new TargetFilterDiagnostic("AG0202", $"Invalid aspect filter rule '{rule}': {message}");
			}

			public static TargetFilterDiagnostic UnknownConditionKey(string key)
			{
				return new TargetFilterDiagnostic(AspectDiagnosticID.UnknownAspectFilterConditionKey, $"Unknown target filter condition key '{key}'.");
			}

			public static TargetFilterDiagnostic InvalidParameterPattern(string pattern, string message)
			{
				return new TargetFilterDiagnostic(AspectDiagnosticID.InvalidAspectFilterParameterPattern, $"Invalid target filter parameter pattern '{pattern}': {message}");
			}

			public static TargetFilterDiagnostic InvalidDottedPattern(string pattern, string message)
			{
				return new TargetFilterDiagnostic(AspectDiagnosticID.InvalidAspectFilterDottedPattern, $"Invalid target filter dotted pattern '{pattern}': {message}");
			}
		}

		readonly struct TargetFilterKey : IEquatable<TargetFilterKey>
		{
			readonly string[]     _items;
			readonly int          _hashCode;

			public TargetFilterKey(List<string> items)
			{
				_items = items.ToArray();

				unchecked
				{
					var hashCode = 17;

					hashCode = hashCode * 31 + _items.Length;

					foreach (var item in _items)
						hashCode = hashCode * 31 + StringComparer.Ordinal.GetHashCode(item);

					_hashCode = hashCode;
				}
			}

			public string[] Items => _items;

			public bool Equals(TargetFilterKey other)
			{
				if (_items.Length != other._items.Length)
					return false;

				for (var i = 0; i < _items.Length; i++)
					if (!StringComparer.Ordinal.Equals(_items[i], other._items[i]))
						return false;

				return true;
			}

			public override bool Equals(object? obj)
			{
				return obj is TargetFilterKey other && Equals(other);
			}

			public override int GetHashCode()
			{
				return _hashCode;
			}
		}

		enum FilterMatcher
		{
			Pattern,
			ExplicitPattern,
			Contains,
			Regex
		}

		enum RulePrefix
		{
			None,
			Exclude,
			And,
			Or
		}

		readonly struct ConditionGroupLine
		{
			public ConditionGroupLine(string text, bool forceAnd)
			{
				Text     = text;
				ForceAnd = forceAnd;
			}

			public string Text     { get; }
			public bool   ForceAnd { get; }
		}

		[Flags]
		public enum AccessibilityMask
		{
			None      = 0,
			Public    = 1,
			Private   = 2,
			Protected = 4,
			Internal  = 8
		}

		[Flags]
		public enum ModifierMask
		{
			None     = 0,
			Static   = 1,
			Instance = 2,
			Abstract = 4,
			Virtual  = 8,
			Override = 16,
			Sealed   = 32,
			Extern   = 64,
			Unsafe   = 128
		}

		public enum ParameterModifier
		{
			None,
			Ref,
			Out,
			In,
			Params
		}

		abstract class CompiledFilter(bool isNegative, string ruleText)
		{
			public bool IsNegative { get; } = isNegative;
			public string RuleText { get; } = ruleText;

			public abstract bool IsMatch(in MethodTarget target);
		}

		sealed class PatternFilter(bool isNegative, string ruleText, CompiledPatternMatcher matcher) : CompiledFilter(isNegative, ruleText)
		{
			public override bool IsMatch(in MethodTarget target)
			{
				return matcher.IsMatch(target);
			}
		}

		sealed class ContainsFilter(bool isNegative, string ruleText, string pattern) : CompiledFilter(isNegative, ruleText)
		{
			public override bool IsMatch(in MethodTarget target)
			{
				return target.Signature.Contains(pattern, StringComparison.Ordinal);
			}
		}

		sealed class RegexFilter : CompiledFilter
		{
			readonly Regex _regex;

			public RegexFilter(bool isNegative, string ruleText, Regex regex)
				: base(isNegative, ruleText)
			{
				_regex = regex;
			}

			public override bool IsMatch(in MethodTarget target)
			{
				try
				{
					return _regex.IsMatch(target.Signature);
				}
				catch (RegexMatchTimeoutException)
				{
					return false;
				}
			}
		}

		sealed record RegexCacheEntry(Regex? Regex, string? ErrorMessage);
	}
}
