using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Microsoft.CodeAnalysis.Text;

namespace AspectGenerator
{
	static partial class AspectFilters
	{
		/// <summary>
		/// The hand-written splitter that read target filters before the generated parser, moved here unchanged except for
		/// the names it calls the shared compile steps by. Filters never use it: <see cref="GetFilters(IEnumerable{string?}, Action{TargetFilterDiagnostic}?)"/>
		/// goes through <see cref="GramTargetFilterFront"/>. It is kept so that the tests can run both readers over the same
		/// inputs and compare what they compile, and only the tests call it.
		/// </summary>
		internal sealed class LegacyTargetFilterFront : ITargetFilterFront
		{
			static readonly LegacyTargetFilterFront Instance = new();

			LegacyTargetFilterFront()
			{
			}

			/// <summary>The filters as the hand-written splitter reads them; not cached.</summary>
			internal static TargetFilterSet GetFilters(IEnumerable<string?> filters, Action<TargetFilterDiagnostic>? reportDiagnostic = null)
			{
				return CreateFilterSet(ReadRules(filters).ToArray(), Instance).ReportDiagnostics(reportDiagnostic);
			}

			bool ITargetFilterFront.TryReadRule(string text, List<TargetFilterDiagnostic> diagnostics, out FilterRule rule)
			{
				rule = null!;

				if (!TryParseRule(text, diagnostics, out var prefix, out var matcher, out var body))
					return false;

				var unknownKey      = default(string);
				var isConditionLine = false;

				if (matcher == FilterMatcher.Pattern)
				{
					if (TryGetUnknownSimpleConditionKey(body, out var key))
						unknownKey = key;
					else
						isConditionLine = IsConditionRule(body);
				}

				rule = new FilterRule(text, prefix, matcher, body, unknownKey, isConditionLine, null);
				return true;
			}

			bool ITargetFilterFront.TryCompilePattern(FilterRule rule, List<TargetFilterDiagnostic> diagnostics, out CompiledPatternMatcher pattern, out string? matchesNothing)
			{
				matchesNothing = null;
				return TryCompile(rule.Body, diagnostics, out pattern);
			}

			bool ITargetFilterFront.TryCompileConditionGroup(List<ConditionGroupLine> lines, List<TargetFilterDiagnostic> diagnostics, out CompiledPatternMatcher pattern, out string? matchesNothing)
			{
				matchesNothing = null;
				return TryParseConditionRule(lines.Select(static l => (l.Text, l.ForceAnd)), diagnostics, out pattern);
			}

			static bool TryParseRule(string rule, List<TargetFilterDiagnostic> diagnostics, out RulePrefix prefix, out FilterMatcher matcher, out string body)
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

			// ---- the splitters ----

			static int IndexOfTopLevel(string text, char value)
			{
				var depth   = 0;
				var escaped = false;

				for (var i = 0; i < text.Length; i++)
				{
					var ch = text[i];

					if (escaped)
					{
						escaped = false;
						continue;
					}

					if (ch == '\\')
					{
						escaped = true;
						continue;
					}

					if (ch == '[' && TryReadRankSpecifier(text, i, out var rankEnd))
					{
						i = rankEnd;
						continue;
					}

					if (ch == '<')
						depth++;
					else if (ch == '>' && depth > 0)
						depth--;
					else if (depth == 0 && ch == value)
						return i;
				}

				return -1;
			}

			static List<string> SplitWhitespace(string text)
			{
				var result  = new List<string>();
				var start   = -1;
				var depth   = 0;
				var escaped = false;

				for (var i = 0; i < text.Length; i++)
				{
					var ch = text[i];

					if (escaped)
					{
						escaped = false;
						continue;
					}

					if (ch == '\\')
					{
						escaped = true;
						continue;
					}

					if (ch == '[' && TryReadRankSpecifier(text, i, out var rankEnd))
					{
						if (start < 0)
							start = i;

						i = rankEnd;
						continue;
					}

					if (ch == '<')
						depth++;
					else if (ch == '>' && depth > 0)
						depth--;

					if (depth == 0 && char.IsWhiteSpace(ch))
					{
						if (start >= 0)
						{
							result.Add(text[start..i]);
							start = -1;
						}

						continue;
					}

					if (start < 0)
						start = i;
				}

				if (start >= 0)
					result.Add(text[start..]);

				return result;
			}

