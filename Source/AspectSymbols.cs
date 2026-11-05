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
		/// <summary>
		/// The attribute data of an applied aspect. An aspect applied to a type with a target filter is kept as syntax; its
		/// data is looked up on the type, so the arguments keep the types the attribute model gives them (an enum or
		/// <c>typeof</c> inside an <c>object[]</c>, for example), which constants read from the syntax lose.
		/// </summary>
		public static AttributeData? GetAppliedAttributeData(AttributeInfo attribute)
		{
			if (attribute.AppliedAttributeData is {} data)
				return data;

			if (attribute is not { AppliedAttributeSyntax: {} syntax, AppliedSemanticModel: {} semanticModel } ||
				syntax.Parent?.Parent is not BaseTypeDeclarationSyntax typeDeclaration ||
				semanticModel.GetDeclaredSymbol(typeDeclaration) is not {} type)
				return null;

			foreach (var typeAttribute in type.GetAttributes())
				if (typeAttribute.ApplicationSyntaxReference is {} reference &&
					reference.SyntaxTree == syntax.SyntaxTree &&
					reference.Span == syntax.Span)
					return typeAttribute;

			return null;
		}

		/// <summary>
		/// The iterators a call of the method can run. A non-virtual method runs itself. A virtual, abstract or interface
		/// method runs its own body (when it has one) or any override or implementation in this compilation, so each of
		/// them that is an iterator counts; overrides in other assemblies cannot be seen. Only methods that return a
		/// sequence or enumerator type can be iterators, which keeps the search of overrides to those.
		/// </summary>
		public static List<IMethodSymbol> GetIteratorsTheCallCanRun(IMethodSymbol method, Compilation compilation)
		{
			var target = (method.ReducedFrom ?? method).OriginalDefinition;
			var result = new List<IMethodSymbol>();

			if (!IsSequenceType(target.ReturnType))
				return result;

			if (!target.IsAbstract && IsIterator(target))
				result.Add(target);

			var isVirtual = !target.IsStatic && !target.IsSealed &&
				(target.IsVirtual || target.IsAbstract || target.IsOverride || target.ContainingType.TypeKind == TypeKind.Interface);

			if (!isVirtual)
				return result;

			foreach (var type in GetSourceTypes(compilation.Assembly.GlobalNamespace))
			{
				if (SymbolEqualityComparer.Default.Equals(type, target.ContainingType))
					continue;

				foreach (var candidate in GetOverridesIn(type, target))
					if (!candidate.IsAbstract && !result.Contains(candidate, SymbolEqualityComparer.Default) && IsIterator(candidate))
						result.Add(candidate);
			}

			return result;

			// Overrides of the target declared in the type, or for an interface member the type's implementations of it
			// (explicit ones included, which are not found by name).
			static IEnumerable<IMethodSymbol> GetOverridesIn(INamedTypeSymbol type, IMethodSymbol target)
			{
				if (target.ContainingType.TypeKind == TypeKind.Interface)
				{
					foreach (var face in type.AllInterfaces)
					{
						if (!SymbolEqualityComparer.Default.Equals(face.OriginalDefinition, target.ContainingType))
							continue;

						foreach (var member in face.GetMembers(target.Name))
							if (SymbolEqualityComparer.Default.Equals(member.OriginalDefinition, target) &&
								type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation &&
								SymbolEqualityComparer.Default.Equals(implementation.ContainingType.OriginalDefinition, type))
								yield return implementation.OriginalDefinition;
					}

					yield break;
				}

				foreach (var member in type.GetMembers(target.Name))
				{
					if (member is not IMethodSymbol { IsOverride: true } candidate)
						continue;

					for (var overridden = candidate.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
						if (SymbolEqualityComparer.Default.Equals(overridden.OriginalDefinition, target))
						{
							yield return candidate.OriginalDefinition;
							break;
						}
				}
			}

			static bool IsSequenceType(ITypeSymbol type)
			{
				return type is INamedTypeSymbol
				{
					Name               : "IEnumerable" or "IEnumerator" or "IAsyncEnumerable" or "IAsyncEnumerator",
					ContainingNamespace: { Name: "Generic" or "Collections" }
				};
			}

			static IEnumerable<INamedTypeSymbol> GetSourceTypes(INamespaceSymbol ns)
			{
				foreach (var member in ns.GetMembers())
				{
					if (member is INamespaceSymbol child)
					{
						foreach (var type in GetSourceTypes(child))
							yield return type;
					}
					else if (member is INamedTypeSymbol type)
					{
						foreach (var nested in GetNested(type))
							yield return nested;
					}
				}
			}

			static IEnumerable<INamedTypeSymbol> GetNested(INamedTypeSymbol type)
			{
				yield return type;

				foreach (var nested in type.GetTypeMembers())
					foreach (var inner in GetNested(nested))
						yield return inner;
			}
		}

		/// <summary>
		/// Whether the method is an iterator (<c>yield</c>, sync or async): its body runs while the returned sequence is
		/// enumerated, not during the call. A source method is recognized by a <c>yield</c> statement in its own body
		/// (not in a lambda or local function), a compiled one by the state machine attribute the compiler puts on it.
		/// </summary>
		public static bool IsIterator(IMethodSymbol method)
		{
			var target = (method.ReducedFrom ?? method).OriginalDefinition;

			target = target.PartialImplementationPart ?? target;

			if (target.DeclaringSyntaxReferences.Length == 0)
				return target.GetAttributes().Any(static a => a.AttributeClass is
				{
					Name               : "IteratorStateMachineAttribute" or "AsyncIteratorStateMachineAttribute",
					ContainingNamespace: { Name: "CompilerServices", ContainingNamespace: { Name: "Runtime", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } }
				});

			foreach (var reference in target.DeclaringSyntaxReferences)
			{
				if (reference.GetSyntax() is not MethodDeclarationSyntax declaration)
					continue;

				SyntaxNode? body = declaration.Body ?? (SyntaxNode?)declaration.ExpressionBody;

				if (body is not null && body
					.DescendantNodes(static node => node is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
					.OfType<YieldStatementSyntax>()
					.Any())
					return true;
			}

			return false;
		}

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
