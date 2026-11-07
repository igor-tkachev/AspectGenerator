using Microsoft.CodeAnalysis.Text;

namespace AspectGenerator
{
	// The values the target filter grammar (TargetFilter.gram) builds. A line is read by `parse Line`; the pieces of a line
	// that the filter language reads again after trimming them (a condition item, a value, a dotted name, a parameter
	// list and its parameters, the words of a method pattern) are read by the publications of their own, over that piece.
	// Every span is a position in the text that was parsed; for a piece read over a slice of a line it is the position in
	// the line.

	/// <summary>The operator a line begins with.</summary>
	enum TargetFilterLinePrefix
	{
		None,
		Exclude,
		And,
		Or
	}

	/// <summary>How the body of a line is matched.</summary>
	enum TargetFilterMatcher
	{
		/// <summary>No matcher prefix: a native rule (a condition rule or a method pattern).</summary>
		Native,
		/// <summary><c>pattern:</c>, a native rule that is never grouped with the lines around it.</summary>
		Pattern,
		/// <summary><c>contains:</c>, an ordinal substring of the canonical signature.</summary>
		Contains,
		/// <summary><c>regex:</c>, a regular expression over the canonical signature.</summary>
		Regex
	}

	/// <summary>One line of a target filter.</summary>
	sealed class TargetFilterLineSyntax
	{
		public TargetFilterLineSyntax(TargetFilterLinePrefix prefix, TextSpan prefixSpan, TargetFilterMatcher matcher, TextSpan matcherSpan, TargetFilterBodySyntax body)
		{
			Prefix      = prefix;
			PrefixSpan  = prefixSpan;
			Matcher     = matcher;
			MatcherSpan = matcherSpan;
			Body        = body;
		}

		public TargetFilterLinePrefix Prefix      { get; }
		/// <summary>The operator character, or an empty span where the line has none.</summary>
		public TextSpan               PrefixSpan  { get; }
		public TargetFilterMatcher    Matcher     { get; }
		/// <summary>The matcher name and its colon, or an empty span for a native rule.</summary>
		public TextSpan               MatcherSpan { get; }
		public TargetFilterBodySyntax Body        { get; }
	}

	/// <summary>What follows the operator and the matcher prefix.</summary>
	abstract class TargetFilterBodySyntax
	{
		protected TargetFilterBodySyntax(TextSpan span)
		{
			Span = span;
		}

		public TextSpan Span { get; }
	}

	/// <summary>A line with nothing after its operator or matcher prefix.</summary>
	sealed class EmptyBodySyntax : TargetFilterBodySyntax
	{
		public EmptyBodySyntax(TextSpan span)
			: base(span)
		{
		}
	}

	/// <summary>The text of a <c>contains:</c> or <c>regex:</c> rule, which the grammar does not read.</summary>
	sealed class TextBodySyntax : TargetFilterBodySyntax
	{
		public TextBodySyntax(TextSpan span)
			: base(span)
		{
		}
	}

	/// <summary>A native line that begins with a word and a colon that is not a condition key.</summary>
	sealed class UnknownKeyBodySyntax : TargetFilterBodySyntax
	{
		public UnknownKeyBodySyntax(TextSpan span, TextSpan key)
			: base(span)
		{
			Key = key;
		}

		public TextSpan Key { get; }
	}

	/// <summary>
	/// A condition rule: items separated by top-level semicolons. An item is the span between two of them, as written;
	/// each is read again, trimmed, by <c>parse ConditionItem</c>.
	/// </summary>
	sealed class ConditionLineSyntax : TargetFilterBodySyntax
	{
		public ConditionLineSyntax(TextSpan span, TextSpan[] items)
			: base(span)
		{
			Items = items;
		}

		public TextSpan[] Items { get; }
	}

	/// <summary>
	/// A compact method pattern: the modifiers and the dotted name (read again, trimmed, by <c>parse Words</c>), an optional
	/// parameter list and an optional return type pattern after the first top-level colon.
	/// </summary>
	sealed class MethodPatternSyntax : TargetFilterBodySyntax
	{
		public MethodPatternSyntax(TextSpan span, TextSpan name, ParameterGroupSyntax? parameters, TextSpan? returns)
			: base(span)
		{
			Name       = name;
			Parameters = parameters;
			Returns    = returns;
		}

		/// <summary>The text before the parameter list (or before the colon), as written.</summary>
		public TextSpan              Name       { get; }
		public ParameterGroupSyntax? Parameters { get; }
		/// <summary>The text after the first top-level colon, as written.</summary>
		public TextSpan?             Returns    { get; }
	}

