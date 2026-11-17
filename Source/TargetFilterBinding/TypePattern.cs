using System;
using System.Collections.Immutable;

using AspectGenerator.FilterV2;
using AspectGenerator.TargetFilterFacts;

namespace AspectGenerator.TargetFilterBinding
{
	/// <summary>
	/// A type pattern, compiled. The relation is structural: a pattern matches a type when it matches its form (a name
	/// with type arguments, an array of a rank, a nullable, a tuple, a function pointer) and each part of it.
	/// <para>
	/// A type parameter left in the type is matched by <c>*</c> alone. On the definition of a method any other pattern
	/// <see cref="Tri.Depends"/> on what a call puts there; in a call the type parameter belongs to the calling code and
	/// the pattern does not match. What is known of the form decides either way: <c>T[]</c> is no <c>List&lt;*&gt;</c>.
	/// </para>
	/// </summary>
	abstract class TypePattern
	{
		/// <remarks>
		/// A pattern matches the patterns inside it by calling them; the compiler of patterns refuses one nested deeper
		/// than <see cref="PatternCompiler.MaxDepth"/>, so the depth of these calls is bounded where the pattern is read.
		/// </remarks>
		public Tri Match(TypeShape type, MatchPhase phase)
		{
			return type is TypeParameterShape ? MatchTypeParameter(phase) : MatchCore(type, phase);
		}

		internal virtual Tri MatchTypeParameter(MatchPhase phase)
		{
			return phase == MatchPhase.Definition ? Tri.Depends : Tri.False;
		}

		internal abstract Tri MatchCore(TypeShape type, MatchPhase phase);

		/// <summary>
		/// Matches an attribute class, by its name as it is or without its <c>Attribute</c> suffix. An attribute class
		/// is never constructed from a type parameter of the method, so the answer is the same in both phases.
		/// </summary>
		public virtual bool MatchAttribute(NamedTypeShape attribute)
		{
			return MatchCore(attribute, MatchPhase.Call) == Tri.True;
		}
	}

	/// <summary><c>*</c>: any type whatever, a type parameter, a pointer and a function pointer included.</summary>
	sealed class AnyTypePattern : TypePattern
	{
		public static readonly AnyTypePattern Instance = new();

		internal override Tri MatchTypeParameter(MatchPhase phase)
		{
			return Tri.True;
		}

		internal override Tri MatchCore(TypeShape type, MatchPhase phase)
		{
			return Tri.True;
		}
	}

	/// <summary>A named type: a dotted pattern over the namespaces, the outer types and the type.</summary>
	sealed class NamedTypePattern : TypePattern
	{
		const string AttributeSuffix = "Attribute";

		readonly NamePattern _name;

		public NamedTypePattern(NamePattern name)
		{
			_name = name;
		}

		internal override Tri MatchCore(TypeShape type, MatchPhase phase)
		{
			return type is NamedTypeShape named ? _name.Match(new SegmentPath(named.Segments), phase) : Tri.False;
		}

		public override bool MatchAttribute(NamedTypeShape attribute)
		{
			if (_name.Match(new SegmentPath(attribute.Segments), MatchPhase.Call) == Tri.True)
				return true;

			if (attribute.Segments.IsEmpty)
				return false;

			var name = attribute.Segments[attribute.Segments.Length - 1].Name;

			return
				name.Length > AttributeSuffix.Length &&
				name.EndsWith(AttributeSuffix, StringComparison.Ordinal) &&
				_name.Match(new SegmentPath(attribute.Segments, null, name.Length - AttributeSuffix.Length), MatchPhase.Call) == Tri.True;
		}
	}

	/// <summary><c>P[,]</c>: an array of that rank whose elements match.</summary>
	sealed class ArrayTypePattern : TypePattern
	{
		readonly TypePattern _element;
		readonly int         _rank;

		public ArrayTypePattern(TypePattern element, int rank)
		{
			_element = element;
			_rank    = rank;
		}

		internal override Tri MatchCore(TypeShape type, MatchPhase phase)
		{
			return type is ArrayTypeShape array && array.Rank == _rank ? _element.Match(array.Element, phase) : Tri.False;
		}
	}

	/// <summary>
	/// <c>P?</c>: <c>Nullable&lt;U&gt;</c> where <c>P</c> matches <c>U</c>, or a reference type that <c>P</c> matches
	/// (its annotation is not looked at). A value type that is not nullable does not match.
	/// </summary>
	sealed class NullableTypePattern : TypePattern
	{
		readonly TypePattern _inner;

		public NullableTypePattern(TypePattern inner)
		{
			_inner = inner;
		}

		internal override Tri MatchCore(TypeShape type, MatchPhase phase)
		{
			if (type is NamedTypeShape { NullableUnderlying: { } underlying })
				return _inner.Match(underlying, phase);

			return type.IsReferenceType ? _inner.Match(type, phase) : Tri.False;
		}
	}

