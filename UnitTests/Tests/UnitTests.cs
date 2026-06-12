using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	[TestClass]
	public class UnitTests
	{
		[TestMethod]
		public void GeneratedSourceBaselineTest()
		{
			var baseDirectory  = AppContext.BaseDirectory;
			var repositoryRoot = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", ".."));
			var baselineRoot   = Path.Combine(repositoryRoot, "Baselines", "GeneratedFiles");
			var baselineFiles  = Directory.GetFiles(baselineRoot, "*.g.cs", SearchOption.AllDirectories);

			foreach (var baselineFile in baselineFiles)
			{
				var relativeProjectPath = Path.GetRelativePath(baselineRoot, baselineFile);
				var generatedFile       = Path.Combine(
					repositoryRoot,
					Path.GetDirectoryName(relativeProjectPath)!,
					"obj",
					"GeneratedFiles",
					"AspectGenerator",
					"AspectGenerator.AspectSourceGenerator",
					Path.GetFileName(relativeProjectPath));

				Assert.IsTrue(File.Exists(generatedFile), $"Generated file does not exist. Build the solution before running baseline tests: {generatedFile}");

				var expected = Normalize(File.ReadAllText(baselineFile));
				var actual   = Normalize(File.ReadAllText(generatedFile));

				Assert.AreEqual(expected, actual, relativeProjectPath);
			}

			static string Normalize(string text)
			{
				return Regex
					.Replace(
						text.Replace("\r\n", "\n").Replace('\r', '\n'),
						@"InterceptsLocationAttribute\(1, ""[^""]+""\)",
						@"InterceptsLocationAttribute(1, ""<location>"")");
			}
		}

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
					["build_property.AspectGeneratorGenerateInterceptors"] = "false",
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
					["build_property.AspectGeneratorGenerateInterceptors"] = "true",
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
					["build_property.AspectGeneratorGenerateInterceptors"] = "false",
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

		[Aspects.TestAspect]
		public static string StaticMethod()
		{
			return "hi";
		}

		[Aspects.TestAspect]
		public static string StaticMethod(string name)
		{
			return $"hi, {name}";
		}

		[TestMethod]
		public void StaticMethodTest()
		{
			var str = StaticMethod() + ".." + StaticMethod() + "++" +
			          StaticMethod("John");

			Assert.AreEqual("hi__I__..hi__I__++hi, John__I__", str);

			Console.WriteLine(str);
		}

		[TestMethod]
		public void GenericMethodTest()
		{
			var now = DateTime.Now;
			var str = TestCode.GenericMethod(1) + TestCode.GenericMethod(DateTime.Today) + TestCode.GenericMethod(now);

			Assert.AreEqual($"value is 1__X____I__value is {DateTime.Today}__X____I__value is {now}__X____I__", str);

			Console.WriteLine(str);
		}

		[Aspects.TestAspect, Aspects.TestAspect2]
		public static void ReturnVoidMethod()
		{
		}

		[TestMethod]
		public void ReturnVoidMethodTest()
		{
			ReturnVoidMethod();
		}

		[Aspects.EmptyAspect]
		public static int EmptyMethod()
		{
			return default;
		}

		[TestMethod]
		public void EmptyAspectTest()
		{
			EmptyMethod();
		}

		[Aspects.InitAspect]
		public static int InitMethod()
		{
			return default;
		}

		[TestMethod]
		public void InitAspectTest()
		{
			Aspects.InitAspectAttribute.CallCount = 0;

			InitMethod();

			Assert.AreEqual(1, Aspects.InitAspectAttribute.CallCount);
		}

		[Aspects.IgnoreCatch]
		public static int IgnoreCatchMethod()
		{
			throw new ();
		}

		[TestMethod]
		public void IgnoreCatchTest()
		{
			IgnoreCatchMethod();
		}

		[Aspects.Finally]
		public static int FinallyMethod()
		{
			return 1;
		}

		[TestMethod]
		public void FinallyTest()
		{
			var n = FinallyMethod();

			Assert.AreEqual(2, n);
		}

		[Aspects.Finally]
		public int MemberMethod(int n)
		{
			return n;
		}

		[TestMethod]
		public void MemberMethodTest()
		{
			var n = MemberMethod(1);

			Assert.AreEqual(2, n);
		}

		[TestMethod]
		public void ExtensionTest()
		{
			var str = this.ExtensionMethod(1);

			Assert.AreEqual("1__I__", str);

			Console.WriteLine(str);
		}

		[TestMethod]
		public void ClassExtensionTest()
		{
			var str = new TestClassExtension().ExtensionMethod(1);

			Assert.AreEqual("1__I__", str);

			Console.WriteLine(str);
		}

		[TestMethod]
		public void StructExtensionTest()
		{
			var str = new TestStructExtension().ExtensionMethod(2);

			Assert.AreEqual("2__I__", str);

			Console.WriteLine(str);
		}

		[Aspects.AllEvents]
		internal void AllEventsMethod()
		{
		}

		[TestMethod]
		public void AllEventsTest()
		{
			Aspects.AllEventsAttribute.ClearCounters();

			AllEventsMethod();

			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnInitCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnUsingCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnUsingCounterAsync);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnBeforeCallCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnBeforeCallCounterAsync);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnAfterCallCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnAfterCallCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnCatchCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnCatchCounterAsync);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnFinallyCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnFinallyCounterAsync);
		}

		[Aspects.AllEvents]
		internal async Task AllEventsMethodAsync()
		{
			await Task.FromResult(0);
		}

		[TestMethod]
		public async Task AllEventsTestAsync()
		{
			Aspects.AllEventsAttribute.ClearCounters();

			await AllEventsMethodAsync();

			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnInitCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnUsingCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnUsingCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnBeforeCallCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnBeforeCallCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnAfterCallCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnAfterCallCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnCatchCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnCatchCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnFinallyCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnFinallyCounterAsync);
		}

		[Aspects.AllEvents]
		internal async Task<int> AllEventsMethodAsync2()
		{
			return await Task.FromResult(4);
		}

		[TestMethod]
		public async Task AllEventsTestAsync2()
		{
			Aspects.AllEventsAttribute.ClearCounters();

			var r = await AllEventsMethodAsync2();

			Assert.AreEqual(4, r);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnInitCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnUsingCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnUsingCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnBeforeCallCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnBeforeCallCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnAfterCallCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnAfterCallCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnCatchCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnCatchCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnFinallyCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnFinallyCounterAsync);
		}

		[Aspects.Args(Arg1 = "1", Arg2 = 2, Arg3 = [ 1, 2, 3 ])]
		[Aspects.Args(Arg3 = [2])]
		[Aspects.Args(Arg3 = [])]
		[Aspects.Args(Arg1 = "xyz")]
		[Aspects.Args(Arg4 = 'w')]
		[Aspects.Args(Arg5 = null)]
		[Aspects.Args(Arg5 = typeof(int))]
		[Aspects.Args(Arg5 = typeof(List<>))]
		[Aspects.Args(Arg5 = typeof(List<DateTime>))]
		[Aspects.Args(Arg5 = 1.10d)]
		internal string ArgsMethod()
		{
			return "0";
		}

		[TestMethod]
		public void ArgsTest()
		{
			var args = ArgsMethod();

			Assert.AreEqual("02", args);

			Console.WriteLine(args);
		}

		[Aspects.LiteralArgs(
			Text = "quote\" slash\\ newline\n",
			Character = '\'',
			Number = 1.25d,
			Single = 3.5f,
			Kind = Aspects.LiteralKind.Second,
			Values = [ "a\"b", "c\\d", "e\nf" ])]
		internal string LiteralArgsMethod()
		{
			return "literal-failed";
		}

		[TestMethod]
		public void LiteralArgsTest()
		{
			var args = LiteralArgsMethod();

			Assert.AreEqual("literal-ok", args);
		}

		[Aspects.Using]
		internal string UsingMethod()
		{
			return "0";
		}

		[TestMethod]
		public void UsingTest()
		{
			var args = UsingMethod();
		}

		[Aspects.Using]
		internal Task<string> UsingMethodAsync()
		{
			return Task.FromResult("0");
		}

		[TestMethod]
		public async Task UsingAsyncTest()
		{
			var args = await UsingMethodAsync();
		}

		[Aspects.Ordered(Order = 2, Value = "1")]
		[Aspects.Ordered(Order = 1, Value = "2")]
		internal static string OrderedMethod()
		{
			return "0";
		}

		[Aspects.Ordered(Order = 2, Value = "2")]
		[Aspects.Ordered(Order = 3, Value = "3")]
		internal static int OrderedMethod2()
		{
			return 1;
		}

		[TestMethod]
		public void OrderedTest()
		{
			var s1 = OrderedMethod();

			Assert.AreEqual("012", s1);

			var i2 = OrderedMethod2();

			Assert.AreEqual(132, i2);
		}

		[Aspects.Arguments]
		internal static string ArgumentsMethod()
		{
			return "_";
		}


		[TestMethod]
		public void ArgumentsTest()
		{
			var s = ArgumentsMethod();
			Assert.AreEqual("_0", s);
		}

		[Aspects.Arguments]
		internal static string ArgumentsMethod(string s, int i)
		{
			return s + i;
		}

		[TestMethod]
		public void Arguments2Test()
		{
			var s = ArgumentsMethod("_", 1);
			Assert.AreEqual("_12", s);
		}

		[Aspects.Arguments]
		internal static string ArgumentsRefMethod(string s, int i, ref bool b)
		{
			return s + i + b;
		}

		[TestMethod]
		public void ArgumentsRefTest()
		{
			var b = true;
			var s = ArgumentsRefMethod("_", 1, ref b);

			Assert.AreEqual("_1True3", s);
		}

		[Aspects.Arguments]
		internal static string ArgumentsInMethod(string s, int i, in bool b, ref int? _)
		{
			return s + i + b;
		}

		[TestMethod]
		public void ArgumentsInTest()
		{
			var  b = true;
			int? n = 1;
			var  s = ArgumentsInMethod("_", 1, in b, ref n);

			Assert.AreEqual("_1True4", s);
		}

		[Aspects.Arguments]
		internal static string ArgumentsInMethod(string s, int i, bool b, ref int? _)
		{
			return s + i + b;
		}

		[TestMethod]
		public void ArgumentsIn1Test()
		{
			var  b = true;
			int? n = 1;
			var  s = ArgumentsInMethod("_", 1, b, ref n);

			Assert.AreEqual("_1True4", s);
		}

		[Aspects.Arguments]
		internal static string ArgumentsOutMethod(out string s)
		{
			s = "1";
			return "_" + s;
		}

		[TestMethod]
		public void ArgumentsOutTest()
		{
			var s1 = ArgumentsOutMethod(out _);

			Assert.AreEqual("_11", s1);
		}

		[TestMethod]
		public void MultiProjectMainTest()
		{
			var s = ClassLibrary.TestClass.MainMethod("Main");

			Console.WriteLine(s);
			Assert.AreEqual("Main MainMethod TestMethod + CrossProject aspect.", s);
		}

		[TestMethod]
		public void MultiProjectTestTest()
		{
			var s = ClassLibrary.TestClass.TestMethod("Test");

			Console.WriteLine(s);
			Assert.AreEqual("Test TestMethod + CrossProject aspect.", s);
		}

		public static string InterceptedMethod(string str)
		{
			return str + " InterceptedMethod";
		}

		[TestMethod]
		public void InterceptedMethodTest()
		{
			var s = InterceptedMethod("Intercepted");

			Console.WriteLine(s);
			Assert.AreEqual("Intercepted InterceptedMethod + InterceptMethods aspect.", s);
		}

		public static T InterceptedGenericMethod<T>(T p)
		{
			return p;
		}

		[TestMethod]
		public void InterceptedGenericMethodTest()
		{
			var s = InterceptedGenericMethod("Intercepted");

			Console.WriteLine(s);
			Assert.AreEqual("Intercepted + InterceptMethods aspect.", s);

			var i = InterceptedGenericMethod(10);

			Console.WriteLine(i);

			Assert.AreEqual(10, i);
		}

		[TestMethod]
		public void SubstringInterceptTest()
		{
			var s = "test string".Substring(5);

			Console.WriteLine(s);
			Assert.AreEqual("string + InterceptMethods aspect.", s);
		}

		[OnCall]
		public static int OnCallTestMethod(int n)
		{
			return n;
		}

		[TestMethod]
		public void OnCallTest()
		{
			var n = OnCallTestMethod(2);

			Console.WriteLine(n);
			Assert.AreEqual(4, n);
		}

		[TestMethod]
		public void OnCallMemberTest()
		{
			var obj = new OnCallObject();
			var n   = obj.OnCall(2);

			Console.WriteLine(n);
			Assert.AreEqual(6, n);
		}
	}
}
