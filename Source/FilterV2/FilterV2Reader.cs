using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Microsoft.CodeAnalysis.Text;

namespace AspectGenerator.FilterV2
{
	/// <summary>The diagnostic ids of target filters, version 2.</summary>
	internal static class FilterV2DiagnosticID
	{
		public const string SyntaxError      = "AG0220";
		public const string UnknownOrReserved = "AG0221";
		public const string InvalidPattern   = "AG0224";
		public const string Legacy           = "AG0225";
		public const string NestedTooDeeply  = "AG0227";
		public const string LeadingOperator  = "AG0231";
		public const string InvalidReference = "AG0233";
		public const string InvalidAny       = "AG0234";

		public static string For(FilterSyntaxErrorKind kind)
		{
			return kind switch
			{
				FilterSyntaxErrorKind.Syntax              => SyntaxError,
				FilterSyntaxErrorKind.NotBeforeGroup      => SyntaxError,
				FilterSyntaxErrorKind.NotBeforeSeveral    => SyntaxError,
				FilterSyntaxErrorKind.OpenGeneric         => SyntaxError,
				FilterSyntaxErrorKind.KeywordAsName       => SyntaxError,
				FilterSyntaxErrorKind.UnknownKey          => UnknownOrReserved,
				FilterSyntaxErrorKind.Reserved            => UnknownOrReserved,
				FilterSyntaxErrorKind.WrongValue          => UnknownOrReserved,
				FilterSyntaxErrorKind.GlobstarInSegment   => InvalidPattern,
				FilterSyntaxErrorKind.AnyRunTwice         => InvalidPattern,
				FilterSyntaxErrorKind.QuestionOutsideType => InvalidPattern,
				FilterSyntaxErrorKind.EmptyAlternatives   => InvalidPattern,
				FilterSyntaxErrorKind.InvalidEscape       => InvalidPattern,
				FilterSyntaxErrorKind.Legacy              => Legacy,
				FilterSyntaxErrorKind.NestedTooDeeply     => NestedTooDeeply,
				FilterSyntaxErrorKind.LeadingOperator     => LeadingOperator,
				FilterSyntaxErrorKind.InvalidReference    => InvalidReference,
				FilterSyntaxErrorKind.InvalidAny          => InvalidAny,
				_                                         => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
			};
		}
	}

	/// <summary>A diagnostic of one filter item: its id, where it stands in the item, and what it says.</summary>
	internal sealed class FilterDiagnostic
	{
		public FilterDiagnostic(FilterSyntaxError error)
		{
			Id      = FilterV2DiagnosticID.For(error.Kind);
			Kind    = error.Kind;
			Span    = error.Span;
			Message = error.Message;
		}

		public string                Id      { get; }
		public FilterSyntaxErrorKind Kind    { get; }
		public TextSpan              Span    { get; }
		public string                Message { get; }

		public override string ToString()
		{
			return $"{Id} {Kind}@{Span.Start}+{Span.Length}: {Message}";
		}
	}

	/// <summary>What one item of a filter reads to: its tree, or <see langword="null"/> where it cannot be read at all, and its diagnostics.</summary>
	internal sealed class FilterItemReading
	{
		public FilterItemReading(FilterItemSyntax? item, ImmutableArray<FilterDiagnostic> diagnostics)
		{
			Item        = item;
			Diagnostics = diagnostics;
		}

		public FilterItemSyntax?                Item        { get; }
		public ImmutableArray<FilterDiagnostic> Diagnostics { get; }
		public bool                             HasErrors   => !Diagnostics.IsEmpty;
	}