	/// <summary><c>(P1, P2, …)</c>: a tuple of as many elements, each matching; element names are not looked at.</summary>
	sealed class TupleTypePattern : TypePattern
	{
		readonly ImmutableArray<TypePattern> _elements;

		public TupleTypePattern(ImmutableArray<TypePattern> elements)
		{
			_elements = elements;
		}

		internal override Tri MatchCore(TypeShape type, MatchPhase phase)
		{
			if (type is not NamedTypeShape { TupleElements: { IsDefault: false } elements } || elements.Length != _elements.Length)
				return Tri.False;

			var answer = Tri.True;

			for (var i = 0; i < elements.Length && answer != Tri.False; i++)
				answer = answer.And(_elements[i].Match(elements[i], phase));

			return answer;
		}
	}

	/// <summary><c>{P1, P2}</c>: either.</summary>
	sealed class AlternativesTypePattern : TypePattern
	{
		readonly ImmutableArray<TypePattern> _alternatives;

		public AlternativesTypePattern(ImmutableArray<TypePattern> alternatives)
		{
			_alternatives = alternatives;
		}

		internal override Tri MatchTypeParameter(MatchPhase phase)
		{
			var best = Tri.False;

			foreach (var alternative in _alternatives)
				best = best.Or(alternative.MatchTypeParameter(phase));

			return best;
		}

		internal override Tri MatchCore(TypeShape type, MatchPhase phase)
		{
			var best = Tri.False;

			foreach (var alternative in _alternatives)
			{
				best = best.Or(alternative.MatchCore(type, phase));

				if (best == Tri.True)
					break;
			}

			return best;
		}

		public override bool MatchAttribute(NamedTypeShape attribute)
		{
			foreach (var alternative in _alternatives)
				if (alternative.MatchAttribute(attribute))
					return true;

			return false;
		}
	}

	/// <summary>
	/// <c>delegate* unmanaged[Cdecl]&lt;int, void&gt;</c>: a function pointer of the same kind (managed where the pattern
	/// names none, as in C#), with exactly the calling conventions named and as many parameters, each matching; the last
	/// type is the return. A parameter written with <c>ref</c>, <c>ref readonly</c>, <c>in</c> or <c>out</c> matches
	/// that passing only; one written without matches any.
	/// </summary>
	sealed class FunctionPointerTypePattern : TypePattern
	{
		readonly bool                                                           _isManaged;
		readonly ImmutableArray<string>                                         _conventions;
		readonly ImmutableArray<(ParameterModifiers? Modifiers, TypePattern Type)> _parameters;

		/// <param name="parameters">The parameters and, last, the return.</param>
		public FunctionPointerTypePattern(bool isManaged, ImmutableArray<string> conventions, ImmutableArray<(ParameterModifiers? Modifiers, TypePattern Type)> parameters)
		{
			_isManaged   = isManaged;
			_conventions = conventions;
			_parameters  = parameters;
		}

		internal override Tri MatchCore(TypeShape type, MatchPhase phase)
		{
			if (type is not FunctionPointerTypeShape pointer ||
				pointer.IsManaged != _isManaged ||
				pointer.Parameters.Length != _parameters.Length - 1 ||
				!SameConventions(pointer.Conventions))
			{
				return Tri.False;
			}

			var answer = Tri.True;

			for (var i = 0; i < _parameters.Length && answer != Tri.False; i++)
			{
				var shape   = i < pointer.Parameters.Length ? pointer.Parameters[i] : pointer.Return;
				var pattern = _parameters[i];

				if (pattern.Modifiers is { } modifiers && modifiers != shape.Modifiers)
					return Tri.False;

				answer = answer.And(pattern.Type.Match(shape.Type, phase));
			}

			return answer;
		}

		/// <summary>The same names, each as often: the order they are written in says nothing.</summary>
		bool SameConventions(ImmutableArray<string> conventions)
		{
			if (conventions.Length != _conventions.Length)
				return false;

			foreach (var convention in _conventions)
				if (Count(conventions, convention) != Count(_conventions, convention))
					return false;

			return true;

			static int Count(ImmutableArray<string> names, string name)
			{
				var count = 0;

				foreach (var candidate in names)
					if (candidate == name)
						count++;

				return count;
			}
		}
	}

	/// <summary>
	/// Quoted text where a type stands, and nothing else: a type whose name is that text, as quoted text in any name,
	/// or a type whose whole text it is. That names the types no pattern spells: <c>"System.Int32*"</c> is a pointer.
	/// The text of a type is its full name with its type arguments, written as C# writes them without spaces.
	/// </summary>
	sealed class QuotedTypePattern : TypePattern
	{
		readonly string           _text;
		readonly NamedTypePattern _name;

