using System;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static AspectGenerator.Tests.TargetFilters.TargetFilterCases;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// One behaviour of the target-filter parser per test, observed through the fixed target list. Where a test pins
	/// behaviour that looks like a defect, its comment says so: the test then states what happens today, and changes
	/// together with the fix.
	/// </summary>
	[TestClass]
	public class TargetFilterPinTests
	{
		const string SaveVoid          = "public System.Void Acme.Core.Service.Save()";
		const string SaveInt           = "public System.Void Acme.Core.Service.Save(System.Int32)";
		const string SaveAsyncPlain    = "public System.Threading.Tasks.Task Acme.Core.Service.SaveAsync()";
		const string SaveAsyncToken    = "public System.Threading.Tasks.Task Acme.Core.Service.SaveAsync(System.Threading.CancellationToken)";
		const string LoadAsyncString   = "public System.Threading.Tasks.Task<System.String> Acme.Core.Service.LoadAsync()";
		const string Ping              = "public System.Void Acme.Core.Service.Ping()";
		const string FindString        = "public System.String Acme.Core.Service.Find(System.String)";
		const string PublicStatic      = "public static System.Void Acme.Core.Service.Create()";
		const string TaggedMethod      = "public System.Void Acme.Core.Service.Tagged()";
		const string WorkOfTaggedType  = "public System.Void TaggedGlobal.Work()";
		const string CancelWithToken   = "public System.Void Lib.Tasks.Worker.Cancel(System.Threading.CancellationToken)";
		const string SaveItem          = "public System.Void Acme.Core.Service.Save_Item()";
		const string ServiceRunOfImpl  = "public System.Void Acme.Core.Impl.Run()";

		static int Index(string signature)
		{
			var entries = TargetFilterTargets.Entries;

			for (var i = 0; i < entries.Length; i++)
				if (entries[i].Target.Signature == signature)
					return i;

			throw new AssertFailedException("No target has the signature " + signature);
		}

		static TargetFilterProbe.Outcome Probe(string input)
		{
			return TargetFilterProbe.Probe(input);
		}

		static char Kind(TargetFilterProbe.Outcome outcome, string signature)
		{
			return outcome.Decisions[Index(signature)].Kind;
		}

		static void AssertSameDecisions(string expected, string actual)
		{
			var e = Probe(expected);
			var a = Probe(actual);

			Assert.AreEqual(e.Kinds, a.Kinds, $"'{expected}' and '{actual}' decide differently.");
			CollectionAssert.AreEqual(e.Ids, a.Ids, $"'{expected}' and '{actual}' report different diagnostics.");
		}

		static void AssertDiagnostics(string input, params string[] ids)
		{
			CollectionAssert.AreEqual(ids, Probe(input).Ids, "Diagnostics of: " + TargetFilterProbe.Escape(input));
		}

		static void AssertMatchesNothing(TargetFilterProbe.Outcome outcome)
		{
			Assert.IsFalse(outcome.Kinds.Any(static k => k != 'n'), "Expected no decisions, got " + outcome.Kinds.Count(static k => k != 'n') + ".");
		}

		// ---- type patterns ----

		[TestMethod]
		public void ReturnsNullableStringMatchesStringReturnTest()
		{
			// The canonical signature carries no nullable annotations, so a pattern with ? matches the unannotated type.
			AssertSameDecisions(ReturnsString, ReturnsNullableString);
			AssertSameDecisions(ReturnsSystemString, ReturnsNullableString);

			var outcome = Probe(ReturnsNullableString);

			Assert.AreEqual('i', Kind(outcome, FindString));
			Assert.AreEqual('n', Kind(outcome, SaveVoid));
			Assert.AreEqual(0, outcome.Ids.Length);
		}

		[TestMethod]
		public void QuestionMarkAfterIdentifierInTypePatternIsDroppedTest()
		{
			// "Ta?g" reads as "Tag": the ? is taken for a nullable annotation, not a one-character wildcard.
			AssertSameDecisions(AttributePlain, AttributeQuestionMark);

			var outcome = Probe(AttributeQuestionMark);

			Assert.AreEqual('i', Kind(outcome, TaggedMethod));
			Assert.AreEqual('i', Kind(outcome, WorkOfTaggedType));
			Assert.AreEqual('n', Kind(outcome, SaveVoid));
		}

		[TestMethod]
		public void QuestionMarkInSegmentPatternIsOneCharacterWildcardTest()
		{
			var outcome = Probe(MethodQuestionMark);

			Assert.AreEqual('i', Kind(outcome, SaveVoid));
			Assert.AreEqual('n', Kind(outcome, SaveItem));
			Assert.AreEqual('n', Kind(outcome, Ping));
		}

		[TestMethod]
		public void QuestionMarkAfterStarKeepsTheWildcardTest()
		{
			// "Task*?" needs at least one character after Task, so the plain Task does not match and Task<...> does.
			var withQuestion = Probe(ReturnsStarQuestion);
			var without      = Probe(ReturnsStar);

			Assert.AreEqual('n', Kind(withQuestion, SaveAsyncPlain));
			Assert.AreEqual('i', Kind(withQuestion, LoadAsyncString));
			Assert.AreEqual('i', Kind(without, SaveAsyncPlain));
			Assert.AreEqual('i', Kind(without, LoadAsyncString));
		}

		[TestMethod]
		public void StarThenQuestionMarkMethodPatternNeedsOneCharacterTest()
		{
			AssertSameDecisions(MethodStar, MethodStarQuestion);
		}

		[TestMethod]
		public void EscapedStarAfterContainsWildcardMatchesContainsTest()
		{
			// Known defect: "*a\*" should mean a name that contains "a*", but the trailing backslash is dropped and the
			// pattern becomes "contains a". Update this test when the escape is honoured.
			AssertSameDecisions(MethodContainsA, MethodContainsEscaped);

			var outcome = Probe(MethodContainsEscaped);

			Assert.AreEqual('i', Kind(outcome, SaveVoid));
			Assert.AreEqual('n', Kind(outcome, Ping));
		}

		// ---- modifiers and accessibility ----

		[TestMethod]
		public void ContradictoryAccessibilityMatchesNothingTest()
		{
			var outcome = Probe(PublicPrivate);

			AssertMatchesNothing(outcome);
			Assert.AreEqual(0, outcome.Ids.Length);
			Assert.AreEqual('i', Kind(Probe(PublicOnly), SaveVoid));
		}

		[TestMethod]
		public void MisspelledModifierBecomesPartOfTheMethodNameTest()
		{
			var outcome = Probe(Misspelled);

			Assert.AreEqual(0, outcome.Ids.Length);
			AssertMatchesNothing(outcome);
		}

		[TestMethod]
		public void ModifierWordsAreCaseSensitiveTest()
		{
			AssertMatchesNothing(Probe(ModifierUpperCase));
			AssertMatchesNothing(Probe(ModifierOnlyUpperCase));
			Assert.AreEqual(0, Probe(ModifierUpperCase).Ids.Length);
			Assert.AreEqual(0, Probe(ModifierOnlyUpperCase).Ids.Length);

			Assert.AreEqual('i', Kind(Probe(ModifierLowerCase), SaveVoid));
			Assert.AreEqual('i', Kind(Probe(ModifierOnlyLowerCase), PublicStatic));
		}

		// ---- keys, matcher prefixes, prefixes ----

		[TestMethod]
		public void ConditionKeysAreCaseInsensitiveTest()
		{
			AssertSameDecisions(KeyLowerCase, KeyUpperCase);
			AssertSameDecisions(KeyLowerCase, KeyMixedCase);
			Assert.AreEqual('i', Kind(Probe(KeyUpperCase), SaveVoid));
		}

		[TestMethod]
		public void MatcherPrefixesAreCaseInsensitiveTest()
		{
			AssertSameDecisions(RegexPrefixLowerCase, RegexPrefixUpperCase);
			AssertSameDecisions(ContainsSave, ContainsPrefixUpper);
			AssertSameDecisions(PatternPrefixLower, PatternPrefixUpper);
			Assert.AreEqual('i', Kind(Probe(RegexPrefixUpperCase), SaveVoid));
		}

		[TestMethod]
		public void ExcludePrefixReadsOnlyTheFirstPrefixTest()
		{
			// "-&" is an exclude whose body starts with "&": the second prefix is not read, so the rule is a compact
			// method pattern that matches nothing, and there is no leading-& diagnostic.
			foreach (var input in new[] { ExcludeAndPrefix, ExcludeOrPrefix })
			{
				var outcome = Probe(input);

				Assert.AreEqual(0, outcome.Ids.Length, input);
				AssertMatchesNothing(outcome);
			}
		}

		[TestMethod]
		public void PatternPrefixKeepsAConditionRuleStandaloneTest()
		{
			AssertSameDecisions(ConditionEquivalent, PatternStandalone);

			// A pattern: rule is not a native condition group, so a following leading | has no group to continue.
			AssertDiagnostics(PatternThenBar, "AG0202");
			AssertDiagnostics(ConditionThenBar);
		}

		[TestMethod]
		public void ParamsWithoutParenthesesTest()
		{
			AssertSameDecisions(ParamsWithParens, ParamsWithoutParens);

			var outcome = Probe(ParamsWithoutParens);

			Assert.AreEqual('i', Kind(outcome, SaveAsyncToken));
			Assert.AreEqual('i', Kind(outcome, CancelWithToken));
			Assert.AreEqual('n', Kind(outcome, SaveAsyncPlain));
			Assert.AreEqual(0, outcome.Ids.Length);
		}

		// ---- patterns ----

		[TestMethod]
		public void SingleNameIsTheSameAsAnyNamespaceAndTypeTest()
		{
			AssertSameDecisions(SingleNameExpanded, SingleName);

			var outcome = Probe(SingleName);

			Assert.AreEqual('i', Kind(outcome, SaveVoid));
			Assert.AreEqual('n', Kind(outcome, SaveAsyncPlain));
		}

		[TestMethod]
		public void FinalRecursiveWildcardIsAllowedInNamespaceAndFullTypeOnlyTest()
		{
			AssertDiagnostics(FinalStarNamespace);
			AssertDiagnostics(FinalStarFullType);
			AssertDiagnostics(FinalStarCompact, "AG0206");

			Assert.AreEqual('i', Kind(Probe(FinalStarNamespace), SaveVoid));
			Assert.AreEqual('i', Kind(Probe(FinalStarFullType), SaveVoid));
		}

		[TestMethod]
		public void SimpleKeyBeforeReturnTypeIsAnUnknownConditionKeyTest()
		{
			AssertDiagnostics(SaveWithReturn, "AG0204");
			AssertMatchesNothing(Probe(SaveWithReturn));

			// With a modifier or a wildcard the text before the colon is a method pattern, not a key.
			AssertDiagnostics(PublicSaveWithReturn);
			Assert.AreEqual('i', Kind(Probe(PublicSaveWithReturn), SaveVoid));
		}

		// ---- diagnostics ----

		[TestMethod]
		public void LeadingAmpersandOrBarWithoutConditionGroupIsInvalidTest()
		{
			foreach (var input in new[] { LeadingAnd, LeadingOr, LeadingAndCompact, LeadingOrAfterRegex, LeadingOrAfterContains })
				AssertDiagnostics(input, "AG0202");
		}

		[TestMethod]
		public void EmptyOperandIsInvalidTest()
		{
			foreach (var input in new[] { EmptyOperandOrTrailing, EmptyOperandOrLeading, EmptyOperandAnd, EmptyBodyDash, EmptyBodyRegex, EmptyBodyContains })
				AssertDiagnostics(input, "AG0202");
		}

		[TestMethod]
		public void DuplicatedEllipsisAndThisParameterReportParameterPatternTest()
		{
			AssertDiagnostics(EllipsisTwice, "AG0205");
			AssertDiagnostics(EllipsisTwiceParams, "AG0205");
			AssertDiagnostics(ThisParameter, "AG0205");
		}

		[TestMethod]
		public void InvalidRegexIsReportedTest()
		{
			AssertDiagnostics(RegexInvalid, "AG0201");
			AssertMatchesNothing(Probe(RegexInvalid));
		}

		[TestMethod]
		public void DiagnosticsAreReportedInRuleOrderTest()
		{
			AssertDiagnostics("foo: bar\nSave(..., ...)\n& method: x\nregex: [\nA.**.B.**", "AG0204", "AG0205", "AG0202", "AG0201", "AG0206");
		}

		[TestMethod]
		public void EmptyDottedSegmentInCompactPatternIsReportedTest()
		{
			// An empty segment ("Save.", ".Save", "A..B") is an invalid dotted pattern; it used to end in a
			// NullReferenceException.
			foreach (var input in new[] { "Save.", ".Save", "..", "Acme..Save" })
			{
				var outcome = Probe(input);

				Assert.IsNull(outcome.Exception, input);
				CollectionAssert.AreEqual(new[] { "AG0206" }, outcome.Ids, input);
				AssertMatchesNothing(outcome);
			}
		}

		[TestMethod]
		public void InvalidDottedPatternDoesNotDisableOtherRulesTest()
		{
			var outcome = Probe("Save.\nPing");

			CollectionAssert.AreEqual(new[] { "AG0206" }, outcome.Ids);
			Assert.AreEqual('i', Kind(outcome, Ping));
			Assert.AreEqual('n', Kind(outcome, SaveVoid));
		}

		// ---- contains: and regex: ----

		[TestMethod]
		public void ContainsIsOrdinalAndCaseSensitiveTest()
		{
			foreach (var (input, text) in new[] { (ContainsSave, "Save"), (ContainsLowerSave, "save"), (ContainsUpperSave, "SAVE") })
			{
				var outcome = Probe(input);

				for (var i = 0; i < TargetFilterTargets.Entries.Length; i++)
					Assert.AreEqual(
						TargetFilterTargets.Entries[i].Target.Signature.Contains(text, StringComparison.Ordinal),
						outcome.Decisions[i].Kind == 'i',
						$"{input} / {TargetFilterTargets.Entries[i].Target.Signature}");
			}

			Assert.AreEqual('i', Kind(Probe(ContainsSave), SaveVoid));
			Assert.AreEqual('n', Kind(Probe(ContainsLowerSave), SaveVoid));
			AssertMatchesNothing(Probe(ContainsUpperSave));
		}

		[TestMethod]
		public void ContainsDoesNotParseOperatorsTest()
		{
			AssertMatchesNothing(Probe(ContainsWithSeparators));
			Assert.AreEqual(0, Probe(ContainsWithSeparators).Ids.Length);
		}

		[TestMethod]
		public void RegexIsUnanchoredAndCaseSensitiveTest()
		{
			foreach (var (input, pattern) in new[] { (RegexUnanchored, "Sav"), (RegexAnchored, "^Sav"), (RegexLowerCase, "sav") })
			{
				var outcome = Probe(input);

				for (var i = 0; i < TargetFilterTargets.Entries.Length; i++)
					Assert.AreEqual(
						Regex.IsMatch(TargetFilterTargets.Entries[i].Target.Signature, pattern, RegexOptions.CultureInvariant),
						outcome.Decisions[i].Kind == 'i',
						$"{input} / {TargetFilterTargets.Entries[i].Target.Signature}");
			}

			Assert.AreEqual('i', Kind(Probe(RegexUnanchored), SaveVoid));
			AssertMatchesNothing(Probe(RegexAnchored));

			var anchored = Probe(RegexAnchoredSignature);

			Assert.AreEqual(1, anchored.Kinds.Count(static k => k == 'i'));
			Assert.AreEqual('i', Kind(anchored, SaveVoid));
		}

		// ---- ordering ----

		[TestMethod]
		public void LastMatchingRuleWinsTest()
		{
			var includes = Probe(LastMatchIncludes);
			var excludes = Probe(LastMatchExcludes);
			var save     = Index(SaveVoid);
			var ping     = Index(Ping);

			Assert.AreEqual('i', includes.Decisions[save].Kind);
			Assert.AreEqual(2, includes.Decisions[save].RuleIndex);
			Assert.AreEqual('e', excludes.Decisions[save].Kind);
			Assert.AreEqual(1, excludes.Decisions[save].RuleIndex);

			// A method only the first rule matches keeps that decision (the unprefixed lines are one group).
			Assert.AreEqual('i', includes.Decisions[ping].Kind);
			Assert.AreEqual(0, includes.Decisions[ping].RuleIndex);
			Assert.AreEqual('n', excludes.Decisions[ping].Kind);
		}

		[TestMethod]
		public void ExcludeCountsOnlyWhenItIsTheLastMatchTest()
		{
			var excludeFirst = Probe(ExcludeNotLast);
			var excludeLast  = Probe(ExcludeLast);
			var save         = Index(SaveVoid);
			var saveAsync    = Index(SaveAsyncPlain);

			Assert.AreEqual('i', excludeFirst.Decisions[save].Kind);
			Assert.AreEqual(1, excludeFirst.Decisions[save].RuleIndex);
			Assert.AreEqual('i', excludeFirst.Decisions[saveAsync].Kind);
			Assert.AreEqual('n', Kind(excludeFirst, Ping));

			Assert.AreEqual('e', excludeLast.Decisions[save].Kind);
			Assert.AreEqual(1, excludeLast.Decisions[save].RuleIndex);
			Assert.AreEqual('i', excludeLast.Decisions[saveAsync].Kind);
			Assert.AreEqual(0, excludeLast.Decisions[saveAsync].RuleIndex);
		}

		[TestMethod]
		public void CommentsAndBlankLinesAreNotRulesTest()
		{
			AssertSameDecisions("Save", "# comment\n\n   \nSave\n# another");
			Assert.AreEqual(1, Probe("# comment\n\n   \nSave\n# another").RuleCount);
		}

		[TestMethod]
		public void RuleSeparatorsOfEveryKindAreAcceptedTest()
		{
			AssertSameDecisions("Save\nPing", "Save\r\nPing");
			AssertSameDecisions("Save\nPing", "Save\rPing");
			Assert.AreEqual(2, Probe("Save\r\nPing").RuleCount);
		}

		[TestMethod]
		public void ConditionLinesOfOneActionFormOneRuleTest()
		{
			var outcome = Probe("namespace: Acme.Core\n& type: Service\n& method: Save*");

			Assert.AreEqual(1, outcome.RuleCount);
			Assert.AreEqual('i', Kind(outcome, SaveVoid));
		}

		[TestMethod]
		public void PlanFollowsTheRulesTest()
		{
			Assert.AreEqual("target-pattern|111010|Save|", TargetFilterProbe.FormatPlan(Probe("Save").Plan));
			Assert.AreEqual("signature-text|101011||", TargetFilterProbe.FormatPlan(Probe("contains: Save").Plan));
			Assert.AreEqual("target-pattern|111000||", TargetFilterProbe.FormatPlan(Probe("-Save").Plan));
			Assert.AreEqual("target-pattern|101011||", TargetFilterProbe.FormatPlan(Probe("public **.*").Plan));
			Assert.AreEqual("target-pattern|111010||Acme", TargetFilterProbe.FormatPlan(Probe("fulltype: Acme.Core.Service").Plan));
		}

		// ---- what a record keeps ----

		[TestMethod]
		public void PlanListElementsAreEncodedUnambiguouslyTest()
		{
			// One literal name containing a comma and two alternative names must not look alike.
			var one = Probe("method: FromAsse,mbly");
			var two = Probe("method: FromAsse | mbly");

			Assert.AreNotEqual(TargetFilterProbe.FormatPlan(one.Plan), TargetFilterProbe.FormatPlan(two.Plan));
			Assert.AreNotEqual(TargetFilterProbe.FormatRecord("x", one), TargetFilterProbe.FormatRecord("x", two));
		}

		[TestMethod]
		public void UnmatchedRulesAreKeptInTheRecordTest()
		{
			// A rule that matches no target (here an exclude) still changes the record, so dropping it is noticed.
			var plain = TargetFilterProbe.FormatRecord("x", Probe("Save"));
			var more  = TargetFilterProbe.FormatRecord("x", Probe("Save\n-attributes: NoLog"));

			Assert.AreNotEqual(plain, more);
			CollectionAssert.AreEqual(new[] { "+ Save", "- attributes: NoLog" }, Probe("Save\n-attributes: NoLog").RuleTexts);
		}

		[TestMethod]
		public void DiagnosticsAreAttributedToTheirRuleLinesTest()
		{
			var outcome = Probe("# comment\nfoo: bar\nSave\nbaz: x\n& method: y");

			CollectionAssert.AreEqual(new[] { "AG0204", "AG0204", "AG0202" }, outcome.Ids);
			CollectionAssert.AreEqual(new[] { 0, 2, 3 }, outcome.DiagnosticRuleLines);

			// Two diagnostics of one id for two lines keep their order.
			CollectionAssert.AreEqual(new[] { 0, 1 }, Probe("foo: a\nfoo: b").DiagnosticRuleLines);
			CollectionAssert.AreEqual(new[] { 0, 1 }, Probe("bar: a\nbaz: b").DiagnosticRuleLines);
		}

		// ---- how the target factory describes a method, as far as the patterns can see it ----

		[TestMethod]
		public void RefReadOnlyParameterIsDescribedAsAByValueParameterTest()
		{
			// Possible defect: "ref readonly" is not mapped, so the parameter looks like an ordinary one in the
			// signature and in the parameter patterns.
			var peek = TargetFilterTargets.Entries[Index("public System.Int32 Acme.Core.Service.Peek(System.Int32)")];

			Assert.AreEqual(AspectFilters.ParameterModifier.None, peek.Target.Parameters[0].Modifier);
			Assert.AreEqual(Microsoft.CodeAnalysis.RefKind.RefReadOnlyParameter, RefKindOf(peek));
		}

		[TestMethod]
		public void MultiDimensionalArrayIsDescribedAsAnArrayTest()
		{
			// Possible defect: the rank is dropped, so int[,] and int[] print alike.
			Assert.IsTrue(TargetFilterTargets.Entries.Any(static e => e.Target.Signature == "public System.Int32[] Acme.Core.Service.Matrix()"));
			Assert.AreEqual('i', Kind(Probe("returns: int[]"), "public System.Int32[] Acme.Core.Service.Matrix()"));
		}

		[TestMethod]
		public void UnreducedExtensionMethodReceiverIsTheContainingTypeTest()
		{
			// The generator sees the reduced form; for the unreduced symbol the receiver prints as the static class.
			Assert.IsTrue(TargetFilterTargets.Entries.Any(static e => e.Target.Signature == "public static System.String Acme.Core.Extensions.Shout(this Acme.Core.Extensions)"));
			Assert.IsTrue(TargetFilterTargets.Entries.Any(static e => e.Target.Signature == "public static System.String Acme.Core.Extensions.Shout(this System.String)"));
		}

		[TestMethod]
		public void ReducedGenericExtensionMethodKeepsItsTypeParametersTest()
		{
			// Possible defect: the signature of a reduced generic extension method names the type parameters, not the
			// type arguments of the call (the receiver is substituted, the method and its return type are not).
			Assert.IsTrue(TargetFilterTargets.Entries.Any(static e => e.Target.Signature == "public static System.Collections.Generic.IEnumerable<T> Acme.Core.Extensions.Take<T>(this System.Collections.Generic.IEnumerable<System.Char>,System.Int32)"));
		}

		static Microsoft.CodeAnalysis.RefKind RefKindOf(TargetFilterTargets.Entry entry)
		{
			return entry.Symbol.Parameters[0].RefKind;
		}
	}
}
