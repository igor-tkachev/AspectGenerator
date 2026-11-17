using System;
using System.Collections.Generic;
using System.Collections.Immutable;

using AspectGenerator.FilterV2;
using AspectGenerator.TargetFilterFacts;

using Microsoft.CodeAnalysis.Text;

namespace AspectGenerator.TargetFilterBinding
{
	/// <summary>How the modifiers of a parameter pattern are matched.</summary>
	enum ParameterModifierMatching
	{
		/// <summary>
		/// A pattern written with modifiers matches a parameter with exactly those: <c>ref int</c> is no
		/// <c>this ref int</c>. A pattern without modifiers matches any.
		/// </summary>
		ExactSet,

		/// <summary>
		/// Each kind of modifier on its own: how the parameter is passed (<c>ref</c>, <c>ref readonly</c>, <c>in</c>,
		/// <c>out</c>) is matched exactly when the pattern writes one and is free when it does not; <c>this</c> and
		/// <c>params</c> are required when written and free when not. <c>ref int</c> matches <c>this ref int</c>.
		/// </summary>
		PerKind,
	}

	static class PatternDefaults
	{
		/// <summary>
		/// How parameter modifiers are matched: what a pattern writes is required, what it does not write is free, and
		/// how a parameter is passed is matched exactly where the pattern says how. This is the one place that chooses
		/// it; the tests run the other choice as well.
		/// </summary>
		public const ParameterModifierMatching ModifierMatching = ParameterModifierMatching.PerKind;
	}

	/// <summary>A pattern the language refuses for what it is as a whole: where, under which id, and what to write.</summary>
	sealed class PatternException : Exception
	{
		public PatternException(string id, TextSpan span, string message)
			: base(message)
		{
			Id   = id;
			Span = span;
		}

		public string   Id   { get; }
		public TextSpan Span { get; }
	}

	/// <summary>
	/// Compiles the patterns of a filter's syntax tree into matchers. It reads trees without errors only: a tree with
	/// an error node is never compiled, because the filter it belongs to selects nothing. What it refuses itself it
	/// throws as a <see cref="PatternException"/>, which the reader of the block reports.
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

		/// <summary>
		/// The wildcards and alternatives one pattern may hold. A match costs the length of the pattern times the
		/// length of the name, call by call: a pattern past this is refused where it is written.
		/// </summary>
		public const int MaxChoices = 256;

		/// <summary>How deep patterns may stand in one another: type arguments, alternatives, tuples.</summary>
		public const int MaxDepth = 32;

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

		static PatternException TooDeep(TextSpan span)
		{
			return new PatternException(FilterV2DiagnosticID.NestedTooDeeply, span, $"The pattern is nested deeper than {MaxDepth} levels.");
		}

		// ---- names -----------------------------------------------------------------------------------------------

		/// <summary>
		/// A dotted name where it stands as a whole value (<c>namespace:</c>, <c>method:</c>, a compact signature).
		/// Alternatives written as the whole name are patterns of their own, each with its own <c>global::</c>.
		/// </summary>
		/// <param name="isMethodPath">The name is the path of a method, whose type part may begin with a C# type name: <c>int.Parse</c>.</param>
		public static NamePatternSet CompileName(NamePatternSyntax syntax, Anchoring anchoring, bool isMethodPath = false)
		{
			var whole    = WholeAlternatives(syntax);
			var patterns = ImmutableArray.CreateBuilder<NamePattern>(whole.Count);

			foreach (var alternative in whole)
				patterns.Add(CompileOneName(alternative, anchoring, isMethodPath, 0));

			return new NamePatternSet(patterns.MoveToImmutable());
		}

		/// <summary>
		/// The patterns a name stands for as a whole: itself, or, where it is <c>{a,b}</c> and nothing else, each of the
		/// alternatives, and theirs in turn where one is again nothing but alternatives.
		/// </summary>
		public static List<NamePatternSyntax> WholeAlternatives(NamePatternSyntax syntax)
		{
			var result  = new List<NamePatternSyntax>();
			var pending = new Stack<(NamePatternSyntax Pattern, int Depth)>();

			pending.Push((syntax, 0));

			while (pending.Count > 0)
			{
				var (pattern, depth) = pending.Pop();

				if (pattern.Global is null &&
					pattern.Segments.Length == 1 &&
					pattern.Segments[0] is { IsGlobstar: false, TypeArguments: null } segment &&
					segment.Parts.Length == 1 &&
					segment.Parts[0].Kind == NamePartKind.Alternatives)
				{
					if (depth >= MaxDepth)
						throw TooDeep(pattern.Span);

					// Pushed last to first, so that they come off in the order they are written.
					for (var i = segment.Parts[0].Alternatives.Length - 1; i >= 0; i--)
						pending.Push((segment.Parts[0].Alternatives[i], depth + 1));
				}
				else
				{
					result.Add(pattern);
				}
			}

			return result;
		}