		public QuotedTypePattern(string text, NamedTypePattern name)
		{
			_text = text;
			_name = name;
		}

		internal override Tri MatchCore(TypeShape type, MatchPhase phase)
		{
			var named = _name.MatchCore(type, phase);

			return named == Tri.True || type is NamedTypeShape ? named : (type.ToString() == _text).ToTri();
		}

		public override bool MatchAttribute(NamedTypeShape attribute)
		{
			return _name.MatchAttribute(attribute);
		}
	}

	/// <summary>A parameter pattern: <c>ref readonly int</c>, <c>this IEnumerable&lt;*&gt;</c>, <c>_</c>, <c>..</c>.</summary>
	sealed class ParameterPattern
	{
		const ParameterModifiers Passing = ParameterModifiers.Ref | ParameterModifiers.RefReadOnly | ParameterModifiers.In | ParameterModifiers.Out;

		readonly ParameterModifierMatching _matching;

		public ParameterPattern(ParameterPatternKind kind, ParameterModifiers modifiers, TypePattern? type, ParameterModifierMatching matching = PatternDefaults.ModifierMatching)
		{
			Kind      = kind;
			Modifiers = modifiers;
			Type      = type;
			_matching = matching;
		}

		public ParameterPatternKind Kind      { get; }
		public ParameterModifiers   Modifiers { get; }
		public TypePattern?         Type      { get; }

		/// <summary>A pattern written without modifiers matches any parameter of a matching type; see <see cref="ParameterModifierMatching"/> for one written with.</summary>
		public Tri Match(ParameterShape parameter, MatchPhase phase)
		{
			if (Kind != ParameterPatternKind.Typed)
				return Tri.True;

			if (Modifiers != ParameterModifiers.None)
			{
				if (_matching == ParameterModifierMatching.ExactSet)
				{
					if (Modifiers != parameter.Modifiers)
						return Tri.False;
				}
				else
				{
					// How it is passed, exactly, where the pattern says how; `this` and `params` where the pattern says so.
					if ((Modifiers & Passing) != 0 && (Modifiers & Passing) != (parameter.Modifiers & Passing))
						return Tri.False;

					if ((Modifiers & ~Passing & ~parameter.Modifiers) != 0)
						return Tri.False;
				}
			}

			return Type!.Match(parameter.Type, phase);
		}
	}

	/// <summary><c>(int, .., CancellationToken)</c>: the whole parameter list; <c>..</c> stands for any run, at most once.</summary>
	sealed class ParameterListPattern
	{
		readonly ImmutableArray<ParameterPattern> _parameters;
		readonly int                              _anyRun;

		public ParameterListPattern(ImmutableArray<ParameterPattern> parameters)
		{
			_parameters = parameters;
			_anyRun     = -1;

			for (var i = 0; i < parameters.Length && _anyRun < 0; i++)
				if (parameters[i].Kind == ParameterPatternKind.AnyRun)
					_anyRun = i;
		}

		public Tri Match(ImmutableArray<ParameterShape> parameters, MatchPhase phase)
		{
			if (_anyRun < 0)
				return parameters.Length == _parameters.Length ? MatchRun(parameters, 0, 0, _parameters.Length, phase) : Tri.False;

			var after = _parameters.Length - _anyRun - 1;

			if (parameters.Length < _anyRun + after)
				return Tri.False;

			return MatchRun(parameters, 0, 0, _anyRun, phase).And(MatchRun(parameters, parameters.Length - after, _anyRun + 1, after, phase));
		}

		Tri MatchRun(ImmutableArray<ParameterShape> parameters, int first, int firstPattern, int count, MatchPhase phase)
		{
			var answer = Tri.True;

			for (var i = 0; i < count && answer != Tri.False; i++)
				answer = answer.And(_parameters[firstPattern + i].Match(parameters[first + i], phase));

			return answer;
		}
	}

	/// <summary>The value of <c>returns:</c>: a type, and the way it is returned when the pattern says <c>ref</c> or <c>ref readonly</c>.</summary>
	sealed class ReturnsPattern
	{
		readonly FilterRefKind _refKind;
		readonly TypePattern   _type;

		public ReturnsPattern(FilterRefKind refKind, TypePattern type)
		{
			_refKind = refKind;
			_type    = type;
		}

		public Tri Match(TypeShape type, ReturnRef returnRef, MatchPhase phase)
		{
			var expected = _refKind switch
			{
				FilterRefKind.Ref         => ReturnRef.Ref,
				FilterRefKind.RefReadonly => ReturnRef.RefReadOnly,
				_                         => returnRef,
			};

			return expected == returnRef ? _type.Match(type, phase) : Tri.False;
		}
	}
}
