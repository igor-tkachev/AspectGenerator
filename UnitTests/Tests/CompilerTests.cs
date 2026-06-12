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
				[AspectSourceGenerator.DiagnosticID.AsyncHookRequiresTask]     = ["Async hook",            "Task or Task<T>"],
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
	}
}
