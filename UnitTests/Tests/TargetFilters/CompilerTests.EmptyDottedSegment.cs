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
			var control      = RunGenerator(EmptyDottedSegmentSource.Replace("BAD_FILTER", "A.**.B.**"));
			var diagnostic   = result.Diagnostics.SingleOrDefault(static d => d.Id == AspectDiagnosticID.InvalidAspectFilterDottedPattern);
			var controlDiagnostic = control.Diagnostics.SingleOrDefault(static d => d.Id == AspectDiagnosticID.InvalidAspectFilterDottedPattern);

			Assert.IsNotNull(diagnostic, string.Join(", ", result.Diagnostics.Select(static d => $"{d.Id}: {d.GetMessage()}")));
			StringAssert.Contains(diagnostic.GetMessage(), "empty segment");
			Assert.IsNotNull(controlDiagnostic);
			Assert.AreEqual(controlDiagnostic.Location.IsInSource, diagnostic.Location.IsInSource);

			// Today the diagnostic sits on the attribute that carries the filter.
			var attribute = "AspectGenerator.Tests.GeneratorDriver.BadFilterAspect(TargetFilter = \"Save.\\nmethod: Ping\")";
			var start     = source.IndexOf(attribute, StringComparison.Ordinal);

			Assert.IsTrue(start >= 0);
			Assert.AreEqual(new Microsoft.CodeAnalysis.Text.TextSpan(start, attribute.Length), diagnostic.Location.SourceSpan, "Actual: " + diagnostic.Location.SourceTree?.GetText().ToString(diagnostic.Location.SourceSpan));
			Assert.IsFalse(result.Results.Any(static r => r.Exception is not null), "The generator threw.");

			Assert.IsFalse(
				compilation.GetDiagnostics().Any(static d => d.Severity == DiagnosticSeverity.Error),
				string.Join(Environment.NewLine, compilation.GetDiagnostics()));

			// Ping (the valid rule of the same filter) and Other (a second aspect) are intercepted; Save is not.
			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");

			Assert.AreEqual(2, CountOccurrences(interceptors, "InterceptsLocationAttribute(1, \""), interceptors);
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
