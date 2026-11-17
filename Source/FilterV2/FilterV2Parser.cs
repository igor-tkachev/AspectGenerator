using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

using DotGram;

using Microsoft.CodeAnalysis.Text;

namespace AspectGenerator.FilterV2
{
	/// <summary>
	/// The parser of target filters, version 2. Its members are generated from <c>FilterV2.gram</c> and committed under
	/// <c>Source/TargetFilter/Generated</c>; the build does not run the generator. What is written here is what the
	/// grammar calls: the nodes it builds, and the messages of the forms it refuses.
	/// </summary>
	[Gram("FilterV2.gram")]
	internal partial class FilterV2Parser
	{
		/// <summary>
		/// A reading nested deeper than the stack it runs on allows is not moved to a thread of its own: a generator runs on
		/// the compiler's threads. It fails instead, and the item is reported as nested too deeply.
		/// </summary>
		static partial void OnHandingOff(ref object? state)
		{
			throw new InsufficientExecutionStackException();
		}

		static TextSpan Span(SourceSpan span)
		{
			return new TextSpan((int)span.Start, (int)span.Length);
		}

		/// <summary>The span of a token that begins where a rule does and is <paramref name="text"/> long: trivia after it is not its.</summary>
		static TextSpan Span(SourceSpan span, string text)
		{
			return new TextSpan((int)span.Start, text.Length);
		}

		/// <summary>An identifier with no uppercase or titlecase letter.</summary>
		static bool IsLowercase(string identifier)
		{
			foreach (var ch in identifier)
				if (char.IsUpper(ch) || char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.TitlecaseLetter)
					return false;

			return true;
		}

		/// <summary>A key of the language, or of 1.1, written in another case: <c>Method</c>, <c>NAMESPACE</c>, <c>Contains</c>.</summary>
		static bool IsKeyInAnotherCase(string identifier)
		{
			if (IsLowercase(identifier))
				return false;

			var lower = identifier.ToLowerInvariant();

			return FilterV2Language.IsKey(lower) || lower is "contains" or "regex" or "pattern" or "fulltype" or "fullmethod" or "path" or "attributes" or "signature" or "within" or "assembly";
		}

		static ImmutableArray<T> Array<T>(T[]? items)
		{
			return items is null ? ImmutableArray<T>.Empty : ImmutableArray.Create(items);
		}

		static ImmutableArray<T> List<T>(T first, T[]? more)
		{
			var builder = ImmutableArray.CreateBuilder<T>(1 + (more?.Length ?? 0));

			builder.Add(first);

			if (more is not null)
				builder.AddRange(more);

			return builder.MoveToImmutable();
		}

		static FilterSyntaxError Error(FilterSyntaxErrorKind kind, SourceSpan span, string message)
		{
			return new FilterSyntaxError(kind, Span(span), message);
		}

		// ---- an item and its lines -------------------------------------------------------------------------------

		static FilterItemSyntax Item(SourceSpan span, FilterSyntaxError? leadingOperator, FilterLineSyntax[] lines)
		{
			return new FilterItemSyntax(Span(span), leadingOperator, Array(lines));
		}

		/// <summary>
		/// A line the grammar could not read, stepped over to its logical end. The line stands from where it began to the
		/// end of the line of text where reading stopped, and the error from where reading stopped to that end.
		/// </summary>
		static FilterLineSyntax BadLine(SourceSpan span, string parserInput, string parserExpected, long parserFailurePosition)
		{
			var start   = (int)span.Start;
			var failure = (int)Math.Max(start, Math.Min(parserFailurePosition, parserInput.Length));
			var end     = failure;

			while (end < parserInput.Length && parserInput[end] is not ('\r' or '\n'))
				end++;

			var error = new FilterSyntaxError(
				FilterSyntaxErrorKind.Syntax,
				TextSpan.FromBounds(failure, end),
				failure == end ? $"Syntax error at the end of the line: expected {FilterV2Expected.Describe(parserExpected)}." : $"Syntax error: expected {FilterV2Expected.Describe(parserExpected)}.");

			return new FilterLineSyntax(TextSpan.FromBounds(start, end), null, new ErrorExpressionSyntax(error));
		}

		static FilterSyntaxError LeadingOperator(SourceSpan span)
		{
			return Error(FilterSyntaxErrorKind.LeadingOperator, span,
				"An item cannot begin with 'and' or 'or': a line continues only within its own item, so write the whole line in one item.");
		}

		static FilterLineSyntax Line(SourceSpan span, TextSpan? minus, FilterExpressionSyntax expression)
		{
			return new FilterLineSyntax(Span(span), minus, expression);
		}

