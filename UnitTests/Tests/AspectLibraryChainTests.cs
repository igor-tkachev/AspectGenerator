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
			Assert.AreEqual("Get;Value;3", RunChainApp(compilation, ("ChainApi", api.Image), ("ChainAspects", aspects.Image)));
		}

		[TestMethod]
		public void AspectsOfLibraryUsingForwardedApiAreAppliedTest()
		{
			// The aspect library was compiled against ChainFacade when it declared the API; ChainFacade now forwards the API to
			// ChainApi. The library references ChainFacade only.
			var api         = CompileChainLibrary("ChainApi",    ChainApiSource,    new() { ["build_property.AspectGeneratorPublicApi"] = "true" });
			var facadeV1    = CompileChainLibrary("ChainFacade", ChainApiSource.Replace("ChainApi", "ChainFacade", StringComparison.Ordinal), new() { ["build_property.AspectGeneratorPublicApi"] = "true" });
			var aspects     = CompileChainLibrary("ChainAspects", ChainAspectsSource, new(), facadeV1.Reference);
			var facadeV2    = CompileChainLibrary("ChainFacade", GetForwardersSource(api.Reference), new() { ["build_property.AspectGeneratorGenerateApi"] = "false" }, api.Reference);

			var result       = RunChainApp([ChainAppSource], new(), out var compilation, api.Reference, facadeV2.Reference, aspects.Reference);
			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");

			StringAssert.Contains(interceptors, "Service.Get()");
			StringAssert.Contains(interceptors, "Own.Value()");

			var errors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();

			Assert.AreEqual(0, errors.Length, string.Join(Environment.NewLine, errors.Select(static e => e.ToString())));
			Assert.AreEqual("Get;Value;3", RunChainApp(compilation, ("ChainApi", api.Image), ("ChainFacade", facadeV2.Image), ("ChainAspects", aspects.Image)));

			// [assembly: TypeForwardedTo] for every public type of the API.
			static string GetForwardersSource(MetadataReference apiReference)
			{
				var probe = CSharpCompilation.Create("ForwarderProbe", references: GetMetadataReferences().Append(apiReference));
				var api   = probe.SourceModule.ReferencedAssemblySymbols.Single(static a => a.Identity.Name == "ChainApi");
				var types = api.GlobalNamespace.GetNamespaceMembers().Single(static n => n.Name == "AspectGenerator").GetTypeMembers()
					.Where(static t => t.DeclaredAccessibility == Accessibility.Public)
					.Select(static t => t.Arity == 0
						? $"[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(global::AspectGenerator.{t.Name}))]"
						: $"[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(global::AspectGenerator.{t.Name}<{new string(',', t.Arity - 1)}>))]");

				return string.Join(Environment.NewLine, types);
			}
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

		static string RunChainApp(Compilation compilation, params (string Name, byte[] Image)[] libraries)
		{
			using var stream = new MemoryStream();

			var emit = compilation.Emit(stream);

			Assert.IsTrue(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));

			var context = new ChainContext(libraries.ToDictionary(static l => l.Name, static l => l.Image, StringComparer.Ordinal));

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

		sealed class ChainContext(Dictionary<string,byte[]> images) : AssemblyLoadContext("ChainTests", isCollectible: true)
		{
			readonly Dictionary<string,Assembly> _loaded = new(StringComparer.Ordinal);

			protected override Assembly? Load(AssemblyName name)
			{
				if (name.Name is not {} simpleName || !images.TryGetValue(simpleName, out var image))
					return null;

				if (!_loaded.TryGetValue(simpleName, out var assembly))
				{
					assembly = LoadFromStream(new MemoryStream(image));
					_loaded.Add(simpleName, assembly);
				}

				return assembly;
			}
		}
	}
}
