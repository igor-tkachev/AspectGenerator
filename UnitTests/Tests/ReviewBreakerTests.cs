using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	public partial class CompilerTests
	{
		// ---- 1. generic interceptor shapes ----

		[TestMethod]
		public void RB_GenericGroupUsesFirstCallAspectsTest()
		{
			// Tag selects only List<Hidden>.Add (filter over the constructed signature of a referenced method);
			// Log selects every List<*>.Add. Both calls need a generic interceptor and share one.
			Breaker(
				"""
				[assembly: AspectGenerator.Tests.GeneratorDriver.Tag(TargetFilter = "contains: Hidden>.Add")]
				[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = "regex: List<[^>]+>\\.Add\\(")]

				static class Calls
				{
					class Hidden { public override string ToString() => "h"; }

					static void AddHidden(System.Collections.Generic.List<Hidden> list) => list.Add(new Hidden());
					static void AddGeneric<T>(System.Collections.Generic.List<T> list, T item) => list.Add(item);

					public static string Run()
					{
						AddHidden(new());
						AddGeneric(new System.Collections.Generic.List<int>(), 1);
						return "";
					}
				}
				""",
				null,
				expectedValue: "",
				checkLog: static log =>
				{
					// The List<Hidden> call must get Tag and Log, the List<T> call only Log.
					Assert.AreEqual(1, CountOccurrences(log, "Tag:"), "Tag applied to a call its filter did not select: " + log);
					Assert.AreEqual(2, CountOccurrences(log, ".Add<>;"), log);
				});
		}

		[TestMethod]
		public void RB_ExtensionStaticFormThenReducedInGenericTest()
		{
			Breaker(
				"""
				static class Ext
				{
					[Log] public static int Twice<T>(this System.Collections.Generic.IEnumerable<T> items) => System.Linq.Enumerable.Count(items) * 2;
				}

				static class Calls
				{
					static int A<T>(T[] items) => Ext.Twice<T>(items);
					static int B<T>(T[] items) => items.Twice();

					public static string Run() => $"{A(new[] { 1 })}{B(new[] { "a", "b" })}";
				}
				""",
				"Ext.Twice<Int32>;Ext.Twice<String>;24");
		}

		[TestMethod]
		public void RB_ExtensionReducedThenStaticFormInGenericTest()
		{
			Breaker(
				"""
				static class Ext
				{
					[Log] public static int Twice<T>(this System.Collections.Generic.IEnumerable<T> items) => System.Linq.Enumerable.Count(items) * 2;
				}

				static class Calls
				{
					static int B<T>(T[] items) => items.Twice();
					static int A<T>(T[] items) => Ext.Twice<T>(items);

					public static string Run() => $"{B(new[] { "a", "b" })}{A(new[] { 1 })}";
				}
				""",
				"Ext.Twice<String>;Ext.Twice<Int32>;42");
		}

		[TestMethod]
		public void RB_AllowsRefStructGenericTest()
		{
			Breaker(
				"""
				static class Helper
				{
					[Log] public static string Name<T>(T value) where T : allows ref struct => typeof(T).Name;
				}

				static class Calls
				{
					static string G<T>(T value) where T : allows ref struct => Helper.Name(value);

					public static string Run()
					{
						Span<int> span = stackalloc int[1];
						return G(1) + Helper.Name(span) + G(span);
					}
				}
				""",
				"Helper.Name<Int32>;Helper.Name<Span`1>;Helper.Name<Span`1>;Int32Span`1Span`1");
		}

		[TestMethod]
		public void RB_NestedGenericInstanceWithCrossConstraintsTest()
		{
			Breaker(
				"""
				class Outer<A> where A : class
				{
					public class Inner<B> where B : struct, IComparable<B>
					{
						public string Tag = "i";

						[Log]
						public string M<C, D>(C c, D d) where C : A where D : System.Collections.Generic.IEnumerable<B>, new() => $"{Tag}{c}{System.Linq.Enumerable.Count(d)}";
					}
				}

				class Base { public override string ToString() => "b"; }
				sealed class Derived : Base { public override string ToString() => "d"; }

				static class Calls
				{
					static string G<X, Y>(Outer<X>.Inner<Y> inner, X x) where X : class where Y : struct, IComparable<Y>
					{
						return inner.M(x, new System.Collections.Generic.List<Y>());
					}

					public static string Run() => G(new Outer<Base>.Inner<int>(), new Derived()) + new Outer<Base>.Inner<long>().M<Derived, System.Collections.Generic.List<long>>(new Derived(), new());
				}
				""",
				"Inner`1[Base,Int32].M<Base,List`1>;Inner`1[Base,Int64].M<Derived,List`1>;id0id0");
		}

		[TestMethod]
		public void RB_ClassNullableAndDefaultConstraintsTest()
		{
			Breaker(
				"""
				#nullable enable
				interface IShape { string Name { get; } }
				static class Helper
				{
					[Log] public static string N1<T>(T? v) where T : class?, IShape? => v?.Name ?? "null";
					[Log] public static string N2<T>(T v) where T : notnull => $"{v}";
					[Log] public static string N3<T, U>(T t, U u) where T : U where U : IShape => t.Name + u.Name;
					[Log] public static string N4<T>(T v) where T : Enum => v.ToString();
					[Log] public static string N5<T>(T v) where T : Delegate => "del";
				}
				sealed class Sq : IShape { public string Name => "sq"; }
				enum E { One }
				static class Calls
				{
					static string G<T, U, V, W>(T t, U u, V v, W w) where T : class, IShape where U : notnull where V : struct, Enum where W : Delegate
						=> Helper.N1(t) + Helper.N2(u) + Helper.N3<T, T>(t, t) + Helper.N4(v) + Helper.N5(w);
					public static string Run() => G(new Sq(), 1, E.One, new Action(() => { }));
				}
				""",
				"Helper.N1<Sq>;Helper.N2<Int32>;Helper.N3<Sq,Sq>;Helper.N4<E>;Helper.N5<Action>;sq1sqsqOnedel");
		}

		// ---- 2. struct receivers ----

		[TestMethod]
		public void RB_StructReceiverSemanticsTest()
		{
			Breaker(
				"""
				struct Counter
				{
					public int Value;
					[Log] public void Inc() => Value++;
					[Log] public readonly int Get() => Value;
				}

				interface IBump { [Log] void Bump(); int Read(); }

				struct Bumper : IBump
				{
					int _v;
					[Log] public void Bump() => _v++;
					public int Read() => _v;
				}

				class Holder
				{
					public readonly Counter Ro;
					public Counter Rw;
					public string Go()
					{
						Ro.Inc();                 // defensive copy: Ro stays 0
						Rw.Inc();                 // field mutated
						return $"{Ro.Value}{Rw.Value}";
					}
				}

				static class Calls
				{
					static Counter Make() => new Counter { Value = 5 };
					static int ByIn(in Counter c) { c.Inc(); return c.Value; }          // copy, stays 0
					static int ByRef(ref Counter c) { c.Inc(); return c.Value; }        // mutated
					static readonly Counter StaticRo = default;
					static Counter[] Arr = new Counter[1];

					static void Generic<T>(ref T t) where T : struct, IBump => t.Bump();

					public static string Run()
					{
						var s  = "";
						s += new Holder().Go();
						Make().Inc();
						var c = new Counter();
						s += ByIn(c);
						s += ByRef(ref c);
						ref var r = ref Arr[0];
						r.Inc();
						Arr[0].Inc();
						s += Arr[0].Value;
						StaticRo.Inc();
						s += StaticRo.Value;
						s += Make().Get();
						Counter? n = new Counter();
						n?.Inc();
						s += n!.Value.Value;
						var b = new Bumper();
						Generic(ref b);
						s += b.Read();
						return s;
					}
				}
				""",
				null,
				expectedValue: "010120501");
		}

		[TestMethod]
		public void RB_ExtensionRefReadonlyThisTest()
		{
			Breaker(
				"""
				struct Counter { public int Value; }
				static class Ext
				{
					[Log] public static void Inc(this ref Counter c) => c.Value++;
					[Log] public static int GetIn(this in Counter c) => c.Value;
					[Log] public static int GetRr(this ref readonly Counter c) => c.Value;
				}
				static class Calls
				{
					public static string Run()
					{
						var c = new Counter();
						c.Inc();
						return $"{c.GetIn()}{c.GetRr()}";
					}
				}
				""",
				null,
				expectedValue: "11");
		}

		// ---- 3. dynamic ----

		[TestMethod]
		public void RB_DynamicOverloadsTest()
		{
			Breaker(
				"""
				static class Helper
				{
					[Log] public static string D(dynamic a, int b) => "dyn";
					public static string D(int a, int b) => "int";
					public static string D(string a, long b) => "str";
					[Log] public static dynamic R(dynamic a) => a;
				}
				static class Calls
				{
					public static string Run()
					{
						object o = 5;
						object s = "x";
						return Helper.D(o, 1) + Helper.D(s, 1) + (string)Helper.R((object)"y");
					}
				}
				""",
				null,
				expectedValue: "dyndyny");
		}

		// ---- 6. call shapes ----

		[TestMethod]
		public void RB_ParamsNamedDefaultsRefReadonlyTest()
		{
			Breaker(
				"""
				static class Helper
				{
					[Log] public static string P(string sep, params int[] values) => string.Join(sep, values);
					[Log] public static string S(params ReadOnlySpan<string> values) => string.Concat(values.ToArray());
					[Log] public static string N(int a, int b = 2, string c = "c", int d = 4) => $"{a}{b}{c}{d}";
					[Log] public static int RR(ref readonly int x) => x;
					[Log] public static int RRV(ref readonly int x, in int y) => x + y;
				}
				static class Calls
				{
					public static string Run()
					{
						int v = 7;
						return Helper.P("-", 1, 2) + Helper.P(",") + Helper.S("a", "b") + Helper.N(d: 9, a: 1) + Helper.N(c: "z", a: 0)
							+ Helper.RR(in v) + Helper.RR(ref v) + Helper.RRV(in v, 1);
					}
				}
				""",
				null,
				expectedValue: "1-2ab12c902z4778");
		}

		[TestMethod]
		public void RB_AsyncShapesTest()
		{
			Breaker(
				"""
				static class Helper
				{
					[AsyncLog] public static async System.Threading.Tasks.ValueTask<int> VT(int x) { await System.Threading.Tasks.Task.Yield(); return x; }
					[AsyncLog] public static async System.Threading.Tasks.ValueTask V() { await System.Threading.Tasks.Task.Yield(); }
					[Log] public static async System.Collections.Generic.IAsyncEnumerable<int> E() { await System.Threading.Tasks.Task.Yield(); yield return 1; }
					[AsyncLog] public static async System.Threading.Tasks.Task<T> GT<T>(T x) { await System.Threading.Tasks.Task.Yield(); return x; }
				}
				static class RefStructExt
				{
					[Log] public static int Len<T>(this Span<T> s) => s.Length;
					[Log] public static int RLen(this ReadOnlySpan<char> s) => s.Length;
				}
				static class Calls
				{
					static async System.Threading.Tasks.Task<string> G<T>(T x)
					{
						var v = await Helper.GT(x);
						var arr = new T[2];
						return $"{v}{arr.AsSpan().Len()}";
					}
					static async System.Threading.Tasks.Task<string> Go()
					{
						var s = "";
						s += await Helper.VT(3);
						await Helper.V();
						await foreach (var i in Helper.E())
							s += i;
						s += await G("q");
						s += "abc".AsSpan().RLen();
						return s;
					}
					public static string Run() => Go().GetAwaiter().GetResult();
				}
				""",
				null,
				expectedValue: "31q23");
		}

		[TestMethod]
		public void RB_CallContextsTest()
		{
			Breaker(
				"""
				static class Helper
				{
					public static string Text = "";
					[Log] public static int Add(int a, int b) => a + b;
					[Log] public static string Echo(string s) { Text += s; return s; }
				}
				class Base { public string B; public Base(string b) { B = b; } }
				sealed class Derived : Base
				{
					public static readonly string SF = Helper.Echo("sf");
					public readonly string F = Helper.Echo("f");
					public static string SC;
					static Derived() { SC = Helper.Echo("sc"); }
					public Derived() : base(Helper.Echo("base")) { }
					public string P => Helper.Echo("p");
				}
				static class Calls
				{
					public static string Run()
					{
						Func<int, int> f = x => Helper.Add(x, 1);
						var d = new Derived();
						var n = nameof(Helper.Echo);
						var c = checked(Helper.Add(1, 2));
						var u = unchecked(Helper.Add(int.MaxValue, 1));
						var sw = c switch { 3 => Helper.Echo("sw"), _ => "" };
						Func<string> local = () => { return Inner(); string Inner() => Helper.Echo("lf"); };
						return $"{f(1)}{d.P}{n}{c}{u}{sw}{local()}|{Helper.Text}";
					}
				}
				""",
				null,
				expectedValue: "2pEcho3-2147483648swlf|sfscfbaseswplf",
				expectedInterceptedCalls: 11);
		}

		[TestMethod]
		[Ignore("Documents a known caching issue: the analysis step re-runs on every compilation, so outputs are re-produced. Addressed by the incremental pipeline work.")]
		public void RB_IncrementalStabilityTest()
		{
			var source = WrapBreaker(
				"""
				static class Helper
				{
					[Log] public static string Echo<T>(T value) => $"{value}";
				}
				static class Calls
				{
					static string G<T>(T v) => Helper.Echo(v);
					public static string Run() => G(1) + Helper.Echo(new { A = 1 }) + Helper.Echo("x");
				}
				""");

			var parseOptions = CSharpParseOptions.Default
				.WithLanguageVersion(LanguageVersion.Preview)
				.WithFeatures([new("InterceptorsNamespaces", "AspectGenerator")]);
			var tree        = CSharpSyntaxTree.ParseText(source, parseOptions, "GeneratorDriverTest.cs");
			var compilation = CSharpCompilation.Create("AspectGenerator.Tests.GeneratorDriver", [tree], GetMetadataReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
			var props       = new Dictionary<string,string> { ["build_property.InterceptorsNamespaces"] = "AspectGenerator", ["build_property.AspectGeneratorPropsImported"] = "true" };

			GeneratorDriver driver = CSharpGeneratorDriver.Create(
				[new AspectSourceGenerator().AsSourceGenerator()],
				parseOptions: parseOptions,
				optionsProvider: new TestAnalyzerConfigOptionsProvider(props),
				driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

			driver = driver.RunGenerators(compilation);
			var first = GetGeneratedSourceOrEmpty(driver.GetRunResult(), "Interceptors.g.cs");

			// Same text, a new tree object (as an IDE edit that is undone), plus an unrelated second file.
			var compilation2 = compilation
				.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(source, parseOptions, "GeneratorDriverTest.cs"))
				.AddSyntaxTrees(CSharpSyntaxTree.ParseText("static class Unrelated { static int X() => 1; }", parseOptions, "Other.cs"));

			driver = driver.RunGenerators(compilation2);
			var result2 = driver.GetRunResult();
			var second  = GetGeneratedSourceOrEmpty(result2, "Interceptors.g.cs");
			Console.WriteLine("RB reasons run2: " + string.Join(",", result2.Results.SelectMany(static r => r.TrackedOutputSteps).SelectMany(static s => s.Value).SelectMany(static s => s.Outputs).Select(static o => o.Reason)));

			driver = driver.RunGenerators(compilation2);
			var result3 = driver.GetRunResult();
			var third   = GetGeneratedSourceOrEmpty(result3, "Interceptors.g.cs");

			Assert.AreEqual(first, second);
			Assert.AreEqual(first, third);

			var reasons = result3.Results.SelectMany(static r => r.TrackedOutputSteps).SelectMany(static s => s.Value).SelectMany(static s => s.Outputs).Select(static o => o.Reason).ToList();
			Console.WriteLine("RB reasons: " + string.Join(",", reasons));
			Assert.IsTrue(reasons.All(static r => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged), string.Join(",", reasons));
		}


		// ---- round 2 ----

		[TestMethod]
		public void RB_InterfaceCallOnStructTypeParameterTest()
		{
			Breaker(
				"""
				interface IBump { [Log] void Bump(); int Read(); }
				struct Bumper : IBump
				{
					int _v;
					public void Bump() => _v++;
					public int Read() => _v;
				}
				static class Calls
				{
					static void Generic<T>(ref T t) where T : IBump => t.Bump();
					public static string Run()
					{
						var b = new Bumper();
						Generic(ref b);
						return $"{b.Read()}";
					}
				}
				""",
				null,
				expectedValue: "1");
		}

		[TestMethod]
		public void RB_ExtensionThisRefOnlyTest()
		{
			Breaker(
				"""
				struct Counter { public int Value; }
				static class Ext
				{
					[Log] public static void Inc(this ref Counter c) => c.Value++;
				}
				static class Calls
				{
					public static string Run()
					{
						var c = new Counter();
						c.Inc();
						return $"{c.Value}";
					}
				}
				""",
				null,
				expectedValue: "1");
		}

		[TestMethod]
		public void RB_ExtensionThisRefReadonlyOnlyTest()
		{
			var result = Breaker(
				"""
				struct Counter { public int Value; }
				static class Ext
				{
					[Log] public static int GetRr(this ref readonly Counter c) => c.Value;
				}
				static class Calls
				{
					public static string Run()
					{
						var c = new Counter { Value = 3 };
						return $"{c.GetRr()}";
					}
				}
				""",
				null,
				expectedValue: "3");

			StringAssert.Contains(GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs"), "this ref readonly global::Counter __this__");
		}

		[TestMethod]
		public void RB_AllowsRefStructPassArgumentsTest()
		{
			Breaker(
				"""
				static class Helper
				{
					[ArgsLog] public static string Name<T>(T value) where T : allows ref struct => typeof(T).Name;
				}
				static class Calls
				{
					static string G<T>(T value) where T : allows ref struct => Helper.Name(value);
					public static string Run() => G(1);
				}
				""",
				null,
				expectedValue: "Int32");
		}

		[TestMethod]
		public void RB_AllowsRefStructReturnTest()
		{
			Breaker(
				"""
				static class Helper
				{
					[Log] public static T Id<T>(T value) where T : allows ref struct => value;
				}
				static class Calls
				{
					static int G<T>(T value) where T : allows ref struct { var x = Helper.Id(value); return 1; }
					public static string Run() => $"{G(1)}";
				}
				""",
				null,
				expectedValue: "1");
		}

		[TestMethod]
		public void RB_StaticAbstractInterfaceMemberTest()
		{
			Breaker(
				"""
				interface IMake<TSelf> where TSelf : IMake<TSelf>
				{
					[Log] static abstract string Make();
					[Log] static virtual string Name() => "default";
				}
				sealed class Impl : IMake<Impl>
				{
					public static string Make() => "impl";
				}
				static class Calls
				{
					static string G<T>() where T : IMake<T> => T.Make() + T.Name();
					public static string Run() => G<Impl>();
				}
				""",
				null,
				expectedValue: "impldefault");
		}

		[TestMethod]
		public void RB_GenericOutAndInAndRefStructInGenericTypeTest()
		{
			Breaker(
				"""
				static class Helper
				{
					[Log] public static bool TryGet<T>(T input, out T value, in T other) { value = input; return true; }
				}
				class Buf<T>
				{
					[Log] public int Len(ReadOnlySpan<T> s, ref int counter) => s.Length + counter++;
				}
				static class Calls
				{
					static string G<T>(T v)
					{
						var counter = 1;
						var len = new Buf<T>().Len(new T[] { v, v }, ref counter);
						return Helper.TryGet(v, out var x, in v) ? $"{x}{len}{counter}" : "";
					}
					public static string Run() => G("a");
				}
				""",
				null,
				expectedValue: "a32");
		}

		[TestMethod]
		public void RB_PointerParametersTest()
		{
			Breaker(
				"""
				unsafe static class Helper
				{
					[Log] public static int Deref(int* p) => *p;
					[Log] public static int CallFp(delegate*<int, int> f, int x) => f(x);
					public static int Twice(int x) => x * 2;
				}
				unsafe sealed class Inst
				{
					[Log] public int Deref(int* p) => *p + 1;
					[Log] public int CallFp(delegate*<int, int> f, int x) => f(x) + 1;
				}
				static class Calls
				{
					public static unsafe string Run()
					{
						int v = 4;
						var i = new Inst();
						return $"{Helper.Deref(&v)}{Helper.CallFp(&Helper.Twice, 3)}{i.Deref(&v)}{i.CallFp(&Helper.Twice, 3)}";
					}
				}
				""",
				"Helper.Deref<>;Helper.CallFp<>;Inst.Deref<>;Inst.CallFp<>;4657");
		}

		[TestMethod]
		public void RB_ExplicitPrivateTargetIsWarningTest()
		{
			var result = Breaker(
				"""
				class Owner
				{
					[Log] private string Secret() => "s";
					[Log] protected internal string Pi() => "pi";
					public string Go() => Secret() + Pi();
				}
				static class Calls
				{
					public static string Run() => new Owner().Go();
				}
				""",
				null,
				expectedValue: "spi");

			var d = result.Diagnostics.Where(static d => d.Id == AspectDiagnosticID.CannotIntercept).ToArray();
			Assert.AreEqual(1, d.Length, string.Join("|", d.Select(static x => x.GetMessage())));
			Assert.AreEqual(DiagnosticSeverity.Warning, d[0].Severity);
		}

		// ---- harness ----

		static string WrapBreaker(string body)
		{
			return
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

					[Aspect(OnBeforeCall = nameof(Before))]
					[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
					sealed class TagAttribute : Attribute
					{
						public string? TargetFilter { get; set; }

						public static void Before(InterceptInfo info)
						{
							LogAttribute.Log.Append($"Tag:{info.MemberInfo.Name};");
						}
					}

					[Aspect(OnBeforeCall = nameof(Before), PassArguments = true)]
					[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
					sealed class ArgsLogAttribute : Attribute
					{
						public static void Before(InterceptInfo info) => LogAttribute.Log.Append("Args;");
					}

					[Aspect(OnBeforeCall = nameof(Before), OnBeforeCallAsync = nameof(BeforeAsync))]
					[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
					sealed class AsyncLogAttribute : Attribute
					{
						public static void Before(InterceptInfo info) => LogAttribute.Log.Append("S;");
						public static System.Threading.Tasks.Task BeforeAsync(InterceptInfo info) { LogAttribute.Log.Append("A;"); return System.Threading.Tasks.Task.CompletedTask; }
					}
				}
				""";
		}

		static GeneratorDriverRunResult Breaker(string body, string? expectedLog, string? expectedValue = null, int expectedInterceptedCalls = -1, Action<string>? checkLog = null)
		{
			var source = WrapBreaker(body);

			var parseOptions = CSharpParseOptions.Default
				.WithLanguageVersion(LanguageVersion.Preview)
				.WithFeatures([new("InterceptorsNamespaces", "AspectGenerator")]);

			var compilation = CSharpCompilation.Create(
				"AspectGenerator.Tests.GeneratorDriver",
				[CSharpSyntaxTree.ParseText(source, parseOptions, "GeneratorDriverTest.cs")],
				GetMetadataReferences(),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));

			var props = new Dictionary<string,string> { ["build_property.InterceptorsNamespaces"] = "AspectGenerator", ["build_property.AspectGeneratorPropsImported"] = "true" };

			GeneratorDriver driver = CSharpGeneratorDriver.Create(
				[new AspectSourceGenerator().AsSourceGenerator()],
				parseOptions: parseOptions,
				optionsProvider: new TestAnalyzerConfigOptionsProvider(props));

			driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

			var result       = driver.GetRunResult();
			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");
			var all          = output.GetDiagnostics();
			var generated    = all.Where(static d => d.Location.SourceTree?.FilePath.EndsWith("Interceptors.g.cs", StringComparison.Ordinal) == true && d.Severity >= DiagnosticSeverity.Warning).ToArray();
			var errors       = all.Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();
			var ag           = result.Diagnostics.Select(static d => $"{d.Id} {d.Severity}: {d.GetMessage()}").ToArray();

			Console.WriteLine("RB generator diagnostics: " + string.Join(" | ", ag));
			Console.WriteLine("RB warnings in generated code: " + string.Join(" | ", generated.Select(static d => d.ToString())));

			Assert.AreEqual(0, errors.Length, "ERRORS\n" + string.Join(Environment.NewLine, errors.Select(static e => e.ToString())) + "\nAG: " + string.Join(" | ", ag) + Environment.NewLine + interceptors);

			using var stream = new MemoryStream();

			var emit = output.Emit(stream);

			Assert.IsTrue(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics) + Environment.NewLine + interceptors);

			if (expectedInterceptedCalls >= 0)
				Assert.AreEqual(expectedInterceptedCalls, CountOccurrences(interceptors, "InterceptsLocation"), "AG: " + string.Join(" | ", ag) + Environment.NewLine + interceptors);

			var context = new AssemblyLoadContext("ReviewBreakerTests", isCollectible: true);

			try
			{
				stream.Position = 0;

				var assembly = context.LoadFromStream(stream);
				var run      = assembly.GetType("Calls")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
				var value    = (string)run.Invoke(null, null)!;
				var log      = assembly.GetType("AspectGenerator.Tests.GeneratorDriver.LogAttribute")!.GetField("Log")!.GetValue(null)!.ToString()!;

				Console.WriteLine($"RB log='{log}' value='{value}'");

				if (expectedLog is not null)
					Assert.AreEqual(expectedLog, log + value, "AG: " + string.Join(" | ", ag) + Environment.NewLine + interceptors);
				checkLog?.Invoke(log);
				if (expectedValue is not null)
					Assert.AreEqual(expectedValue, value, "log=" + log + "\nAG: " + string.Join(" | ", ag) + Environment.NewLine + interceptors);
			}
			finally
			{
				context.Unload();
			}

			Assert.AreEqual(0, generated.Length, "WARNINGS IN GENERATED CODE\n" + string.Join(Environment.NewLine, generated.Select(static d => d.ToString())) + Environment.NewLine + interceptors);

			return result;
		}
	}
}
