using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// The generated target filter parser links, loads and reads a handful of lines. The grammar is the only thing it is
	/// checked against here; the filter semantics are pinned elsewhere.
	/// </summary>
	[TestClass]
	public class TargetFilterParserSmokeTests
	{
		static void AssertLine(string input, string? @operator, string? matcherPrefix, string text)
		{
			var match = TargetFilterParser.TryParseLine(input);

			Assert.IsTrue(match.IsSuccess, $"'{input}' was refused: {match.Error}");

			var line = match.Value;

			Assert.AreEqual(@operator,      line.Operator,      $"operator of '{input}'");
			Assert.AreEqual(matcherPrefix,  line.MatcherPrefix, $"matcher prefix of '{input}'");
			Assert.AreEqual(text,           line.Text,          $"text of '{input}'");
		}

		[TestMethod]
		public void PlainLineIsTheRestTest()
		{
			AssertLine("Save",           null, null, "Save");
			AssertLine("Acme.Core.*",    null, null, "Acme.Core.*");
			AssertLine("",               null, null, "");
		}

		[TestMethod]
		public void OperatorsAreTakenOffTheFrontTest()
		{
			AssertLine("-Save",          "-", null, "Save");
			AssertLine("&Save",          "&", null, "Save");
			AssertLine("|Save",          "|", null, "Save");
		}

		[TestMethod]
		public void MatcherPrefixesAreTakenAfterTheOperatorTest()
		{
			AssertLine("pattern:Save*",      null, "pattern:",  "Save*");
			AssertLine("contains:Save",      null, "contains:", "Save");
			AssertLine("regex:^Save\\d+$",   null, "regex:",    "^Save\\d+$");
			AssertLine("-contains:Save",     "-",  "contains:", "Save");
			AssertLine("&regex:Save",        "&",  "regex:",    "Save");
		}

		[TestMethod]
		public void UnknownPrefixStaysInTheRestTest()
		{
			AssertLine("other:Save",   null, null, "other:Save");
			AssertLine("--Save",       "-",  null, "-Save");
		}

		[TestMethod]
		public void LineBreakIsRefusedTest()
		{
			Assert.IsFalse(TargetFilterParser.TryParseLine("Save\nPing").IsSuccess);
		}
	}
}
