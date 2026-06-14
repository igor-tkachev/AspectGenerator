using System.Collections.Immutable;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	[TestClass]
	public class AspectFilterTests
	{
		[TestMethod]
		public void StringFilterSplitsLinesAndSkipsCommentsTest()
		{
			var filters = AspectFilters.GetFilters(
				"""
				# comment
				contains: Save

				-contains: HealthCheck
				""");

			Assert.IsTrue(filters.IsMatch("public System.Void MyApp.UserService.Save()"));
			Assert.IsFalse(filters.IsMatch("public System.Void MyApp.UserService.HealthCheck()"));
		}

		[TestMethod]
		public void ArrayFilterConcatenatesItemsTest()
		{
			var filters = AspectFilters.GetFilters(
				new[]
				{
					"contains: Save",
					"""
					# comment
					-contains: HealthCheck
					"""
				});

			Assert.IsTrue(filters.IsMatch("public System.Void MyApp.UserService.Save()"));
			Assert.IsFalse(filters.IsMatch("public System.Void MyApp.UserService.HealthCheck()"));
		}

		[TestMethod]
		public void ArrayFilterCacheUsesItemValuesTest()
		{
			var first = AspectFilters.GetFilters(
				new[]
				{
					"contains: Save",
					"-contains: HealthCheck"
				});
			var second = AspectFilters.GetFilters(
				new[]
				{
					"contains: Save",
					"-contains: HealthCheck"
				});

			Assert.IsTrue(first.IsMatch("public System.Void MyApp.UserService.Save()"));
			Assert.IsFalse(second.IsMatch("public System.Void MyApp.UserService.HealthCheck()"));
		}

		[TestMethod]
		public void MatcherPrefixesAreAppliedTest()
		{
			var filters = AspectFilters.GetFilters(
				[
					"regex: .*UserService\\.Save\\(\\)$",
					"-contains: HealthCheck"
				]);

			Assert.IsTrue(filters.IsMatch("public System.Void MyApp.UserService.Save()"));
			Assert.IsFalse(filters.IsMatch("public System.Void MyApp.UserService.HealthCheck()"));
			Assert.IsFalse(filters.IsMatch("public System.Void MyApp.UserService.Load()"));
		}

		[TestMethod]
		public void InvalidRegexIsReportedTest()
		{
			var reported = ImmutableArray.CreateBuilder<string>();
			var filters  = AspectFilters.GetFilters("regex: [", (pattern, message) => reported.Add($"{pattern}: {message}"));

			Assert.IsTrue(filters.IsEmpty);
			Assert.AreEqual(1, reported.Count);
			StringAssert.StartsWith(reported[0], "[: ");
		}

		[TestMethod]
		public void LastMatchingRuleWinsTest()
		{
			var filters = AspectFilters.GetFilters(
				[
					"contains: Service",
					"-contains: HealthCheck",
					"contains: Save"
				]);

			Assert.IsTrue(filters.IsMatch("public System.Void MyApp.UserService.Save()"));
			Assert.IsFalse(filters.IsMatch("public System.Void MyApp.UserService.HealthCheck()"));
			Assert.IsFalse(filters.IsMatch("public System.Void MyApp.Controller.Index()"));
		}

		[TestMethod]
		public void PatternRulesAreAcceptedButNotCompiledYetTest()
		{
			var filters = AspectFilters.GetFilters("pattern: public MyApp.Services.*");

			Assert.IsTrue(filters.IsEmpty);
		}
	}
}
