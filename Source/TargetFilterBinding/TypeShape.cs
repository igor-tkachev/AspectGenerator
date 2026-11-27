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
			return TypeShapeText.Write(this)!;
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

		/// <summary>
		/// A type parameter stands somewhere in the type: on the definition of a method the type is then another in
		/// every call. Known when the shape is made, so that asking does not follow the type down.
		/// </summary>
		public abstract bool MentionsTypeParameter { get; }
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

			foreach (var segment in segments)
				foreach (var argument in segment.TypeArguments)
					MentionsTypeParameter |= argument.MentionsTypeParameter;
		}

		public override bool MentionsTypeParameter { get; }

		public ImmutableArray<NameSegment> Segments { get; }

		public override bool IsReferenceType { get; }

		/// <summary>The <c>T</c> of <c>System.Nullable&lt;T&gt;</c>; <see langword="null"/> for any other type.</summary>
		public TypeShape? NullableUnderlying { get; }

		/// <summary>The elements of a tuple, however long (the nesting past seven is not seen); default for a type that is no tuple.</summary>
		public ImmutableArray<TypeShape> TupleElements { get; }

		public override string ToString()
		{
			return TypeShapeText.Write(this)!;
		}
	}

	sealed class ArrayTypeShape : TypeShape
	{
		public ArrayTypeShape(TypeShape element, int rank)
		{
			Element               = element;
			Rank                  = rank;
			MentionsTypeParameter = element.MentionsTypeParameter;
		}

		public override bool MentionsTypeParameter { get; }

		public TypeShape Element { get; }
		public int       Rank    { get; }

		public override bool IsReferenceType => true;

		public override string ToString()
		{
			return TypeShapeText.Write(this)!;
		}
	}

	/// <summary><c>T*</c>. No pattern but <c>*</c> matches a pointer.</summary>
	sealed class PointerTypeShape : TypeShape
	{
		public PointerTypeShape(TypeShape pointedAt)
		{
			PointedAt             = pointedAt;
			MentionsTypeParameter = pointedAt.MentionsTypeParameter;
		}

		public TypeShape PointedAt { get; }

		public override bool MentionsTypeParameter { get; }

		public override string ToString()
		{
			return TypeShapeText.Write(this)!;
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

		public override bool MentionsTypeParameter => true;

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

			MentionsTypeParameter = @return.Type.MentionsTypeParameter;

			foreach (var parameter in parameters)
				MentionsTypeParameter |= parameter.Type.MentionsTypeParameter;
		}

		public override bool MentionsTypeParameter { get; }

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
			return TypeShapeText.Write(this)!;
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
			return TypeShapeText.Write(this)!;
		}
	}

	/// <summary>
	/// The text of a type as C# writes it, without spaces after commas. A type is nested as deep as its source nests it,
	/// so the text is written from a stack of what is still to write and not by calls that follow the type down.
	/// </summary>
	static class TypeShapeText
	{
		/// <summary>The text of a shape, of a name segment or of a method; <see langword="null"/> where it is longer than <paramref name="limit"/>.</summary>
		public static string? Write(object root, int limit = int.MaxValue)
		{
			var text    = new System.Text.StringBuilder();
			var pending = new System.Collections.Generic.Stack<object>();

			pending.Push(root);

			while (pending.Count > 0)
			{
				switch (pending.Pop())
				{
					case string literal:
						text.Append(literal);
						break;

					case NameSegment segment:
						text.Append(segment.Name);

						if (!segment.TypeArguments.IsEmpty)
						{
							pending.Push(">");
							PushList(pending, segment.TypeArguments);
							text.Append('<');
						}

						break;

					case NamedTypeShape named:
						for (var i = named.Segments.Length - 1; i >= 0; i--)
						{
							pending.Push(named.Segments[i]);

							if (i > 0)
								pending.Push(".");
						}

						break;

					case ArrayTypeShape array:
					{
						// The rank specifiers follow the element type in C# order: an array of int[,] is int[][,].
						var specifiers    = new System.Text.StringBuilder();
						TypeShape current = array;

						while (current is ArrayTypeShape inner)
						{
							specifiers.Append('[').Append(',', inner.Rank - 1).Append(']');
							current = inner.Element;
						}

						pending.Push(specifiers.ToString());
						pending.Push(current);
						break;
					}

					case PointerTypeShape pointer:
					{
						var stars         = 0;
						TypeShape current = pointer;

						while (current is PointerTypeShape inner)
						{
							stars++;
							current = inner.PointedAt;
						}

						pending.Push(new string('*', stars));
						pending.Push(current);
						break;
					}

					case TypeParameterShape parameter:
						text.Append(parameter.Name);
						break;

					case FunctionPointerTypeShape function:
					{
						text.Append("delegate*");

						if (!function.IsManaged)
							text.Append(function.Conventions.IsEmpty ? " unmanaged" : $" unmanaged[{string.Join(",", function.Conventions)}]");

						text.Append('<');

						// The parameters and, last, the return, each after the one before and a comma.
						pending.Push(">");
						PushParameter(pending, function.Return);

						for (var i = function.Parameters.Length - 1; i >= 0; i--)
						{
							pending.Push(",");
							PushParameter(pending, function.Parameters[i]);
						}

						break;
					}

					case MethodShape method:
						pending.Push(method.Method);
						pending.Push(".");
						pending.Push(method.ContainingType);
						break;
				}

				if (text.Length > limit)
					return null;
			}

			return text.ToString();
		}

		static void PushList(System.Collections.Generic.Stack<object> pending, ImmutableArray<TypeShape> types)
		{
			for (var i = types.Length - 1; i >= 0; i--)
			{
				pending.Push(types[i]);

				if (i > 0)
					pending.Push(",");
			}
		}

		static void PushParameter(System.Collections.Generic.Stack<object> pending, FunctionPointerParameterShape parameter)
		{
			pending.Push(parameter.Type);

			pending.Push(parameter.Modifiers switch
			{
				ParameterModifiers.Ref         => "ref ",
				ParameterModifiers.RefReadOnly => "ref readonly ",
				ParameterModifiers.In          => "in ",
				ParameterModifiers.Out         => "out ",
				_                              => "",
			});
		}
	}
}
