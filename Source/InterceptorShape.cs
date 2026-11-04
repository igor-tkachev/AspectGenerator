using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace AspectGenerator
{
	/// <summary>
	/// Decides how the interceptor of a call is shaped and which calls cannot be intercepted by generated code at all.
	/// </summary>
	static class InterceptorShape
	{
		/// <summary>
		/// Returns why a selected call cannot get a compilable interceptor, or <c>null</c> when it can.
		/// The call is then left as it is and reported instead of producing code that does not compile.
		/// </summary>
		public static string? GetUnsupportedReason(
			InvocationExpressionSyntax invocation,
			IMethodSymbol              method,
			List<AttributeInfo>        attributes,
			SemanticModel              semanticModel,
			CancellationToken          cancellationToken)
		{
			var compilation = semanticModel.Compilation;

			if (invocation.Expression is not (MemberAccessExpressionSyntax or IdentifierNameSyntax or GenericNameSyntax or MemberBindingExpressionSyntax))
				return $"the call form '{invocation.Expression.GetType().Name}' is not supported";

			// The interceptor calls the target through its receiver, which would be a virtual call for base.M().
			if (invocation.Expression is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax })
				return "base calls are not intercepted: the interceptor would call the override instead";

			if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.ReducedExtension))
				return method.MethodKind == MethodKind.LocalFunction
					? "local functions are not supported"
					: $"methods of kind {method.MethodKind} are not supported";

			var target = (method.ReducedFrom ?? method).OriginalDefinition;

			for (var type = target.ContainingType; type is not null; type = type.ContainingType)
				if (!type.CanBeReferencedByName)
					return "members of C# 14 extension blocks are not supported";

			if (!compilation.IsSymbolAccessibleWithin(target, compilation.Assembly))
				return "the target method is not accessible from the generated interceptor class (it is private or protected)";

			if (method.IsVararg)
				return "__arglist methods are not supported";

			if (method.ReturnsByRef || method.ReturnsByRefReadonly)
				return "methods that return by reference are not supported";

			if (method.ReturnType.IsRefLikeType)
				return "methods that return a ref struct are not supported";

			foreach (var parameter in method.Parameters)
				if (parameter.Type.TypeKind == TypeKind.Dynamic && parameter.RefKind != RefKind.None)
					return "by-reference parameters of type dynamic are not supported";

			var options       = attributes.SelectMany(static a => AspectSourceGenerator.GetAspectOptions(a)).ToList();
			var passArguments = options.Any(static o => o is { Key: "PassArguments", Value: true });
			var usesOnCall    = options.Any(static o => o is { Key: "OnCall", Value: string s } && !string.IsNullOrWhiteSpace(s));

			if (passArguments && (method.Parameters.Any(static p => p.Type.IsRefLikeType) || !method.IsStatic && method.ReceiverType?.IsRefLikeType == true))
				return "PassArguments cannot box a ref struct argument";

			if (RequiresReflectionLookup(method) && method.MethodKind != MethodKind.ReducedExtension && !method.IsStatic && method.IsGenericMethod)
				return "generic instance methods with ref struct or pointer parameters are not supported";

			if (NeedsGenericInterceptor(method, compilation))
			{
				if (usesOnCall)
					return "an OnCall hook cannot replace a call whose type arguments cannot be named in generated code";

				if (GetGenericShape(method) is not {} shape)
					return "the type arguments of the call cannot be named in generated code";

				var typeParameters = GetTypeParameters(shape);
				var names          = typeParameters.Select(static t => t.Name).ToList();

				if (names.Distinct().Count() != names.Count)
					return "the target method and its containing types reuse a type parameter name";

				// The compiler's signature check fails for such a call (a generic method calling itself, a generic type
				// calling its own members), so a generic interceptor cannot be used.
				if (GetCallTypeArguments(method).Any(t => ContainsTypeParameter(t, typeParameters)))
					return "calls whose type arguments are the target's own type parameters are not supported";
			}

			if (IsRemovedConditionalCall(target, invocation))
				return "the call is removed by its [Conditional] attribute";

			if (target is { IsPartialDefinition: true, PartialImplementationPart: null })
				return "the partial method has no implementation";

			// Last: it binds the enclosing lambda, the other checks only look at the target.
			if (IsInExpressionTree(invocation, semanticModel, cancellationToken))
				return "calls inside expression trees are not intercepted: the expression must keep the original method";

			return null;
		}

		static IEnumerable<ITypeSymbol> GetCallTypeArguments(IMethodSymbol method)
		{
			var target = method.GetConstructedReducedFrom() ?? method;

			foreach (var typeArgument in target.TypeArguments)
				yield return typeArgument;

			for (var type = target.ContainingType; type is not null; type = type.ContainingType)
				foreach (var typeArgument in type.TypeArguments)
					yield return typeArgument;
		}

		static bool ContainsTypeParameter(ITypeSymbol type, List<ITypeParameterSymbol> typeParameters)
		{
			switch (type)
			{
				case ITypeParameterSymbol typeParameter :
					return typeParameters.Contains(typeParameter, SymbolEqualityComparer.Default);
				case IArrayTypeSymbol array :
					return ContainsTypeParameter(array.ElementType, typeParameters);
				case IPointerTypeSymbol pointer :
					return ContainsTypeParameter(pointer.PointedAtType, typeParameters);
				case INamedTypeSymbol named :
					for (var t = named; t is not null; t = t.ContainingType)
						foreach (var typeArgument in t.TypeArguments)
							if (ContainsTypeParameter(typeArgument, typeParameters))
								return true;
					return false;
				default :
					return false;
			}
		}

		/// <summary>
		/// A call to a <c>[Conditional]</c> method whose symbols are not defined is not compiled at all; the generated
		/// method info would refer to the removed call in an expression tree.
		/// </summary>
		static bool IsRemovedConditionalCall(IMethodSymbol target, InvocationExpressionSyntax invocation)
		{
			var symbols = new List<string>();

			foreach (var attribute in target.GetAttributes())
				if (attribute.AttributeClass is { Name: "ConditionalAttribute", ContainingNamespace: { Name: "Diagnostics", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } } &&
					attribute.ConstructorArguments is [{ Value: string symbol }])
					symbols.Add(symbol);

			if (symbols.Count == 0)
				return false;

			var defined = invocation.SyntaxTree.Options.PreprocessorSymbolNames;

			return !symbols.Any(s => defined.Contains(s));
		}

		/// <summary>
		/// A call in a lambda converted to an expression tree (including the clauses of a query expression over
		/// <c>IQueryable</c>) is data for a query provider, not a call; it is never intercepted.
		/// </summary>
		static bool IsInExpressionTree(InvocationExpressionSyntax invocation, SemanticModel semanticModel, CancellationToken cancellationToken)
		{
			if (!invocation.Ancestors().Any(static a => a is AnonymousFunctionExpressionSyntax or QueryExpressionSyntax))
				return false;

			for (var operation = semanticModel.GetOperation(invocation, cancellationToken); operation is not null; operation = operation.Parent)
			{
				if (operation is IAnonymousFunctionOperation { Parent.Type: {} type } && IsExpressionType(type))
					return true;
			}

			return false;

			static bool IsExpressionType(ITypeSymbol type)
			{
				for (var t = type as INamedTypeSymbol; t is not null; t = t.BaseType)
					if (t is { Name: "LambdaExpression", ContainingNamespace: { Name: "Expressions", ContainingNamespace: { Name: "Linq", ContainingNamespace: { Name: "System", ContainingNamespace.IsGlobalNamespace: true } } } })
						return true;

				return false;
			}
		}

		/// <summary>
		/// A non-generic interceptor names the call's type arguments. When one of them cannot be named in the generated
		/// file (a type parameter of the calling method or type, an anonymous type, a type the file cannot access), the
		/// interceptor is generic over the target's own type parameters instead; the compiler passes the call's type
		/// arguments to it.
		/// </summary>
		public static bool NeedsGenericInterceptor(IMethodSymbol method, Compilation compilation)
		{
			var target = method.GetConstructedReducedFrom() ?? method;

			foreach (var typeArgument in target.TypeArguments)
				if (!CanBeNamed(typeArgument, compilation))
					return true;

			for (var type = target.ContainingType; type is not null; type = type.ContainingType)
				foreach (var typeArgument in type.TypeArguments)
					if (!CanBeNamed(typeArgument, compilation))
						return true;

			return false;
		}

		static bool CanBeNamed(ITypeSymbol type, Compilation compilation)
		{
			switch (type)
			{
				case ITypeParameterSymbol :
					return false;
				case IArrayTypeSymbol array :
					return CanBeNamed(array.ElementType, compilation);
				case IPointerTypeSymbol pointer :
					return CanBeNamed(pointer.PointedAtType, compilation);
				case INamedTypeSymbol named :
				{
					if (named.IsAnonymousType || !compilation.IsSymbolAccessibleWithin(named.OriginalDefinition, compilation.Assembly))
						return false;

					for (var t = named; t is not null; t = t.ContainingType)
						foreach (var typeArgument in t.TypeArguments)
							if (!CanBeNamed(typeArgument, compilation))
								return false;

					return true;
				}
				default :
					return true;
			}
		}

		/// <summary>
		/// The target method written in terms of its own type parameters (and those of its containing types), which become
		/// the type parameters of a generic interceptor. A reduced extension method stays reduced, so the interceptor keeps
		/// its <c>this</c> parameter.
		/// </summary>
		public static IMethodSymbol? GetGenericShape(IMethodSymbol method)
		{
			if (method.MethodKind != MethodKind.ReducedExtension)
				return method.OriginalDefinition;

			var definition = method.ReducedFrom!.OriginalDefinition;
			var reduced    = definition.ReduceExtensionMethod(definition.Parameters[0].Type);

			// The receiver must fix every type parameter of the extension method to itself, otherwise the reduced form
			// does not describe the method.
			if (reduced?.GetConstructedReducedFrom() is not {} constructed ||
				!constructed.TypeArguments.SequenceEqual<ITypeSymbol>(definition.TypeParameters, SymbolEqualityComparer.Default))
				return null;

			return reduced;
		}

		/// <summary>
		/// Type parameters of a generic interceptor in the order the compiler passes type arguments to it: those of the
		/// containing types, outermost first, then those of the method.
		/// </summary>
		public static List<ITypeParameterSymbol> GetTypeParameters(IMethodSymbol shape)
		{
			var target = (shape.ReducedFrom ?? shape).OriginalDefinition;
			var types  = new List<INamedTypeSymbol>();

			for (var type = target.ContainingType; type is not null; type = type.ContainingType)
				types.Insert(0, type);

			var result = types.SelectMany(static t => t.TypeParameters).ToList();

			result.AddRange(target.TypeParameters);

			return result;
		}

		/// <summary>
		/// The method info of a target with ref struct or pointer parameters (or receiver) cannot be taken from an
		/// expression tree: such values cannot appear in one.
		/// </summary>
		public static bool RequiresReflectionLookup(IMethodSymbol method)
		{
			if (method.Parameters.Any(static p => IsExpressionTreeIncompatible(p.Type)))
				return true;

			return method.ReceiverType is {} receiver && (method.MethodKind == MethodKind.ReducedExtension || !method.IsStatic) && IsExpressionTreeIncompatible(receiver);
		}

		static bool IsExpressionTreeIncompatible(ITypeSymbol type)
		{
			return type.IsRefLikeType || type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer;
		}

		public static string PrintTypeParameterList(List<ITypeParameterSymbol> typeParameters)
		{
			return typeParameters.Count == 0 ? "" : $"<{string.Join(", ", typeParameters.Select(static t => t.Name))}>";
		}

		public static string PrintConstraints(List<ITypeParameterSymbol> typeParameters, System.Func<ITypeSymbol,string> printType)
		{
			var sb = new StringBuilder();

			foreach (var typeParameter in typeParameters)
			{
				var constraints = new List<string>();

				if (typeParameter.HasUnmanagedTypeConstraint)
					constraints.Add("unmanaged");
				else if (typeParameter.HasValueTypeConstraint)
					constraints.Add("struct");
				else if (typeParameter.HasReferenceTypeConstraint)
					constraints.Add(typeParameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?" : "class");
				else if (typeParameter.HasNotNullConstraint)
					constraints.Add("notnull");

				foreach (var constraintType in typeParameter.ConstraintTypes)
					constraints.Add(printType(constraintType));

				if (typeParameter.HasConstructorConstraint && !typeParameter.HasValueTypeConstraint)
					constraints.Add("new()");

				if (typeParameter.AllowsRefLikeType)
					constraints.Add("allows ref struct");

				if (constraints.Count > 0)
					sb.Append($" where {typeParameter.Name} : {string.Join(", ", constraints)}");
			}

			return sb.ToString();
		}
	}
}
