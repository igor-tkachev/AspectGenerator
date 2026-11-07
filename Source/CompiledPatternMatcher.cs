using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis.Text;

namespace AspectGenerator
{
	static partial class AspectFilters
	{
		/// <summary>
		/// Reads an array rank specifier at <paramref name="start"/>: '[', then only commas and spaces, then ']'
		/// (<c>[]</c>, <c>[,]</c>, <c>[,,]</c>). It is one unit for the top-level splits, so the commas of a rank do not
		/// separate parameters. Any other '[' (a wildcard rule such as <c>*[*</c>) is an ordinary character.
		/// </summary>
		static bool TryReadRankSpecifier(string text, int start, out int end)
		{
			end = start + 1;

			while (end < text.Length && (text[end] == ',' || text[end] == ' '))
				end++;

			return end < text.Length && text[end] == ']';
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

				if (ch == '[' && TryReadRankSpecifier(text, i, out var rankEnd))
				{
					i = rankEnd;
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

		static readonly HashSet<string> _conditionKeys = new(StringComparer.OrdinalIgnoreCase)
		{
			"namespace",
			"path",
			"type",
			"fulltype",
			"method",
			"fullmethod",
			"returns",
			"param",
			"params",
			"attributes",
			"signature"
		};

		/// <summary>A condition key, compared ordinally and ignoring case.</summary>
		internal static bool IsConditionKey(string key)
		{
			return _conditionKeys.Contains(key);
		}

		/// <summary>One <c>key: value</c> item of a condition group, with the text its value is read from.</summary>
		readonly struct ConditionItem
		{
			public ConditionItem(string key, string value, bool forceAnd, string source, TextSpan valueSpan)
			{
				Key       = key;
				Value     = value;
				ForceAnd  = forceAnd;
				Source    = source;
				ValueSpan = valueSpan;
			}

			public string   Key       { get; }
			/// <summary>The value, trimmed.</summary>
			public string   Value     { get; }
			public bool     ForceAnd  { get; }
			/// <summary>The text <see cref="ValueSpan"/> is a span of.</summary>
			public string   Source    { get; }
			public TextSpan ValueSpan { get; }
		}

		sealed class ConditionBucket
		{
			public ConditionBucket(string key)
			{
				Key = key;
			}

			public string              Key    { get; }
			public List<ConditionItem> Items  { get; } = new();

			/// <summary>The values of the bucket as one value: they are alternatives of one another.</summary>
			public string Value => string.Join(" | ", Items.Select(static i => i.Value));
		}

		/// <summary>
		/// The items of a condition group by key: a key's values are alternatives, items of different keys must all hold, and
		/// an item from a line that began with <c>&amp;</c> starts a condition of its own.
		/// </summary>
		static List<ConditionBucket> GroupConditionItems(List<ConditionItem> items)
		{
			var buckets = new List<ConditionBucket>();

			foreach (var item in items)
			{
				var bucketIndex = -1;

				if (!item.ForceAnd)
					for (var i = buckets.Count - 1; i >= 0; i--)
						if (string.Equals(buckets[i].Key, item.Key, StringComparison.OrdinalIgnoreCase))
						{
							bucketIndex = i;
							break;
						}

				if (bucketIndex < 0)
				{
					var bucket = new ConditionBucket(item.Key);
					bucket.Items.Add(item);
					buckets.Add(bucket);
				}
				else
					buckets[bucketIndex].Items.Add(item);
			}

			return buckets;
		}

		/// <summary>The accessibility masks a method can have: the matcher requires every bit of a rule's mask.</summary>
		static bool IsAchievableAccessibility(AccessibilityMask mask)
		{
			foreach (var achievable in new[]
			{
				AccessibilityMask.Public,
				AccessibilityMask.Protected,
				AccessibilityMask.Internal,
				AccessibilityMask.Private,
				AccessibilityMask.Protected | AccessibilityMask.Internal,
				AccessibilityMask.Private   | AccessibilityMask.Protected
			})
				if ((mask & achievable) == mask)
					return true;

			return false;
		}

		sealed class CompiledPatternMatcher
		{
			readonly AccessibilityMask         _accessibility;
			readonly ModifierMask              _modifiers;
			readonly ImmutableArray<Condition> _conditions;
			readonly DottedPattern?            _fullMethodPattern;
			readonly ParameterListPattern?     _parameters;
			readonly TypePattern?              _returnType;

			CompiledPatternMatcher(
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

			public static CompiledPatternMatcher Create(
				AccessibilityMask         accessibility,
				ModifierMask              modifiers,
				ImmutableArray<Condition> conditions,
				DottedPattern?            fullMethodPattern,
				ParameterListPattern?     parameters,
				TypePattern?              returnType)
			{
				return new CompiledPatternMatcher(accessibility, modifiers, conditions, fullMethodPattern, parameters, returnType);
			}

			public AccessibilityMask Accessibility => _accessibility;

			/// <summary>
			/// Adds the method names this rule can match when every match must have one of a known set of literal names.
			/// Returns <see langword="false"/> when the rule can match a method with any name.
			/// </summary>
			public bool TryGetLiteralMethodNames(HashSet<string> names)
			{
				if (_fullMethodPattern is not null)
					return TryAddLiteralName(_fullMethodPattern.Segments[^1], names);

				foreach (var condition in _conditions)
				{
					var conditionNames = new HashSet<string>(StringComparer.Ordinal);

					if (condition.TryGetLiteralMethodNames(conditionNames))
					{
						names.UnionWith(conditionNames);
						return true;
					}
				}

				return false;
			}

			/// <summary>
			/// Adds the first segment of the full method name (the root namespace, or the type for a type in the global namespace)
			/// when every match must start with one of a known set of literal segments.
			/// Returns <see langword="false"/> when the rule can match a method under any root.
			/// </summary>
			public bool TryGetLiteralRootSegments(HashSet<string> roots)
			{
				if (_fullMethodPattern is not null)
					return TryAddLiteralName(_fullMethodPattern.Segments[0], roots);

				foreach (var condition in _conditions)
				{
					var conditionRoots = new HashSet<string>(StringComparer.Ordinal);

					if (condition.TryGetLiteralRootSegments(conditionRoots))
					{
						roots.UnionWith(conditionRoots);
						return true;
					}
				}

				return false;
			}

			internal static bool TryAddLiteralName(SegmentMatcher segment, HashSet<string> names)
			{
				if (segment.ExactText is not {} text)
					return false;

				// A generic method or type segment is written as Name<...>; the call site and the symbol use the bare name.
				var genericStart = text.IndexOf('<');

				names.Add(genericStart >= 0 ? text[..genericStart] : text);
				return true;
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
		}

		sealed class Condition
		{
			readonly Condition[]?           _children;
			readonly ConditionOperator      _operator;
			readonly string                _key;
			readonly DottedPattern?        _dottedPattern;
			readonly SegmentMatcher?       _segmentPattern;
			readonly TypePattern?          _typePattern;
			readonly ParameterPattern?     _parameterPattern;
			readonly ParameterListPattern? _parameterListPattern;
			readonly TypePattern?          _attributePattern;
			readonly SegmentMatcher?       _signaturePattern;

			Condition(
				string                key,
				DottedPattern?        dottedPattern,
				SegmentMatcher?       segmentPattern,
				TypePattern?          typePattern,
				ParameterPattern?     parameterPattern,
				ParameterListPattern? parameterListPattern,
				TypePattern?          attributePattern,
				SegmentMatcher?       signaturePattern)
			{
				_children              = null;
				_operator              = ConditionOperator.None;
				_key                  = key;
				_dottedPattern        = dottedPattern;
				_segmentPattern       = segmentPattern;
				_typePattern          = typePattern;
				_parameterPattern     = parameterPattern;
				_parameterListPattern = parameterListPattern;
				_attributePattern     = attributePattern;
				_signaturePattern     = signaturePattern;
			}

			Condition(ConditionOperator @operator, Condition[] children)
			{
				_children              = children;
				_operator              = @operator;
				_key                   = "";
				_dottedPattern         = null;
				_segmentPattern        = null;
				_typePattern           = null;
				_parameterPattern      = null;
				_parameterListPattern  = null;
				_attributePattern      = null;
				_signaturePattern      = null;
			}

			public static Condition Leaf(
				string                key,
				DottedPattern?        dottedPattern,
				SegmentMatcher?       segmentPattern,
				TypePattern?          typePattern,
				ParameterPattern?     parameterPattern,
				ParameterListPattern? parameterListPattern,
				TypePattern?          attributePattern,
				SegmentMatcher?       signaturePattern)
			{
				return new Condition(key, dottedPattern, segmentPattern, typePattern, parameterPattern, parameterListPattern, attributePattern, signaturePattern);
			}

			/// <summary>Any of <paramref name="children"/> (<see cref="ConditionOperator.Any"/>) or all of them.</summary>
			public static Condition Combine(ConditionOperator @operator, Condition[] children)
			{
				return new Condition(@operator, children);
			}

			public bool TryGetLiteralMethodNames(HashSet<string> names)
			{
				return TryGetLiteralValues(names, static (condition, values) => condition._key switch
				{
					"method"     => CompiledPatternMatcher.TryAddLiteralName(condition._segmentPattern!, values),
					"fullmethod" => condition._dottedPattern!.Segments[^1].Kind != SegmentMatcherKind.Recursive &&
						CompiledPatternMatcher.TryAddLiteralName(condition._dottedPattern.Segments[^1], values),
					_            => false
				});
			}

			public bool TryGetLiteralRootSegments(HashSet<string> roots)
			{
				return TryGetLiteralValues(roots, static (condition, values) => condition._key switch
				{
					"namespace" or "fulltype" or "fullmethod" => CompiledPatternMatcher.TryAddLiteralName(condition._dottedPattern!.Segments[0], values),
					_                                          => false
				});
			}

			bool TryGetLiteralValues(HashSet<string> values, Func<Condition,HashSet<string>,bool> getLeafValues)
			{
				if (_children is null)
					return getLeafValues(this, values);

				if (_operator == ConditionOperator.Any)
				{
					// Every alternative must be restricted, otherwise one of them can match anything.
					var alternatives = new HashSet<string>(StringComparer.Ordinal);

					foreach (var child in _children)
						if (!child.TryGetLiteralValues(alternatives, getLeafValues))
							return false;

					values.UnionWith(alternatives);
					return true;
				}

				// All children must match, so one restricted child restricts the whole condition.
				foreach (var child in _children)
				{
					var childValues = new HashSet<string>(StringComparer.Ordinal);

					if (child.TryGetLiteralValues(childValues, getLeafValues))
					{
						values.UnionWith(childValues);
						return true;
					}
				}

				return false;
			}

			public bool IsMatch(in MethodTarget target)
			{
				if (_children is not null)
				{
					if (_operator == ConditionOperator.Any)
					{
						foreach (var child in _children)
							if (child.IsMatch(target))
								return true;

						return false;
					}

					foreach (var child in _children)
						if (!child.IsMatch(target))
							return false;

					return true;
				}

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
					"attributes" => target.Attributes.Any(attribute => _attributePattern!.IsMatch(attribute)),
					"signature"  => _signaturePattern!.IsMatch(target.Signature),
					_            => false
				};
			}
		}

		enum ConditionOperator
		{
			None,
			Any,
			All
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

			/// <summary>No parameters: <c>()</c>.</summary>
			public static ParameterListPattern Empty()
			{
				return new ParameterListPattern(false, [], -1);
			}

			/// <summary>Any parameters: <c>(...)</c>.</summary>
			public static ParameterListPattern Ignore()
			{
				return new ParameterListPattern(true, [], -1);
			}

			/// <summary>The parameters in order; the one at <paramref name="ellipsisIndex"/> (if not -1) is <c>...</c> and is null.</summary>
			public static ParameterListPattern Create(ImmutableArray<ParameterPattern?> parameters, int ellipsisIndex)
			{
				return new ParameterListPattern(false, parameters, ellipsisIndex);
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

			/// <summary><c>_</c>: any one parameter.</summary>
			public static ParameterPattern Any()
			{
				return new ParameterPattern(true, null, null);
			}

			public static ParameterPattern Create(ParameterModifier? modifier, TypePattern typePattern)
			{
				return new ParameterPattern(false, modifier, typePattern);
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

			public static TypePattern FromDotted(DottedPattern dottedPattern)
			{
				return new TypePattern(dottedPattern, null);
			}

			public static TypePattern FromSegment(SegmentMatcher segmentPattern)
			{
				return new TypePattern(null, segmentPattern);
			}

			/// <summary>The diagnostic of an empty type pattern.</summary>
			public static TargetFilterDiagnostic Empty(string text)
			{
				return TargetFilterDiagnostic.InvalidRule(text, "Type pattern cannot be empty.");
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

			/// <summary>
			/// Replaces each C# alias written as an identifier (<c>int</c>, <c>string</c>, …) with its full name and drops a
			/// <c>?</c> that follows an identifier, since the canonical signature omits nullable annotations. Escapes are not
			/// looked at: the text is read as identifiers and other characters.
			/// </summary>
			public static string ExpandTypeAliases(string text)
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

			/// <summary>Only a ** after another one can try a way again, so a pattern with one ** has nothing to remember.</summary>
			readonly bool _rememberWays;

			readonly int   _recursiveCount;
			readonly int[] _recursiveOrdinals;

			DottedPattern(ImmutableArray<SegmentMatcher> segments)
			{
				_segments     = segments;
				_recursiveCount    = segments.Count(static s => s.Kind == SegmentMatcherKind.Recursive);
				_rememberWays      = _recursiveCount > 1;
				_recursiveOrdinals = new int[segments.Length];

				for (int i = 0, ordinal = 0; i < segments.Length; i++)
					_recursiveOrdinals[i] = segments[i].Kind == SegmentMatcherKind.Recursive ? ordinal++ : -1;
			}

			public ImmutableArray<SegmentMatcher> Segments => _segments;

			public static DottedPattern FromSegments(ImmutableArray<SegmentMatcher> segments)
			{
				return new DottedPattern(segments);
			}

			/// <summary>
			/// Compiles the trimmed parts of the dotted pattern <paramref name="text"/>. <paramref name="pattern"/> is null when a
			/// part is empty; it holds the valid segments when another part is invalid.
			/// </summary>
			public static bool TryCreate(string text, IReadOnlyList<string> parts, List<TargetFilterDiagnostic> diagnostics, bool allowRecursiveFinalSegment, out DottedPattern pattern)
			{
				pattern = null!;

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

			/// <summary>
			/// Whether the segments match the values in order, a <c>**</c> standing for any number of values (none included)
			/// and a final <c>**</c> for whatever is left.
			/// </summary>
			/// <remarks>
			/// Each <c>**</c> tries the number of values it takes in turn, fewest first, and the first way that matches is the
			/// answer, so a segment is compared with a value only when a way being tried reaches that pair. A way that fails is
			/// remembered by where it starts (the <c>**</c> it is tried from and the value it starts at), and is not tried again
			/// when that <c>**</c> is reached once more from an earlier one: without that, k of them try a power of the name's
			/// length in ways. The comparisons keep the order an exhaustive search makes them in, less those of the ways
			/// already known to fail, and the answer is the search's. A way whose failure came from a regular expression that
			/// timed out is not remembered, since tried again it may match, as the search would try it.
			/// </remarks>
			public bool IsMatch(List<string> values)
			{
				var ways = default(Ways);

				return IsMatch(values, 0, 0, ref ways);
			}

			bool IsMatch(List<string> values, int patternIndex, int valueIndex, ref Ways ways)
			{
				while (patternIndex < _segments.Length)
				{
					var segment = _segments[patternIndex];

					if (segment.Kind == SegmentMatcherKind.Recursive)
					{
						if (patternIndex == _segments.Length - 1)
							return true;

						var row = _rememberWays ? _recursiveOrdinals[patternIndex] : -1;

						for (var i = valueIndex; i <= values.Count; i++)
						{
							if (row >= 0 && ways.HasFailed(row, i))
								continue;

							var timedOutBefore = ways.TimedOut;

							ways.TimedOut = false;

							if (IsMatch(values, patternIndex + 1, i, ref ways))
								return true;

							if (row >= 0 && !ways.TimedOut)
								ways.Fail(row, i, _recursiveCount, values.Count);

							ways.TimedOut |= timedOutBefore;
						}

						return false;
					}

					if (valueIndex >= values.Count || !segment.IsMatch(values[valueIndex], ref ways.TimedOut))
						return false;

					patternIndex++;
					valueIndex++;
				}

				return valueIndex == values.Count;
			}

			/// <summary>
			/// The ways known to fail, one row of value indexes per <c>**</c>, each allocated when that <c>**</c> first records
			/// a failure; and whether a comparison timed out in the way being tried.
			/// </summary>
			struct Ways
			{
				ulong[]?[]? _rows;

				public bool TimedOut;

				public readonly bool HasFailed(int row, int value)
				{
					return _rows?[row] is {} bits && (bits[value >> 6] & 1UL << (value & 63)) != 0;
				}

				public void Fail(int row, int value, int rowCount, int valueCount)
				{
					_rows ??= new ulong[]?[rowCount];

					// One bit for each of valueCount + 1 starts, counted in long so that no length overflows.
					var bits = _rows[row] ??= new ulong[(int)(((long)valueCount + 64) >> 6)];

					bits[value >> 6] |= 1UL << (value & 63);
				}
			}
		}

		/// <summary>
		/// For the tests: the segment comparison by regular expression of this thread that is to time out, counted from 1
		/// (0: none), and how many have been made since it was set. A real timeout cannot be made to happen when wanted.
		/// </summary>
		[ThreadStatic] internal static int ForcedRegexTimeoutAt;
		[ThreadStatic] internal static int RegexComparisons;

		static bool TimeoutForcedForTests()
		{
			return ForcedRegexTimeoutAt != 0 && ++RegexComparisons == ForcedRegexTimeoutAt;
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

			public string? ExactText => Kind == SegmentMatcherKind.Exact ? _text : null;

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
				var timedOut = false;

				return IsMatch(value, ref timedOut);
			}

			/// <summary>As <see cref="IsMatch(string)"/>; <paramref name="timedOut"/> is set when a regular expression timed out.</summary>
			public bool IsMatch(string value, ref bool timedOut)
			{
				return Kind switch
				{
					SegmentMatcherKind.Any       => true,
					SegmentMatcherKind.Exact     => string.Equals(value, _text, StringComparison.Ordinal),
					SegmentMatcherKind.Prefix    => value.StartsWith(_text!, StringComparison.Ordinal),
					SegmentMatcherKind.Suffix    => value.EndsWith(_text!, StringComparison.Ordinal),
					SegmentMatcherKind.Contains  => value.Contains(_text!, StringComparison.Ordinal),
					SegmentMatcherKind.Regex     => IsRegexMatch(value, ref timedOut),
					SegmentMatcherKind.Recursive => true,
					_                            => false
				};
			}

			bool IsRegexMatch(string value, ref bool timedOut)
			{
				try
				{
					if (TimeoutForcedForTests())
						throw new RegexMatchTimeoutException(value, _regex!.ToString(), _regex.MatchTimeout);

					return _regex!.IsMatch(value);
				}
				catch (RegexMatchTimeoutException)
				{
					timedOut = true;
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

			/// <summary>
			/// A pattern whose only wildcards are a <c>*</c> at its start, at its end, or both, is a suffix, prefix or contains
			/// test. Only unescaped wildcards count: <c>*a\*</c> ends with <c>a*</c>.
			/// </summary>
			static bool IsSimpleWildcard(string pattern, out SegmentMatcherKind kind, out string text)
			{
				kind = SegmentMatcherKind.Regex;
				text = "";

				var first   = -1;
				var last    = -1;
				var stars   = 0;
				var escaped = false;

				for (var i = 0; i < pattern.Length; i++)
				{
					var ch = pattern[i];

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

					if (ch == '?')
						return false;

					if (ch == '*')
					{
						if (first < 0)
							first = i;

						last = i;
						stars++;
					}
				}

				if (stars == 0 || stars > 2 || stars == 2 && !(first == 0 && last == pattern.Length - 1))
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
