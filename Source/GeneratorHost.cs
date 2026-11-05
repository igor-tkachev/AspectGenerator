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
		/// Neither the compiler nor a Roslyn workspace: a test driver, a custom tool. The MSBuild properties
		/// <c>DesignTimeBuild</c> and <c>BuildingProject</c> decide the mode.
		/// </summary>
		Unknown,
		/// <summary>
		/// The C# compiler (<c>csc</c>) or the compiler server (<c>VBCSCompiler</c>): a real build, whatever the properties say.
		/// </summary>
		Compiler,
		/// <summary>
		/// A Roslyn workspace host (Visual Studio, the C# language server of VS Code, <c>MSBuildWorkspace</c> tools):
		/// live analysis, never a build.
		/// </summary>
		Workspace,
	}

	/// <summary>
	/// Tells the compiler's run of the generator from an IDE's.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The MSBuild properties alone cannot: the IDE reads <c>DesignTimeBuild</c> and <c>BuildingProject</c> from
	/// <c>obj/.../*.GeneratedMSBuildEditorConfig.editorconfig</c>, which every real build rewrites with
	/// <c>BuildingProject=true</c> and an empty <c>DesignTimeBuild</c>. From then until the next design-time build the IDE
	/// hands the generator a build's properties, and a generator that trusts them runs the full build analysis on every
	/// edit, and writes the build report over the compiler's.
	/// </para>
	/// <para>
	/// Two facts about the process are reliable instead. A build's generator runs inside the compiler, whose entry assembly
	/// is <c>csc</c> or <c>VBCSCompiler</c> (a native host such as <c>devenv.exe</c> has no entry assembly at all). An IDE
	/// runs the generator through a Roslyn workspace, whose <see cref="AnalyzerConfigOptionsProvider"/> comes from
	/// <c>Microsoft.CodeAnalysis.Workspaces</c>; the compiler's comes from <c>Microsoft.CodeAnalysis</c> and never loads the
	/// workspace assembly. Any other host keeps the property rule.
	/// </para>
	/// </remarks>
	static class GeneratorHost
	{
		const string CompilerAssemblyName       = "csc";
		const string CompilerServerAssemblyName = "VBCSCompiler";
		const string WorkspacesAssemblyName     = "Microsoft.CodeAnalysis.Workspaces";

		static readonly Lazy<string?> _entryAssemblyName = new(GetEntryAssemblyName);

		public static string? EntryAssemblyName => _entryAssemblyName.Value;

		public static GeneratorHostKind Detect(AnalyzerConfigOptionsProvider optionsProvider)
		{
			return Classify(EntryAssemblyName, optionsProvider.GetType().Assembly.GetName().Name);
		}

		public static GeneratorHostKind Classify(string? entryAssemblyName, string? optionsProviderAssemblyName)
		{
			if (string.Equals(entryAssemblyName, CompilerAssemblyName,       StringComparison.OrdinalIgnoreCase) ||
				string.Equals(entryAssemblyName, CompilerServerAssemblyName, StringComparison.OrdinalIgnoreCase))
				return GeneratorHostKind.Compiler;

			if (string.Equals(optionsProviderAssemblyName, WorkspacesAssemblyName, StringComparison.OrdinalIgnoreCase))
				return GeneratorHostKind.Workspace;

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
				GeneratorHostKind.Compiler  => $"compiler ({entryAssemblyName})",
				GeneratorHostKind.Workspace => $"workspace ({entryAssemblyName})",
				_                           => $"unknown ({entryAssemblyName})",
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
				// A host may refuse the question; then the properties decide, as before.
				return null;
			}
		}
	}
}
