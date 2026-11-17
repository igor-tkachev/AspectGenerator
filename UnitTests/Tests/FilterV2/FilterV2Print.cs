using System;
using System.Linq;
using System.Text;

using AspectGenerator.FilterV2;

namespace AspectGenerator.Tests.FilterV2
{
	/// <summary>
	/// A compact text of a version 2 filter tree, for tests: what was read, without the spans. Lines are separated by
	/// <c> | </c>; an error prints as <c>!Kind</c>.
	/// </summary>
	static class FilterV2Print
	{
		public static string Item(FilterItemSyntax item)
		{
			var builder = new StringBuilder();

			if (item.LeadingOperator is not null)
				builder.Append("!LeadingOperator ");

			for (var i = 0; i < item.Lines.Length; i++)
			{
				if (i > 0)
					builder.Append(" | ");

				Line(builder, item.Lines[i]);
			}

			return builder.ToString();
		}

		static void Line(StringBuilder builder, FilterLineSyntax line)
		{
			if (line.IsSubtraction)
				builder.Append("- ");

			Expression(builder, line.Expression);
		}

		static void Expression(StringBuilder builder, FilterExpressionSyntax expression)
		{
			switch (expression)
			{
				case ErrorExpressionSyntax error:
					builder.Append('!').Append(error.Error.Kind);

					if (error.ErrorParts.Length > 0)
					{
						builder.Append('(');

						foreach (var part in error.ErrorParts.OfType<FilterExpressionSyntax>())
							Expression(builder, part);

						builder.Append(')');
					}

					break;

				case OrExpressionSyntax or:
					Operands(builder, "or", or.Operands.ToArray());
					break;

				case AndExpressionSyntax and:
					Operands(builder, "and", and.Operands.ToArray());

					foreach (var error in and.OperatorErrors)
						builder.Append(" !").Append(error.Kind);

					break;

				case NotExpressionSyntax not:
					builder.Append("not ");
					Expression(builder, not.Operand);
					break;

				case GroupExpressionSyntax group:
					builder.Append('(');
					Expression(builder, group.Expression);
					builder.Append(')');
					break;

				case KeyConditionSyntax condition:
					builder.Append(condition.Key).Append(": ");
					Value(builder, condition.Value);
					break;

				case ReferenceExpressionSyntax reference:
					Value(builder, reference.Reference);
					break;

				case WordRunSyntax words:
					builder.Append('[');

					for (var i = 0; i < words.Words.Length; i++)
					{
						if (i > 0)
							builder.Append(' ');

						var word = words.Words[i];

						if (word.Error is { } error)
							builder.Append('!').Append(error.Kind);
						else
							builder.Append(word.Word);
					}

					if (words.Signature is { } signature)
					{
						if (words.Words.Length > 0)
							builder.Append(' ');

						builder.Append("sig ");
						Name(builder, signature.Name);

						if (signature.Parameters is { } parameters)
							Value(builder, parameters);

						if (signature.Returns is { } returns)
						{
							builder.Append(" : ");
							Type(builder, returns);
						}
					}

					builder.Append(']');
					break;

				default:
					throw new InvalidOperationException(expression.GetType().Name);
			}
		}

		static void Operands(StringBuilder builder, string op, FilterExpressionSyntax[] operands)
		{
			builder.Append(op).Append('{');

			for (var i = 0; i < operands.Length; i++)
			{
				if (i > 0)
					builder.Append(", ");

				Expression(builder, operands[i]);
			}

			builder.Append('}');
		}

		public static string Value(FilterValueSyntax value)
		{
			var builder = new StringBuilder();

			Value(builder, value);

			return builder.ToString();
		}

		static void Value(StringBuilder builder, FilterValueSyntax value)
		{
			switch (value)
			{
				case AnyValueSyntax:
					builder.Append("any");
					break;

				case ReferenceSyntax reference:
					builder.Append(reference.Level == FilterLevel.Aspect ? "aspect" : "assembly").Append("->").Append(reference.Target);
					break;

				case ErrorValueSyntax error:
					builder.Append('!').Append(error.Error.Kind);
					break;

				case NamePatternSyntax name:
					Name(builder, name);
					break;

				case TypePatternSyntax type:
					Type(builder, type);
					break;

				case ReturnsPatternSyntax returns:
					if (returns.RefKind == FilterRefKind.Ref)
						builder.Append("ref ");
					else if (returns.RefKind == FilterRefKind.RefReadonly)
						builder.Append("ref readonly ");

					Type(builder, returns.Type);
					break;

				case ParameterListPatternSyntax list:
					builder.Append('(');

					for (var i = 0; i < list.Parameters.Length; i++)
					{
						if (i > 0)
							builder.Append(", ");

						Parameter(builder, list.Parameters[i]);
					}

					builder.Append(')');
					break;

				case ParameterPatternSyntax parameter:
					Parameter(builder, parameter);
					break;

				default:
					throw new InvalidOperationException(value.GetType().Name);
			}
		}

