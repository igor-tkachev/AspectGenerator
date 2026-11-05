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