	/// <summary>
	/// Reads one item of a target filter (a whole string, or one element of a <c>string[]</c>) into its tree and reports
	/// every error the tree carries, in text order, together with the errors that one condition alone decides: <c>not</c>
	/// before several conditions, <c>..</c> twice, a reference that names no property or not the one it stands in, and
	/// <c>any</c> in a subtraction or under <c>not</c>. What needs the lines of a whole filter (a property included twice,
	/// a reference or <c>any</c> in a compound line, the levels a reference may name in its block) is decided with the
	/// declarations.
	/// </summary>
	internal static class FilterV2Reader
	{
		public static FilterItemReading Read(string text)
		{
			FilterItemSyntax item;

			try
			{
				var match = FilterV2Parser.TryParseItem(text);

				if (!match.IsSuccess)
				{
					// Every line is stepped over when it cannot be read, so a refusal of the whole item is a reading the
					// grammar could not finish at all.
					var position = (int)Math.Min(match.Position, text.Length);

					return new FilterItemReading(null, ImmutableArray.Create(new FilterDiagnostic(new FilterSyntaxError(
						FilterSyntaxErrorKind.Syntax, TextSpan.FromBounds(position, text.Length), $"Syntax error: {match.Error}"))));
				}

				item = match.Value!;
			}
			catch (InsufficientExecutionStackException)
			{
				return new FilterItemReading(null, ImmutableArray.Create(new FilterDiagnostic(new FilterSyntaxError(
					FilterSyntaxErrorKind.NestedTooDeeply, new TextSpan(0, text.Length), "The filter is nested too deeply to be read."))));
			}

			return new FilterItemReading(item, Check(item, text));
		}

		static ImmutableArray<FilterDiagnostic> Check(FilterItemSyntax item, string text)
		{
			var errors = new List<FilterSyntaxError>();

			if (item.LeadingOperator is not null)
				errors.Add(item.LeadingOperator);

			foreach (var line in item.Lines)
				CheckLine(line, text, errors);

			return errors
				.OrderBy(static e => e.Span.Start)
				.ThenBy(static e => e.Span.Length)
				.Select(static e => new FilterDiagnostic(e))
				.ToImmutableArray();
		}

		/// <summary>Walks a line without recursion: a tree is as deep as the brackets of the filter.</summary>
		static void CheckLine(FilterLineSyntax line, string text, List<FilterSyntaxError> errors)
		{
			var stack = new Stack<(FilterSyntaxNode Node, bool UnderNot)>();

			stack.Push((line.Expression, false));

			while (stack.Count > 0)
			{
				var (node, underNot) = stack.Pop();

				if (node.Error is { } error)
					errors.Add(error);

				switch (node)
				{
					case AndExpressionSyntax and:
						errors.AddRange(and.OperatorErrors);
						break;

					case NotExpressionSyntax { Operand: WordRunSyntax { ConditionCount: > 1 } words } not:
						errors.Add(NotBeforeSeveral(not, words, text));
						break;

					case ParameterListPatternSyntax list:
						CheckAnyRuns(list, errors);
						break;

					case TypePatternSyntax type:
						CheckNullable(type, errors);
						break;

					case KeyConditionSyntax condition:
						CheckKey(condition, errors);

						if (condition.Value is AnyValueSyntax any && (underNot || line.IsSubtraction))
							errors.Add(new FilterSyntaxError(FilterSyntaxErrorKind.InvalidAny, any.Span, line.IsSubtraction
								? "'- p: any' subtracts nothing: 'any' removes every restriction on the property and stands only in an include."
								: "'not p: any' is no condition: 'any' removes every restriction on the property and stands only as a whole value."));
						break;

					case ReferenceSyntax reference:
						CheckReferenceTarget(reference, errors);
						break;
				}

				var children = node.Children as IList<FilterSyntaxNode> ?? node.Children.ToList();
				var isNot    = underNot || node is NotExpressionSyntax;

				for (var i = children.Count - 1; i >= 0; i--)
					stack.Push((children[i], isNot));
			}
		}

