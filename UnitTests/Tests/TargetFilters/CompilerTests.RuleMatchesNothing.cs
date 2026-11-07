using System;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	public partial class CompilerTests
	{
		[TestMethod]
		public void RuleThatCanMatchNothingIsAWarningAWhiteSpaceNameIsInformationAndOtherRulesStillApplyTest()
		{
			var result     = RunGeneratorAndGetCompilation(EmptyDottedSegmentSource.Replace("BAD_FILTER", "public private Save\\npublik Save\\nmethod: Ping"), out var compilation);
			var diagnostic = result.Diagnostics.SingleOrDefault(static d => d.Id == AspectDiagnosticID.AspectFilterRuleMatchesNothing);
			var typo       = result.Diagnostics.SingleOrDefault(static d => d.Id == AspectDiagnosticID.AspectFilterNameWithWhiteSpace);

			Assert.IsNotNull(diagnostic, string.Join(", ", result.Diagnostics.Select(static d => $"{d.Id}: {d.GetMessage()}")));
			Assert.IsNotNull(typo,       string.Join(", ", result.Diagnostics.Select(static d => $"{d.Id}: {d.GetMessage()}")));
			Assert.AreEqual(DiagnosticSeverity.Warning, diagnostic.Severity);
			Assert.AreEqual(DiagnosticSeverity.Info,    typo.Severity);
			Assert.AreEqual(
				"Target filter rule 'public private Save' can match no method: no method is public and private at once; a method must have every accessibility word of a rule",
				diagnostic.GetMessage());
			Assert.AreEqual(
				"Target filter rule 'publik Save' names 'publik Save', with white space outside '<...>': a C# name never has white space there, so the rule probably holds a typo, such as a misspelled modifier word",
				typo.GetMessage());
			Assert.IsTrue(typo.Location.IsInSource);
			Assert.IsTrue(diagnostic.Location.IsInSource);
			Assert.IsFalse(result.Results.Any(static r => r.Exception is not null), "The generator threw.");
			Assert.IsFalse(
				compilation.GetDiagnostics().Any(static d => d.Severity == DiagnosticSeverity.Error),
				string.Join(Environment.NewLine, compilation.GetDiagnostics()));

			// The rules are kept and select nothing; Ping (the other rule of the filter) and Other (a second aspect) are
			// intercepted.
			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");

			Assert.AreEqual(2, CountOccurrences(interceptors, "InterceptsLocationAttribute(1, \""), interceptors);
		}
	}
}
