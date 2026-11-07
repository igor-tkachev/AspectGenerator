using System;
using System.Collections.Generic;

using DotGram;

using Microsoft.CodeAnalysis.Text;

namespace AspectGenerator
{
	/// <summary>
	/// The parser of target filter lines. Its members are generated from <c>TargetFilter.gram</c> and committed under
	/// <c>Generated</c>; the build does not run the generator. What is written here is what the grammar calls: the values
	/// it builds on the way to the syntax of <c>TargetFilterSyntax.cs</c>, and the few tests that are C#'s rather than a
	/// grammar's.
	/// </summary>
	[Gram("TargetFilter.gram")]
	internal partial class TargetFilterParser
	{
		/// <summary>
		/// A reading nested deeper than the stack it runs on allows is not moved to a thread of its own: a generator runs on
		/// the compiler's threads. It fails instead, and the rule is reported as one that cannot be read.
		/// </summary>
		static partial void OnHandingOff(ref object? state)
		{
			throw new InsufficientExecutionStackException();
		}

		/// <summary>A matcher prefix that reads a native rule.</summary>
		static bool IsPatternMatcher(string name)
		{
			return string.Equals(name, "pattern", StringComparison.OrdinalIgnoreCase);
		}

		/// <summary>A matcher prefix whose text the grammar does not read.</summary>
		static bool IsTextMatcher(string name)
		{
			return string.Equals(name, "contains", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "regex", StringComparison.OrdinalIgnoreCase);
		}

		static TargetFilterMatcher TextMatcher(string name)
		{
			return string.Equals(name, "regex", StringComparison.OrdinalIgnoreCase) ? TargetFilterMatcher.Regex : TargetFilterMatcher.Contains;
		}

		internal static bool IsConditionKey(string name)
		{
			return AspectFilters.IsConditionKey(name);
		}

		/// <summary>
		/// Reads one operand of a condition value: up to the first <c>|</c> or <c>&amp;</c> outside any <c>&lt;…&gt;</c> and
		/// <c>(…)</c>, or to the end. The two kinds of bracket are counted apart, a closing bracket with nothing open is an
		/// ordinary character, and an escaped character counts for nothing. One pass, whatever the operand holds.
		/// </summary>
		static bool ReadOperand(ReadOnlySpan<char> input, ref int position)
		{
			var angles  = 0;
			var parens  = 0;
			var escaped = false;
			var i       = position;

			for (; i < input.Length; i++)
			{
				var ch = input[i];

				if (escaped)
				{
					escaped = false;
					continue;
				}

				if (ch is '|' or '&' && angles == 0 && parens == 0)
					break;

				switch (ch)
				{
					case '\\':                escaped = true; break;
					case '<':                 angles++;       break;
					case '>' when angles > 0: angles--;       break;
					case '(':                 parens++;       break;
					case ')' when parens > 0: parens--;       break;
				}
			}

			position = i;
			return true;
		}

		static TextSpan Span(SourceSpan span)
		{
			return new TextSpan(span.Start, span.Length);
		}

		static TargetFilterLineSyntax Line(OperatorSyntax? @operator, BodySyntax body)
		{
			return new TargetFilterLineSyntax(
				@operator?.Prefix ?? TargetFilterLinePrefix.None,
				@operator?.Span   ?? default,
				body.Matcher,
				body.MatcherSpan,
				body.Body);
		}

		static TextSpan[] Spans(TextSpan first, TextSpan[] more)
		{
			var result = new TextSpan[more.Length + 1];

			result[0] = first;
			Array.Copy(more, 0, result, 1, more.Length);

			return result;
		}

		static TextSpan[] Spans(ChunkSyntax? first, ChunkSyntax[]? more)
		{
			var result = new List<TextSpan>();

			if (first?.Word is {} word)
				result.Add(word);

			foreach (var chunk in more ?? [])
				if (chunk.Word is {} next)
					result.Add(next);

			return result.ToArray();
		}

		static ConditionAlternativeSyntax[] Alternatives(ConditionAlternativeSyntax first, ConditionAlternativeSyntax[] more)
		{
			var result = new List<ConditionAlternativeSyntax>(more.Length + 1) { first };

			result.AddRange(more);

			return result.ToArray();
		}

		static ParameterSyntax Parameter(ModifierSyntax? modifier, ParameterTypeSyntax type)
		{
			return new ParameterSyntax(
				type.IsThis ? ParameterSyntaxKind.This : ParameterSyntaxKind.Type,
				modifier?.Modifier,
				modifier?.Span ?? default,
				type.Span);
		}

		internal sealed class OperatorSyntax
		{
			public OperatorSyntax(TargetFilterLinePrefix prefix, TextSpan span)
			{
				Prefix = prefix;
				Span   = span;
			}

			public TargetFilterLinePrefix Prefix { get; }
			public TextSpan               Span   { get; }
		}

		internal sealed class BodySyntax
		{
			public BodySyntax(TargetFilterMatcher matcher, TextSpan matcherSpan, TargetFilterBodySyntax body)
			{
				Matcher     = matcher;
				MatcherSpan = matcherSpan;
				Body        = body;
			}

			public TargetFilterMatcher    Matcher     { get; }
			public TextSpan               MatcherSpan { get; }
			public TargetFilterBodySyntax Body        { get; }
		}

		internal sealed class NameSyntax
		{
			public NameSyntax(string text, TextSpan span)
			{
				Text = text;
				Span = span;
			}

			public string   Text { get; }
			public TextSpan Span { get; }
		}

		internal sealed class ChunkSyntax
		{
			public ChunkSyntax(TextSpan? word)
			{
				Word = word;
			}

			/// <summary>The word, or <see langword="null"/> where the chunk is only escapes.</summary>
			public TextSpan? Word { get; }
		}

		internal sealed class ModifierSyntax
		{
			public ModifierSyntax(AspectFilters.ParameterModifier modifier, TextSpan span)
			{
				Modifier = modifier;
				Span     = span;
			}

			public AspectFilters.ParameterModifier Modifier { get; }
			public TextSpan                        Span     { get; }
		}

		internal sealed class ParameterTypeSyntax
		{
			public ParameterTypeSyntax(bool isThis, TextSpan span)
			{
				IsThis = isThis;
				Span   = span;
			}

			public bool     IsThis { get; }
			public TextSpan Span   { get; }
		}
	}
}
