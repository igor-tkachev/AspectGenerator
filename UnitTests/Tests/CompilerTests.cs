using System;
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
				[AspectSourceGenerator.DiagnosticID.HookInvalidParameters]     = ["WrongParameter",        "invalid parameter list"],
				[AspectSourceGenerator.DiagnosticID.HookInvalidReturnType]     = ["WrongReturn",           "invalid return type"],
				[AspectSourceGenerator.DiagnosticID.OnCallHookMismatch]        = ["OnCall hook",           "must match target method"],
				[AspectSourceGenerator.DiagnosticID.HookRequiresInterceptData] = ["UseInterceptData=true", "ref InterceptData<T>"],
				[AspectSourceGenerator.DiagnosticID.AsyncHookRequiresTask]     = ["Async hook",            "ValueTask<T>"],
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
		public void GenerateInterceptorsOptionTest()
		{
			var disabledResult = RunGenerator(
				GenerationOptionsSource,
				new()
				{
					[$"build_property.AspectGenerator{AspectSourceGenerator.OptionID.GenerateInterceptors}"] = "false",
				});

			AssertGenerated   (disabledResult, "AspectAttribute.g.cs");
			AssertNotGenerated(disabledResult, "Interceptors.g.cs");

			var designTimeResult = RunGenerator(
				GenerationOptionsSource,
				new()
				{
					["build_property.DesignTimeBuild"] = "true",
				});

			AssertGenerated   (designTimeResult, "AspectAttribute.g.cs");
			AssertNotGenerated(designTimeResult, "Interceptors.g.cs");

			var enabledResult = RunGenerator(
				GenerationOptionsSource,
				new()
				{
					[$"build_property.AspectGenerator{AspectSourceGenerator.OptionID.GenerateInterceptors}"] = "true",
				});

			AssertGenerated(enabledResult, "AspectAttribute.g.cs");
			AssertGenerated(enabledResult, "Interceptors.g.cs");
		}

		[TestMethod]
		public void HookContractDiagnosticsRunWhenInterceptorsAreDisabledTest()
		{
			var result = RunGenerator(
				HookContractDiagnosticsSource,
				new()
				{
					[$"build_property.AspectGenerator{AspectSourceGenerator.OptionID.GenerateInterceptors}"] = "false",
				});

			CollectionAssert.Contains(result.Diagnostics.Select(static d => d.Id).ToArray(), AspectSourceGenerator.DiagnosticID.HookInvalidParameters);
			CollectionAssert.Contains(result.Diagnostics.Select(static d => d.Id).ToArray(), AspectSourceGenerator.DiagnosticID.AsyncHookRequiresTask);

			AssertNotGenerated(result, "Interceptors.g.cs");
		}

		[TestMethod]
		public void ValueTaskTargetSupportsAsyncHooksTest()
		{
			var result = RunGenerator(ValueTaskGenerationSource);

			CollectionAssert.DoesNotContain(result.Diagnostics.Select(static d => d.Id).ToArray(), AspectSourceGenerator.DiagnosticID.AsyncHookRequiresTask);
			AssertGenerated(result, "Interceptors.g.cs");
		}

		[TestMethod]
		public void AsyncVoidTargetReportsDiagnosticTest()
		{
			var result = RunGenerator(AsyncVoidDiagnosticsSource);
			var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Id == AspectSourceGenerator.DiagnosticID.AsyncHookRequiresTask);

			Assert.IsNotNull(diagnostic, $"Expected diagnostic {AspectSourceGenerator.DiagnosticID.AsyncHookRequiresTask}. Actual diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.Id))}");
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

			StringAssert.Contains(source, "[\"Category\"] = \"audit\"");
			Assert.IsFalse(source.Contains("[\"TargetFilter\"]", StringComparison.Ordinal), "TargetFilter is a selector and should not be emitted into runtime AspectArguments.");
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
			var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Id == AspectSourceGenerator.DiagnosticID.InvalidAspectFilterRegex);

			Assert.IsNotNull(diagnostic, $"Expected diagnostic {AspectSourceGenerator.DiagnosticID.InvalidAspectFilterRegex}. Actual diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.Id))}");
			StringAssert.Contains(diagnostic.GetMessage(), "Invalid aspect filter regex");
		}

		[TestMethod]
		public void PatternAspectFilterAppliesAspectTest()
		{
			var result = RunGenerator(FilterPatternSource);

			AssertNoDiagnostic(result, AspectSourceGenerator.DiagnosticID.InvalidAspectFilterRule);
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
			var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Id == AspectSourceGenerator.DiagnosticID.InvalidAspectFilterRule);

			Assert.IsNotNull(diagnostic, $"Expected diagnostic {AspectSourceGenerator.DiagnosticID.InvalidAspectFilterRule}. Actual diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.Id))}");
			StringAssert.Contains(diagnostic.GetMessage(), "Leading '&'");
		}

		[TestMethod]
		public void ContainsAndRegexFiltersDoNotParseInlineOperatorsTest()
		{
			var result = RunGenerator(FilterContainsRegexRawOperatorsSource);

			AssertNoDiagnostic(result, AspectSourceGenerator.DiagnosticID.InvalidAspectFilterRule);
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
			var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Id == AspectSourceGenerator.DiagnosticID.InvalidAspectFilterDottedPattern);

			Assert.IsNotNull(diagnostic, $"Expected diagnostic {AspectSourceGenerator.DiagnosticID.InvalidAspectFilterDottedPattern}. Actual diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.Id))}");
			StringAssert.Contains(diagnostic.GetMessage(), "'**' cannot be used as the final method segment");
		}

		[TestMethod]
		public void MethodLevelTargetFilterReportsDiagnosticTest()
		{
			var result = RunGenerator(FilterMethodLevelTargetFilterSource);
			var diagnostic = result.Diagnostics.SingleOrDefault(d => d.Id == AspectSourceGenerator.DiagnosticID.MethodLevelTargetFilter);

			Assert.IsNotNull(diagnostic, $"Expected diagnostic {AspectSourceGenerator.DiagnosticID.MethodLevelTargetFilter}. Actual diagnostics: {string.Join(", ", result.Diagnostics.Select(d => d.Id))}");
			StringAssert.Contains(diagnostic.GetMessage(), "assembly-level or type-level");
		}

		[TestMethod]
		public void FiltersDoNotGenerateInterceptorsWhenInterceptorsAreDisabledTest()
		{
			var result = RunGenerator(
				FilterAssemblySource,
				new()
				{
					[$"build_property.AspectGenerator{AspectSourceGenerator.OptionID.GenerateInterceptors}"] = "false",
				});

			AssertNotGenerated(result, "Interceptors.g.cs");
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

		static GeneratorDriverRunResult RunGenerator(string source, Dictionary<string,string>? properties = null)
		{
			var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
			var compilation  = CSharpCompilation.Create(
				"AspectGenerator.Tests.GeneratorDriver",
				[CSharpSyntaxTree.ParseText(source, parseOptions, "GeneratorDriverTest.cs")],
				GetMetadataReferences(),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

			var optionsProvider = new TestAnalyzerConfigOptionsProvider(properties ?? new());
			var generator       = new AspectSourceGenerator();

			GeneratorDriver driver = CSharpGeneratorDriver.Create(
				[generator.AsSourceGenerator()],
				parseOptions: parseOptions,
				optionsProvider: optionsProvider);

			driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

			return driver.GetRunResult();
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

				public static void After(InterceptInfo<string> info) => info.ReturnValue += info.AspectArguments["Category"];
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
	}
}
