using System.Collections.Immutable;
using System.Linq;

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
					startContext.Options.AnalyzerConfigOptionsProvider);

				if (options.AspectDiagnosticSeverity == AspectDiagnosticSeverity.Off)
					return;

				var descriptor = AspectDiagnostics.GetInterceptedCallMarkerDescriptor(options.AspectDiagnosticSeverity);
				var aspectDeclarations = AspectDefinitionRegistry.FindAspectDeclarations(
					startContext.Compilation,
					startContext.CancellationToken);
				var registry = AspectDefinitionRegistry.Create(
					startContext.Compilation,
					aspectDeclarations,
					NullDiagnosticSink.Instance,
					startContext.CancellationToken);
				var selection = new AspectSelectionService(
					startContext.Compilation,
					options,
					registry,
					NullDiagnosticSink.Instance);

				startContext.RegisterSyntaxNodeAction(
					context =>
					{
						var invocation = (InvocationExpressionSyntax)context.Node;

						if (!selection.TryAnalyzeInvocation(
							invocation,
							context.SemanticModel,
							context.CancellationToken,
							out var analyzedInvocation))
							return;

						var aspects = string.Join(", ", analyzedInvocation.Attributes
							.Select(static attribute => attribute.AttributeClass.Name)
							.Distinct(System.StringComparer.Ordinal));

						context.ReportDiagnostic(
							Diagnostic.Create(
								descriptor,
								invocation.GetLocation(),
								aspects));
					},
					SyntaxKind.InvocationExpression);
			});
		}
	}
}