			static bool TryReadMatcherPrefix(string rule, out string name, out string body)
			{
				name = "";
				body = "";

				var colonIndex = IndexOfTopLevel(rule, ':');

				if (colonIndex <= 0)
					return false;

				var candidate = rule[..colonIndex].Trim();

				if (candidate.Length == 0 || candidate.Any(static c => !char.IsLetter(c)))
					return false;

				name = candidate;
				body = rule[(colonIndex + 1)..].Trim();
				return true;
			}

			// ---- native rules ----

			static bool TryGetUnknownSimpleConditionKey(string text, out string key)
			{
				key = "";

				if (!TryReadMatcherPrefix(text, out var candidate, out _))
					return false;

				if (string.Equals(candidate, "pattern", StringComparison.OrdinalIgnoreCase) ||
					string.Equals(candidate, "contains", StringComparison.OrdinalIgnoreCase) ||
					string.Equals(candidate, "regex", StringComparison.OrdinalIgnoreCase) ||
					IsConditionKey(candidate))
					return false;

				key = candidate;
				return true;
			}

			static bool TryCompile(string text, List<TargetFilterDiagnostic> diagnostics, out CompiledPatternMatcher compiledPattern)
			{
				if (IsConditionRule(text))
					return TryParseConditionRule(text, diagnostics, out compiledPattern);

				return TryParseMethodPattern(text, diagnostics, out compiledPattern);
			}

			static bool IsConditionRule(string text)
			{
				if (IndexOfTopLevel(text, ';') >= 0)
					return true;

				if (!TryReadMatcherPrefix(text, out var key, out _))
					return IsModifierOrAccessibilityRule(text);

				return IsConditionKey(key);
			}

			static bool TryParseConditionRule(string text, List<TargetFilterDiagnostic> diagnostics, out CompiledPatternMatcher compiledPattern)
			{
				return TryParseConditionRule([(text, false)], diagnostics, out compiledPattern);
			}

			static bool TryParseConditionRule(IEnumerable<(string Text, bool ForceAnd)> lines, List<TargetFilterDiagnostic> diagnostics, out CompiledPatternMatcher compiledPattern)
			{
				var accessibility = AccessibilityMask.None;
				var modifiers     = ModifierMask.None;
				var items         = new List<ConditionItem>();
				var valid         = true;

				foreach (var line in lines)
				{
					foreach (var item in SplitTopLevel(line.Text, ';'))
					{
						if (item.Length == 0)
							continue;

						if (!TryReadMatcherPrefix(item, out var key, out var value))
						{
							foreach (var token in SplitWhitespace(item))
							{
								if (TryReadAccessibility(token, out var accessibilityMask))
								{
									accessibility |= accessibilityMask;
									continue;
								}

								if (TryReadModifier(token, out var modifierMask))
								{
									modifiers |= modifierMask;
									continue;
								}

								diagnostics.Add(TargetFilterDiagnostic.InvalidRule(item, "Condition item must contain only modifier/accessibility tokens or 'key: value'."));
								valid = false;
							}

							continue;
						}

						if (!IsConditionKey(key))
						{
							diagnostics.Add(TargetFilterDiagnostic.UnknownConditionKey(key));
							valid = false;
							continue;
						}

						items.Add(new ConditionItem(key, value, line.ForceAnd, value, new TextSpan(0, value.Length)));
					}
				}

				var conditions = ImmutableArray.CreateBuilder<Condition>();

				foreach (var bucket in GroupConditionItems(items))
				{
					if (TryParseCondition(bucket.Key, bucket.Value, diagnostics, out var condition))
						conditions.Add(condition);
					else
						valid = false;
				}

				compiledPattern = CompiledPatternMatcher.Create(accessibility, modifiers, conditions.ToImmutable(), null, null, null);
				return valid;
			}

			static bool IsModifierOrAccessibilityRule(string text)
			{
				var tokens = SplitWhitespace(text);

				if (tokens.Count == 0)
					return false;

				foreach (var token in tokens)
					if (!TryReadAccessibility(token, out _) && !TryReadModifier(token, out _))
						return false;

				return true;
			}

