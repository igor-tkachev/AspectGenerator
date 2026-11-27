using System;
using System.Collections.Immutable;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// Which calls share an interceptor, and how the calls of a generic method declared in the project are decided.
	/// An interceptor emits the aspect list of its first call for every call it intercepts, so calls share one only when
	/// they have the same target and the same aspect list.
	/// </summary>
	public partial class CompilerTests
	{
		[TestMethod]
		public void CallsOfOneTargetWithDifferentAspectListsGetInterceptorsOfTheirOwnTest()
		{
			// The selection gives every call of one non-generic target the same list, so this is checked on the grouping
			// alone: three calls of one method symbol, two of them with the same list.
			var compilation = CSharpCompilation.Create(
				"InterceptorGrouping",
				[CSharpSyntaxTree.ParseText(
					"""
					sealed class FirstAttribute  : System.Attribute { }
					sealed class SecondAttribute : System.Attribute { }

					static class Helper
					{
						public static void Run()
						{
							Run();
							Run();
							Run();
						}
					}
					""")],
				GetMetadataReferences(),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

			var tree   = compilation.SyntaxTrees[0];
			var model  = compilation.GetSemanticModel(tree);
			var calls  = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
			var method = (IMethodSymbol)model.GetSymbolInfo(calls[0]).Symbol!;
			var first  = new AttributeInfo(null, null, null, compilation.GetTypeByMetadataName("FirstAttribute")!,  null, null, null, null);
			var second = new AttributeInfo(null, null, null, compilation.GetTypeByMetadataName("SecondAttribute")!, null, null, null, null);

			var invocations = ImmutableArray.Create(
				new AnalyzedInvocation(calls[0], method, [first]),
				new AnalyzedInvocation(calls[1], method, [second]),
				new AnalyzedInvocation(calls[2], method, [first]));

			Assert.IsFalse(InterceptorGroupComparer.Instance.Equals(invocations[0], invocations[1]));
			Assert.IsFalse(InterceptorGroupComparer.Instance.Equals(invocations[1], invocations[0]));
			Assert.IsTrue (InterceptorGroupComparer.Instance.Equals(invocations[0], invocations[2]));

			var interceptors = InterceptorNamingService.Create(invocations);

			Assert.AreEqual(2, interceptors.Count);

			Assert.AreEqual("Run_Interceptor", interceptors[0].Name);
			CollectionAssert.AreEqual(new[] { first }, interceptors[0].Attributes);
			CollectionAssert.AreEqual(new[] { calls[0], calls[2] }, interceptors[0].Invocations.Select(static i => i.Inv).ToArray());

			Assert.AreEqual("Run_Interceptor_1", interceptors[1].Name);
			CollectionAssert.AreEqual(new[] { second }, interceptors[1].Attributes);
			CollectionAssert.AreEqual(new[] { calls[1] }, interceptors[1].Invocations.Select(static i => i.Inv).ToArray());
		}

		const string PerCallSource =
			"""
			ASSEMBLY_ATTRIBUTES

			TYPE_ATTRIBUTE
			static class Helper
			{
				public static string Get<T>(T value)
				{
					return $"{value}";
				}
			}

			static class Calls
			{
				static string Generic<TItem>(TItem item)
				{
					return Helper.Get(item);
				}

				static string Other<TOther>(TOther other)
				{
					return Helper.Get(other);
				}

				public static string Run()
				{
					return Helper.Get(1) + Helper.Get("a") + Generic(2) + Generic("b") + Other(3);
				}
			}

			namespace AspectGenerator.Tests.GeneratorDriver
			{
				[Aspect(OnBeforeCall = nameof(Before))]
				[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
				sealed class TraceAttribute : Attribute
				{
					public string? TargetFilter { get; set; }

					public static void Before(InterceptInfo info)
					{
						LogAttribute.Log.Append("Trace;");
					}
				}
			}
			""";

		[TestMethod]
		public void GenericMethodOfTheProjectIsDecidedPerCallWhereAPatternNamesAConcreteTypeTest()
		{
			const string log   = "AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = \"FILTER\")";
			const string trace = "AspectGenerator.Tests.GeneratorDriver.Trace(TargetFilter = \"FILTER\")";

			static string Assembly(string attribute, string filter)
			{
				return $"[assembly: {attribute.Replace("FILTER", filter)}]";
			}

			static string Type(string attribute, string filter)
			{
				return $"[{attribute.Replace("FILTER", filter)}]";
			}

			// The calls, in order: Get(1), Get("a"), Get(item) for 2 and for "b" (one call site), Get(other) for 3.
			foreach (var (assemblyAttributes, typeAttribute, expected, interceptorCount) in new[]
			{
				// A rule naming a concrete type argument selects the call with that argument, decided per call: the
				// parameter rule binds every call site of the compilation and judges it by its constructed target.
				// A call in a generic caller is described with the caller's type parameter and is not selected.
				(Assembly(log, "param: System.Int32"), "", "Helper.Get<Int32>;1a2b3", 1),

				// Two rules, two aspects: each call gets the list of its own type arguments.
				(
					Assembly(log, "Helper.Get<System.Int32>(System.Int32)") + Environment.NewLine + Assembly(trace, "Helper.Get<System.String>(System.String)"),
					"",
					"Helper.Get<Int32>;Trace;1a2b3",
					2),

				// A call in a generic caller is described with the caller's own type parameter, which only * matches: the
				// concrete type arguments are excluded to reach those sites alone. Both generic callers (Generic and Other)
				// have such a site, and one generic interceptor runs the aspect for every type argument of the caller.
				(Assembly(log, "param: * and not param: System.Int32 and not param: System.String"), "", "Helper.Get<Int32>;Helper.Get<String>;Helper.Get<Int32>;1a2b3", 1),

				// Two aspects: one selects the call with a concrete type argument, the other the call sites of the generic callers.
				(
					Assembly(log, "param: System.Int32") + Environment.NewLine + Assembly(trace, "param: * and not param: System.Int32 and not param: System.String"),
					"",
					"Helper.Get<Int32>;Trace;Trace;Trace;1a2b3",
					2),

				// A type-level rule decides the same way: the pattern names a concrete type where the definition has a type
				// parameter, so each call decides.
				("", Type(log, "param: System.Int32"), "Helper.Get<Int32>;1a2b3", 1),

				// An aspect whose definition is selected as a whole (the type's method: Get<*>) and another that selects one
				// call: each decides for itself, and the calls of the generic callers share one generic interceptor.
				(Assembly(log, "param: System.Int32"), Type(trace, "method: Get<*>"), "Helper.Get<Int32>;Trace;Trace;Trace;Trace;Trace;1a2b3", 3),
			})
			{
				var result = AssertRunsWithResult(
					PerCallSource
						.Replace("ASSEMBLY_ATTRIBUTES", assemblyAttributes)
						.Replace("TYPE_ATTRIBUTE",      typeAttribute),
					expected);

				var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");

				Assert.AreEqual(interceptorCount, CountOccurrences(interceptors, "public static string Get_Interceptor"), interceptors);
			}
		}
	}
}
