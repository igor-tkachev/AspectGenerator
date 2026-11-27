using System;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	public partial class CompilerTests
	{
		[TestMethod]
		public void EmptyDottedSegmentInTargetFilterIsReportedAndOtherRulesStillApplyTest()
		{
			var source       = EmptyDottedSegmentSource.Replace("BAD_FILTER", "Save.\\nmethod: Ping");
			var result       = RunGeneratorAndGetCompilation(source, out var compilation);
			var control      = RunGenerator(EmptyDottedSegmentSource.Replace("BAD_FILTER", "method: Sa**ve"));
			var diagnostic   = result.Diagnostics.SingleOrDefault(static d => d.Id == AspectDiagnosticID.FilterSyntaxError);
			var controlDiagnostic = control.Diagnostics.SingleOrDefault(static d => d.Id == AspectDiagnosticID.FilterInvalidPattern);

			Assert.IsNotNull(diagnostic, string.Join(", ", result.Diagnostics.Select(static d => $"{d.Id}: {d.GetMessage()}")));
			Assert.IsNotNull(controlDiagnostic);
			Assert.AreEqual(controlDiagnostic.Location.IsInSource, diagnostic.Location.IsInSource);

			// The diagnostic points inside the string literal, where the name ends after its dot.
			var literal = "TargetFilter = \"Save.\\nmethod: Ping\"";
			var at      = source.IndexOf(literal, StringComparison.Ordinal);
			var start   = at + "TargetFilter = \"".Length;

			Assert.IsTrue(at >= 0);
			Assert.AreEqual(new Microsoft.CodeAnalysis.Text.TextSpan(start + "Save.".Length, 0), diagnostic.Location.SourceSpan, "Actual: " + diagnostic.Location.SourceTree?.GetText().ToString(diagnostic.Location.SourceSpan));
			Assert.IsFalse(result.Results.Any(static r => r.Exception is not null), "The generator threw.");

			Assert.IsFalse(
				compilation.GetDiagnostics().Any(static d => d.Severity == DiagnosticSeverity.Error),
				string.Join(Environment.NewLine, compilation.GetDiagnostics()));

			// A filter with an error selects nothing, so the aspect that holds it intercepts neither Save nor Ping; only
			// Other (a second aspect, with a filter that reads) is intercepted.
			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");

			Assert.AreEqual(1, CountOccurrences(interceptors, "InterceptsLocationAttribute(1, \""), interceptors);
		}

		const string EmptyDottedSegmentSource =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.BadFilterAspect(TargetFilter = "BAD_FILTER")]
			[assembly: AspectGenerator.Tests.GeneratorDriver.GoodFilterAspect(TargetFilter = "method: Other")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class BadFilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info)
				{
					info.ReturnValue += " bad";
				}
			}

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class GoodFilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info)
				{
					info.ReturnValue += " good";
				}
			}

			static class Target
			{
				public static void Invoke()
				{
					Save();
					Ping();
					Other();
				}

				public static string Save()
				{
					return "save";
				}

				public static string Ping()
				{
					return "ping";
				}

				public static string Other()
				{
					return "other";
				}
			}
			""";
	}
}
