using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using AspectGenerator.Selection;
using AspectGenerator.TargetFilterBinding;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspectGenerator
{
	sealed class Options
	{
		public bool?                     GenerateApi;
		public bool?                     DesignTimeBuild;
		public bool?                     BuildingProject;
		public bool?                     PublicApi;
		public bool?                     DebuggerStepThrough;
		public string?                   ReportFile;
		public string?                   ReportSizeLimit;
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
		public GeneratorHostKind         Host;
	}

	internal record GeneratorExecutionOptions(
		bool                     GenerateApi,
		bool                     EmitInterceptors,
		bool                     DesignTimeBuild,
		bool                     PublicApi,
		bool                     DebuggerStepThrough,
		string?                  ReportFile,
		long                     ReportSizeLimit,
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
		bool                     PropsImported,
		GeneratorHostKind        Host);

	internal record AttributeInfo(
		AttributeData?   AppliedAttributeData,
		AttributeSyntax? AppliedAttributeSyntax,
		SemanticModel?   AppliedSemanticModel,
		INamedTypeSymbol AttributeClass,
		AttributeData?   AspectDefinitionData,
		AttributeSyntax? AspectDefinitionSyntax,
		SemanticModel?   AspectDefinitionSemanticModel,
		string?          DefaultTargetFilter)
	{
		/// <summary>
		/// What the call gets the aspect with once the target filters have decided its settings; <see langword="null"/>
		/// while the aspect is applied as written (<see cref="AppliedAttributeData"/>, <see cref="AppliedAttributeSyntax"/>).
		/// </summary>
		public EffectiveApplication? EffectiveApplication { get; init; }
	}

	/// <summary>
	/// An aspect as a selected method gets it: its settings, each taken from the most specific attribute of the selection
	/// that writes it. All constructor arguments together are one setting; each named argument except <c>TargetFilter</c> is
	/// one of its own. The aspect instance is built from this: the constructor with its arguments, then the named arguments
	/// in order.
	/// <para>
	/// Two applications are equal when they build the same aspect from the same attributes: the same constructor, equal
	/// arguments (<see cref="TypedConstants"/>), the same attributes (by reference) and the same named arguments in the same
	/// order. Calls are grouped into one interceptor by this equality, so two applications built separately for two calls
	/// must compare equal whenever their settings are.
	/// </para>
	/// </summary>
	internal sealed record EffectiveApplication
	{
		public EffectiveApplication(
			AttributeData                          reportAt,
			IMethodSymbol?                         constructor,
			ImmutableArray<TypedConstant>          constructorArguments,
			AttributeData                          constructorSource,
			ImmutableArray<EffectiveNamedArgument> namedArguments)
		{
			ReportAt             = reportAt;
			Constructor          = constructor;
			ConstructorArguments = constructorArguments;
			ConstructorSource    = constructorSource;
			NamedArguments       = namedArguments;
		}

		/// <summary>The attribute diagnostics about the application stand on: the most specific one that selects the method.</summary>
		public AttributeData ReportAt { get; }

		/// <summary>
		/// The constructor, from the most specific attribute that writes constructor arguments. When none writes any, it is
		/// the one of <see cref="ReportAt"/>, with the arguments its defaults give. <see langword="null"/> only when the
		/// compiler could not bind the constructor of that attribute.
		/// </summary>
		public IMethodSymbol?                Constructor          { get; }
		public ImmutableArray<TypedConstant> ConstructorArguments { get; }

		/// <summary>The attribute <see cref="Constructor"/> and <see cref="ConstructorArguments"/> were taken from.</summary>
		public AttributeData ConstructorSource { get; }

		/// <summary>
		/// The named arguments, except <c>TargetFilter</c>: from the least specific level that the selection takes one from to
		/// the most specific, and on each as its attribute writes them.
		/// </summary>
		public ImmutableArray<EffectiveNamedArgument> NamedArguments { get; }

		public bool Equals(EffectiveApplication? other)
		{
			if (ReferenceEquals(this, other))
				return true;

			if (other is null ||
				!ReferenceEquals(ReportAt, other.ReportAt) ||
				!ReferenceEquals(ConstructorSource, other.ConstructorSource) ||
				!SymbolEqualityComparer.Default.Equals(Constructor, other.Constructor) ||
				!TypedConstants.SequenceEqual(ConstructorArguments, other.ConstructorArguments) ||
				NamedArguments.Length != other.NamedArguments.Length)
				return false;

			for (var i = 0; i < NamedArguments.Length; i++)
				if (!NamedArguments[i].Equals(other.NamedArguments[i]))
					return false;

			return true;
		}

		public override int GetHashCode()
		{
			var hash = unchecked(SymbolEqualityComparer.Default.GetHashCode(Constructor) * 31 + TypedConstants.GetHashCode(ConstructorArguments));

			foreach (var argument in NamedArguments)
				hash = unchecked(hash * 31 + argument.GetHashCode());

			return hash;
		}
	}

	/// <summary>A named argument of an <see cref="EffectiveApplication"/> and the attribute it was taken from.</summary>
	internal sealed record EffectiveNamedArgument
	{
		public EffectiveNamedArgument(string name, TypedConstant value, AttributeData source)
		{
			Name   = name;
			Value  = value;
			Source = source;
		}

		public string        Name   { get; }
		public TypedConstant Value  { get; }
		public AttributeData Source { get; }

		public bool Equals(EffectiveNamedArgument? other)
		{
			return
				other is not null &&
				string.Equals(Name, other.Name, StringComparison.Ordinal) &&
				ReferenceEquals(Source, other.Source) &&
				TypedConstants.Equals(Value, other.Value);
		}

		public override int GetHashCode()
		{
			return unchecked(StringComparer.Ordinal.GetHashCode(Name) * 31 + TypedConstants.GetHashCode(Value));
		}
	}

	sealed class AspectSelectionPlan
	{
		public AspectSelectionPlan(
			HashSet<IMethodSymbol>                selectedTargetMethods,
			Dictionary<string,TargetMethodBucket> targetMethodBuckets,
			bool                                  mayTargetExternalMethods,
			ImmutableArray<AnchorTerm>            externalTargetTerms)
		{
			SelectedTargetMethods    = selectedTargetMethods;
			TargetMethodBuckets      = targetMethodBuckets;
			MayTargetExternalMethods = mayTargetExternalMethods;
			ExternalTargetTerms      = externalTargetTerms;
		}

		/// <summary>The methods declared here that an aspect selects, or may select at some of their calls.</summary>
		public HashSet<IMethodSymbol>                SelectedTargetMethods { get; }
		public Dictionary<string,TargetMethodBucket> TargetMethodBuckets   { get; }

		/// <summary>
		/// Any call site may target a selected method that has no declaration in this compilation, so every call site is bound.
		/// </summary>
		public bool MayTargetExternalMethods { get; }

		/// <summary>
		/// What the assembly filters say of the names of the methods without a declaration in this compilation that they may
		/// still select: a call whose name meets every name condition of one term is bound and decided.
		/// </summary>
		public ImmutableArray<AnchorTerm> ExternalTargetTerms { get; }

		public bool MayTargetExternalName(string name)
		{
			foreach (var term in ExternalTargetTerms)
				if (term.MatchesName(name))
					return true;

			return false;
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

	/// <summary>
	/// Why one aspect does or does not apply to one method, for the build report.
	/// </summary>
	/// <param name="Method">The method as the report names it: its definition, or for a decision per call the constructed method.</param>
	/// <param name="NotApplied">The aspect is written on the method and its filters do not select it (AG0232).</param>
	/// <param name="PerCall">The decision depends on the call: it is the one of this constructed method.</param>
	/// <param name="Attributes">Where the attributes the aspect is written on the method are; empty when it is not written on the method.</param>
	internal sealed record ExplainedMethod(
		string                             Method,
		string                             Aspect,
		bool                               NotApplied,
		bool                               PerCall,
		ImmutableArray<Location>           Attributes,
		AspectGenerator.Cascade.CascadeExplanation Explanation);

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
