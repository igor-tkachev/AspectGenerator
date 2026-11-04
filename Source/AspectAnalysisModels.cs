using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspectGenerator
{
	sealed class Options
	{
		public bool?                     GenerateApi;
		public bool?                     DesignTimeBuild;
		public bool?                     PublicApi;
		public bool?                     DebuggerStepThrough;
		public string?                   ReportFile;
		public AspectDiagnosticSeverity? AspectDiagnosticSeverity;
		public string?                   InvalidAspectDiagnosticSeverityValue;
		public bool?                     Trace;
		public string?                   TraceLevel;
		public string?                   TargetFramework;
		public string?                   Configuration;
		public string?                   ProjectDirectory;
		public string?                   CompilerGeneratedFilesOutputPath;
		public string?                   InterceptorsNamespace;
		public string?                   InterceptorsNamespaces;
	}

	internal record GeneratorExecutionOptions(
		bool                     GenerateApi,
		bool                     EmitInterceptors,
		bool                     DesignTimeBuild,
		bool                     PublicApi,
		bool                     DebuggerStepThrough,
		string?                  ReportFile,
		AspectDiagnosticSeverity AspectDiagnosticSeverity,
		bool                     Trace,
		AspectTraceLevel         TraceLevel,
		string?                  InvalidTraceLevelValue,
		string?                  TargetFramework,
		string?                  Configuration,
		string?                  ProjectDirectory,
		string?                  CompilerGeneratedFilesOutputPath,
		string?                  InterceptorsNamespace,
		string?                  InterceptorsNamespaces);

	internal record AttributeInfo(
		AttributeData?   AppliedAttributeData,
		AttributeSyntax? AppliedAttributeSyntax,
		SemanticModel?   AppliedSemanticModel,
		INamedTypeSymbol AttributeClass,
		AttributeData?   AspectDefinitionData,
		AttributeSyntax? AspectDefinitionSyntax,
		SemanticModel?   AspectDefinitionSemanticModel,
		string?          DefaultTargetFilter);

	record AspectFilterSet(
		AttributeInfo                 Attribute,
		AspectFilters.TargetFilterSet Filters);

	sealed class AspectSelectionPlan
	{
		public AspectSelectionPlan(
			HashSet<IMethodSymbol>              selectedTargetMethods,
			Dictionary<IMethodSymbol,List<AttributeInfo>> selectedTargetAttributes,
			Dictionary<string,TargetMethodBucket>          targetMethodBuckets,
			bool                                mayTargetExternalMethods)
		{
			SelectedTargetMethods      = selectedTargetMethods;
			SelectedTargetAttributes   = selectedTargetAttributes;
			TargetMethodBuckets        = targetMethodBuckets;
			MayTargetExternalMethods   = mayTargetExternalMethods;
		}

		public HashSet<IMethodSymbol>                       SelectedTargetMethods    { get; }
		public Dictionary<IMethodSymbol,List<AttributeInfo>> SelectedTargetAttributes { get; }
		public Dictionary<string,TargetMethodBucket>         TargetMethodBuckets      { get; }
		public bool                                          MayTargetExternalMethods { get; }

		public bool TryGetSelectedAttributes(IMethodSymbol method, out List<AttributeInfo> attributes)
		{
			return SelectedTargetAttributes.TryGetValue(method.OriginalDefinition, out attributes!);
		}
	}

	sealed class TargetMethodBucket
	{
		public TargetMethodBucket(string name)
		{
			Name = name;
		}

		public string          Name                  { get; }
		public HashSet<int>    GenericArities        { get; } = new();
		public List<ArgRange>  ArgumentRanges        { get; } = new();
		public HashSet<string> ContainingTypeNames   { get; } = new(StringComparer.Ordinal);
		public List<IMethodSymbol> Methods           { get; } = new();

		public void Add(IMethodSymbol method)
		{
			Methods.Add(method);
			GenericArities.Add(method.TypeParameters.Length);

			foreach (var containingName in GetContainingTypeNames(method.ContainingType))
				ContainingTypeNames.Add(containingName);

			var minimumArgumentCount = method.Parameters.Count(static p => !p.IsOptional && !p.IsParams);
			var maximumArgumentCount = method.Parameters.Any(static p => p.IsParams)
				? int.MaxValue
				: method.Parameters.Length;

			ArgumentRanges.Add(new ArgRange(minimumArgumentCount, maximumArgumentCount));

			if (method.IsExtensionMethod && method.Parameters.Length > 0)
				ArgumentRanges.Add(new ArgRange(Math.Max(0, minimumArgumentCount - 1), maximumArgumentCount == int.MaxValue ? int.MaxValue : maximumArgumentCount - 1));
		}

		public bool AllowsGenericArity(int genericArity)
		{
			return GenericArities.Contains(genericArity);
		}

		public bool AllowsArgumentCount(int argumentCount)
		{
			return ArgumentRanges.Any(range => range.Contains(argumentCount));
		}

		static IEnumerable<string> GetContainingTypeNames(INamedTypeSymbol? type)
		{
			for (var current = type; current is not null; current = current.ContainingType)
				yield return current.Name;
		}
	}

	readonly struct ArgRange
	{
		public ArgRange(int min, int max)
		{
			Min = min;
			Max = max;
		}

		public int Min { get; }
		public int Max { get; }

		public bool Contains(int value)
		{
			return value >= Min && value <= Max;
		}
	}

	internal record AnalyzedInvocation(
		InvocationExpressionSyntax Inv,
		IMethodSymbol              Method,
		List<AttributeInfo>        Attributes);

	internal record DiagnosticInfo(
		string             Id,
		string             Message,
		Location?          Location,
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
