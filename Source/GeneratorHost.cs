using System;
using System.Reflection;

using Microsoft.CodeAnalysis.Diagnostics;

namespace AspectGenerator
{
	/// <summary>
	/// The process that runs the generator.
	/// </summary>
	enum GeneratorHostKind
	{
		/// <summary>
		/// Anything that is neither the compiler nor a known editor: a test driver, <c>dotnet watch</c>, a tool built on
		/// <c>AdhocWorkspace</c> or <c>MSBuildWorkspace</c>, an IDE this generator does not know. Its compilation may be
		/// emitted and run, so it always builds.
		/// </summary>
		Unknown,
		/// <summary>
		/// The C# compiler (<c>csc</c>) or the compiler server (<c>VBCSCompiler</c>): a real build, whatever the properties say.
		/// </summary>
		Compiler,
		/// <summary>
		/// A known interactive editor (Visual Studio, the C# language server of VS Code). The MSBuild properties
		/// <c>DesignTimeBuild</c> and <c>BuildingProject</c> decide the mode there.
		/// </summary>
		Editor,
	}

	/// <summary>
	/// Tells the compiler's run of the generator from an editor's, and both from any other host.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A build's generator runs inside the compiler, whose entry assembly is <c>csc</c> or <c>VBCSCompiler</c>: it always
	/// builds, so a <c>DesignTimeBuild=true</c> left set by a project never switches a real build to design time.
	/// </para>
	/// <para>
	/// The editors this generator knows run it out of process: Visual Studio 2022 in <c>ServiceHub.RoslynCodeAnalysisService.exe</c>
	/// (entry assembly <c>ServiceHub.Host.dotnet.x64</c>), Visual Studio 2026 in <c>DevHub.exe</c>, VS Code in
	/// <c>Microsoft.CodeAnalysis.LanguageServer</c>; a native host without an entry assembly (<c>devenv.exe</c> in process)
	/// counts as an editor when its <see cref="AnalyzerConfigOptionsProvider"/> comes from <c>Microsoft.CodeAnalysis.Workspaces</c>.
	/// Only there do the MSBuild properties decide: <c>DesignTimeBuild=true</c> without <c>BuildingProject=true</c> is the
	/// editor's live analysis, which emits no runnable code and skips the expensive work. The editor reads both properties
	/// from <c>obj/.../*.GeneratedMSBuildEditorConfig.editorconfig</c>, which every real build rewrites with
	/// <c>BuildingProject=true</c>; from then on the editor builds too, on purpose: its compilation is what Hot Reload and
	/// Edit and Continue emit the delta from, and a delta without interceptors silently drops the aspects of the edited
	/// method.
	/// </para>
	/// <para>
	/// Every other host builds. A Roslyn workspace is not an editor marker: <c>AdhocWorkspace</c>, <c>MSBuildWorkspace</c>,
	/// <c>dotnet watch</c> and the <c>Microsoft.CodeAnalysis.Testing</c> harness all hand the generator a workspace's options
	/// provider, and all of them may emit and run the compilation. An editor this generator does not know pays the full
	/// analysis per run; its name is in the build report's <c>Generator host</c> row.
	/// </para>
	/// </remarks>
	static class GeneratorHost
	{
		const string CompilerAssemblyName       = "csc";
		const string CompilerServerAssemblyName = "VBCSCompiler";
		const string WorkspacesAssemblyName     = "Microsoft.CodeAnalysis.Workspaces";

		static readonly string[] _editorAssemblyNames =
		[
			"ServiceHub.Host.dotnet.x64",          // Visual Studio 2022, ServiceHub.RoslynCodeAnalysisService.exe
			"DevHub",                              // Visual Studio 2026
			"Microsoft.CodeAnalysis.LanguageServer", // VS Code C# extension
		];

		static readonly Lazy<string?> _entryAssemblyName = new(GetEntryAssemblyName);

		public static string? EntryAssemblyName => _entryAssemblyName.Value;

		/// <summary>
		/// The host the tests pretend to be; the test driver itself is an unknown host, which always builds.
		/// </summary>
		internal static GeneratorHostKind? TestHost { get; set; }

		public static GeneratorHostKind Detect(AnalyzerConfigOptionsProvider optionsProvider)
		{
			if (TestHost is {} testHost)
				return testHost;

			return Classify(EntryAssemblyName, optionsProvider.GetType().Assembly.GetName().Name);
		}

		public static GeneratorHostKind Classify(string? entryAssemblyName, string? optionsProviderAssemblyName)
		{
			if (string.Equals(entryAssemblyName, CompilerAssemblyName,       StringComparison.OrdinalIgnoreCase) ||
				string.Equals(entryAssemblyName, CompilerServerAssemblyName, StringComparison.OrdinalIgnoreCase))
				return GeneratorHostKind.Compiler;

			foreach (var editorAssemblyName in _editorAssemblyNames)
				if (string.Equals(entryAssemblyName, editorAssemblyName, StringComparison.OrdinalIgnoreCase))
					return GeneratorHostKind.Editor;

			if (entryAssemblyName is null &&
				string.Equals(optionsProviderAssemblyName, WorkspacesAssemblyName, StringComparison.OrdinalIgnoreCase))
				return GeneratorHostKind.Editor;

			return GeneratorHostKind.Unknown;
		}

		/// <summary>
		/// Describes the host for the build report and the trace.
		/// </summary>
		public static string Describe(GeneratorHostKind host)
		{
			var entryAssemblyName = EntryAssemblyName ?? "no entry assembly";

			return host switch
			{
				GeneratorHostKind.Compiler => $"compiler ({entryAssemblyName})",
				GeneratorHostKind.Editor   => $"editor ({entryAssemblyName})",
				_                          => $"unknown ({entryAssemblyName})",
			};
		}

		static string? GetEntryAssemblyName()
		{
			try
			{
				return Assembly.GetEntryAssembly()?.GetName().Name;
			}
			catch
			{
				// Without a name the options provider decides between an in-process editor and an unknown host.
				return null;
			}
		}
	}
}
