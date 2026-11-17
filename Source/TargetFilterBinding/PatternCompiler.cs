using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

using AspectGenerator.FilterV2;
using AspectGenerator.TargetFilterFacts;

namespace AspectGenerator.TargetFilterBinding
{
	/// <summary>
	/// Compiles the patterns of a filter's syntax tree into matchers. It reads trees without errors only: a tree with
	/// an error node is never compiled, because the filter it belongs to selects nothing.
	/// </summary>
	static class PatternCompiler
	{
		/// <summary>How a dotted pattern is anchored where it does not say <c>global::</c>.</summary>
		public enum Anchoring
		{
			/// <summary>The whole name: <c>namespace:</c>.</summary>
			Rooted,
			/// <summary>A trailing run of whole segments: types, methods, attributes.</summary>
			Trailing,
		}

		// C#'s names for the types of the System namespace. They are rooted: `int` is System.Int32 and no other Int32.
		static readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal)
		{
			["bool"]    = "Boolean",
			["byte"]    = "Byte",
			["sbyte"]   = "SByte",
			["char"]    = "Char",
			["decimal"] = "Decimal",
			["double"]  = "Double",
			["float"]   = "Single",
			["int"]     = "Int32",
			["uint"]    = "UInt32",
			["nint"]    = "IntPtr",
			["nuint"]   = "UIntPtr",
			["long"]    = "Int64",
			["ulong"]   = "UInt64",
			["short"]   = "Int16",
			["ushort"]  = "UInt16",
			["object"]  = "Object",
			["string"]  = "String",
			["void"]    = "Void",
		};

		// ---- names -----------------------------------------------------------------------------------------------

		/// <summary>
		/// A dotted name where it stands as a whole value (<c>namespace:</c>, <c>method:</c>, a compact signature).
		/// Alternatives written as the whole name are patterns of their own, each with its own <c>global::</c>.
		/// </summary>
		public static NamePatternSet CompileName(NamePatternSyntax syntax, Anchoring anchoring)
		{
			if (TryGetWholeAlternatives(syntax, out var alternatives))
			{
				var patterns = ImmutableArray.CreateBuilder<NamePattern>(alternatives.Length);

				foreach (var alternative in alternatives)
					patterns.Add(CompileOneName(alternative, anchoring));

				return new NamePatternSet(patterns.MoveToImmutable());
			}

			return new NamePatternSet(ImmutableArray.Create(CompileOneName(syntax, anchoring)));
		}

		/// <summary><c>{a,b}</c> and nothing else: the alternatives stand for the whole name.</summary>
		static bool TryGetWholeAlternatives(NamePatternSyntax syntax, out ImmutableArray<NamePatternSyntax> alternatives)
		{
			if (syntax.Global is null &&
				syntax.Segments.Length == 1 &&
				syntax.Segments[0] is { IsGlobstar: false, TypeArguments: null } segment &&
				segment.Parts.Length == 1 &&
				segment.Parts[0].Kind == NamePartKind.Alternatives)
			{
				alternatives = segment.Parts[0].Alternatives;
				return true;
			}

			alternatives = default;
			return false;
		}

		static NamePattern CompileOneName(NamePatternSyntax syntax, Anchoring anchoring)
		{
			RuntimeHelpers.EnsureSufficientExecutionStack();

			if (IsEmptyName(syntax))
				return NamePattern.EmptyPath;

			var builder = new Builder();
			var step    = builder.Add(NamePattern.StepKind.Accept, null);

			for (var i = syntax.Segments.Length - 1; i >= 0; i--)
			{
				var segment = syntax.Segments[i];

				step = segment.IsGlobstar
					? builder.Add(NamePattern.StepKind.Globstar, step)
					: builder.Segment(segment, builder.Add(NamePattern.StepKind.SegmentEnd, step));
			}

			var globstars = 0;

			foreach (var segment in syntax.Segments)
				if (segment.IsGlobstar)
					globstars++;

			var isFixed = globstars == 0 && !builder.AlternativesSpanSegments;

			return new NamePattern(
				step,
				builder.Count,
				isRooted: syntax.Global is not null || anchoring == Anchoring.Rooted,
				fixedSegments: isFixed ? syntax.Segments.Length : -1,
				minSegments: syntax.Segments.Length - globstars,
				choices: builder.Choices);
		}

		/// <summary><c>""</c>: one segment of empty text, the name of the global namespace.</summary>
		static bool IsEmptyName(NamePatternSyntax syntax)
		{
			if (syntax.Segments.Length != 1 || syntax.Segments[0] is not { IsGlobstar: false, TypeArguments: null } segment)
				return false;

			foreach (var part in segment.Parts)
				if (part.Kind != NamePartKind.Text || part.Text!.Length != 0)
					return false;

			return true;
		}

		sealed class Builder
		{
			public int  Count;
			public int  Choices;
			public bool AlternativesSpanSegments;

