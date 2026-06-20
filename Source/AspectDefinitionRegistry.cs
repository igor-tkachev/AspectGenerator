using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspectGenerator
{
	sealed class AspectDefinitionRegistry
	{
		readonly Dictionary<ISymbol,(AttributeSyntax Syntax,SemanticModel SemanticModel)> _aspectAttributes;

		AspectDefinitionRegistry(Dictionary<ISymbol,(AttributeSyntax Syntax,SemanticModel SemanticModel)> aspectAttributes)
		{
			_aspectAttributes = aspectAttributes;
		}

		public Dictionary<ISymbol,(AttributeSyntax Syntax,SemanticModel SemanticModel)> AspectAttributes => _aspectAttributes;

		public static AspectDefinitionRegistry Create(
			Compilation                          compilation,
			ImmutableArray<ClassDeclarationSyntax> aspectDeclarations,
			IAspectDiagnosticSink                diagnostics,
			CancellationToken                    cancellationToken)
		{
			var aspectAttributes = new Dictionary<ISymbol,(AttributeSyntax Syntax,SemanticModel SemanticModel)>(SymbolEqualityComparer.Default);

			foreach (var declaration in aspectDeclarations)
			{
				var semanticModel = compilation.GetSemanticModel(declaration.SyntaxTree);

				if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken) is not {} symbol)
					continue;

				foreach (var attribute in declaration.AttributeLists.SelectMany(static l => l.Attributes))
				{
					if (!AspectSymbols.IsAspectDefinitionAttribute(attribute))
						continue;

					aspectAttributes[symbol] = (attribute, semanticModel);
					break;
				}
			}

			return new AspectDefinitionRegistry(aspectAttributes);
		}

		public static ImmutableArray<ClassDeclarationSyntax> FindAspectDeclarations(Compilation compilation, CancellationToken cancellationToken)
		{
			var result = ImmutableArray.CreateBuilder<ClassDeclarationSyntax>();

			foreach (var tree in compilation.SyntaxTrees)
			{
				var root = tree.GetRoot(cancellationToken);

				foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
				{
					if (declaration.AttributeLists
						.SelectMany(static list => list.Attributes)
						.Any(static attribute => AspectSymbols.IsAspectDefinitionAttribute(attribute)))
						result.Add(declaration);
				}
			}

			return result.ToImmutable();
		}

		public bool TryGetDefinition(INamedTypeSymbol attributeClass, out AttributeInfo definition)
		{
			if (_aspectAttributes.TryGetValue(attributeClass, out var aspectAttribute))
			{
				definition = new AttributeInfo(
					null,
					null,
					null,
					attributeClass,
					null,
					aspectAttribute.Syntax,
					aspectAttribute.SemanticModel,
					GetDefaultTargetFilter(aspectAttribute.Syntax, aspectAttribute.SemanticModel));
				return true;
			}

			if (TryGetExternalAspectDefinition(attributeClass, out var externalAspectDefinitionData))
			{
				definition = new AttributeInfo(
					null,
					null,
					null,
					attributeClass,
					externalAspectDefinitionData,
					null,
					null,
					GetDefaultTargetFilter(externalAspectDefinitionData));
				return true;
			}

			definition = null!;
			return false;
		}

		public bool TryGetExternalAspectDefinition(INamedTypeSymbol attributeClass, out AttributeData? aspectDefinitionData)
		{
			aspectDefinitionData = attributeClass.GetAttributes().FirstOrDefault(static aa => aa is
			{
				AttributeClass:
				{
					ContainingNamespace.Name: "AspectGenerator",
					Name: "AspectAttribute"
				}
			});

			return aspectDefinitionData is not null;
		}

		static string? GetDefaultTargetFilter(AttributeSyntax attribute, SemanticModel semanticModel)
		{
			foreach (var arg in attribute.ArgumentList?.Arguments ?? default)
			{
				if (arg.NameEquals?.Name.Identifier.ValueText != "DefaultTargetFilter")
					continue;

				return AspectSymbols.GetAttributeArgumentValue(arg.Expression, semanticModel) as string;
			}

			return null;
		}

		static string? GetDefaultTargetFilter(AttributeData? attribute)
		{
			if (attribute is null)
				return null;

			foreach (var arg in attribute.NamedArguments)
				if (arg.Key == "DefaultTargetFilter")
					return arg.Value.Value as string;

			return null;
		}
	}
}
