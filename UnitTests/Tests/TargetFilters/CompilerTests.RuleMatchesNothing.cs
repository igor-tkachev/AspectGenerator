using System;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	public partial class CompilerTests
	{
		[TestMethod]
		public void RuleThatCanMatchNothingIsAWarningAndOtherRulesStillApplyTest()
		{
			var result       = RunGeneratorAndGetCompilation(EmptyDottedSegmentSource.Replace("BAD_FILTER", "publik Save\\nmethod: Ping"), out var compilation);
			var diagnostic   = result.Diagnostics.SingleOrDefault(static d => d.Id == AspectDiagnosticID.AspectFilterRuleMatchesNothing);

			Assert.IsNotNull(diagnostic, string.Join(", ", result.Diagnostics.Select(static d => $"{d.Id}: {d.GetMessage()}")));
			Assert.AreEqual(DiagnosticSeverity.Warning, diagnostic.Severity);
			Assert.AreEqual(
				"Target filter rule 'publik Save' can match no method: the name segment 'publik Save' contains white space outside '<...>', which no namespace, type or method name has",
				diagnostic.GetMessage());
			Assert.IsTrue(diagnostic.Location.IsInSource);
			Assert.IsFalse(result.Results.Any(static r => r.Exception is not null), "The generator threw.");
			Assert.IsFalse(
				compilation.GetDiagnostics().Any(static d => d.Severity == DiagnosticSeverity.Error),
				string.Join(Environment.NewLine, compilation.GetDiagnostics()));

			// The rule is kept and selects nothing; Ping (the other rule of the filter) and Other (a second aspect) are
			// intercepted.
			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");

			Assert.AreEqual(2, CountOccurrences(interceptors, "InterceptsLocationAttribute(1, \""), interceptors);
		}
	}
}