			static bool TryParseMethodPattern(string text, List<TargetFilterDiagnostic> diagnostics, out CompiledPatternMatcher matcher)
			{
				var accessibility = AccessibilityMask.None;
				var modifiers     = ModifierMask.None;
				var returnType    = default(TypePattern?);
				var parameters    = default(ParameterListPattern?);
				var valid         = true;

				var returnIndex = IndexOfTopLevel(text, ':');

				if (returnIndex >= 0)
				{
					var returnText = text[(returnIndex + 1)..].Trim();
					text = text[..returnIndex].Trim();

					if (!TryParseTypePattern(returnText, diagnostics, out returnType))
						valid = false;
				}

				var openParen = IndexOfTopLevel(text, '(');

				if (openParen >= 0)
				{
					var closeParen = FindMatchingCloseParen(text, openParen);

					if (closeParen < 0 || closeParen != text.Length - 1)
					{
						diagnostics.Add(TargetFilterDiagnostic.InvalidRule(text, "Parameter list must end the method pattern before an optional return pattern."));
						valid = false;
					}
					else if (!TryParseParameterList(text[(openParen + 1)..closeParen], diagnostics, out parameters))
						valid = false;

					text = text[..openParen].Trim();
				}

				var tokens = SplitWhitespace(text);
				var index  = 0;

				while (index < tokens.Count)
				{
					var token = tokens[index];

					if (TryReadAccessibility(token, out var accessibilityMask))
					{
						accessibility |= accessibilityMask;
						index++;
						continue;
					}

					if (TryReadModifier(token, out var modifierMask))
					{
						modifiers |= modifierMask;
						index++;
						continue;
					}

					break;
				}

				var patternText = string.Join(" ", tokens.Skip(index));

				if (patternText.Length == 0)
				{
					diagnostics.Add(TargetFilterDiagnostic.InvalidRule(text, "Method pattern is missing."));
					matcher = CompiledPatternMatcher.Create(accessibility, modifiers, [], null, parameters, returnType);
					return false;
				}

				if (!TryParseDottedPattern(patternText, diagnostics, allowRecursiveFinalSegment: false, out var methodPattern))
				{
					valid = false;

					// An empty segment ("Save.", "A..B") yields no pattern at all; the diagnostic is already reported.
					if (methodPattern is null)
					{
						matcher = CompiledPatternMatcher.Create(accessibility, modifiers, [], null, parameters, returnType);
						return false;
					}
				}

				if (methodPattern.Segments.Length == 1)
					methodPattern = DottedPattern.FromSegments([SegmentMatcher.Recursive, methodPattern.Segments[0]]);

				matcher = CompiledPatternMatcher.Create(accessibility, modifiers, [], methodPattern, parameters, returnType);
				return valid;
			}

			static int FindMatchingCloseParen(string text, int openParen)
			{
				var depth   = 0;
				var escaped = false;

				for (var i = openParen; i < text.Length; i++)
				{
					var ch = text[i];

					if (escaped)
					{
						escaped = false;
						continue;
					}

					if (ch == '\\')
					{
						escaped = true;
						continue;
					}

					if (ch == '<')
						depth++;
					else if (ch == '>' && depth > 0)
						depth--;
					else if (depth == 0 && ch == ')')
						return i;
				}

				return -1;
			}

			// ---- conditions ----

			static bool TryParseCondition(string key, string value, List<TargetFilterDiagnostic> diagnostics, out Condition condition)
			{
				condition = null!;
				key       = key.ToLowerInvariant();

				if (key == "path")
					key = "fulltype";

				if (!TryParseOrExpression(key, value, diagnostics, out condition))
					return false;

				return true;
			}

			static bool TryParseOrExpression(string key, string value, List<TargetFilterDiagnostic> diagnostics, out Condition condition)
			{
				condition = null!;

				if (!TrySplitConditionValues(value, '|', diagnostics, out var alternatives))
					return false;

				if (alternatives.Count == 1)
					return TryParseAndExpression(key, alternatives[0], diagnostics, out condition);

				var conditions = new List<Condition>(alternatives.Count);
				var valid      = true;

				foreach (var alternative in alternatives)
				{
					if (TryParseAndExpression(key, alternative, diagnostics, out var alternativeCondition))
						conditions.Add(alternativeCondition);
					else
						valid = false;
				}

				condition = Condition.Combine(ConditionOperator.Any, conditions.ToArray());
				return valid;
			}