		static FilterExpressionSyntax LegacyLine(SourceSpan span, string op)
		{
			var word = op == "&" ? "and" : "or";

			return new ErrorExpressionSyntax(Error(FilterSyntaxErrorKind.Legacy, span,
				$"'{op}' before a line is version 1.1 syntax: continue the line before with '{word}' (a line that begins with '{word}' continues the one above it)."));
		}

		// ---- expressions -----------------------------------------------------------------------------------------

		static FilterExpressionSyntax Or(SourceSpan span, FilterExpressionSyntax first, FilterExpressionSyntax[]? more)
		{
			return more is null || more.Length == 0 ? first : new OrExpressionSyntax(Span(span), List(first, more));
		}

		static FilterExpressionSyntax And(SourceSpan span, FilterExpressionSyntax first, OperatorToken[]? operators, FilterExpressionSyntax[]? more)
		{
			if (more is null || more.Length == 0)
				return first;

			var errors = ImmutableArray.CreateBuilder<FilterSyntaxError>();

			foreach (var op in operators ?? [])
				if (op.Error is not null)
					errors.Add(op.Error);

			return new AndExpressionSyntax(Span(span), List(first, more), errors.ToImmutable());
		}

		static OperatorToken LegacyOperator(SourceSpan span, string op)
		{
			var instead = op == "|" ? "'or' (or alternatives in one pattern, '{Get,Find}*')" : "'and'";

			return new OperatorToken(Error(FilterSyntaxErrorKind.Legacy, span, $"'{op}' between conditions is version 1.1 syntax: write {instead}."));
		}

		static FilterExpressionSyntax NotBeforeGroup(SourceSpan span, FilterExpressionSyntax operand)
		{
			return new ErrorExpressionSyntax(
				Error(FilterSyntaxErrorKind.NotBeforeGroup, span,
					"'not' stands before a single condition only: write 'not a and not b' for 'not (a or b)', and 'not a or not b' for 'not (a and b)'."),
				ImmutableArray.Create<FilterSyntaxNode>(operand));
		}

		static FilterExpressionSyntax Not(SourceSpan span, FilterExpressionSyntax operand)
		{
			return new NotExpressionSyntax(Span(span), operand);
		}

		static FilterExpressionSyntax Group(SourceSpan span, FilterExpressionSyntax expression)
		{
			return new GroupExpressionSyntax(Span(span), expression);
		}

		static FilterExpressionSyntax BareAny(SourceSpan span)
		{
			return new ErrorExpressionSyntax(Error(FilterSyntaxErrorKind.InvalidAny, span,
				"'any' is a whole value of a key ('method: any'), never a condition of its own or a part of one."));
		}

		// ---- conditions ------------------------------------------------------------------------------------------

		static KeyToken Key(string text, SourceSpan span)
		{
			return new KeyToken(FilterV2Language.Key(text), text, Span(span));
		}

		static FilterExpressionSyntax Condition(SourceSpan span, KeyToken key, FilterValueSyntax value)
		{
			return new KeyConditionSyntax(Span(span), key.Key, key.Span, value);
		}

		static FilterExpressionSyntax WrongValue(SourceSpan span, KeyToken key)
		{
			var message = key.Key == FilterKey.Params
				? "'params:' takes a parameter list ('(int, ..)'); a condition on one parameter is written 'param: …'."
				: $"'{key.Text}:' takes only 'any' or a reference ('assembly.{key.Text}', 'aspect.{key.Text}'); a condition on it is written with its words.";

			return new ErrorExpressionSyntax(Error(FilterSyntaxErrorKind.WrongValue, span, message));
		}

		static FilterExpressionSyntax LegacyKey(SourceSpan span, NameToken key, string rest)
		{
			var value   = rest.Trim();
			var instead = key.Text switch
			{
				"contains" or "regex" => "it was removed; name the target with 'namespace:', 'type:' and 'method:' patterns",
				"pattern"             => $"write the compact signature without it: '{value}'",
				"signature"           => $"write the compact signature without it: '{value}'",
				"fulltype" or "path"  => $"write 'type: global::{value}'",
				"fullmethod"          => $"write 'method: global::{value}'",
				"attributes"          => $"write 'attribute: {value}'",
				_                     => "it was removed",
			};

			return new ErrorExpressionSyntax(new FilterSyntaxError(FilterSyntaxErrorKind.Legacy, Trimmed(span, rest), $"'{key.Text}:' is version 1.1 syntax: {instead}."));
		}

