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
	public sealed class AspectGeneratorDiagnosticAnalyzer : DiagnosticAnalyzer
	{
		public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => AspectSourceGenerator.MarkerSupportedDiagnostics;

		public override void Initialize(AnalysisContext context)
		{
			context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
			context.EnableConcurrentExecution();

			context.RegisterCompilationStartAction(static startContext =>
			{
				var markerEnabled = new Lazy<bool>(
					() => AspectSourceGenerator.IsInterceptedCallMarkerEnabled(startContext.Compilation, startContext.Options.AnalyzerConfigOptionsProvider),
					LazyThreadSafetyMode.ExecutionAndPublication);
				var diagnostics = new Lazy<ImmutableArray<Diagnostic>>(
					() => GetInterceptedCallMarkerDiagnostics(startContext.Compilation, startContext.Options, startContext.CancellationToken),
					LazyThreadSafetyMode.ExecutionAndPublication);

				startContext.RegisterSyntaxNodeAction(
					context =>
					{
						if (!markerEnabled.Value)
							return;

						var invocation = (InvocationExpressionSyntax)context.Node;
						var location   = invocation.GetLocation();

						foreach (var diagnostic in diagnostics.Value)
						{
							if (diagnostic.Location.SourceTree == location.SourceTree &&
								diagnostic.Location.SourceSpan  == location.SourceSpan)
								context.ReportDiagnostic(diagnostic);
						}
					},
					SyntaxKind.InvocationExpression);
			});
		}

		static ImmutableArray<Diagnostic> GetInterceptedCallMarkerDiagnostics(
			Compilation       compilation,
			AnalyzerOptions   options,
			CancellationToken cancellationToken)
		{
			var aspectDeclarations = ImmutableArray.CreateBuilder<ClassDeclarationSyntax>();
			var invocations        = ImmutableArray.CreateBuilder<InvocationExpressionSyntax>();

			foreach (var tree in compilation.SyntaxTrees)
			{
				var root = tree.GetRoot(cancellationToken);

				foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
				{
					if (declaration.AttributeLists
						.SelectMany(static list => list.Attributes)
						.Any(static attribute => IsAspectAttributeName(attribute.Name.ToString())))
						aspectDeclarations.Add(declaration);
				}

				foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
				{
					if (invocation.Expression is MemberAccessExpressionSyntax or IdentifierNameSyntax)
						invocations.Add(invocation);
				}
			}

			return AspectSourceGenerator.GetInterceptedCallMarkerDiagnostics(
				compilation,
				options.AnalyzerConfigOptionsProvider,
				aspectDeclarations.ToImmutable(),
				invocations.ToImmutable(),
				cancellationToken);
		}

		static bool IsAspectAttributeName(string name) =>
			name is "Aspect" or "AspectAttribute" or "AspectGenerator.Aspect" or "AspectGenerator.AspectAttribute";
	}
}
