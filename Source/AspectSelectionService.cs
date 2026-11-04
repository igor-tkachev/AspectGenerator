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
		readonly Dictionary<SyntaxTree,SemanticModel> _semanticModels = new();
		readonly Dictionary<IMethodSymbol,IMethodSymbol> _genericShapes = new(SymbolEqualityComparer.Default);
		readonly Dictionary<IMethodSymbol,IMethodSymbol> _reducedGenericShapes = new(SymbolEqualityComparer.Default);
		List<INamedTypeSymbol>? _sourceTypes;

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

		/// <summary>
		/// Reports the target filter diagnostics of a design-time build without selecting a target method or binding a call:
		/// the constructor has compiled the assembly-level filters; this compiles the type-level ones and checks method-level
		/// aspect applications for a <c>TargetFilter</c>. Only attributes that can carry a filter are bound: those with a
		/// <c>TargetFilter</c> argument, and type-level ones named after an aspect that has a <c>DefaultTargetFilter</c>.
		/// A build reports the same diagnostics while it selects the target methods.
		/// </summary>
		public void ReportDesignTimeFilterDiagnostics(ImmutableArray<AttributeSyntax> attributes, CancellationToken cancellationToken)
		{
			HashSet<string>? defaultFilterNames = null;

			foreach (var attribute in attributes)
			{
				if (cancellationToken.IsCancellationRequested)
					break;

				if (attribute.Parent is not AttributeListSyntax list)
					continue;

				var hasTargetFilter = AspectSymbols.HasNamedArgument(attribute, "TargetFilter");

				if (list.Parent is TypeDeclarationSyntax typeDeclaration)
				{
					if (!hasTargetFilter)
					{
						defaultFilterNames ??= GetDefaultTargetFilterAspectNames(cancellationToken);

						if (!defaultFilterNames.Contains(GetRightmostName(attribute.Name)))
							continue;
					}

					var semanticModel = GetSemanticModel(attribute.SyntaxTree);

					// A build compiles the filters of a type while it selects the type's methods, so a type without any
					// reports nothing.
					if (!MayHaveSelectableMethods(typeDeclaration, semanticModel, cancellationToken))
						continue;

					CreateAppliedAspectFilterSet(attribute, semanticModel);
				}
				else if (list.Parent is MethodDeclarationSyntax && hasTargetFilter && list.Target is null or { Identifier.ValueText: "method" })
				{
					var semanticModel = GetSemanticModel(attribute.SyntaxTree);

					if (semanticModel.GetSymbolInfo(attribute, cancellationToken).Symbol is not IMethodSymbol { ContainingType: var aspectClass } ||
						!AspectSymbols.IsConditionalAspectEnabled(aspectClass, _compilation, attribute.SyntaxTree) ||
						!_registry.TryGetDefinition(aspectClass, out _))
						continue;

					ReportMethodLevelTargetFilter(attribute.GetLocation());
				}
			}
		}

		/// <summary>
		/// A record (its compiler-generated methods are selectable), or a type one of whose declarations declares a method.
		/// </summary>
		static bool MayHaveSelectableMethods(TypeDeclarationSyntax typeDeclaration, SemanticModel semanticModel, CancellationToken cancellationToken)
		{
			if (typeDeclaration is RecordDeclarationSyntax || HasMethodDeclaration(typeDeclaration))
				return true;

			if (!typeDeclaration.Modifiers.Any(static m => m.ValueText == "partial"))
				return false;

			if (semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) is not {} type)
				return false;

			foreach (var reference in type.DeclaringSyntaxReferences)
				if (reference.GetSyntax(cancellationToken) is TypeDeclarationSyntax declaration && HasMethodDeclaration(declaration))
					return true;

			return false;

			static bool HasMethodDeclaration(TypeDeclarationSyntax declaration)
			{
				return declaration.Members.Any(static member => member is MethodDeclarationSyntax);
			}
		}

		/// <summary>
		/// The names a type-level application of an aspect with a <c>DefaultTargetFilter</c> can be written with: the aspect
		/// class name with and without the <c>Attribute</c> suffix, and every using alias. Whether an alias denotes such an
		/// aspect is left to binding: an alias may name another alias, in another file. The aspects are the ones declared
		/// here and the ones of referenced aspect libraries.
		/// </summary>
		HashSet<string> GetDefaultTargetFilterAspectNames(CancellationToken cancellationToken)
		{
			var names = new HashSet<string>(StringComparer.Ordinal);

			foreach (var symbol in _registry.AspectAttributes.Keys)
				if (symbol is INamedTypeSymbol aspectClass && HasDefaultTargetFilter(aspectClass))
					AddName(aspectClass.Name);

			foreach (var assembly in _compilation.SourceModule.ReferencedAssemblySymbols)
				if (assembly.GetTypeByMetadataName("AspectGenerator.AspectAttribute") is not null)
					AddExternalAspectNames(assembly.GlobalNamespace);

			if (names.Count == 0)
				return names;

			var aliases = new List<string>();

			foreach (var tree in _compilation.SyntaxTrees)
				AddAliases(tree.GetRoot(cancellationToken), aliases);

			foreach (var alias in aliases)
				AddName(alias);

			return names;

			void AddName(string name)
			{
				names.Add(name);

				if (name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length)
					names.Add(name.Substring(0, name.Length - "Attribute".Length));
			}

			void AddExternalAspectNames(INamespaceOrTypeSymbol container)
			{
				foreach (var member in container.GetMembers())
				{
					if (member is INamespaceSymbol namespaceSymbol)
						AddExternalAspectNames(namespaceSymbol);
					else if (member is INamedTypeSymbol type)
					{
						if (_registry.TryGetExternalAspectDefinition(type, out _) && HasDefaultTargetFilter(type))
							AddName(type.Name);

						AddExternalAspectNames(type);
					}
				}
			}

			static void AddAliases(SyntaxNode node, List<string> aliases)
			{
				var usings = node switch
				{
					CompilationUnitSyntax compilationUnit               => compilationUnit.Usings,
					BaseNamespaceDeclarationSyntax namespaceDeclaration => namespaceDeclaration.Usings,
					_                                                   => default
				};

				foreach (var usingDirective in usings)
					if (usingDirective.Alias is {} alias)
						aliases.Add(alias.Name.Identifier.ValueText);

				var members = node switch
				{
					CompilationUnitSyntax compilationUnit               => compilationUnit.Members,
					BaseNamespaceDeclarationSyntax namespaceDeclaration => namespaceDeclaration.Members,
					_                                                   => default
				};

				foreach (var member in members)
					if (member is BaseNamespaceDeclarationSyntax)
						AddAliases(member, aliases);
			}
		}

		static string GetRightmostName(NameSyntax name)
		{
			return name switch
			{
				QualifiedNameSyntax qualified           => qualified.Right.Identifier.ValueText,
				AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.ValueText,
				SimpleNameSyntax simple                 => simple.Identifier.ValueText,
				_                                       => ""
			};
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
				var semanticModel = GetSemanticModel(invocation.SyntaxTree);
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

		/// <summary>
		/// One semantic model per tree: a semantic model caches what it binds, so a new model per call site would bind the
		/// enclosing method body again for every call in it.
		/// </summary>
		SemanticModel GetSemanticModel(SyntaxTree tree)
		{
			lock (_semanticModels)
			{
				if (!_semanticModels.TryGetValue(tree, out var semanticModel))
				{
					semanticModel = _compilation.GetSemanticModel(tree);
					_semanticModels.Add(tree, semanticModel);
				}

				return semanticModel;
			}
		}

		public AspectSelectionPlan BuildSelectionPlan(
			ImmutableArray<MethodDeclarationSyntax> methodDeclarations,
			bool                                    externalAspectDefinitionsPossible,
			CancellationToken                       cancellationToken)
		{
			var selectedMethods    = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
			var selectedAttributes = new Dictionary<IMethodSymbol,List<AttributeInfo>>(SymbolEqualityComparer.Default);
			var buckets            = new Dictionary<string,TargetMethodBucket>(StringComparer.Ordinal);
			var mayTargetExternalMethods  = externalAspectDefinitionsPossible;
			var externalTargetMethodNames = new HashSet<string>(StringComparer.Ordinal);

			// Assembly-level filters also select methods of referenced assemblies, which have no declaration here.
			// Type-level filters only select methods of the source type they are applied to.
			foreach (var filterSet in _assemblyFilters)
			{
				var filterPlan = filterSet.Filters.Plan;

				if (filterPlan.MayTargetAnyExternalMethod)
					mayTargetExternalMethods = true;
				else
				{
					externalTargetMethodNames.UnionWith(filterPlan.ExternalMethodNames);

					if (filterPlan.ExternalRootSegments.Any(ReferencedAssembliesContainRoot))
						mayTargetExternalMethods = true;
				}
			}

			var targetMethodDeclarations = 0;

			foreach (var methodDeclaration in methodDeclarations)
			{
				if (cancellationToken.IsCancellationRequested)
					break;

				targetMethodDeclarations++;
				var semanticModel = GetSemanticModel(methodDeclaration.SyntaxTree);

				if (semanticModel.GetDeclaredSymbol(methodDeclaration, cancellationToken) is not IMethodSymbol method)
					continue;

				SelectMethod(method);
			}

			// Records get ToString, Equals, GetHashCode, PrintMembers and Deconstruct from the compiler, with no
			// declaration the syntax provider could see; target filters still select them.
			foreach (var type in GetSourceTypes())
			{
				if (cancellationToken.IsCancellationRequested)
					break;

				if (!type.IsRecord)
					continue;

				foreach (var member in type.GetMembers())
					if (member is IMethodSymbol { IsImplicitlyDeclared: true, MethodKind: MethodKind.Ordinary, CanBeReferencedByName: true } method)
						SelectMethod(method);
			}

			_trace?.SetCounter("Target method declarations", targetMethodDeclarations);
			_trace?.SetCounter("Target methods after declaration filter", selectedMethods.Count);
			_trace?.SetCounter("Target methods after semantic filter", selectedMethods.Count);

			if (!mayTargetExternalMethods)
				AddReceiverTypeNames(buckets.Values, cancellationToken);

			return new AspectSelectionPlan(selectedMethods, selectedAttributes, buckets, mayTargetExternalMethods, externalTargetMethodNames);

			void SelectMethod(IMethodSymbol method)
			{
				var attributes = GetSelectedAttributes(method);

				if (attributes.Count == 0)
					return;

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
		}

		/// <summary>
		/// Every named type declared in this compilation, nested types included; collected once.
		/// </summary>
		List<INamedTypeSymbol> GetSourceTypes()
		{
			if (_sourceTypes is not null)
				return _sourceTypes;

			var types = new List<INamedTypeSymbol>();

			AddTypes(_compilation.Assembly.GlobalNamespace, types);
			_sourceTypes = types;

			return types;

			static void AddTypes(INamespaceOrTypeSymbol container, List<INamedTypeSymbol> types)
			{
				foreach (var member in container.GetMembers())
				{
					if (member is INamespaceSymbol namespaceSymbol)
						AddTypes(namespaceSymbol, types);
					else if (member is INamedTypeSymbol type)
					{
						types.Add(type);
						AddTypes(type, types);
					}
				}
			}
		}

		bool ReferencedAssembliesContainRoot(string root)
		{
			foreach (var assembly in _compilation.SourceModule.ReferencedAssemblySymbols)
				if (assembly.GlobalNamespace.GetMembers(root).Any())
					return true;

			return false;
		}

		void AddReceiverTypeNames(IEnumerable<TargetMethodBucket> buckets, CancellationToken cancellationToken)
		{
			var staticBuckets = buckets.Where(static bucket => bucket.CanRejectByTypeReceiver).ToList();

			if (staticBuckets.Count == 0)
				return;

			// A static method can be called through its containing type, through any type derived from it,
			// and through a using alias of any of them. Derived types of a source type can only be source types.
			var aliases = new HashSet<string>(StringComparer.Ordinal);

			foreach (var tree in _compilation.SyntaxTrees)
				AddAliases(tree.GetRoot(cancellationToken), aliases);

			var sourceTypes = GetSourceTypes();

			// Direct source subtypes of each type, built once; a bucket then walks only its own containing types' subtrees.
			var derivedTypes = new Dictionary<INamedTypeSymbol,List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);

			foreach (var type in sourceTypes)
			{
				if (type.BaseType?.OriginalDefinition is not {} baseType)
					continue;

				if (!derivedTypes.TryGetValue(baseType, out var derived))
				{
					derived = new List<INamedTypeSymbol>();
					derivedTypes.Add(baseType, derived);
				}

				derived.Add(type);
			}

			foreach (var bucket in staticBuckets)
			{
				var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
				var pending = new Stack<INamedTypeSymbol>(bucket.Methods.Select(static method => method.ContainingType.OriginalDefinition));

				bucket.ReceiverTypeNames.UnionWith(bucket.ContainingTypeNames);
				bucket.ReceiverTypeNames.UnionWith(aliases);

				while (pending.Count > 0)
				{
					var type = pending.Pop();

					if (!visited.Add(type) || !derivedTypes.TryGetValue(type, out var derived))
						continue;

					foreach (var derivedType in derived)
					{
						bucket.ReceiverTypeNames.Add(derivedType.Name);
						pending.Push(derivedType.OriginalDefinition);
					}
				}
			}

			static void AddAliases(SyntaxNode node, HashSet<string> aliases)
			{
				var usings = node switch
				{
					CompilationUnitSyntax compilationUnit           => compilationUnit.Usings,
					BaseNamespaceDeclarationSyntax namespaceDeclaration => namespaceDeclaration.Usings,
					_                                               => default
				};

				foreach (var usingDirective in usings)
					if (usingDirective.Alias is {} alias)
						aliases.Add(alias.Name.Identifier.ValueText);

				var members = node switch
				{
					CompilationUnitSyntax compilationUnit           => compilationUnit.Members,
					BaseNamespaceDeclarationSyntax namespaceDeclaration => namespaceDeclaration.Members,
					_                                               => default
				};

				foreach (var member in members)
					if (member is BaseNamespaceDeclarationSyntax)
						AddAliases(member, aliases);
			}
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
				: plan.MayTargetExternalMethod(method)
					? GetSelectedAttributes(method)
					: [];

			if (attributes.Count == 0)
				return false;

			if (InterceptorShape.GetUnsupportedReason(invocation, method, attributes, semanticModel, cancellationToken) is {} reason)
			{
				ReportNotIntercepted(invocation, method, attributes, reason);
				return false;
			}

			var genericShape = InterceptorShape.NeedsGenericInterceptor(method, _compilation)
				? GetGenericShape(method)
				: null;

			analyzedInvocation = new AnalyzedInvocation(invocation, method, attributes, genericShape);
			return true;
		}

		/// <summary>
		/// One shape symbol per target, so that every call of the target that needs a generic interceptor shares it.
		/// </summary>
		IMethodSymbol GetGenericShape(IMethodSymbol method)
		{
			var key = (method.ReducedFrom ?? method).OriginalDefinition;

			// An instance-form extension call needs an interceptor with a 'this' parameter, a static-form call one without.
			var shapes = method.MethodKind == MethodKind.ReducedExtension ? _reducedGenericShapes : _genericShapes;

			lock (_cacheGate)
			{
				if (!shapes.TryGetValue(key, out var shape))
				{
					shape = InterceptorShape.GetGenericShape(method)!;
					shapes.Add(key, shape);
				}

				return shape;
			}
		}

		/// <summary>
		/// A call that cannot be intercepted is left as it is. It is reported with AG0003, a warning, when the aspect was
		/// applied to the target method itself, and with AG0006, information, when only a target filter selected it: a
		/// broad filter is expected to skip what it cannot rewrite. The two ids can be configured separately.
		/// </summary>
		void ReportNotIntercepted(InvocationExpressionSyntax invocation, IMethodSymbol method, List<AttributeInfo> attributes, string reason)
		{
			var isApplied = InterceptorShape.IsAppliedToTarget(method, attributes);

			_diagnostics.Report(
				isApplied ? AspectDiagnosticID.CannotIntercept : AspectDiagnosticID.FilterSelectedCallNotIntercepted,
				$"Call to '{method.ToDisplayString()}' is not intercepted: {reason}.",
				invocation.GetLocation(),
				isApplied ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info);
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

				var semanticModel = GetSemanticModel(typeDeclaration.SyntaxTree);

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

			ReportMethodLevelTargetFilter(location);
		}

		void ReportMethodLevelTargetFilter(Location? location)
		{
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

		/// <summary>
		/// The invocation shapes the generator can intercept; other invocations are never candidates.
		/// </summary>
		public static bool IsCandidateInvocation(SyntaxNode node)
		{
			return node is InvocationExpressionSyntax
			{
				Expression: MemberAccessExpressionSyntax or IdentifierNameSyntax or GenericNameSyntax or MemberBindingExpressionSyntax
			};
		}

		/// <summary>
		/// Rejects a call site before symbol binding when the plan proves it cannot bind to a selected method.
		/// </summary>
		public bool MayMatchCallSite(InvocationExpressionSyntax invocation, AspectSelectionPlan plan)
		{
			if (plan.MayTargetExternalMethods)
				return true;

			if (!TryGetInvocationName(invocation, out var name))
			{
				_trace?.IncrementCounter("Call-site prefilter rejected missing method name");
				return false;
			}

			// A filter may select a method of a referenced assembly under this name; only binding can tell.
			if (plan.ExternalTargetMethodNames.Contains(name))
				return true;

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

			if (bucket.CanRejectByTypeReceiver &&
				TryGetExplicitReceiverName(invocation, out var receiverName) &&
				!bucket.ReceiverTypeNames.Contains(receiverName))
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

		static bool TryGetExplicitReceiverName(InvocationExpressionSyntax invocation, out string receiverName)
		{
			receiverName = "";

			if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
				return false;

			// The receiver is a simple name or ends in one (Name, A.B.Name, global::A.Name). It may denote a type,
			// an alias, a namespace or a value; the caller decides what a mismatch proves.
			switch (memberAccess.Expression)
			{
				case IdentifierNameSyntax identifier:
					receiverName = identifier.Identifier.ValueText;
					return true;

				case MemberAccessExpressionSyntax { Name: IdentifierNameSyntax identifier }:
					receiverName = identifier.Identifier.ValueText;
					return true;

				case QualifiedNameSyntax { Right: IdentifierNameSyntax identifier }:
					receiverName = identifier.Identifier.ValueText;
					return true;

				default:
					return false;
			}
		}
	}
}
