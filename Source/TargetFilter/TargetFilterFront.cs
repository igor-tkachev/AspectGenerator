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
		/// Reads target filter lines with the generated parser (<see cref="TargetFilterParser"/>) and compiles what it reads
		/// into the rules the matchers run. The grammar decides how a line and each of its pieces read; this class trims the
		/// pieces the language trims, hands each to the publication that reads it, and builds the rules, reporting what
		/// the filter language has always reported, in the same order.
		/// </summary>
		/// <remarks>
		/// It also tells which valid rules can match no method (<c>AG0210</c>: accessibility words no method can have all
		/// of), and which require white space where no C# name has it (<c>AG0211</c>, a likely typo).
		/// </remarks>
		static class GramTargetFilterFront
		{
			/// <summary>Reads one rule line; <see langword="false"/>, with a diagnostic, for a line without a body.</summary>
			public static bool TryReadRule(string text, int line, List<TargetFilterDiagnostic> diagnostics, out FilterRule rule)
			{
				rule = null!;

				TargetFilterLineSyntax syntax;

				try
				{
					syntax = Read(TargetFilterParser.TryParseLine(text), text);
				}
				catch (Exception ex) when (Unreadable(ex, text) is {} diagnostic)
				{
					diagnostics.Add(diagnostic);
					return false;
				}

				var body    = syntax.Body;
				var matcher = syntax.Matcher switch
				{
					TargetFilterMatcher.Pattern  => FilterMatcher.ExplicitPattern,
					TargetFilterMatcher.Contains => FilterMatcher.Contains,
					TargetFilterMatcher.Regex    => FilterMatcher.Regex,
					_                            => FilterMatcher.Pattern
				};

				if (body is EmptyBodySyntax)
				{
					// What follows the operator, as the rule says it: "contains:", or nothing at all.
					var afterOperator = syntax.Matcher == TargetFilterMatcher.Native ? body.Span.Start : syntax.MatcherSpan.Start;

					diagnostics.Add(TargetFilterDiagnostic.InvalidRule(text[afterOperator..], "Target filter rule body cannot be empty."));
					return false;
				}

				var prefix = syntax.Prefix switch
				{
					TargetFilterLinePrefix.Exclude => RulePrefix.Exclude,
					TargetFilterLinePrefix.And     => RulePrefix.And,
					TargetFilterLinePrefix.Or      => RulePrefix.Or,
					_                              => RulePrefix.None
				};

				var isNative   = syntax.Matcher == TargetFilterMatcher.Native;
				var unknownKey = isNative && body is UnknownKeyBodySyntax unknown ? Text(text, unknown.Key) : null;

				rule = new FilterRule(text, line, prefix, matcher, Text(text, body.Span), unknownKey, isNative && body is ConditionLineSyntax, syntax);
				return true;
			}

			/// <summary>
			/// Compiles a <c>pattern:</c> rule or a native method pattern. <paramref name="notes"/> says what is worth telling
			/// about it when it is valid.
			/// </summary>
			public static bool TryCompilePattern(FilterRule rule, List<TargetFilterDiagnostic> diagnostics, out CompiledPatternMatcher pattern, out RuleNotes notes)
			{
				pattern = null!;
				notes   = default;

				try
				{
					return rule.Syntax.Body is ConditionLineSyntax
						? TryCompileConditionLines([(rule, false)], diagnostics, out pattern, out notes)
						: TryCompileMethodPattern(rule.Text, (MethodPatternSyntax)rule.Syntax.Body, diagnostics, out pattern, out notes);
				}
				catch (Exception ex) when (Unreadable(ex, rule.Text) is {} diagnostic)
				{
					diagnostics.Add(diagnostic);
					return false;
				}
			}

			/// <summary>Compiles the lines of one native condition group into one rule.</summary>
			public static bool TryCompileConditionGroup(List<ConditionGroupLine> lines, List<TargetFilterDiagnostic> diagnostics, out CompiledPatternMatcher pattern, out RuleNotes notes)
			{
				pattern = null!;
				notes   = default;

				try
				{
					return TryCompileConditionLines(lines.Select(static l => (l.Rule, l.ForceAnd)), diagnostics, out pattern, out notes);
				}
				catch (Exception ex) when (Unreadable(ex, lines[0].Rule.Text) is {} diagnostic)
				{
					diagnostics.Add(diagnostic);
					return false;
				}
			}

			/// <summary>
			/// The diagnostic of a rule that cannot be read, or <see langword="null"/> for any other failure. A rule nested
			/// deeper than the stack allows is one (the parser does not move a reading to a thread of its own, see
			/// <see cref="TargetFilterParser"/>); a text one of the grammar's readings refuses is another, which the
			/// readings are written never to do, and which is reported rather than let fail the generator.
			/// </summary>
			static TargetFilterDiagnostic? Unreadable(Exception exception, string rule)
			{
				return exception switch
				{
					InsufficientExecutionStackException => TargetFilterDiagnostic.RuleTooDeep(rule),
					RefusedException                    => TargetFilterDiagnostic.InvalidRule(rule, "The rule cannot be read."),
					_                                   => null
				};
			}

			/// <summary>A reading of the grammar refused its text.</summary>
			sealed class RefusedException(string message) : Exception(message);

			// ---- condition rules ----

			static bool TryCompileConditionLines(
				IEnumerable<(FilterRule Rule, bool ForceAnd)> lines,
				List<TargetFilterDiagnostic>                  diagnostics,
				out CompiledPatternMatcher                    compiledPattern,
				out RuleNotes                                 notes)
			{
				var accessibility = AccessibilityMask.None;
				var modifiers     = ModifierMask.None;
				var items         = new List<ConditionItem>();
				var valid         = true;

				foreach (var (rule, forceAnd) in lines)
				{
					var source = rule.Text;

					foreach (var itemSpan in ((ConditionLineSyntax)rule.Syntax.Body).Items)
					{
						var span = Trim(source, itemSpan);

						if (span.IsEmpty)
							continue;

						var mark = diagnostics.Count;

						ReadItem(rule, forceAnd, span);
						Place(diagnostics, mark, rule.Line, span);
					}
				}

				var conditions = ImmutableArray.CreateBuilder<Condition>();
				var whiteSpace = default(string);

				foreach (var bucket in GroupConditionItems(items))
				{
					// One value is read where it stands; the values of one key are alternatives, and are read as the text
					// they make when joined, as that is what the language has always read. That text stands nowhere in the
					// lines: what it reports points at the values it was joined from.
					var single = bucket.Items.Count == 1;
					var value  = single ? bucket.Items[0].Value     : bucket.Value;
					var source = single ? bucket.Items[0].Source    : value;
					var span   = single ? bucket.Items[0].ValueSpan : new TextSpan(0, value.Length);
					var syntax = Read(TargetFilterParser.TryParseConditionValue(source, span.Start, span.Length), source);
					var mark   = diagnostics.Count;

					if (TryCompileCondition(bucket.Key, source, value, span, syntax, diagnostics, out var condition, out var conditionWhiteSpace))
					{
						conditions.Add(condition);
						whiteSpace ??= conditionWhiteSpace;
					}
					else
						valid = false;

					var first = bucket.Items[0];
					var last  = bucket.Items[^1];

					if (single)
						Place(diagnostics, mark, first.Line, first.ValueSpan);
					else
						Place(diagnostics, mark, new RuleLinesSpan(first.Line, first.ValueSpan.Start, last.Line, last.ValueSpan.End));
				}

				compiledPattern = CompiledPatternMatcher.Create(accessibility, modifiers, conditions.ToImmutable(), null, null, null);
				notes           = valid ? new RuleNotes(MatchesNothing(accessibility), whiteSpace) : default;
				return valid;

				void ReadItem(FilterRule rule, bool forceAnd, TextSpan span)
				{
					var source = rule.Text;

					switch (Read(TargetFilterParser.TryParseConditionItem(source, span.Start, span.Length), source))
					{
						case KeyItemSyntax keyItem:
						{
							var key = Text(source, keyItem.Key);

							if (!IsConditionKey(key))
							{
								diagnostics.Add(TargetFilterDiagnostic.UnknownConditionKey(key).At(keyItem.Key));
								valid = false;
								return;
							}

							items.Add(new ConditionItem(key, Text(source, keyItem.Value), forceAnd, source, rule.Line, keyItem.Value));
							break;
						}

						case WordsItemSyntax wordsItem:
							foreach (var word in wordsItem.Words.Words)
							{
								var token = Text(source, word);

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

								diagnostics.Add(TargetFilterDiagnostic.InvalidRule(Text(source, span), "Condition item must contain only modifier/accessibility tokens or 'key: value'.").At(word));
								valid = false;
							}

							break;
					}
				}
			}

			static bool TryCompileCondition(
				string                       key,
				string                       source,
				string                       value,
				TextSpan                     span,
				ConditionValueSyntax         syntax,
				List<TargetFilterDiagnostic> diagnostics,
				out Condition                condition,
				out string?                  whiteSpace)
			{
				condition      = null!;
				whiteSpace = null;
				key            = key.ToLowerInvariant();

				if (key == "path")
					key = "fulltype";

				var alternatives = syntax.Alternatives;

				if (alternatives.FirstOrDefault(a => Trim(source, a.Span).IsEmpty) is {} empty)
				{
					diagnostics.Add(TargetFilterDiagnostic.InvalidRule(value, "Condition value has an empty operand around '|'.").At(OperatorNextTo(source, empty.Span, '|', span)));
					return false;
				}

				if (alternatives.Length == 1)
					return TryCompileAlternative(key, source, alternatives[0], diagnostics, out condition, out whiteSpace);

				var conditions = new List<Condition>(alternatives.Length);
				var reasons    = new List<string?>(alternatives.Length);
				var valid      = true;

				foreach (var alternative in alternatives)
				{
					if (TryCompileAlternative(key, source, alternative, diagnostics, out var alternativeCondition, out var reason))
					{
						conditions.Add(alternativeCondition);
						reasons.Add(reason);
					}
					else
						valid = false;
				}

				condition = Condition.Combine(ConditionOperator.Any, conditions.ToArray());

				// Any alternative that can match makes the condition one that can.
				if (valid && reasons.All(static r => r is not null))
					whiteSpace = reasons[0];

				return valid;
			}

			static bool TryCompileAlternative(
				string                       key,
				string                       source,
				ConditionAlternativeSyntax   alternative,
				List<TargetFilterDiagnostic> diagnostics,
				out Condition                condition,
				out string?                  whiteSpace)
			{
				condition      = null!;
				whiteSpace = null;

				var operands = alternative.Operands.Select(o => Trim(source, o)).ToArray();
				var empty    = Array.FindIndex(operands, static o => o.IsEmpty);

				if (empty >= 0)
				{
					var quoted = Trim(source, alternative.Span);

					diagnostics.Add(TargetFilterDiagnostic.InvalidRule(Text(source, quoted), "Condition value has an empty operand around '&'.").At(OperatorNextTo(source, alternative.Operands[empty], '&', quoted)));
					return false;
				}

				if (operands.Length == 1)
					return TryCompileSingle(key, source, operands[0], diagnostics, out condition, out whiteSpace);

				var conditions = new List<Condition>(operands.Length);
				var valid      = true;

				foreach (var operand in operands)
				{
					if (TryCompileSingle(key, source, operand, diagnostics, out var operandCondition, out var reason))
					{
						conditions.Add(operandCondition);
						whiteSpace ??= reason;
					}
					else
						valid = false;
				}

				condition = Condition.Combine(ConditionOperator.All, conditions.ToArray());
				return valid;
			}

			/// <summary>One operand of a condition value, over a trimmed span; what it reports points into the operand.</summary>
			static bool TryCompileSingle(
				string                       key,
				string                       source,
				TextSpan                     span,
				List<TargetFilterDiagnostic> diagnostics,
				out Condition                condition,
				out string?                  whiteSpace)
			{
				var mark  = diagnostics.Count;
				var valid = TryCompileOperand(key, source, span, diagnostics, out condition, out whiteSpace);

				Fill(diagnostics, mark, span);
				return valid;
			}

			static bool TryCompileOperand(
				string                       key,
				string                       source,
				TextSpan                     span,
				List<TargetFilterDiagnostic> diagnostics,
				out Condition                condition,
				out string?                  whiteSpace)
			{
				condition      = null!;
				whiteSpace = null;

				var value = Text(source, span);

				switch (key)
				{
					case "namespace":
					case "fulltype":
					case "fullmethod":
						if (!TryCompileDotted(value, Read(TargetFilterParser.TryParseDotted(source, span.Start, span.Length), source), source, allowRecursiveFinalSegment: true, diagnostics, out var dottedPattern, out whiteSpace))
							return false;

						condition = Condition.Leaf(key, dottedPattern, null, null, null, null, null, null);
						return true;

					case "type":
					case "method":
						if (!SegmentMatcher.TryParse(value, diagnostics, out var segmentPattern))
							return false;

						whiteSpace = WhiteSpaceSegment(value);
						condition      = Condition.Leaf(key, null, segmentPattern, null, null, null, null, null);
						return true;

					case "returns":
						if (!TryCompileType(source, span, diagnostics, out var typePattern))
							return false;

						condition = Condition.Leaf(key, null, null, typePattern, null, null, null, null);
						return true;

					case "param":
						if (!TryCompileParameter(source, span, diagnostics, out var parameterPattern))
							return false;

						condition = Condition.Leaf(key, null, null, null, parameterPattern, null, null, null);
						return true;

					case "params":
						if (!TryCompileParameterList(source, span, diagnostics, out var parameterListPattern))
							return false;

						condition = Condition.Leaf(key, null, null, null, null, parameterListPattern, null, null);
						return true;

					case "attributes":
						if (!TryCompileType(source, span, diagnostics, out var attributePattern))
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

			// ---- method patterns ----

			static bool TryCompileMethodPattern(
				string                       source,
				MethodPatternSyntax          syntax,
				List<TargetFilterDiagnostic> diagnostics,
				out CompiledPatternMatcher   matcher,
				out RuleNotes                notes)
			{
				var accessibility = AccessibilityMask.None;
				var modifiers     = ModifierMask.None;
				var returnType    = default(TypePattern?);
				var parameters    = default(ParameterListPattern?);
				var valid         = true;

				notes = default;

				if (syntax.Returns is {} returns && !TryCompileType(source, Trim(source, returns), diagnostics, out returnType))
					valid = false;

				if (syntax.Parameters is {} group)
				{
					if (!group.EndsThePattern)
					{
						// What comes before the return type, as the message has always quoted it.
						var before = TextSpan.FromBounds(syntax.Span.Start, syntax.Returns is {} r ? r.Start - 1 : syntax.Span.End);

						diagnostics.Add(TargetFilterDiagnostic.InvalidRule(Text(source, Trim(source, before)), "Parameter list must end the method pattern before an optional return pattern.").At(Trim(source, before)));
						valid = false;
					}
					else
					{
						var mark = diagnostics.Count;

						if (!TryCompileParameterList(source, group.Inner, diagnostics, out parameters))
							valid = false;

						Fill(diagnostics, mark, Trim(source, group.Inner));
					}
				}

				var name  = Trim(source, syntax.Name);
				var words = Read(TargetFilterParser.TryParseWords(source, name.Start, name.Length), source).Words;
				var index = 0;

				while (index < words.Length)
				{
					var word = Text(source, words[index]);

					if (TryReadAccessibility(word, out var accessibilityMask))
					{
						accessibility |= accessibilityMask;
						index++;
						continue;
					}

					if (TryReadModifier(word, out var modifierMask))
					{
						modifiers |= modifierMask;
						index++;
						continue;
					}

					break;
				}

				// The name is its words after the modifiers, one space between two of them.
				var patternText = string.Join(" ", words.Skip(index).Select(w => Text(source, w)));

				if (patternText.Length == 0)
				{
					diagnostics.Add(TargetFilterDiagnostic.InvalidRule(Text(source, name), "Method pattern is missing.").At(name));
					matcher = CompiledPatternMatcher.Create(accessibility, modifiers, [], null, parameters, returnType);
					return false;
				}

				// The name is read as the words make it; what it reports points at the words in the line.
				var nameMark = diagnostics.Count;
				var compiled = TryCompileDotted(patternText, Read(TargetFilterParser.TryParseDotted(patternText), patternText), patternText, allowRecursiveFinalSegment: false, diagnostics, out var methodPattern, out var nameWhiteSpace);

				Reanchor(diagnostics, nameMark, TextSpan.FromBounds(words[index].Start, words[^1].End));

				if (!compiled)
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
				notes   = valid ? new RuleNotes(MatchesNothing(accessibility), nameWhiteSpace) : default;
				return valid;
			}

			// ---- dotted, type and parameter patterns ----

			static bool TryCompileDotted(
				string                       text,
				DottedSyntax                 syntax,
				string                       source,
				bool                         allowRecursiveFinalSegment,
				List<TargetFilterDiagnostic> diagnostics,
				out DottedPattern            pattern,
				out string?                  whiteSpace)
			{
				var parts = syntax.Parts.Select(p => Text(source, Trim(source, p))).ToArray();

				whiteSpace = null;

				foreach (var part in parts)
					whiteSpace ??= WhiteSpaceSegment(part);

				return DottedPattern.TryCreate(text, parts, diagnostics, allowRecursiveFinalSegment, out pattern);
			}

			/// <summary>A type pattern, over a trimmed span; its C# aliases are expanded before it is read as a dotted pattern.</summary>
			static bool TryCompileType(string source, TextSpan span, List<TargetFilterDiagnostic> diagnostics, out TypePattern pattern)
			{
				pattern = null!;

				var text = Text(source, span);

				if (text.Length == 0)
				{
					diagnostics.Add(TypePattern.Empty(text).At(span));
					return false;
				}

				// The aliases are expanded into a text of its own; what it reports points at the type as written.
				var mark  = diagnostics.Count;
				var valid = TryCompileExpandedType(TypePattern.ExpandTypeAliases(text), diagnostics, out pattern);

				Reanchor(diagnostics, mark, span);
				return valid;
			}

			static bool TryCompileExpandedType(string text, List<TargetFilterDiagnostic> diagnostics, out TypePattern pattern)
			{
				pattern = null!;

				var dotted = Read(TargetFilterParser.TryParseDotted(text), text);

				if (dotted.Parts.Length > 1)
				{
					if (!TryCompileDotted(text, dotted, text, allowRecursiveFinalSegment: true, diagnostics, out var dottedPattern, out _))
						return false;

					pattern = TypePattern.FromDotted(dottedPattern);
					return true;
				}

				if (!SegmentMatcher.TryParse(text, diagnostics, out var segmentPattern))
					return false;

				pattern = TypePattern.FromSegment(segmentPattern);
				return true;
			}

			/// <summary>A parameter list pattern, with or without its parentheses.</summary>
			static bool TryCompileParameterList(string source, TextSpan span, List<TargetFilterDiagnostic> diagnostics, out ParameterListPattern pattern)
			{
				pattern = null!;
				span    = Trim(source, span);

				var list  = Read(TargetFilterParser.TryParseParameterList(source, span.Start, span.Length), source);
				var inner = list.Parenthesized ? Trim(source, list.Inner) : span;
				var text  = Text(source, inner);

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

				var parts         = Read(TargetFilterParser.TryParseParameterParts(source, inner.Start, inner.Length), source).Parts;
				var items         = ImmutableArray.CreateBuilder<ParameterPattern?>();
				var ellipsisIndex = -1;
				var valid         = true;

				foreach (var partSpan in parts)
				{
					var part = Trim(source, partSpan);

					if (Text(source, part) == "...")
					{
						if (ellipsisIndex >= 0)
						{
							diagnostics.Add(TargetFilterDiagnostic.InvalidParameterPattern(text, "Parameter wildcard '...' can appear at most once.").At(part));
							valid = false;
						}

						ellipsisIndex = items.Count;
						items.Add(null);
						continue;
					}

					var mark = diagnostics.Count;

					if (!TryCompileParameter(source, part, diagnostics, out var parameterPattern))
					{
						Fill(diagnostics, mark, part);
						valid = false;
						continue;
					}

					items.Add(parameterPattern);
				}

				pattern = ParameterListPattern.Create(items.ToImmutable(), ellipsisIndex);
				return valid;
			}

			/// <summary>One parameter pattern, over a trimmed span.</summary>
			static bool TryCompileParameter(string source, TextSpan span, List<TargetFilterDiagnostic> diagnostics, out ParameterPattern pattern)
			{
				pattern = null!;

				var parameter = Read(TargetFilterParser.TryParseParameter(source, span.Start, span.Length), source);

				switch (parameter.Kind)
				{
					case ParameterSyntaxKind.Any:
						pattern = ParameterPattern.Any();
						return true;

					case ParameterSyntaxKind.This:
						diagnostics.Add(TargetFilterDiagnostic.InvalidParameterPattern(Text(source, parameter.Type), "'this' is not supported in parameter patterns.").At(span));
						return false;

					default:
						if (!TryCompileType(source, parameter.Type, diagnostics, out var typePattern))
							return false;

						pattern = ParameterPattern.Create(parameter.Modifier, typePattern);
						return true;
				}
			}

			// ---- rules that match nothing, and names with white space ----

			/// <summary>Why a rule with these accessibility words can match no method, or <see langword="null"/>.</summary>
			static string? MatchesNothing(AccessibilityMask accessibility)
			{
				if (IsAchievableAccessibility(accessibility))
					return null;

				var words = new List<string>();

				if ((accessibility & AccessibilityMask.Public)    != 0) words.Add("public");
				if ((accessibility & AccessibilityMask.Protected) != 0) words.Add("protected");
				if ((accessibility & AccessibilityMask.Internal)  != 0) words.Add("internal");
				if ((accessibility & AccessibilityMask.Private)   != 0) words.Add("private");

				return $"no method is {string.Join(" and ", words)} at once; a method must have every accessibility word of a rule";
			}

			/// <summary>
			/// The segment, where a name segment (of a namespace, a type or a method) holds white space where a C# name never
			/// does, or <see langword="null"/>. A C# name holds white space only inside <c>&lt;…&gt;</c>, between the type
			/// arguments of a function pointer (<c>Empty&lt;delegate* unmanaged&lt;System.Void&gt;[]&gt;</c>); white space
			/// counts when it stands outside <c>&lt;…&gt;</c> in the part of the segment that is compared character for
			/// character with the start of a name, before its first wildcard, which could stand for a <c>&lt;</c>. An escaped
			/// bracket counts as a bracket, since it matches the name's own; an array rank is passed over. Names that are not
			/// C#'s, written in F# or IL, can hold white space anywhere: such a rule is told about, not called empty.
			/// </summary>
			static string? WhiteSpaceSegment(string segment)
			{
				var depth   = 0;
				var escaped = false;

				for (var i = 0; i < segment.Length; i++)
				{
					var ch = segment[i];

					if (!escaped)
					{
						if (ch == '\\')
						{
							escaped = true;
							continue;
						}

						if (ch is '*' or '?')
							return null;

						if (ch == '[' && TryReadRankSpecifier(segment, i, out var rankEnd))
						{
							i = rankEnd;
							continue;
						}
					}

					escaped = false;

					if (ch == '<')
						depth++;
					else if (ch == '>' && depth > 0)
						depth--;
					else if (depth == 0 && char.IsWhiteSpace(ch))
						return segment;
				}

				return null;
			}

			// ---- reading ----

			/// <summary>
			/// The operator <paramref name="op"/> next to the empty operand at <paramref name="operand"/>: the one after it, or
			/// the one before it for the last operand; <paramref name="fallback"/> where neither stands there.
			/// </summary>
			static TextSpan OperatorNextTo(string source, TextSpan operand, char op, TextSpan fallback)
			{
				if (operand.End < source.Length && source[operand.End] == op)
					return new TextSpan(operand.End, 1);

				if (operand.Start > 0 && source[operand.Start - 1] == op)
					return new TextSpan(operand.Start - 1, 1);

				return fallback;
			}

			static T Read<T>(TargetFilterParser.Match<T> match, string source)
			{
				// Every publication reads every text; a refusal is a defect of the grammar.
				if (!match.IsSuccess)
					throw new RefusedException($"The target filter grammar refused '{source}': {match.Error}");

				return match.Value;
			}

			static string Text(string source, TextSpan span)
			{
				return source.Substring(span.Start, span.Length);
			}

			/// <summary>The span without the white space at either end, as <see cref="string.Trim()"/> takes it.</summary>
			static TextSpan Trim(string source, TextSpan span)
			{
				var start = span.Start;
				var end   = span.End;

				while (start < end && char.IsWhiteSpace(source[start]))
					start++;

				while (end > start && char.IsWhiteSpace(source[end - 1]))
					end--;

				return TextSpan.FromBounds(start, end);
			}
		}
	}
}
