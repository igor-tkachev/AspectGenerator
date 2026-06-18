using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspectGenerator
{
	static class AspectSymbols
	{
		public static bool IsAspectAttributeName(string name)
		{
			return name is "Aspect" or "AspectAttribute" or "AspectGenerator.Aspect" or "AspectGenerator.AspectAttribute";
		}

		public static bool IsAspectGeneratorOptionsAttributeName(string name)
		{
			return
				name == "AspectGeneratorOptions" ||
				name == "AspectGeneratorOptionsAttribute" ||
				name == "AspectGenerator.AspectGeneratorOptions" ||
				name == "AspectGenerator.AspectGeneratorOptionsAttribute" ||
				name.EndsWith(".AspectGeneratorOptions", StringComparison.Ordinal) ||
				name.EndsWith(".AspectGeneratorOptionsAttribute", StringComparison.Ordinal);
		}

		public static bool IsAspectDefinitionAttribute(AttributeSyntax attribute)
		{
			return IsAspectAttributeName(attribute.Name.ToString());
		}

		public static bool IsAspectDefinition(INamedTypeSymbol type)
		{
			return type.GetAttributes().Any(static attribute => attribute is
			{
				AttributeClass:
				{
					Name: "AspectAttribute",
					ContainingNamespace.Name: "AspectGenerator"
				}
			});
		}

		public static bool IsAspectApplication(AttributeData attribute)
		{
			return attribute.AttributeClass is {} attributeClass && IsAspectDefinition(attributeClass);
		}

		public static bool IsConditionalAspectEnabled(INamedTypeSymbol aspectClass, Compilation compilation, SyntaxTree? applicationSyntaxTree)
		{
			var conditionalSymbols = aspectClass.GetAttributes()
				.Where(static attribute => attribute.AttributeClass is
				{
					Name: "ConditionalAttribute",
					ContainingNamespace:
					{
						Name: "Diagnostics",
						ContainingNamespace:
						{
							Name: "System",
							ContainingNamespace.IsGlobalNamespace: true
						}
					}
				})
				.Select(static attribute => attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string : null)
				.Where(static symbol => !string.IsNullOrWhiteSpace(symbol))
				.ToArray();

			if (conditionalSymbols.Length == 0)
				return true;

			var definedSymbols = new HashSet<string>(StringComparer.Ordinal);

			if (applicationSyntaxTree?.Options is CSharpParseOptions applicationOptions)
				foreach (var symbol in applicationOptions.PreprocessorSymbolNames)
					definedSymbols.Add(symbol);
			else
				foreach (var tree in compilation.SyntaxTrees)
					if (tree.Options is CSharpParseOptions options)
						foreach (var symbol in options.PreprocessorSymbolNames)
							definedSymbols.Add(symbol);

			foreach (var conditionalSymbol in conditionalSymbols)
				if (definedSymbols.Contains(conditionalSymbol!))
					return true;

			return false;
		}

		public static bool HasNamedArgument(AttributeSyntax attribute, string name)
		{
			return attribute.ArgumentList?.Arguments.Any(a => a.NameEquals?.Name.Identifier.ValueText == name) == true;
		}

		public static object? GetAttributeArgumentValue(ExpressionSyntax expression, SemanticModel semanticModel)
		{
			if (expression is ImplicitArrayCreationExpressionSyntax { Initializer.Expressions: var implicitValues })
				return implicitValues.Select(e => semanticModel.GetConstantValue(e).Value).ToArray();

			if (expression is ArrayCreationExpressionSyntax { Initializer.Expressions: var arrayValues })
				return arrayValues.Select(e => semanticModel.GetConstantValue(e).Value).ToArray();

			if (expression is CollectionExpressionSyntax { Elements: var elements })
				return elements
					.OfType<ExpressionElementSyntax>()
					.Select(e => semanticModel.GetConstantValue(e.Expression).Value)
					.ToArray();

			return semanticModel.GetConstantValue(expression).Value;
		}
	}
}