		static NamePattern CompileOneName(NamePatternSyntax syntax, Anchoring anchoring, bool isMethodPath, int depth)
		{
			if (IsEmptyName(syntax))
				return NamePattern.EmptyPath;

			var shape = new Shape();

			shape.Scan(syntax, 0);

			if (shape.Choices > MaxChoices)
			{
				throw new PatternException(FilterV2DiagnosticID.NestedTooDeeply, syntax.Span,
					$"The pattern holds {shape.Choices} wildcards and alternatives; a pattern holds {MaxChoices} at most.");
			}

			// `int.Parse`: the type a method path begins with, by its C# name.
			var alias    = isMethodPath && syntax.Global is null && syntax.Segments.Length >= 2 ? AliasOf(syntax.Segments[0]) : null;
			var isRooted = syntax.Global is not null || anchoring == Anchoring.Rooted || alias is not null;
			var first    = alias is null ? 0 : 1;

			if (!shape.HasAlternatives && !(shape.Globstars > 0 && shape.HasArguments))
			{
				var segments = ImmutableArray.CreateBuilder<NamePattern.Segment>(syntax.Segments.Length + first);

				if (alias is not null)
				{
					segments.Add(new NamePattern.Segment(new SegmentGlob(ImmutableArray.Create("System")), default));
					segments.Add(new NamePattern.Segment(new SegmentGlob(ImmutableArray.Create(alias)), default));
				}

				for (var i = first; i < syntax.Segments.Length; i++)
					segments.Add(SimpleSegment(syntax.Segments[i], depth));

				return new NamePattern(segments.ToImmutable(), isRooted);
			}

			var builder = new Builder(depth);
			var accept  = builder.Add(NamePattern.StepKind.Accept, null);
			var step    = accept;

			for (var i = syntax.Segments.Length - 1; i >= first; i--)
			{
				var segment = syntax.Segments[i];

				step = segment.IsGlobstar
					? builder.Add(NamePattern.StepKind.Globstar, step)
					: builder.Segment(segment, builder.Add(NamePattern.StepKind.SegmentEnd, step), 0);
			}

			if (alias is not null)
			{
				step = builder.Text(alias, builder.Add(NamePattern.StepKind.SegmentEnd, step));
				step = builder.Text("System", builder.Add(NamePattern.StepKind.SegmentEnd, step));
			}

			var isFixed = shape.Globstars == 0 && !builder.AlternativesSpanSegments;

			return new NamePattern(
				step,
				accept,
				builder.Steps(),
				isRooted,
				fixedSegments: isFixed ? syntax.Segments.Length + first : -1,
				minSegments: syntax.Segments.Length - shape.Globstars + first);
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

		/// <summary>The System type a segment names with C#'s word for it; <c>@int</c> and <c>"int"</c> name a type called int.</summary>
		static string? AliasOf(NameSegmentSyntax segment)
		{
			if (segment is { IsGlobstar: false, TypeArguments: null } && segment.Parts.Length == 1)
			{
				var part = segment.Parts[0];

				if (part.Kind == NamePartKind.Text && !part.IsQuoted && part.Span.Length == part.Text!.Length && _aliases.TryGetValue(part.Text, out var clrName))
					return clrName;
			}

			return null;
		}

		/// <summary>What a name holds, read off its syntax before it is compiled.</summary>
		sealed class Shape
		{
			public int  Choices;
			public int  Globstars;
			public bool HasAlternatives;
			public bool HasArguments;

			public void Scan(NamePatternSyntax pattern, int depth)
			{
				if (depth > MaxDepth)
					throw TooDeep(pattern.Span);

				foreach (var segment in pattern.Segments)
				{
					if (segment.IsGlobstar)
					{
						Choices++;

						if (depth == 0)
							Globstars++;

						continue;
					}

					HasArguments |= segment.TypeArguments is not null;

					foreach (var part in segment.Parts)
					{
						if (part.Kind == NamePartKind.Star)
						{
							Choices++;
						}
						else if (part.Kind == NamePartKind.Alternatives)
						{
							Choices++;
							HasAlternatives = true;

							foreach (var alternative in part.Alternatives)
								Scan(alternative, depth + 1);
						}
					}
				}
			}
		}

		static NamePattern.Segment SimpleSegment(NameSegmentSyntax segment, int depth)
		{
			if (segment.IsGlobstar)
				return new NamePattern.Segment(null, default);

			var pieces = ImmutableArray.CreateBuilder<string>();
			var piece  = "";

			foreach (var part in segment.Parts)
			{
				switch (part.Kind)
				{
					case NamePartKind.Text:
						piece += part.Text;
						break;

					case NamePartKind.Star:
						pieces.Add(piece);
						piece = "";
						break;

					default:
						throw new InvalidOperationException("A pattern with an error is not compiled.");
				}
			}

			pieces.Add(piece);

			return new NamePattern.Segment(new SegmentGlob(pieces.ToImmutable()), Arguments(segment.TypeArguments, depth));
		}

		static ImmutableArray<TypePattern> Arguments(TypeArgumentsSyntax? typeArguments, int depth)
		{
			if (typeArguments is null)
				return default;

			var arguments = ImmutableArray.CreateBuilder<TypePattern>(typeArguments.Arguments.Length);

			foreach (var argument in typeArguments.Arguments)
				arguments.Add(CompileType(argument, depth + 1));

			return arguments.MoveToImmutable();
		}

		sealed class Builder(int depth)
		{
			readonly List<NamePattern.Step> _steps = [];

			public bool AlternativesSpanSegments;

			public NamePattern.Step[] Steps()
			{
				return _steps.ToArray();
			}

			public NamePattern.Step Add(NamePattern.StepKind kind, NamePattern.Step? next)
			{
				var step = new NamePattern.Step(kind, next, _steps.Count);

				_steps.Add(step);

				return step;
			}

			/// <summary>The steps of literal text, ending in <paramref name="next"/>.</summary>
			public NamePattern.Step Text(string text, NamePattern.Step next)
			{
				var step = next;

				for (var i = text.Length - 1; i >= 0; i--)
				{
					step      = Add(NamePattern.StepKind.Char, step);
					step.Char = text[i];
				}

				return step;
			}

			/// <summary>The steps of one segment's parts and type arguments, ending in <paramref name="next"/>.</summary>
			public NamePattern.Step Segment(NameSegmentSyntax segment, NamePattern.Step next, int nesting)
			{
				var step = next;

				if (Arguments(segment.TypeArguments, depth + nesting) is { IsDefault: false } arguments)
				{
					step           = Add(NamePattern.StepKind.Arguments, step);
					step.Arguments = arguments;
				}

				for (var i = segment.Parts.Length - 1; i >= 0; i--)
				{
					var part = segment.Parts[i];

					switch (part.Kind)
					{
						case NamePartKind.Text:
							step = Text(part.Text!, step);
							break;

						case NamePartKind.Star:
							// Two stars with nothing between them are one.
							if (step.Kind != NamePattern.StepKind.Star)
								step = Add(NamePattern.StepKind.Star, step);

							break;

						case NamePartKind.Alternatives:
						{
							var branches = ImmutableArray.CreateBuilder<NamePattern.Step>(part.Alternatives.Length);

							foreach (var alternative in part.Alternatives)
								branches.Add(Branch(alternative, step, nesting + 1));

							step          = Add(NamePattern.StepKind.Alternatives, step);
							step.Branches = branches.MoveToImmutable();
							break;
						}

						default:
							throw new InvalidOperationException("A pattern with an error is not compiled.");
					}
				}

				return step;
			}

			/// <summary>
			/// An alternative inside a name: its own segments, the last of which goes on with what follows the braces. It
			/// is a piece of a longer name, so it cannot be rooted and cannot stand for whole segments.
			/// </summary>
			NamePattern.Step Branch(NamePatternSyntax alternative, NamePattern.Step next, int nesting)
			{
				// Shape.Scan has refused a pattern nested deeper than MaxDepth, so this recursion is no deeper.
				if (alternative.Global is { } global)
				{
					throw new PatternException(FilterV2DiagnosticID.InvalidPattern, global,
						"'global::' roots a whole pattern: it stands at the start of the pattern, or of an alternative where the braces are the whole pattern ('{global::A.B, C}').");
				}

				var step = next;

				if (alternative.Segments.Length != 1)
					AlternativesSpanSegments = true;

				for (var i = alternative.Segments.Length - 1; i >= 0; i--)
				{
					var segment = alternative.Segments[i];
					var isLast  = i == alternative.Segments.Length - 1;

					if (segment.IsGlobstar)
					{
						throw new PatternException(FilterV2DiagnosticID.InvalidPattern, segment.Span,
							"'**' stands for whole segments and cannot be an alternative inside a name; write the alternatives as whole patterns ('{**.A, B}').");
					}

					step = Segment(segment, isLast ? step : Add(NamePattern.StepKind.SegmentEnd, step), nesting);
				}

				return step;
			}
		}

		// ---- types -----------------------------------------------------------------------------------------------

		public static TypePattern CompileType(TypePatternSyntax syntax)
		{
			return CompileType(syntax, 0);
		}

		static TypePattern CompileType(TypePatternSyntax syntax, int depth)
		{
			// Every rank and the `?` wrap the type once more, as a type argument does: a pattern is matched by following
			// its wrappers one into the other.
			depth += syntax.Suffixes.Length;

			if (depth > MaxDepth)
				throw TooDeep(syntax.Span);

			var pattern = CompileCore(syntax.Core, depth);

			// Ranks stand in C# order, the outermost first: `int[][,]` is an array of `int[,]`. A `?` closes what stands
			// before it, as it does for the compiler: `int?[]` is an array of nullable int, and `int[]?[,]` an array of
			// two dimensions of `int[]`. Right after the type it makes the type nullable; after a rank it says only that
			// the array may be null, which no pattern looks at.
			for (var i = 0; i < syntax.Suffixes.Length;)
			{
				if (syntax.Suffixes[i].IsNullable)
				{
					if (i == 0)
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

		static TypePattern CompileCore(TypeCoreSyntax core, int depth)
		{
			switch (core)
			{
				case NamedTypeSyntax named:
					return CompileNamedType(named.Name, depth);

				case TupleTypeSyntax tuple:
					return new TupleTypePattern(CompileTypes(tuple.Elements, depth));

				case TypeAlternativesSyntax alternatives:
					return new AlternativesTypePattern(CompileTypes(alternatives.Alternatives, depth));

				case FunctionPointerTypeSyntax pointer:
				{
					var parameters = ImmutableArray.CreateBuilder<(ParameterModifiers?, TypePattern)>(pointer.Parameters.Length);

					foreach (var parameter in pointer.Parameters)
						parameters.Add((parameter.Modifier is { } modifier ? Modifiers(modifier) : null, CompileType(parameter.Type, depth + 1)));

					return new FunctionPointerTypePattern(
						isManaged: pointer.CallingConvention != "unmanaged",
						pointer.UnmanagedConventions,
						parameters.MoveToImmutable());
				}

				default:
					throw new InvalidOperationException("A pattern with an error is not compiled.");
			}
		}

		static ImmutableArray<TypePattern> CompileTypes(ImmutableArray<TypePatternSyntax> types, int depth)
		{
			var patterns = ImmutableArray.CreateBuilder<TypePattern>(types.Length);

			foreach (var type in types)
				patterns.Add(CompileType(type, depth + 1));

			return patterns.MoveToImmutable();
		}

		static TypePattern CompileNamedType(NamePatternSyntax name, int depth)
		{
			var whole = WholeAlternatives(name);

			if (whole.Count == 1)
				return CompileOneNamedType(whole[0], depth);

			var patterns = ImmutableArray.CreateBuilder<TypePattern>(whole.Count);

			foreach (var alternative in whole)
				patterns.Add(CompileOneNamedType(alternative, depth));

			return new AlternativesTypePattern(patterns.MoveToImmutable());
		}

		static TypePattern CompileOneNamedType(NamePatternSyntax name, int depth)
		{
			var named = new NamedTypePattern(CompileOneName(name, Anchoring.Trailing, false, depth));

			if (name.Global is null && name.Segments.Length == 1 && name.Segments[0] is { IsGlobstar: false, TypeArguments: null } segment && segment.Parts.Length == 1)
			{
				var part = segment.Parts[0];

				if (part.Kind == NamePartKind.Star)
					return AnyTypePattern.Instance;

				if (AliasOf(segment) is { } clrName)
				{
					return new NamedTypePattern(new NamePattern(
						ImmutableArray.Create(
							new NamePattern.Segment(new SegmentGlob(ImmutableArray.Create("System")), default),
							new NamePattern.Segment(new SegmentGlob(ImmutableArray.Create(clrName)), default)),
						isRooted: true));
				}

				// Quoted text alone is also the whole text of a type, which is how a type no pattern spells is named.
				if (part.Kind == NamePartKind.Text && part.IsQuoted)
					return new QuotedTypePattern(part.Text!, named);
			}

			return named;
		}

		// ---- parameters and returns ------------------------------------------------------------------------------

		public static ParameterPattern CompileParameter(ParameterPatternSyntax syntax, ParameterModifierMatching matching = PatternDefaults.ModifierMatching)
		{
			var modifiers = ParameterModifiers.None;

			foreach (var modifier in syntax.Modifiers)
				modifiers |= Modifiers(modifier);

			return new ParameterPattern(syntax.Kind, modifiers, syntax.Type is null ? null : CompileType(syntax.Type), matching);
		}

		public static ParameterListPattern CompileParameters(ParameterListPatternSyntax syntax, ParameterModifierMatching matching = PatternDefaults.ModifierMatching)
		{
			var parameters = ImmutableArray.CreateBuilder<ParameterPattern>(syntax.Parameters.Length);

			foreach (var parameter in syntax.Parameters)
				parameters.Add(CompileParameter(parameter, matching));

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
