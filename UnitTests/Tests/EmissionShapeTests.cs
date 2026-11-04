using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// Call shapes whose interceptors used to be emitted but did not compile. Each source is generated, compiled with the
	/// interceptors, loaded and run: the log shows which calls were intercepted and which method info the hooks saw.
	/// </summary>
	public partial class CompilerTests
	{
		[TestMethod]
		public void GenericCallerTypeArgumentsTest()
		{
			var interceptors = AssertRuns(
				"""
				static class Helper
				{
					[Log]
					public static T2 Pick<T1,T2>(Func<T1,T2> f, T1 value) => f(value);
				}

				static class Calls
				{
					static string Generic<TSource>(TSource value) => Helper.Pick(new Func<TSource,string>(v => $"{v}"), value);

					public static string Run() => Generic("a") + Generic(1);
				}
				""",
				"Helper.Pick<String,String>;Helper.Pick<Int32,String>;a1");

			// One generic interceptor serves every call whose type arguments are the caller's type parameters.
			StringAssert.Contains(interceptors, "Pick_Interceptor<T1, T2>(global::System.Func<T1, T2> f, T1 value)");
			Assert.AreEqual(1, CountOccurrences(interceptors, "public static T2 Pick_Interceptor"), interceptors);
		}

		[TestMethod]
		public void GenericInstanceMethodTest()
		{
			AssertRuns(
				"""
				sealed class Node
				{
					public Node? Next;
					public string Text = "";

					[Log]
					public void ForEach<TContext>(TContext context, Action<TContext,Node> action)
					{
						action(context, this);
						Next?.ForEach(context, action);
					}
				}

				static class Calls
				{
					static string Walk<TContext>(Node node, TContext context)
					{
						var text = "";
						node.ForEach(context, (c, n) => text += $"{c}{n.Text}");
						return text;
					}

					public static string Run() => Walk(new Node { Text = "a", Next = new Node { Text = "b" } }, 1);
				}
				""",
				"Node.ForEach<Int32>;1a1b");
		}

		[TestMethod]
		public void RecursiveGenericCallIsReportedAndSkippedTest()
		{
			// The compiler rejects a generic interceptor for a call whose type arguments are the target's own type
			// parameters (a generic method calling itself, a generic type calling its own members); such calls are skipped.
			var result = AssertRunsWithResult(
				"""
				sealed class Node
				{
					public Node? Next;

					[Log]
					public string Each<TContext>(TContext context) => Next == null ? $"{context}" : Next.Each(context);
				}

				class Store<T>
				{
					public string Items = "";

					[Log]
					public void Add(T item) => Items += item;

					public void AddTwice(T item)
					{
						Add(item);
						Add(item);
					}
				}

				static class Calls
				{
					public static string Run()
					{
						var store = new Store<int>();
						store.AddTwice(1);
						return new Node { Next = new Node() }.Each(1) + store.Items;
					}
				}
				""",
				"Node.Each<Int32>;111");

			var diagnostics = result.Diagnostics.Where(static d => d.Id == AspectDiagnosticID.CannotIntercept).ToArray();

			Assert.AreEqual(3, diagnostics.Length);
			Assert.IsTrue(diagnostics.All(static d => d.GetMessage().Contains("own type parameters", StringComparison.Ordinal)));
		}

		[TestMethod]
		public void RemovedConditionalCallIsReportedAndSkippedTest()
		{
			var result = AssertRunsWithResult(
				"""
				static class Helper
				{
					public static string Text = "";

					[Log]
					[System.Diagnostics.Conditional("NOT_DEFINED")]
					public static void Trace(string text) => Text += text;
				}

				static class Calls
				{
					public static string Run()
					{
						Helper.Trace("x");
						return "<" + Helper.Text + ">";
					}
				}
				""",
				"<>");

			StringAssert.Contains(result.Diagnostics.Single(static d => d.Id == AspectDiagnosticID.CannotIntercept).GetMessage(), "Conditional");
		}

		[TestMethod]
		public void GenericContainingTypeArgumentsTest()
		{
			AssertRuns(
				"""
				class Store<T>
				{
					public string Items = "";

					[Log]
					public void Add(T item) => Items += item;
				}

				class Outer<A>
				{
					public class Inner<B>
					{
						[Log]
						public static string Get<C>(A a, B b, C c) => $"{a}{b}{c}";
					}
				}

				class User<U>
				{
					public string Go(Store<U> store, U item)
					{
						store.Add(item);
						return store.Items + Outer<U>.Inner<int>.Get<U>(item, 2, item);
					}
				}

				static class Calls
				{
					public static string Run() => new User<string>().Go(new Store<string>(), "x");
				}
				""",
				"Store`1[String].Add<>;Inner`1[String,Int32].Get<String>;xx2x");
		}

		[TestMethod]
		public void GenericConstraintsTest()
		{
			var interceptors = AssertRuns(
				"""
				static class Helper
				{
					[Log] public static T Make<T>() where T : class, new() => new T();
					[Log] public static int Size<T>(T value) where T : unmanaged => System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
					[Log] public static string Max<T>(T a, T b) where T : notnull, IComparable<T> => (a.CompareTo(b) >= 0 ? a : b).ToString()!;
					[Log] public static T? Opt<T>(T? value) where T : struct => value;
				}

				class Item
				{
					public override string ToString() => "item";
				}

				static class Calls
				{
					static string Generic<TItem,TValue,TKey>(TValue value, TKey a, TKey b)
						where TItem : class, new()
						where TValue : unmanaged
						where TKey : notnull, IComparable<TKey>
					{
						return $"{Helper.Make<TItem>()}{Helper.Size(value)}{Helper.Max(a, b)}{Helper.Opt<TValue>(value)}";
					}

					public static string Run() => Generic<Item,long,string>(5, "a", "b");
				}
				""",
				"Helper.Make<Item>;Helper.Size<Int64>;Helper.Max<String>;Helper.Opt<Int64>;item8b5");

			StringAssert.Contains(interceptors, "where T : class, new()");
			StringAssert.Contains(interceptors, "where T : unmanaged");
			StringAssert.Contains(interceptors, "where T : notnull, global::System.IComparable<T>");
			StringAssert.Contains(interceptors, "where T : struct");
		}

		[TestMethod]
		public void GenericReducedExtensionCallTest()
		{
			AssertRuns(
				"""
				static class Extensions
				{
					[Log]
					public static int Count2<T>(this System.Collections.Generic.IEnumerable<T> items) => System.Linq.Enumerable.Count(items) * 2;
				}

				static class Calls
				{
					static int Generic<T>(T[] items) => items.Count2();

					public static string Run() => $"{Generic(new[] { 1, 2 })}";
				}
				""",
				"Extensions.Count2<Int32>;4");
		}

		[TestMethod]
		public void UnnameableTypeArgumentsTest()
		{
			AssertRuns(
				"""
				static class Helper
				{
					[Log]
					public static string Echo<T>(T value) => $"{value}";
				}

				static class Calls
				{
					class Hidden
					{
						public override string ToString() => "hidden";
					}

					public static string Run() => Helper.Echo(new { A = 1 }) + Helper.Echo(new Hidden());
				}
				""",
				"Helper.Echo<<>f__AnonymousType0`1>;Helper.Echo<Hidden>;{ A = 1 }hidden");
		}

		[TestMethod]
		public void MultiLineCallInGeneratedCommentTest()
		{
			// The call and the aspect application are echoed into comments of the generated file; a line break in either
			// used to end the comment and turn the rest of the text into code.
			AssertRuns(
				""""
				[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = """
					method: Text
					""")]

				static class Helper
				{
					public static string Text(object value, int flag, bool escape = false) => $"{value}{flag}{escape}";
				}

				class Table
				{
				}

				static class Calls
				{
					public static string Run()
					{
						object table = new Table();

						return Helper.Text(
							new string('a', 1),
							table is Table sqlTable ? 1 : 0,
							escape : true);
					}
				}
				"""",
				"Helper.Text<>;a1True");
		}

		[TestMethod]
		public void StaticFormExtensionCallTest()
		{
			AssertRuns(
				"""
				static class Extensions
				{
					[Log]
					public static int Twice(this string text) => text.Length * 2;
				}

				static class Calls
				{
					public static string Run() => $"{Extensions.Twice("ab")}{"abc".Twice()}";
				}
				""",
				"Extensions.Twice<>;Extensions.Twice<>;46");
		}

		[TestMethod]
		public void InaccessibleTargetIsReportedAndSkippedTest()
		{
			var result = AssertRunsWithResult(
				"""
				class Base
				{
					[Log]
					protected static string Protected() => "p";

					[Log]
					public static string Public() => "q";
				}

				class Derived : Base
				{
					public string Go() => Protected() + Public();
				}

				static class Calls
				{
					public static string Run() => new Derived().Go();
				}
				""",
				"Base.Public<>;pq");

			var diagnostic = result.Diagnostics.Single(static d => d.Id == AspectDiagnosticID.CannotIntercept);

			Assert.AreEqual(DiagnosticSeverity.Warning, diagnostic.Severity);
			StringAssert.Contains(diagnostic.GetMessage(), "not accessible");
		}

		[TestMethod]
		public void FilterSelectedInaccessibleTargetIsInformationTest()
		{
			var result = AssertRunsWithResult(
				"""
				[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "method: Protected")]

				class Base
				{
					protected static string Protected() => "p";
				}

				class Derived : Base
				{
					public string Go() => Protected();
				}

				static class Calls
				{
					public static string Run() => new Derived().Go();
				}
				""",
				"p");

			Assert.AreEqual(DiagnosticSeverity.Info, result.Diagnostics.Single(static d => d.Id == AspectDiagnosticID.CannotIntercept).Severity);
		}

		[TestMethod]
		public void LocalFunctionIsReportedAndSkippedTest()
		{
			var result = AssertRunsWithResult(
				"""
				[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "method: Inner")]

				static class Calls
				{
					public static string Run()
					{
						return Inner();

						static string Inner() => "inner";
					}
				}
				""",
				"inner");

			StringAssert.Contains(result.Diagnostics.Single(static d => d.Id == AspectDiagnosticID.CannotIntercept).GetMessage(), "local functions");
		}

		[TestMethod]
		public void ExpressionTreeCallIsReportedAndSkippedTest()
		{
			var result = AssertRunsWithResult(
				"""
				static class Helper
				{
					[Log]
					public static bool IsOk(int value) => value > 1;
				}

				static class Calls
				{
					public static string Run()
					{
						System.Linq.Expressions.Expression<Func<int,bool>> expression = v => Helper.IsOk(v);

						var query =
							from v in new[] { 1, 2, 3 }.AsQueryable()
							where Helper.IsOk(v)
							select v;

						return $"{expression.Body.NodeType}{query.Count()}{Helper.IsOk(5)}";
					}
				}
				""",
				"Helper.IsOk<>;Call2True");

			var diagnostics = result.Diagnostics.Where(static d => d.Id == AspectDiagnosticID.CannotIntercept).ToArray();

			Assert.AreEqual(2, diagnostics.Length);
			Assert.IsTrue(diagnostics.All(static d => d.GetMessage().Contains("expression trees", StringComparison.Ordinal)));
		}

		[TestMethod]
		public void BaseCallIsReportedAndSkippedTest()
		{
			var result = AssertRunsWithResult(
				"""
				class Base
				{
					[Log]
					public virtual string Name() => "base";
				}

				sealed class Derived : Base
				{
					public override string Name() => "derived+" + base.Name();
				}

				static class Calls
				{
					public static string Run() => ((Base)new Derived()).Name();
				}
				""",
				"Base.Name<>;derived+base");

			StringAssert.Contains(result.Diagnostics.Single(static d => d.Id == AspectDiagnosticID.CannotIntercept).GetMessage(), "base calls");
		}

		[TestMethod]
		public void OutParameterTest()
		{
			AssertRuns(
				"""
				static class Helper
				{
					[Log]
					public static bool TryGet(int key, out string value)
					{
						value = $"v{key}";
						return true;
					}
				}

				[Log(TargetFilter = "method: Deconstruct")]
				sealed record Person(string Name);

				static class Calls
				{
					public static string Run()
					{
						new Person("n").Deconstruct(out var name);

						return Helper.TryGet(1, out var value) ? value + name : "";
					}
				}
				""",
				"Person.Deconstruct<>;Helper.TryGet<>;v1n");
		}

		[TestMethod]
		public void NonGenericTaskTargetTest()
		{
			AssertRuns(
				"""
				static class Helper
				{
					public static string Done = "";

					[Log]
					public static System.Threading.Tasks.Task RunAsync()
					{
						Done = "done";
						return System.Threading.Tasks.Task.CompletedTask;
					}
				}

				static class Calls
				{
					public static string Run()
					{
						Helper.RunAsync().GetAwaiter().GetResult();
						return Helper.Done;
					}
				}
				""",
				"Helper.RunAsync<>;done");
		}

		[TestMethod]
		public void DynamicParameterTest()
		{
			AssertRuns(
				"""
				static class Helper
				{
					[Log]
					public static string Describe(dynamic value) => "dynamic";

					public static string Describe(string value) => "string";
				}

				static class Calls
				{
					public static string Run()
					{
						object value = "text";
						return Helper.Describe(value);
					}
				}
				""",
				"Helper.Describe<>;dynamic");
		}

		[TestMethod]
		public void RefStructParametersTest()
		{
			AssertRuns(
				"""
				static class Helper
				{
					[Log]
					public static int Sum(params ReadOnlySpan<int> values)
					{
						var sum = 0;
						foreach (var v in values)
							sum += v;
						return sum;
					}

					[Log]
					public static int First(scoped ReadOnlySpan<int> values) => values[0];

					[Log]
					public static int Width(this ReadOnlySpan<char> text) => text.Length;
				}

				class Buffer
				{
					[Log]
					public int Length(ReadOnlySpan<char> text, string? tag, ref int counter) => text.Length + counter++;
				}

				static class Calls
				{
					public static string Run()
					{
						var counter = 10;
						var length  = new Buffer().Length("abc".AsSpan(), null, ref counter);

						return $"{Helper.Sum(1, 2, 3)}{Helper.First([7])}{"ab".AsSpan().Width()}{length}{counter}";
					}
				}
				""",
				"Buffer.Length<>;Helper.Sum<>;Helper.First<>;Helper.Width<>;6721311");
		}

		[TestMethod]
		public void StructReceiverTest()
		{
			AssertRuns(
				"""
				struct Counter
				{
					int _value;

					[Log]
					public void Increment() => _value++;

					[Log]
					public readonly int Get() => _value;
				}

				static class Calls
				{
					public static string Run()
					{
						var counter = new Counter();
						counter.Increment();
						counter.Increment();
						return $"{counter.Get()}";
					}
				}
				""",
				"Counter.Increment<>;Counter.Increment<>;Counter.Get<>;2");
		}

		static string AssertRuns(string body, string expected)
		{
			return GetGeneratedSourceOrEmpty(AssertRunsWithResult(body, expected), "Interceptors.g.cs");
		}

		static GeneratorDriverRunResult AssertRunsWithResult(string body, string expected)
		{
			var source =
				$$"""
				using System;
				using System.Linq;
				using AspectGenerator;
				using AspectGenerator.Tests.GeneratorDriver;

				{{body}}

				namespace AspectGenerator.Tests.GeneratorDriver
				{
					[Aspect(OnBeforeCall = nameof(Before))]
					[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
					sealed class LogAttribute : Attribute
					{
						public string? TargetFilter { get; set; }

						public static readonly System.Text.StringBuilder Log = new();

						public static void Before(InterceptInfo info)
						{
							var method   = (System.Reflection.MethodInfo)info.MemberInfo;
							var type     = method.DeclaringType!;
							var typeArgs = type.IsGenericType ? $"[{string.Join(",", type.GetGenericArguments().Select(static t => t.Name))}]" : "";

							Log.Append($"{type.Name}{typeArgs}.{method.Name}<{string.Join(",", method.GetGenericArguments().Select(static t => t.Name))}>;");
						}
					}
				}
				""";

			var result      = RunGeneratorAndGetCompilation(source, out var compilation);
			var errors      = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();
			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");

			Assert.AreEqual(0, errors.Length, string.Join(Environment.NewLine, errors.Select(static e => e.ToString())) + Environment.NewLine + interceptors);

			using var stream = new MemoryStream();

			var emit = compilation.Emit(stream);

			Assert.IsTrue(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics) + Environment.NewLine + interceptors);

			var context = new AssemblyLoadContext("EmissionShapeTests", isCollectible: true);

			try
			{
				stream.Position = 0;

				var assembly = context.LoadFromStream(stream);
				var run      = assembly.GetType("Calls")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
				var value    = (string)run.Invoke(null, null)!;
				var log      = assembly.GetType("AspectGenerator.Tests.GeneratorDriver.LogAttribute")!.GetField("Log")!.GetValue(null)!.ToString();

				Assert.AreEqual(expected, log + value, interceptors);
			}
			finally
			{
				context.Unload();
			}

			return result;
		}
	}
}