		static void Parameter(StringBuilder builder, ParameterPatternSyntax parameter)
		{
			switch (parameter.Kind)
			{
				case ParameterPatternKind.AnyOne: builder.Append('_');  return;
				case ParameterPatternKind.AnyRun: builder.Append(".."); return;
				case ParameterPatternKind.Error:  builder.Append('!').Append(parameter.Error!.Kind); return;
			}

			foreach (var modifier in parameter.Modifiers)
				builder.Append(Modifier(modifier)).Append(' ');

			Type(builder, parameter.Type!);
		}

		static string Modifier(FilterParameterModifier modifier)
		{
			return modifier switch
			{
				FilterParameterModifier.RefReadonly => "ref readonly",
				_                                   => modifier.ToString().ToLowerInvariant(),
			};
		}

		static void Name(StringBuilder builder, NamePatternSyntax name)
		{
			if (name.Global is not null)
				builder.Append("global::");

			for (var i = 0; i < name.Segments.Length; i++)
			{
				if (i > 0)
					builder.Append('.');

				var segment = name.Segments[i];

				if (segment.IsGlobstar)
				{
					builder.Append("**");
					continue;
				}

				foreach (var part in segment.Parts)
				{
					switch (part.Kind)
					{
						case NamePartKind.Text when part.IsQuoted:
							builder.Append('"').Append(part.Text).Append('"');

							if (part.Error is { } error)
								builder.Append('!').Append(error.Kind);

							break;

						case NamePartKind.Text:
							builder.Append(part.Text);
							break;

						case NamePartKind.Star:
							builder.Append('*');
							break;

						case NamePartKind.Alternatives:
							builder.Append('{');

							for (var a = 0; a < part.Alternatives.Length; a++)
							{
								if (a > 0)
									builder.Append(',');

								Name(builder, part.Alternatives[a]);
							}

							builder.Append('}');
							break;

						case NamePartKind.Error:
							builder.Append('!').Append(part.Error!.Kind);
							break;
					}
				}

				if (segment.TypeArguments is { } arguments)
				{
					if (arguments.Error is { } error)
					{
						builder.Append("<!").Append(error.Kind).Append('>');
						continue;
					}

					builder.Append('<');

					for (var a = 0; a < arguments.Arguments.Length; a++)
					{
						if (a > 0)
							builder.Append(',');

						Type(builder, arguments.Arguments[a]);
					}

					builder.Append('>');
				}
			}

			if (name.Error is { } nameError)
				builder.Append('!').Append(nameError.Kind);
		}

		static void Type(StringBuilder builder, TypePatternSyntax type)
		{
			switch (type.Core)
			{
				case NamedTypeSyntax named:
					Name(builder, named.Name);
					break;

				case TupleTypeSyntax tuple:
					builder.Append("tuple(");
					Types(builder, tuple.Elements.ToArray());
					builder.Append(')');
					break;

				case TypeAlternativesSyntax alternatives:
					builder.Append("either{");
					Types(builder, alternatives.Alternatives.ToArray());
					builder.Append('}');
					break;

				case FunctionPointerTypeSyntax pointer:
					builder.Append("fnptr");

					if (pointer.CallingConvention is { } convention)
						builder.Append(' ').Append(convention);

					if (pointer.UnmanagedConventions.Length > 0)
						builder.Append('[').Append(string.Join(",", pointer.UnmanagedConventions)).Append(']');

					builder.Append('<');

					for (var i = 0; i < pointer.Parameters.Length; i++)
					{
						if (i > 0)
							builder.Append(',');

						if (pointer.Parameters[i].Modifier is { } modifier)
							builder.Append(Modifier(modifier)).Append(' ');

						Type(builder, pointer.Parameters[i].Type);
					}

					builder.Append('>');
					break;
			}

			foreach (var suffix in type.Suffixes)
			{
				if (suffix.IsNullable)
					builder.Append('?');
				else
					builder.Append('[').Append(new string(',', suffix.Rank - 1)).Append(']');
			}
		}

		static void Types(StringBuilder builder, TypePatternSyntax[] types)
		{
			for (var i = 0; i < types.Length; i++)
			{
				if (i > 0)
					builder.Append(',');

				Type(builder, types[i]);
			}
		}
	}
}
