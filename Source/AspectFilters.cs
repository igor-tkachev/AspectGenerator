using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

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

		/// <summary>
		/// Reads the filters as one list of rule lines. A diagnostic reported names, where it can be told, the filter it
		/// points into (<see cref="TargetFilterDiagnostic.FilterIndex"/>, the index in <paramref name="filters"/>) and the
		/// characters of that filter (<see cref="TargetFilterDiagnostic.FilterSpan"/>).
		/// </summary>
		public static TargetFilterSet GetFilters(
			IEnumerable<string?>            filters,
			Action<TargetFilterDiagnostic>? reportDiagnostic = null)
		{
			var origins = reportDiagnostic is null ? null : new List<RuleOrigin>();
			var key     = new TargetFilterKey(ReadRules(filters, origins));

			if (_filterCache.Count > MaxCacheEntries)
				_filterCache.Clear();

			var set = _filterCache.GetOrAdd(key, static k => CreateFilterSet(k.Items));

			// A reading that ran out of stack says nothing about the filter, only about the thread it ran on: the next
			// read, on another thread, reads it again.
			if (set.HasTransientDiagnostics)
				_filterCache.TryRemove(key, out _);

			return set.ReportDiagnostics(reportDiagnostic, origins);
		}

		/// <summary>The filters as <see cref="GetFilters(IEnumerable{string?}, Action{TargetFilterDiagnostic}?)"/> reads them, without the cache: for the tests that time the reader.</summary>
		internal static TargetFilterSet ReadFiltersUncached(IEnumerable<string?> filters)
		{
			return CreateFilterSet(ReadRules(filters, null).ToArray());
		}

		static TargetFilterSet CreateFilterSet(string[] rules)
		{
			var diagnostics = new List<TargetFilterDiagnostic>();
			var filters     = Compile(rules, diagnostics);

			return TargetFilterSet.Create(filters, diagnostics.ToArray());
		}

		/// <summary>
		/// The rule lines of the filters: every line break (CR LF, CR or LF) ends a line, every line is trimmed, blank lines
		/// and comments are dropped. <paramref name="origins"/>, when given, receives where each rule line stands.
		/// </summary>
		static List<string> ReadRules(IEnumerable<string?> filters, List<RuleOrigin>? origins)
		{
			var rules = new List<string>();
			var index = -1;

			foreach (var item in filters)
			{
				index++;

				if (item is null)
					continue;

				var start = 0;

				while (start <= item.Length)
				{
					var end = start;

					while (end < item.Length && item[end] is not ('\r' or '\n'))
						end++;

					var first = start;
					var last  = end;

					while (first < last && char.IsWhiteSpace(item[first]))
						first++;

					while (last > first && char.IsWhiteSpace(item[last - 1]))
						last--;

					if (last > first && item[first] != '#')
					{
						rules.Add(item.Substring(first, last - first));
						origins?.Add(new RuleOrigin(index, first));
					}

					start = end < item.Length && item[end] == '\r' && end + 1 < item.Length && item[end + 1] == '\n' ? end + 2 : end + 1;
				}
			}

			return rules;
		}

		/// <summary>Where a rule line stands: the index of its filter, and where the trimmed line begins in it.</summary>
		internal readonly struct RuleOrigin
		{
			public RuleOrigin(int filter, int start)
			{
				Filter = filter;
				Start  = start;
			}

			public int Filter { get; }
			public int Start  { get; }
		}

		/// <summary>
		/// Groups the rule lines into compiled rules. How a line reads is <see cref="GramTargetFilterFront"/>'s; how lines
		/// combine is decided here: a native condition line joins the open condition group of the same action, a leading
		/// <c>&amp;</c> continues it, a leading <c>|</c> starts an alternative group with the previous group's action,
		/// anything else ends it.
		/// </summary>
		static CompiledFilter[] Compile(string[] rules, List<TargetFilterDiagnostic> diagnostics)
		{
			var result               = new List<CompiledFilter>();
			var conditionGroup       = default(List<ConditionGroupLine>);
			var conditionGroupAction = false;
			var previousGroupAction  = default(bool?);
			var previousWasConditionGroup = false;

			for (var line = 0; line < rules.Length; line++)
			{
				var mark = diagnostics.Count;

				// What a line reports points into it; where a step tells no closer place, at the whole line.
				ReadLine(rules[line], line);
				Place(diagnostics, mark, line, new TextSpan(0, rules[line].Length));
			}

			FlushConditionGroup();
			return result.ToArray();

			void ReadLine(string text, int index)
			{
				if (!GramTargetFilterFront.TryReadRule(text, index, diagnostics, out var rule))
					return;

				var prefix  = rule.Prefix;
				var matcher = rule.Matcher;
				var body    = rule.Body;

				if (prefix is RulePrefix.And or RulePrefix.Or && matcher != FilterMatcher.Pattern)
				{
					diagnostics.Add(TargetFilterDiagnostic.InvalidRule(text, $"Line prefix '{(prefix == RulePrefix.And ? "&" : "|")}' can only be used with native condition rules.").At(rule.Syntax.PrefixSpan));
					return;
				}

				if (matcher == FilterMatcher.Contains)
				{
					FlushConditionGroup();
					result.Add(new ContainsFilter(prefix == RulePrefix.Exclude, FormatRule(prefix, body), body));
					previousWasConditionGroup = false;
					return;
				}

				if (matcher == FilterMatcher.Regex)
				{
					FlushConditionGroup();

					var regex = GetRegex(body);

					if (regex.Regex is null)
						diagnostics.Add(TargetFilterDiagnostic.InvalidRegex(body, regex.ErrorMessage ?? "Invalid regex pattern.").At(rule.Syntax.Body.Span));
					else
						result.Add(new RegexFilter(prefix == RulePrefix.Exclude, FormatRule(prefix, body), regex.Regex));

					previousWasConditionGroup = false;
					return;
				}

				if (matcher == FilterMatcher.ExplicitPattern)
				{
					FlushConditionGroup();
					AddPattern(prefix == RulePrefix.Exclude, rule);
					previousWasConditionGroup = false;
					return;
				}

				if (rule.UnknownKey is {} unknownKey)
				{
					FlushConditionGroup();
					diagnostics.Add(TargetFilterDiagnostic.UnknownConditionKey(unknownKey).At(((UnknownKeyBodySyntax)rule.Syntax.Body).Key));
					previousWasConditionGroup = false;
					return;
				}

				var isConditionLine = rule.IsConditionLine;

				if (prefix is RulePrefix.And or RulePrefix.Or && !isConditionLine)
				{
					diagnostics.Add(TargetFilterDiagnostic.InvalidRule(text, $"Line prefix '{(prefix == RulePrefix.And ? "&" : "|")}' can only be used with native condition rules.").At(rule.Syntax.PrefixSpan));
					return;
				}

				if (!isConditionLine)
				{
					FlushConditionGroup();
					AddPattern(prefix == RulePrefix.Exclude, rule);
					previousWasConditionGroup = false;
					return;
				}

				if (prefix == RulePrefix.And)
				{
					if (conditionGroup is null)
					{
						diagnostics.Add(TargetFilterDiagnostic.InvalidRule(text, "Leading '&' requires an active condition group.").At(rule.Syntax.PrefixSpan));
						return;
					}

					conditionGroup.Add(new ConditionGroupLine(rule, forceAnd: true));
					return;
				}

				if (prefix == RulePrefix.Or)
				{
					if (conditionGroup is null && !previousWasConditionGroup)
					{
						diagnostics.Add(TargetFilterDiagnostic.InvalidRule(text, "Leading '|' requires a previous native condition group.").At(rule.Syntax.PrefixSpan));
						return;
					}

					var action = conditionGroup is null ? previousGroupAction!.Value : conditionGroupAction;
					FlushConditionGroup();

					conditionGroup       = new List<ConditionGroupLine> { new(rule, forceAnd: false) };
					conditionGroupAction = action;
					previousGroupAction  = action;
					return;
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

			void AddPattern(bool isNegative, FilterRule rule)
			{
				var span = RuleLinesSpan.InLine(rule.Line, new TextSpan(0, rule.Text.Length));
				var mark = diagnostics.Count;

				if (GramTargetFilterFront.TryCompilePattern(rule, diagnostics, out var pattern, out var notes))
					Add(new PatternFilter(isNegative, FormatRule(isNegative ? RulePrefix.Exclude : RulePrefix.None, rule.Body), pattern), notes, span);

				Place(diagnostics, mark, rule.Line, span.Span);
			}

			void FlushConditionGroup()
			{
				if (conditionGroup is null)
					return;

				// A group is about all of its lines.
				var first = conditionGroup[0].Rule;
				var last  = conditionGroup[^1].Rule;
				var span  = new RuleLinesSpan(first.Line, 0, last.Line, last.Text.Length);
				var mark  = diagnostics.Count;

				if (GramTargetFilterFront.TryCompileConditionGroup(conditionGroup, diagnostics, out var pattern, out var notes))
					Add(new PatternFilter(conditionGroupAction, FormatConditionGroup(conditionGroupAction, conditionGroup), pattern), notes, span);

				Place(diagnostics, mark, span);

				previousWasConditionGroup = true;
				previousGroupAction       = conditionGroupAction;
				conditionGroup = null;
			}

			void Add(CompiledFilter filter, RuleNotes notes, RuleLinesSpan span)
			{
				if (notes.MatchesNothing is {} reason)
					diagnostics.Add(TargetFilterDiagnostic.RuleMatchesNothing(filter.RuleText, reason, result.Count).InLines(span));

				if (notes.WhiteSpaceSegment is {} segment)
					diagnostics.Add(TargetFilterDiagnostic.NameWithWhiteSpace(filter.RuleText, segment, result.Count).InLines(span));

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

		// ---- where a diagnostic points ----
		//
		// A diagnostic is made where the text it is about is read, often a piece of a line, a value or a pattern read on
		// its own. It is given a span in that text (At); each step that read the text as a piece of a larger one widens or
		// moves it into that one, until the line is known and it is placed in the rule lines (Place). A text made up for
		// the reading, which stands nowhere in the line (an expanded type alias, the words of a method name joined), moves
		// what it reported to the span it was made from.

		/// <summary>Gives the diagnostics from <paramref name="from"/> on that have no span, or an empty one, <paramref name="span"/>, a span of the same text.</summary>
		static void Fill(List<TargetFilterDiagnostic> diagnostics, int from, TextSpan span)
		{
			for (var i = from; i < diagnostics.Count; i++)
				if (diagnostics[i].Lines is null && diagnostics[i].Span is not { IsEmpty: false })
					diagnostics[i] = diagnostics[i].At(span);
		}

		/// <summary>Moves the diagnostics from <paramref name="from"/> on, made over a text that stands nowhere in the line, to <paramref name="span"/>, the span it was made from.</summary>
		static void Reanchor(List<TargetFilterDiagnostic> diagnostics, int from, TextSpan span)
		{
			for (var i = from; i < diagnostics.Count; i++)
				if (diagnostics[i].Lines is null)
					diagnostics[i] = diagnostics[i].At(span);
		}

		/// <summary>Places the diagnostics from <paramref name="from"/> on, made over rule line <paramref name="line"/>, in it; those with no span, or an empty one, at <paramref name="span"/>.</summary>
		static void Place(List<TargetFilterDiagnostic> diagnostics, int from, int line, TextSpan span)
		{
			for (var i = from; i < diagnostics.Count; i++)
				if (diagnostics[i].Lines is null)
					diagnostics[i] = diagnostics[i].InLines(RuleLinesSpan.InLine(line, diagnostics[i].Span is { IsEmpty: false } own ? own : span));
		}

		/// <summary>Places the diagnostics from <paramref name="from"/> on that are not placed yet at <paramref name="span"/>, whatever text they were made over.</summary>
		static void Place(List<TargetFilterDiagnostic> diagnostics, int from, RuleLinesSpan span)
		{
			for (var i = from; i < diagnostics.Count; i++)
				if (diagnostics[i].Lines is null)
					diagnostics[i] = diagnostics[i].InLines(span);
		}

		/// <summary>A span of the rule lines of a filter set: from a position in one line to a position in it or a later one.</summary>
		internal readonly struct RuleLinesSpan
		{
			public RuleLinesSpan(int startLine, int start, int endLine, int end)
			{
				StartLine = startLine;
				Start     = start;
				EndLine   = endLine;
				End       = end;
			}

			public static RuleLinesSpan InLine(int line, TextSpan span)
			{
				return new RuleLinesSpan(line, span.Start, line, span.End);
			}

			public int StartLine { get; }
			public int Start     { get; }
			public int EndLine   { get; }
			public int End       { get; }

			/// <summary>The span in its line, for a span within one line.</summary>
			public TextSpan Span => TextSpan.FromBounds(Start, End);
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

			internal bool HasTransientDiagnostics => _diagnostics.Any(static d => d.IsTransient);

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

			/// <summary>Reports the diagnostics, each told the filter and the characters it points at from where the rule lines stand.</summary>
			internal TargetFilterSet ReportDiagnostics(Action<TargetFilterDiagnostic>? reportDiagnostic, List<RuleOrigin>? origins)
			{
				if (reportDiagnostic is null)
					return this;

				foreach (var diagnostic in _diagnostics)
				{
					// A span that runs from one filter into another (a condition group continued in the next one) points
					// into none of them.
					if (origins is not null &&
						diagnostic.Lines is {} lines &&
						origins[lines.StartLine] is var start &&
						origins[lines.EndLine] is var end &&
						start.Filter == end.Filter)
						reportDiagnostic(diagnostic.InFilter(start.Filter, TextSpan.FromBounds(start.Start + lines.Start, end.Start + lines.End)));
					else
						reportDiagnostic(diagnostic);
				}

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

			TargetFilterDiagnostic(string id, string message, DiagnosticSeverity severity, int ruleIndex, bool isTransient = false)
				: this(id, message, severity, ruleIndex, isTransient, null, null, -1, default)
			{
			}

			TargetFilterDiagnostic(
				string             id,
				string             message,
				DiagnosticSeverity severity,
				int                ruleIndex,
				bool               isTransient,
				TextSpan?          span,
				RuleLinesSpan?     lines,
				int                filterIndex,
				TextSpan           filterSpan)
			{
				Id          = id;
				Message     = message;
				Severity    = severity;
				RuleIndex   = ruleIndex;
				IsTransient = isTransient;
				Span        = span;
				Lines       = lines;
				FilterIndex = filterIndex;
				FilterSpan  = filterSpan;
			}

			public string             Id       { get; }
			public string             Message  { get; }
			public DiagnosticSeverity Severity { get; }

			/// <summary>
			/// The index, among the filters read, of the filter the diagnostic points into, or -1 where it cannot be told (a
			/// rule that runs from one filter into the next, or no reader that tells it).
			/// </summary>
			public int FilterIndex { get; }

			/// <summary>The characters of that filter the diagnostic points at; meaningful only where <see cref="FilterIndex"/> is not -1.</summary>
			public TextSpan FilterSpan { get; }

			/// <summary>While the diagnostic is made: a span in the text being read, which is not placed in a rule line yet.</summary>
			internal TextSpan? Span { get; }

			/// <summary>Where in the rule lines of its filter set the diagnostic points.</summary>
			internal RuleLinesSpan? Lines { get; }

			/// <summary>The diagnostic pointing at <paramref name="span"/> of the text being read.</summary>
			internal TargetFilterDiagnostic At(TextSpan span)
			{
				return new TargetFilterDiagnostic(Id, Message, Severity, RuleIndex, IsTransient, span, Lines, FilterIndex, FilterSpan);
			}

			internal TargetFilterDiagnostic InLines(RuleLinesSpan lines)
			{
				return new TargetFilterDiagnostic(Id, Message, Severity, RuleIndex, IsTransient, null, lines, FilterIndex, FilterSpan);
			}

			internal TargetFilterDiagnostic InFilter(int filterIndex, TextSpan filterSpan)
			{
				return new TargetFilterDiagnostic(Id, Message, Severity, RuleIndex, IsTransient, Span, Lines, filterIndex, filterSpan);
			}

			/// <summary>The index of the compiled rule the diagnostic is about, or -1 where it is about no compiled rule.</summary>
			internal int RuleIndex { get; }

			/// <summary>The diagnostic is about the reading (it ran out of stack), not about the rule; it is not cached.</summary>
			internal bool IsTransient { get; }

			public static TargetFilterDiagnostic RuleTooDeep(string rule)
			{
				return new TargetFilterDiagnostic("AG0202", $"Invalid aspect filter rule '{rule}': The rule nests '<' or '(' too deeply to be read.", DiagnosticSeverity.Error, -1, isTransient: true);
			}

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

			public static TargetFilterDiagnostic NameWithWhiteSpace(string rule, string segment, int ruleIndex)
			{
				return new TargetFilterDiagnostic(
					AspectDiagnosticID.AspectFilterNameWithWhiteSpace,
					$"Target filter rule '{rule}' names '{segment}', with white space outside '<...>': a C# name never has white space there, so the rule probably holds a typo, such as a misspelled modifier word",
					DiagnosticSeverity.Info,
					ruleIndex);
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

		/// <summary>One rule line as <see cref="GramTargetFilterFront"/> reads it.</summary>
		sealed class FilterRule
		{
			public FilterRule(string text, int line, RulePrefix prefix, FilterMatcher matcher, string body, string? unknownKey, bool isConditionLine, TargetFilterLineSyntax syntax)
			{
				Text            = text;
				Line            = line;
				Prefix          = prefix;
				Matcher         = matcher;
				Body            = body;
				UnknownKey      = unknownKey;
				IsConditionLine = isConditionLine;
				Syntax          = syntax;
			}

			/// <summary>The line as the filter reader passed it.</summary>
			public string                  Text            { get; }
			/// <summary>The index of the line among the rule lines of the filter set.</summary>
			public int                     Line            { get; }
			public RulePrefix              Prefix          { get; }
			public FilterMatcher           Matcher         { get; }
			/// <summary>The text after the operator and the matcher prefix: what a compiled rule's text shows.</summary>
			public string                  Body            { get; }
			/// <summary>For a native line that begins with a word and a colon that is not a condition key, that word.</summary>
			public string?                 UnknownKey      { get; }
			/// <summary>A native line that reads as a condition rule.</summary>
			public bool                    IsConditionLine { get; }
			/// <summary>The grammar's reading of the line.</summary>
			public TargetFilterLineSyntax  Syntax          { get; }
		}

		/// <summary>What is worth telling about a valid rule: why it can match no method, and a name segment with white space where no C# name has it.</summary>
		readonly struct RuleNotes
		{
			public RuleNotes(string? matchesNothing, string? whiteSpaceSegment)
			{
				MatchesNothing    = matchesNothing;
				WhiteSpaceSegment = whiteSpaceSegment;
			}

			public string? MatchesNothing    { get; }
			public string? WhiteSpaceSegment { get; }
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
