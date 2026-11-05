using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// Helpers for the generated-call regression tests: run the generator on an app (and a library compiled with it),
	/// compile, run <c>Calls.Run()</c>, and describe the outcome as "skips | errors | value".
	/// </summary>
	public partial class CompilerTests
	{
		static string[] ProbeGeneratedErrors(Compilation compilation)
		{
			return compilation.GetDiagnostics()
				.Where(static d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree?.FilePath.EndsWith("Interceptors.g.cs", StringComparison.Ordinal) == true)
				.Select(static d => d.ToString())
				.ToArray();
		}

		const string ProbeLogAspect =
			"""
			using System;
			using AspectGenerator;

			static class ProbeLog
			{
				public static readonly System.Text.StringBuilder Text = new();
			}

			[Aspect(OnBeforeCall = nameof(Before))]
			[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
			sealed class LogAttribute : Attribute
			{
				public static void Before(InterceptInfo info) => ProbeLog.Text.Append(info.MemberInfo.Name).Append(';');
			}

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Assembly)]
			sealed class AfterAttribute : Attribute
			{
				public string? TargetFilter { get; set; }
				public static void After(InterceptInfo info) => ProbeLog.Text.Append("after:").Append(info.MemberInfo.Name).Append(';');
			}
			""";

		sealed class ProbeContext(byte[]? libImage) : AssemblyLoadContext("ProbeTests", isCollectible: true)
		{
			Assembly? _lib;

			protected override Assembly? Load(AssemblyName name)
			{
				if (libImage is not null && name.Name == "ProbeLib")
					return _lib ??= LoadFromStream(new MemoryStream(libImage));

				return null;
			}
		}

		static (MetadataReference Reference, byte[] Image) CompileProbeLibrary(params string[] sources)
		{
			var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview).WithFeatures([new("InterceptorsNamespaces", "AspectGenerator")]);
			var compilation  = CSharpCompilation.Create(
				"ProbeLib",
				sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, parseOptions, $"Lib{i}.cs")),
				GetMetadataReferences(),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

			GeneratorDriver driver = CSharpGeneratorDriver.Create(
				[new AspectSourceGenerator().AsSourceGenerator()],
				parseOptions: parseOptions,
				optionsProvider: new TestAnalyzerConfigOptionsProvider(new()
				{
					["build_property.AspectGeneratorPublicApi"]    = "true",
					["build_property.InterceptorsNamespaces"]      = "AspectGenerator",
					["build_property.AspectGeneratorPropsImported"] = "true",
				}));

			driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

			using var stream = new MemoryStream();

			var emit = output.Emit(stream);

			Assert.IsTrue(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));

			var image = stream.ToArray();

			return (MetadataReference.CreateFromImage(image), image);
		}

		/// <summary>Runs the generator on the app, compiles and runs Calls.Run(); returns "skips | errors | value".</summary>
		static string RunProbe(string[] sources, (MetadataReference Reference, byte[] Image)? lib = null, string? diagnosticId = null)
		{
			var result       = RunGeneratorAndGetCompilation(sources, out var compilation, reference: lib?.Reference);
			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");
			var sb           = new StringBuilder();
			var skips        = result.Diagnostics.Where(static d => d.Id == AspectDiagnosticID.CannotIntercept).ToArray();

			sb.Append("AG0003=").Append(skips.Length);

			foreach (var s in skips)
				sb.Append(" [").Append(s.GetMessage()).Append(']');

			foreach (var d in result.Diagnostics.Where(static d => d.Id != AspectDiagnosticID.CannotIntercept && d.Severity >= DiagnosticSeverity.Info))
				sb.Append(" {").Append(d.Id).Append(' ').Append(d.Severity).Append(": ").Append(d.GetMessage()).Append('}');

			if (diagnosticId is not null)
				foreach (var d in result.Diagnostics.Where(d => d.Id == diagnosticId))
					sb.Append(" | ").Append(diagnosticId).Append(' ').Append(d.Severity).Append(" @").Append(d.Location.GetLineSpan().StartLinePosition.Line);

			var errors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();

			if (errors.Length > 0)
				return sb.Append(" | ERRORS ").Append(string.Join(" ; ", errors.Select(static e => e.ToString()))).Append("\n").Append(interceptors).ToString();

			using var stream = new MemoryStream();

			var emit = compilation.Emit(stream);

			if (!emit.Success)
				return sb.Append(" | EMIT ").Append(string.Join(" ; ", emit.Diagnostics)).ToString();

			var context = new ProbeContext(lib?.Image);

			try
			{
				stream.Position = 0;

				var assembly = context.LoadFromStream(stream);
				var run      = assembly.GetType("Calls")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;

				sb.Append(" | ").Append((string)run.Invoke(null, null)!);
			}
			catch (Exception ex)
			{
				sb.Append(" | EXCEPTION ").Append(ex.InnerException?.Message ?? ex.Message);
			}
			finally
			{
				context.Unload();
			}

			return sb.ToString();
		}

		const string ProbeLibAspect =
			"""
			using System;
			using AspectGenerator;

			public static class LibLog
			{
				public static readonly System.Text.StringBuilder Text = new();
			}
			""";
	}
}
