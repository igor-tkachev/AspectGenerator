using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace AspectGenerator
{
	internal static class AspectFilters
	{
		const int RegexTimeoutMilliseconds = 200;

		static readonly ConcurrentDictionary<string,RegexCacheEntry>       RegexCache = new();
		static readonly ConcurrentDictionary<TargetFilterKey,TargetFilterSet> FilterCache = new();

		internal static TargetFilterSet GetFilters(
			string?                filter,
			Action<string,string>? reportInvalidRegex = null)
		{
			return GetFilters([filter], reportInvalidRegex);
		}

		internal static TargetFilterSet GetFilters(
			IEnumerable<string?>   filters,
			Action<string,string>? reportInvalidRegex = null)
		{
			var items = filters.ToImmutableArray();
			var key   = new TargetFilterKey(items);

			return FilterCache.GetOrAdd(
				key,
				static k =>
				{
					var rules = ParseRules(k.Items);

					return TargetFilterSet.Create(rules);
				})
				.ReportInvalidRegex(reportInvalidRegex);
		}

		static ImmutableArray<string> ParseRules(ImmutableArray<string?> items)
		{
			var result = ImmutableArray.CreateBuilder<string>();

			foreach (var item in items)
				AddRules(result, item);

			return result.ToImmutable();
		}

		static ImmutableArray<CompiledFilter> Compile(ImmutableArray<string> rules)
		{
			var result = ImmutableArray.CreateBuilder<CompiledFilter>();

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

			return result.ToImmutable();
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

		static void AddRules(ImmutableArray<string>.Builder result, string? value)
		{
			if (value is null)
				return;

			foreach (var line in value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
			{
				var rule = line.Trim();

				if (rule.Length > 0 && !rule.StartsWith("#", StringComparison.Ordinal))
					result.Add(rule);
			}
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
			return RegexCache.GetOrAdd(
				pattern,
				static p =>
				{
					try
					{
						return new RegexCacheEntry(
							new Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(RegexTimeoutMilliseconds)),
							null);
					}
					catch (ArgumentException ex)
					{
						return new RegexCacheEntry(null, ex.Message);
					}
				});
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

		internal readonly struct TargetFilterSet
		{
			readonly ImmutableArray<string>         _rules;
			readonly ImmutableArray<CompiledFilter> _filters;

			TargetFilterSet(ImmutableArray<string> rules, ImmutableArray<CompiledFilter> filters)
			{
				_rules   = rules;
				_filters = filters;
			}

			internal bool IsEmpty => _filters.IsDefaultOrEmpty;

			internal static TargetFilterSet Create(ImmutableArray<string> rules)
			{
				return new TargetFilterSet(rules, Compile(rules));
			}

			internal bool IsMatch(string targetSignature)
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

			internal TargetFilterSet ReportInvalidRegex(Action<string,string>? reportInvalidRegex)
			{
				if (reportInvalidRegex is null)
					return this;

				foreach (var rule in _rules)
				{
					if (!TryParseRule(rule, out _, out var matcher, out var pattern) || matcher != FilterMatcher.Regex)
						continue;

					var regex = GetRegex(pattern);

					if (regex.Regex is null)
						reportInvalidRegex(pattern, regex.ErrorMessage ?? "Invalid regex pattern.");
				}

				return this;
			}
		}

		readonly struct TargetFilterKey : IEquatable<TargetFilterKey>
		{
			readonly ImmutableArray<string?> _items;
			readonly int                     _hashCode;

			internal TargetFilterKey(ImmutableArray<string?> items)
			{
				_items = items.IsDefault ? ImmutableArray<string?>.Empty : items;

				unchecked
				{
					var hashCode = 17;
					hashCode = hashCode * 31 + _items.Length;

					foreach (var item in _items)
						hashCode = hashCode * 31 + (item is null ? 0 : StringComparer.Ordinal.GetHashCode(item));

					_hashCode = hashCode;
				}
			}

			internal ImmutableArray<string?> Items => _items;

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