			public NamePattern.Step Add(NamePattern.StepKind kind, NamePattern.Step? next)
			{
				if (kind is NamePattern.StepKind.Star or NamePattern.StepKind.Globstar or NamePattern.StepKind.Alternatives)
					Choices++;

				return new NamePattern.Step(kind, next) { Index = Count++ };
			}

			/// <summary>The steps of one segment's parts and type arguments, ending in <paramref name="next"/>.</summary>
			public NamePattern.Step Segment(NameSegmentSyntax segment, NamePattern.Step next)
			{
				var step = next;

				if (segment.TypeArguments is { } typeArguments)
				{
					var arguments = ImmutableArray.CreateBuilder<TypePattern>(typeArguments.Arguments.Length);

					foreach (var argument in typeArguments.Arguments)
						arguments.Add(CompileType(argument));

					step           = Add(NamePattern.StepKind.Arguments, step);
					step.Arguments = arguments.MoveToImmutable();
				}

				for (var i = segment.Parts.Length - 1; i >= 0; i--)
				{
					var part = segment.Parts[i];

					switch (part.Kind)
					{
						case NamePartKind.Text:
							if (part.Text!.Length == 0)
								break;

							step      = Add(NamePattern.StepKind.Text, step);
							step.Text = part.Text;
							break;

						case NamePartKind.Star:
							// `**` inside a segment is refused by the reader; two stars with nothing between them are one.
							if (step.Kind != NamePattern.StepKind.Star)
								step = Add(NamePattern.StepKind.Star, step);

							break;

						case NamePartKind.Alternatives:
						{
							var branches = ImmutableArray.CreateBuilder<NamePattern.Step>(part.Alternatives.Length);

							foreach (var alternative in part.Alternatives)
								branches.Add(Branch(alternative, step));

							step          = Add(NamePattern.StepKind.Alternatives, step);
							step.Branches = branches.MoveToImmutable();
							break;
						}

						default:
							throw new InvalidOperationException("A pattern with an error is not compiled.");
					}
				}

				for (var star = step; star != next && star is not null; star = star.Next)
					if (star.Kind == NamePattern.StepKind.Star)
						star.TailLength = TailLength(star.Next!);

				return step;
			}

			/// <summary>
			/// An alternative inside a segment: its own segments, the last of which goes on with what follows the braces.
			/// A <c>global::</c> on it roots nothing here, since the alternative is a part of a longer name.
			/// </summary>
			NamePattern.Step Branch(NamePatternSyntax alternative, NamePattern.Step next)
			{
				RuntimeHelpers.EnsureSufficientExecutionStack();

				var step = next;

				if (alternative.Segments.Length != 1 || alternative.Segments[0].IsGlobstar)
					AlternativesSpanSegments = true;

				for (var i = alternative.Segments.Length - 1; i >= 0; i--)
				{
					var segment = alternative.Segments[i];
					var isLast  = i == alternative.Segments.Length - 1;

					step = segment.IsGlobstar
						? Add(NamePattern.StepKind.Globstar, step)
						: Segment(segment, isLast ? step : Add(NamePattern.StepKind.SegmentEnd, step));
				}

				return step;
			}

			/// <summary>The length of the text from a step to the end of its segment, or -1 when anything but text stands there.</summary>
			static int TailLength(NamePattern.Step step)
			{
				var length = 0;

				for (var current = step; current is not null; current = current.Next)
				{
					switch (current.Kind)
					{
						case NamePattern.StepKind.Text:
							length += current.Text.Length;
							break;

						case NamePattern.StepKind.SegmentEnd:
						case NamePattern.StepKind.Arguments:
							return length;

						default:
							return -1;
					}
				}

				return -1;
			}
		}

		// ---- types -----------------------------------------------------------------------------------------------

		public static TypePattern CompileType(TypePatternSyntax syntax)
		{
			RuntimeHelpers.EnsureSufficientExecutionStack();

			var pattern = CompileCore(syntax.Core);

			// Ranks stand in C# order, the outermost first: `int[][,]` is an array of `int[,]`. A `?` closes what
			// stands before it: `int?[]` is an array of nullable int, `int[]?` an array that may be null.
			for (var i = 0; i < syntax.Suffixes.Length;)
			{
				if (syntax.Suffixes[i].IsNullable)
				{
					pattern = new NullableTypePattern(pattern);
					i++;
					continue;
				}

				var end = i;

				while (end < syntax.Suffixes.Length && !syntax.Suffixes[end].IsNullable)
					end++;

				for (var rank = end - 1; rank >= i; rank--)
					pattern = new ArrayTypePattern(pattern, syntax.Suffixes[rank].Rank);

				i = end;
			}

			return pattern;
		}