		static FilterExpressionSyntax CasedKey(SourceSpan span, NameToken key, string rest)
		{
			var lower = key.Text.ToLowerInvariant();

			return new ErrorExpressionSyntax(new FilterSyntaxError(FilterSyntaxErrorKind.Legacy, key.Span,
				$"Keys are lowercase: write '{lower}:'. A compact signature of a method named {key.Text} is written '@{key.Text}: …' or '{key.Text}(): …'."));
		}

		static FilterExpressionSyntax ReservedKey(SourceSpan span, NameToken key, string rest)
		{
			return new ErrorExpressionSyntax(new FilterSyntaxError(FilterSyntaxErrorKind.Reserved, Trimmed(span, rest), $"'{key.Text}:' is reserved for a later version."));
		}

		/// <summary>The span of a form that ends with <paramref name="rest"/>, without the white space it ends with.</summary>
		static TextSpan Trimmed(SourceSpan span, string rest)
		{
			var whole = Span(span);

			return new TextSpan(whole.Start, whole.Length - (rest.Length - rest.TrimEnd().Length));
		}

		static FilterExpressionSyntax UnknownKey(SourceSpan span, NameToken key)
		{
			return new ErrorExpressionSyntax(new FilterSyntaxError(FilterSyntaxErrorKind.UnknownKey, key.Span,
				$"Unknown key '{key.Text}:'. A compact signature of a method whose name is lowercase is written '@{key.Text}: …' or '{key.Text}(): …'."));
		}

		static ReferenceSyntax Reference(SourceSpan span, string level, NameToken target)
		{
			var whole = Span(span);

			return new ReferenceSyntax(
				whole,
				level == "aspect" ? FilterLevel.Aspect : FilterLevel.Assembly,
				new TextSpan(whole.Start, level.Length),
				target.Text,
				target.Span);
		}

		static FilterExpressionSyntax Words(SourceSpan span, WordSyntax? first, WordSyntax[]? more, SignatureSyntax? signature)
		{
			var words = first is null ? ImmutableArray<WordSyntax>.Empty : List(first, more);

			return new WordRunSyntax(Span(span), words, signature);
		}

		static WordSyntax ReservedWord(SourceSpan span, string word)
		{
			return new WordSyntax(Error(FilterSyntaxErrorKind.Reserved, span, $"'{word}' is reserved for a later version."));
		}

		static FilterExpressionSyntax ReservedLiteral(SourceSpan span)
		{
			return new ErrorExpressionSyntax(Error(FilterSyntaxErrorKind.Reserved, span, "'/…/' literals are reserved for a later version."));
		}

		// ---- name patterns ---------------------------------------------------------------------------------------

		static NamePatternSyntax Name(SourceSpan span, SpanToken? global, NameSegmentSyntax first, NameSegmentSyntax[]? more)
		{
			return new NamePatternSyntax(Span(span), global?.Span, List(first, more));
		}

		static NamePatternSyntax QuestionAfterName(SourceSpan span, NamePatternSyntax name, SpanToken? question)
		{
			if (question is null)
				return name;

			return new NamePatternSyntax(Span(span), name.Global, name.Segments,
				new FilterSyntaxError(FilterSyntaxErrorKind.QuestionOutsideType, question.Span, QuestionMessage));
		}

		const string QuestionMessage = "'?' means nullable and stands only after a type; for any characters in a name write '*'.";

		static NameSegmentSyntax Segment(SourceSpan span, NamePartSyntax[] parts, TypeArgumentsSyntax? typeArguments)
		{
			return new NameSegmentSyntax(Span(span), false, Array(parts), typeArguments);
		}

		static NameSegmentSyntax KeywordSegment(SourceSpan span, string word)
		{
			return new NameSegmentSyntax(Span(span), false, ImmutableArray.Create(NamePartSyntax.Refused(KeywordAsName(span, word))), null);
		}

		static FilterValueSyntax KeywordName(SourceSpan span, string word)
		{
			return new ErrorValueSyntax(KeywordAsName(span, word));
		}

		static FilterSyntaxError KeywordAsName(SourceSpan span, string word)
		{
			return Error(FilterSyntaxErrorKind.KeywordAsName, span, $"'{word}' is a keyword: a name equal to it is written '@{word}'.");
		}

		static NamePartSyntax GlobstarInSegment(SourceSpan span)
		{
			return NamePartSyntax.Refused(Error(FilterSyntaxErrorKind.GlobstarInSegment, span,
				"'**' stands only as a whole segment ('Shop.**.Save'); within a segment write '*'."));
		}

