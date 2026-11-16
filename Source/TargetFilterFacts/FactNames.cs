using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;

namespace AspectGenerator.TargetFilterFacts
{
	/// <summary>
	/// The text of types and methods that location, method and type conditions are matched against. Type arguments are
	/// written as C# writes them without spaces (<c>List&lt;System.Int32&gt;</c>), arrays carry their rank in C# order
	/// (<c>int[][,]</c>), and a type parameter is its name.
	/// </summary>
	static class FactNames
	{
		public static string FormatNamespace(INamespaceSymbol @namespace)
		{
			return @namespace.IsGlobalNamespace ? "" : @namespace.ToDisplayString();
		}

		public static string FormatMethodName(IMethodSymbol method)
		{
			if (method.TypeArguments.Length == 0)
				return method.Name;

			return $"{method.Name}<{string.Join(",", method.TypeArguments.Select(FormatType))}>";
		}

		public static string FormatType(ITypeSymbol type)
		{
			if (type is IArrayTypeSymbol arrayType)
				return FormatArrayType(arrayType);

			if (type is ITypeParameterSymbol typeParameter)
				return typeParameter.Name;

			if (type is INamedTypeSymbol namedType)
				return FormatNamedType(namedType);

			return type.ToDisplayString(
				new SymbolDisplayFormat(
					typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
					genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
					miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers));
		}

		// The rank specifiers follow the element type in C# order: an array of int[,] is int[][,].
		static string FormatArrayType(IArrayTypeSymbol arrayType)
		{
			var specifiers      = new StringBuilder();
			ITypeSymbol current = arrayType;

			while (current is IArrayTypeSymbol array)
			{
				specifiers.Append('[').Append(',', array.Rank - 1).Append(']');
				current = array.ElementType;
			}

			return FormatType(current) + specifiers;
		}

		static string FormatNamedType(INamedTypeSymbol type)
		{
			var name               = GetNameWithoutArity(type.Name);
			var typeParameterCount = type.TypeParameters.Length;

			if (typeParameterCount > 0)
			{
				var typeArguments = type.TypeArguments.Length > typeParameterCount
					? type.TypeArguments.Skip(type.TypeArguments.Length - typeParameterCount)
					: type.TypeArguments;

				name += $"<{string.Join(",", typeArguments.Select(FormatType))}>";
			}

			if (type.ContainingType is not null)
				return $"{FormatNamedType(type.ContainingType)}.{name}";

			if (type.ContainingNamespace.IsGlobalNamespace)
				return name;

			return $"{type.ContainingNamespace.ToDisplayString()}.{name}";
		}

		static string GetNameWithoutArity(string name)
		{
			var tick = name.IndexOf('`');

			return tick >= 0 ? name[..tick] : name;
		}
	}
}
