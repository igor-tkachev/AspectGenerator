using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AspectGenerator
{
	static partial class AspectFilters
	{
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

				if (ch == '<')
					depth++;
				else if (ch == '>' && depth > 0)
					depth--;
				else if (depth == 0 && ch == value)
					return i;
			}

			return -1;
		}

		static List<string> SplitTopLevel(string text, char separator)
		{
			var result  = new List<string>();
			var start   = 0;
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

				if (ch == '<')
					depth++;
				else if (ch == '>' && depth > 0)
					depth--;
				else if (depth == 0 && ch == separator)
				{
					result.Add(text[start..i].Trim());
					start = i + 1;
				}
			}

			result.Add(text[start..].Trim());
			return result;
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

		sealed class PatternParser
		{
			static readonly HashSet<string> _conditionKeys = new(StringComparer.OrdinalIgnoreCase)
			{
				"namespace",
				"type",
				"fulltype",
				"method",
				"fullmethod",
				"returns",
				"param",
				"params",
				"signature"
			};

			readonly AccessibilityMask         _accessibility;
			readonly ModifierMask              _modifiers;
			readonly ImmutableArray<Condition> _conditions;
			readonly DottedPattern?            _fullMethodPattern;
			readonly ParameterListPattern?     _parameters;
			readonly TypePattern?              _returnType;

			PatternParser(
				AccessibilityMask         accessibility,
				ModifierMask              modifiers,
				ImmutableArray<Condition> conditions,
				DottedPattern?            fullMethodPattern,
				ParameterListPattern?     parameters,
				TypePattern?              returnType)
			{
				_accessibility     = accessibility;
				_modifiers         = modifiers;
				_conditions        = conditions;
				_fullMethodPattern = fullMethodPattern;
				_parameters        = parameters;
				_returnType        = returnType;
			}

			static bool IsKnownConditionKey(string key)
			{
				return _conditionKeys.Contains(key);
			}

			public static bool Compile(string text, List<TargetFilterDiagnostic> diagnostics, out PatternParser pattern)
			{
				if (IsConditionRule(text))
					return TryParseConditionRule(text, diagnostics, out pattern);

				return TryParseMethodPattern(text, diagnostics, out pattern);
			}

			static bool IsConditionRule(string text)
			{
				if (IndexOfTopLevel(text, ';') >= 0)
					return true;

				if (!TryReadMatcherPrefix(text, out var key, out _))
					return false;

				return IsKnownConditionKey(key);
			}

			static bool TryParseConditionRule(string text, List<TargetFilterDiagnostic> diagnostics, out PatternParser pattern)
			{
				var accessibility = AccessibilityMask.None;
				var modifiers     = ModifierMask.None;
				var conditions    = ImmutableArray.CreateBuilder<Condition>();
				var valid         = true;

				foreach (var item in SplitTopLevel(text, ';'))
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

					if (!IsKnownConditionKey(key))
					{
						diagnostics.Add(TargetFilterDiagnostic.UnknownConditionKey(key));
						valid = false;
						continue;
					}

					if (!Condition.TryParse(key, value, diagnostics, out var condition))
					{
						valid = false;
						continue;
					}

					conditions.Add(condition);
				}

				pattern = new PatternParser(accessibility, modifiers, conditions.ToImmutable(), null, null, null);
				return valid;
			}

			static bool TryParseMethodPattern(string text, List<TargetFilterDiagnostic> diagnostics, out PatternParser pattern)
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

					if (!TypePattern.TryParse(returnText, diagnostics, out returnType))
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
					else if (!ParameterListPattern.TryParse(text[(openParen + 1)..closeParen], diagnostics, out parameters))
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
					pattern = new PatternParser(accessibility, modifiers, [], null, parameters, returnType);
					return false;
				}

				if (!DottedPattern.TryParse(patternText, diagnostics, allowRecursiveFinalSegment: false, out var methodPattern))
					valid = false;

				if (methodPattern.Segments.Length == 1)
					methodPattern = DottedPattern.FromSegments([SegmentMatcher.Recursive, methodPattern.Segments[0]]);

				pattern = new PatternParser(accessibility, modifiers, [], methodPattern, parameters, returnType);
				return valid;
			}

			public bool IsMatch(in MethodTarget target)
			{
				if (_accessibility != AccessibilityMask.None && (target.Accessibility & _accessibility) != _accessibility)
					return false;

				if (_modifiers != ModifierMask.None && (target.Modifiers & _modifiers) != _modifiers)
					return false;

				foreach (var condition in _conditions)
					if (!condition.IsMatch(target))
						return false;

				if (_fullMethodPattern is not null && !_fullMethodPattern.IsMatch(target.FullMethodSegments))
					return false;

				if (_parameters is not null && !_parameters.IsMatch(target.Parameters))
					return false;

				if (_returnType is not null && !_returnType.IsMatch(target.ReturnType))
					return false;

				return true;
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
		}

		sealed class Condition
		{
			readonly string                _key;
			readonly DottedPattern?        _dottedPattern;
			readonly SegmentMatcher?       _segmentPattern;
			readonly TypePattern?          _typePattern;
			readonly ParameterPattern?     _parameterPattern;
			readonly ParameterListPattern? _parameterListPattern;
			readonly SegmentMatcher?       _signaturePattern;

			Condition(
				string                key,
				DottedPattern?        dottedPattern,
				SegmentMatcher?       segmentPattern,
				TypePattern?          typePattern,
				ParameterPattern?     parameterPattern,
				ParameterListPattern? parameterListPattern,
				SegmentMatcher?       signaturePattern)
			{
				_key                  = key;
				_dottedPattern        = dottedPattern;
				_segmentPattern       = segmentPattern;
				_typePattern          = typePattern;
				_parameterPattern     = parameterPattern;
				_parameterListPattern = parameterListPattern;
				_signaturePattern     = signaturePattern;
			}

			public static bool TryParse(string key, string value, List<TargetFilterDiagnostic> diagnostics, out Condition condition)
			{
				condition = null!;
				key       = key.ToLowerInvariant();

				switch (key)
				{
					case "namespace":
					case "fulltype":
					case "fullmethod":
						if (!DottedPattern.TryParse(value, diagnostics, allowRecursiveFinalSegment: true, out var dottedPattern))
							return false;

						condition = new Condition(key, dottedPattern, null, null, null, null, null);
						return true;

					case "type":
					case "method":
						if (!SegmentMatcher.TryParse(value, diagnostics, out var segmentPattern))
							return false;

						condition = new Condition(key, null, segmentPattern, null, null, null, null);
						return true;

					case "returns":
						if (!TypePattern.TryParse(value, diagnostics, out var typePattern))
							return false;

						condition = new Condition(key, null, null, typePattern, null, null, null);
						return true;

					case "param":
						if (!ParameterPattern.TryParse(value, diagnostics, out var parameterPattern))
							return false;

						condition = new Condition(key, null, null, null, parameterPattern, null, null);
						return true;

					case "params":
						if (!ParameterListPattern.TryParse(value, diagnostics, out var parameterListPattern))
							return false;

						condition = new Condition(key, null, null, null, null, parameterListPattern, null);
						return true;

					case "signature":
						if (!SegmentMatcher.TryParse(value, diagnostics, out var signaturePattern))
							return false;

						condition = new Condition(key, null, null, null, null, null, signaturePattern);
						return true;

					default:
						diagnostics.Add(TargetFilterDiagnostic.UnknownConditionKey(key));
						return false;
				}
			}

			public bool IsMatch(in MethodTarget target)
			{
				return _key switch
				{
					"namespace"  => _dottedPattern!.IsMatch(target.NamespaceSegments),
					"fulltype"   => _dottedPattern!.IsMatch(target.FullTypeSegments),
					"fullmethod" => _dottedPattern!.IsMatch(target.FullMethodSegments),
					"type"       => _segmentPattern!.IsMatch(target.TypeName),
					"method"     => _segmentPattern!.IsMatch(target.MethodName),
					"returns"    => _typePattern!.IsMatch(target.ReturnType),
					"param"      => target.Parameters.Any(p => _parameterPattern!.IsMatch(p)),
					"params"     => _parameterListPattern!.IsMatch(target.Parameters),
					"signature"  => _signaturePattern!.IsMatch(target.Signature),
					_            => false
				};
			}
		}

		sealed class ParameterListPattern
		{
			readonly bool                               _ignore;
			readonly ImmutableArray<ParameterPattern?> _parameters;
			readonly int                                _ellipsisIndex;

			ParameterListPattern(bool ignore, ImmutableArray<ParameterPattern?> parameters, int ellipsisIndex)
			{
				_ignore        = ignore;
				_parameters    = parameters;
				_ellipsisIndex = ellipsisIndex;
			}

			public static bool TryParse(string text, List<TargetFilterDiagnostic> diagnostics, out ParameterListPattern pattern)
			{
				pattern = null!;
				text    = text.Trim();

				if (text.Length == 0)
				{
					pattern = new ParameterListPattern(false, [], -1);
					return true;
				}

				if (text == "...")
				{
					pattern = new ParameterListPattern(true, [], -1);
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

					if (!ParameterPattern.TryParse(part, diagnostics, out var parameterPattern))
					{
						valid = false;
						continue;
					}

					items.Add(parameterPattern);
				}

				pattern = new ParameterListPattern(false, items.ToImmutable(), ellipsisIndex);
				return valid;
			}

			public bool IsMatch(List<ParameterTarget> parameters)
			{
				if (_ignore)
					return true;

				if (_ellipsisIndex < 0)
				{
					if (_parameters.Length != parameters.Count)
						return false;

					for (var i = 0; i < _parameters.Length; i++)
						if (!_parameters[i]!.IsMatch(parameters[i]))
							return false;

					return true;
				}

				var beforeCount = _ellipsisIndex;
				var afterCount  = _parameters.Length - _ellipsisIndex - 1;

				if (parameters.Count < beforeCount + afterCount)
					return false;

				for (var i = 0; i < beforeCount; i++)
					if (!_parameters[i]!.IsMatch(parameters[i]))
						return false;

				for (var i = 0; i < afterCount; i++)
				{
					var patternIndex   = _ellipsisIndex + 1 + i;
					var parameterIndex = parameters.Count - afterCount + i;

					if (!_parameters[patternIndex]!.IsMatch(parameters[parameterIndex]))
						return false;
				}

				return true;
			}
		}

		sealed class ParameterPattern
		{
			readonly bool               _any;
			readonly ParameterModifier? _modifier;
			readonly TypePattern?       _typePattern;

			ParameterPattern(bool any, ParameterModifier? modifier, TypePattern? typePattern)
			{
				_any         = any;
				_modifier    = modifier;
				_typePattern = typePattern;
			}

			public static bool TryParse(string text, List<TargetFilterDiagnostic> diagnostics, out ParameterPattern pattern)
			{
				pattern = null!;
				text    = text.Trim();

				if (text == "_")
				{
					pattern = new ParameterPattern(true, null, null);
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
					break;
				}

				if (text.StartsWith("this ", StringComparison.Ordinal))
				{
					diagnostics.Add(TargetFilterDiagnostic.InvalidParameterPattern(text, "'this' is not supported in parameter patterns."));
					return false;
				}

				if (!TypePattern.TryParse(text, diagnostics, out var typePattern))
					return false;

				pattern = new ParameterPattern(false, modifier, typePattern);
				return true;
			}

			public bool IsMatch(ParameterTarget parameter)
			{
				if (_any)
					return true;

				if (_modifier is {} modifier && parameter.Modifier != modifier)
					return false;

				return _typePattern!.IsMatch(parameter.Type);
			}
		}

		sealed class TypePattern
		{
			static readonly Dictionary<string,string> _typeAliases = new(StringComparer.Ordinal)
			{
				["bool"]    = "System.Boolean",
				["byte"]    = "System.Byte",
				["sbyte"]   = "System.SByte",
				["char"]    = "System.Char",
				["decimal"] = "System.Decimal",
				["double"]  = "System.Double",
				["float"]   = "System.Single",
				["int"]     = "System.Int32",
				["uint"]    = "System.UInt32",
				["nint"]    = "System.IntPtr",
				["nuint"]   = "System.UIntPtr",
				["long"]    = "System.Int64",
				["ulong"]   = "System.UInt64",
				["object"]  = "System.Object",
				["short"]   = "System.Int16",
				["ushort"]  = "System.UInt16",
				["string"]  = "System.String",
				["void"]    = "System.Void"
			};

			readonly DottedPattern?  _dottedPattern;
			readonly SegmentMatcher? _segmentPattern;

			TypePattern(DottedPattern? dottedPattern, SegmentMatcher? segmentPattern)
			{
				_dottedPattern  = dottedPattern;
				_segmentPattern = segmentPattern;
			}

			public static bool TryParse(string text, List<TargetFilterDiagnostic> diagnostics, out TypePattern pattern)
			{
				pattern = null!;
				text    = text.Trim();

				if (text.Length == 0)
				{
					diagnostics.Add(TargetFilterDiagnostic.InvalidRule(text, "Type pattern cannot be empty."));
					return false;
				}

				text = ExpandTypeAliases(text);

				if (IndexOfTopLevel(text, '.') >= 0)
				{
					if (!DottedPattern.TryParse(text, diagnostics, allowRecursiveFinalSegment: true, out var dottedPattern))
						return false;

					pattern = new TypePattern(dottedPattern, null);
					return true;
				}

				if (!SegmentMatcher.TryParse(text, diagnostics, out var segmentPattern))
					return false;

				pattern = new TypePattern(null, segmentPattern);
				return true;
			}

			public bool IsMatch(string typeName)
			{
				return _dottedPattern is not null
					? _dottedPattern.IsMatch(SplitTopLevel(typeName, '.'))
					: _segmentPattern!.IsMatch(GetLastTypeSegment(typeName));
			}

			static string GetLastTypeSegment(string typeName)
			{
				var segments = SplitTopLevel(typeName, '.');

				return segments.Count == 0 ? typeName : segments[^1];
			}

			static string ExpandTypeAliases(string text)
			{
				var sb = new StringBuilder(text.Length);

				for (var i = 0; i < text.Length;)
				{
					var ch = text[i];

					if (IsIdentifierStart(ch))
					{
						var start = i++;

						while (i < text.Length && IsIdentifierPart(text[i]))
							i++;

						var token = text[start..i];

						if (_typeAliases.TryGetValue(token, out var alias))
							sb.Append(alias);
						else
							sb.Append(token);

						if (i < text.Length && text[i] == '?' && !HasUnescapedWildcard(token))
						{
							i++;
						}

						continue;
					}

					sb.Append(ch);
					i++;
				}

				return sb.ToString();
			}

			static bool IsIdentifierStart(char ch)
			{
				return char.IsLetter(ch) || ch == '_';
			}

			static bool IsIdentifierPart(char ch)
			{
				return char.IsLetterOrDigit(ch) || ch == '_';
			}

			static bool HasUnescapedWildcard(string text)
			{
				var escaped = false;

				foreach (var ch in text)
				{
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

					if (ch is '*' or '?')
						return true;
				}

				return false;
			}
		}

		sealed class DottedPattern
		{
			readonly ImmutableArray<SegmentMatcher> _segments;

			DottedPattern(ImmutableArray<SegmentMatcher> segments)
			{
				_segments = segments;
			}

			public ImmutableArray<SegmentMatcher> Segments => _segments;

			public static DottedPattern FromSegments(ImmutableArray<SegmentMatcher> segments)
			{
				return new DottedPattern(segments);
			}

			public static bool TryParse(string text, List<TargetFilterDiagnostic> diagnostics, bool allowRecursiveFinalSegment, out DottedPattern pattern)
			{
				pattern = null!;

				var parts = SplitTopLevel(text.Trim(), '.');

				if (parts.Count == 0 || parts.Any(static p => p.Length == 0))
				{
					diagnostics.Add(TargetFilterDiagnostic.InvalidDottedPattern(text, "Dotted pattern contains an empty segment."));
					return false;
				}

				var segments = ImmutableArray.CreateBuilder<SegmentMatcher>();
				var valid    = true;

				for (var i = 0; i < parts.Count; i++)
				{
					var part = parts[i];

					if (part.Contains("**", StringComparison.Ordinal) && part != "**")
					{
						diagnostics.Add(TargetFilterDiagnostic.InvalidDottedPattern(text, "Recursive wildcard '**' must be a complete segment."));
						valid = false;
						continue;
					}

					if (part == "**" && i == parts.Count - 1 && !allowRecursiveFinalSegment)
					{
						diagnostics.Add(TargetFilterDiagnostic.InvalidDottedPattern(text, "Method pattern must end with a method name segment. '**' cannot be used as the final method segment."));
						valid = false;
						continue;
					}

					if (!SegmentMatcher.TryParse(part, diagnostics, out var segment))
					{
						valid = false;
						continue;
					}

					segments.Add(segment);
				}

				pattern = new DottedPattern(segments.ToImmutable());
				return valid;
			}

			public bool IsMatch(List<string> values)
			{
				return IsMatch(0, 0);

				bool IsMatch(int patternIndex, int valueIndex)
				{
					while (patternIndex < _segments.Length)
					{
						var segment = _segments[patternIndex];

						if (segment.Kind == SegmentMatcherKind.Recursive)
						{
							if (patternIndex == _segments.Length - 1)
								return true;

							for (var i = valueIndex; i <= values.Count; i++)
								if (IsMatch(patternIndex + 1, i))
									return true;

							return false;
						}

						if (valueIndex >= values.Count || !segment.IsMatch(values[valueIndex]))
							return false;

						patternIndex++;
						valueIndex++;
					}

					return valueIndex == values.Count;
				}
			}
		}

		enum SegmentMatcherKind
		{
			Any,
			Exact,
			Prefix,
			Suffix,
			Contains,
			Regex,
			Recursive
		}

		sealed class SegmentMatcher
		{
			public static readonly SegmentMatcher Recursive = new(SegmentMatcherKind.Recursive, "**", null);

			readonly string? _text;
			readonly Regex?  _regex;

			SegmentMatcher(SegmentMatcherKind kind, string? text, Regex? regex)
			{
				Kind   = kind;
				_text  = text;
				_regex = regex;
			}

			public SegmentMatcherKind Kind { get; }

			public static bool TryParse(string pattern, List<TargetFilterDiagnostic> diagnostics, out SegmentMatcher matcher)
			{
				matcher = null!;

				if (pattern == "**")
				{
					matcher = Recursive;
					return true;
				}

				if (pattern == "*")
				{
					matcher = new SegmentMatcher(SegmentMatcherKind.Any, null, null);
					return true;
				}

				if (!HasWildcard(pattern))
				{
					matcher = new SegmentMatcher(SegmentMatcherKind.Exact, Unescape(pattern), null);
					return true;
				}

				if (IsSimpleWildcard(pattern, out var kind, out var text))
				{
					matcher = new SegmentMatcher(kind, Unescape(text), null);
					return true;
				}

				try
				{
					matcher = new SegmentMatcher(
						SegmentMatcherKind.Regex,
						null,
						new Regex("^" + ToRegex(pattern) + "$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(RegexTimeoutMilliseconds)));
					return true;
				}
				catch (ArgumentException ex)
				{
					diagnostics.Add(TargetFilterDiagnostic.InvalidDottedPattern(pattern, ex.Message));
					return false;
				}
			}

			public bool IsMatch(string value)
			{
				return Kind switch
				{
					SegmentMatcherKind.Any       => true,
					SegmentMatcherKind.Exact     => string.Equals(value, _text, StringComparison.Ordinal),
					SegmentMatcherKind.Prefix    => value.StartsWith(_text!, StringComparison.Ordinal),
					SegmentMatcherKind.Suffix    => value.EndsWith(_text!, StringComparison.Ordinal),
					SegmentMatcherKind.Contains  => value.Contains(_text!, StringComparison.Ordinal),
					SegmentMatcherKind.Regex     => IsRegexMatch(value),
					SegmentMatcherKind.Recursive => true,
					_                            => false
				};
			}

			bool IsRegexMatch(string value)
			{
				try
				{
					return _regex!.IsMatch(value);
				}
				catch (RegexMatchTimeoutException)
				{
					return false;
				}
			}

			static bool HasWildcard(string pattern)
			{
				var escaped = false;

				foreach (var ch in pattern)
				{
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

					if (ch is '*' or '?')
						return true;
				}

				return false;
			}

			static bool IsSimpleWildcard(string pattern, out SegmentMatcherKind kind, out string text)
			{
				kind = SegmentMatcherKind.Regex;
				text = "";

				if (pattern.Count(static c => c == '?') > 0)
					return false;

				var first = pattern.IndexOf('*');
				var last  = pattern.LastIndexOf('*');

				if (first != last && !(first == 0 && last == pattern.Length - 1 && pattern[1..^1].IndexOf('*') < 0))
					return false;

				if (first == 0 && last == pattern.Length - 1)
				{
					kind = SegmentMatcherKind.Contains;
					text = pattern[1..^1];
					return text.Length > 0;
				}

				if (first == 0)
				{
					kind = SegmentMatcherKind.Suffix;
					text = pattern[1..];
					return true;
				}

				if (first == pattern.Length - 1)
				{
					kind = SegmentMatcherKind.Prefix;
					text = pattern[..^1];
					return true;
				}

				return false;
			}

			static string ToRegex(string pattern)
			{
				var sb      = new StringBuilder();
				var escaped = false;

				foreach (var ch in pattern)
				{
					if (escaped)
					{
						sb.Append(Regex.Escape(ch.ToString()));
						escaped = false;
						continue;
					}

					if (ch == '\\')
					{
						escaped = true;
						continue;
					}

					if (ch == '*')
						sb.Append(".*");
					else if (ch == '?')
						sb.Append('.');
					else
						sb.Append(Regex.Escape(ch.ToString()));
				}

				return sb.ToString();
			}

			static string Unescape(string pattern)
			{
				var sb      = new StringBuilder();
				var escaped = false;

				foreach (var ch in pattern)
				{
					if (escaped)
					{
						sb.Append(ch);
						escaped = false;
						continue;
					}

					if (ch == '\\')
					{
						escaped = true;
						continue;
					}

					sb.Append(ch);
				}

				return sb.ToString();
			}
		}

		static bool TryReadAccessibility(string text, out AccessibilityMask accessibility)
		{
			accessibility = text switch
			{
				"public"    => AccessibilityMask.Public,
				"private"   => AccessibilityMask.Private,
				"protected" => AccessibilityMask.Protected,
				"internal"  => AccessibilityMask.Internal,
				_           => AccessibilityMask.None
			};

			return accessibility != AccessibilityMask.None;
		}

		static bool TryReadModifier(string text, out ModifierMask modifier)
		{
			modifier = text switch
			{
				"static"   => ModifierMask.Static,
				"instance" => ModifierMask.Instance,
				"abstract" => ModifierMask.Abstract,
				"virtual"  => ModifierMask.Virtual,
				"override" => ModifierMask.Override,
				"sealed"   => ModifierMask.Sealed,
				"extern"   => ModifierMask.Extern,
				"unsafe"   => ModifierMask.Unsafe,
				_          => ModifierMask.None
			};

			return modifier != ModifierMask.None;
		}
	}
}
