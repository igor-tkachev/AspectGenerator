using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// An error in a filter turns its aspect off in a build, wherever the filter is written. The marker analyzer reads the
	/// same filters, so it marks no call the build does not intercept.
	/// </summary>
	public partial class CompilerTests
	{
		const string MarkerFilterErrorSourceTemplate =
			"""
			using System;
			using AspectGenerator;

			[assembly: AspectGenerator.Tests.GeneratorDriver.FilterAspect(TargetFilter = "method: Save")]

			namespace AspectGenerator.Tests.GeneratorDriver;

			[Aspect(OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
			sealed class FilterAspectAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static void After(InterceptInfo<string> info)
				{
					info.ReturnValue += " filtered";
				}
			}

			static class Target
			{
				public static string Save()
				{
					return "save";
				}

				public static string Invoke()
				{
					LOCAL_CALL
					return Save();

					LOCAL_DECL
				}
			}

			TYPE_DECL
			""";

		[TestMethod]
		public void MarkersMatchInterceptorsWhenATypeWithoutMethodsHasAFilterErrorTest()
		{
			// The type declares no method, so only the filter reading that precedes the selection reaches its filter.
			var source = MarkerFilterErrorSourceTemplate
				.Replace("LOCAL_CALL", "", StringComparison.Ordinal)
				.Replace("LOCAL_DECL", "", StringComparison.Ordinal)
				.Replace("TYPE_DECL", "[FilterAspect(TargetFilter = \"regex: [\")] sealed class Empty { }", StringComparison.Ordinal);

			var result = RunGenerator(source);

			Assert.IsTrue(result.Diagnostics.Any(static d => d.Id == AspectDiagnosticID.FilterOldSyntaxOrRepeatedProperty), "The build reports the error.");
			AssertMarkersMatchInterceptors(source);
		}

		[TestMethod]
		public void MarkersMatchInterceptorsWhenALocalFunctionHasAFilterErrorTest()
		{
			var source = MarkerFilterErrorSourceTemplate
				.Replace("LOCAL_CALL", "Inner();", StringComparison.Ordinal)
				.Replace("LOCAL_DECL", "[FilterAspect(TargetFilter = \"regex: [\")] static string Inner() { return \"inner\"; }", StringComparison.Ordinal)
				.Replace("TYPE_DECL", "", StringComparison.Ordinal);

			var result = RunGenerator(source);

			Assert.IsTrue(result.Diagnostics.Any(static d => d.Id == AspectDiagnosticID.FilterOldSyntaxOrRepeatedProperty), "The build reports the error.");
			AssertMarkersMatchInterceptors(source);
		}
	}
}
