using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// The host decides the mode before the MSBuild properties do: the compiler always builds, a Roslyn workspace (the
	/// IDE) never does, and only another host (this test driver) follows <c>DesignTimeBuild</c> and <c>BuildingProject</c>.
	/// The IDE reads those properties from the editorconfig that every real build rewrites with a build's values.
	/// </summary>
	public partial class CompilerTests
	{
		const string WorkspacesAssemblyName = "Microsoft.CodeAnalysis.Workspaces";

		[TestMethod]
		public void GeneratorHostIsClassifiedByEntryAssemblyThenByOptionsProviderTest()
		{
			Assert.AreEqual(GeneratorHostKind.Compiler,  GeneratorHost.Classify("csc",          "Microsoft.CodeAnalysis"));
			Assert.AreEqual(GeneratorHostKind.Compiler,  GeneratorHost.Classify("VBCSCompiler", "Microsoft.CodeAnalysis"));
			Assert.AreEqual(GeneratorHostKind.Compiler,  GeneratorHost.Classify("CSC",          null));
			// The compiler never loads the workspace assembly; if it ever did, it is still the compiler.
			Assert.AreEqual(GeneratorHostKind.Compiler,  GeneratorHost.Classify("csc",          WorkspacesAssemblyName));

			// devenv.exe is a native host: no entry assembly. The out-of-process hosts have one: ServiceHub.Host.dotnet.x64
			// in VS 2022 17.14 (process ServiceHub.RoslynCodeAnalysisService.exe), DevHub in VS 2026 18.11, as measured.
			Assert.AreEqual(GeneratorHostKind.Workspace, GeneratorHost.Classify(null,                                  WorkspacesAssemblyName));
			Assert.AreEqual(GeneratorHostKind.Workspace, GeneratorHost.Classify("ServiceHub.Host.dotnet.x64",          WorkspacesAssemblyName));
			Assert.AreEqual(GeneratorHostKind.Workspace, GeneratorHost.Classify("DevHub",                              WorkspacesAssemblyName));
			Assert.AreEqual(GeneratorHostKind.Workspace, GeneratorHost.Classify("Microsoft.CodeAnalysis.LanguageServer", WorkspacesAssemblyName));

			Assert.AreEqual(GeneratorHostKind.Unknown,   GeneratorHost.Classify("testhost", "AspectGenerator.Tests"));
			Assert.AreEqual(GeneratorHostKind.Unknown,   GeneratorHost.Classify("dotnet",   "Microsoft.CodeAnalysis"));
			Assert.AreEqual(GeneratorHostKind.Unknown,   GeneratorHost.Classify(null,       null));
		}

		[TestMethod]
		public void TestDriverIsAnUnknownHostTest()
		{
			var provider = new TestAnalyzerConfigOptionsProvider(new());

			Assert.AreEqual(GeneratorHostKind.Unknown, GeneratorHost.Detect(provider));
			Assert.AreEqual(GeneratorHostKind.Unknown, AspectOptionsResolver.GetMSBuildOptions(provider).Host);
			StringAssert.StartsWith(GeneratorHost.Describe(GeneratorHostKind.Unknown), "unknown (");
		}

		[TestMethod]
		public void WorkspaceHostIsDesignTimeWhateverThePropertiesSayTest()
		{
			// The editorconfig after a real build: BuildingProject=true, DesignTimeBuild empty.
			var afterBuild = ResolveFor(GeneratorHostKind.Workspace, designTimeBuild: null, buildingProject: true);

			Assert.IsTrue (afterBuild.DesignTimeBuild);
			Assert.IsFalse(afterBuild.EmitInterceptors);

			var explicitBuild = ResolveFor(GeneratorHostKind.Workspace, designTimeBuild: false, buildingProject: true);

			Assert.IsTrue (explicitBuild.DesignTimeBuild);
			Assert.IsFalse(explicitBuild.EmitInterceptors);

			var designTime = ResolveFor(GeneratorHostKind.Workspace, designTimeBuild: true, buildingProject: null);

			Assert.IsTrue (designTime.DesignTimeBuild);
			Assert.IsFalse(designTime.EmitInterceptors);
		}

		[TestMethod]
		public void CompilerHostIsABuildWhateverThePropertiesSayTest()
		{
			var normal = ResolveFor(GeneratorHostKind.Compiler, designTimeBuild: null, buildingProject: true);

			Assert.IsFalse(normal.DesignTimeBuild);
			Assert.IsTrue (normal.EmitInterceptors);

			// A project that leaves DesignTimeBuild=true set, with or without BuildingProject.
			var leftSet = ResolveFor(GeneratorHostKind.Compiler, designTimeBuild: true, buildingProject: null);

			Assert.IsFalse(leftSet.DesignTimeBuild);
			Assert.IsTrue (leftSet.EmitInterceptors);
		}

		[TestMethod]
		public void UnknownHostFollowsThePropertiesTest()
		{
			Assert.IsTrue (ResolveFor(GeneratorHostKind.Unknown, designTimeBuild: true,  buildingProject: null ).DesignTimeBuild);
			Assert.IsTrue (ResolveFor(GeneratorHostKind.Unknown, designTimeBuild: true,  buildingProject: false).DesignTimeBuild);
			Assert.IsFalse(ResolveFor(GeneratorHostKind.Unknown, designTimeBuild: true,  buildingProject: true ).DesignTimeBuild);
			Assert.IsFalse(ResolveFor(GeneratorHostKind.Unknown, designTimeBuild: null,  buildingProject: true ).DesignTimeBuild);
			Assert.IsFalse(ResolveFor(GeneratorHostKind.Unknown, designTimeBuild: null,  buildingProject: null ).DesignTimeBuild);
			Assert.IsFalse(ResolveFor(GeneratorHostKind.Unknown, designTimeBuild: false, buildingProject: null ).DesignTimeBuild);
		}

		[TestMethod]
		public void BuildReportIsWrittenByTheCompilerOnlyTest()
		{
			var reportFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "report.md");

			try
			{
				// The IDE after a real build: a build's properties, but a workspace host.
				AspectBuildReport.Write(CreateAnalysis(GeneratorHostKind.Workspace, designTimeBuild: null, buildingProject: true, reportFile));

				Assert.IsFalse(File.Exists(reportFile), $"A workspace host must not write the build report: {reportFile}");

				AspectBuildReport.Write(CreateAnalysis(GeneratorHostKind.Compiler, designTimeBuild: true, buildingProject: null, reportFile));

				Assert.IsTrue(File.Exists(reportFile), $"The compiler must write the build report: {reportFile}");

				var report = File.ReadAllText(reportFile);

				StringAssert.Contains(report, "| Generator host | compiler (");
				StringAssert.Contains(report, "| Interceptor generation | enabled |");

				File.Delete(reportFile);

				AspectBuildReport.Write(CreateAnalysis(GeneratorHostKind.Unknown, designTimeBuild: null, buildingProject: true, reportFile));

				Assert.IsTrue(File.Exists(reportFile), $"An unknown host with a build's properties writes the build report: {reportFile}");
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