		static NamePartSyntax LegacyEscape(SourceSpan span)
		{
			return NamePartSyntax.Refused(Error(FilterSyntaxErrorKind.Legacy, span,
				"Backslash escapes are version 1.1 syntax: write exact text in quotes (\"<Main>$\"), and a name equal to a keyword with '@' (@static)."));
		}

		static NamePartSyntax QuestionInName(SourceSpan span)
		{
			return NamePartSyntax.Refused(Error(FilterSyntaxErrorKind.QuestionOutsideType, span, QuestionMessage));
		}

		static NamePartSyntax Alternatives(SourceSpan span, NamePatternSyntax first, NamePatternSyntax[]? more)
		{
			return NamePartSyntax.Choice(Span(span), List(first, more));
		}

		static NamePartSyntax EmptyAlternatives(SourceSpan span)
		{
			return NamePartSyntax.Refused(Error(FilterSyntaxErrorKind.EmptyAlternatives, span, "'{}' holds no alternative."));
		}

		/// <summary>Quoted text, unescaped: <c>\"</c> and <c>\\</c> are its only escapes.</summary>
		static NamePartSyntax Quoted(SourceSpan span, string text)
		{
			if (text.IndexOf('\\') < 0)
				return NamePartSyntax.Quoted(Span(span), text, null);

			var builder = new StringBuilder(text.Length);
			var invalid = -1;

			for (var i = 0; i < text.Length; i++)
			{
				var ch = text[i];

				if (ch == '\\' && i + 1 < text.Length)
				{
					var next = text[++i];

					if (next is not ('"' or '\\') && invalid < 0)
						invalid = i - 1;

					builder.Append(next);
				}
				else
				{
					builder.Append(ch);
				}
			}

			var error = invalid < 0
				? null
				: new FilterSyntaxError(FilterSyntaxErrorKind.InvalidEscape, new TextSpan((int)span.Start + 1 + invalid, 2),
					"In quoted text only '\\\"' and '\\\\' are escapes.");

			return NamePartSyntax.Quoted(Span(span), builder.ToString(), error);
		}

		static TypeArgumentsSyntax TypeArguments(SourceSpan span, TypePatternSyntax first, TypePatternSyntax[]? more)
		{
			return new TypeArgumentsSyntax(Span(span), List(first, more));
		}

		static TypeArgumentsSyntax OpenGeneric(SourceSpan span, string text)
		{
			var arity = 1;

			foreach (var ch in text)
				if (ch == ',')
					arity++;

			var instead = "<" + string.Join(",", System.Linq.Enumerable.Repeat("*", arity)) + ">";

			return new TypeArgumentsSyntax(Span(span), ImmutableArray<TypePatternSyntax>.Empty,
				Error(FilterSyntaxErrorKind.OpenGeneric, span, $"'{text}' is no pattern: write '{instead}' for any type arguments of that arity."));
		}

		// ---- type patterns ---------------------------------------------------------------------------------------

		/// <summary>
		/// A type that begins with <c>{</c>, read once as alternatives of types: alternatives of names where a name goes on
		/// after them or every alternative is a name, alternatives of types otherwise.
		/// </summary>
		static TypeCoreSyntax Braced(SourceSpan span, TypePatternSyntax? first, TypePatternSyntax[]? more, SpanToken close, NameTailToken tail)
		{
			var whole  = Span(span);
			var braces = TextSpan.FromBounds(whole.Start, close.Span.End);

			if (first is null)
				return NamedFromBraces(whole, NamePartSyntax.Refused(new FilterSyntaxError(FilterSyntaxErrorKind.EmptyAlternatives, braces, "'{}' holds no alternative.")), tail);

			var items = List(first, more);
			var names = ImmutableArray.CreateBuilder<NamePatternSyntax>(items.Length);

			foreach (var item in items)
				if (item.Suffixes.IsEmpty && item.Core is NamedTypeSyntax named)
					names.Add(named.Name);

			if (names.Count == items.Length)
				return NamedFromBraces(whole, NamePartSyntax.Choice(braces, names.MoveToImmutable()), tail);

			if (tail.IsEmpty)
				return new TypeAlternativesSyntax(whole, items);

			return NamedFromBraces(whole, NamePartSyntax.Refused(new FilterSyntaxError(FilterSyntaxErrorKind.TypesInName, braces,
				"Alternatives that a name goes on after hold names only; alternatives of other types stand alone ('{int[], string}').")), tail);
		}

