using System;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	[TestClass]
	public class CompilerTests
	{
		[TestMethod]
		public void HookContractDiagnosticsTest()
		{
			var result = RunGenerator(HookContractDiagnosticsSource);

			foreach (var expected in new Dictionary<string,string[]>
			{
				[AspectDiagnosticID.HookInvalidParameters]     = ["WrongParameter",        "invalid parameter list"],
				[AspectDiagnosticID.HookInvalidReturnType]     = ["WrongReturn",           "invalid return type"],
				[AspectDiagnosticID.OnCallHookMismatch]        = ["OnCall hook",           "must match target method"],
				[AspectDiagnosticID.HookRequiresInterceptData] = ["UseInterceptData=true", "ref InterceptData<T>"],
				[AspectDiagnosticID.AsyncHookRequiresTask]     = ["Async hook",            "ValueTask<T>"],
			})
			{
				var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Id == expected.Key);

				Assert.IsNotNull(diagnostic, $"Expected diagnostic {expected.Key}. Actual diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.Id))}");

				foreach (var fragment in expected.Value)
					StringAssert.Contains(diagnostic.GetMessage(), fragment);
			}

			Assert.IsTrue(result.Diagnostics.Where(d => d.Id.StartsWith("AG01", StringComparison.Ordinal)).All(d => d.Location.SourceTree is not null));
		}

		[TestMethod]
		public void InterceptorEmissionFollowsBuildModeTest()
		{
			var normalResult = RunGenerator(GenerationOptionsSource);
			var designTimeResult = RunGenerator(
				GenerationOptionsSource,
				new()
				{
					["build_property.DesignTimeBuild"] = "true",
				});

			AssertGenerated   (normalResult, "AspectAttribute.g.cs");
			AssertGenerated   (normalResult, "Interceptors.g.cs");
			AssertGenerated   (designTimeResult, "AspectAttribute.g.cs");
			AssertNotGenerated(designTimeResult, "Interceptors.g.cs");
		}

		[TestMethod]
		public void AssemblyOptionsSyntaxFallbackControlsGeneratedApiTest()
		{
			var result = RunGeneratorAndGetCompilation(AssemblyOptionsSyntaxFallbackSource, out var compilation);

			AssertGenerated(result, "AspectGeneratorOptionsAttribute.g.cs");
			AssertNotGenerated(result, "AspectAttribute.g.cs");
			AssertNoDiagnostic(result, AspectDiagnosticID.NamespaceNotAllowed);
			Assert.IsFalse(compilation.GetDiagnostics().Any(static d => d.Severity == DiagnosticSeverity.Error), string.Join(Environment.NewLine, compilation.GetDiagnostics()));

			var optionsSource = GetGeneratedSource(result, "AspectGeneratorOptionsAttribute.g.cs");

			StringAssert.Contains(optionsSource, "enum AspectDiagnosticSeverity");
			StringAssert.Contains(optionsSource, "sealed class AspectGeneratorOptionsAttribute");
			Assert.IsFalse(optionsSource.Contains("public enum AspectDiagnosticSeverity", StringComparison.Ordinal));
			Assert.IsFalse(optionsSource.Contains("public sealed class AspectGeneratorOptionsAttribute", StringComparison.Ordinal));
		}

		[TestMethod]
		public void RuntimeApiVisibilityFollowsPublicApiTest()
		{
			var internalResult = RunGenerator(GenerationOptionsSource);
			var publicResult   = RunGenerator(
				GenerationOptionsSource,
				new()
				{
					[$"build_property.AspectGenerator{AspectOptionName.PublicApi}"] = "true",
				});

			var internalApi = GetGeneratedSource(internalResult, "AspectAttribute.g.cs");
			var publicApi   = GetGeneratedSource(publicResult,   "AspectAttribute.g.cs");

			StringAssert.Contains(internalApi, "sealed class AspectAttribute");
			Assert.IsFalse(internalApi.Contains("public sealed class AspectAttribute", StringComparison.Ordinal));
			StringAssert.Contains(publicApi, "public sealed class AspectAttribute");

			var publicOptionsApi = GetGeneratedSource(publicResult, "AspectGeneratorOptionsAttribute.g.cs");

			Assert.IsFalse(publicOptionsApi.Contains("public sealed class AspectGeneratorOptionsAttribute", StringComparison.Ordinal));
		}

		[TestMethod]
		public void AspectGeneratorGenerateInterceptorsPropertyIsIgnoredTest()
		{
			var result = RunGenerator(
				GenerationOptionsSource,
				new()
				{
					["build_property.AspectGeneratorGenerateInterceptors"] = "false",
				});

			AssertGenerated(result, "Interceptors.g.cs");
		}

		[TestMethod]
		public void AspectGeneratorOptionsGenerateInterceptorsNamedArgumentDoesNotExistTest()
		{
			var result = RunGeneratorAndGetCompilation(GenerateInterceptorsAssemblyOptionSource, out var compilation);

			AssertGenerated(result, "AspectGeneratorOptionsAttribute.g.cs");
			Assert.IsTrue(compilation.GetDiagnostics().Any(static d =>
				d.Severity == DiagnosticSeverity.Error &&
				d.GetMessage().Contains("GenerateInterceptors", StringComparison.Ordinal)));
		}

		[TestMethod]
		public void HookContractDiagnosticsRunDuringDesignTimeBuildTest()
		{
			var result = RunGenerator(
				HookContractDiagnosticsSource,
				new()
				{
					["build_property.DesignTimeBuild"] = "true",
				});

			CollectionAssert.Contains(result.Diagnostics.Select(static d => d.Id).ToArray(), AspectDiagnosticID.HookInvalidParameters);
			CollectionAssert.Contains(result.Diagnostics.Select(static d => d.Id).ToArray(), AspectDiagnosticID.AsyncHookRequiresTask);

			AssertNotGenerated(result, "Interceptors.g.cs");
		}

		[TestMethod]
		public void ValueTaskTargetSupportsAsyncHooksTest()
		{
			var result = RunGenerator(ValueTaskGenerationSource);

			CollectionAssert.DoesNotContain(result.Diagnostics.Select(static d => d.Id).ToArray(), AspectDiagnosticID.AsyncHookRequiresTask);
			AssertGenerated(result, "Interceptors.g.cs");
		}

		[TestMethod]
		public void TypedAspectHookUsesLazyAspectAndMemberInfoTest()
		{
			var result = RunGenerator(TypedAspectGenerationSource);
			var source = GetGeneratedSource(result, "Interceptors.g.cs");

			AssertNoDiagnostic(result, AspectDiagnosticID.HookInvalidParameters);
			StringAssert.Contains(source, "private static class Target_Interceptor_State");
			StringAssert.Contains(source, "internal static readonly SR.MemberInfo TargetMethod");
			StringAssert.Contains(source, "internal static readonly global::AspectGenerator.Tests.GeneratorDriver.TypedAspectAttribute Aspect0");
			StringAssert.Contains(source, "static Target_Interceptor_State()");
			StringAssert.Contains(source, "var __targetMethod__ = Target_Interceptor_State.TargetMethod;");
			StringAssert.Contains(source, "var __aspect__0 = Target_Interceptor_State.Aspect0;");
			StringAssert.Contains(source, "Aspect          = __aspect__0,");
			Assert.IsFalse(source.Contains("Lazy<", StringComparison.Ordinal), "Generated interceptors should use per-interceptor state holders instead of Lazy<T>.");
			var removedApiName = "Aspect" + "Arguments";
			Assert.IsFalse(source.Contains(removedApiName, StringComparison.Ordinal), $"{removedApiName} should not be emitted.");
			StringAssert.Contains(source, "TypedAspectAttribute.After(__aspect__0, __info__0);");
		}

		[TestMethod]
		public void InstanceLifetimeUsesLocalAspectConstructionTest()
		{
			var result = RunGenerator(InstanceLifetimeGenerationSource);
			var source = GetGeneratedSource(result, "Interceptors.g.cs");

			StringAssert.Contains(source, "private static class Target_Interceptor_State");
			StringAssert.Contains(source, "internal static readonly SR.MemberInfo TargetMethod");
			Assert.IsFalse(source.Contains("internal static readonly global::AspectGenerator.Tests.GeneratorDriver.InstanceAspectAttribute Aspect0", StringComparison.Ordinal));
			StringAssert.Contains(source, "var __aspect__0 = new global::AspectGenerator.Tests.GeneratorDriver.InstanceAspectAttribute(\"audit\") { Level = 2 };");
			StringAssert.Contains(source, "InstanceAspectAttribute.After(__aspect__0, __info__0);");
		}

		[TestMethod]
		public void AsyncVoidTargetReportsDiagnosticTest()
		{
			var result = RunGenerator(AsyncVoidDiagnosticsSource);
			var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Id == AspectDiagnosticID.AsyncHookRequiresTask);

			Assert.IsNotNull(diagnostic, $"Expected diagnostic {AspectDiagnosticID.AsyncHookRequiresTask}. Actual diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.Id))}");
			StringAssert.Contains(diagnostic.GetMessage(), "Async void");
			StringAssert.Contains(diagnostic.GetMessage(), "ValueTask<T>");
		}

		[TestMethod]
		public void AssemblyAspectFilterAppliesAspectTest()
		{
			var result = RunGenerator(FilterAssemblySource);

			AssertGeneratedSourceContains(result, "AssemblyTarget_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "OtherTarget_Interceptor");
		}

		[TestMethod]
		public void TypeAspectFilterAppliesAspectInsideTypeTest()
		{
			var result = RunGenerator(FilterTypeSource);

			AssertGeneratedSourceContains(result, "SaveUser_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "LoadUser_Interceptor");
		}

		[TestMethod]
		public void ContainsTargetFilterAppliesAspectTest()
		{
			var result = RunGenerator(FilterContainsSource);

			AssertGeneratedSourceContains(result, "SaveUser_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "LoadUser_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "HealthCheck_Interceptor");
		}

		[TestMethod]
		public void StringTargetFilterAppliesAspectTest()
		{
			var result = RunGenerator(FilterStringContainsSource);

			AssertGeneratedSourceContains(result, "SaveUser_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "LoadUser_Interceptor");
		}

		[TestMethod]
		public void MultilineStringTargetFilterAppliesAspectTest()
		{
			var result = RunGenerator(FilterMultilineStringContainsSource);

			AssertGeneratedSourceContains(result, "SaveUser_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "LoadUser_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "HealthCheck_Interceptor");
		}

		[TestMethod]
		public void FilterAppliedAspectPreservesAppliedArgumentsTest()
		{
			var result = RunGenerator(FilterAppliedArgumentsSource);
			var source = GetGeneratedSource(result, "Interceptors.g.cs");

			StringAssert.Contains(source, "new global::AspectGenerator.Tests.GeneratorDriver.FilterAspectAttribute() { Category = \"audit\" }");
			var removedApiName = "Aspect" + "Arguments";
			Assert.IsFalse(source.Contains(removedApiName, StringComparison.Ordinal), $"{removedApiName} should not be emitted.");
		}

		[TestMethod]
		public void ExplicitMethodAspectAppliesWhenNoFilterMatchesTest()
		{
			var result = RunGenerator(FilterExplicitMethodSource);

			AssertGeneratedSourceContains(result, "Target_Interceptor");
		}

		[TestMethod]
		public void InvalidAspectFilterRegexReportsDiagnosticTest()
		{
			var result = RunGenerator(FilterInvalidRegexSource);
			var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Id == AspectDiagnosticID.InvalidAspectFilterRegex);

			Assert.IsNotNull(diagnostic, $"Expected diagnostic {AspectDiagnosticID.InvalidAspectFilterRegex}. Actual diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.Id))}");
			StringAssert.Contains(diagnostic.GetMessage(), "Invalid aspect filter regex");
		}

		[TestMethod]
		public void PatternAspectFilterAppliesAspectTest()
		{
			var result = RunGenerator(FilterPatternSource);

			AssertNoDiagnostic(result, AspectDiagnosticID.InvalidAspectFilterRule);
			AssertGeneratedSourceContains(result, "SaveUser_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "LoadUser_Interceptor");
		}

		[TestMethod]
		public void PatternConditionFilterAppliesAspectTest()
		{
			var result = RunGenerator(FilterPatternConditionSource);

			AssertGeneratedSourceContains(result, "SaveAsync_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "Save_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "HealthCheckAsync_Interceptor");
		}

		[TestMethod]
		public void PatternConditionOrValuesApplyAspectTest()
		{
			var result = RunGenerator(FilterPatternConditionOrSource);

			AssertGeneratedSourceContains(result, "SaveAsync_Interceptor");
			AssertGeneratedSourceContains(result, "UpdateWithCancellation_Interceptor");
			AssertGeneratedSourceContains(result, "TryGet_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "Ping_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "Load_Interceptor");
		}

		[TestMethod]
		public void PatternConditionLinesUseAndAcrossDifferentKeysTest()
		{
			var result = RunGenerator(FilterPatternConditionLineAndSource);

			AssertGeneratedSourceContains(result, "SaveWithCancellation_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "SaveWithoutCancellation_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "LoadWithCancellation_Interceptor");
		}

		[TestMethod]
		public void PatternConditionRepeatedKeysUseOrAcrossLinesTest()
		{
			var result = RunGenerator(FilterPatternRepeatedKeyOrSource);

			AssertGeneratedSourceContains(result, "SaveUser_Interceptor");
			AssertGeneratedSourceContains(result, "UpdateUser_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "LoadUser_Interceptor");
		}

		[TestMethod]
		public void PatternConditionInlineAndAppliesToSameFieldAndParametersTest()
		{
			var result = RunGenerator(FilterPatternInlineAndSource);

			AssertGeneratedSourceContains(result, "SaveAsync_Interceptor");
			AssertGeneratedSourceContains(result, "SaveWithDependenciesAsync_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "Save_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "LoadWithCancellationAsync_Interceptor");
		}

		[TestMethod]
		public void PatternConditionLinePrefixesCreateAlternativeAndExcludeGroupsTest()
		{
			var result = RunGenerator(FilterPatternLinePrefixSource);

			AssertGeneratedSourceContains(result, "SaveUser_Interceptor");
			AssertGeneratedSourceContains(result, "RunJob_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "HealthCheck_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "Ping_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "LoadUser_Interceptor");
		}

		[TestMethod]
		public void InvalidPatternConditionPrefixReportsDiagnosticTest()
		{
			var result = RunGenerator(FilterInvalidConditionPrefixSource);
			var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Id == AspectDiagnosticID.InvalidAspectFilterRule);

			Assert.IsNotNull(diagnostic, $"Expected diagnostic {AspectDiagnosticID.InvalidAspectFilterRule}. Actual diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.Id))}");
			StringAssert.Contains(diagnostic.GetMessage(), "Leading '&'");
		}

		[TestMethod]
		public void ContainsAndRegexFiltersDoNotParseInlineOperatorsTest()
		{
			var result = RunGenerator(FilterContainsRegexRawOperatorsSource);

			AssertNoDiagnostic(result, AspectDiagnosticID.InvalidAspectFilterRule);
			AssertGeneratedSourceContains(result, "Save_Interceptor");
		}

		[TestMethod]
		public void PatternPathAliasAppliesAspectTest()
		{
			var result = RunGenerator(FilterPatternPathAliasSource);

			AssertGeneratedSourceContains(result, "Save_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "Skip_Interceptor");
		}

		[TestMethod]
		public void PatternRuleSkipSemanticsPreserveLastEffectiveDecisionTest()
		{
			var result = RunGenerator(FilterPatternRuleSkipSource);

			AssertGeneratedSourceContains(result, "IncludedThenExcludedThenIncluded_Interceptor");
			AssertGeneratedSourceContains(result, "NegativeBeforeInclude_Interceptor");
			AssertGeneratedSourceContains(result, "IncludeThenInclude_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "IncludedThenExcluded_Interceptor");
		}

		[TestMethod]
		public void PatternConditionModifierTokensApplyAspectTest()
		{
			var result = RunGenerator(FilterPatternConditionModifiersSource);

			AssertGeneratedSourceContains(result, "ProtectedInternalTarget_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "ProtectedTarget_Interceptor");
		}

		[TestMethod]
		public void PatternParameterFilterAppliesAspectTest()
		{
			var result = RunGenerator(FilterPatternParametersSource);

			AssertGeneratedSourceContains(result, "SaveWithCancellation_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "SaveWithoutCancellation_Interceptor");
		}

		[TestMethod]
		public void PatternPrimitiveTypeAliasesApplyAspectTest()
		{
			var result = RunGenerator(FilterPatternPrimitiveAliasesSource);

			AssertGeneratedSourceContains(result, "TryGet_Interceptor");
			AssertGeneratedSourceContains(result, "LoadAsync_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "Skip_Interceptor");
		}

		[TestMethod]
		public void PatternNestedGenericTypeAppliesAspectTest()
		{
			var result = RunGenerator(FilterPatternNestedGenericTypeSource);

			AssertGeneratedSourceContains(result, "Save_Interceptor");
			AssertGeneratedSourceDoesNotContain(result, "Skip_Interceptor");
		}

		[TestMethod]
		public void InvalidPatternReportsDiagnosticTest()
		{
			var result = RunGenerator(FilterInvalidPatternSource);
			var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Id == AspectDiagnosticID.InvalidAspectFilterDottedPattern);

			Assert.IsNotNull(diagnostic, $"Expected diagnostic {AspectDiagnosticID.InvalidAspectFilterDottedPattern}. Actual diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.Id))}");
			StringAssert.Contains(diagnostic.GetMessage(), "'**' cannot be used as the final method segment");
		}

		[TestMethod]
		public void EmptyTargetFilterRuleBodyReportsDiagnosticTest()
		{
			foreach (var rule in new[] { "contains:", "regex:", "pattern:" })
			{
				var result = RunGenerator(FilterRuleDiagnosticSource.Replace("FILTER_RULE", rule));

				AssertFilterDiagnostic(
					result,
					AspectDiagnosticID.InvalidAspectFilterRule,
					"Target filter rule body cannot be empty.");
			}
		}

		[TestMethod]
		public void LinePrefixOnNonPatternMatcherReportsDiagnosticTest()
		{
			foreach (var rule in new[] { "& regex: .*Save.*", "| contains: Save" })
			{
				var result = RunGenerator(FilterRuleDiagnosticSource.Replace("FILTER_RULE", rule));

				AssertFilterDiagnostic(
					result,
					AspectDiagnosticID.InvalidAspectFilterRule,
					"can only be used with native condition rules");
			}
		}

		[TestMethod]
		public void InvalidParameterPatternsReportDiagnosticTest()
		{
			foreach (var rule in new[] { "params: this string", "Save(..., ...)" })
			{
				var result = RunGenerator(FilterRuleDiagnosticSource.Replace("FILTER_RULE", rule));

				AssertFilterDiagnostic(
					result,
					AspectDiagnosticID.InvalidAspectFilterParameterPattern,
					rule.Contains("this", StringComparison.Ordinal) ? "'this' is not supported" : "can appear at most once");
			}
		}

		[TestMethod]
		public void EmptyConditionOperandsReportDiagnosticTest()
		{
			foreach (var rule in new[] { "method: Save | ", "method: Save & " })
			{
				var result = RunGenerator(FilterRuleDiagnosticSource.Replace("FILTER_RULE", rule));

				AssertFilterDiagnostic(
					result,
					AspectDiagnosticID.InvalidAspectFilterRule,
					"Condition value has an empty operand");
			}
		}

		[TestMethod]
		public void MethodLevelTargetFilterReportsDiagnosticTest()
		{
			var result = RunGenerator(FilterMethodLevelTargetFilterSource);
			var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Id == AspectDiagnosticID.MethodLevelTargetFilter);

			Assert.IsNotNull(diagnostic, $"Expected diagnostic {AspectDiagnosticID.MethodLevelTargetFilter}. Actual diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.Id))}");
			StringAssert.Contains(diagnostic.GetMessage(), "assembly-level or type-level");
		}

		[TestMethod]
		public void FiltersRespectDesignTimeBuildTest()
		{
			var result = RunGenerator(
				FilterAssemblySource,
				new()
				{
					["build_property.DesignTimeBuild"] = "true",
				});

			AssertNotGenerated(result, "Interceptors.g.cs");
		}

		[TestMethod]
		public void AspectFiltersUseCanonicalMethodSignatureTest()
		{
			var result = RunGenerator(FilterCanonicalSignatureSource);

			foreach (var expected in new[]
			{
				"PublicInstance_Interceptor",
				"StaticVoid_Interceptor",
				"TaskResult_Interceptor",
				"GenericMethod_Interceptor",
				"GenericContainerMethod_Interceptor",
				"ByRef_Interceptor",
				"ExtensionTarget_Interceptor",
				"ArrayParameter_Interceptor",
				"NullableIgnored_Interceptor",
				"VirtualTarget_Interceptor",
				"OverrideTarget_Interceptor",
			})
				AssertGeneratedSourceContains(result, expected);
		}

		[TestMethod]
		public void ConditionalAspectAppliesWhenSymbolIsDefinedTest()
		{
			var result = RunGenerator(ConditionalDirectAspectSource, preprocessorSymbols: ["DEBUG"]);

			AssertGeneratedSourceContains(result, "Target_Interceptor");
		}

		[TestMethod]
		public void ConditionalAspectDoesNotApplyWhenSymbolIsNotDefinedTest()
		{
			var result = RunGenerator(ConditionalDirectAspectSource);

			AssertNotGenerated(result, "Interceptors.g.cs");
		}

		[TestMethod]
		public void ConditionalAspectAppliesWhenAnyConditionalSymbolIsDefinedTest()
		{
			var result = RunGenerator(ConditionalMultipleSymbolsAspectSource, preprocessorSymbols: ["TRACE"]);

			AssertGeneratedSourceContains(result, "Target_Interceptor");
		}

		[TestMethod]
		public void ConditionalAssemblyTargetFilterRespectsConditionalAttributeTest()
		{
			var disabledResult = RunGenerator(ConditionalAssemblyFilterSource);
			var enabledResult  = RunGenerator(ConditionalAssemblyFilterSource, preprocessorSymbols: ["DEBUG"]);

			AssertNotGenerated(disabledResult, "Interceptors.g.cs");
			AssertGeneratedSourceContains(enabledResult, "Target_Interceptor");
		}

		[TestMethod]
		public void ConditionalTypeTargetFilterRespectsConditionalAttributeTest()
		{
			var disabledResult = RunGenerator(ConditionalTypeFilterSource);
			var enabledResult  = RunGenerator(ConditionalTypeFilterSource, preprocessorSymbols: ["DEBUG"]);

			AssertNotGenerated(disabledResult, "Interceptors.g.cs");
			AssertGeneratedSourceContains(enabledResult, "Target_Interceptor");
		}

		[TestMethod]
		public void InterceptedCallMarkerReportsInfoByDefaultTest()
		{
			var diagnostics = RunAnalyzer(TraceDirectSource);
			var diagnostic  = diagnostics.SingleOrDefault(d => d.Id == AspectDiagnosticID.InterceptedCallMarker);

			Assert.IsNotNull(diagnostic, $"Expected diagnostic {AspectDiagnosticID.InterceptedCallMarker}. Actual diagnostics: {string.Join(", ", diagnostics.Select(d => d.Id))}");
			Assert.AreEqual(DiagnosticSeverity.Info, diagnostic.Severity);
			Assert.AreEqual("Call is marked for interception by TraceAspectAttribute", diagnostic.GetMessage());
		}

		[TestMethod]
		public void InterceptedCallMarkerCanBeDisabledTest()
		{
			var diagnostics = RunAnalyzer(
				TraceDirectSource,
				new()
				{
					[$"build_property.AspectGenerator{AspectOptionName.AspectDiagnosticSeverity}"] = "Off",
				});

			AssertNoDiagnostic(diagnostics, AspectDiagnosticID.InterceptedCallMarker);
		}

		[TestMethod]
		public void InterceptedCallMarkerReportsConfiguredSeverityTest()
		{
			AssertMarkerSeverity("Hidden",  DiagnosticSeverity.Hidden);
			AssertMarkerSeverity("Info",    DiagnosticSeverity.Info);
			AssertMarkerSeverity("Warning", DiagnosticSeverity.Warning);
			AssertMarkerSeverity("Error",   DiagnosticSeverity.Error);
			AssertMarkerSeverity("hidden",  DiagnosticSeverity.Hidden);
			AssertMarkerSeverity("0",       DiagnosticSeverity.Hidden);
			AssertMarkerSeverity("1",       DiagnosticSeverity.Info);
			AssertMarkerSeverity("2",       DiagnosticSeverity.Warning);
			AssertMarkerSeverity("3",       DiagnosticSeverity.Error);

			static void AssertMarkerSeverity(string configuredSeverity, DiagnosticSeverity expectedSeverity)
			{
				var diagnostics = RunAnalyzer(
					TraceDirectSource,
					new()
					{
						[$"build_property.AspectGenerator{AspectOptionName.AspectDiagnosticSeverity}"] = configuredSeverity,
					});

				var diagnostic = diagnostics.SingleOrDefault(d => d.Id == AspectDiagnosticID.InterceptedCallMarker);

				Assert.IsNotNull(diagnostic, $"Expected diagnostic {AspectDiagnosticID.InterceptedCallMarker}. Actual diagnostics: {string.Join(", ", diagnostics.Select(d => d.Id))}");
				Assert.AreEqual(expectedSeverity, diagnostic.Severity);
				StringAssert.Contains(diagnostic.GetMessage(), "TraceAspectAttribute");
				Assert.AreEqual("Target()", diagnostic.Location.SourceTree?.GetRoot().FindNode(diagnostic.Location.SourceSpan).ToString());
			}
		}

		[TestMethod]
		public void InvalidMsBuildAspectDiagnosticSeverityReportsWarningAndFallsBackToInfoTest()
		{
			var properties = new Dictionary<string,string>
			{
				[$"build_property.AspectGenerator{AspectOptionName.AspectDiagnosticSeverity}"] = "Offf",
			};
			var result = RunGenerator(TraceDirectSource, properties);
			var invalidDiagnostic = result.Diagnostics.SingleOrDefault(d => d.Id == AspectDiagnosticID.InvalidAspectDiagnosticSeverity);

			AssertInvalidAspectDiagnosticSeverity(invalidDiagnostic, "Offf");
			Assert.AreEqual(Location.None, invalidDiagnostic!.Location);

			var analyzerDiagnostics = RunAnalyzer(TraceDirectSource, properties);
			var marker = analyzerDiagnostics.SingleOrDefault(d => d.Id == AspectDiagnosticID.InterceptedCallMarker);

			Assert.IsNotNull(marker, $"Expected diagnostic {AspectDiagnosticID.InterceptedCallMarker}. Actual diagnostics: {string.Join(", ", analyzerDiagnostics.Select(d => d.Id))}");
			Assert.AreEqual(DiagnosticSeverity.Info, marker.Severity);
			AssertNoDiagnostic(analyzerDiagnostics, AspectDiagnosticID.InvalidAspectDiagnosticSeverity);
		}

		[TestMethod]
		public void InterceptedCallMarkerCanBeConfiguredByAssemblyOptionsTest()
		{
			var diagnostics = RunAnalyzer(TraceAssemblyOptionsMarkerSource);
			var diagnostic = diagnostics.SingleOrDefault(d => d.Id == AspectDiagnosticID.InterceptedCallMarker);

			Assert.IsNotNull(diagnostic, $"Expected diagnostic {AspectDiagnosticID.InterceptedCallMarker}. Actual diagnostics: {string.Join(", ", diagnostics.Select(d => d.Id))}");
			Assert.AreEqual(DiagnosticSeverity.Warning, diagnostic.Severity);
			StringAssert.Contains(diagnostic.GetMessage(), "TraceAspectAttribute");
		}

		[TestMethod]
		public void InterceptedCallMarkerAssemblyOptionsOverrideMsBuildOptionsTest()
		{
			var diagnostics = RunAnalyzer(
				TraceAssemblyOptionsMarkerSource,
				new()
				{
					[$"build_property.AspectGenerator{AspectOptionName.AspectDiagnosticSeverity}"] = "Error",
				});
			var diagnostic = diagnostics.SingleOrDefault(d => d.Id == AspectDiagnosticID.InterceptedCallMarker);

			Assert.IsNotNull(diagnostic, $"Expected diagnostic {AspectDiagnosticID.InterceptedCallMarker}. Actual diagnostics: {string.Join(", ", diagnostics.Select(d => d.Id))}");
			Assert.AreEqual(DiagnosticSeverity.Warning, diagnostic.Severity);
		}

		[TestMethod]
		public void InvalidAssemblyAspectDiagnosticSeverityReportsWarningAtExpressionAndFallsBackToInfoTest()
		{
			var result = RunGenerator(TraceAssemblyOptionsInvalidMarkerSource);
			var invalidDiagnostic = result.Diagnostics.SingleOrDefault(d => d.Id == AspectDiagnosticID.InvalidAspectDiagnosticSeverity);

			AssertInvalidAspectDiagnosticSeverity(invalidDiagnostic, "99");
			Assert.AreEqual("99", invalidDiagnostic!.Location.SourceTree?.GetRoot().FindNode(invalidDiagnostic.Location.SourceSpan).ToString());

			var analyzerDiagnostics = RunAnalyzer(TraceAssemblyOptionsInvalidMarkerSource);
			var marker = analyzerDiagnostics.SingleOrDefault(d => d.Id == AspectDiagnosticID.InterceptedCallMarker);

			Assert.IsNotNull(marker, $"Expected diagnostic {AspectDiagnosticID.InterceptedCallMarker}. Actual diagnostics: {string.Join(", ", analyzerDiagnostics.Select(d => d.Id))}");
			Assert.AreEqual(DiagnosticSeverity.Info, marker.Severity);
			AssertNoDiagnostic(analyzerDiagnostics, AspectDiagnosticID.InvalidAspectDiagnosticSeverity);
		}

		[TestMethod]
		public void InterceptedCallMarkerAssemblyOptionsSupportIdentifierAndNumericSeverityTest()
		{
			AssertAssemblySeverity(TraceAssemblyOptionsIdentifierMarkerSource, DiagnosticSeverity.Hidden);
			AssertAssemblySeverity(TraceAssemblyOptionsNumericMarkerSource, DiagnosticSeverity.Error);
			AssertAssemblySeverity(TraceAssemblyOptionsOffMarkerSource, null);

			static void AssertAssemblySeverity(string source, DiagnosticSeverity? expectedSeverity)
			{
				var diagnostics = RunAnalyzer(source);
				var markerDiagnostics = diagnostics
					.Where(d => d.Id == AspectDiagnosticID.InterceptedCallMarker)
					.ToArray();

				if (expectedSeverity is null)
				{
					Assert.AreEqual(0, markerDiagnostics.Length);
					return;
				}

				Assert.AreEqual(1, markerDiagnostics.Length);
				Assert.AreEqual(expectedSeverity.Value, markerDiagnostics[0].Severity);
			}
		}

		[TestMethod]
		public void InterceptedCallMarkerReportsOneDiagnosticForMultipleAspectsTest()
		{
			var diagnostics = RunAnalyzer(
				MarkerMultipleAspectsSource,
				new()
				{
					[$"build_property.AspectGenerator{AspectOptionName.AspectDiagnosticSeverity}"] = "Warning",
				});
			var markerDiagnostics = diagnostics.Where(d => d.Id == AspectDiagnosticID.InterceptedCallMarker).ToArray();

			Assert.AreEqual(1, markerDiagnostics.Length);
			StringAssert.Contains(markerDiagnostics[0].GetMessage(), "LogAttribute");
			StringAssert.Contains(markerDiagnostics[0].GetMessage(), "AuditAttribute");
		}

		[TestMethod]
		public void InterceptedCallMarkersReportEachCallSiteTest()
		{
			var diagnostics = RunAnalyzer(
				MarkerMultipleCallSitesSource,
				new()
				{
					[$"build_property.AspectGenerator{AspectOptionName.AspectDiagnosticSeverity}"] = "Warning",
				});

			Assert.AreEqual(2, diagnostics.Count(d => d.Id == AspectDiagnosticID.InterceptedCallMarker));
		}

		[TestMethod]
		public void InterceptedCallMarkersReportDuringDesignTimeBuildTest()
		{
			var reportFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "report.txt");

			try
			{
				var result = RunGenerator(
					TraceDirectSource,
					new()
					{
						[$"build_property.AspectGenerator{AspectOptionName.AspectDiagnosticSeverity}"] = "Warning",
						[$"build_property.AspectGenerator{AspectOptionName.ReportFile}"] = reportFile,
						["build_property.DesignTimeBuild"] = "true",
					});
				var diagnostics = RunAnalyzer(
					TraceDirectSource,
					new()
					{
						[$"build_property.AspectGenerator{AspectOptionName.AspectDiagnosticSeverity}"] = "Warning",
						[$"build_property.AspectGenerator{AspectOptionName.ReportFile}"] = reportFile,
						["build_property.DesignTimeBuild"] = "true",
					});

				AssertDiagnostic(diagnostics, AspectDiagnosticID.InterceptedCallMarker);
				AssertNotGenerated(result, "Interceptors.g.cs");
				Assert.IsFalse(File.Exists(reportFile), $"Report file should not be generated for design-time build: {reportFile}");
			}
			finally
			{
				DeleteReportDirectory(reportFile);
			}
		}

		[TestMethod]
		public void ConditionalDisabledAspectDoesNotEmitInterceptedCallMarkerTest()
		{
			var result = RunGenerator(
					ConditionalDirectAspectSource,
					new()
					{
						[$"build_property.AspectGenerator{AspectOptionName.AspectDiagnosticSeverity}"] = "Warning",
						["build_property.DesignTimeBuild"] = "true",
				});
			var diagnostics = RunAnalyzer(
					ConditionalDirectAspectSource,
					new()
					{
						[$"build_property.AspectGenerator{AspectOptionName.AspectDiagnosticSeverity}"] = "Warning",
						["build_property.DesignTimeBuild"] = "true",
				});

			AssertNoDiagnostic(diagnostics, AspectDiagnosticID.InterceptedCallMarker);
			AssertNotGenerated(result, "Interceptors.g.cs");
		}

		[TestMethod]
		public void InterceptedCallMarkersAreSuppressedForExcludedTargetsTest()
		{
			var diagnostics = RunAnalyzer(
				AssemblyIncludeTypeExcludeFilterSource,
				new()
				{
					[$"build_property.AspectGenerator{AspectOptionName.AspectDiagnosticSeverity}"] = "Warning",
				});

			Assert.AreEqual(1, diagnostics.Count(d => d.Id == AspectDiagnosticID.InterceptedCallMarker));
			Assert.IsTrue(diagnostics
				.Where(d => d.Id == AspectDiagnosticID.InterceptedCallMarker)
				.All(d => d.Location.SourceTree?.GetRoot().FindNode(d.Location.SourceSpan).ToString() != "HealthCheck()"));
		}

		[TestMethod]
		public void InterceptedCallMarkersAreSuppressedForDisabledConditionalAspectTest()
		{
			var disabledDiagnostics = RunAnalyzer(
					ConditionalDirectAspectSource,
					new()
					{
						[$"build_property.AspectGenerator{AspectOptionName.AspectDiagnosticSeverity}"] = "Warning",
					});
			var enabledDiagnostics = RunAnalyzer(
					ConditionalDirectAspectSource,
					new()
					{
						[$"build_property.AspectGenerator{AspectOptionName.AspectDiagnosticSeverity}"] = "Warning",
					},
				preprocessorSymbols: ["DEBUG"]);

			AssertNoDiagnostic(disabledDiagnostics, AspectDiagnosticID.InterceptedCallMarker);
			Assert.AreEqual(1, enabledDiagnostics.Count(d => d.Id == AspectDiagnosticID.InterceptedCallMarker));
		}

		[TestMethod]
		public void BuildReportFileGeneratedForNormalBuildTest()
		{
			var reportFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "report.txt");

			try
			{
				var result = RunGenerator(
					TraceFilterSource,
					new()
					{
						[$"build_property.AspectGenerator{AspectOptionName.ReportFile}"] = reportFile,
					});

				Assert.AreEqual(0, result.Diagnostics.Count(IsReportDiagnostic));
				Assert.IsTrue(File.Exists(reportFile), $"Report file was not generated: {reportFile}");

				var report = File.ReadAllText(reportFile);

				StringAssert.Contains(report, "# AspectGenerator Build Report");
				StringAssert.Contains(report, "| Generated call sites | 2 |");
				StringAssert.Contains(report, "| Target methods | 2 |");
				StringAssert.Contains(report, "| Interceptor generation | enabled |");
				StringAssert.Contains(report, "| Generated API | enabled |");
				StringAssert.Contains(report, "| Interceptors namespace | `AspectGenerator` |");
				StringAssert.Contains(report, "## Generated Sources");
				StringAssert.Contains(report, "Interceptors.g.cs");
				StringAssert.Contains(report, "## Target Methods");
				StringAssert.Contains(report, "`AspectGenerator.Tests.GeneratorDriver.Service.Save()`");
				StringAssert.Contains(report, "`Save_Interceptor`");
				StringAssert.Contains(report, "`AspectGenerator.Tests.GeneratorDriver.TraceAspectAttribute`");
				StringAssert.Contains(report, "DeclaredLifetime: Auto");
				StringAssert.Contains(report, "EffectiveLifetime: Static");
				StringAssert.Contains(report, "## Intercepted Call Sites");
				StringAssert.Contains(report, "`Save()`");
				StringAssert.Contains(report, "`Load()`");
			}
			finally
			{
				DeleteReportDirectory(reportFile);
			}
		}

		[TestMethod]
		public void BuildReportFileNotGeneratedForDesignTimeBuildTest()
		{
			var reportFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "report.txt");

			try
			{
				RunGenerator(
					TraceFilterSource,
					new()
					{
						[$"build_property.AspectGenerator{AspectOptionName.ReportFile}"] = reportFile,
						["build_property.DesignTimeBuild"] = "true",
					});

				Assert.IsFalse(File.Exists(reportFile), $"Report file should not be generated for design-time build: {reportFile}");
			}
			finally
			{
				DeleteReportDirectory(reportFile);
			}
		}

		[TestMethod]
		public void BuildReportIgnoresRemovedGenerateInterceptorsPropertyTest()
		{
			var reportFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "report.txt");

			try
			{
				RunGenerator(
					TraceDirectSource,
					new()
					{
						[$"build_property.AspectGenerator{AspectOptionName.ReportFile}"] = reportFile,
						["build_property.AspectGeneratorGenerateInterceptors"] = "false",
					});

				var report = File.ReadAllText(reportFile);

				StringAssert.Contains(report, "| Generated call sites | 1 |");
				StringAssert.Contains(report, "| Target methods | 1 |");
				StringAssert.Contains(report, "| Interceptor generation | enabled |");
				Assert.IsFalse(report.Contains("No interceptor source was emitted.", StringComparison.Ordinal));
			}
			finally
			{
				DeleteReportDirectory(reportFile);
			}
		}

		[TestMethod]
		public void RealDiagnosticsStillEmitWithoutReportDiagnosticsTest()
		{
			var reportFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "report.txt");

			try
			{
				var result = RunGenerator(
					InvalidUnknownConditionKeyFilterSource,
					new()
					{
						[$"build_property.AspectGenerator{AspectOptionName.ReportFile}"] = reportFile,
					});

				AssertDiagnostic(result, AspectDiagnosticID.UnknownAspectFilterConditionKey);
				Assert.AreEqual(0, result.Diagnostics.Count(IsReportDiagnostic));
				Assert.IsTrue(File.Exists(reportFile), $"Report file was not generated: {reportFile}");
			}
			finally
			{
				DeleteReportDirectory(reportFile);
			}
		}

		[TestMethod]
		public void CompileTimeReportingDoesNotAffectGeneratedCodeTest()
		{
			var offResult = RunGenerator(TraceDirectSource);
			var verboseResult = RunGenerator(TraceDirectSource);
			var source = GetGeneratedSource(verboseResult, "Interceptors.g.cs");

			Assert.AreEqual(GetGeneratedSource(offResult, "Interceptors.g.cs"), source);
			Assert.IsFalse(source.Contains("Trace.WriteLine", StringComparison.Ordinal));
			Assert.IsFalse(source.Contains("System.Diagnostics.Trace", StringComparison.Ordinal));
		}

		[TestMethod]
		public void UnknownSimpleTargetFilterConditionKeyReportsDiagnosticTest()
		{
			var result = RunGenerator(InvalidUnknownConditionKeyFilterSource);

			AssertDiagnostic(result, AspectDiagnosticID.UnknownAspectFilterConditionKey);
		}

		[TestMethod]
		public void LeadingOrAfterStandaloneFilterRuleReportsDiagnosticTest()
		{
			foreach (var rule in new[]
			{
				"regex:^public .*$",
				"contains: Save",
				"pattern: MyApp.**.Save*",
				"Save*: System.String",
			})
			{
				var result = RunGenerator(InvalidLeadingOrFilterSource.Replace("FILTER_RULE", rule));

				AssertDiagnostic(result, AspectDiagnosticID.InvalidAspectFilterRule);
				Assert.IsTrue(result.Diagnostics.Any(d =>
					d.Id == AspectDiagnosticID.InvalidAspectFilterRule &&
					d.GetMessage().Contains("Leading '|' requires a previous native condition group.", StringComparison.Ordinal)));
			}
		}

		[TestMethod]
		public void TypeLevelExcludeSuppressesAssemblyLevelIncludeForSameAspectTypeTest()
		{
			var result = RunGenerator(AssemblyIncludeTypeExcludeFilterSource);
			var source = GetGeneratedSource(result, "Interceptors.g.cs");

			StringAssert.Contains(source, "Save_Interceptor");
			Assert.IsFalse(source.Contains("HealthCheck_Interceptor", StringComparison.Ordinal));
		}

		[TestMethod]
		public void AssemblyIncludeAndTypeIncludeApplySameAspectTypeTest()
		{
			var result = RunGenerator(AssemblyIncludeTypeIncludeFilterSource);
			var source = GetGeneratedSource(result, "Interceptors.g.cs");

			StringAssert.Contains(source, "Save_Interceptor");
			StringAssert.Contains(source, "Load_Interceptor");
		}

		[TestMethod]
		public void DirectMethodAspectIsNotSuppressedByTypeLevelExcludeTest()
		{
			var result = RunGenerator(DirectMethodAspectWithTypeExcludeFilterSource);

			StringAssert.Contains(GetGeneratedSource(result, "Interceptors.g.cs"), "HealthCheck_Interceptor");
		}

		[TestMethod]
		public void TypeLevelExcludeDoesNotSuppressDifferentAspectTypeTest()
		{
			var result = RunGenerator(MultipleAspectTypesFilterPrecedenceSource);
			var source = GetGeneratedSource(result, "Interceptors.g.cs");

			StringAssert.Contains(source, "Save_Interceptor");
			StringAssert.Contains(source, "HealthCheck_Interceptor");
		}

		[TestMethod]
		public void SameKeyAndOrConditionGroupSemanticsTest()
		{
			var result = RunGenerator(SameKeyAndOrFilterSource);
			var source = GetGeneratedSource(result, "Interceptors.g.cs");

			StringAssert.Contains(source, "SaveAsync_Interceptor");
			Assert.IsFalse(source.Contains("Save_Interceptor", StringComparison.Ordinal));
			Assert.IsFalse(source.Contains("UpdateAsync_Interceptor", StringComparison.Ordinal));
		}

		[TestMethod]
		public void EmptyInterceptorsNamespacesReportsDiagnosticTest()
		{
			var result = RunGenerator(
				TraceDirectSource,
				new()
				{
					["build_property.InterceptorsNamespaces"] = "",
				});

			AssertDiagnostic(result, AspectDiagnosticID.NamespaceNotAllowed);
			Assert.IsTrue(result.Diagnostics.Any(d =>
				d.Id == AspectDiagnosticID.NamespaceNotAllowed &&
				d.GetMessage().Contains("InterceptorsNamespaces MSBuild property is empty.", StringComparison.Ordinal)));
		}

		static GeneratorDriverRunResult RunGenerator(string source, Dictionary<string,string>? properties = null, string[]? preprocessorSymbols = null)
		{
			return RunGeneratorAndGetCompilation(source, out _, properties, preprocessorSymbols);
		}

		static GeneratorDriverRunResult RunGeneratorAndGetCompilation(string source, out Compilation outputCompilation, Dictionary<string,string>? properties = null, string[]? preprocessorSymbols = null)
		{
			var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

			if (preprocessorSymbols is not null)
				parseOptions = parseOptions.WithPreprocessorSymbols(preprocessorSymbols);

			var compilation  = CSharpCompilation.Create(
				"AspectGenerator.Tests.GeneratorDriver",
				[CSharpSyntaxTree.ParseText(source, parseOptions, "GeneratorDriverTest.cs")],
				GetMetadataReferences(),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

			var effectiveProperties = properties is null
				? new Dictionary<string,string>()
				: new Dictionary<string,string>(properties);

			effectiveProperties.TryAdd("build_property.InterceptorsNamespaces", "AspectGenerator");

			var optionsProvider = new TestAnalyzerConfigOptionsProvider(effectiveProperties);
			var generator       = new AspectSourceGenerator();

			GeneratorDriver driver = CSharpGeneratorDriver.Create(
				[generator.AsSourceGenerator()],
				parseOptions: parseOptions,
				optionsProvider: optionsProvider);

			driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out outputCompilation, out _);

			return driver.GetRunResult();
		}

		static ImmutableArray<Diagnostic> RunAnalyzer(string source, Dictionary<string,string>? properties = null, string[]? preprocessorSymbols = null)
		{
			RunGeneratorAndGetCompilation(source, out var compilation, properties, preprocessorSymbols);

			var effectiveProperties = properties is null
				? new Dictionary<string,string>()
				: new Dictionary<string,string>(properties);

			effectiveProperties.TryAdd("build_property.InterceptorsNamespaces", "AspectGenerator");

			var optionsProvider = new TestAnalyzerConfigOptionsProvider(effectiveProperties);
			var analyzerOptions = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, optionsProvider);
			var analyzers       = ImmutableArray.Create<DiagnosticAnalyzer>(new AspectGeneratorMarkerAnalyzer());

			return compilation
				.WithAnalyzers(analyzers, analyzerOptions)
				.GetAnalyzerDiagnosticsAsync()
				.GetAwaiter()
				.GetResult();
		}

		static IEnumerable<MetadataReference> GetMetadataReferences()
		{
			var trustedPlatformAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;

			return trustedPlatformAssemblies
				.Split(Path.PathSeparator)
				.Select(static path => MetadataReference.CreateFromFile(path));
		}

		static void AssertGenerated(GeneratorDriverRunResult result, string hintName)
		{
			Assert.IsTrue(
				result.Results.SelectMany(static r => r.GeneratedSources).Any(s => string.Equals(s.HintName, hintName, StringComparison.Ordinal)),
				$"Expected generated source '{hintName}'.");
		}

		static void AssertNotGenerated(GeneratorDriverRunResult result, string hintName)
		{
			Assert.IsFalse(
				result.Results.SelectMany(static r => r.GeneratedSources).Any(s => string.Equals(s.HintName, hintName, StringComparison.Ordinal)),
				$"Did not expect generated source '{hintName}'.");
		}

		static void AssertNoDiagnostic(GeneratorDriverRunResult result, string diagnosticId)
		{
			CollectionAssert.DoesNotContain(result.Diagnostics.Select(static d => d.Id).ToArray(), diagnosticId);
		}

		static void AssertNoDiagnostic(ImmutableArray<Diagnostic> diagnostics, string diagnosticId)
		{
			CollectionAssert.DoesNotContain(diagnostics.Select(static d => d.Id).ToArray(), diagnosticId);
		}

		static void AssertDiagnostic(GeneratorDriverRunResult result, string diagnosticId)
		{
			CollectionAssert.Contains(result.Diagnostics.Select(static d => d.Id).ToArray(), diagnosticId);
		}

		static void AssertDiagnostic(ImmutableArray<Diagnostic> diagnostics, string diagnosticId)
		{
			CollectionAssert.Contains(diagnostics.Select(static d => d.Id).ToArray(), diagnosticId);
		}

		static void AssertFilterDiagnostic(GeneratorDriverRunResult result, string diagnosticId, string messageFragment)
		{
			var diagnostic = result.Diagnostics.FirstOrDefault(d =>
				d.Id == diagnosticId &&
				d.GetMessage().Contains(messageFragment, StringComparison.Ordinal));

			Assert.IsNotNull(diagnostic, $"Expected diagnostic {diagnosticId} containing '{messageFragment}'. Actual diagnostics: {string.Join(", ", result.Diagnostics.Select(static d => $"{d.Id}: {d.GetMessage()}"))}");
		}

		static void AssertInvalidAspectDiagnosticSeverity(Diagnostic? diagnostic, string value)
		{
			Assert.IsNotNull(diagnostic, $"Expected diagnostic {AspectDiagnosticID.InvalidAspectDiagnosticSeverity}.");
			Assert.AreEqual(DiagnosticSeverity.Warning, diagnostic.Severity);

			var message = diagnostic.GetMessage();

			StringAssert.Contains(message, value);
			StringAssert.Contains(message, "Off, Hidden, Info, Warning, and Error");
			StringAssert.Contains(message, "project file");
			StringAssert.Contains(message, "Directory.Build.props");
			StringAssert.Contains(message, "imported .props files");
			StringAssert.Contains(message, "AspectGeneratorOptions assembly attribute");
		}

		static string GetGeneratedSource(GeneratorDriverRunResult result, string hintName)
		{
			var sources = result.Results
				.SelectMany(static r => r.GeneratedSources)
				.Where(s => string.Equals(s.HintName, hintName, StringComparison.Ordinal))
				.ToArray();

			Assert.AreEqual(1, sources.Length, $"Expected generated source '{hintName}'.");

			return sources[0].SourceText.ToString();
		}

		static void AssertGeneratedSourceContains(GeneratorDriverRunResult result, string text)
		{
			StringAssert.Contains(GetGeneratedSource(result, "Interceptors.g.cs"), text);
		}

		static void AssertGeneratedSourceDoesNotContain(GeneratorDriverRunResult result, string text)
		{
			Assert.IsFalse(GetGeneratedSource(result, "Interceptors.g.cs").Contains(text, StringComparison.Ordinal), $"Did not expect generated source to contain '{text}'.");
		}

		static bool IsReportDiagnostic(Diagnostic diagnostic)
		{
			return diagnostic.Id.Length == 6 &&
				diagnostic.Id.StartsWith("AG07", StringComparison.Ordinal);
		}

		static void DeleteReportDirectory(string reportFile)
		{
			var directory = Path.GetDirectoryName(reportFile);

			if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
		}

		sealed class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
		{
			readonly AnalyzerConfigOptions _globalOptions;

			public TestAnalyzerConfigOptionsProvider(Dictionary<string,string> properties)
			{
				_globalOptions = new TestAnalyzerConfigOptions(properties);
			}

			public override AnalyzerConfigOptions GlobalOptions => _globalOptions;

			public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _globalOptions;

			public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _globalOptions;
		}

		sealed class TestAnalyzerConfigOptions : AnalyzerConfigOptions
		{
			readonly Dictionary<string,string> _properties;

			public TestAnalyzerConfigOptions(Dictionary<string,string> properties)
			{
				_properties = properties;
			}

			public override bool TryGetValue(string key, out string value)
			{
				return _properties.TryGetValue(key, out value!);
			}
		}

		const string GenerationOptionsSource =
			"""
			using System;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(OnAfterCall))]
			sealed class TestAspectAttribute : Attribute
			{
				public static void OnAfterCall(InterceptInfo<string> info)
				{
					info.ReturnValue += " intercepted";
				}
			}

			static class Service
			{
				public static void Call()
				{
					Target();
				}

				[TestAspect]
				public static string Target()
				{
					return "target";
				}
			}
			""";

		const string AssemblyOptionsSyntaxFallbackSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGeneratorOptions(
				GenerateApi = false,
				PublicApi = true,
				DebuggerStepThrough = true,
				InterceptorsNamespace = "AspectGenerator")]

			namespace AspectGenerator.Tests.GeneratorDriver;
			""";

		const string GenerateInterceptorsAssemblyOptionSource =
			"""
			using AspectGenerator;

			[assembly: AspectGeneratorOptions(GenerateInterceptors = false)]
			""";

		const string HookContractDiagnosticsSource =
			"""
			using System;
			using System.Threading.Tasks;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(WrongParameter))]
			sealed class InvalidParameterAttribute : Attribute
			{
				public static void WrongParameter(string info)
				{
				}
			}

			[Aspect(OnAfterCall = nameof(WrongReturn))]
			sealed class InvalidReturnAttribute : Attribute
			{
				public static int WrongReturn(InterceptInfo<string> info)
				{
					return 0;
				}
			}

			[Aspect(OnCall = nameof(Call))]
			sealed class InvalidOnCallAttribute : Attribute
			{
				public static string Call(string value)
				{
					return value;
				}
			}

			[Aspect(
				OnAfterCall      = nameof(Data),
				UseInterceptData = true)]
			sealed class InvalidInterceptDataAttribute : Attribute
			{
				public static void Data(InterceptInfo<string> info)
				{
				}
			}

			[Aspect(OnAfterCallAsync = nameof(AfterAsync))]
			sealed class InvalidAsyncAttribute : Attribute
			{
				public static Task AfterAsync(InterceptInfo<string> info)
				{
					return Task.CompletedTask;
				}
			}

			static class Target
			{
				public static void Invoke()
				{
					InvalidParameterTarget();
					InvalidReturnTarget();
					InvalidOnCallTarget(1);
					InvalidInterceptDataTarget();
					InvalidAsyncTarget();
				}

				[InvalidParameter]
				public static string InvalidParameterTarget()
				{
					return "";
				}

				[InvalidReturn]
				public static string InvalidReturnTarget()
				{
					return "";
				}

				[InvalidOnCall]
				public static string InvalidOnCallTarget(int value)
				{
					return value.ToString();
				}

				[InvalidInterceptData]
				public static string InvalidInterceptDataTarget()
				{
					return "";
				}

				[InvalidAsync]
				public static string InvalidAsyncTarget()
				{
					return "";
				}
			}
			""";

		const string ValueTaskGenerationSource =
			"""
			using System;
			using System.Threading.Tasks;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCallAsync = nameof(AfterAsync))]
			sealed class ValueTaskAspectAttribute : Attribute
			{
				public static ValueTask AfterAsync(InterceptInfo<int> info)
				{
					info.ReturnValue++;
					return ValueTask.CompletedTask;
				}
			}

			static class Target
			{
				public static void Invoke()
				{
					ValueTaskTarget();
				}

				[ValueTaskAspect]
				public static ValueTask<int> ValueTaskTarget()
				{
					return ValueTask.FromResult(1);
				}
			}
			""";

		const string TypedAspectGenerationSource =
			"""
			using System;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			sealed class TypedAspectAttribute : Attribute
			{
				public TypedAspectAttribute(string category)
				{
					Category = category;
				}

				public string Category { get; }
				public int    Level    { get; set; }

				public static void After(TypedAspectAttribute aspect, InterceptInfo<string> info)
				{
					info.ReturnValue += aspect.Category + aspect.Level.ToString();
				}
			}

			static class TargetType
			{
				public static void Invoke()
				{
					Target();
				}

				[TypedAspect("audit", Level = 2)]
				public static string Target()
				{
					return "";
				}
			}
			""";

		const string InstanceLifetimeGenerationSource =
			"""
			using System;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(
				OnAfterCall = nameof(After),
				Lifetime = AspectInstanceLifetime.Instance)]
			sealed class InstanceAspectAttribute : Attribute
			{
				public InstanceAspectAttribute(string category)
				{
					Category = category;
				}

				public string Category { get; }
				public int    Level    { get; set; }

				public static void After(InstanceAspectAttribute aspect, InterceptInfo<string> info)
				{
					info.ReturnValue += aspect.Category + aspect.Level.ToString();
				}
			}

			static class TargetType
			{
				public static void Invoke()
				{
					Target();
				}

				[InstanceAspect("audit", Level = 2)]
				public static string Target()
				{
					return "";
				}
			}
			""";

		const string AsyncVoidDiagnosticsSource =
			"""
			using System;
			using System.Threading.Tasks;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCallAsync = nameof(AfterAsync))]
			sealed class AsyncVoidAspectAttribute : Attribute
			{
				public static ValueTask AfterAsync(InterceptInfo info)
				{
					return ValueTask.CompletedTask;
				}
			}

			static class Target
			{
				public static void Invoke()
				{
					AsyncVoidTarget();
				}

				[AsyncVoidAspect]
				public static async void AsyncVoidTarget()
				{
					await Task.CompletedTask;
				}
			}
			""";

		const string FilterAssemblySource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(TargetFilter = [@"regex: .*\.AssemblyTarget\(\)$"])]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string[]?        TargetFilter     { get; set; }
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			static class Target
			{
				public static void Invoke()
				{
					AssemblyTarget();
					OtherTarget();
				}

				public static string AssemblyTarget() => "target";
				public static string OtherTarget() => "other";
			}
			""";

		const string FilterTypeSource =
			"""
			using System;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string[]?        TargetFilter     { get; set; }
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			[FilterAspect(TargetFilter = [@"regex: .*\.SaveUser\(\)$"])]
			sealed class UserService
			{
				public void Invoke()
				{
					SaveUser();
					LoadUser();
				}

				public string SaveUser() => "save";
				public string LoadUser() => "load";
			}
			""";

		const string FilterContainsSource =
			"""
			using System;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string[]?        TargetFilter     { get; set; }
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			[FilterAspect(
				TargetFilter =
				[
					"contains: Save",
					"-contains: HealthCheck"
				])]
			sealed class UserService
			{
				public void Invoke()
				{
					SaveUser();
					LoadUser();
					HealthCheck();
				}

				public string SaveUser() => "save";
				public string LoadUser() => "load";
				public string HealthCheck() => "health";
			}
			""";

		const string FilterStringContainsSource =
			"""
			using System;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string?          TargetFilter     { get; set; }
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			[FilterAspect(
				TargetFilter = "contains: Save")]
			sealed class UserService
			{
				public void Invoke()
				{
					SaveUser();
					LoadUser();
				}

				public string SaveUser() => "save";
				public string LoadUser() => "load";
			}
			""";

		const string FilterMultilineStringContainsSource =
			"""
			using System;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string?          TargetFilter     { get; set; }
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			[FilterAspect(
				TargetFilter = @"contains: Save
			# Exclude health probes.
			-contains: HealthCheck")]
			sealed class UserService
			{
				public void Invoke()
				{
					SaveUser();
					LoadUser();
					HealthCheck();
				}

				public string SaveUser() => "save";
				public string LoadUser() => "load";
				public string HealthCheck() => "health";
			}
			""";

		const string FilterAppliedArgumentsSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(
				TargetFilter = "contains: SaveUser",
				Category = "audit")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }
				public string? Category     { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += ((FilterAspectAttribute)info.Aspect!).Category;
			}

			static class Target
			{
				public static void Invoke()
				{
					SaveUser();
				}

				public static string SaveUser() => "save";
			}
			""";

		const string FilterExplicitMethodSource =
			"""
			using System;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string[]?        TargetFilter     { get; set; }
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			[FilterAspect(TargetFilter = [@"-regex: .*\.Target\(\)$"])]
			static class Target
			{
				public static void Invoke()
				{
					Target();
				}

				[FilterAspect]
				public static string Target() => "target";
			}
			""";

		const string FilterInvalidRegexSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(TargetFilter = ["regex: ["])]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string[]?        TargetFilter     { get; set; }
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			static class Target
			{
				public static void Invoke()
				{
					Target();
				}

				public static string Target() => "target";
			}
			""";

		const string FilterPatternSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(TargetFilter = "public **.SaveUser()")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			static class Target
			{
				public static void Invoke()
				{
					SaveUser();
					LoadUser();
				}

				public static string SaveUser() => "save";
				public static string LoadUser() => "load";
			}
			""";

		const string FilterPatternConditionSource =
			"""
			using System;
			using System.Threading.Tasks;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(
				TargetFilter = @"namespace:AspectGenerator.Tests.GeneratorDriver; type:*Service; method:*Async; returns:System.Threading.Tasks.Task*
			-method:HealthCheckAsync")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo info)
				{
				}
			}

			sealed class UserService
			{
				public void Invoke()
				{
					SaveAsync();
					Save();
					HealthCheckAsync();
				}

				public Task<string> SaveAsync() => Task.FromResult("save");
				public string Save() => "save";
				public Task<string> HealthCheckAsync() => Task.FromResult("health");
			}
			""";

		const string FilterPatternConditionOrSource =
			"""
			using System;
			using System.Threading;
			using System.Threading.Tasks;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(
				TargetFilter =
				[
					"namespace:AspectGenerator.Tests.GeneratorDriver | AspectGenerator.Tests.GeneratorDriver.Jobs; type:*Service | *Repository; method:Save* | Update*; returns:System.Threading.Tasks.Task* | System.Threading.Tasks.ValueTask*; param:*CancellationToken | out System.Int32",
					"| namespace:AspectGenerator.Tests.GeneratorDriver; type:*Service; method:TryGet; params:(string, out int)",
					"-method:Ping | HealthCheck"
				])]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string[]? TargetFilter { get; set; }

				public static void After(InterceptInfo info)
				{
				}
			}

			sealed class UserService
			{
				public void Invoke(CancellationToken cancellationToken)
				{
					SaveAsync(cancellationToken);
					UpdateWithCancellation(1, cancellationToken);
					TryGet("id", out _);
					Ping(cancellationToken);
					Load();
				}

				public Task<string> SaveAsync(CancellationToken cancellationToken) => Task.FromResult("");
				public ValueTask<string> UpdateWithCancellation(int value, CancellationToken cancellationToken) => ValueTask.FromResult("");
				public bool TryGet(string id, out int value)
				{
					value = 1;
					return true;
				}
				public Task<string> Ping(CancellationToken cancellationToken) => Task.FromResult("");
				public string Load() => "";
			}
			""";

		const string FilterPatternConditionLineAndSource =
			"""
			using System;
			using System.Threading;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(
				TargetFilter = @"
				namespace: AspectGenerator.Tests.GeneratorDriver
				type: *Service
				method: Save*
				params: ..., *CancellationToken
				")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			sealed class UserService
			{
				public void Invoke(CancellationToken cancellationToken)
				{
					SaveWithCancellation(cancellationToken);
					SaveWithoutCancellation();
					LoadWithCancellation(cancellationToken);
				}

				public string SaveWithCancellation(CancellationToken cancellationToken) => "";
				public string SaveWithoutCancellation() => "";
				public string LoadWithCancellation(CancellationToken cancellationToken) => "";
			}
			""";

		const string FilterPatternInlineAndSource =
			"""
			using System;
			using System.Threading;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(
				TargetFilter =
				[
					"method: Save* & *Async",
					"| param: *CancellationToken & *IDisposable"
				])]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string[]? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			sealed class UserService
			{
				public void Invoke(CancellationToken cancellationToken, IDisposable disposable)
				{
					SaveAsync();
					Save();
					SaveWithDependenciesAsync(cancellationToken, disposable);
					LoadWithCancellationAsync(cancellationToken);
				}

				public string SaveAsync() => "";
				public string Save() => "";
				public string SaveWithDependenciesAsync(CancellationToken cancellationToken, IDisposable disposable) => "";
				public string LoadWithCancellationAsync(CancellationToken cancellationToken) => "";
			}
			""";

		const string FilterPatternRepeatedKeyOrSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(
				TargetFilter = @"
				type: *Service
				method: Save*
				method: Update*
				")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			sealed class UserService
			{
				public void Invoke()
				{
					SaveUser();
					UpdateUser();
					LoadUser();
				}

				public string SaveUser() => "";
				public string UpdateUser() => "";
				public string LoadUser() => "";
			}
			""";

		const string FilterPatternLinePrefixSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(
				TargetFilter = @"
				namespace: AspectGenerator.Tests.GeneratorDriver
				& type: *Service
				& method: Save*

				| namespace: AspectGenerator.Tests.GeneratorDriver
				& type: *Job
				& method: Run*

				- method: HealthCheck
				| method: Ping
				")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			sealed class UserService
			{
				public void Invoke()
				{
					SaveUser();
					LoadUser();
					HealthCheck();
					Ping();
					new ImportJob().RunJob();
				}

				public string SaveUser() => "";
				public string LoadUser() => "";
				public string HealthCheck() => "";
				public string Ping() => "";
			}

			sealed class ImportJob
			{
				public string RunJob() => "";
			}
			""";

		const string FilterInvalidConditionPrefixSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(TargetFilter = "& method: Save")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			static class Target
			{
				public static void Invoke()
				{
					Save();
				}

				public static string Save() => "";
			}
			""";

		const string FilterContainsRegexRawOperatorsSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(
				TargetFilter =
				[
					"contains: Save|",
					"regex: ^public .*\\.Save\\(\\)$"
				])]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string[]? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			static class Target
			{
				public static void Invoke()
				{
					Save();
				}

				public static string Save() => "";
			}
			""";

		const string FilterPatternPathAliasSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(TargetFilter = "path:AspectGenerator.Tests.GeneratorDriver.*Service; method:Save")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			sealed class UserService
			{
				public void Invoke()
				{
					Save();
					Skip();
				}

				public string Save() => "save";
				public string Skip() => "skip";
			}
			""";

		const string FilterPatternRuleSkipSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(
				TargetFilter =
				[
					"method:IncludedThenExcludedThenIncluded",
					"-method:IncludedThenExcludedThenIncluded",
					"method:IncludedThenExcludedThenIncluded",
					"method:IncludedThenExcluded",
					"-method:IncludedThenExcluded",
					"-method:NegativeBeforeInclude",
					"method:NegativeBeforeInclude",
					"method:IncludeThenInclude",
					"method:IncludeThenInclude"
				])]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string[]? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			static class Target
			{
				public static void Invoke()
				{
					IncludedThenExcludedThenIncluded();
					IncludedThenExcluded();
					NegativeBeforeInclude();
					IncludeThenInclude();
				}

				public static string IncludedThenExcludedThenIncluded() => "";
				public static string IncludedThenExcluded() => "";
				public static string NegativeBeforeInclude() => "";
				public static string IncludeThenInclude() => "";
			}
			""";

		const string FilterPatternConditionModifiersSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(TargetFilter = "fulltype:AspectGenerator.Tests.GeneratorDriver.UserService; protected internal")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			sealed class UserService
			{
				public void Invoke()
				{
					ProtectedInternalTarget();
					ProtectedTarget();
				}

				protected internal string ProtectedInternalTarget() => "target";
				protected string ProtectedTarget() => "target";
			}
			""";

		const string FilterPatternParametersSource =
			"""
			using System;
			using System.Threading;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(TargetFilter = "Save*(..., *CancellationToken)")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			sealed class UserService
			{
				public void Invoke(CancellationToken cancellationToken)
				{
					SaveWithCancellation(1, cancellationToken);
					SaveWithoutCancellation(1);
				}

				public string SaveWithCancellation(int value, CancellationToken cancellationToken) => "save";
				public string SaveWithoutCancellation(int value) => "save";
			}
			""";

		const string FilterPatternPrimitiveAliasesSource =
			"""
			using System;
			using System.Threading.Tasks;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(
				TargetFilter =
				[
					"TryGet(string, out int) : bool",
					"LoadAsync() : System.Threading.Tasks.Task<string>"
				])]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string[]? TargetFilter { get; set; }

				public static void After(InterceptInfo info)
				{
				}
			}

			sealed class UserService
			{
				public void Invoke()
				{
					TryGet("id", out _);
					LoadAsync();
					Skip(1);
				}

				public bool TryGet(string id, out int value)
				{
					value = 1;
					return true;
				}

				public Task<string> LoadAsync() => Task.FromResult("");
				public string Skip(int value) => value.ToString();
			}
			""";

		const string FilterPatternNestedGenericTypeSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(
				TargetFilter = "fulltype:**.Outer<*>.Inner<*>; method:Save; returns:*Inner<*>")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo info)
				{
				}
			}

			sealed class Outer<T>
			{
				public sealed class Inner<U>
				{
					public Inner<U> Save() => this;
					public string Skip() => "";
				}
			}

			static class Target
			{
				public static void Invoke()
				{
					var target = new Outer<string>.Inner<int>();

					target.Save();
					target.Skip();
				}
			}
			""";

		const string FilterInvalidPatternSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(TargetFilter = "A.**.B.**")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			static class Target
			{
				public static void Invoke()
				{
					SaveUser();
				}

				public static string SaveUser() => "save";
			}
			""";

		const string FilterRuleDiagnosticSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(TargetFilter = "FILTER_RULE")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			static class Target
			{
				public static void Invoke()
				{
					Save();
				}

				public static string Save() => "save";
			}
			""";

		const string FilterMethodLevelTargetFilterSource =
			"""
			using System;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " filtered";
			}

			static class Target
			{
				public static void Invoke()
				{
					Load();
				}

				[FilterAspect(TargetFilter = "Save")]
				public static string Load() => "load";
			}
			""";

		const string FilterCanonicalSignatureSource =
			"""
			#nullable enable
			using System;
			using System.Threading.Tasks;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(
				TargetFilter =
				[
					@"regex: ^public System.String AspectGenerator\.Tests\.GeneratorDriver\.CanonicalTarget\.PublicInstance\(System.Int32\)$",
					@"regex: ^public static System.Void AspectGenerator\.Tests\.GeneratorDriver\.CanonicalTarget\.StaticVoid\(\)$",
					@"regex: ^public System.Threading.Tasks.Task<System.String> AspectGenerator\.Tests\.GeneratorDriver\.CanonicalTarget\.TaskResult\(\)$",
					@"regex: ^public System.String AspectGenerator\.Tests\.GeneratorDriver\.CanonicalTarget\.GenericMethod<System.String>\(System.String\)$",
					@"regex: ^public System.String AspectGenerator\.Tests\.GeneratorDriver\.GenericContainer<System.String>\.GenericContainerMethod\(System.String\)$",
					@"regex: ^public System.Boolean AspectGenerator\.Tests\.GeneratorDriver\.CanonicalTarget\.ByRef\(ref System.Int32,out System.Int32,in System.Int32\)$",
					@"regex: ^public static System.String AspectGenerator\.Tests\.GeneratorDriver\.CanonicalExtensions\.ExtensionTarget\(this AspectGenerator\.Tests\.GeneratorDriver\.CanonicalTarget,System.Int32\)$",
					@"regex: ^public System.String\[\] AspectGenerator\.Tests\.GeneratorDriver\.CanonicalTarget\.ArrayParameter\(System.String\[\]\)$",
					@"regex: ^public System.String AspectGenerator\.Tests\.GeneratorDriver\.CanonicalTarget\.NullableIgnored\(System.String\)$",
					@"regex: ^public virtual System.String AspectGenerator\.Tests\.GeneratorDriver\.CanonicalBase\.VirtualTarget\(\)$",
					@"regex: ^public override System.String AspectGenerator\.Tests\.GeneratorDriver\.CanonicalDerived\.OverrideTarget\(\)$"
				])]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string[]?        TargetFilter     { get; set; }
				public static void After(InterceptInfo info)
				{
				}
			}

			class CanonicalBase
			{
				public virtual string VirtualTarget() => "";
			}

			class CanonicalDerived : CanonicalBase
			{
				public override string OverrideTarget() => "";
			}

			class GenericContainer<T>
			{
				public T GenericContainerMethod(T value) => value;
			}

			class CanonicalTarget
			{
				public void Invoke()
				{
					var value = 1;
					var input = 2;

					PublicInstance(1);
					StaticVoid();
					TaskResult();
					GenericMethod("value");
					new GenericContainer<string>().GenericContainerMethod("value");
					ByRef(ref value, out _, in input);
					this.ExtensionTarget(1);
					ArrayParameter(["value"]);
					NullableIgnored(null);
					new CanonicalBase().VirtualTarget();
					new CanonicalDerived().OverrideTarget();
				}

				public string PublicInstance(int value) => value.ToString();
				public static void StaticVoid() {}
				public Task<string> TaskResult() => Task.FromResult("");
				public T GenericMethod<T>(T value) => value;
				public bool ByRef(ref int value, out int outValue, in int inValue)
				{
					value++;
					outValue = inValue;
					return true;
				}
				public string[] ArrayParameter(string[] values) => values;
				public string? NullableIgnored(string? value) => value;
			}

			static class CanonicalExtensions
			{
				public static string ExtensionTarget(this CanonicalTarget target, int value) => value.ToString();
			}
			""";

		const string ConditionalDirectAspectSource =
			"""
			using System;
			using System.Diagnostics;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Conditional("DEBUG")]
			[Aspect(OnAfterCall = nameof(After))]
			sealed class DebugAspectAttribute : Attribute
			{
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " debug";
			}

			static class Service
			{
				public static void Invoke()
				{
					Target();
				}

				[DebugAspect]
				public static string Target() => "";
			}
			""";

		const string ConditionalMultipleSymbolsAspectSource =
			"""
			using System;
			using System.Diagnostics;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Conditional("DEBUG")]
			[Conditional("TRACE")]
			[Aspect(OnAfterCall = nameof(After))]
			sealed class TraceOrDebugAspectAttribute : Attribute
			{
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " trace";
			}

			static class Service
			{
				public static void Invoke()
				{
					Target();
				}

				[TraceOrDebugAspect]
				public static string Target() => "";
			}
			""";

		const string ConditionalAssemblyFilterSource =
			"""
			using System;
			using System.Diagnostics;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.DebugFilterAspect(TargetFilter = "method: Target")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Conditional("DEBUG")]
			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class DebugFilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " debug";
			}

			static class Service
			{
				public static void Invoke()
				{
					Target();
				}

				public static string Target() => "";
			}
			""";

		const string ConditionalTypeFilterSource =
			"""
			using System;
			using System.Diagnostics;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Conditional("DEBUG")]
			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class DebugFilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " debug";
			}

			[DebugFilterAspect(TargetFilter = "method: Target")]
			sealed class Service
			{
				public void Invoke()
				{
					Target();
				}

				public string Target() => "";
			}
			""";

		const string TraceDirectSource =
			"""
			using System;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			sealed class TraceAspectAttribute : Attribute
			{
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " traced";
			}

			static class Service
			{
				public static void Invoke()
				{
					Target();
				}

				[TraceAspect]
				public static string Target() => "";
			}
			""";

		const string MarkerMultipleAspectsSource =
			"""
			using System;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			sealed class LogAttribute : Attribute
			{
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " logged";
			}

			[Aspect(OnAfterCall = nameof(After))]
			sealed class AuditAttribute : Attribute
			{
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " audited";
			}

			static class Service
			{
				public static void Invoke()
				{
					Target();
				}

				[Log]
				[Audit]
				public static string Target() => "";
			}
			""";

		const string TraceAssemblyOptionsMarkerSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGeneratorOptions(AspectDiagnosticSeverity = AspectDiagnosticSeverity.Warning)]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			sealed class TraceAspectAttribute : Attribute
			{
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " traced";
			}

			static class Service
			{
				public static void Invoke()
				{
					Target();
				}

				[TraceAspect]
				public static string Target() => "";
			}
			""";

		const string TraceAssemblyOptionsIdentifierMarkerSource =
			"""
			using System;
			using AspectGenerator;
			using static AspectGenerator.AspectDiagnosticSeverity;

			[assembly: AspectGeneratorOptions(AspectDiagnosticSeverity = Hidden)]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			sealed class TraceAspectAttribute : Attribute
			{
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " traced";
			}

			static class Service
			{
				public static void Invoke()
				{
					Target();
				}

				[TraceAspect]
				public static string Target() => "";
			}
			""";

		const string TraceAssemblyOptionsNumericMarkerSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGeneratorOptions(AspectDiagnosticSeverity = 3)]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			sealed class TraceAspectAttribute : Attribute
			{
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " traced";
			}

			static class Service
			{
				public static void Invoke()
				{
					Target();
				}

				[TraceAspect]
				public static string Target() => "";
			}
			""";

		const string TraceAssemblyOptionsOffMarkerSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGeneratorOptions(AspectDiagnosticSeverity = -1)]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			sealed class TraceAspectAttribute : Attribute
			{
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " traced";
			}

			static class Service
			{
				public static void Invoke()
				{
					Target();
				}

				[TraceAspect]
				public static string Target() => "";
			}
			""";

		const string TraceAssemblyOptionsInvalidMarkerSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGeneratorOptions(AspectDiagnosticSeverity = 99)]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			sealed class TraceAspectAttribute : Attribute
			{
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " traced";
			}

			static class Service
			{
				public static void Invoke()
				{
					Target();
				}

				[TraceAspect]
				public static string Target() => "";
			}
			""";

		const string MarkerMultipleCallSitesSource =
			"""
			using System;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			sealed class LogAttribute : Attribute
			{
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " logged";
			}

			static class Service
			{
				public static void Invoke()
				{
					Target();
					Target();
				}

				[Log]
				public static string Target() => "";
			}
			""";

		const string TraceFilterSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.TraceAspect(
				TargetFilter = @"
				method: *
				- method: HealthCheck
				")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class TraceAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info) => info.ReturnValue += " traced";
			}

			static class Service
			{
				public static void Invoke()
				{
					Save();
					HealthCheck();
					Load();
				}

				public static string Save() => "";
				public static string HealthCheck() => "";
				public static string Load() => "";
			}
			""";

		const string InvalidUnknownConditionKeyFilterSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "foo: bar")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly, Inherited = false, AllowMultiple = true)]
			sealed class LogAttribute : Attribute
			{
				public string? TargetFilter { get; set; }
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " logged";
			}
			""";

		const string InvalidLeadingOrFilterSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.Log(
				TargetFilter = @"
				namespace: MyApp.Services
				FILTER_RULE
				| method: Save*
				")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly, Inherited = false, AllowMultiple = true)]
			sealed class LogAttribute : Attribute
			{
				public string? TargetFilter { get; set; }
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " logged";
			}
			""";

		const string AssemblyIncludeTypeExcludeFilterSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "namespace: AspectGenerator.Tests.GeneratorDriver")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
			sealed class LogAttribute : Attribute
			{
				public string? TargetFilter { get; set; }
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " logged";
			}

			[Log(TargetFilter = "- method: HealthCheck")]
			static class Service
			{
				public static void Invoke()
				{
					Save();
					HealthCheck();
				}

				public static string Save() => "";
				public static string HealthCheck() => "";
			}
			""";

		const string AssemblyIncludeTypeIncludeFilterSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "method: Save")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
			sealed class LogAttribute : Attribute
			{
				public string? TargetFilter { get; set; }
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " logged";
			}

			[Log(TargetFilter = "method: Load")]
			static class Service
			{
				public static void Invoke()
				{
					Save();
					Load();
				}

				public static string Save() => "";
				public static string Load() => "";
			}
			""";

		const string DirectMethodAspectWithTypeExcludeFilterSource =
			"""
			using System;
			using AspectGenerator;

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class LogAttribute : Attribute
			{
				public string? TargetFilter { get; set; }
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " logged";
			}

			[Log(TargetFilter = "- method: HealthCheck")]
			static class Service
			{
				public static void Invoke()
				{
					HealthCheck();
				}

				[Log]
				public static string HealthCheck() => "";
			}
			""";

		const string MultipleAspectTypesFilterPrecedenceSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "method: *")]
			[assembly: AspectGenerator.Tests.GeneratorDriver.Audit(TargetFilter = "method: HealthCheck")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
			sealed class LogAttribute : Attribute
			{
				public string? TargetFilter { get; set; }
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " logged";
			}

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly, Inherited = false, AllowMultiple = true)]
			sealed class AuditAttribute : Attribute
			{
				public string? TargetFilter { get; set; }
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " audited";
			}

			[Log(TargetFilter = "- method: HealthCheck")]
			static class Service
			{
				public static void Invoke()
				{
					Save();
					HealthCheck();
				}

				public static string Save() => "";
				public static string HealthCheck() => "";
			}
			""";

		const string SameKeyAndOrFilterSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.Log(
				TargetFilter = @"
				method: Save*
				& method: *Async
				method: Update*
				")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly, Inherited = false, AllowMultiple = true)]
			sealed class LogAttribute : Attribute
			{
				public string? TargetFilter { get; set; }
				public static void After(InterceptInfo<string> info) => info.ReturnValue += " logged";
			}

			static class Service
			{
				public static void Invoke()
				{
					Save();
					SaveAsync();
					UpdateAsync();
				}

				public static string Save() => "";
				public static string SaveAsync() => "";
				public static string UpdateAsync() => "";
			}
			""";

	}
}
