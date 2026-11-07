using System.Collections.Generic;
using System.Globalization;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AspectGenerator
{
	/// <summary>
	/// Finds where the characters of a target filter stand in the source it was written in, so that a filter diagnostic
	/// points at the characters it is about rather than at the whole attribute.
	/// </summary>
	/// <remarks>
	/// A filter is a constant string: a string literal (regular, verbatim or raw, single- or multi-line), an interpolated
	/// string of constants, a concatenation, a parenthesized or cast constant, or a reference to a <c>const</c> declared in
	/// the compilation, made of any of these. Each character of the value is traced to the source characters that make it
	/// (an escape sequence, a doubled quote or brace, a character after the indentation a multi-line raw literal strips).
	/// The trace is trusted only when the characters it decodes are the filter, character for character; anything else
	/// (a <c>const</c> of a referenced assembly, <c>nameof</c>, a form not listed here) gives no location, and the caller
	/// falls back to the attribute.
	/// </remarks>
	sealed class TargetFilterSourceMap
	{
		// How many const references and nested expressions are followed before giving up; a constant cannot refer to
		// itself, so this only bounds the work.
		const int MaxDepth = 64;

		// How many expressions one trace may visit and characters it may write, on top of the filter's own length. Each
		// const is traced once and copied where it is used again, so a trace is linear in the source it reads; the budget
		// stops one that reads too much source for too short a filter, which then points at the attribute.
		const int MaxWork = 100_000;

		readonly SyntaxTree[] _trees;
		readonly TextSpan[]   _spans;
		readonly int[]        _units;

		TargetFilterSourceMap(SyntaxTree[] trees, TextSpan[] spans, int[] units)
		{
			_trees = trees;
			_spans = spans;
			_units = units;
		}

		/// <summary>
		/// The map of <paramref name="expression"/>, whose constant value is <paramref name="value"/>, or <see langword="null"/>
		/// where its characters cannot all be traced exactly.
		/// </summary>
		public static TargetFilterSourceMap? Create(ExpressionSyntax expression, Compilation compilation, string value)
		{
			return Create(expression, compilation, value, MaxWork);
		}

		/// <summary>The map, with a budget of <paramref name="maxWork"/> expressions and characters on top of the value's length.</summary>
		internal static TargetFilterSourceMap? Create(ExpressionSyntax expression, Compilation compilation, string value, int maxWork)
		{
			var builder = new Builder(compilation, value.Length, maxWork);

			if (!builder.Append(expression, 0) || !builder.WithinBudget || !string.Equals(new string(builder.Chars.ToArray()), value, System.StringComparison.Ordinal))
				return null;

			return new TargetFilterSourceMap(builder.Trees.ToArray(), builder.Spans.ToArray(), builder.Units.ToArray());
		}

		/// <summary>
		/// Where characters <paramref name="span"/> of the value stand, or <see langword="null"/> where they do not stand
		/// together: in two files, or out of order (a <c>const</c> declared above the text that uses it).
		/// </summary>
		public Location? Locate(TextSpan span)
		{
			if (span.End > _spans.Length || _spans.Length == 0)
				return null;

			if (span.IsEmpty)
			{
				var at = span.Start < _spans.Length ? _spans[span.Start].Start : _spans[_spans.Length - 1].End;
				var on = _trees[span.Start < _spans.Length ? span.Start : _spans.Length - 1];

				return Location.Create(on, new TextSpan(at, 0));
			}

			var tree = _trees[span.Start];

			// Every character comes after the one before it in the source, except the two halves of a surrogate pair one
			// escape makes, which share the escape. The same source text used twice (a const referenced twice) is not in
			// order, and points nowhere.
			for (var i = span.Start + 1; i < span.End; i++)
				if (_trees[i] != tree || _units[i] != _units[i - 1] && _spans[i].Start < _spans[i - 1].End)
					return null;

			return Location.Create(tree, TextSpan.FromBounds(_spans[span.Start].Start, _spans[span.End - 1].End));
		}

		sealed class Builder(Compilation compilation, int length, int maxWork)
		{
			// What a const traced before wrote: where in the trace, or -1 where it could not be traced.
			readonly Dictionary<ISymbol,(int Start, int Length)> _constants = new(SymbolEqualityComparer.Default);

			int _work;
			int _unit;

			// Lists, indexed directly: a copy reads what the trace wrote, character by character.
			public List<char>       Chars { get; } = new();
			public List<SyntaxTree> Trees { get; } = new();
			public List<TextSpan>   Spans { get; } = new();

			/// <summary>The source unit (a character, an escape sequence) each character was made from: equal only for the two halves of one escape.</summary>
			public List<int>        Units { get; } = new();

			/// <summary>The trace has done no more work and written no more characters than it may.</summary>
			public bool WithinBudget => _work <= maxWork + length && Chars.Count <= length;

			public bool Append(ExpressionSyntax expression, int depth)
			{
				if (depth > MaxDepth)
					return false;

				_work++;

				// Checked before and after: a literal writes all its characters before it returns.
				return WithinBudget && AppendExpression(expression, depth) && WithinBudget;
			}

			bool AppendExpression(ExpressionSyntax expression, int depth)
			{
				switch (expression)
				{
					case ParenthesizedExpressionSyntax parenthesized:
						return Append(parenthesized.Expression, depth + 1);

					case CastExpressionSyntax cast:
						return Append(cast.Expression, depth + 1);

					case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
						return AppendLiteral(literal.Token);

					case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
						return Append(binary.Left, depth + 1) && Append(binary.Right, depth + 1);

					case InterpolatedStringExpressionSyntax interpolated:
						return AppendInterpolated(interpolated, depth);

					case IdentifierNameSyntax or MemberAccessExpressionSyntax:
						return AppendConstant(expression, depth);

					default:
						return false;
				}
			}

			/// <summary>A reference to a <c>const</c> field or local declared in the compilation: its initializer.</summary>
			bool AppendConstant(ExpressionSyntax expression, int depth)
			{
				if (!compilation.ContainsSyntaxTree(expression.SyntaxTree))
					return false;

				var symbol = compilation.GetSemanticModel(expression.SyntaxTree).GetSymbolInfo(expression).Symbol;

				if (symbol is not (IFieldSymbol { IsConst: true } or ILocalSymbol { IsConst: true }))
					return false;

				// A const traced before is copied, so that one used many times (each the sum of two others) costs its
				// length, not the size of the tree of references below it.
				if (_constants.TryGetValue(symbol, out var traced))
					return traced.Start >= 0 && Copy(traced.Start, traced.Length);

				// A const field declared once: one declarator, with its initializer.
				if (symbol.DeclaringSyntaxReferences is not [var reference] ||
					reference.GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: var initializer } ||
					!compilation.ContainsSyntaxTree(initializer.SyntaxTree))
				{
					_constants[symbol] = (-1, 0);
					return false;
				}

				var start = Chars.Count;
				var ok    = Append(initializer, depth + 1);

				_constants[symbol] = ok ? (start, Chars.Count - start) : (-1, 0);
				return ok;
			}

			/// <summary>Writes again what the trace wrote at <paramref name="start"/>, as source units of their own.</summary>
			bool Copy(int start, int count)
			{
				_work += count;

				if (!WithinBudget || Chars.Count + count > length)
					return false;

				var units = new Dictionary<int,int>();

				for (var i = start; i < start + count; i++)
				{
					if (!units.TryGetValue(Units[i], out var unit))
						units[Units[i]] = unit = ++_unit;

					Chars.Add(Chars[i]);
					Trees.Add(Trees[i]);
					Spans.Add(Spans[i]);
					Units.Add(unit);
				}

				return true;
			}

			bool AppendLiteral(SyntaxToken token)
			{
				var text  = token.Text;
				var start = token.SpanStart;

				switch (token.Kind())
				{
					case SyntaxKind.StringLiteralToken:
						return text.StartsWith("@", System.StringComparison.Ordinal)
							? AppendVerbatim(token.SyntaxTree!, text, start, 2, text.Length - 1, braces: false)
							: AppendRegular(token.SyntaxTree!, text, start, 1, text.Length - 1, braces: false);

					case SyntaxKind.SingleLineRawStringLiteralToken:
					{
						var quotes = CountQuotes(text, 0);

						return quotes * 2 <= text.Length && AppendAsWritten(token.SyntaxTree!, text, start, quotes, text.Length - quotes, 0, atLineStart: false);
					}

					case SyntaxKind.MultiLineRawStringLiteralToken:
					{
						var quotes = CountQuotes(text, 0);

						// The content runs from the line after the opening quotes to the line break before the closing line,
						// whose white space is the indentation every content line loses.
						var first = AfterLineBreak(text, quotes);
						var close = text.Length - quotes;

						if (first < 0 || close < first)
							return false;

						var last  = close;

						while (last > first && IsIndentation(text[last - 1]))
							last--;

						var indentation = close - last;
						var end         = BeforeLineBreak(text, last);

						return end >= first && AppendAsWritten(token.SyntaxTree!, text, start, first, end, indentation, atLineStart: true);
					}

					default:
						return false;
				}
			}

			bool AppendInterpolated(InterpolatedStringExpressionSyntax interpolated, int depth)
			{
				var kind        = interpolated.StringStartToken.Kind();
				var indentation = 0;

				if (kind == SyntaxKind.InterpolatedMultiLineRawStringStartToken)
				{
					// The end token holds the line break before the closing line and that line's white space.
					var endText = interpolated.StringEndToken.Text;
					var quotes  = endText.Length - endText.TrimEnd('"').Length;
					var last    = endText.Length - quotes;
					var white   = last;

					while (white > 0 && IsIndentation(endText[white - 1]))
						white--;

					indentation = last - white;
				}

				var atLineStart = kind == SyntaxKind.InterpolatedMultiLineRawStringStartToken;

				foreach (var content in interpolated.Contents)
				{
					switch (content)
					{
						case InterpolatedStringTextSyntax part:
						{
							var token = part.TextToken;
							var text  = token.Text;
							var tree  = token.SyntaxTree!;
							var ok    = kind switch
							{
								SyntaxKind.InterpolatedStringStartToken                => AppendRegular(tree, text, token.SpanStart, 0, text.Length, braces: true),
								SyntaxKind.InterpolatedVerbatimStringStartToken        => AppendVerbatim(tree, text, token.SpanStart, 0, text.Length, braces: true),
								SyntaxKind.InterpolatedSingleLineRawStringStartToken   => AppendAsWritten(tree, text, token.SpanStart, 0, text.Length, 0, atLineStart: false),
								SyntaxKind.InterpolatedMultiLineRawStringStartToken    => AppendAsWritten(tree, text, token.SpanStart, 0, text.Length, indentation, atLineStart),
								_                                                       => false
							};

							if (!ok || !WithinBudget)
								return false;

							atLineStart = text.Length > 0 && IsLineBreak(text[text.Length - 1]);
							break;
						}

						case InterpolationSyntax { AlignmentClause: null, FormatClause: null } hole:
							if (!Append(hole.Expression, depth + 1))
								return false;

							atLineStart = false;
							break;

						default:
							return false;
					}
				}

				return true;
			}

			/// <summary>Characters as written, except the indentation at the start of each line of a multi-line raw literal.</summary>
			bool AppendAsWritten(SyntaxTree tree, string text, int offset, int start, int end, int indentation, bool atLineStart)
			{
				var i = start;

				while (i < end)
				{
					if (atLineStart)
					{
						// A line loses the closing line's indentation; a line of white space shorter than it becomes empty.
						for (var skipped = 0; skipped < indentation && i < end && IsIndentation(text[i]); skipped++)
							i++;

						atLineStart = false;
						continue;
					}

					var ch = text[i];

					if (ch == '\r' && i + 1 < end && text[i + 1] == '\n')
					{
						Add(tree, offset + i, offset + i + 1, '\r');
						Add(tree, offset + i + 1, offset + i + 2, '\n');
						i += 2;
						atLineStart = true;
						continue;
					}

					Add(tree, offset + i, offset + i + 1, ch);
					atLineStart = IsLineBreak(ch);
					i++;
				}

				return true;
			}

			/// <summary>A verbatim literal's characters: a doubled quote is one quote, and in an interpolated one a doubled brace one brace.</summary>
			bool AppendVerbatim(SyntaxTree tree, string text, int offset, int start, int end, bool braces)
			{
				for (var i = start; i < end; i++)
				{
					var ch = text[i];

					if ((ch == '"' || braces && ch is '{' or '}') && i + 1 < end && text[i + 1] == ch)
					{
						Add(tree, offset + i, offset + i + 2, ch);
						i++;
						continue;
					}

					Add(tree, offset + i, offset + i + 1, ch);
				}

				return true;
			}

			/// <summary>A regular literal's characters: its escape sequences, and in an interpolated one a doubled brace as one brace.</summary>
			bool AppendRegular(SyntaxTree tree, string text, int offset, int start, int end, bool braces)
			{
				var i = start;

				while (i < end)
				{
					var ch = text[i];

					if (braces && ch is '{' or '}' && i + 1 < end && text[i + 1] == ch)
					{
						Add(tree, offset + i, offset + i + 2, ch);
						i += 2;
						continue;
					}

					if (ch != '\\')
					{
						Add(tree, offset + i, offset + i + 1, ch);
						i++;
						continue;
					}

					if (i + 1 >= end)
						return false;

					var escape = text[i + 1];
					var simple = escape switch
					{
						'\'' => '\'',
						'"'  => '"',
						'\\' => '\\',
						'0'  => '\0',
						'a'  => '\a',
						'b'  => '\b',
						'e'  => '\u001b',
						'f'  => '\f',
						'n'  => '\n',
						'r'  => '\r',
						't'  => '\t',
						'v'  => '\v',
						_    => (char?)null
					};

					if (simple is {} value)
					{
						Add(tree, offset + i, offset + i + 2, value);
						i += 2;
						continue;
					}

					// \xH to \xHHHH, \uHHHH, \UHHHHHHHH: the code they name, one or two UTF-16 characters.
					var digits = escape switch
					{
						'x' => CountHexDigits(text, i + 2, end, 4),
						'u' => 4,
						'U' => 8,
						_   => 0
					};

					if (digits == 0 || i + 2 + digits > end || CountHexDigits(text, i + 2, end, digits) != digits)
						return false;

					var code   = int.Parse(text.Substring(i + 2, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
					var length = 2 + digits;

					if (code > 0x10FFFF)
						return false;

					// A code above U+FFFF is a surrogate pair; one up to it is a single unit, a lone surrogate included.
					if (code > 0xFFFF)
					{
						var pair = char.ConvertFromUtf32(code);

						Add(tree, offset + i, offset + i + length, pair[0]);
						Add(tree, offset + i, offset + i + length, pair[1], sameUnit: true);
					}
					else
						Add(tree, offset + i, offset + i + length, (char)code);

					i += length;
				}

				return true;
			}

			void Add(SyntaxTree tree, int start, int end, char ch, bool sameUnit = false)
			{
				// A literal is read to its end; Append stops the trace at the next expression once it is over budget.
				_work++;

				if (!sameUnit)
					_unit++;

				Chars.Add(ch);
				Trees.Add(tree);
				Spans.Add(TextSpan.FromBounds(start, end));
				Units.Add(_unit);
			}
		}

		static int CountQuotes(string text, int start)
		{
			var i = start;

			while (i < text.Length && text[i] == '"')
				i++;

			return i - start;
		}

		static int CountHexDigits(string text, int start, int end, int max)
		{
			var count = 0;

			while (count < max && start + count < end && System.Uri.IsHexDigit(text[start + count]))
				count++;

			return count;
		}

		/// <summary>The position after the first line break at or after <paramref name="start"/>, or -1.</summary>
		static int AfterLineBreak(string text, int start)
		{
			for (var i = start; i < text.Length; i++)
				if (IsLineBreak(text[i]))
					return text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? i + 2 : i + 1;

			return -1;
		}

		/// <summary>The position of the line break that ends right before <paramref name="end"/>, or -1.</summary>
		static int BeforeLineBreak(string text, int end)
		{
			if (end <= 0 || !IsLineBreak(text[end - 1]))
				return -1;

			return text[end - 1] == '\n' && end >= 2 && text[end - 2] == '\r' ? end - 2 : end - 1;
		}

		static bool IsLineBreak(char ch)
		{
			return ch is '\r' or '\n' or '\u0085' or '\u2028' or '\u2029';
		}

		static bool IsIndentation(char ch)
		{
			return !IsLineBreak(ch) && char.IsWhiteSpace(ch);
		}
	}
}
