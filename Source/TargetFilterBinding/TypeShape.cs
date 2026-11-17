using System.Collections.Immutable;

using AspectGenerator.TargetFilterFacts;

namespace AspectGenerator.TargetFilterBinding
{
	// The structure of types and names that filter patterns are matched against. It is what a pattern can see of a
	// type and nothing more, so the same matcher runs over the symbols of a compilation and over records written by
	// hand in a test table.

	/// <summary>
	/// One segment of a dotted name: a namespace, a type with the type arguments of its own nesting level, or a method
	/// with its type arguments.
	/// </summary>
	sealed class NameSegment
	{
		public NameSegment(string name, ImmutableArray<TypeShape> typeArguments = default)
		{
			Name          = name;
			TypeArguments = typeArguments.IsDefault ? ImmutableArray<TypeShape>.Empty : typeArguments;
		}

		public string                    Name          { get; }
		public ImmutableArray<TypeShape> TypeArguments { get; }

		public override string ToString()
		{
			return TypeArguments.IsEmpty ? Name : $"{Name}<{string.Join(",", TypeArguments)}>";
		}
	}

	/// <summary>A type as a pattern sees it.</summary>
	abstract class TypeShape
	{
		/// <summary>
		/// Whether <c>P?</c> matches the type as it matches <c>P</c>: a nullable annotation on a reference type is
		/// ignored.
		/// </summary>
		public virtual bool IsReferenceType => false;
	}

	/// <summary>
	/// A type with a name: its namespaces, its outer types and itself, each a segment. <c>Nullable&lt;T&gt;</c> and
	/// tuples are named types that also say what they wrap.
	/// </summary>
	sealed class NamedTypeShape : TypeShape
	{
		public NamedTypeShape(
			ImmutableArray<NameSegment> segments,
			bool                        isReferenceType,
			TypeShape?                  nullableUnderlying = null,
			ImmutableArray<TypeShape>   tupleElements      = default)
		{
			Segments           = segments;
			IsReferenceType    = isReferenceType;
			NullableUnderlying = nullableUnderlying;
			TupleElements      = tupleElements;
		}

		public ImmutableArray<NameSegment> Segments { get; }

		public override bool IsReferenceType { get; }

		/// <summary>The <c>T</c> of <c>System.Nullable&lt;T&gt;</c>; <see langword="null"/> for any other type.</summary>
		public TypeShape? NullableUnderlying { get; }

		/// <summary>The elements of a tuple, however long (the nesting past seven is not seen); default for a type that is no tuple.</summary>
		public ImmutableArray<TypeShape> TupleElements { get; }

		public override string ToString()
		{
			return string.Join(".", Segments);
		}
	}

	sealed class ArrayTypeShape : TypeShape
	{
		public ArrayTypeShape(TypeShape element, int rank)
		{
			Element = element;
			Rank    = rank;
		}

		public TypeShape Element { get; }
		public int       Rank    { get; }

		public override bool IsReferenceType => true;

		public override string ToString()
		{
			// The rank specifiers follow the element type in C# order: an array of int[,] is int[][,].
			var specifiers    = "";
			TypeShape current = this;

			while (current is ArrayTypeShape array)
			{
				specifiers += "[" + new string(',', array.Rank - 1) + "]";
				current     = array.Element;
			}

			return current + specifiers;
		}
	}

	/// <summary><c>T*</c>. No pattern but <c>*</c> matches a pointer.</summary>
	sealed class PointerTypeShape : TypeShape
	{
		public PointerTypeShape(TypeShape pointedAt)
		{
			PointedAt = pointedAt;
		}

		public TypeShape PointedAt { get; }

		public override string ToString()
		{
			return PointedAt + "*";
		}
	}

	/// <summary>
	/// A type parameter that nothing has replaced. In the definition of a method it stands for what each call puts
	/// there; in a call it is a type parameter of the calling code. Only <c>*</c> matches it for sure.
	/// </summary>
	sealed class TypeParameterShape : TypeShape
	{
		public TypeParameterShape(string name)
		{
			Name = name;
		}

		public string Name { get; }

		public override string ToString()
		{
			return Name;
		}
	}

	/// <summary>A parameter or the return of a function pointer.</summary>
	readonly struct FunctionPointerParameterShape
	{
		public FunctionPointerParameterShape(TypeShape type, ParameterModifiers modifiers)
		{
			Type      = type;
			Modifiers = modifiers;
		}

		public TypeShape          Type      { get; }

		/// <summary><c>ref</c>, <c>ref readonly</c>, <c>in</c> or <c>out</c>; a return by reference is <c>ref</c> or <c>ref readonly</c>.</summary>
		public ParameterModifiers Modifiers { get; }
	}

	/// <summary><c>delegate* unmanaged[Cdecl]&lt;int, void&gt;</c>.</summary>
	sealed class FunctionPointerTypeShape : TypeShape
	{
		public FunctionPointerTypeShape(
			bool                                          isManaged,
			ImmutableArray<string>                        conventions,
			ImmutableArray<FunctionPointerParameterShape> parameters,
			FunctionPointerParameterShape                 @return)
		{
			IsManaged   = isManaged;
			Conventions = conventions;
			Parameters  = parameters;
			Return      = @return;
		}

		public bool IsManaged { get; }

		/// <summary>
		/// The calling conventions of an unmanaged pointer as C# writes them in <c>unmanaged[…]</c> (<c>Cdecl</c>,
		/// <c>SuppressGCTransition</c>); empty for <c>unmanaged</c> alone.
		/// </summary>
		public ImmutableArray<string> Conventions { get; }

		public ImmutableArray<FunctionPointerParameterShape> Parameters { get; }
		public FunctionPointerParameterShape                 Return     { get; }

		public override string ToString()
		{
			return "delegate*";
		}
	}

	/// <summary>A parameter of a method.</summary>
	readonly struct ParameterShape
	{
		public ParameterShape(TypeShape type, ParameterModifiers modifiers)
		{
			Type      = type;
			Modifiers = modifiers;
		}

		public TypeShape          Type      { get; }
		public ParameterModifiers Modifiers { get; }
	}

	/// <summary>A method by its path: the containing type and the method's own segment.</summary>
	readonly struct MethodShape
	{
		public MethodShape(NamedTypeShape containingType, NameSegment method)
		{
			ContainingType = containingType;
			Method         = method;
		}

		public NamedTypeShape ContainingType { get; }
		public NameSegment    Method         { get; }

		public override string ToString()
		{
			return ContainingType + "." + Method;
		}
	}
}
