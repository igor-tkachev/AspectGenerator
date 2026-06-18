using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspectGenerator
{
	sealed class AspectSelectionService
	{
		readonly Compilation _compilation;
		readonly GeneratorExecutionOptions _options;
		readonly AspectDefinitionRegistry _registry;
		readonly IAspectDiagnosticSink _diagnostics;
		readonly ImmutableArray<AspectFilterSet> _assemblyFilters;
		readonly Dictionary<IMethodSymbol,List<AttributeInfo>> _methodCache = new(SymbolEqualityComparer.Default);
		readonly Dictionary<INamedTypeSymbol,ImmutableArray<AspectFilterSet>> _typeFilterCache = new(SymbolEqualityComparer.Default);
		readonly object _cacheGate = new();

		public AspectSelectionService(
			Compilation              compilation,
			GeneratorExecutionOptions options,
			AspectDefinitionRegistry registry,
			IAspectDiagnosticSink    diagnostics)
		{
			_compilation = compilation;
			_options = options;
			_registry = registry;
			_diagnostics = diagnostics;
			_assemblyFilters = BuildAssemblyFilters();
		}

		public ImmutableArray<AnalyzedInvocation> AnalyzeInvocations(
			ImmutableArray<InvocationExpressionSyntax> invocations,
			CancellationToken cancellationToken)
		{
			var result = ImmutableArray.CreateBuilder<AnalyzedInvocation>();

			foreach (var invocation in invocations)
			{
				var semanticModel = _compilation.GetSemanticModel(invocation.SyntaxTree);

				if (TryAnalyzeInvocation(invocation, semanticModel, cancellationToken, out var analyzedInvocation))
					result.Add(analyzedInvocation);

				if (cancellationToken.IsCancellationRequested)
					break;
			}

			return result.ToImmutable();
		}

		public bool TryAnalyzeInvocation(
			InvocationExpressionSyntax invocation,
			SemanticModel semanticModel,
			CancellationToken cancellationToken,
			out AnalyzedInvocation analyzedInvocation)
		{
			analyzedInvocation = null!;

			var info = semanticModel.GetSymbolInfo(invocation, cancellationToken);

			if (info.Symbol is not IMethodSymbol method)
				return false;

			var attributes = GetSelectedAttributes(method);

			if (attributes.Count == 0)
				return false;

			analyzedInvocation = new AnalyzedInvocation(invocation, method, attributes);
			return true;
		}

		List<AttributeInfo> GetSelectedAttributes(IMethodSymbol method)
		{
			lock (_cacheGate)
			{
				if (_methodCache.TryGetValue(method, out var cached))
					return cached;

				var attributes = new List<AttributeInfo>();
				var explicitAttributeClasses = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

				foreach (var methodAttribute in method.GetAttributes())
				{
					if (methodAttribute.AttributeClass is not {} attributeClass)
						continue;

					if (!AspectSymbols.IsConditionalAspectEnabled(attributeClass, _compilation, methodAttribute.ApplicationSyntaxReference?.SyntaxTree))
						continue;

					if (_registry.TryGetDefinition(attributeClass, out var definition))
					{
						ReportMethodLevelTargetFilter(methodAttribute);
						attributes.Add(definition with
						{
							AppliedAttributeData = methodAttribute
						});
						explicitAttributeClasses.Add(attributeClass);
					}
				}

				var target = AspectTargetFactory.Create(method);

				AddMatchedFilterAttributes(attributes, explicitAttributeClasses, _assemblyFilters, target);
				AddMatchedFilterAttributes(attributes, explicitAttributeClasses, GetTypeFilters(method.ContainingType), target);

				_methodCache[method] = attributes.Distinct().ToList();
				return _methodCache[method];
			}
		}

		ImmutableArray<AspectFilterSet> BuildAssemblyFilters()
		{
			var result = ImmutableArray.CreateBuilder<AspectFilterSet>();

			foreach (var filterAttribute in _compilation.Assembly.GetAttributes())
			{
				if (!filterAttribute.NamedArguments.Any(static a => a.Key == "TargetFilter"))
					continue;

				if (CreateAppliedAspectFilterSet(filterAttribute) is {} filterSet)
					result.Add(filterSet);
			}

			return result.ToImmutable();
		}

		ImmutableArray<AspectFilterSet> GetTypeFilters(INamedTypeSymbol? type)
		{
			if (type is null)
				return [];

			if (_typeFilterCache.TryGetValue(type, out var cached))
				return cached;

			var result = ImmutableArray.CreateBuilder<AspectFilterSet>();

			foreach (var syntaxReference in type.DeclaringSyntaxReferences)
			{
				if (syntaxReference.GetSyntax() is not TypeDeclarationSyntax typeDeclaration)
					continue;

				var semanticModel = _compilation.GetSemanticModel(typeDeclaration.SyntaxTree);

				foreach (var filterAttribute in typeDeclaration.AttributeLists.SelectMany(static list => list.Attributes))
				{
					if (!AspectSymbols.HasNamedArgument(filterAttribute, "TargetFilter"))
						continue;

					if (CreateAppliedAspectFilterSet(filterAttribute, semanticModel) is {} filterSet)
						result.Add(filterSet);
				}
			}

			var filters = result.ToImmutable();
			_typeFilterCache[type] = filters;

			return filters;
		}

		AspectFilterSet? CreateAppliedAspectFilterSet(AttributeSyntax filterAttribute, SemanticModel semanticModel)
		{
			if (semanticModel.GetSymbolInfo(filterAttribute).Symbol is not IMethodSymbol { ContainingType: var aspectClass })
				return null;

			if (!AspectSymbols.IsConditionalAspectEnabled(aspectClass, semanticModel.Compilation, filterAttribute.SyntaxTree))
				return null;

			if (!_registry.TryGetDefinition(aspectClass, out var definition))
				return null;

			var filters = GetNamedCompiledFilterValue(
				filterAttribute,
				semanticModel,
				"TargetFilter",
				filterAttribute.GetLocation());

			if (filters.IsEmpty)
				return null;

			return new AspectFilterSet(
				definition with
				{
					AppliedAttributeSyntax = filterAttribute,
					AppliedSemanticModel = semanticModel
				},
				filters);
		}

		AspectFilterSet? CreateAppliedAspectFilterSet(AttributeData filterAttribute)
		{
			if (filterAttribute.AttributeClass is not {} aspectClass)
				return null;

			if (!AspectSymbols.IsConditionalAspectEnabled(aspectClass, _compilation, filterAttribute.ApplicationSyntaxReference?.SyntaxTree))
				return null;

			if (!_registry.TryGetDefinition(aspectClass, out var definition))
				return null;

			var location = filterAttribute.ApplicationSyntaxReference is {} syntaxReference
				? Location.Create(syntaxReference.SyntaxTree, syntaxReference.Span)
				: null;
			var filters = GetNamedCompiledFilterValue(filterAttribute, "TargetFilter", location);

			if (filters.IsEmpty)
				return null;

			return new AspectFilterSet(
				definition with
				{
					AppliedAttributeData = filterAttribute
				},
				filters);
		}

		AspectFilters.TargetFilterSet GetNamedCompiledFilterValue(
			AttributeSyntax attribute,
			SemanticModel semanticModel,
			string name,
			Location? location)
		{
			foreach (var arg in attribute.ArgumentList?.Arguments ?? default)
			{
				if (arg.NameEquals?.Name.Identifier.ValueText != name)
					continue;

				var value = AspectSymbols.GetAttributeArgumentValue(arg.Expression, semanticModel);

				return CompileAspectFilters(value, location);
			}

			return default;
		}

		AspectFilters.TargetFilterSet GetNamedCompiledFilterValue(
			AttributeData attribute,
			string name,
			Location? location)
		{
			foreach (var arg in attribute.NamedArguments)
			{
				if (arg.Key != name)
					continue;

				var value = arg.Value.Kind == TypedConstantKind.Array
					? arg.Value.Values.Select(static v => v.Value).ToArray()
					: arg.Value.Value;

				return CompileAspectFilters(value, location);
			}

			return default;
		}

		AspectFilters.TargetFilterSet CompileAspectFilters(object? filterValue, Location? location)
		{
			return filterValue is object?[] values
				? AspectFilters.GetFilters(values.Select(static value => value as string), ReportFilterDiagnostic)
				: AspectFilters.GetFilters(filterValue as string, ReportFilterDiagnostic);

			void ReportFilterDiagnostic(AspectFilters.TargetFilterDiagnostic diagnostic)
			{
				_diagnostics.Report(
					diagnostic.Id,
					diagnostic.Message,
					location);
			}
		}

		void AddMatchedFilterAttributes(
			List<AttributeInfo> attributes,
			HashSet<INamedTypeSymbol> explicitAttributeClasses,
			ImmutableArray<AspectFilterSet> filterSets,
			in AspectFilters.MethodTarget target)
		{
			foreach (var filterSet in filterSets)
			{
				var evaluation = filterSet.Filters.Evaluate(target);

				if (!evaluation.IsMatch)
				{
					if (evaluation.IsExcluded)
						attributes.RemoveAll(attribute =>
							SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, filterSet.Attribute.AttributeClass) &&
							!explicitAttributeClasses.Contains(attribute.AttributeClass));

					continue;
				}

				if (attributes.Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, filterSet.Attribute.AttributeClass)))
					continue;

				attributes.Add(filterSet.Attribute);
			}
		}

		void ReportMethodLevelTargetFilter(AttributeData attribute)
		{
			if (!attribute.NamedArguments.Any(static a => a.Key == "TargetFilter"))
				return;

			var location = attribute.ApplicationSyntaxReference is {} syntaxReference
				? Location.Create(syntaxReference.SyntaxTree, syntaxReference.Span)
				: null;

			_diagnostics.Report(
				AspectDiagnosticID.MethodLevelTargetFilter,
				"TargetFilter is only supported on assembly-level or type-level aspect attributes. Remove TargetFilter from this method-level aspect attribute.",
				location);
		}
	}
}
