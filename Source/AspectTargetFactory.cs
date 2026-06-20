using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspectGenerator
{
	static class AspectTargetFactory
	{
		public static AspectFilters.MethodTarget Create(IMethodSymbol method)
		{
			var sourceMethod   = method.ReducedFrom ?? method;
			var fullTypeName   = FormatType(sourceMethod.ContainingType);
			var namespaceName  = sourceMethod.ContainingType.ContainingNamespace.IsGlobalNamespace
				? ""
				: sourceMethod.ContainingType.ContainingNamespace.ToDisplayString();
			var methodName     = FormatMethodName(sourceMethod);
			var fullMethodName = $"{fullTypeName}.{methodName}";
			var parameters     = new List<AspectFilters.ParameterTarget>();

			foreach (var parameter in sourceMethod.Parameters)
			{
				parameters.Add(
					new AspectFilters.ParameterTarget
					{
						Modifier = GetParameterModifier(parameter),
						Type     = FormatType(parameter.Type)
					});
			}

			return new AspectFilters.MethodTarget
			{
				Accessibility      = GetAccessibilityMask(sourceMethod.DeclaredAccessibility),
				Modifiers          = GetModifierMask(sourceMethod),
				Namespace          = namespaceName,
				TypeName           = FormatSimpleTypeName(sourceMethod.ContainingType),
				FullTypeName       = fullTypeName,
				MethodName         = methodName,
				FullMethodName     = fullMethodName,
				ReturnType         = FormatType(sourceMethod.ReturnType),
				Signature          = GetCanonicalSignature(method),
				Attributes         = GetAttributeNames(sourceMethod),
				NamespaceSegments  = SplitDottedName(namespaceName),
				FullTypeSegments   = SplitDottedName(fullTypeName),
				FullMethodSegments = SplitDottedName(fullMethodName),
				Parameters         = parameters
			};
		}

		static string GetCanonicalSignature(IMethodSymbol method)
		{
			var sourceMethod = method.ReducedFrom ?? method;
			var parts        = new List<string> { GetAccessibility(sourceMethod.DeclaredAccessibility) };

			if (sourceMethod.IsStatic)                              parts.Add("static");
			if (sourceMethod.IsAbstract)                            parts.Add("abstract");
			if (sourceMethod.IsVirtual && !sourceMethod.IsOverride) parts.Add("virtual");
			if (sourceMethod.IsOverride)                            parts.Add("override");
			if (sourceMethod.IsSealed)                              parts.Add("sealed");
			if (sourceMethod.IsExtern)                              parts.Add("extern");
			if (HasUnsafeModifier(sourceMethod))                    parts.Add("unsafe");

			var sb = new StringBuilder();

			sb
				.Append(string.Join(" ", parts))
				.Append(' ')
				.Append(FormatType(sourceMethod.ReturnType))
				.Append(' ')
				.Append(FormatType(sourceMethod.ContainingType))
				.Append('.')
				.Append(sourceMethod.Name);

			if (sourceMethod.TypeArguments.Length > 0)
				sb.Append('<').Append(string.Join(",", sourceMethod.TypeArguments.Select(FormatType))).Append('>');

			sb.Append('(');

			var parameters = new List<string>();

			var methodParameters = sourceMethod.Parameters.AsEnumerable();

			if (method.ReducedFrom is not null || method.IsExtensionMethod || sourceMethod.IsExtensionMethod)
			{
				var receiverType = method.ReceiverType ?? sourceMethod.Parameters.FirstOrDefault()?.Type;

				if (receiverType is not null)
					parameters.Add($"this {FormatType(receiverType)}");

				if (sourceMethod.Parameters.Length > 0)
					methodParameters = methodParameters.Skip(1);
			}

			foreach (var parameter in methodParameters)
			{
				var parameterText = new StringBuilder();

				if (parameter.IsParams)
					parameterText.Append("params ");

				parameterText.Append(parameter.RefKind switch
				{
					RefKind.Ref => "ref ",
					RefKind.Out => "out ",
					RefKind.In  => "in ",
					_           => ""
				});

				parameterText.Append(FormatType(parameter.Type));
				parameters.Add(parameterText.ToString());
			}

			sb.Append(string.Join(",", parameters));
			sb.Append(')');

			return sb.ToString();
		}

		static List<string> GetAttributeNames(IMethodSymbol method)
		{
			var result = new List<string>();

			AddAttributes(method.GetAttributes(), result);

			for (var type = method.ContainingType; type is not null; type = type.ContainingType)
				AddAttributes(type.GetAttributes(), result);

			return result.Distinct().ToList();
		}

		static void AddAttributes(ImmutableArray<AttributeData> attributes, List<string> result)
		{
			foreach (var attribute in attributes)
			{
				if (attribute.AttributeClass is not {} attributeClass)
					continue;

				var shortName = GetMetadataNameWithoutArity(attributeClass.Name);
				result.Add(shortName);

				const string suffix = "Attribute";

				if (shortName.EndsWith(suffix, System.StringComparison.Ordinal) && shortName.Length > suffix.Length)
					result.Add(shortName[..^suffix.Length]);

				result.Add(FormatNamedType(attributeClass, includeNamespace: true));
			}
		}

		static AspectFilters.AccessibilityMask GetAccessibilityMask(Accessibility accessibility)
		{
			return accessibility switch
			{
				Accessibility.Public               => AspectFilters.AccessibilityMask.Public,
				Accessibility.Protected            => AspectFilters.AccessibilityMask.Protected,
				Accessibility.Internal             => AspectFilters.AccessibilityMask.Internal,
				Accessibility.Private              => AspectFilters.AccessibilityMask.Private,
				Accessibility.ProtectedOrInternal  => AspectFilters.AccessibilityMask.Protected | AspectFilters.AccessibilityMask.Internal,
				Accessibility.ProtectedAndInternal => AspectFilters.AccessibilityMask.Private   | AspectFilters.AccessibilityMask.Protected,
				_                                  => AspectFilters.AccessibilityMask.Private
			};
		}

		static AspectFilters.ModifierMask GetModifierMask(IMethodSymbol method)
		{
			var result = method.IsStatic
				? AspectFilters.ModifierMask.Static
				: AspectFilters.ModifierMask.Instance;

			if (method.IsAbstract)                      result |= AspectFilters.ModifierMask.Abstract;
			if (method.IsVirtual && !method.IsOverride) result |= AspectFilters.ModifierMask.Virtual;
			if (method.IsOverride)                      result |= AspectFilters.ModifierMask.Override;
			if (method.IsSealed)                        result |= AspectFilters.ModifierMask.Sealed;
			if (method.IsExtern)                        result |= AspectFilters.ModifierMask.Extern;
			if (HasUnsafeModifier(method))              result |= AspectFilters.ModifierMask.Unsafe;

			return result;
		}

		static AspectFilters.ParameterModifier GetParameterModifier(IParameterSymbol parameter)
		{
			if (parameter.IsParams)
				return AspectFilters.ParameterModifier.Params;

			return parameter.RefKind switch
			{
				RefKind.Ref => AspectFilters.ParameterModifier.Ref,
				RefKind.Out => AspectFilters.ParameterModifier.Out,
				RefKind.In  => AspectFilters.ParameterModifier.In,
				_           => AspectFilters.ParameterModifier.None
			};
		}

		static List<string> SplitDottedName(string name)
		{
			if (string.IsNullOrEmpty(name))
				return [];

			var result  = new List<string>();
			var start   = 0;
			var depth   = 0;

			for (var i = 0; i < name.Length; i++)
			{
				var ch = name[i];

				if (ch == '<')
					depth++;
				else if (ch == '>' && depth > 0)
					depth--;
				else if (depth == 0 && ch == '.')
				{
					result.Add(name[start..i]);
					start = i + 1;
				}
			}

			result.Add(name[start..]);
			return result;
		}

		static bool HasUnsafeModifier(IMethodSymbol method)
		{
			foreach (var syntaxReference in method.DeclaringSyntaxReferences)
				if (syntaxReference.GetSyntax() is MethodDeclarationSyntax methodDeclaration &&
					methodDeclaration.Modifiers.Any(SyntaxKind.UnsafeKeyword))
					return true;

			return false;
		}

		static string GetAccessibility(Accessibility accessibility)
		{
			return accessibility switch
			{
				Accessibility.Public               => "public",
				Accessibility.Protected            => "protected",
				Accessibility.Internal             => "internal",
				Accessibility.Private              => "private",
				Accessibility.ProtectedOrInternal  => "protected internal",
				Accessibility.ProtectedAndInternal => "private protected",
				_                                  => "private"
			};
		}

		static string FormatType(ITypeSymbol type)
		{
			if (type is IArrayTypeSymbol arrayType)
				return $"{FormatType(arrayType.ElementType)}[]";

			if (type is ITypeParameterSymbol typeParameter)
				return typeParameter.Name;

			if (type is INamedTypeSymbol namedType)
				return FormatNamedType(namedType, includeNamespace: true);

			return type.ToDisplayString(
				new SymbolDisplayFormat(
					typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
					genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
					miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers));
		}

		static string FormatNamedType(INamedTypeSymbol type, bool includeNamespace)
		{
			var name = GetMetadataNameWithoutArity(type.Name);
			var typeParameterCount = type.TypeParameters.Length;

			if (typeParameterCount > 0)
			{
				var typeArguments = type.TypeArguments.Length > typeParameterCount
					? type.TypeArguments.Skip(type.TypeArguments.Length - typeParameterCount)
					: type.TypeArguments;

				name += $"<{string.Join(",", typeArguments.Select(FormatType))}>";
			}

			if (type.ContainingType is not null)
				return $"{FormatNamedType(type.ContainingType, includeNamespace)}.{name}";

			if (!includeNamespace || type.ContainingNamespace.IsGlobalNamespace)
				return name;

			return $"{type.ContainingNamespace.ToDisplayString()}.{name}";
		}

		static string FormatSimpleTypeName(INamedTypeSymbol type)
		{
			var name = GetMetadataNameWithoutArity(type.Name);

			if (!type.IsGenericType)
				return name;

			var typeParameterCount = type.TypeParameters.Length;
			var typeArguments = type.TypeArguments.Length > typeParameterCount
				? type.TypeArguments.Skip(type.TypeArguments.Length - typeParameterCount)
				: type.TypeArguments;

			return $"{name}<{string.Join(",", typeArguments.Select(FormatType))}>";
		}

		static string GetMetadataNameWithoutArity(string name)
		{
			var tick = name.IndexOf('`');

			return tick >= 0 ? name[..tick] : name;
		}

		static string FormatMethodName(IMethodSymbol method)
		{
			if (method.TypeArguments.Length == 0)
				return method.Name;

			return $"{method.Name}<{string.Join(",", method.TypeArguments.Select(FormatType))}>";
		}
	}
}
