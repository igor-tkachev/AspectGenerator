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
				("Peek(ref readonly System.Int32)",                      "Host.Peek<>;55"),
				("method: Host.Peek and params: (ref readonly int)",     "Host.Peek<>;55"),
				("Host.Peek(int)",                                       "Host.Peek<>;55"), // a parameter pattern without a modifier accepts any modifier
				("Host.Peek(in int)",                                    "55"),
				("Host.Peek(ref int)",                                   "55"),
				("Host.Peek(out int)",                                   "55"), // a written kind must match exactly
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
				("Matrix()",                         "Host.Matrix<>;1"),
				("Host.Matrix() : int[,]",           "Host.Matrix<>;1"),
				("Host.Matrix() : int[]",            "1"),
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
				("Shout(this System.String)",                 "Exts.Shout<>;ab!"),
				("Exts.Shout(this System.String)",            "Exts.Shout<>;ab!"),
				("params: (this System.String)",              "Exts.Shout<>;ab!"),
				("Shout(this Exts)",                          "ab!"),
				("params: (this Exts, ..)",                   "ab!"),
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
			const string unrelated = "namespace: NoSuchNamespace.**";

			// A condition describes the call, with its type arguments. Where a pattern names a concrete type and the
			// definition has a type parameter there, each call decides; otherwise the definition decides once. The result
			// does not depend on the other filters of the project (an assembly filter on a namespace changes nothing for a
			// type that declares its own location). A type
			// parameter's name in a pattern is an ordinary name: Repository<TEntity> names a type TEntity, not the
			// parameter.
			foreach (var (typeFilter, assemblyFilter, expected) in new[]
			{
				("type: Repository<*>",                    "",         selected),
				("type: Repository<System.String>",        "",         selected),
				("type: Repository<System.String>",        unrelated,  selected),
				("type: Repository<*>",                    unrelated,  selected),
				("type: Repository<System.Int32>",         "",         "7"),
				("type: Repository<System.Int32>",         unrelated,  "7"),
				("type: Repository<TEntity>",              "",         "7"),
				("",                                       "Repository<*>.Get<*>(*)",                                                     selected),
				("",                                       "method: Repository<*>.Get<*> and params: (*)",                                selected),
				("",                                       "method: Repository<System.String>.Get<System.Int32> and params: (System.Int32)", selected),
				("",                                       "System.Collections.Generic.List<System.Int32>.Add(System.Int32)",             "List`1[Int32].Add<>;7"),
				("",                                       "method: System.Collections.Generic.List<System.Int32>.Add",                   "List`1[Int32].Add<>;7"),
				("",                                       "System.Collections.Generic.List<T>.Add(T)",                                   "7"),
			})
				AssertRunsWithResult(
					GenericSource
						.Replace("TYPE_ATTRIBUTE", typeFilter.Length == 0 ? "" : $"[AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = \"{typeFilter}\")]")
						.Replace("ASSEMBLY_ATTRIBUTE", assemblyFilter.Length == 0 ? "" : $"[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = \"{assemblyFilter}\")]"),
					expected);
		}

		[TestMethod]
		public void ReducedGenericExtensionCallOfAReferencedAssemblyIsJudgedInStaticFormTest()
		{
			// Extension methods are judged in their static form, constructed from the call: the instance-form call a.Any() and
			// Enumerable.Any<int>(a) are one target (version 1.1 described the instance form by the reduced symbol, whose
			// method type parameters were not substituted).
			const string instance = "a.Any()";
			const string @static  = "Enumerable.Any<int>(a)";

			foreach (var (call, filter, expected) in new[]
			{
				(instance, "method: Any<System.Int32>",                                                                          "Enumerable.Any<Int32>;True"),
				(@static,  "method: Any<System.Int32>",                                                                          "Enumerable.Any<Int32>;True"),
				(instance, "method: Any<*> and params: (this System.Collections.Generic.IEnumerable<System.Int32>)",             "Enumerable.Any<Int32>;True"),
				(@static,  "method: Any<*> and params: (this System.Collections.Generic.IEnumerable<System.Int32>)",             "Enumerable.Any<Int32>;True"),
				(instance, "method: Any<System.String>",                                                                         "True"),
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
