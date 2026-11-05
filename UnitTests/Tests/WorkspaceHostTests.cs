using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// The generator run by a real Roslyn workspace, the way <c>AdhocWorkspace</c> and <c>MSBuildWorkspace</c> tools and the
	/// <c>Microsoft.CodeAnalysis.Testing</c> harness run it: the workspace's own options provider, generated documents from
	/// <c>Project.GetSourceGeneratedDocumentsAsync</c>. A workspace is not an editor, so it builds, whatever a design-time
	/// evaluation of the project put into the properties.
	/// </summary>
	public partial class CompilerTests
	{
		[TestMethod]
		public async Task WorkspaceHostIsUnknownAndEmitsInterceptorsTest()
		{
			using var workspace = new AdhocWorkspace();

			var project  = CreateWorkspaceProject(workspace, globalConfig: null);
			var provider = project.AnalyzerOptions.AnalyzerConfigOptionsProvider;

			Assert.AreEqual("Microsoft.CodeAnalysis.Workspaces", provider.GetType().Assembly.GetName().Name);
			Assert.AreEqual(GeneratorHostKind.Unknown, GeneratorHost.Detect(provider));

			var generated = await project.GetSourceGeneratedDocumentsAsync();

			CollectionAssert.Contains(generated.Select(static d => d.HintName).ToList(), "Interceptors.g.cs");
		}

		/// <summary>
		/// An <c>MSBuildWorkspace</c> evaluates the project as a design-time build, so its editorconfig says
		/// <c>DesignTimeBuild=true</c>; the tool then emits and runs the compilation, which must carry the interceptors.
		/// </summary>
		[TestMethod]
		public async Task WorkspaceHostEmitsInterceptorsUnderDesignTimePropertiesTest()
		{
			using var workspace = new AdhocWorkspace();

			var project = CreateWorkspaceProject(workspace, DesignTimeGlobalConfig);

			Assert.AreEqual("true", GetGlobalOption(project, "build_property.DesignTimeBuild"));

			var generated = await project.GetSourceGeneratedDocumentsAsync();

			CollectionAssert.Contains(generated.Select(static d => d.HintName).ToList(), "Interceptors.g.cs");
		}

		/// <summary>
		/// The same workspace and properties in a known editor are its live analysis; after a build the editor builds.
		/// </summary>
		[TestMethod]
		public async Task EditorWorkspaceFollowsThePropertiesTest()
		{
			GeneratorHost.TestHost = GeneratorHostKind.Editor;

			try
			{
				using var workspace = new AdhocWorkspace();

				var liveAnalysis = await CreateWorkspaceProject(workspace, DesignTimeGlobalConfig).GetSourceGeneratedDocumentsAsync();

				CollectionAssert.DoesNotContain(liveAnalysis.Select(static d => d.HintName).ToList(), "Interceptors.g.cs");
				CollectionAssert.Contains      (liveAnalysis.Select(static d => d.HintName).ToList(), "AspectAttribute.g.cs");

				var afterBuild = await CreateWorkspaceProject(workspace, AfterBuildGlobalConfig).GetSourceGeneratedDocumentsAsync();

				CollectionAssert.Contains(afterBuild.Select(static d => d.HintName).ToList(), "Interceptors.g.cs");
			}
			finally
			{
				GeneratorHost.TestHost = null;
			}
		}

		const string DesignTimeGlobalConfig =
			"""
			is_global = true
			build_property.DesignTimeBuild = true
			build_property.BuildingProject = false
			""";

		const string AfterBuildGlobalConfig =
			"""
			is_global = true
			build_property.DesignTimeBuild =
			build_property.BuildingProject = true
			""";

		static Project CreateWorkspaceProject(AdhocWorkspace workspace, string? globalConfig)
		{
			var parseOptions = CSharpParseOptions.Default
				.WithLanguageVersion(LanguageVersion.Preview)
				.WithFeatures([new("InterceptorsNamespaces", "AspectGenerator")]);

			var generatorAssembly = typeof(AspectSourceGenerator).Assembly;
			var projectInfo       = ProjectInfo.Create(
				ProjectId.CreateNewId(),
				VersionStamp.Default,
				"AspectGenerator.Tests.Workspace",
				"AspectGenerator.Tests.Workspace",
				LanguageNames.CSharp,
				compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
				parseOptions:       parseOptions,
				metadataReferences: GetMetadataReferences(),
				analyzerReferences: [new AnalyzerFileReference(generatorAssembly.Location, new LoadedAssemblyLoader(generatorAssembly))]);

			var project = workspace.AddProject(projectInfo);

			project = project.AddDocument("GeneratorDriverTest.cs", SourceText.From(GenerationOptionsSource)).Project;
			project = project.AddAnalyzerConfigDocument(
				".globalconfig",
				SourceText.From(
					$"""
					{globalConfig ?? "is_global = true"}
					build_property.InterceptorsNamespaces = AspectGenerator
					build_property.AspectGeneratorPropsImported = true
					"""),
				filePath: "/.globalconfig").Project;

			return project;
		}

		static string? GetGlobalOption(Project project, string key)
		{
			return project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(key, out var value) ? value : null;
		}

		sealed class LoadedAssemblyLoader : IAnalyzerAssemblyLoader
		{
			readonly Assembly _assembly;

			public LoadedAssemblyLoader(Assembly assembly)
			{
				_assembly = assembly;
			}

			public void AddDependencyLocation(string fullPath)
			{
			}

			public Assembly LoadFromPath(string fullPath)
			{
				return _assembly;
			}
		}
	}
}
