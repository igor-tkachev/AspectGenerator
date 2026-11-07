using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;

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
			var key = new TargetFilterKey(ReadRules(filters));

			if (_filterCache.Count > MaxCacheEntries)
				_filterCache.Clear();

			return _filterCache
				.GetOrAdd(key, static k => CreateFilterSet(k.Items, GramTargetFilterFront.Instance))
				.ReportDiagnostics(reportDiagnostic);
		}

		/// <summary>The filters as <see cref="GetFilters(IEnumerable{string?}, Action{TargetFilterDiagnostic}?)"/> reads them, without the cache: for the tests that time the reader.</summary>
		internal static TargetFilterSet ReadFiltersUncached(IEnumerable<string?> filters)
		{
			return CreateFilterSet(ReadRules(filters).ToArray(), GramTargetFilterFront.Instance);
		}

		static TargetFilterSet CreateFilterSet(string[] rules, ITargetFilterFront front)
		{
			var diagnostics = new List<TargetFilterDiagnostic>();
			var filters     = Compile(rules, front, diagnostics);

			return TargetFilterSet.Create(filters, diagnostics.ToArray());
		}

		/// <summary>The rule lines of the filters: every line break ends a line, every line is trimmed, blank lines and comments are dropped.</summary>
		static List<string> ReadRules(IEnumerable<string?> filters)
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

			return rules;
		}

		/// <summary>
		/// Groups the rule lines into compiled rules. How a line reads is the front's; how lines combine is the same for every
		/// front: a native condition line joins the open condition group of the same action, a leading <c>&amp;</c> continues
		/// it, a leading <c>|</c> starts an alternative group with the previous group's action, anything else ends it.
		/// </summary>
		static CompiledFilter[] Compile(string[] rules, ITargetFilterFront front, List<TargetFilterDiagnostic> diagnostics)
		{
			var result               = new List<CompiledFilter>();
			var conditionGroup       = default(List<ConditionGroupLine>);
			var conditionGroupAction = false;
			var previousGroupAction  = default(bool?);
			var previousWasConditionGroup = false;

			foreach (var text in rules)
			{
				if (!front.TryReadRule(text, diagnostics, out var rule))
					continue;

				var prefix  = rule.Prefix;
				var matcher = rule.Matcher;
				var body    = rule.Body;

				if (prefix is RulePrefix.And or RulePrefix.Or && matcher != FilterMatcher.Pattern)
				{
					diagnostics.Add(TargetFilterDiagnostic.InvalidRule(text, $"Line prefix '{(prefix == RulePrefix.And ? "&" : "|")}' can only be used with native condition rules."));
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
					AddPattern(prefix == RulePrefix.Exclude, rule);
					previousWasConditionGroup = false;
					continue;
				}

				if (rule.UnknownKey is {} unknownKey)
				{
					FlushConditionGroup();
					diagnostics.Add(TargetFilterDiagnostic.UnknownConditionKey(unknownKey));
					previousWasConditionGroup = false;
					continue;
				}

				var isConditionLine = rule.IsConditionLine;

				if (prefix is RulePrefix.And or RulePrefix.Or && !isConditionLine)
				{
					diagnostics.Add(TargetFilterDiagnostic.InvalidRule(text, $"Line prefix '{(prefix == RulePrefix.And ? "&" : "|")}' can only be used with native condition rules."));
					continue;
				}

				if (!isConditionLine)
				{
					FlushConditionGroup();
					AddPattern(prefix == RulePrefix.Exclude, rule);
					previousWasConditionGroup = false;
					continue;
				}

				if (prefix == RulePrefix.And)
				{
					if (conditionGroup is null)
					{
						diagnostics.Add(TargetFilterDiagnostic.InvalidRule(text, "Leading '&' requires an active condition group."));
						continue;
					}

					conditionGroup.Add(new ConditionGroupLine(rule, forceAnd: true));
					continue;
				}

				if (prefix == RulePrefix.Or)
				{
					if (conditionGroup is null && !previousWasConditionGroup)
					{
						diagnostics.Add(TargetFilterDiagnostic.InvalidRule(text, "Leading '|' requires a previous native condition group."));
						continue;
					}

					var action = conditionGroup is null ? previousGroupAction!.Value : conditionGroupAction;
					FlushConditionGroup();

					conditionGroup       = new List<ConditionGroupLine> { new(rule, forceAnd: false) };
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

				conditionGroup.Add(new ConditionGroupLine(rule, forceAnd: false));
				previousWasConditionGroup = false;
			}

			FlushConditionGroup();
			return result.ToArray();

			void AddPattern(bool isNegative, FilterRule rule)
			{
				if (front.TryCompilePattern(rule, diagnostics, out var pattern, out var matchesNothing))
					Add(new PatternFilter(isNegative, FormatRule(isNegative ? RulePrefix.Exclude : RulePrefix.None, rule.Body), pattern), matchesNothing);
			}

			void FlushConditionGroup()
			{
				if (conditionGroup is null)
					return;

				if (front.TryCompileConditionGroup(conditionGroup, diagnostics, out var pattern, out var matchesNothing))
					Add(new PatternFilter(conditionGroupAction, FormatConditionGroup(conditionGroupAction, conditionGroup), pattern), matchesNothing);

				previousWasConditionGroup = true;
				previousGroupAction       = conditionGroupAction;
				conditionGroup = null;
			}

			void Add(CompiledFilter filter, string? matchesNothing)
			{
				if (matchesNothing is not null)
					diagnostics.Add(TargetFilterDiagnostic.RuleMatchesNothing(filter.RuleText, matchesNothing, result.Count));

				result.Add(filter);
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
			readonly TargetFilterPlan         _plan;

			TargetFilterSet(CompiledFilter[] filters, TargetFilterDiagnostic[] diagnostics, TargetFilterPlan plan)
			{
				_filters     = filters;
				_diagnostics = diagnostics;
				_plan        = plan;
			}

			public bool IsEmpty => _filters.Length == 0;

			// The compiled rules in evaluation order, for tests that need to know which rule decided.
			internal IReadOnlyList<CompiledFilter> Rules => _filters;
			public TargetFilterPlan Plan => _plan;

			internal static TargetFilterSet Create(CompiledFilter[] filters, TargetFilterDiagnostic[] diagnostics)
			{
				return new TargetFilterSet(filters, diagnostics, TargetFilterPlan.Create(filters));
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

		public readonly struct TargetFilterPlan
		{
			TargetFilterPlan(
				string classification,
				bool   canFilterDeclarations,
				bool   canPrefilterCallSites,
				bool   requiresTargetSemanticModel,
				bool   requiresCallSiteSemanticModel,
				bool   mayTargetExternalMethods,
				bool   mayTargetAnyExternalMethod,
				ImmutableArray<string> externalMethodNames,
				ImmutableArray<string> externalRootSegments)
			{
				Classification                = classification;
				CanFilterDeclarations         = canFilterDeclarations;
				CanPrefilterCallSites         = canPrefilterCallSites;
				RequiresTargetSemanticModel   = requiresTargetSemanticModel;
				RequiresCallSiteSemanticModel = requiresCallSiteSemanticModel;
				MayTargetExternalMethods      = mayTargetExternalMethods;
				MayTargetAnyExternalMethod    = mayTargetAnyExternalMethod;
				ExternalMethodNames           = externalMethodNames;
				ExternalRootSegments          = externalRootSegments;
			}

			public string Classification                { get; }
			public bool   CanFilterDeclarations         { get; }
			public bool   CanPrefilterCallSites         { get; }
			public bool   RequiresTargetSemanticModel   { get; }
			public bool   RequiresCallSiteSemanticModel { get; }
			public bool   MayTargetExternalMethods      { get; }

			/// <summary>
			/// A rule can match a method from a referenced assembly whatever its name or namespace (text rules, wildcard method names).
			/// </summary>
			public bool   MayTargetAnyExternalMethod    { get; }

			/// <summary>
			/// Literal method names of rules that can match methods from referenced assemblies only under these names.
			/// </summary>
			public ImmutableArray<string> ExternalMethodNames  { get; }

			/// <summary>
			/// Literal root segments (root namespace, or the type name for the global namespace) of rules that can match
			/// methods from referenced assemblies only under these roots.
			/// </summary>
			public ImmutableArray<string> ExternalRootSegments { get; }

			internal static TargetFilterPlan Create(CompiledFilter[] filters)
			{
				var hasSignatureText  = false;
				var anyExternalMethod = false;
				var methodNames       = new HashSet<string>(StringComparer.Ordinal);
				var rootSegments      = new HashSet<string>(StringComparer.Ordinal);

				foreach (var filter in filters)
				{
					// An exclude rule never selects a method, so it cannot add external targets.
					if (filter.IsNegative)
						continue;

					switch (filter)
					{
						case PatternFilter pattern:
							if (pattern.Matcher.TryGetLiteralMethodNames(methodNames))
								break;

							if (pattern.Matcher.TryGetLiteralRootSegments(rootSegments))
								break;

							anyExternalMethod = true;
							break;

						default:
							hasSignatureText  = true;
							anyExternalMethod = true;
							break;
					}
				}

				return new TargetFilterPlan(
					hasSignatureText ? "signature-text" : "target-pattern",
					canFilterDeclarations: true,
					canPrefilterCallSites: !anyExternalMethod,
					requiresTargetSemanticModel: true,
					requiresCallSiteSemanticModel: false,
					mayTargetExternalMethods: anyExternalMethod || methodNames.Count > 0 || rootSegments.Count > 0,
					mayTargetAnyExternalMethod: anyExternalMethod,
					externalMethodNames: [..methodNames],
					externalRootSegments: [..rootSegments]);
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
				: this(id, message, DiagnosticSeverity.Error, -1)
			{
			}

			TargetFilterDiagnostic(string id, string message, DiagnosticSeverity severity, int ruleIndex)
			{
				Id        = id;
				Message   = message;
				Severity  = severity;
				RuleIndex = ruleIndex;
			}

			public string             Id       { get; }
			public string             Message  { get; }
			public DiagnosticSeverity Severity { get; }

			/// <summary>The index of the compiled rule the diagnostic is about, or -1 where it is about no compiled rule.</summary>
			internal int RuleIndex { get; }

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

			public static TargetFilterDiagnostic RuleMatchesNothing(string rule, string reason, int ruleIndex)
			{
				return new TargetFilterDiagnostic(AspectDiagnosticID.AspectFilterRuleMatchesNothing, $"Target filter rule '{rule}' can match no method: {reason}", DiagnosticSeverity.Warning, ruleIndex);
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
			public ConditionGroupLine(FilterRule rule, bool forceAnd)
			{
				Rule     = rule;
				ForceAnd = forceAnd;
			}

			public FilterRule Rule     { get; }
			public string     Text     => Rule.Body;
			public bool       ForceAnd { get; }
		}

		/// <summary>One rule line as a front reads it.</summary>
		sealed class FilterRule
		{
			public FilterRule(string text, RulePrefix prefix, FilterMatcher matcher, string body, string? unknownKey, bool isConditionLine, TargetFilterLineSyntax? syntax)
			{
				Text            = text;
				Prefix          = prefix;
				Matcher         = matcher;
				Body            = body;
				UnknownKey      = unknownKey;
				IsConditionLine = isConditionLine;
				Syntax          = syntax;
			}

			/// <summary>The line as the filter reader passed it.</summary>
			public string                  Text            { get; }
			public RulePrefix              Prefix          { get; }
			public FilterMatcher           Matcher         { get; }
			/// <summary>The text after the operator and the matcher prefix: what a compiled rule's text shows.</summary>
			public string                  Body            { get; }
			/// <summary>For a native line that begins with a word and a colon that is not a condition key, that word.</summary>
			public string?                 UnknownKey      { get; }
			/// <summary>A native line that reads as a condition rule.</summary>
			public bool                    IsConditionLine { get; }
			/// <summary>The grammar's reading of the line; the legacy front has none.</summary>
			public TargetFilterLineSyntax? Syntax          { get; }
		}

		/// <summary>
		/// How rule lines are read and compiled. <see cref="GramTargetFilterFront"/> reads them with the generated parser and
		/// is the one filters use; the hand-written splitter it replaced is kept only to compare the two in the tests.
		/// </summary>
		interface ITargetFilterFront
		{
			/// <summary>Reads one rule line; <see langword="false"/>, with a diagnostic, for a line without a body.</summary>
			bool TryReadRule(string text, List<TargetFilterDiagnostic> diagnostics, out FilterRule rule);

			/// <summary>
			/// Compiles a <c>pattern:</c> rule or a native method pattern. <paramref name="matchesNothing"/> is the reason a valid
			/// rule can match no method, or <see langword="null"/>.
			/// </summary>
			bool TryCompilePattern(FilterRule rule, List<TargetFilterDiagnostic> diagnostics, out CompiledPatternMatcher pattern, out string? matchesNothing);

			/// <summary>Compiles the lines of one native condition group into one rule.</summary>
			bool TryCompileConditionGroup(List<ConditionGroupLine> lines, List<TargetFilterDiagnostic> diagnostics, out CompiledPatternMatcher pattern, out string? matchesNothing);
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
			Params,
			RefReadOnly
		}

		internal abstract class CompiledFilter(bool isNegative, string ruleText)
		{
			public bool IsNegative { get; } = isNegative;
			public string RuleText { get; } = ruleText;

			public abstract bool IsMatch(in MethodTarget target);
		}

		sealed class PatternFilter(bool isNegative, string ruleText, CompiledPatternMatcher matcher) : CompiledFilter(isNegative, ruleText)
		{
			public CompiledPatternMatcher Matcher { get; } = matcher;

			public override bool IsMatch(in MethodTarget target)
			{
				return Matcher.IsMatch(target);
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
