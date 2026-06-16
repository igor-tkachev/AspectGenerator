using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
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
			var items = filters.ToImmutableArray();
			var rules = new List<string>();

			foreach (var item in items)
				if (item is not null)
					foreach (var line in item.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
					{
						var rule = line.Trim();

						if (rule.Length > 0 && !rule.StartsWith("#", StringComparison.Ordinal))
							rules.Add(rule);
					}

			var key   = new TargetFilterKey(rules);

			if (_filterCache.Count > MaxCacheEntries)
				_filterCache.Clear();

			return _filterCache
				.GetOrAdd(key, static k => TargetFilterSet.Create(k.Items))
				.ReportDiagnostics(reportDiagnostic);
		}

		static List<CompiledFilter> Compile(List<string> rules, List<TargetFilterDiagnostic> diagnostics)
		{
			var result = new List<CompiledFilter>();

			foreach (var rule in rules)
			{
				if (!TryParseRule(rule, out var isNegative, out var matcher, out var body))
					continue;

				if (matcher == FilterMatcher.Contains)
				{
					result.Add(new ContainsFilter(isNegative, body));
					continue;
				}

				if (matcher == FilterMatcher.Pattern)
				{
					if (PatternParser.Compile(body, diagnostics, out var pattern))
						result.Add(new PatternFilter(isNegative, pattern));

					continue;
				}

				var regex = GetRegex(body);

				if (regex.Regex is null)
					diagnostics.Add(TargetFilterDiagnostic.InvalidRegex(body, regex.ErrorMessage ?? "Invalid regex pattern."));
				else
					result.Add(new RegexFilter(isNegative, body, regex.Regex));
			}

			return result;

			bool TryParseRule(string rule, out bool isNegative, out FilterMatcher matcher, out string body)
			{
				isNegative = false;
				matcher    = FilterMatcher.Pattern;
				body       = "";

				rule = rule.Trim();

				if (rule.Length == 0 || rule.StartsWith("#", StringComparison.Ordinal))
					return false;

				if (rule[0] == '-')
				{
					isNegative = true;
					rule       = rule[1..].TrimStart();
				}

				if (TryReadMatcherPrefix(rule, out var matcherName, out var matcherBody))
				{
					if (string.Equals(matcherName, "pattern", StringComparison.OrdinalIgnoreCase))
					{
						matcher = FilterMatcher.Pattern;
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
			readonly ImmutableArray<TargetFilterDiagnostic> _diagnostics;
			readonly List<CompiledFilter>                  _filters;

			TargetFilterSet(List<CompiledFilter> filters, ImmutableArray<TargetFilterDiagnostic> diagnostics)
			{
				_filters     = filters;
				_diagnostics = diagnostics;
			}

			public bool IsEmpty => _filters.Count == 0;

			public static TargetFilterSet Create(List<string> rules)
			{
				var diagnostics = new List<TargetFilterDiagnostic>();

				return new TargetFilterSet(Compile(rules, diagnostics), diagnostics.ToImmutableArray());
			}

			public bool IsMatch(in MethodTarget target)
			{
				var matched = false;

				foreach (var filter in _filters)
				{
					if (!filter.IsMatch(target))
						continue;

					matched = !filter.IsNegative;
				}

				return matched;
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
			public List<string>          NamespaceSegments  { get; init; }
			public List<string>          FullTypeSegments   { get; init; }
			public List<string>          FullMethodSegments { get; init; }
			public List<ParameterTarget> Parameters         { get; init; }

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
				return new TargetFilterDiagnostic("AG0204", $"Unknown target filter condition key '{key}'.");
			}

			public static TargetFilterDiagnostic InvalidParameterPattern(string pattern, string message)
			{
				return new TargetFilterDiagnostic("AG0205", $"Invalid target filter parameter pattern '{pattern}': {message}");
			}

			public static TargetFilterDiagnostic InvalidDottedPattern(string pattern, string message)
			{
				return new TargetFilterDiagnostic("AG0206", $"Invalid target filter dotted pattern '{pattern}': {message}");
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

		abstract class CompiledFilter
		{
			protected CompiledFilter(bool isNegative)
			{
				IsNegative = isNegative;
			}

			public bool IsNegative { get; }

			public abstract bool IsMatch(in MethodTarget target);
		}

		sealed class PatternFilter : CompiledFilter
		{
			readonly PatternParser _pattern;

			public PatternFilter(bool isNegative, PatternParser pattern)
				: base(isNegative)
			{
				_pattern = pattern;
			}

			public override bool IsMatch(in MethodTarget target)
			{
				return _pattern.IsMatch(target);
			}
		}

		sealed class ContainsFilter : CompiledFilter
		{
			readonly string _pattern;

			public ContainsFilter(bool isNegative, string pattern)
				: base(isNegative)
			{
				_pattern = pattern;
			}

			public override bool IsMatch(in MethodTarget target)
			{
				return target.Signature.Contains(_pattern, StringComparison.Ordinal);
			}
		}

		sealed class RegexFilter : CompiledFilter
		{
			readonly Regex _regex;

			public RegexFilter(bool isNegative, string pattern, Regex regex)
				: base(isNegative)
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
