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
			const string unrelated = "contains: NoSuchMethodAnywhere";

			// A method declared in the project is judged by its definition, with its type parameters. A call the
			// definition does not select is judged again by the call's type arguments only when the project's
			// assembly-level filters can also select methods of referenced assemblies; that is one flag for the whole
			// project, so a type-level rule depends on the other filters (an unrelated contains: rule turns it on).
			foreach (var (typeFilter, assemblyFilter, expected) in new[]
			{
				("fulltype: Repository<TEntity>",          "",         selected),
				("fulltype: Repository<System.String>",    "",         "7"),
				("fulltype: Repository<System.String>",    unrelated,  selected),
				("fulltype: Repository<TEntity>",          unrelated,  selected),
				("fulltype: Repository<System.Int32>",     unrelated,  "7"),
				("",                                       "Repository<TEntity>.Get<TKey>(TKey)",                                         selected),
				("",                                       "contains: Repository<TEntity>.Get<TKey>(TKey)",                               selected),
				("",                                       "contains: Repository<System.String>.Get<System.Int32>(System.Int32)",         selected),
				("",                                       "System.Collections.Generic.List<System.Int32>.Add(System.Int32)",             "List`1[Int32].Add<>;7"),
				("",                                       "contains: System.Collections.Generic.List<System.Int32>.Add(System.Int32)",   "List`1[Int32].Add<>;7"),
				("",                                       "contains: System.Collections.Generic.List<T>.Add(T)",                         "7"),
			})
				AssertRunsWithResult(
					GenericSource
						.Replace("TYPE_ATTRIBUTE", typeFilter.Length == 0 ? "" : $"[AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = \"{typeFilter}\")]")
						.Replace("ASSEMBLY_ATTRIBUTE", assemblyFilter.Length == 0 ? "" : $"[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = \"{assemblyFilter}\")]"),
					expected);
		}

		[TestMethod]
		public void ReducedGenericExtensionCallOfAReferencedAssemblyKeepsItsTypeParametersTest()
		{
			// Known exception to "a method of a referenced assembly is judged per call": an instance-form call of a generic
			// extension method is described by the reduced symbol, whose method type parameters are not substituted
			// (Any<TSource>(this IEnumerable<System.Int32>)), while the static form is constructed (Any<System.Int32>(...)).
			const string instance = "a.Any()";
			const string @static  = "Enumerable.Any<int>(a)";

			foreach (var (call, filter, expected) in new[]
			{
				(instance, "contains: Any<System.Int32>",                                                                   "True"),
				(@static,  "contains: Any<System.Int32>",                                                                   "Enumerable.Any<Int32>;True"),
				(instance, "contains: Any<TSource>(this System.Collections.Generic.IEnumerable<System.Int32>)",             "Enumerable.Any<Int32>;True"),
				(@static,  "contains: Any<TSource>(this System.Collections.Generic.IEnumerable<System.Int32>)",             "True"),
			})
				AssertRunsWithResult(
					ReferencedExtensionSource.Replace("FILTER", filter).Replace("CALL", call),
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
			ASSEMBLY_ATTRIBUTE

			TYPE_ATTRIBUTE
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

		const string ReferencedExtensionSource =
			"""
			using System.Linq;

			[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "FILTER")]

			static class Calls
			{
				public static string Run()
				{
					var a = new[] { 1 };

					return $"{CALL}";
				}
			}
			""";
	}
}
