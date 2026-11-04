using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AspectGenerator
{
	[DiagnosticAnalyzer(LanguageNames.CSharp)]
	public sealed class AspectGeneratorMarkerAnalyzer : DiagnosticAnalyzer
	{
		public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
			[
				AspectDiagnostics.InterceptedCallMarkerHiddenDescriptor,
				AspectDiagnostics.InterceptedCallMarkerInfoDescriptor,
				AspectDiagnostics.InterceptedCallMarkerWarningDescriptor,
				AspectDiagnostics.InterceptedCallMarkerErrorDescriptor
			];

		public override void Initialize(AnalysisContext context)
		{
			context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
			context.EnableConcurrentExecution();

			context.RegisterCompilationStartAction(static startContext =>
			{
				var options = AspectOptionsResolver.Resolve(
					startContext.Compilation,
					startContext.Options.AnalyzerConfigOptionsProvider,
					NullDiagnosticSink.Instance);

				if (options.AspectDiagnosticSeverity == AspectDiagnosticSeverity.Off)
					return;

				var descriptor  = AspectDiagnostics.GetInterceptedCallMarkerDescriptor(options.AspectDiagnosticSeverity);
				var compilation = startContext.Compilation;

				// Markers must name exactly the calls the generator intercepts, so they use the generator's
				// declaration-first selection plan and call-site prefilter. The plan is built once, on first use.
				var selection = new SelectionCache(compilation, options);

				startContext.RegisterSyntaxNodeAction(
					context =>
					{
						if (!AspectSelectionService.IsCandidateInvocation(context.Node))
							return;

						if (selection.Get(context.CancellationToken) is not {} selected)
							return;

						var (service, plan) = selected;

						var invocation = (InvocationExpressionSyntax)context.Node;

						if (!service.MayMatchCallSite(invocation, plan))
							return;

						if (!service.TryAnalyzeInvocation(
							invocation,
							context.SemanticModel,
							plan,
							context.CancellationToken,
							out var analyzedInvocation))
							return;

						var aspects = string.Join(", ", analyzedInvocation.Attributes
							.Select(static attribute => attribute.AttributeClass.Name)
							.Distinct(System.StringComparer.Ordinal));
						var interceptorName = GetGeneratedInterceptorMethodName(analyzedInvocation.Method);
						var interceptor     = $"{GetGeneratedInterceptorType(options)}.{interceptorName}";

						context.ReportDiagnostic(
							Diagnostic.Create(
								descriptor,
								invocation.GetLocation(),
								aspects,
								interceptor));
					},
					SyntaxKind.InvocationExpression);
			});
		}

		/// <summary>
		/// Builds the selection plan once per compilation. A build that is cancelled is not kept: the plan builder stops
		/// early on cancellation, and a partial plan would hide markers for the rest of the compilation.
		/// </summary>
		sealed class SelectionCache
		{
			readonly Compilation               _compilation;
			readonly GeneratorExecutionOptions _options;
			readonly object                    _gate = new();

			(AspectSelectionService Service, AspectSelectionPlan Plan)? _selection;
			bool _built;

			public SelectionCache(Compilation compilation, GeneratorExecutionOptions options)
			{
				_compilation = compilation;
				_options     = options;
			}

			public (AspectSelectionService Service, AspectSelectionPlan Plan)? Get(CancellationToken cancellationToken)
			{
				// _selection is written before _built is published, so a reader that sees _built also sees the plan.
				if (Volatile.Read(ref _built))
					return _selection;

				lock (_gate)
				{
					if (_built)
						return _selection;

					var selection = CreateSelection(_compilation, _options, cancellationToken);

					cancellationToken.ThrowIfCancellationRequested();

					_selection = selection;
					Volatile.Write(ref _built, true);

					return selection;
				}
			}
		}

		static (AspectSelectionService Service, AspectSelectionPlan Plan)? CreateSelection(
			Compilation               compilation,
			GeneratorExecutionOptions options,
			CancellationToken         cancellationToken)
		{
			AspectDefinitionRegistry.FindDeclarations(compilation, cancellationToken, out var aspectDeclarations, out var methodDeclarations);

			var registry = AspectDefinitionRegistry.Create(
				compilation,
				aspectDeclarations,
				NullDiagnosticSink.Instance,
				cancellationToken);
			var externalAspectDefinitionsPossible = AspectDefinitionRegistry.HasExternalAspectDefinitions(compilation);

			if (registry.AspectAttributes.Count == 0 && !externalAspectDefinitionsPossible)
				return null;

			var service = new AspectSelectionService(
				compilation,
				options,
				registry,
				NullDiagnosticSink.Instance);

			return (service, service.BuildSelectionPlan(methodDeclarations, externalAspectDefinitionsPossible, cancellationToken));
		}

		static string GetGeneratedInterceptorType(GeneratorExecutionOptions options)
		{
			var interceptorsNamespace = string.IsNullOrWhiteSpace(options.InterceptorsNamespace)
				? "AspectGenerator"
				: options.InterceptorsNamespace!;

			return $"{interceptorsNamespace}.AspectGeneratorInterceptors";
		}

		static string GetGeneratedInterceptorMethodName(IMethodSymbol method)
		{
			return InterceptorNamingService.GetBaseName(method);
		}
	}
}
