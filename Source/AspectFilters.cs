using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace AspectGenerator
{
	static class AspectFilters
	{
		const int RegexTimeoutMilliseconds = 200;
		const int MaxCacheEntries          = 1024;

		static readonly ConcurrentDictionary<TargetFilterKey,TargetFilterSet> _filterCache = new();

		public static TargetFilterSet GetFilters(
			string?                filter,
			Action<string,string>? reportInvalidRegex = null,
			Action<string>?        reportUnsupportedPattern = null)
		{
			return GetFilters([filter], reportInvalidRegex, reportUnsupportedPattern);
		}

		public static TargetFilterSet GetFilters(
			IEnumerable<string?>   filters,
			Action<string,string>? reportInvalidRegex = null,
			Action<string>?        reportUnsupportedPattern = null)
		{
			var items = filters.ToImmutableArray();
			var rules = ParseRules(items);
			var key   = new TargetFilterKey(rules);

			if (_filterCache.Count > MaxCacheEntries)
				_filterCache.Clear();

			return _filterCache
				.GetOrAdd(key, static k => TargetFilterSet.Create(k.Items))
				.ReportDiagnostics(reportInvalidRegex, reportUnsupportedPattern);
		}

		static List<string> ParseRules(ImmutableArray<string?> items)
		{
			var result = new List<string>();

			foreach (var item in items)
				if (item is not null)
					foreach (var line in item.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
					{
						var rule = line.Trim();

						if (rule.Length > 0 && !rule.StartsWith("#", StringComparison.Ordinal))
							result.Add(rule);
					}

			return result;
		}

		static List<CompiledFilter> Compile(List<string> rules)
		{
			var result = new List<CompiledFilter>();

			foreach (var rule in rules)
			{
				if (!TryParseRule(rule, out var isNegative, out var matcher, out var pattern))
					continue;

				if (matcher == FilterMatcher.Pattern)
					continue;

				if (matcher == FilterMatcher.Contains)
				{
					result.Add(new CompiledFilter(isNegative, matcher, pattern, null));
					continue;
				}

				var regex = GetRegex(pattern);

				if (regex.Regex is not null)
					result.Add(new CompiledFilter(isNegative, matcher, pattern, regex.Regex));
			}

			return result;
		}

		static bool TryParseRule(string rule, out bool isNegative, out FilterMatcher matcher, out string pattern)
		{
			isNegative = false;
			matcher    = FilterMatcher.Pattern;
			pattern    = "";

			rule = rule.Trim();

			if (rule.Length == 0 || rule.StartsWith("#", StringComparison.Ordinal))
				return false;

			if (rule[0] == '-')
			{
				isNegative = true;
				rule       = rule[1..].TrimStart();
			}

			if (TryReadMatcherPrefix(rule, "pattern", out var patternBody))
			{
				matcher = FilterMatcher.Pattern;
				pattern = patternBody;
			}
			else if (TryReadMatcherPrefix(rule, "regex", out var regexBody))
			{
				matcher = FilterMatcher.Regex;
				pattern = regexBody;
			}
			else if (TryReadMatcherPrefix(rule, "contains", out var containsBody))
			{
				matcher = FilterMatcher.Contains;
				pattern = containsBody;
			}
			else
				pattern = rule;

			return pattern.Length > 0;
		}

		static bool TryReadMatcherPrefix(string rule, string prefix, out string body)
		{
			body = "";

			if (!rule.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				return false;

			var index = prefix.Length;

			while (index < rule.Length && char.IsWhiteSpace(rule[index]))
				index++;

			if (index >= rule.Length || rule[index] != ':')
				return false;

			body = rule[(index + 1)..].Trim();
			return true;
		}

		static RegexCacheEntry GetRegex(string pattern)
		{
			try
			{
				return new RegexCacheEntry(
					new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(RegexTimeoutMilliseconds)),
					null);
			}
			catch (ArgumentException ex)
			{
				return new RegexCacheEntry(null, ex.Message);
			}
		}

		static bool IsRegexMatch(CompiledFilter filter, string targetSignature)
		{
			try
			{
				return filter.Regex?.IsMatch(targetSignature) == true;
			}
			catch (RegexMatchTimeoutException)
			{
				return false;
			}
		}

		public readonly struct TargetFilterSet
		{
			readonly List<string>         _rules;
			readonly List<CompiledFilter> _filters;

			TargetFilterSet(List<string> rules, List<CompiledFilter> filters)
			{
				_rules   = rules;
				_filters = filters;
			}

			public bool IsEmpty => _filters.Count == 0;

			public static TargetFilterSet Create(List<string> rules)
			{
				return new TargetFilterSet(rules, Compile(rules));
			}

			public bool IsMatch(string targetSignature)
			{
				var matched = false;

				foreach (var filter in _filters)
				{
					var isMatch = filter.Matcher switch
					{
						FilterMatcher.Contains => targetSignature.Contains(filter.Pattern, StringComparison.Ordinal),
						FilterMatcher.Regex    => IsRegexMatch(filter, targetSignature),
						_                      => false
					};

					if (!isMatch)
						continue;

					matched = !filter.IsNegative;
				}

				return matched;
			}

			public TargetFilterSet ReportDiagnostics(
				Action<string,string>? reportInvalidRegex,
				Action<string>?        reportUnsupportedPattern)
			{
				if (reportInvalidRegex is null && reportUnsupportedPattern is null)
					return this;

				foreach (var rule in _rules)
				{
					if (!TryParseRule(rule, out _, out var matcher, out var pattern))
						continue;

					if (matcher == FilterMatcher.Pattern)
					{
						reportUnsupportedPattern?.Invoke(pattern);
						continue;
					}

					if (matcher == FilterMatcher.Regex)
					{
						var regex = GetRegex(pattern);

						if (regex.Regex is null)
							reportInvalidRegex?.Invoke(pattern, regex.ErrorMessage ?? "Invalid regex pattern.");
					}
				}

				return this;
			}
		}

		readonly struct TargetFilterKey : IEquatable<TargetFilterKey>
		{
			readonly List<string> _items;
			readonly int          _hashCode;

			public TargetFilterKey(List<string> items)
			{
				_items = items;

				unchecked
				{
					var hashCode = 17;

					hashCode = hashCode * 31 + _items.Count;

					foreach (var item in _items)
						hashCode = hashCode * 31 + StringComparer.Ordinal.GetHashCode(item);

					_hashCode = hashCode;
				}
			}

			public List<string> Items => _items;

			public bool Equals(TargetFilterKey other)
			{
				if (_items.Count != other._items.Count)
					return false;

				for (var i = 0; i < _items.Count; i++)
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
			Contains,
			Regex
		}

		sealed record CompiledFilter(
			bool          IsNegative,
			FilterMatcher Matcher,
			string        Pattern,
			Regex?        Regex);

		sealed record RegexCacheEntry(Regex? Regex, string? ErrorMessage);
	}
}