			static bool TryParseAndExpression(string key, string value, List<TargetFilterDiagnostic> diagnostics, out Condition condition)
			{
				condition = null!;

				if (!TrySplitConditionValues(value, '&', diagnostics, out var items))
					return false;

				if (items.Count == 1)
					return TryParseSingle(key, items[0], diagnostics, out condition);

				var conditions = new List<Condition>(items.Count);
				var valid      = true;

				foreach (var item in items)
				{
					if (TryParseSingle(key, item, diagnostics, out var itemCondition))
						conditions.Add(itemCondition);
					else
						valid = false;
				}

				condition = Condition.Combine(ConditionOperator.All, conditions.ToArray());
				return valid;
			}

			static bool TryParseSingle(string key, string value, List<TargetFilterDiagnostic> diagnostics, out Condition condition)
			{
				condition = null!;

				switch (key)
				{
					case "namespace":
					case "fulltype":
					case "fullmethod":
						if (!TryParseDottedPattern(value, diagnostics, allowRecursiveFinalSegment: true, out var dottedPattern))
							return false;

						condition = Condition.Leaf(key, dottedPattern, null, null, null, null, null, null);
						return true;

					case "type":
					case "method":
						if (!SegmentMatcher.TryParse(value, diagnostics, out var segmentPattern))
							return false;

						condition = Condition.Leaf(key, null, segmentPattern, null, null, null, null, null);
						return true;

					case "returns":
						if (!TryParseTypePattern(value, diagnostics, out var typePattern))
							return false;

						condition = Condition.Leaf(key, null, null, typePattern, null, null, null, null);
						return true;

					case "param":
						if (!TryParseParameter(value, diagnostics, out var parameterPattern))
							return false;

						condition = Condition.Leaf(key, null, null, null, parameterPattern, null, null, null);
						return true;

					case "params":
						if (!TryParseParameterList(value, diagnostics, out var parameterListPattern))
							return false;

						condition = Condition.Leaf(key, null, null, null, null, parameterListPattern, null, null);
						return true;

					case "attributes":
						if (!TryParseTypePattern(value, diagnostics, out var attributePattern))
							return false;

						condition = Condition.Leaf(key, null, null, null, null, null, attributePattern, null);
						return true;

					case "signature":
						if (!SegmentMatcher.TryParse(value, diagnostics, out var signaturePattern))
							return false;

						condition = Condition.Leaf(key, null, null, null, null, null, null, signaturePattern);
						return true;

					default:
						diagnostics.Add(TargetFilterDiagnostic.UnknownConditionKey(key));
						return false;
				}
			}

			static bool TrySplitConditionValues(string text, char separator, List<TargetFilterDiagnostic> diagnostics, out List<string> result)
			{
				result              = new List<string>();
				var start           = 0;
				var genericDepth    = 0;
				var parenthesisDepth = 0;
				var escaped         = false;

				for (var i = 0; i < text.Length; i++)
				{
					var ch = text[i];

					if (escaped)
					{
						escaped = false;
						continue;
					}

					if (ch == '\\')
					{
						escaped = true;
						continue;
					}

					if (ch == '<')
						genericDepth++;
					else if (ch == '>' && genericDepth > 0)
						genericDepth--;
					else if (ch == '(')
						parenthesisDepth++;
					else if (ch == ')' && parenthesisDepth > 0)
						parenthesisDepth--;
					else if (ch == separator && genericDepth == 0 && parenthesisDepth == 0)
					{
						var value = text[start..i].Trim();

						if (value.Length == 0)
						{
							diagnostics.Add(TargetFilterDiagnostic.InvalidRule(text, $"Condition value has an empty operand around '{separator}'."));
							return false;
						}

						result.Add(value);
						start = i + 1;
					}
				}

				var last = text[start..].Trim();

				if (last.Length == 0)
				{
					diagnostics.Add(TargetFilterDiagnostic.InvalidRule(text, $"Condition value has an empty operand around '{separator}'."));
					return false;
				}

				result.Add(last);
				return true;
			}

			// ---- parameter, type and dotted patterns ----