		static TypePattern CompileCore(TypeCoreSyntax core)
		{
			switch (core)
			{
				case NamedTypeSyntax named:
					return CompileNamedType(named.Name);

				case TupleTypeSyntax tuple:
					return new TupleTypePattern(CompileTypes(tuple.Elements));

				case TypeAlternativesSyntax alternatives:
					return new AlternativesTypePattern(CompileTypes(alternatives.Alternatives));

				case FunctionPointerTypeSyntax pointer:
				{
					var parameters = ImmutableArray.CreateBuilder<(ParameterModifiers?, TypePattern)>(pointer.Parameters.Length);

					foreach (var parameter in pointer.Parameters)
						parameters.Add((parameter.Modifier is { } modifier ? Modifiers(modifier) : null, CompileType(parameter.Type)));

					return new FunctionPointerTypePattern(
						isManaged: pointer.CallingConvention != "unmanaged",
						pointer.UnmanagedConventions,
						parameters.MoveToImmutable());
				}

				default:
					throw new InvalidOperationException("A pattern with an error is not compiled.");
			}
		}

		static ImmutableArray<TypePattern> CompileTypes(ImmutableArray<TypePatternSyntax> types)
		{
			var patterns = ImmutableArray.CreateBuilder<TypePattern>(types.Length);

			foreach (var type in types)
				patterns.Add(CompileType(type));

			return patterns.MoveToImmutable();
		}

		static TypePattern CompileNamedType(NamePatternSyntax name)
		{
			if (TryGetWholeAlternatives(name, out var alternatives))
			{
				var patterns = ImmutableArray.CreateBuilder<TypePattern>(alternatives.Length);

				foreach (var alternative in alternatives)
					patterns.Add(CompileNamedType(alternative));

				return new AlternativesTypePattern(patterns.MoveToImmutable());
			}

			if (name.Global is null && name.Segments.Length == 1 && name.Segments[0] is { IsGlobstar: false, TypeArguments: null } segment && segment.Parts.Length == 1)
			{
				var part = segment.Parts[0];

				if (part.Kind == NamePartKind.Star)
					return AnyTypePattern.Instance;

				// `@int` and `"int"` name a type called int; `int` is System.Int32.
				if (part.Kind == NamePartKind.Text && !part.IsQuoted && part.Span.Length == part.Text!.Length && _aliases.TryGetValue(part.Text, out var clrName))
					return new NamedTypePattern(Alias(clrName));
			}

			return new NamedTypePattern(CompileOneName(name, Anchoring.Trailing));
		}

		static NamePattern Alias(string clrName)
		{
			var accept = new NamePattern.Step(NamePattern.StepKind.Accept, null)                          { Index = 0 };
			var end2   = new NamePattern.Step(NamePattern.StepKind.SegmentEnd, accept)                    { Index = 1 };
			var type   = new NamePattern.Step(NamePattern.StepKind.Text, end2)                            { Index = 2, Text = clrName };
			var end1   = new NamePattern.Step(NamePattern.StepKind.SegmentEnd, type)                      { Index = 3 };
			var system = new NamePattern.Step(NamePattern.StepKind.Text, end1)                            { Index = 4, Text = "System" };

			return new NamePattern(system, 5, isRooted: true, fixedSegments: 2, minSegments: 2, choices: 0);
		}

		// ---- parameters and returns ------------------------------------------------------------------------------

		public static ParameterPattern CompileParameter(ParameterPatternSyntax syntax)
		{
			var modifiers = ParameterModifiers.None;

			foreach (var modifier in syntax.Modifiers)
				modifiers |= Modifiers(modifier);

			return new ParameterPattern(syntax.Kind, modifiers, syntax.Type is null ? null : CompileType(syntax.Type));
		}

		public static ParameterListPattern CompileParameters(ParameterListPatternSyntax syntax)
		{
			var parameters = ImmutableArray.CreateBuilder<ParameterPattern>(syntax.Parameters.Length);

			foreach (var parameter in syntax.Parameters)
				parameters.Add(CompileParameter(parameter));

			return new ParameterListPattern(parameters.MoveToImmutable());
		}

		public static ReturnsPattern CompileReturns(ReturnsPatternSyntax syntax)
		{
			return new ReturnsPattern(syntax.RefKind, CompileType(syntax.Type));
		}

		static ParameterModifiers Modifiers(FilterParameterModifier modifier)
		{
			return modifier switch
			{
				FilterParameterModifier.Ref         => ParameterModifiers.Ref,
				FilterParameterModifier.RefReadonly => ParameterModifiers.RefReadOnly,
				FilterParameterModifier.Out         => ParameterModifiers.Out,
				FilterParameterModifier.In          => ParameterModifiers.In,
				FilterParameterModifier.Params      => ParameterModifiers.Params,
				FilterParameterModifier.This        => ParameterModifiers.This,
				_                                   => throw new ArgumentOutOfRangeException(nameof(modifier), modifier, null),
			};
		}
	}
}
