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
		public bool?                     PropsImported;
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
		string?                  InterceptorsNamespaces,
		bool                     PropsImported);

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
			bool                                mayTargetExternalMethods,
			HashSet<string>                     externalTargetMethodNames)
		{
			SelectedTargetMethods      = selectedTargetMethods;
			SelectedTargetAttributes   = selectedTargetAttributes;
			TargetMethodBuckets        = targetMethodBuckets;
			MayTargetExternalMethods   = mayTargetExternalMethods;
			ExternalTargetMethodNames  = externalTargetMethodNames;
		}

		public HashSet<IMethodSymbol>                       SelectedTargetMethods    { get; }
		public Dictionary<IMethodSymbol,List<AttributeInfo>> SelectedTargetAttributes { get; }
		public Dictionary<string,TargetMethodBucket>         TargetMethodBuckets      { get; }
		/// <summary>
		/// Any call site may target a selected method that has no declaration in this compilation, so every call site is bound.
		/// </summary>
		public bool                                          MayTargetExternalMethods { get; }

		/// <summary>
		/// Names of methods without a declaration in this compilation that target filters may still select.
		/// Call sites with these names are bound and evaluated against the filters.
		/// </summary>
		public HashSet<string>                               ExternalTargetMethodNames { get; }

		public bool MayTargetExternalMethod(IMethodSymbol method)
		{
			return MayTargetExternalMethods || ExternalTargetMethodNames.Contains(method.Name);
		}

		public bool TryGetSelectedAttributes(IMethodSymbol method, out List<AttributeInfo> attributes)
		{
			// An instance-form extension call binds to the reduced method; the plan holds the declared static method.
			return SelectedTargetAttributes.TryGetValue((method.ReducedFrom ?? method).OriginalDefinition, out attributes!);
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

		/// <summary>
		/// Simple names a type receiver of a call to one of <see cref="Methods"/> can have: the containing types,
		/// the source types derived from them, and every using alias of the compilation.
		/// Filled by the selection plan when <see cref="CanRejectByTypeReceiver"/> is true.
		/// </summary>
		public HashSet<string> ReceiverTypeNames     { get; } = new(StringComparer.Ordinal);

		/// <summary>
		/// Every method is an ordinary static method, so a call can only name a type (or an alias of one) as its receiver,
		/// never a value: an explicit receiver whose simple name is not in <see cref="ReceiverTypeNames"/> cannot bind to them.
		/// Instance, extension, extension-block and static abstract or virtual interface members keep every receiver.
		/// </summary>
		public bool CanRejectByTypeReceiver { get; private set; } = true;

		// C# 14 extension blocks have TypeKind.Extension, value 14, which Roslyn 4.14 added. The generator is built against
		// an older Roslyn so that it loads in older compilers, so the value is compared as a number; a compiler without it
		// cannot produce extension block types.
		static bool IsExtensionBlock(INamedTypeSymbol type)
		{
			return (int)type.TypeKind == 14;
		}

		public void Add(IMethodSymbol method)
		{
			Methods.Add(method);
			GenericArities.Add(method.TypeParameters.Length);

			if (!method.IsStatic || method.IsExtensionMethod || method.IsAbstract || method.IsVirtual ||
				method.ContainingType is null || IsExtensionBlock(method.ContainingType))
				CanRejectByTypeReceiver = false;

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

	/// <param name="GenericShape">The target in terms of its own type parameters when the interceptor has to be generic; otherwise <c>null</c>.</param>
	internal record AnalyzedInvocation(
		InvocationExpressionSyntax Inv,
		IMethodSymbol              Method,
		List<AttributeInfo>        Attributes,
		IMethodSymbol?             GenericShape = null);

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
