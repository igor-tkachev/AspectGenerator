using System.Collections.Generic;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using AspectGenerator.TargetFilterBinding;

namespace AspectGenerator.Selection
{
	/// <summary>
	/// Finds where a diagnostic of a filter stands in the source: the characters of the item it is about inside the string
	/// literal that writes it, so that the diagnostic points at those characters rather than at the whole attribute.
	/// </summary>
	/// <remarks>
	/// The argument of a <c>TargetFilter</c> or <c>DefaultTargetFilter</c> is a constant string or an array of them
	/// (<c>new[] { … }</c>, <c>new string[] { … }</c> or a collection expression), each element read as one item of the
	/// filter. An item is located through <see cref="TargetFilterSourceMap"/>, which traces the characters of a string
	/// literal, an interpolated string of constants, a concatenation or a <c>const</c> declared in the compilation. Where
	/// the characters cannot be traced exactly (a constant of a referenced assembly, <c>nameof</c>, an array that is not
	/// written out in place, an item index the argument does not have) the diagnostic is reported at the fallback, the
	/// attribute.
	/// </remarks>
	static class FilterDiagnosticLocator
	{
		/// <summary>
		/// The location of the characters <paramref name="diagnostic"/> is about in <paramref name="expression"/>, the argument
		/// that writes the filter, or <paramref name="fallback"/> where they cannot be found.
		/// </summary>
		public static Location? Locate(ExpressionSyntax? expression, Compilation compilation, FilterBlockDiagnostic diagnostic, Location? fallback)
		{
			return TryLocate(expression, compilation, diagnostic) ?? fallback;
		}

		static Location? TryLocate(ExpressionSyntax? expression, Compilation compilation, FilterBlockDiagnostic diagnostic)
		{
			if (expression is null || !compilation.ContainsSyntaxTree(expression.SyntaxTree))
				return null;

			var item = GetItem(expression, diagnostic.Item);

			if (item is null || !compilation.ContainsSyntaxTree(item.SyntaxTree))
				return null;

			var value = compilation.GetSemanticModel(item.SyntaxTree).GetConstantValue(item).Value as string;

			if (value is null)
				return null;

			return TargetFilterSourceMap.Create(item, compilation, value)?.Locate(diagnostic.Span);
		}

		/// <summary>The expression that writes item <paramref name="index"/>, or <see langword="null"/> where there is none.</summary>
		static ExpressionSyntax? GetItem(ExpressionSyntax expression, int index)
		{
			var elements = GetElements(expression, out var isArray);

			if (!isArray)
				return index == 0 ? expression : null;

			return elements is not null && index >= 0 && index < elements.Count ? elements[index] : null;
		}

		/// <summary>
		/// The elements of an array written out in place; <see langword="null"/> for one with an element that is not a single
		/// value (a spread), whose items do not stand at the indices of its elements.
		/// </summary>
		static IReadOnlyList<ExpressionSyntax>? GetElements(ExpressionSyntax expression, out bool isArray)
		{
			isArray = true;

			switch (expression)
			{
				case ImplicitArrayCreationExpressionSyntax { Initializer.Expressions: var values }:
					return values;

				case ArrayCreationExpressionSyntax { Initializer.Expressions: var values }:
					return values;

				case CollectionExpressionSyntax { Elements: var elements }:
				{
					var result = new List<ExpressionSyntax>(elements.Count);

					foreach (var element in elements)
					{
						if (element is not ExpressionElementSyntax value)
							return null;

						result.Add(value.Expression);
					}

					return result;
				}

				default:
					isArray = false;
					return null;
			}
		}
	}
}