		static FilterSyntaxError NotBeforeSeveral(NotExpressionSyntax not, WordRunSyntax words, string text)
		{
			var parts = new List<string>();

			foreach (var word in words.Words)
				parts.Add(Text(text, word.Span));

			if (words.Signature is { } signature)
			{
				parts.Add(Text(text, signature.Name.Span));

				if (signature.Parameters is { } parameters)
					parts.Add("params: " + Text(text, parameters.Span));

				if (signature.Returns is { } returns)
					parts.Add("returns: " + Text(text, returns.Span));
			}

			var none   = string.Join(" and ", parts.Select(static p => "not " + p));
			var notAll = string.Join(" or ",  parts.Select(static p => "not " + p));

			return new FilterSyntaxError(FilterSyntaxErrorKind.NotBeforeSeveral, not.Span,
				$"'not' stands before a single condition, and '{Text(text, words.Span)}' is {parts.Count}: write '{none}' (none of them) or '{notAll}' (not all of them).");
		}

		static void CheckAnyRuns(ParameterListPatternSyntax list, List<FilterSyntaxError> errors)
		{
			var seen = false;

			foreach (var parameter in list.Parameters)
			{
				if (parameter.Kind != ParameterPatternKind.AnyRun)
					continue;

				if (seen)
					errors.Add(new FilterSyntaxError(FilterSyntaxErrorKind.AnyRunTwice, parameter.Span, "'..' stands at most once in a parameter list."));

				seen = true;
			}
		}

		/// <summary><c>int??</c>: <c>?</c> follows a type, and a nullable type is no type it can follow.</summary>
		static void CheckNullable(TypePatternSyntax type, List<FilterSyntaxError> errors)
		{
			for (var i = 1; i < type.Suffixes.Length; i++)
				if (type.Suffixes[i].IsNullable && type.Suffixes[i - 1].IsNullable)
					errors.Add(new FilterSyntaxError(FilterSyntaxErrorKind.QuestionOutsideType, type.Suffixes[i].Span, "'?' stands once after a type: a nullable type is not made nullable again."));
		}

		static void CheckKey(KeyConditionSyntax condition, List<FilterSyntaxError> errors)
		{
			if (condition.Key == FilterKey.Overrides && !FilterV2Language.OverridesIsKey)
			{
				errors.Add(new FilterSyntaxError(FilterSyntaxErrorKind.UnknownKey, condition.KeySpan,
					"Unknown key 'overrides:': 'implements:' matches a method that overrides a matching member as well as one that implements it."));
				return;
			}

			if (condition.Value is not ReferenceSyntax reference || !FilterV2Language.TryGetProperty(reference.Target, out var target))
				return;

			var property = FilterV2Language.Property(condition.Key);

			if (target != property)
				errors.Add(new FilterSyntaxError(FilterSyntaxErrorKind.InvalidReference, reference.TargetSpan,
					$"A reference names the property it stands in: write '{LevelName(reference.Level)}.{FilterV2Language.Name(property)}' here, or a condition on '{reference.Target}' of its own."));
		}

		/// <summary>The checks of a reference that its own text decides: whether it names a property at all.</summary>
		static void CheckReferenceTarget(ReferenceSyntax reference, List<FilterSyntaxError> errors)
		{
			if (FilterV2Language.TryGetProperty(reference.Target, out _))
				return;

			var level = LevelName(reference.Level);

			var message = reference.Target switch
			{
				"namespace" or "type" => $"'namespace:' and 'type:' are both about the location of a method: write '{level}.location'.",
				"overrides"           => $"'implements' covers overriding: write '{level}.implements'.",
				_                     => $"'{level}.{reference.Target}' names no property. A name that begins with '{level}.' is written '@{level}.{reference.Target}'.",
			};

			errors.Add(new FilterSyntaxError(FilterSyntaxErrorKind.InvalidReference, reference.TargetSpan, message));
		}

		static string LevelName(FilterLevel level)
		{
			return level == FilterLevel.Aspect ? "aspect" : "assembly";
		}

		static string Text(string text, TextSpan span)
		{
			return text.Substring(span.Start, span.Length);
		}
	}
}
