using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
		/// The constructor and the arguments the generated code builds an applied aspect with: those of its effective
		/// application when the target filters decided its settings (the constructor arguments of one attribute, each named
		/// argument of the attribute that writes it), otherwise those of the one attribute the aspect is applied with.
		/// <see langword="null"/> when the arguments are only known from syntax. <c>TargetFilter</c> is never one of them.
		/// </summary>
		public static AppliedArguments? GetAppliedArguments(AttributeInfo attribute)
		{
			if (attribute.EffectiveApplication is {} effective)
			{
				var named = ImmutableArray.CreateBuilder<KeyValuePair<string, TypedConstant>>(effective.NamedArguments.Length);

				foreach (var argument in effective.NamedArguments)
					if (argument.Name != "TargetFilter")
						named.Add(new KeyValuePair<string, TypedConstant>(argument.Name, argument.Value));

				return new AppliedArguments(effective.Constructor, effective.ConstructorArguments, named.ToImmutable());
			}

			if (GetAppliedAttributeData(attribute) is not {} data)
				return null;

			return new AppliedArguments(
				data.AttributeConstructor,
				data.ConstructorArguments,
				[..data.NamedArguments.Where(static argument => argument.Key != "TargetFilter")]);
		}

		/// <summary>
		/// The named arguments an applied aspect is ordered by: those of its effective application, otherwise those of the
		/// attribute it is applied with (none for an aspect known only from syntax).
		/// </summary>
		public static ImmutableArray<KeyValuePair<string, TypedConstant>> GetOrderNamedArguments(AttributeInfo attribute)
		{
			if (attribute.EffectiveApplication is {} effective)
				return [..effective.NamedArguments.Select(static argument => new KeyValuePair<string, TypedConstant>(argument.Name, argument.Value))];

			return attribute.AppliedAttributeData?.NamedArguments ?? ImmutableArray<KeyValuePair<string, TypedConstant>>.Empty;
		}

		/// <summary>
		/// The attribute diagnostics about an applied aspect stand on: the most specific attribute of its effective
		/// application, otherwise the one it is applied with.
		/// </summary>
		public static AttributeData? GetReportingAttribute(AttributeInfo attribute)
		{
			return attribute.EffectiveApplication?.ReportAt ?? attribute.AppliedAttributeData;
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

		/// <summary>
		/// The generator's own <c>Interceptors.g.cs</c>, present in a compilation the analyzer sees after the generator ran
		/// (the editor after a build, a test driver). The generator itself never sees it, so the analyzer skips it to select
		/// the same targets; on a large project it is megabytes of attributes that cost most of a per-edit analysis.
		/// </summary>
		public static bool IsGeneratedInterceptorsTree(SyntaxTree tree)
		{
			// The driver names a generated tree <assembly>/<generator type>/<hint name>; only that exact file, not a user's
			// own Interceptors.g.cs or MyInterceptors.g.cs elsewhere.
			var path = tree.FilePath;

			if (!string.Equals(System.IO.Path.GetFileName(path), "Interceptors.g.cs", StringComparison.Ordinal))
				return false;

			var directory = System.IO.Path.GetDirectoryName(path);

			return directory is not null &&
				string.Equals(System.IO.Path.GetFileName(directory), typeof(AspectSourceGenerator).FullName, StringComparison.Ordinal);
		}
	}

	/// <summary>The constructor and arguments an applied aspect is built with (see <see cref="AspectSymbols.GetAppliedArguments"/>).</summary>
	readonly record struct AppliedArguments(
		IMethodSymbol?                                      Constructor,
		ImmutableArray<TypedConstant>                       ConstructorArguments,
		ImmutableArray<KeyValuePair<string, TypedConstant>> NamedArguments);
}
