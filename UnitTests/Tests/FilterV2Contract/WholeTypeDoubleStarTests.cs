using System;
using System.Collections.Generic;

using AspectGenerator.FilterV2;
using AspectGenerator.TargetFilterBinding;
using AspectGenerator.TargetFilterFacts;
using AspectGenerator.Tests.TargetFilterBinding;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.FilterV2Contract
{
	/// <summary>
	/// <c>**</c> written as the whole of a type pattern is open to two readings, and the compiler of patterns takes the
	/// choice as a parameter: a name pattern (an array or a pointer is no name, so none matches; a tuple is a named type), or any type
	/// like <c>*</c>. The default is the first. Every row is run under both readings; <c>System.**.Task</c> and every
	/// other pattern read the same under both.
	/// </summary>
	[TestClass]
	public class WholeTypeDoubleStarTests
	{
		// A copy of the constant, so that what is asserted about it is not folded away at compile time.
		static readonly bool DefaultReading = PatternDefaults.WholeTypeDoubleStarIsAnyType;

		const bool Name = false;
		const bool Any  = true;

		static bool Matches(string pattern, string type, bool doubleStarIsAnyType)
		{
			var reading = FilterV2Reader.Read("returns: " + pattern);

			Assert.AreEqual(0, reading.Diagnostics.Length, $"'{pattern}': {string.Join("; ", reading.Diagnostics)}");

			var returns = (ReturnsPatternSyntax)((KeyConditionSyntax)reading.Item!.Lines[0].Expression).Value;
			var compiled = PatternCompiler.CompileType(returns.Type, doubleStarIsAnyType);

			return compiled.Match(ShapeText.Parse(type), MatchPhase.Call) == Tri.True;
		}

		/// <summary>Where <c>**</c> reads as a name, and where as any type: the same row, so the answers sit side by side.</summary>
		static readonly (string Pattern, string Type, bool AsName, bool AsAny)[] Rows =
		[
			// Named types: both readings match.
			("**",                 "int",                                    true,  true),
			("**",                 "string",                                 true,  true),
			("**",                 "System.Threading.Tasks.Task",            true,  true),
			("**",                 "System.Collections.Generic.List<int>",   true,  true),

			// What is no name: an array of any rank. Only the reading as any type matches it. A tuple is the named type
			// ValueTuple<...> to a pattern of names, so a name pattern matches it too.
			("**",                 "int[]",                                  false, true),
			("**",                 "int[,]",                                 false, true),
			("**",                 "string[][]",                             false, true),
			("**",                 "(int, string)",                          true,  true),

			// The same with `?`.
			("**?",                "string",                                 true,  true),
			("**?",                "int",                                    false, false),
			("**?",                "System.Nullable<int>",                   true,  true),
			("**?",                "int[]",                                  false, true),
			("**?",                "(int, string)",                          false, false),

			// A type argument is a type pattern of its own.
			("List<**>",           "System.Collections.Generic.List<int>",   true,  true),
			("List<**>",           "System.Collections.Generic.List<int[]>", false, true),
			("List<**>",           "System.Collections.Generic.List<(int, string)>", true, true),

			// A name with `**` in it, or beside other types, is a name or alternatives either way.
			("System.**.Task",     "System.Threading.Tasks.Task",            true,  true),
			("System.**.Task",     "int[]",                                  false, false),
			("**.Task",            "System.Threading.Tasks.Task",            true,  true),
			("**.Task",            "int[]",                                  false, false),
			("{**, int}",          "int[]",                                  false, true),
			("{**, int}",          "string",                                 true,  true),
			("global::**",         "int[]",                                  false, false),

			// `*` is any type under both.
			("*",                  "int[]",                                  true,  true),
			("*",                  "(int, string)",                          true,  true),
		];

		[TestMethod]
		public void BothReadingsOfADoubleStarTypePatternTest()
		{
			var failures = new List<string>();

			foreach (var (pattern, type, asName, asAny) in Rows)
			{
				if (Matches(pattern, type, Name) != asName)
					failures.Add($"'{pattern}' on {type} as a name: expected {asName}");

				if (Matches(pattern, type, Any) != asAny)
					failures.Add($"'{pattern}' on {type} as any type: expected {asAny}");
			}

			Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
		}

		[TestMethod]
		public void ReadAsAnyTypeADoubleStarIsAStarTest()
		{
			var types = new[] { "int", "string", "System.Nullable<int>", "int[]", "int[,]", "(int, string)", "System.Threading.Tasks.Task", "System.Collections.Generic.List<int[]>", "!T" };

			foreach (var type in types)
			{
				foreach (var (doubleStar, star) in new[] { ("**", "*"), ("**?", "*?") })
				{
					var shape = ShapeText.Parse(type);

					Assert.AreEqual(
						PatternCompiler.CompileType(Syntax(star), Name).Match(shape, MatchPhase.Call),
						PatternCompiler.CompileType(Syntax(doubleStar), Any).Match(shape, MatchPhase.Call),
						$"'{doubleStar}' on {type}, as any type, against '{star}'");

					Assert.AreEqual(
						PatternCompiler.CompileType(Syntax(star), Name).Match(shape, MatchPhase.Definition),
						PatternCompiler.CompileType(Syntax(doubleStar), Any).Match(shape, MatchPhase.Definition),
						$"'{doubleStar}' on {type}, as any type, on a definition, against '{star}'");
				}
			}
		}

		[TestMethod]
		public void TheDefaultIsTheReadingAsANameTest()
		{
			// Igor has not answered; the default stays what the language did before the question was asked.
			Assert.IsFalse(DefaultReading);
			Assert.AreEqual(Matches("**", "int[]", Name), Matches("**", "int[]", DefaultReading));
		}

		static TypePatternSyntax Syntax(string pattern)
		{
			var reading = FilterV2Reader.Read("returns: " + pattern);

			return ((ReturnsPatternSyntax)((KeyConditionSyntax)reading.Item!.Lines[0].Expression).Value).Type;
		}
	}
}