		static TypeCoreSyntax NamedFromBraces(TextSpan whole, NamePartSyntax braces, NameTailToken tail)
		{
			var parts = ImmutableArray.CreateBuilder<NamePartSyntax>(1 + tail.Parts.Length);

			parts.Add(braces);
			parts.AddRange(tail.Parts);

			var end      = tail.Arguments?.Span.End ?? (tail.Parts.IsEmpty ? braces.Span.End : tail.Parts[tail.Parts.Length - 1].Span.End);
			var segment  = new NameSegmentSyntax(TextSpan.FromBounds(whole.Start, end), false, parts.MoveToImmutable(), tail.Arguments);
			var segments = ImmutableArray.CreateBuilder<NameSegmentSyntax>(1 + tail.More.Length);

			segments.Add(segment);
			segments.AddRange(tail.More);

			return new NamedTypeSyntax(new NamePatternSyntax(whole, null, segments.MoveToImmutable()));
		}

		static TypeSuffixSyntax Rank(SourceSpan span, string commas)
		{
			var rank = 1;

			foreach (var ch in commas)
				if (ch == ',')
					rank++;

			return new TypeSuffixSyntax(Span(span), rank);
		}

		static TypeCoreSyntax FunctionPointer(SourceSpan span, CallingConventionToken? convention, FunctionPointerParameterSyntax first, FunctionPointerParameterSyntax[]? more)
		{
			return new FunctionPointerTypeSyntax(
				Span(span),
				convention?.Kind,
				convention?.Unmanaged ?? ImmutableArray<string>.Empty,
				List(first, more));
		}

		static CallingConventionToken Convention(string kind, string? first, string[]? more)
		{
			return new CallingConventionToken(kind, first is null ? ImmutableArray<string>.Empty : List(first, more));
		}

		static ParameterListPatternSyntax Parameters(SourceSpan span, ParameterPatternSyntax? first, ParameterPatternSyntax[]? more)
		{
			return new ParameterListPatternSyntax(Span(span), first is null ? ImmutableArray<ParameterPatternSyntax>.Empty : List(first, more));
		}

		static ParameterPatternSyntax LegacyAnyRun(SourceSpan span)
		{
			return new ParameterPatternSyntax(Span(span), ParameterPatternKind.Error, default, null,
				Error(FilterSyntaxErrorKind.Legacy, span, "'...' is version 1.1 syntax: write '..' for any run of parameters."));
		}

		// ---- what the grammar hands between its rules ------------------------------------------------------------

		/// <summary>A key as written.</summary>
		internal sealed class KeyToken
		{
			public KeyToken(FilterKey key, string text, TextSpan span)
			{
				Key  = key;
				Text = text;
				Span = span;
			}

			public FilterKey Key  { get; }
			public string    Text { get; }
			public TextSpan  Span { get; }
		}

		/// <summary>A word or a piece of text and where it stands.</summary>
		internal sealed class NameToken
		{
			public NameToken(string text, TextSpan span)
			{
				Text = text;
				Span = span;
			}

			public string   Text { get; }
			public TextSpan Span { get; }
		}

		/// <summary>Where a piece of text stands.</summary>
		internal sealed class SpanToken
		{
			public SpanToken(TextSpan span)
			{
				Span = span;
			}

			public TextSpan Span { get; }
		}

		/// <summary>An operator between conditions: <c>and</c>, or a 1.1 one read with its error.</summary>
		internal sealed class OperatorToken
		{
			public static readonly OperatorToken And = new(null);

			public OperatorToken(FilterSyntaxError? error)
			{
				Error = error;
			}

			public FilterSyntaxError? Error { get; }
		}

		/// <summary>What follows alternatives in a name: more parts, type arguments, more segments, or nothing.</summary>
		internal sealed class NameTailToken
		{
			public NameTailToken(TextSpan span, ImmutableArray<NamePartSyntax> parts, TypeArgumentsSyntax? arguments, ImmutableArray<NameSegmentSyntax> more)
			{
				Span      = span;
				Parts     = parts;
				Arguments = arguments;
				More      = more;
			}

			public TextSpan                          Span      { get; }
			public ImmutableArray<NamePartSyntax>    Parts     { get; }
			public TypeArgumentsSyntax?              Arguments { get; }
			public ImmutableArray<NameSegmentSyntax> More      { get; }
			public bool                              IsEmpty   => Span.IsEmpty;
		}

		/// <summary>The calling convention of a function pointer.</summary>
		internal sealed class CallingConventionToken
		{
			public CallingConventionToken(string kind, ImmutableArray<string> unmanaged)
			{
				Kind      = kind;
				Unmanaged = unmanaged;
			}

			public string                 Kind      { get; }
			public ImmutableArray<string> Unmanaged { get; }
		}
	}
}
