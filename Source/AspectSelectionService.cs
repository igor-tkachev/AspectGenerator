using System;
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
		readonly GeneratorTraceReport? _trace;
		readonly ImmutableArray<AspectFilterSet> _assemblyFilters;
		readonly Dictionary<IMethodSymbol,List<AttributeInfo>> _methodCache = new(SymbolEqualityComparer.Default);
		readonly Dictionary<INamedTypeSymbol,ImmutableArray<AspectFilterSet>> _typeFilterCache = new(SymbolEqualityComparer.Default);
		readonly object _cacheGate = new();

		public AspectSelectionService(
			Compilation              compilation,
			GeneratorExecutionOptions options,
			AspectDefinitionRegistry registry,
			IAspectDiagnosticSink    diagnostics,
			GeneratorTraceReport?    trace = null)
		{
			_compilation = compilation;
			_options = options;
			_registry = registry;
			_diagnostics = diagnostics;
			_trace = trace;
			_assemblyFilters = BuildAssemblyFilters();
		}

		public ImmutableArray<AnalyzedInvocation> AnalyzeInvocations(
			ImmutableArray<InvocationExpressionSyntax> invocations,
			AspectSelectionPlan                        plan,
			CancellationToken                          cancellationToken)
		{
			var result = ImmutableArray.CreateBuilder<AnalyzedInvocation>();
			var afterPrefilter = 0;

			foreach (var invocation in invocations)
			{
				if (!MayMatchCallSite(invocation, plan))
					continue;

				afterPrefilter++;
				var semanticModel = _compilation.GetSemanticModel(invocation.SyntaxTree);
				_trace?.IncrementCounter("Symbol resolution attempts");

				if (TryAnalyzeInvocation(invocation, semanticModel, plan, cancellationToken, out var analyzedInvocation))
				{
					_trace?.IncrementCounter("Symbol resolution successes");
					result.Add(analyzedInvocation);
				}
				else
					_trace?.IncrementCounter("Symbol resolution failures");

				if (cancellationToken.IsCancellationRequested)
					break;
			}

			_trace?.SetCounter("Invocation candidates after prefilter", afterPrefilter);
			return result.ToImmutable();
		}

		public AspectSelectionPlan BuildSelectionPlan(
			ImmutableArray<MethodDeclarationSyntax> methodDeclarations,
			bool                                    externalAspectDefinitionsPossible,
			CancellationToken                       cancellationToken)
		{
			var selectedMethods    = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
			var selectedAttributes = new Dictionary<IMethodSymbol,List<AttributeInfo>>(SymbolEqualityComparer.Default);
			var buckets            = new Dictionary<string,TargetMethodBucket>(StringComparer.Ordinal);
			var mayTargetExternalMethods = externalAspectDefinitionsPossible ||
				_assemblyFilters.Any(static filterSet => filterSet.Filters.Plan.MayTargetExternalMethods);
			var targetMethodDeclarations = 0;

			foreach (var methodDeclaration in methodDeclarations)
			{
				if (cancellationToken.IsCancellationRequested)
					break;

				targetMethodDeclarations++;
				var semanticModel = _compilation.GetSemanticModel(methodDeclaration.SyntaxTree);

				if (semanticModel.GetDeclaredSymbol(methodDeclaration, cancellationToken) is not IMethodSymbol method)
					continue;

				var attributes = GetSelectedAttributes(method);

				if (attributes.Count == 0)
					continue;

				var originalMethod = method.OriginalDefinition;
				selectedMethods.Add(originalMethod);
				selectedAttributes[originalMethod] = attributes;

				if (!buckets.TryGetValue(originalMethod.Name, out var bucket))
				{
					bucket = new TargetMethodBucket(originalMethod.Name);
					buckets.Add(originalMethod.Name, bucket);
				}

				bucket.Add(originalMethod);
			}

			_trace?.SetCounter("Target method declarations", targetMethodDeclarations);
			_trace?.SetCounter("Target methods after declaration filter", selectedMethods.Count);
			_trace?.SetCounter("Target methods after semantic filter", selectedMethods.Count);

			return new AspectSelectionPlan(selectedMethods, selectedAttributes, buckets, mayTargetExternalMethods);
		}

		public bool TryAnalyzeInvocation(
			InvocationExpressionSyntax invocation,
			SemanticModel semanticModel,
			AspectSelectionPlan plan,
			CancellationToken cancellationToken,
			out AnalyzedInvocation analyzedInvocation)
		{
			analyzedInvocation = null!;

			var info = semanticModel.GetSymbolInfo(invocation, cancellationToken);

			if (info.Symbol is not IMethodSymbol method)
				return false;

			var attributes = plan.TryGetSelectedAttributes(method, out var selectedAttributes)
				? selectedAttributes
				: plan.MayTargetExternalMethods
					? GetSelectedAttributes(method)
					: [];

			if (attributes.Count == 0)
				return false;

			analyzedInvocation = new AnalyzedInvocation(invocation, method, attributes);
			return true;
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

		public List<AttributeInfo> GetSelectedAttributes(IMethodSymbol method)
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
						_trace?.IncrementCounter("Selected aspect applications");
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
				if (!HasTargetFilter(filterAttribute) &&
					!HasDefaultTargetFilter(filterAttribute.AttributeClass))
					continue;

				if (CreateAppliedAspectFilterSet(filterAttribute) is {} filterSet)
				{
					result.Add(filterSet);
					_trace?.IncrementCounter("Target filters");
					_trace?.AddTargetFilter(
						result.Count - 1,
						filterSet.Filters.Plan.Classification,
						filterSet.Filters.Plan.CanFilterDeclarations,
						filterSet.Filters.Plan.CanPrefilterCallSites,
						filterSet.Filters.Plan.RequiresTargetSemanticModel,
						filterSet.Filters.Plan.RequiresCallSiteSemanticModel,
						filterSet.Filters.Plan.MayTargetExternalMethods);
				}
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
					if (!AspectSymbols.HasNamedArgument(filterAttribute, "TargetFilter") &&
						!HasDefaultTargetFilter(filterAttribute, semanticModel))
						continue;

					if (CreateAppliedAspectFilterSet(filterAttribute, semanticModel) is {} filterSet)
					{
						result.Add(filterSet);
						_trace?.IncrementCounter("Target filters");
						_trace?.AddTargetFilter(
							result.Count - 1,
							filterSet.Filters.Plan.Classification,
							filterSet.Filters.Plan.CanFilterDeclarations,
							filterSet.Filters.Plan.CanPrefilterCallSites,
							filterSet.Filters.Plan.RequiresTargetSemanticModel,
							filterSet.Filters.Plan.RequiresCallSiteSemanticModel,
							filterSet.Filters.Plan.MayTargetExternalMethods);
					}
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

			var targetFilter = GetNamedFilterValue(
				filterAttribute,
				semanticModel,
				"TargetFilter");
			var filters = CompileAspectFilters(definition.DefaultTargetFilter, targetFilter, filterAttribute.GetLocation());

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
			var targetFilter = GetNamedFilterValue(filterAttribute, "TargetFilter");
			var filters      = CompileAspectFilters(definition.DefaultTargetFilter, targetFilter, location);

			if (filters.IsEmpty)
				return null;

			return new AspectFilterSet(
				definition with
				{
					AppliedAttributeData = filterAttribute
				},
				filters);
		}

		object? GetNamedFilterValue(
			AttributeSyntax attribute,
			SemanticModel semanticModel,
			string name)
		{
			foreach (var arg in attribute.ArgumentList?.Arguments ?? default)
			{
				if (arg.NameEquals?.Name.Identifier.ValueText != name)
					continue;

				return AspectSymbols.GetAttributeArgumentValue(arg.Expression, semanticModel);
			}

			return null;
		}

		object? GetNamedFilterValue(
			AttributeData attribute,
			string name)
		{
			foreach (var arg in attribute.NamedArguments)
			{
				if (arg.Key != name)
					continue;

				var value = arg.Value.Kind == TypedConstantKind.Array
					? arg.Value.Values.Select(static v => v.Value).ToArray()
					: arg.Value.Value;

				return value;
			}

			return null;
		}

		AspectFilters.TargetFilterSet CompileAspectFilters(string? defaultTargetFilter, object? targetFilter, Location? location)
		{
			var filters = new List<string?>();

			if (targetFilter is null)
				filters.Add("method: *");

			filters.Add(defaultTargetFilter);

			if (targetFilter is object?[] values)
				filters.AddRange(values.Select(static value => value as string));
			else
				filters.Add(targetFilter as string);

			return AspectFilters.GetFilters(filters, ReportFilterDiagnostic);

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
				_trace?.IncrementCounter("Applied target filter matches");
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

		bool HasDefaultTargetFilter(INamedTypeSymbol? aspectClass)
		{
			return aspectClass is not null &&
				_registry.TryGetDefinition(aspectClass, out var definition) &&
				!string.IsNullOrWhiteSpace(definition.DefaultTargetFilter);
		}

		bool HasDefaultTargetFilter(AttributeSyntax filterAttribute, SemanticModel semanticModel)
		{
			return semanticModel.GetSymbolInfo(filterAttribute).Symbol is IMethodSymbol { ContainingType: var aspectClass } &&
				HasDefaultTargetFilter(aspectClass);
		}

		static bool HasTargetFilter(AttributeData attribute)
		{
			return attribute.NamedArguments.Any(static a => a.Key == "TargetFilter");
		}

		bool MayMatchCallSite(InvocationExpressionSyntax invocation, AspectSelectionPlan plan)
		{
			if (plan.MayTargetExternalMethods)
				return true;

			if (!TryGetInvocationName(invocation, out var name))
			{
				_trace?.IncrementCounter("Call-site prefilter rejected missing method name");
				return false;
			}

			if (!plan.TargetMethodBuckets.TryGetValue(name, out var bucket))
			{
				_trace?.IncrementCounter("Call-site prefilter rejected missing method name");
				return false;
			}

			if (TryGetGenericArity(invocation, out var genericArity) &&
				!bucket.AllowsGenericArity(genericArity))
			{
				_trace?.IncrementCounter("Call-site prefilter rejected generic arity");
				return false;
			}

			if (!bucket.AllowsArgumentCount(invocation.ArgumentList.Arguments.Count))
			{
				_trace?.IncrementCounter("Call-site prefilter rejected argument count");
				return false;
			}

			if (TryGetExplicitReceiverTypeName(invocation, out var receiverTypeName) &&
				!bucket.ContainingTypeNames.Contains(receiverTypeName))
			{
				_trace?.IncrementCounter("Call-site prefilter rejected receiver type");
				return false;
			}

			return true;
		}

		static bool TryGetInvocationName(InvocationExpressionSyntax invocation, out string name)
		{
			return TryGetName(invocation.Expression, out name);

			static bool TryGetName(ExpressionSyntax expression, out string name)
			{
				switch (expression)
				{
					case IdentifierNameSyntax identifier:
						name = identifier.Identifier.ValueText;
						return true;

					case GenericNameSyntax generic:
						name = generic.Identifier.ValueText;
						return true;

					case MemberAccessExpressionSyntax memberAccess:
						return TryGetName(memberAccess.Name, out name);

					case MemberBindingExpressionSyntax memberBinding:
						return TryGetName(memberBinding.Name, out name);

					default:
						name = "";
						return false;
				}
			}
		}

		static bool TryGetGenericArity(InvocationExpressionSyntax invocation, out int genericArity)
		{
			return TryGetGenericArity(invocation.Expression, out genericArity);

			static bool TryGetGenericArity(ExpressionSyntax expression, out int genericArity)
			{
				switch (expression)
				{
					case GenericNameSyntax generic:
						genericArity = generic.TypeArgumentList.Arguments.Count;
						return true;

					case MemberAccessExpressionSyntax memberAccess:
						return TryGetGenericArity(memberAccess.Name, out genericArity);

					case MemberBindingExpressionSyntax memberBinding:
						return TryGetGenericArity(memberBinding.Name, out genericArity);

					default:
						genericArity = 0;
						return false;
				}
			}
		}

		static bool TryGetExplicitReceiverTypeName(InvocationExpressionSyntax invocation, out string receiverTypeName)
		{
			receiverTypeName = "";

			if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
				return false;

			if (!TryGetTypeLikeName(memberAccess.Expression, out receiverTypeName))
				return false;

			return true;

			static bool TryGetTypeLikeName(ExpressionSyntax expression, out string name)
			{
				switch (expression)
				{
					case IdentifierNameSyntax identifier when IsTypeLike(identifier.Identifier.ValueText):
						name = identifier.Identifier.ValueText;
						return true;

					case MemberAccessExpressionSyntax memberAccess when TryGetTypeLikeName(memberAccess.Name, out name):
						return true;

					default:
						name = "";
						return false;
				}
			}

			static bool IsTypeLike(string name)
			{
				return name.Length > 0 && char.IsUpper(name[0]);
			}
		}
	}
}
