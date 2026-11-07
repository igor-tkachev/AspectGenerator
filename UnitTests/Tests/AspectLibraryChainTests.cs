using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// An aspect library that declares no aspect API of its own: it uses the public <c>AspectAttribute</c> of another
	/// assembly. Its aspects must apply in an application that references both, as the aspects of a library with its own
	/// API do.
	/// </summary>
	public partial class CompilerTests
	{
		const string ChainApiSource =
			"""
			namespace ChainApi
			{
				public static class Marker
				{
				}
			}
			""";

		const string ChainAspectsSource =
			"""
			using System;
			using AspectGenerator;

			namespace ChainAspects
			{
				public static class ChainLog
				{
					public static readonly System.Text.StringBuilder Text = new();
				}

				[Aspect(OnBeforeCall = nameof(Before))]
				[AttributeUsage(AttributeTargets.Method)]
				public sealed class ChainLogAttribute : Attribute
				{
					public static void Before(InterceptInfo info)
					{
						ChainLog.Text.Append(info.MemberInfo.Name).Append(';');
					}
				}

				[Aspect(OnBeforeCall = nameof(Before), DefaultTargetFilter = "unknownkey: value")]
				[AttributeUsage(AttributeTargets.Class)]
				public sealed class ChainTypeLogAttribute : Attribute
				{
					public static void Before(InterceptInfo info)
					{
						ChainLog.Text.Append("type:").Append(info.MemberInfo.Name).Append(';');
					}
				}

				public static class Service
				{
					[ChainLog]
					public static int Get()
					{
						return 1;
					}
				}
			}
			""";

		const string ChainAppSource =
			"""
			using ChainAspects;

			static class Own
			{
				[ChainLog]
				public static int Value()
				{
					return 2;
				}
			}

			static class Calls
			{
				public static string Run()
				{
					var sum = Service.Get() + Own.Value();

					return ChainLog.Text.ToString() + sum;
				}
			}
			""";

		const string ChainAppTypeFilterSource =
			"""
			using ChainAspects;

			[ChainTypeLog]
			static class Filtered
			{
				public static int Value()
				{
					return 3;
				}
			}
			""";

		[TestMethod]
		public void AspectsOfLibraryUsingAnotherAssemblysApiAreAppliedTest()
		{
			var api    = CompileChainLibrary("ChainApi",    ChainApiSource,    new() { ["build_property.AspectGeneratorPublicApi"] = "true" });
			var aspects = CompileChainLibrary("ChainAspects", ChainAspectsSource, new(), api.Reference);

			var result       = RunChainApp([ChainAppSource], new(), out var compilation, api.Reference, aspects.Reference);
			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");

			StringAssert.Contains(interceptors, "Service.Get()");
			StringAssert.Contains(interceptors, "Own.Value()");

			var errors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();

			Assert.AreEqual(0, errors.Length, string.Join(Environment.NewLine, errors.Select(static e => e.ToString())));
			Assert.AreEqual("Get;Value;3", RunChainApp(compilation, api.Image, aspects.Image));
		}

		[TestMethod]
		public void DefaultTargetFilterOfLibraryUsingAnotherAssemblysApiIsCheckedTest()
		{
			var api     = CompileChainLibrary("ChainApi",    ChainApiSource,    new() { ["build_property.AspectGeneratorPublicApi"] = "true" });
			var aspects = CompileChainLibrary("ChainAspects", ChainAspectsSource, new(), api.Reference);

			var build      = RunChainApp([ChainAppTypeFilterSource], new(), out _, api.Reference, aspects.Reference);
			var designTime = RunChainApp([ChainAppTypeFilterSource], new() { ["build_property.DesignTimeBuild"] = "true" }, out _, api.Reference, aspects.Reference);

			Assert.IsTrue(
				build.Diagnostics.Any(static d => d.Id == AspectDiagnosticID.UnknownAspectFilterConditionKey),
				$"Expected {AspectDiagnosticID.UnknownAspectFilterConditionKey} in a build. Actual: {string.Join(", ", build.Diagnostics.Select(static d => d.Id))}");
			Assert.IsTrue(
				designTime.Diagnostics.Any(static d => d.Id == AspectDiagnosticID.UnknownAspectFilterConditionKey),
				$"Expected {AspectDiagnosticID.UnknownAspectFilterConditionKey} in a design-time build. Actual: {string.Join(", ", designTime.Diagnostics.Select(static d => d.Id))}");
		}

		static (MetadataReference Reference, byte[] Image) CompileChainLibrary(string name, string source, Dictionary<string,string> properties, params MetadataReference[] references)
		{
			var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview).WithFeatures([new("InterceptorsNamespaces", "AspectGenerator")]);
			var compilation  = CSharpCompilation.Create(
				name,
				[CSharpSyntaxTree.ParseText(source, parseOptions, $"{name}.cs")],
				GetMetadataReferences().Concat(references),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

			var effectiveProperties = new Dictionary<string,string>(properties);

			effectiveProperties.TryAdd("build_property.InterceptorsNamespaces",       "AspectGenerator");
			effectiveProperties.TryAdd("build_property.AspectGeneratorPropsImported", "true");

			GeneratorDriver driver = CSharpGeneratorDriver.Create(
				[new AspectSourceGenerator().AsSourceGenerator()],
				parseOptions: parseOptions,
				optionsProvider: new TestAnalyzerConfigOptionsProvider(effectiveProperties));

			driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

			using var stream = new MemoryStream();

			var emit = output.Emit(stream);

			Assert.IsTrue(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));

			var image = stream.ToArray();

			return (MetadataReference.CreateFromImage(image), image);
		}

		static GeneratorDriverRunResult RunChainApp(string[] sources, Dictionary<string,string> properties, out Compilation outputCompilation, params MetadataReference[] references)
		{
			var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview).WithFeatures([new("InterceptorsNamespaces", "AspectGenerator")]);
			var compilation  = CSharpCompilation.Create(
				"ChainApp",
				sources.Select((source, index) => CSharpSyntaxTree.ParseText(source, parseOptions, $"ChainApp{index}.cs")),
				GetMetadataReferences().Concat(references),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

			var effectiveProperties = new Dictionary<string,string>(properties);

			effectiveProperties.TryAdd("build_property.InterceptorsNamespaces",       "AspectGenerator");
			effectiveProperties.TryAdd("build_property.AspectGeneratorPropsImported", "true");

			GeneratorDriver driver = CSharpGeneratorDriver.Create(
				[new AspectSourceGenerator().AsSourceGenerator()],
				parseOptions: parseOptions,
				optionsProvider: new TestAnalyzerConfigOptionsProvider(effectiveProperties));

			using (PretendEditorFor(effectiveProperties))
				driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out outputCompilation, out _);

			return driver.GetRunResult();
		}

		static string RunChainApp(Compilation compilation, byte[] apiImage, byte[] aspectsImage)
		{
			using var stream = new MemoryStream();

			var emit = compilation.Emit(stream);

			Assert.IsTrue(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));

			var context = new ChainContext(apiImage, aspectsImage);

			try
			{
				stream.Position = 0;

				var assembly = context.LoadFromStream(stream);
				var run      = assembly.GetType("Calls")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

				return (string)run.Invoke(null, null)!;
			}
			finally
			{
				context.Unload();
			}
		}

		sealed class ChainContext(byte[] apiImage, byte[] aspectsImage) : AssemblyLoadContext("ChainTests", isCollectible: true)
		{
			Assembly? _api;
			Assembly? _aspects;

			protected override Assembly? Load(AssemblyName name)
			{
				return name.Name switch
				{
					"ChainApi"     => _api     ??= LoadFromStream(new MemoryStream(apiImage)),
					"ChainAspects" => _aspects ??= LoadFromStream(new MemoryStream(aspectsImage)),
					_              => null
				};
			}
		}
	}
}
