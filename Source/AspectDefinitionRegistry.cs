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

		/// <summary>
		/// Collects the declarations the generator receives from its syntax providers: aspect definition classes and
		/// method declarations. Both are type members, so the walk never enters member bodies.
		/// </summary>
		public static void FindDeclarations(
			Compilation                                      compilation,
			CancellationToken                                cancellationToken,
			out ImmutableArray<ClassDeclarationSyntax>       aspectDeclarations,
			out ImmutableArray<MethodDeclarationSyntax>      methodDeclarations)
		{
			var aspects = ImmutableArray.CreateBuilder<ClassDeclarationSyntax>();
			var methods = ImmutableArray.CreateBuilder<MethodDeclarationSyntax>();

			foreach (var tree in compilation.SyntaxTrees)
				AddMembers(tree.GetRoot(cancellationToken));

			aspectDeclarations = aspects.ToImmutable();
			methodDeclarations = methods.ToImmutable();

			void AddMembers(SyntaxNode node)
			{
				var members = node switch
				{
					CompilationUnitSyntax compilationUnit                => compilationUnit.Members,
					BaseNamespaceDeclarationSyntax namespaceDeclaration  => namespaceDeclaration.Members,
					TypeDeclarationSyntax typeDeclaration                => typeDeclaration.Members,
					_                                                    => default
				};

				foreach (var member in members)
				{
					if (member is MethodDeclarationSyntax method)
						methods.Add(method);
					else if (member is BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax)
					{
						if (member is ClassDeclarationSyntax declaration && IsAspectDeclarationCandidate(declaration))
							aspects.Add(declaration);

						AddMembers(member);
					}
				}
			}
		}

		public static bool IsAspectDeclarationCandidate(SyntaxNode node)
		{
			return node is ClassDeclarationSyntax { AttributeLists.Count: > 0 } declaration && declaration.AttributeLists
				.SelectMany(static list => list.Attributes)
				.Any(static attribute => AspectSymbols.IsAspectAttributeName(attribute.Name.ToString()));
		}

		/// <summary>
		/// A referenced assembly declares aspect attributes, so methods without a declaration in this compilation may carry aspects.
		/// </summary>
		public static bool HasExternalAspectDefinitions(Compilation compilation)
		{
			foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
				if (assembly.GetTypeByMetadataName("AspectGenerator.AspectAttribute") is not null &&
					HasExternalAspectDefinitions(assembly.GlobalNamespace))
					return true;

			return false;

			static bool HasExternalAspectDefinitions(INamespaceOrTypeSymbol symbol)
			{
				if (symbol is INamedTypeSymbol type)
				{
					if (type.GetAttributes().Any(static attribute => attribute.AttributeClass is
						{
							ContainingNamespace.Name: "AspectGenerator",
							Name: "AspectAttribute"
						}))
						return true;

					foreach (var nestedType in type.GetTypeMembers())
						if (HasExternalAspectDefinitions(nestedType))
							return true;
				}

				if (symbol is INamespaceSymbol namespaceSymbol)
				{
					foreach (var member in namespaceSymbol.GetMembers())
						if (HasExternalAspectDefinitions(member))
							return true;
				}

				return false;
			}
		}

		public bool TryGetDefinition(INamedTypeSymbol attributeClass, out AttributeInfo definition)
		{
			// A generic aspect is applied constructed ([Gen<int>]); its [Aspect] is declared on the definition.
			if (_aspectAttributes.TryGetValue(attributeClass, out var aspectAttribute) ||
				_aspectAttributes.TryGetValue(attributeClass.OriginalDefinition, out aspectAttribute))
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