	/// <summary>The parenthesized parameter list of a method pattern.</summary>
	sealed class ParameterGroupSyntax
	{
		public ParameterGroupSyntax(TextSpan span, TextSpan inner, bool endsThePattern)
		{
			Span           = span;
			Inner          = inner;
			EndsThePattern = endsThePattern;
		}

		/// <summary>From the opening parenthesis to the end of the name part.</summary>
		public TextSpan Span           { get; }
		/// <summary>The text between the parentheses, as written; empty when the list does not end the pattern.</summary>
		public TextSpan Inner          { get; }
		/// <summary>The list is closed and nothing but white space follows it before the return type.</summary>
		public bool     EndsThePattern { get; }
	}

	/// <summary>One item of a condition rule, read over the trimmed item.</summary>
	abstract class ConditionItemSyntax
	{
		protected ConditionItemSyntax(TextSpan span)
		{
			Span = span;
		}

		public TextSpan Span { get; }
	}

	/// <summary><c>key: value</c>. The value is read again by <c>parse ConditionValue</c>.</summary>
	sealed class KeyItemSyntax : ConditionItemSyntax
	{
		public KeyItemSyntax(TextSpan span, TextSpan key, TextSpan value)
			: base(span)
		{
			Key   = key;
			Value = value;
		}

		public TextSpan Key   { get; }
		public TextSpan Value { get; }
	}

	/// <summary>An item of words, which must all be accessibility or modifier words.</summary>
	sealed class WordsItemSyntax : ConditionItemSyntax
	{
		public WordsItemSyntax(TextSpan span, WordsSyntax words)
			: base(span)
		{
			Words = words;
		}

		public WordsSyntax Words { get; }
	}

	/// <summary>Words separated by white space; white space inside <c>&lt;…&gt;</c>, an array rank or an escape does not separate.</summary>
	sealed class WordsSyntax
	{
		public WordsSyntax(TextSpan[] words)
		{
			Words = words;
		}

		public TextSpan[] Words { get; }
	}

	/// <summary>A condition value: alternatives separated by top-level <c>|</c>.</summary>
	sealed class ConditionValueSyntax
	{
		public ConditionValueSyntax(ConditionAlternativeSyntax[] alternatives)
		{
			Alternatives = alternatives;
		}

		public ConditionAlternativeSyntax[] Alternatives { get; }
	}

	/// <summary>One alternative of a condition value: operands separated by top-level <c>&amp;</c>, each as written.</summary>
	sealed class ConditionAlternativeSyntax
	{
		public ConditionAlternativeSyntax(TextSpan span, TextSpan[] operands)
		{
			Span     = span;
			Operands = operands;
		}

		public TextSpan   Span     { get; }
		public TextSpan[] Operands { get; }
	}

	/// <summary>A dotted pattern: parts separated by top-level dots, each as written.</summary>
	sealed class DottedSyntax
	{
		public DottedSyntax(TextSpan[] parts)
		{
			Parts = parts;
		}

		public TextSpan[] Parts { get; }
	}

	/// <summary>A parameter list pattern, with or without the parentheses around it.</summary>
	sealed class ParameterListSyntax
	{
		public ParameterListSyntax(bool parenthesized, TextSpan inner)
		{
			Parenthesized = parenthesized;
			Inner         = inner;
		}

		public bool     Parenthesized { get; }
		/// <summary>The text inside the parentheses (or the whole text), as written.</summary>
		public TextSpan Inner         { get; }
	}

	/// <summary>The parameters of a parameter list pattern, separated by top-level commas, each as written.</summary>
	sealed class ParameterPartsSyntax
	{
		public ParameterPartsSyntax(TextSpan[] parts)
		{
			Parts = parts;
		}

		public TextSpan[] Parts { get; }
	}

	enum ParameterSyntaxKind
	{
		/// <summary><c>_</c>, any one parameter.</summary>
		Any,
		/// <summary>An optional modifier and a type pattern.</summary>
		Type,
		/// <summary><c>this T</c>, which parameter patterns do not support.</summary>
		This
	}

	/// <summary>One parameter pattern, read over the trimmed parameter.</summary>
	sealed class ParameterSyntax
	{
		public ParameterSyntax(ParameterSyntaxKind kind, AspectFilters.ParameterModifier? modifier, TextSpan modifierSpan, TextSpan type)
		{
			Kind         = kind;
			Modifier     = modifier;
			ModifierSpan = modifierSpan;
			Type         = type;
		}

		public ParameterSyntaxKind               Kind         { get; }
		public AspectFilters.ParameterModifier?  Modifier     { get; }
		/// <summary>The modifier words, or an empty span.</summary>
		public TextSpan                          ModifierSpan { get; }
		/// <summary>The type pattern (for <see cref="ParameterSyntaxKind.This"/>, the text from <c>this</c> on).</summary>
		public TextSpan                          Type         { get; }
	}
}
