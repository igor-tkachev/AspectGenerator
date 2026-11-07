using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// How a target method is described to a filter, checked through the generator: each source is generated, compiled
	/// with the interceptors and run, and the log shows which calls a filter selected.
	/// </summary>
	public partial class CompilerTests
	{
		[TestMethod]
		public void RefReadOnlyParameterIsSelectedByRefReadOnlyTest()
		{
			foreach (var (filter, expected) in new[]
			{
				("Host.Peek(ref readonly int)",                          "Host.Peek<>;55"),
				("contains: Peek(ref readonly System.Int32)",            "Host.Peek<>;55"),
				("regex: Peek\\\\(ref readonly System\\\\.Int32\\\\)$",  "Host.Peek<>;55"),
				("Host.Peek(int)",                                       "Host.Peek<>;55"), // a parameter pattern without a modifier accepts any modifier
				("Host.Peek(in int)",                                    "55"),
				("Host.Peek(ref int)",                                   "55"),
				("contains: Peek(System.Int32)",                         "55"),
			})
				AssertRunsWithResult(
					RefReadOnlySource.Replace("FILTER", filter),
					expected);
		}

		[TestMethod]
		public void MultiDimensionalArrayIsSelectedByItsRankTest()
		{
			foreach (var (filter, expected) in new[]
			{
				("returns: int[,]",                  "Host.Matrix<>;1"),
				("returns: System.Int32[,]",         "Host.Matrix<>;1"),
				("returns: int[]",                   "Host.Vector<>;1"),
				("Host.Fill(int[,], int[][,])",      "Host.Fill<>;1"),
				("Host.Fill(int[][,], int[,])",      "1"),
				("Host.Fill(int[,], int[,][])",      "1"),
				("Host.Fill(int[], int[][])",        "1"),
				("contains: Matrix()",               "Host.Matrix<>;1"),
				("contains: Int32[,] Host.Matrix",   "Host.Matrix<>;1"),
				("contains: Int32[] Host.Matrix",    "1"),
			})
				AssertRunsWithResult(
					MultiDimensionalSource.Replace("FILTER", filter),
					expected);
		}

		[TestMethod]
		public void DeclaredExtensionMethodIsSelectedByItsReceiverTypeTest()
		{
			foreach (var (filter, expected) in new[]
			{
				("contains: Shout(this System.String)",       "Exts.Shout<>;ab!"),
				("contains: Exts.Shout(this System.String)",  "Exts.Shout<>;ab!"),
				("regex: \\\\(this System\\\\.String\\\\)$",   "Exts.Shout<>;ab!"),
				("contains: Shout(this Exts)",                "ab!"),
				("regex: \\\\(this Exts[,)]",                  "ab!"),
				("method: Shout",                             "Exts.Shout<>;ab!"),
			})
				AssertRunsWithResult(
					ExtensionSource.Replace("FILTER", filter),
					expected);
		}

		[TestMethod]
		public void GenericMethodDeclaredInTheProjectIsJudgedByItsDefinitionTest()
		{
			const string selected = "Repository`1[String].Get<Int32>;7";

			// A method declared in the project is judged by its definition, with its type parameters. A call the
			// definition does not select is judged again by the call's type arguments only when the filter can also
			// select methods of referenced assemblies (contains: and regex: can; fulltype: with a constructed type
			// cannot). A method of a referenced assembly is judged per call.
			foreach (var (filter, expected) in new[]
			{
				("Repository<TEntity>.Get<TKey>(TKey)",                                         selected),
				("fulltype: Repository<TEntity>",                                               selected),
				("fulltype: Repository<System.String>",                                         "7"),
				("contains: Repository<TEntity>.Get<TKey>(TKey)",                               selected),
				("contains: Repository<System.String>.Get<System.Int32>(System.Int32)",         selected),
				("System.Collections.Generic.List<System.Int32>.Add(System.Int32)",             "List`1[Int32].Add<>;7"),
				("contains: System.Collections.Generic.List<System.Int32>.Add(System.Int32)",   "List`1[Int32].Add<>;7"),
				("contains: System.Collections.Generic.List<T>.Add(T)",                         "7"),
			})
				AssertRunsWithResult(
					GenericSource.Replace("FILTER", filter),
					expected);
		}

		const string RefReadOnlySource =
			"""
			[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "FILTER")]

			static class Host
			{
				public static int Peek(ref readonly int value) { return value; }
				public static int Peek(string value) { return 5; }
			}

			static class Calls
			{
				public static string Run()
				{
					var x = 5;

					return $"{Host.Peek(in x)}{Host.Peek("a")}";
				}
			}
			""";

		const string MultiDimensionalSource =
			"""
			[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "FILTER")]

			static class Host
			{
				public static int[,] Matrix() { return new int[1, 1]; }
				public static int[] Vector() { return new int[1]; }
				public static void Fill(int[,] a, int[][,] b) { }
			}

			static class Calls
			{
				public static string Run()
				{
					Host.Fill(Host.Matrix(), new int[1][,]);

					return $"{Host.Vector().Length}";
				}
			}
			""";

		const string ExtensionSource =
			"""
			[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "FILTER")]

			static class Exts
			{
				public static string Shout(this string text) { return text + "!"; }
			}

			static class Calls
			{
				public static string Run()
				{
					return "ab".Shout();
				}
			}
			""";

		const string GenericSource =
			"""
			[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "FILTER")]

			class Repository<TEntity>
			{
				public TKey Get<TKey>(TKey key) { return key; }
			}

			static class Calls
			{
				public static string Run()
				{
					var list = new System.Collections.Generic.List<int>();

					list.Add(new Repository<string>().Get<int>(7));

					return $"{list[0]}";
				}
			}
			""";
	}
}