			static bool TryParseParameterList(string text, List<TargetFilterDiagnostic> diagnostics, out ParameterListPattern pattern)
			{
				pattern = null!;
				text    = text.Trim();

				if (TryStripParentheses(text, out var parenthesizedText))
					text = parenthesizedText;

				if (text.Length == 0)
				{
					pattern = ParameterListPattern.Empty();
					return true;
				}

				if (text == "...")
				{
					pattern = ParameterListPattern.Ignore();
					return true;
				}

				var parts         = SplitTopLevel(text, ',');
				var items         = ImmutableArray.CreateBuilder<ParameterPattern?>();
				var ellipsisIndex = -1;
				var valid         = true;

				foreach (var part in parts)
				{
					if (part == "...")
					{
						if (ellipsisIndex >= 0)
						{
							diagnostics.Add(TargetFilterDiagnostic.InvalidParameterPattern(text, "Parameter wildcard '...' can appear at most once."));
							valid = false;
						}

						ellipsisIndex = items.Count;
						items.Add(null);
						continue;
					}

					if (!TryParseParameter(part, diagnostics, out var parameterPattern))
					{
						valid = false;
						continue;
					}

					items.Add(parameterPattern);
				}

				pattern = ParameterListPattern.Create(items.ToImmutable(), ellipsisIndex);
				return valid;
			}

			static bool TryStripParentheses(string text, out string result)
			{
				result = "";

				if (text.Length < 2 || text[0] != '(' || text[^1] != ')')
					return false;

				var depth        = 0;
				var genericDepth = 0;
				var escaped      = false;

				for (var i = 0; i < text.Length; i++)
				{
					var ch = text[i];

					if (escaped)
					{
						escaped = false;
						continue;
					}

					if (ch == '\\')
					{
						escaped = true;
						continue;
					}

					if (ch == '<')
						genericDepth++;
					else if (ch == '>' && genericDepth > 0)
						genericDepth--;
					else if (genericDepth == 0 && ch == '(')
						depth++;
					else if (genericDepth == 0 && ch == ')')
					{
						depth--;

						if (depth == 0 && i != text.Length - 1)
							return false;
					}
				}

				if (depth != 0)
					return false;

				result = text[1..^1].Trim();
				return true;
			}

			static bool TryParseParameter(string text, List<TargetFilterDiagnostic> diagnostics, out ParameterPattern pattern)
			{
				pattern = null!;
				text    = text.Trim();

				if (text == "_")
				{
					pattern = ParameterPattern.Any();
					return true;
				}

				var modifier = default(ParameterModifier?);

				foreach (var candidate in new[] { "ref", "out", "in", "params" })
				{
					if (!text.StartsWith(candidate + " ", StringComparison.Ordinal))
						continue;

					modifier = candidate switch
					{
						"ref"    => ParameterModifier.Ref,
						"out"    => ParameterModifier.Out,
						"in"     => ParameterModifier.In,
						"params" => ParameterModifier.Params,
						_        => ParameterModifier.None
					};
					text = text[(candidate.Length + 1)..].Trim();

					// "ref readonly" is one modifier, distinct from "ref".
					if (modifier == ParameterModifier.Ref && text.StartsWith("readonly ", StringComparison.Ordinal))
					{
						modifier = ParameterModifier.RefReadOnly;
						text     = text["readonly ".Length..].Trim();
					}

					break;
				}

				if (text.StartsWith("this ", StringComparison.Ordinal))
				{
					diagnostics.Add(TargetFilterDiagnostic.InvalidParameterPattern(text, "'this' is not supported in parameter patterns."));
					return false;
				}

				if (!TryParseTypePattern(text, diagnostics, out var typePattern))
					return false;

				pattern = ParameterPattern.Create(modifier, typePattern);
				return true;
			}

			static bool TryParseTypePattern(string text, List<TargetFilterDiagnostic> diagnostics, out TypePattern pattern)
			{
				pattern = null!;
				text    = text.Trim();

				if (text.Length == 0)
				{
					diagnostics.Add(TypePattern.Empty(text));
					return false;
				}

				text = TypePattern.ExpandTypeAliases(text);

				if (IndexOfTopLevel(text, '.') >= 0)
				{
					if (!TryParseDottedPattern(text, diagnostics, allowRecursiveFinalSegment: true, out var dottedPattern))
						return false;

					pattern = TypePattern.FromDotted(dottedPattern);
					return true;
				}

				if (!SegmentMatcher.TryParse(text, diagnostics, out var segmentPattern))
					return false;

				pattern = TypePattern.FromSegment(segmentPattern);
				return true;
			}

			static bool TryParseDottedPattern(string text, List<TargetFilterDiagnostic> diagnostics, bool allowRecursiveFinalSegment, out DottedPattern pattern)
			{
				return DottedPattern.TryCreate(text, SplitTopLevel(text.Trim(), '.'), diagnostics, allowRecursiveFinalSegment, out pattern);
			}
		}
	}
}
