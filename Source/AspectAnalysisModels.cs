using System.Collections.Generic;
using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspectGenerator
{
	sealed class Options
	{
		public bool?   GenerateApi;
		public bool?   DesignTimeBuild;
		public bool?   PublicApi;
		public bool?   DebuggerStepThrough;
		public string? ReportFile;
		public AspectDiagnosticSeverity? AspectDiagnosticSeverity;
		public string? ProjectDirectory;
		public string? CompilerGeneratedFilesOutputPath;
		public string? InterceptorsNamespace;
		public string? InterceptorsNamespaces;
	}

	record GeneratorExecutionOptions(
		bool    GenerateApi,
		bool    EmitInterceptors,
		bool    DesignTimeBuild,
		bool    PublicApi,
		bool    DebuggerStepThrough,
		string? ReportFile,
		AspectDiagnosticSeverity AspectDiagnosticSeverity,
		string? ProjectDirectory,
		string? CompilerGeneratedFilesOutputPath,
		string? InterceptorsNamespace,
		string? InterceptorsNamespaces);

	record AttributeInfo(
		AttributeData?    AppliedAttributeData,
		AttributeSyntax?  AppliedAttributeSyntax,
		SemanticModel?    AppliedSemanticModel,
		INamedTypeSymbol  AttributeClass,
		AttributeData?    AspectDefinitionData,
		AttributeSyntax?  AspectDefinitionSyntax,
		SemanticModel?    AspectDefinitionSemanticModel);

	record AspectFilterSet(
		AttributeInfo                 Attribute,
		AspectFilters.TargetFilterSet Filters);

	record AnalyzedInvocation(
		InvocationExpressionSyntax Inv,
		IMethodSymbol              Method,
		List<AttributeInfo>        Attributes);

	record DiagnosticInfo(
		string    Id,
		string    Message,
		Location? Location,
		DiagnosticSeverity Severity = DiagnosticSeverity.Error);

	interface IAspectDiagnosticSink
	{
		void Report(string id, string message, Location? location, DiagnosticSeverity severity = DiagnosticSeverity.Error);
	}

	sealed class GeneratorDiagnosticSink : IAspectDiagnosticSink
	{
		readonly HashSet<string> _reportedDiagnostics = new();

		public List<DiagnosticInfo> Diagnostics { get; } = new();

		public void Report(string id, string message, Location? location, DiagnosticSeverity severity = DiagnosticSeverity.Error)
		{
			var key = $"{id}:{location?.SourceTree?.FilePath}:{location?.SourceSpan.Start}:{message}";

			if (!_reportedDiagnostics.Add(key))
				return;

			Diagnostics.Add(new DiagnosticInfo(id, message, location, severity));
		}
	}

	sealed class NullDiagnosticSink : IAspectDiagnosticSink
	{
		public static readonly NullDiagnosticSink Instance = new();

		NullDiagnosticSink()
		{
		}

		public void Report(string id, string message, Location? location, DiagnosticSeverity severity = DiagnosticSeverity.Error)
		{
		}
	}
}
