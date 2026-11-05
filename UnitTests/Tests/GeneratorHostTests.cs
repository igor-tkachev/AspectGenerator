using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// The host decides the mode before the MSBuild properties do: the compiler and every unknown host build, and only a
	/// known editor follows <c>DesignTimeBuild</c> and <c>BuildingProject</c>. The editor reads those properties from the
	/// editorconfig that every real build rewrites with a build's values, and then builds on purpose, for Hot Reload.
	/// </summary>
	public partial class CompilerTests
	{
		const string WorkspacesAssemblyName = "Microsoft.CodeAnalysis.Workspaces";

		[TestMethod]
		public void GeneratorHostIsClassifiedByEntryAssemblyTest()
		{
			Assert.AreEqual(GeneratorHostKind.Compiler, GeneratorHost.Classify("csc",          "Microsoft.CodeAnalysis"));
			Assert.AreEqual(GeneratorHostKind.Compiler, GeneratorHost.Classify("VBCSCompiler", "Microsoft.CodeAnalysis"));
			Assert.AreEqual(GeneratorHostKind.Compiler, GeneratorHost.Classify("CSC",          null));
			// The compiler never loads the workspace assembly; if it ever did, it is still the compiler.
			Assert.AreEqual(GeneratorHostKind.Compiler, GeneratorHost.Classify("csc",          WorkspacesAssemblyName));

			// Measured: VS 2022 17.14 runs the generator in ServiceHub.RoslynCodeAnalysisService.exe (entry assembly
			// ServiceHub.Host.dotnet.x64), VS 2026 18.11 in DevHub.exe. devenv.exe in process is a native host without an
			// entry assembly, told from an unknown native host by the workspace's options provider.
			Assert.AreEqual(GeneratorHostKind.Editor,   GeneratorHost.Classify("ServiceHub.Host.dotnet.x64",            WorkspacesAssemblyName));
			Assert.AreEqual(GeneratorHostKind.Editor,   GeneratorHost.Classify("DevHub",                                WorkspacesAssemblyName));
			Assert.AreEqual(GeneratorHostKind.Editor,   GeneratorHost.Classify("Microsoft.CodeAnalysis.LanguageServer", WorkspacesAssemblyName));
			Assert.AreEqual(GeneratorHostKind.Editor,   GeneratorHost.Classify(null,                                    WorkspacesAssemblyName));

			// A workspace alone is not an editor: test drivers, dotnet watch and workspace tools may emit and run the result.
			Assert.AreEqual(GeneratorHostKind.Unknown,  GeneratorHost.Classify("testhost",     WorkspacesAssemblyName));
			Assert.AreEqual(GeneratorHostKind.Unknown,  GeneratorHost.Classify("dotnet-watch", WorkspacesAssemblyName));
			Assert.AreEqual(GeneratorHostKind.Unknown,  GeneratorHost.Classify("Ws",           WorkspacesAssemblyName));
			Assert.AreEqual(GeneratorHostKind.Unknown,  GeneratorHost.Classify("testhost",     "AspectGenerator.Tests"));
			Assert.AreEqual(GeneratorHostKind.Unknown,  GeneratorHost.Classify("dotnet",       "Microsoft.CodeAnalysis"));
			Assert.AreEqual(GeneratorHostKind.Unknown,  GeneratorHost.Classify(null,           null));
			Assert.AreEqual(GeneratorHostKind.Unknown,  GeneratorHost.Classify(null,           "AspectGenerator.Tests"));
		}

		[TestMethod]
		public void TestDriverIsAnUnknownHostTest()
		{
			var provider = new TestAnalyzerConfigOptionsProvider(new());

			Assert.IsNull(GeneratorHost.TestHost);
			Assert.AreEqual(GeneratorHostKind.Unknown, GeneratorHost.Detect(provider));
			Assert.AreEqual(GeneratorHostKind.Unknown, AspectOptionsResolver.GetMSBuildOptions(provider).Host);
			StringAssert.StartsWith(GeneratorHost.Describe(GeneratorHostKind.Unknown), "unknown (");
		}

		[TestMethod]
		public void EditorFollowsThePropertiesTest()
		{
			// The editor's live analysis before any build: no runnable code comes out of it.
			var liveAnalysis = ResolveFor(GeneratorHostKind.Editor, designTimeBuild: true, buildingProject: null);

			Assert.IsTrue (liveAnalysis.DesignTimeBuild);
			Assert.IsFalse(liveAnalysis.EmitInterceptors);

			Assert.IsTrue(ResolveFor(GeneratorHostKind.Editor, designTimeBuild: true, buildingProject: false).DesignTimeBuild);

			// The editorconfig after a real build: BuildingProject=true, DesignTimeBuild empty. The editor builds, because
			// Hot Reload and Edit and Continue emit their delta from the editor's compilation.
			var afterBuild = ResolveFor(GeneratorHostKind.Editor, designTimeBuild: null, buildingProject: true);

			Assert.IsFalse(afterBuild.DesignTimeBuild);
			Assert.IsTrue (afterBuild.EmitInterceptors);

			Assert.IsFalse(ResolveFor(GeneratorHostKind.Editor, designTimeBuild: true,  buildingProject: true).DesignTimeBuild);
			Assert.IsFalse(ResolveFor(GeneratorHostKind.Editor, designTimeBuild: null,  buildingProject: null).DesignTimeBuild);
			Assert.IsFalse(ResolveFor(GeneratorHostKind.Editor, designTimeBuild: false, buildingProject: null).DesignTimeBuild);
		}

		[TestMethod]
		public void CompilerAndUnknownHostsBuildWhateverThePropertiesSayTest()
		{
			foreach (var host in new[] { GeneratorHostKind.Compiler, GeneratorHostKind.Unknown })
			{
				foreach (var (designTimeBuild, buildingProject) in new (bool?, bool?)[] { (null, null), (null, true), (true, null), (true, false), (true, true), (false, null) })
				{
					var options = ResolveFor(host, designTimeBuild, buildingProject);

					Assert.IsFalse(options.DesignTimeBuild,  $"{host}: DesignTimeBuild={designTimeBuild}, BuildingProject={buildingProject}");
					Assert.IsTrue (options.EmitInterceptors, $"{host}: DesignTimeBuild={designTimeBuild}, BuildingProject={buildingProject}");
				}
			}
		}

		/// <summary>
		/// A generator test harness or a workspace tool hands the generator a design-time build's properties (an
		/// <c>MSBuildWorkspace</c> evaluates the project that way) and then emits and runs the compilation.
		/// </summary>
		[TestMethod]
		public void UnknownHostEmitsInterceptorsUnderDesignTimePropertiesTest()
		{
			var properties = new Dictionary<string,string>
			{
				["build_property.DesignTimeBuild"] = "true",
				["build_property.BuildingProject"] = "false",
			};

			GeneratorHost.TestHost = GeneratorHostKind.Unknown;

			try
			{
				AssertGenerated(RunGenerator(GenerationOptionsSource, properties), "Interceptors.g.cs");
			}
			finally
			{
				GeneratorHost.TestHost = null;
			}

			// The same properties in an editor are its live analysis.
			AssertNotGenerated(RunGenerator(GenerationOptionsSource, properties), "Interceptors.g.cs");

			// And the editor after a build emits them again.
			AssertGenerated(RunGenerator(GenerationOptionsSource, new() { ["build_property.BuildingProject"] = "true" }), "Interceptors.g.cs");
		}

		[TestMethod]
		public void BuildReportIsNotWrittenByAnEditorTest()
		{
			var reportFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "report.md");

			try
			{
				// The editor after a real build: it builds, but its report must not overwrite the compiler's.
				AspectBuildReport.Write(CreateAnalysis(GeneratorHostKind.Editor, designTimeBuild: null, buildingProject: true, reportFile));

				Assert.IsFalse(File.Exists(reportFile), $"An editor must not write the build report: {reportFile}");

				AspectBuildReport.Write(CreateAnalysis(GeneratorHostKind.Editor, designTimeBuild: true, buildingProject: null, reportFile));

				Assert.IsFalse(File.Exists(reportFile), $"An editor must not write the build report: {reportFile}");

				AspectBuildReport.Write(CreateAnalysis(GeneratorHostKind.Compiler, designTimeBuild: true, buildingProject: null, reportFile));

				Assert.IsTrue(File.Exists(reportFile), $"The compiler must write the build report: {reportFile}");

				var report = File.ReadAllText(reportFile);

				StringAssert.Contains(report, "| Generator host | compiler (");
				StringAssert.Contains(report, "| Interceptor generation | enabled |");

				File.Delete(reportFile);

				AspectBuildReport.Write(CreateAnalysis(GeneratorHostKind.Unknown, designTimeBuild: true, buildingProject: null, reportFile));

				Assert.IsTrue(File.Exists(reportFile), $"An unknown host builds and writes the build report: {reportFile}");
				StringAssert.Contains(File.ReadAllText(reportFile), "| Generator host | unknown (");
			}
			finally
			{
				DeleteReportDirectory(reportFile);
			}
		}

		[TestMethod]
		public void BuildReportNamesTheTestDriverAsAnUnknownHostTest()
		{
			var reportFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "report.md");

			try
			{
				RunGenerator(
					TraceDirectSource,
					new()
					{
						[$"build_property.AspectGenerator{AspectOptionName.ReportFile}"] = reportFile,
					});

				StringAssert.Contains(File.ReadAllText(reportFile), "| Generator host | unknown (");
			}
			finally
			{
				DeleteReportDirectory(reportFile);
			}
		}

		/// <summary>
		/// The analyzer sees the generator's output in the editor after a build; it must skip exactly that file, so that it
		/// selects what the generator selected, and nothing of the user's.
		/// </summary>
		[TestMethod]
		public void AnalyzerSkipsTheGeneratedInterceptorsTreeOnlyTest()
		{
			var result       = RunGenerator(GenerationOptionsSource);
			var interceptors = result.GeneratedTrees.Single(t => t.FilePath.EndsWith("Interceptors.g.cs", StringComparison.Ordinal));

			Assert.IsTrue(AspectSymbols.IsGeneratedInterceptorsTree(interceptors), interceptors.FilePath);

			foreach (var tree in result.GeneratedTrees.Where(t => t != interceptors))
				Assert.IsFalse(AspectSymbols.IsGeneratedInterceptorsTree(tree), tree.FilePath);

			Assert.IsFalse(AspectSymbols.IsGeneratedInterceptorsTree(CSharpSyntaxTree.ParseText("", path: "Interceptors.g.cs")));
			Assert.IsFalse(AspectSymbols.IsGeneratedInterceptorsTree(CSharpSyntaxTree.ParseText("", path: "/src/AspectGenerator.AspectSourceGenerator/Program.cs")));
		}

		static GeneratorExecutionOptions ResolveFor(GeneratorHostKind host, bool? designTimeBuild, bool? buildingProject, string? reportFile = null)
		{
			var options = new Options
			{
				Host            = host,
				DesignTimeBuild = designTimeBuild,
				BuildingProject = buildingProject,
				ReportFile      = reportFile,
			};

			return AspectOptionsResolver.Resolve(CreateEmptyCompilation(), options, NullDiagnosticSink.Instance);
		}

		static AspectSourceGenerator.AnalysisResult CreateAnalysis(GeneratorHostKind host, bool? designTimeBuild, bool? buildingProject, string reportFile)
		{
			var options = ResolveFor(host, designTimeBuild, buildingProject, reportFile);

			return new AspectSourceGenerator.AnalysisResult(
				CreateEmptyCompilation(),
				options,
				ImmutableArray<AnalyzedInvocation>.Empty,
				ImmutableArray<DiagnosticInfo>.Empty,
				GeneratorTraceReport.Create(options));
		}

		static Compilation CreateEmptyCompilation()
		{
			return CSharpCompilation.Create(
				"AspectGenerator.Tests.GeneratorHost",
				[CSharpSyntaxTree.ParseText("", path: "GeneratorHostTest.cs")],
				GetMetadataReferences(),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
		}
	}
}
