using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

using AspectGenerator.Cascade;
using AspectGenerator.Selection;
using AspectGenerator.TargetFilterBinding;
using AspectGenerator.TargetFilterFacts;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspectGenerator
{
	/// <summary>
	/// Decides which methods get which aspects, from the target filters of every aspect read as blocks of the cascade:
	/// the aspect's <c>DefaultTargetFilter</c>, the assembly attributes, the attributes on a method's type and on the method
	/// itself, and for a method a referenced library placed the aspect on, the library's own attributes.
	/// <para>
	/// A method is decided on its definition once; where a condition names a type in the place of a type parameter of the
	/// definition, each call is decided with the type arguments it binds (<see cref="FilterTarget"/>). An error in any filter
	/// of an aspect turns the aspect off: it selects nothing in the compilation, and every error is reported.
	/// </para>
	/// </summary>
	sealed class AspectSelectionService
	{
		/// <summary>
		/// What an aspect is built with when the attributes that select a method write different constructor arguments: the
		/// aspect's parameterless constructor, the default a conflicting setting falls back to, when the aspect has one. An
		/// aspect whose constructors all take arguments has no default to fall back to; its constructor arguments then stay
		/// those of the most specific attribute that selects the method. The conflict is an error either way.
		/// </summary>
		const bool ConflictingConstructorUsesParameterlessConstructor = true;

		/// <summary>
		/// Several applications of one aspect written on one method (an aspect that allows multiple) are each applied, as
		/// separate aspects ordered as written, each decided as the only block of its method. Otherwise they are alternatives
		/// of one level, as several assembly attributes or attributes on several <c>partial</c> declarations of a type are: one
		/// aspect, whose settings conflict where the attributes write different values.
		/// </summary>
		const bool MethodApplicationsStack = true;

		readonly Compilation                                   _compilation;
		readonly GeneratorExecutionOptions                     _options;
		readonly AspectDefinitionRegistry                      _registry;
		readonly IAspectDiagnosticSink                         _diagnostics;
		readonly GeneratorTraceReport?                         _trace;
		readonly object                                        _cacheGate = new();
		readonly Dictionary<SyntaxTree,SemanticModel>          _semanticModels = new();
		readonly Dictionary<IMethodSymbol,IMethodSymbol>       _genericShapes = new(SymbolEqualityComparer.Default);
		readonly Dictionary<IMethodSymbol,IMethodSymbol>       _reducedGenericShapes = new(SymbolEqualityComparer.Default);
		readonly Dictionary<string,int>                        _filterSelectedSkips = new(StringComparer.Ordinal);
		readonly FactContext                                   _facts;
		readonly TypeShapes                                    _shapes = new();
		readonly Dictionary<INamedTypeSymbol,AspectCascade?>   _aspects = new(SymbolEqualityComparer.Default);
		readonly List<AspectCascade>                           _assemblyAspects = new();
		readonly Dictionary<ISymbol,ImmutableArray<AspectCascade>> _appliedAspects = new(SymbolEqualityComparer.Default);
		readonly Dictionary<IMethodSymbol,MethodSelection>     _selections = new(SymbolEqualityComparer.Default);
		readonly Dictionary<IMethodSymbol,SymbolMethodFacts>   _definitionFacts = new(SymbolEqualityComparer.Default);
		readonly Dictionary<IAssemblySymbol,LibraryFacts>      _libraries = new(SymbolEqualityComparer.Default);
		readonly HashSet<FilterBlock>                          _reportedBlocks = new();
		List<INamedTypeSymbol>? _sourceTypes;
		HashSet<string>?        _defaultFilterAspectNames;
		int                     _traceFilters;

		public AspectSelectionService(
			Compilation              compilation,
			GeneratorExecutionOptions options,
			AspectDefinitionRegistry registry,
			IAspectDiagnosticSink    diagnostics,
			GeneratorTraceReport?    trace = null)
		{
			_compilation = compilation;
			_options     = options;
			_registry    = registry;
			_diagnostics = diagnostics;
			_trace       = trace;
			_facts       = new FactContext(compilation);

			lock (_cacheGate)
			{
				// The default filters of the aspects declared here are read whether or not they are applied: their errors are
				// reported at their [Aspect] attributes.
				foreach (var symbol in _registry.AspectAttributes.Keys)
					if (symbol is INamedTypeSymbol aspectClass)
						GetAspect(aspectClass);

				foreach (var attribute in _compilation.Assembly.GetAttributes())
					if (attribute.AttributeClass is {} attributeClass && GetAspect(attributeClass) is {} aspect && !aspect.Assembly.IsEmpty && !_assemblyAspects.Contains(aspect))
						_assemblyAspects.Add(aspect);

				foreach (var aspect in _assemblyAspects)
					foreach (var block in aspect.Assembly)
						TraceFilter(aspect, block);

				_trace?.SetCounter("Target filters", _traceFilters);
			}
		}

		/// <summary>
		/// Reports the target filter diagnostics of a design-time build without selecting a target method or binding a call:
		/// the constructor has read the assembly filters and the default filters of the aspects declared here; this reads
		/// every other filter written in the source (<see cref="ReadFilters"/>). A build reports the same diagnostics.
		/// </summary>
		public void ReportDesignTimeFilterDiagnostics(ImmutableArray<AttributeSyntax> attributes, CancellationToken cancellationToken)
		{
			ReadFilters(attributes, cancellationToken);
		}

		/// <summary>
		/// Reads the filter of every aspect attribute on a type, a method or a local function that writes a <c>TargetFilter</c>,
		/// so that an error in any of them is reported and turns its aspect off before anything is selected. An attribute
		/// without a filter is bound only when it may name an aspect with a <c>DefaultTargetFilter</c>: reading that default
		/// reports its errors where the aspect is used.
		/// </summary>
		public void ReadFilters(ImmutableArray<AttributeSyntax> attributes, CancellationToken cancellationToken)
		{
			foreach (var attribute in attributes)
			{
				if (cancellationToken.IsCancellationRequested)
					break;

				if (attribute.Parent is not AttributeListSyntax list || list.Parent is not (TypeDeclarationSyntax or MethodDeclarationSyntax or LocalFunctionStatementSyntax))
					continue;

				// [return: ...] applies the attribute to the return value, [type: ...] is the type's own.
				if (list.Target is not null && list.Target.Identifier.ValueText is not ("method" or "type"))
					continue;

				if (!AspectSymbols.HasNamedArgument(attribute, FilterBlockSources.TargetFilterArgument))
				{
					_defaultFilterAspectNames ??= GetDefaultTargetFilterAspectNames(cancellationToken);

					if (!_defaultFilterAspectNames.Contains(GetRightmostName(attribute.Name)))
						continue;
				}

				var semanticModel = GetSemanticModel(attribute.SyntaxTree);

				if (semanticModel.GetSymbolInfo(attribute, cancellationToken).Symbol is not IMethodSymbol { ContainingType: var aspectClass } ||
					!AspectSymbols.IsConditionalAspectEnabled(aspectClass, _compilation, attribute.SyntaxTree))
					continue;

				var declared = semanticModel.GetDeclaredSymbol(list.Parent, cancellationToken);

				lock (_cacheGate)
				{
					if (GetAspect(aspectClass) is not {} aspect)
						continue;

					if (declared is INamedTypeSymbol type)
						GetTypeScope(aspect, type);
					else if (declared is IMethodSymbol method)
						GetMethodBlocks(aspect, method);
				}
			}
		}

		/// <summary>
		/// The names an application of an aspect with a <c>DefaultTargetFilter</c> can be written with: the aspect class name
		/// with and without the <c>Attribute</c> suffix, and every using alias. Whether an alias denotes such an aspect is left
		/// to binding: an alias may name another alias, in another file. The aspects are the ones declared here and the ones
		/// of referenced aspect libraries.
		/// </summary>
		HashSet<string> GetDefaultTargetFilterAspectNames(CancellationToken cancellationToken)
		{
			var names = new HashSet<string>(StringComparer.Ordinal);

			foreach (var symbol in _registry.AspectAttributes.Keys)
				if (symbol is INamedTypeSymbol aspectClass && HasDefaultTargetFilter(aspectClass))
					AddName(aspectClass.Name);

			foreach (var assembly in AspectDefinitionRegistry.GetAspectDefinitionAssemblies(_compilation))
				AddExternalAspectNames(assembly.GlobalNamespace);

			if (names.Count == 0)
				return names;

			var aliases = new HashSet<string>(StringComparer.Ordinal);

			foreach (var tree in _compilation.SyntaxTrees)
				if (!AspectSymbols.IsGeneratedInterceptorsTree(tree))
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
		}

		bool HasDefaultTargetFilter(INamedTypeSymbol aspectClass)
		{
			return _registry.TryGetDefinition(aspectClass, out var definition) && !string.IsNullOrWhiteSpace(definition.DefaultTargetFilter);
		}

		static void AddAliases(SyntaxNode node, HashSet<string> aliases)
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

			// A filter read only when a call reached it (of a library, of a local function) may have turned its aspect off
			// after other calls had got the aspect: the aspect selects nothing, at those calls either.
			lock (_cacheGate)
			{
				if (_aspects.Values.Any(static a => a is { IsOff: true }))
				{
					var kept = ImmutableArray.CreateBuilder<AnalyzedInvocation>(result.Count);

					foreach (var invocation in result)
					{
						var attributes = invocation.Attributes.Where(a => _aspects.TryGetValue(a.AttributeClass, out var aspect) && aspect is { IsOff: false }).ToList();

						if (attributes.Count == invocation.Attributes.Count)
							kept.Add(invocation);
						else if (attributes.Count > 0)
							kept.Add(invocation with { Attributes = attributes });
					}

					return kept.ToImmutable();
				}
			}

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

		/// <summary>
		/// Decides every method declared here: the methods of the declarations, and the members the compiler declares for
		/// records. Every filter on their types and on themselves is read first, so that an error in any of them has turned its
		/// aspect off before the first method is decided. What the assembly filters say of the names of methods of other
		/// assemblies limits which calls are bound (<see cref="MayMatchCallSite"/>).
		/// </summary>
		public AspectSelectionPlan BuildSelectionPlan(
			ImmutableArray<MethodDeclarationSyntax> methodDeclarations,
			bool                                    externalAspectDefinitionsPossible,
			CancellationToken                       cancellationToken)
		{
			var methods = new List<IMethodSymbol>(methodDeclarations.Length);

			foreach (var methodDeclaration in methodDeclarations)
			{
				if (cancellationToken.IsCancellationRequested)
					break;

				var semanticModel = GetSemanticModel(methodDeclaration.SyntaxTree);

				if (semanticModel.GetDeclaredSymbol(methodDeclaration, cancellationToken) is IMethodSymbol method)
					methods.Add(method);
			}

			_trace?.SetCounter("Target method declarations", methods.Count);

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
						methods.Add(method);
			}

			var selectedMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
			var buckets         = new Dictionary<string,TargetMethodBucket>(StringComparer.Ordinal);

			lock (_cacheGate)
			{
				foreach (var method in methods)
				{
					if (cancellationToken.IsCancellationRequested)
						break;

					foreach (var aspect in GetAppliedAspects(method.ContainingType))
						GetTypeScope(aspect, method.ContainingType);

					foreach (var aspect in GetAppliedAspects(method))
						GetMethodBlocks(aspect, method);
				}

				foreach (var method in methods)
				{
					if (cancellationToken.IsCancellationRequested)
						break;

					var definition = method.OriginalDefinition;
					var selection  = GetSelection(definition);

					ReportNotApplied(definition, selection);

					if (!selection.MaySelect || !selectedMethods.Add(definition))
						continue;

					if (!buckets.TryGetValue(definition.Name, out var bucket))
					{
						bucket = new TargetMethodBucket(definition.Name);
						buckets.Add(definition.Name, bucket);
					}

					bucket.Add(definition);
				}
			}

			_trace?.SetCounter("Target methods after declaration filter", selectedMethods.Count);
			_trace?.SetCounter("Target methods after semantic filter", selectedMethods.Count);

			var mayTargetExternalMethods = externalAspectDefinitionsPossible;
			var externalTerms            = ImmutableArray.CreateBuilder<AnchorTerm>();

			lock (_cacheGate)
			{
				foreach (var aspect in _assemblyAspects)
				{
					if (aspect.IsOff)
						continue;

					foreach (var block in aspect.Assembly)
					{
						foreach (var term in block.AnchorsUnder(aspect.Default).Terms)
						{
							if (!MayHoldOfReferencedMethod(term))
								continue;

							if (term.Names.IsEmpty)
								mayTargetExternalMethods = true;
							else if (!externalTerms.Contains(term))
								externalTerms.Add(term);
						}
					}
				}
			}

			if (!mayTargetExternalMethods)
				AddReceiverTypeNames(buckets.Values, cancellationToken);

			_trace?.SetCounter("Target filters", _traceFilters);

			return new AspectSelectionPlan(selectedMethods, buckets, mayTargetExternalMethods, externalTerms.ToImmutable());
		}

		/// <summary>
		/// Whether a term of an assembly filter can hold of a method of a referenced assembly: not when it keeps to the project,
		/// nor when it roots the method in a namespace or type that no referenced assembly declares.
		/// </summary>
		bool MayHoldOfReferencedMethod(AnchorTerm term)
		{
			return !term.IsProjectOnly && (term.Root is not {} root || ReferencedAssembliesContainRoot(root));
		}

		/// <summary>
		/// AG0232: an aspect attribute written on a method that its filters do not select. A method whose selection depends on
		/// the call is not reported, nor an aspect a filter error has turned off.
		/// </summary>
		void ReportNotApplied(IMethodSymbol method, MethodSelection selection)
		{
			foreach (var choice in selection.Choices)
			{
				if (choice.Applied is not null || choice.DependsOnCall || choice.Aspect.IsOff || choice.Scope.Library is not null)
					continue;

				foreach (var block in choice.Scope.MethodBlocks)
				{
					if (block.Block.Source is not FilterBlockSource { Syntax: {} syntax })
						continue;

					_diagnostics.Report(
						AspectDiagnosticID.AspectNotAppliedToMethod,
						AspectDiagnostics.GetAspectNotAppliedToMethodMessage(choice.Aspect.Name, method.ToDisplayString()),
						syntax.GetLocation(),
						DiagnosticSeverity.Warning);
				}
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
				if (!AspectSymbols.IsGeneratedInterceptorsTree(tree))
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

			var attributes = GetSelectedAttributes(method, invocation);

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

			if (!isApplied)
			{
				lock (_cacheGate)
				{
					_filterSelectedSkips.TryGetValue(reason, out var count);
					_filterSelectedSkips[reason] = count + 1;
				}
			}

			_diagnostics.Report(
				isApplied ? AspectDiagnosticID.CannotIntercept : AspectDiagnosticID.FilterSelectedCallNotIntercepted,
				$"Call to '{method.ToDisplayString()}' is not intercepted: {reason}.",
				invocation.GetLocation(),
				isApplied ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info);
		}

		/// <summary>
		/// The number of calls selected only by a target filter that were left as they are, per reason, in ordinal order of
		/// the reason.
		/// </summary>
		public List<(string Reason, int Count)> GetFilterSelectedSkips()
		{
			lock (_cacheGate)
				return _filterSelectedSkips
					.OrderBy(static s => s.Key, StringComparer.Ordinal)
					.Select(static s => (s.Key, s.Value))
					.ToList();
		}

		/// <summary>
		/// The aspects a call gets: those its method's definition decides for every call, and those decided for this call
		/// from the type arguments it binds.
		/// </summary>
		/// <param name="method">The method the call binds to, constructed and, for an extension method in its instance form, reduced.</param>
		List<AttributeInfo> GetSelectedAttributes(IMethodSymbol method, InvocationExpressionSyntax invocation)
		{
			lock (_cacheGate)
			{
				var definition = (method.ReducedFrom ?? method).OriginalDefinition;
				var selection  = GetSelection(definition);

				if (!selection.DependsOnCall)
					return selection.Applied;

				var attributes = new List<AttributeInfo>(selection.Choices.Length);

				foreach (var choice in selection.Choices)
				{
					if (choice.Aspect.IsOff)
						continue;

					if (!choice.DependsOnCall)
					{
						if (choice.Applied is not null && !attributes.Contains(choice.Applied))
							attributes.Add(choice.Applied);

						continue;
					}

					var consumer = new SymbolMethodFacts(GetDefinitionFacts(definition).Record.ForCall(method, invocation), _facts, _shapes);
					var library  = default(SymbolMethodFacts);

					if (choice.Scope.Library is not null)
					{
						var facts = GetLibrary(definition.ContainingAssembly);

						library = new SymbolMethodFacts(facts.Definition(definition, _shapes).Record.ForCall(method, invocation), facts.Context, _shapes);
					}

					if (Decide(choice.Scope, consumer, library, MatchPhase.Call, method) is {} applied && !attributes.Contains(applied))
						attributes.Add(applied);
				}

				return attributes;
			}
		}

		/// <summary>The selection of a method by every aspect that can speak about it, decided on its definition once.</summary>
		MethodSelection GetSelection(IMethodSymbol definition)
		{
			if (_selections.TryGetValue(definition, out var cached))
				return cached;

			var choices = ImmutableArray.CreateBuilder<AspectChoice>();
			var applied = new List<AttributeInfo>();

			foreach (var scope in GetScopes(definition))
			{
				if (scope.Aspect.IsOff)
					continue;

				var consumer = GetDefinitionFacts(definition);
				var library  = scope.Library is null ? null : GetLibrary(definition.ContainingAssembly).Definition(definition, _shapes);

				var decision = DecideOn(scope, consumer, library, MatchPhase.Definition, out var dependsOnCall);
				var choice   = new AspectChoice(scope, dependsOnCall);

				if (!dependsOnCall && decision.Selected)
				{
					choice.Applied = Apply(scope, decision, consumer, library, MatchPhase.Definition, definition);

					// Stacked applications of a library method that its library does not select are decided by this
					// compilation's assembly filters alike, once.
					if (!applied.Contains(choice.Applied))
						applied.Add(choice.Applied);
				}

				choices.Add(choice);
			}

			var selection = new MethodSelection(choices.ToImmutable(), applied);

			_selections.Add(definition, selection);

			return selection;
		}

		/// <summary>
		/// What decides a method for each aspect that can speak about it, in the order the aspects are applied: the aspects
		/// written on the method, then the aspects of the assembly filters, then the aspects on the method's type.
		/// <list type="bullet">
		/// <item>A method declared here is decided by the blocks of this compilation: the aspect's default, the assembly blocks,
		/// the blocks on its type and the blocks on it.</item>
		/// <item>A method of another assembly is decided by the assembly blocks of this compilation, unless the library placed
		/// the aspect on the method itself: then the library's own blocks decide first (the library family).</item>
		/// </list>
		/// </summary>
		List<AspectScope> GetScopes(IMethodSymbol definition)
		{
			var scopes   = new List<AspectScope>();
			var isSource = SymbolEqualityComparer.Default.Equals(definition.ContainingAssembly, _compilation.Assembly);

			var applied = new Dictionary<AspectCascade,(ImmutableArray<FilterBlock> Blocks, Func<ImmutableArray<FilterBlock>,AspectScope> Scope)>();

			foreach (var aspect in GetAppliedAspects(definition))
			{
				if (isSource)
				{
					var typeScope = GetTypeScope(aspect, definition.ContainingType);
					var blocks    = GetMethodBlocks(aspect, definition);

					applied.Add(aspect, (blocks, b => new AspectScope(aspect, typeScope, b, null)));
				}
				else
				{
					var library = GetLibraryScope(aspect, definition);

					applied.Add(aspect, (library.MethodBlocks, b => new AspectScope(aspect, null, [], new LibraryScope(library.Context, b))));
				}
			}

			// The aspects written on the method, in the order of their attributes: stacked applications each on its own.
			foreach (var attribute in applied.Count == 0 ? [] : definition.GetAttributes())
			{
				if (attribute.AttributeClass is not {} attributeClass || GetAspect(attributeClass) is not {} aspect || !applied.TryGetValue(aspect, out var method))
					continue;

				if (MethodApplicationsStack && method.Blocks.Length > 1)
				{
					foreach (var block in method.Blocks)
						if (block.Block.Source is FilterBlockSource { Attribute: {} written } && ReferenceEquals(written, attribute))
							scopes.Add(method.Scope([block]));
				}
				else if (!method.Blocks.IsEmpty && !scopes.Any(s => s.Aspect == aspect))
				{
					scopes.Add(method.Scope(method.Blocks));
				}
			}

			foreach (var aspect in _assemblyAspects)
				if (!scopes.Any(s => s.Aspect == aspect))
					scopes.Add(new AspectScope(aspect, isSource ? GetTypeScope(aspect, definition.ContainingType) : null, isSource ? GetMethodBlocks(aspect, definition) : [], null));

			if (isSource && definition.ContainingType is {} type)
				foreach (var aspect in GetAppliedAspects(type))
					if (!scopes.Any(s => s.Aspect == aspect))
						scopes.Add(new AspectScope(aspect, GetTypeScope(aspect, type), GetMethodBlocks(aspect, definition), null));

			return scopes;
		}

		/// <summary>
		/// Decides one aspect for one target, on the definition or at one call.
		/// </summary>
		/// <param name="consumer">The facts of the target as this compilation sees it.</param>
		/// <param name="library">For a method the library placed the aspect on, the facts as the library's own build sees them.</param>
		/// <param name="dependsOnCall">On the definition: a condition could not answer for every call, so each call decides.</param>
		CascadeDecision DecideOn(AspectScope scope, IMethodFacts consumer, IMethodFacts? library, MatchPhase phase, out bool dependsOnCall)
		{
			var evaluator = scope.Aspect.Evaluator;

			if (scope.Library is {} libraryScope)
			{
				var libraryTarget  = Target(library!, phase);
				var consumerTarget = Target(consumer, phase);
				var decision       = evaluator.DecideLibraryMethod(libraryTarget, libraryScope.Context, libraryScope.MethodCascadeBlocks, consumerTarget);

				dependsOnCall = libraryTarget.DependsOnCall || consumerTarget.DependsOnCall;

				return decision;
			}

			var context = scope.Context;
			var blocks  = scope.MethodCascadeBlocks;

			if (phase == MatchPhase.Call)
			{
				dependsOnCall = false;

				return FilterTarget.DecideCall(consumer, t => evaluator.Decide(t, context, blocks));
			}

			dependsOnCall = !FilterTarget.TryDecideDefinition(consumer, t => evaluator.Decide(t, context, blocks), out var onDefinition);

			return onDefinition;
		}

		/// <summary>Decides one aspect at one call, and the aspect as the call gets it when it is selected.</summary>
		AttributeInfo? Decide(AspectScope scope, IMethodFacts consumer, IMethodFacts? library, MatchPhase phase, IMethodSymbol method)
		{
			var decision = DecideOn(scope, consumer, library, phase, out _);

			return decision.Selected ? Apply(scope, decision, consumer, library, phase, method) : null;
		}

		static FilterTarget Target(IMethodFacts facts, MatchPhase phase)
		{
			return phase == MatchPhase.Definition ? FilterTarget.Definition(facts) : FilterTarget.Call(facts);
		}

		/// <summary>
		/// The aspect as a selected method gets it: its settings from the decision, the attribute that stands for it the most
		/// specific one that selects the method. A settings conflict is reported (AG0235); the method keeps the aspect, and the
		/// conflicting setting is left to the aspect's default.
		/// </summary>
		AttributeInfo Apply(AspectScope scope, CascadeDecision decision, IMethodFacts consumer, IMethodFacts? library, MatchPhase phase, IMethodSymbol method)
		{
			var aspect      = scope.Aspect;
			var reportAt    = FindReportAt(scope, decision, consumer, library, phase);
			var application = FilterBlockSources.CreateApplication(decision.Settings, reportAt);

			foreach (var conflict in decision.Conflicts)
			{
				ReportConflict(aspect, method, conflict);

				if (ConflictingConstructorUsesParameterlessConstructor &&
					conflict.Name == FilterBlockSources.ConstructorSetting &&
					aspect.AspectClass.InstanceConstructors.FirstOrDefault(static c => c.Parameters.IsEmpty) is {} parameterless)
				{
					application = new EffectiveApplication(reportAt, parameterless, ImmutableArray<TypedConstant>.Empty, reportAt, application.NamedArguments);
				}
			}

			_trace?.IncrementCounter(reportAt.ApplicationSyntaxReference is not null && IsOnMethod(scope, reportAt) ? "Selected aspect applications" : "Applied target filter matches");

			// An attribute of a type written in the source stands for the aspect as its syntax, as the aspects a type filter
			// selected always have: the emission finds its data on the type again, and reports at the call.
			if (scope.Type is {} type &&
				type.Blocks.Any(b => b.Block.Source is FilterBlockSource { Attribute: {} written } && ReferenceEquals(written, reportAt)) &&
				reportAt.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax syntax)
			{
				return aspect.Definition with
				{
					AppliedAttributeSyntax = syntax,
					AppliedSemanticModel   = GetSemanticModel(syntax.SyntaxTree),
					EffectiveApplication   = application
				};
			}

			return aspect.Definition with
			{
				AppliedAttributeData = reportAt,
				EffectiveApplication = application
			};
		}

		static bool IsOnMethod(AspectScope scope, AttributeData attribute)
		{
			foreach (var block in scope.MethodBlocks)
				if (block.Block.Source is FilterBlockSource { Attribute: {} written } && ReferenceEquals(written, attribute))
					return true;

			return false;
		}

		/// <summary>
		/// The most specific attribute that selects the method. Every selecting combination holds one block of each level that
		/// has blocks for the method, so it is one of the blocks of the most specific such level; where that level has several,
		/// the first that selects the method on its own with the levels above it.
		/// </summary>
		AttributeData FindReportAt(AspectScope scope, CascadeDecision decision, IMethodFacts consumer, IMethodFacts? library, MatchPhase phase)
		{
			var aspect    = scope.Aspect;
			var evaluator = aspect.Evaluator;

			if (decision.Family == CascadeFamily.Library && scope.Library is { MethodBlocks.IsEmpty: false } libraryScope)
			{
				return First(libraryScope.MethodBlocks, block =>
				{
					var selected = evaluator.DecideLibraryMethod(Target(library!, phase), libraryScope.Context, [block.Block], Target(consumer, phase));

					return selected is { Selected: true, Family: CascadeFamily.Library };
				});
			}

			if (!scope.MethodBlocks.IsEmpty)
				return First(scope.MethodBlocks, block => evaluator.Decide(Target(consumer, phase), scope.Context, [block.Block]).Selected);

			if (scope.Type is { Blocks.IsEmpty: false } type)
			{
				var index = 0;

				return First(type.Blocks, block => evaluator.Decide(Target(consumer, phase), type.Single(evaluator, index++), []).Selected);
			}

			// A selecting combination holds a block below the aspect's default: here an assembly block of this compilation.
			var assembly = 0;

			return First(aspect.Assembly, block =>
			{
				var (single, context) = aspect.SingleAssembly(assembly++);

				return single.Decide(Target(consumer, phase), context, []).Selected;
			});

			static AttributeData First(ImmutableArray<FilterBlock> blocks, Func<FilterBlock,bool> selects)
			{
				if (blocks.Length > 1)
					foreach (var block in blocks)
						if (selects(block))
							return ((FilterBlockSource)block.Block.Source!).Attribute!;

				return ((FilterBlockSource)blocks[0].Block.Source!).Attribute!;
			}
		}

		void ReportConflict(AspectCascade aspect, IMethodSymbol method, CascadeSettingConflict conflict)
		{
			var setting  = conflict.Name == FilterBlockSources.ConstructorSetting ? "the constructor arguments" : $"'{conflict.Name}'";
			var values   = new List<string>();
			var location = default(Location);
			var level    = CascadeLevel.Aspect;

			foreach (var (value, blocks) in conflict.Values)
			{
				var labels = new List<string>();

				foreach (var block in blocks)
				{
					if (block is not CascadeBlock<FilterTarget> { Source: FilterBlockSource source } cascadeBlock)
						continue;

					labels.Add(cascadeBlock.Label);

					// The most specific attribute written in the source; one read from a library has no location.
					if (source.Syntax is {} syntax && (location is null || source.Level > level))
					{
						location = syntax.GetLocation();
						level    = source.Level;
					}
				}

				values.Add($"{value} from {string.Join(" and ", labels)}");
			}

			_diagnostics.Report(
				AspectDiagnosticID.AspectSettingsConflict,
				AspectDiagnostics.GetAspectSettingsConflictMessage(aspect.Name, method.OriginalDefinition.ToDisplayString(), setting, values),
				location);
		}

		// ---- blocks ------------------------------------------------------------------------------------------------------

		/// <summary>The cascade of an aspect class, made on first use; <see langword="null"/> for a class that is no aspect.</summary>
		AspectCascade? GetAspect(INamedTypeSymbol aspectClass)
		{
			if (_aspects.TryGetValue(aspectClass, out var aspect))
				return aspect;

			if (!_registry.TryGetDefinition(aspectClass, out var definition))
			{
				_aspects.Add(aspectClass, null);
				return null;
			}

			var aspectDefault = FilterBlockSources.AspectDefault(definition);
			var assembly      = FilterBlockSources.Assembly(_compilation.Assembly, aspectClass, _compilation);

			aspect = new AspectCascade(definition, aspectDefault, assembly);

			_aspects.Add(aspectClass, aspect);

			Read(aspect, aspectDefault);

			foreach (var block in assembly)
				Read(aspect, block);

			return aspect;
		}

		/// <summary>The aspects applied on a symbol (a type or a method), in the order of their first attribute.</summary>
		ImmutableArray<AspectCascade> GetAppliedAspects(ISymbol? symbol)
		{
			if (symbol is null)
				return [];

			if (_appliedAspects.TryGetValue(symbol, out var cached))
				return cached;

			var aspects = ImmutableArray.CreateBuilder<AspectCascade>();

			foreach (var attribute in symbol.GetAttributes())
				if (attribute.AttributeClass is {} attributeClass && GetAspect(attributeClass) is {} aspect && !aspects.Contains(aspect))
					aspects.Add(aspect);

			var result = aspects.ToImmutable();

			_appliedAspects.Add(symbol, result);

			return result;
		}

		/// <summary>The blocks of an aspect on a type declared here and the plan of its methods; none for a type of another assembly.</summary>
		TypeScope? GetTypeScope(AspectCascade aspect, INamedTypeSymbol? type)
		{
			if (type is null)
				return null;

			type = type.OriginalDefinition;

			if (aspect.Types.TryGetValue(type, out var scope))
				return scope;

			var blocks = SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, _compilation.Assembly)
				? FilterBlockSources.Type(type, aspect.AspectClass, _compilation)
				: [];

			foreach (var block in blocks)
				Read(aspect, block);

			scope = new TypeScope(blocks, aspect.Evaluator.Prepare([..blocks.Select(static b => b.Block)]));

			aspect.Types.Add(type, scope);

			return scope;
		}

		/// <summary>The blocks of an aspect on a method declared here (or on a local function).</summary>
		ImmutableArray<FilterBlock> GetMethodBlocks(AspectCascade aspect, IMethodSymbol method)
		{
			method = method.OriginalDefinition;

			if (aspect.Methods.TryGetValue(method, out var blocks))
				return blocks;

			blocks = FilterBlockSources.Method(method, aspect.AspectClass, _compilation);

			foreach (var block in blocks)
				Read(aspect, block);

			aspect.Methods.Add(method, blocks);

			return blocks;
		}

		/// <summary>
		/// The library's own blocks for a method of a referenced library that carries the aspect: the library's assembly and
		/// type attributes of the aspect and the method's own, read from the library's metadata.
		/// </summary>
		LibraryScope GetLibraryScope(AspectCascade aspect, IMethodSymbol method)
		{
			var library = GetLibrary(method.ContainingAssembly);
			var type    = method.ContainingType.OriginalDefinition;

			if (!library.Assembly.TryGetValue(aspect, out var assemblyBlocks))
			{
				assemblyBlocks = FilterBlockSources.Assembly(method.ContainingAssembly, aspect.AspectClass, _compilation);

				foreach (var block in assemblyBlocks)
					Read(aspect, block);

				library.Assembly.Add(aspect, assemblyBlocks);
			}

			if (!library.Types.TryGetValue((aspect, type), out var context))
			{
				var typeBlocks = FilterBlockSources.Type(type, aspect.AspectClass, _compilation);

				foreach (var block in typeBlocks)
					Read(aspect, block);

				context = aspect.Evaluator.PrepareLibrary([..assemblyBlocks.Select(static b => b.Block)], [..typeBlocks.Select(static b => b.Block)]);

				library.Types.Add((aspect, type), context);
			}

			var methodBlocks = FilterBlockSources.Method(method, aspect.AspectClass, _compilation);

			foreach (var block in methodBlocks)
				Read(aspect, block);

			return new LibraryScope(context, methodBlocks);
		}

		LibraryFacts GetLibrary(IAssemblySymbol assembly)
		{
			if (!_libraries.TryGetValue(assembly, out var library))
			{
				// In the library's own filters `project` means the library's assembly, as in the library's own build.
				library = new LibraryFacts(new FactContext(_compilation, assembly));
				_libraries.Add(assembly, library);
			}

			return library;
		}

		SymbolMethodFacts GetDefinitionFacts(IMethodSymbol definition)
		{
			if (!_definitionFacts.TryGetValue(definition, out var facts))
			{
				facts = new SymbolMethodFacts(MethodFacts.CreateDefinition(definition, FactPlan.All, _facts), _facts, _shapes);
				_definitionFacts.Add(definition, facts);
			}

			return facts;
		}

		/// <summary>
		/// Takes in a block just read: its errors are reported, and turn its aspect off. A filter written in the source reports
		/// each error at its characters; one read from a referenced assembly is reported once, as AG0236 naming the assembly.
		/// </summary>
		void Read(AspectCascade aspect, FilterBlock block)
		{
			if (!block.Block.Declarations.IsEmpty && block.Block.Source is FilterBlockSource { Texts.IsEmpty: false })
				_traceFilters++;

			if (!block.HasErrors || !_reportedBlocks.Add(block))
				return;

			var source = (FilterBlockSource)block.Block.Source!;

			if (!source.IsFromMetadata)
			{
				aspect.IsOff = true;

				foreach (var diagnostic in block.Diagnostics)
					_diagnostics.Report(diagnostic.Id, diagnostic.Message, Locate(source, diagnostic), DiagnosticSeverity.Error);

				return;
			}

			// A library's own block may be left out instead of turning the aspect off; a referenced aspect's default filter is
			// the aspect's own and always does.
			var ignored = source.Level != CascadeLevel.Aspect && CascadeDefaults.LibraryFilterWithError == UnreadableLibraryFilter.Ignored;

			if (!ignored)
				aspect.IsOff = true;

			var first = block.Diagnostics[0];

			_diagnostics.Report(
				AspectDiagnosticID.UnreadableReferencedFilter,
				AspectDiagnostics.GetUnreadableReferencedFilterMessage(block.Block.Label, source.Assembly.Identity.Name, aspect.Name, $"{first.Id}: {first.Message}"),
				null,
				ignored ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error);
		}

		/// <summary>
		/// Where a diagnostic of a filter written in the source stands: the characters of its item inside the argument that
		/// writes the filter, where they can be traced, otherwise the attribute (for the aspect's default, its <c>[Aspect]</c>
		/// attribute).
		/// </summary>
		Location? Locate(FilterBlockSource source, FilterBlockDiagnostic diagnostic)
		{
			var name     = source.Level == CascadeLevel.Aspect ? FilterBlockSources.DefaultTargetFilterArgument : FilterBlockSources.TargetFilterArgument;
			var argument = default(ExpressionSyntax);

			foreach (var written in source.Syntax?.ArgumentList?.Arguments ?? default)
				if (written.NameEquals?.Name.Identifier.ValueText == name)
					argument = written.Expression;

			return FilterDiagnosticLocator.Locate(argument, _compilation, diagnostic, source.Syntax?.GetLocation());
		}

		/// <summary>The trace of a filter: its block and what limits the calls bound for it.</summary>
		void TraceFilter(AspectCascade aspect, FilterBlock block)
		{
			if (_trace is not { IsEnabled: true })
				return;

			var binding = aspect.IsOff
				? "no call: the aspect has a filter error"
				: DescribeBinding(block.AnchorsUnder(aspect.Default));

			_trace.AddTargetFilter(block.Block.Label, binding);
		}

		string DescribeBinding(BindingAnchors anchors)
		{
			if (anchors.SelectsNothing)
				return "no call";

			var terms = new List<string>();

			foreach (var term in anchors.Terms)
			{
				if (term.IsUnbounded)
					return "every call";

				var parts = term.Names.Select(static n => $"name {n}").ToList();

				if (term.Root is {} root)
					parts.Add(ReferencedAssembliesContainRoot(root) ? $"root {root}" : $"root {root}, declared here only");

				if (term.IsProjectOnly)
					parts.Add("declared here");

				terms.Add(string.Join(", ", parts));
			}

			return string.Join(" | ", terms);
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
		/// Rejects a call site before symbol binding when the plan proves it cannot bind to a selected method: a method declared
		/// here that an aspect selects, or a method of another assembly whose name an assembly filter accepts.
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

			// An assembly filter may select a method of a referenced assembly under this name; only binding can tell.
			if (plan.MayTargetExternalName(name))
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

		// ---- state -------------------------------------------------------------------------------------------------------

		/// <summary>One aspect class: its default filter, the assembly blocks of this compilation and what was read for it.</summary>
		sealed class AspectCascade
		{
			CascadeEvaluator<FilterTarget>.CascadeContext?[]? _single;
			CascadeEvaluator<FilterTarget>?[]?                _singleEvaluators;
			CascadeEvaluator<FilterTarget>.CascadeContext?    _referenced;

			public AspectCascade(AttributeInfo definition, FilterBlock aspectDefault, ImmutableArray<FilterBlock> assembly)
			{
				Definition = definition;
				Default    = aspectDefault;
				Assembly   = assembly;
				Evaluator  = new CascadeEvaluator<FilterTarget>(aspectDefault.Block, assembly.Select(static b => b.Block), [], IsProject);

				var name = definition.AttributeClass.Name;

				Name = name.Length > "Attribute".Length && name.EndsWith("Attribute", StringComparison.Ordinal)
					? name.Substring(0, name.Length - "Attribute".Length)
					: name;
			}

			public AttributeInfo                  Definition  { get; }
			public INamedTypeSymbol               AspectClass => Definition.AttributeClass;
			public string                         Name        { get; }
			public FilterBlock                    Default     { get; }
			public ImmutableArray<FilterBlock>    Assembly    { get; }
			public CascadeEvaluator<FilterTarget> Evaluator   { get; }

			/// <summary>The plan of the methods of other assemblies: the assembly blocks of this compilation only.</summary>
			public CascadeEvaluator<FilterTarget>.CascadeContext Referenced => _referenced ??= Evaluator.Prepare([]);

			/// <summary>A filter of the aspect has an error: the aspect selects nothing.</summary>
			public bool IsOff { get; set; }

			public Dictionary<INamedTypeSymbol,TypeScope>           Types   { get; } = new(SymbolEqualityComparer.Default);
			public Dictionary<IMethodSymbol,ImmutableArray<FilterBlock>> Methods { get; } = new(SymbolEqualityComparer.Default);

			/// <summary>The cascade with the aspect's default and one assembly block only, to tell which assembly block selects.</summary>
			public (CascadeEvaluator<FilterTarget> Evaluator, CascadeEvaluator<FilterTarget>.CascadeContext Context) SingleAssembly(int index)
			{
				_singleEvaluators ??= new CascadeEvaluator<FilterTarget>?[Assembly.Length];
				_single           ??= new CascadeEvaluator<FilterTarget>.CascadeContext?[Assembly.Length];

				var evaluator = _singleEvaluators[index] ??= new CascadeEvaluator<FilterTarget>(Default.Block, [Assembly[index].Block], [], IsProject);

				return (evaluator, _single[index] ??= evaluator.Prepare([]));
			}

			static bool IsProject(FilterTarget target)
			{
				return target.Facts.IsProject;
			}
		}

		/// <summary>The blocks of one aspect on one type declared here, and the plan of the type's methods.</summary>
		sealed class TypeScope
		{
			CascadeEvaluator<FilterTarget>.CascadeContext?[]? _single;

			public TypeScope(ImmutableArray<FilterBlock> blocks, CascadeEvaluator<FilterTarget>.CascadeContext context)
			{
				Blocks  = blocks;
				Context = context;
			}

			public ImmutableArray<FilterBlock>                   Blocks  { get; }
			public CascadeEvaluator<FilterTarget>.CascadeContext Context { get; }

			/// <summary>The plan with one of the type's blocks only, to tell which of them selects.</summary>
			public CascadeEvaluator<FilterTarget>.CascadeContext Single(CascadeEvaluator<FilterTarget> evaluator, int index)
			{
				_single ??= new CascadeEvaluator<FilterTarget>.CascadeContext?[Blocks.Length];

				return _single[index] ??= evaluator.Prepare([Blocks[index].Block]);
			}
		}

		/// <summary>The library's own blocks for a method a referenced library placed the aspect on.</summary>
		sealed class LibraryScope
		{
			public LibraryScope(CascadeEvaluator<FilterTarget>.CascadeContext context, ImmutableArray<FilterBlock> methodBlocks)
			{
				Context             = context;
				MethodBlocks        = methodBlocks;
				MethodCascadeBlocks = [..methodBlocks.Select(static b => b.Block)];
			}

			public CascadeEvaluator<FilterTarget>.CascadeContext Context             { get; }
			public ImmutableArray<FilterBlock>                   MethodBlocks        { get; }
			public ImmutableArray<CascadeBlock<FilterTarget>>    MethodCascadeBlocks { get; }
		}

		/// <summary>What one aspect decides one method definition with.</summary>
		sealed class AspectScope
		{
			public AspectScope(AspectCascade aspect, TypeScope? type, ImmutableArray<FilterBlock> methodBlocks, LibraryScope? library)
			{
				Aspect              = aspect;
				Type                = type;
				MethodBlocks        = methodBlocks;
				MethodCascadeBlocks = [..methodBlocks.Select(static b => b.Block)];
				Library             = library;
				Context             = type?.Context ?? aspect.Referenced;
			}

			public AspectCascade                                 Aspect              { get; }
			public TypeScope?                                    Type                { get; }
			public ImmutableArray<FilterBlock>                   MethodBlocks        { get; }
			public ImmutableArray<CascadeBlock<FilterTarget>>    MethodCascadeBlocks { get; }
			public LibraryScope?                                 Library             { get; }

			/// <summary>The plan of the method's type in this compilation; for a method of another assembly, the assembly blocks only.</summary>
			public CascadeEvaluator<FilterTarget>.CascadeContext Context             { get; }
		}

		/// <summary>One aspect's decision on a method's definition.</summary>
		sealed class AspectChoice
		{
			public AspectChoice(AspectScope scope, bool dependsOnCall)
			{
				Scope         = scope;
				DependsOnCall = dependsOnCall;
			}

			public AspectScope   Scope         { get; }
			public AspectCascade Aspect        => Scope.Aspect;

			/// <summary>The definition decides nothing: each call is decided with its own type arguments.</summary>
			public bool DependsOnCall { get; }

			/// <summary>The aspect as every call gets it, when the definition selects the method.</summary>
			public AttributeInfo? Applied { get; set; }
		}

		/// <summary>The decisions of every aspect on one method definition.</summary>
		sealed class MethodSelection
		{
			public MethodSelection(ImmutableArray<AspectChoice> choices, List<AttributeInfo> applied)
			{
				Choices       = choices;
				Applied       = applied;
				DependsOnCall = choices.Any(static c => c.DependsOnCall);
			}

			public ImmutableArray<AspectChoice> Choices { get; }

			/// <summary>The aspects the definition selects for every call, in the order they apply.</summary>
			public List<AttributeInfo> Applied { get; }

			public bool DependsOnCall { get; }

			public bool MaySelect => Applied.Count > 0 || DependsOnCall;
		}

		/// <summary>The facts of the methods of one referenced library, as the library's own build sees them.</summary>
		sealed class LibraryFacts
		{
			readonly Dictionary<IMethodSymbol,SymbolMethodFacts> _definitions = new(SymbolEqualityComparer.Default);

			public LibraryFacts(FactContext context)
			{
				Context = context;
			}

			public FactContext Context { get; }

			public Dictionary<AspectCascade,ImmutableArray<FilterBlock>>                                         Assembly { get; } = new();
			public Dictionary<(AspectCascade, INamedTypeSymbol),CascadeEvaluator<FilterTarget>.CascadeContext> Types    { get; } = new(new AspectTypeComparer());

			public SymbolMethodFacts Definition(IMethodSymbol definition, TypeShapes shapes)
			{
				if (!_definitions.TryGetValue(definition, out var facts))
				{
					facts = new SymbolMethodFacts(MethodFacts.CreateDefinition(definition, FactPlan.All, Context), Context, shapes);
					_definitions.Add(definition, facts);
				}

				return facts;
			}
		}

		sealed class AspectTypeComparer : IEqualityComparer<(AspectCascade, INamedTypeSymbol)>
		{
			public bool Equals((AspectCascade, INamedTypeSymbol) x, (AspectCascade, INamedTypeSymbol) y)
			{
				return ReferenceEquals(x.Item1, y.Item1) && SymbolEqualityComparer.Default.Equals(x.Item2, y.Item2);
			}

			public int GetHashCode((AspectCascade, INamedTypeSymbol) obj)
			{
				return unchecked(obj.Item1.GetHashCode() * 397 ^ SymbolEqualityComparer.Default.GetHashCode(obj.Item2));
			}
		}
	}
}
