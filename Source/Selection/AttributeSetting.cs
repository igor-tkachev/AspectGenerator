using System;
using System.Collections.Immutable;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AspectGenerator.Selection
{
	/// <summary>
	/// The value of one setting of an aspect attribute as the cascade compares it: the value of a named argument, or all
	/// constructor arguments together with the constructor they go to. Two values are equal when an attribute built from
	/// either would be built the same way: the same constructor, and arguments of the same kind, type and value, element by
	/// element for arrays (<see cref="TypedConstants"/>). So <c>1</c> and <c>1L</c> differ, and so do <c>new int[] { 1 }</c>
	/// and <c>new object[] { 1 }</c>, and so do two constructor overloads given the same arguments.
	/// </summary>
	sealed class AttributeSetting : IEquatable<AttributeSetting>
	{
		AttributeSetting(IMethodSymbol? constructor, ImmutableArray<TypedConstant> values)
		{
			Constructor = constructor;
			Values      = values;
		}

		/// <summary>The constructor of the constructor arguments; <see langword="null"/> for a named argument.</summary>
		public IMethodSymbol? Constructor { get; }

		/// <summary>The constructor arguments, or the one value of a named argument.</summary>
		public ImmutableArray<TypedConstant> Values { get; }

		public static AttributeSetting ForConstructor(IMethodSymbol constructor, ImmutableArray<TypedConstant> arguments)
		{
			return new AttributeSetting(constructor, arguments);
		}

		public static AttributeSetting ForNamedArgument(TypedConstant value)
		{
			return new AttributeSetting(null, [value]);
		}

		public bool Equals(AttributeSetting? other)
		{
			return
				other is not null &&
				SymbolEqualityComparer.Default.Equals(Constructor, other.Constructor) &&
				TypedConstants.SequenceEqual(Values, other.Values);
		}

		public override bool Equals(object? obj)
		{
			return Equals(obj as AttributeSetting);
		}

		public override int GetHashCode()
		{
			return unchecked(SymbolEqualityComparer.Default.GetHashCode(Constructor) * 31 + TypedConstants.GetHashCode(Values));
		}

		public override string ToString()
		{
			return Constructor is null
				? Values[0].ToCSharpString()
				: "(" + string.Join(", ", Values.Select(static v => v.ToCSharpString())) + ")";
		}
	}

	/// <summary>
	/// Compares attribute argument values as the attribute would be built from them: the same kind, the same type, and the
	/// same value; an array element by element, compared the same way, and a <c>typeof</c> by its type symbol.
	/// <see cref="TypedConstant"/>'s own equality compares the elements of an array by the identity of the array that holds
	/// them, so two reads of one argument differ.
	/// </summary>
	static class TypedConstants
	{
		public static bool Equals(TypedConstant x, TypedConstant y)
		{
			if (x.Kind != y.Kind || !SymbolEqualityComparer.Default.Equals(x.Type, y.Type) || x.IsNull != y.IsNull)
				return false;

			if (x.IsNull)
				return true;

			return x.Kind switch
			{
				TypedConstantKind.Array => SequenceEqual(x.Values, y.Values),
				TypedConstantKind.Type  => SymbolEqualityComparer.Default.Equals(x.Value as ISymbol, y.Value as ISymbol),
				_                       => object.Equals(x.Value, y.Value),
			};
		}

		public static bool SequenceEqual(ImmutableArray<TypedConstant> x, ImmutableArray<TypedConstant> y)
		{
			if (x.IsDefault || y.IsDefault)
				return x.IsDefault == y.IsDefault;

			if (x.Length != y.Length)
				return false;

			for (var i = 0; i < x.Length; i++)
				if (!Equals(x[i], y[i]))
					return false;

			return true;
		}

		public static int GetHashCode(TypedConstant value)
		{
			var hash = unchecked((int)value.Kind * 31 + SymbolEqualityComparer.Default.GetHashCode(value.Type));

			if (value.IsNull)
				return hash;

			return unchecked(hash * 31 + value.Kind switch
			{
				TypedConstantKind.Array => GetHashCode(value.Values),
				TypedConstantKind.Type  => SymbolEqualityComparer.Default.GetHashCode(value.Value as ISymbol),
				_                       => value.Value?.GetHashCode() ?? 0,
			});
		}

		public static int GetHashCode(ImmutableArray<TypedConstant> values)
		{
			if (values.IsDefault)
				return 0;

			var hash = values.Length;

			foreach (var value in values)
				hash = unchecked(hash * 31 + GetHashCode(value));

			return hash;
		}
	}
}
