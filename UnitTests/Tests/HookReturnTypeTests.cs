using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// AG0104 names the return type a hook needs, and the task types the target framework has. A return type the compiler
	/// cannot resolve is the compiler's error, not a second AspectGenerator one.
	/// </summary>
	public partial class CompilerTests
	{
		const string AsyncHookReturnTypeSource =
			"""
			using System;
			using System.Threading.Tasks;
			using AspectGenerator;

			[Aspect(OnCatchAsync = nameof(OnCatchAsync))]
			[AttributeUsage(AttributeTargets.Method)]
			sealed class VoidAsyncHookAttribute : Attribute
			{
				public static void OnCatchAsync(InterceptInfo info)
				{
				}
			}

			[Aspect(OnAfterCall = nameof(OnAfterCall))]
			[AttributeUsage(AttributeTargets.Method)]
			sealed class IntHookAttribute : Attribute
			{
				public static int OnAfterCall(InterceptInfo info)
				{
					return 0;
				}
			}

			static class Calls
			{
				[VoidAsyncHook] public static async Task<int> RunAsync() { await Task.Yield(); return 1; }
				[IntHook]       public static int Run() { return 1; }

				public static Task<int> Go() { return Run() == 1 ? RunAsync() : Task.FromResult(0); }
			}
			""";

		[TestMethod]
		public void InvalidHookReturnTypeNamesTheExpectedTypeTest()
		{
			var result   = RunGenerator(AsyncHookReturnTypeSource);
			var messages = result.Diagnostics.Where(static d => d.Id == AspectDiagnosticID.HookInvalidReturnType).Select(static d => d.GetMessage()).ToArray();

			Assert.AreEqual(2, messages.Length, string.Join(Environment.NewLine, result.Diagnostics));
			Assert.AreEqual(1, messages.Count(static m => m.Contains("'OnCatchAsync': an async hook must return System.Threading.Tasks.Task or System.Threading.Tasks.ValueTask.", StringComparison.Ordinal)), string.Join(Environment.NewLine, messages));
			Assert.AreEqual(1, messages.Count(static m => m.Contains("'OnAfterCall': it must return void.", StringComparison.Ordinal)), string.Join(Environment.NewLine, messages));
		}

		[TestMethod]
		public void AsyncHookReturnTypeWithoutValueTaskNamesTheTargetFrameworkTest()
		{
			// netstandard2.0 has Task but no ValueTask, as .NET Framework has not.
			var netstandard = Path.Combine(
				Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } packages
					? packages
					: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"),
				"netstandard.library", "2.0.3", "build", "netstandard2.0", "ref", "netstandard.dll");

			if (!File.Exists(netstandard))
				Assert.Inconclusive($"The netstandard2.0 reference assembly is not restored: {netstandard}");

			var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
			var compilation  = CSharpCompilation.Create(
				"AspectGenerator.Tests.NoValueTask",
				[CSharpSyntaxTree.ParseText(AsyncHookReturnTypeSource, parseOptions, "NoValueTask.cs")],
				[MetadataReference.CreateFromFile(netstandard)],
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

			Assert.IsNull(compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask"));

			var properties = new Dictionary<string,string>
			{
				["build_property.InterceptorsNamespaces"]      = "AspectGenerator",
				["build_property.AspectGeneratorPropsImported"] = "true",
				["build_property.TargetFramework"]             = "net472",
			};

			GeneratorDriver driver = CSharpGeneratorDriver.Create(
				[new AspectSourceGenerator().AsSourceGenerator()],
				parseOptions   : parseOptions,
				optionsProvider: new TestAnalyzerConfigOptionsProvider(properties));

			var result  = driver.RunGenerators(compilation).GetRunResult();
			var message = result.Diagnostics.Single(static d => d.Id == AspectDiagnosticID.HookInvalidReturnType && d.GetMessage().Contains("OnCatchAsync", StringComparison.Ordinal)).GetMessage();

			StringAssert.Contains(message, "an async hook must return System.Threading.Tasks.Task; ValueTask is not available on target framework 'net472' unless the System.Threading.Tasks.Extensions package is referenced");
		}

		[TestMethod]
		public void UnresolvedHookReturnTypeIsLeftToTheCompilerTest()
		{
			var result = RunGeneratorAndGetCompilation(
				"""
				using System;
				using System.Threading.Tasks;
				using AspectGenerator;

				[Aspect(OnCatchAsync = nameof(OnCatchAsync))]
				[AttributeUsage(AttributeTargets.Method)]
				sealed class GuardAttribute : Attribute
				{
					public static Missing.ValueTask OnCatchAsync(InterceptInfo info)
					{
						return default;
					}
				}

				static class Calls
				{
					[Guard] public static async Task<int> RunAsync() { await Task.Yield(); return 1; }

					public static Task<int> Go() { return RunAsync(); }
				}
				""",
				out var compilation);

			Assert.IsTrue(compilation.GetDiagnostics().Any(static d => d.Id == "CS0246"), string.Join(Environment.NewLine, compilation.GetDiagnostics()));
			AssertNoDiagnostic(result, AspectDiagnosticID.HookInvalidReturnType);
		}

		// the unresolved-return suppression matches the generator's API types by simple name only.
		[TestMethod]
		public void UnresolvedQualifiedInterceptInfoReturnTypeTest()
		{
			var result = RunGenerator(
				[ProbeLogAspect,
				"""
				using System;
				using AspectGenerator;

				[Aspect(OnAfterCall = nameof(After))]
				[AttributeUsage(AttributeTargets.Method)]
				sealed class AAttribute : Attribute
				{
					public static Missing.InterceptInfo After(InterceptInfo info) { return default!; }
				}

				static class Calls
				{
					[A] public static string M() { return "m;"; }
					public static string Run() { return M(); }
				}
				"""]);

			AssertNoDiagnostic(result, AspectDiagnosticID.HookInvalidReturnType);
		}

		[TestMethod]
		public void LoweredHookDiagnosticKeepsTheOtherAspectsTest()
		{
			// A synchronous hook that returns a value is called as a statement, which compiles: with AG0104 lowered, both
			// aspects still run. An OnInit hook with the wrong return type cannot be assigned: only that aspect is left out.
			const string source =
				"""
				using System;
				using AspectGenerator;

				[Aspect(OnBeforeCall = nameof(Before))]
				[AttributeUsage(AttributeTargets.Method)]
				sealed class BoolHookAttribute : Attribute
				{
					public static bool Before(InterceptInfo info)
					{
						HookLog.Text.Append("bool;");
						return true;
					}
				}

				[Aspect(OnInit = nameof(Init), OnBeforeCall = nameof(Before))]
				[AttributeUsage(AttributeTargets.Method)]
				sealed class BadInitAttribute : Attribute
				{
					public static int Init(InterceptInfo info)
					{
						return 0;
					}

					public static void Before(InterceptInfo info)
					{
						HookLog.Text.Append("bad-init;");
					}
				}

				[Aspect(OnBeforeCall = nameof(Before))]
				[AttributeUsage(AttributeTargets.Method)]
				sealed class OkAttribute : Attribute
				{
					public static void Before(InterceptInfo info)
					{
						HookLog.Text.Append("ok;");
					}
				}

				static class Calls
				{
					[BoolHook, Ok]  public static string A() { return "a;"; }
					[BadInit, Ok]   public static string B() { return "b;"; }

					public static string Run()
					{
						return A() + B() + HookLog.Text;
					}
				}
				""";

			foreach (var severity in new[] { "warning", "none" })
			{
				var result = RunGeneratorAndGetCompilation(
					[HookLogSource, source],
					out var compilation,
					globalConfig: $"is_global = true\ndotnet_diagnostic.AG0104.severity = {severity}\n");

				Assert.AreEqual("a;b;bool;ok;ok;", EmitAndRun(compilation, GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs")), severity);
			}
		}
	}
}
