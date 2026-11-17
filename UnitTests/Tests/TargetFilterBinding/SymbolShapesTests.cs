using System;
using System.Collections.Generic;

using AspectGenerator.TargetFilterBinding;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilterBinding
{
	/// <summary>
	/// The forms of types as the compiler reports them, each against the patterns that name it: what a symbol becomes
	/// for the matchers.
	/// </summary>
	[TestClass]
	public class SymbolShapesTests
	{
		const Tri T = Tri.True;
		const Tri F = Tri.False;
		const Tri D = Tri.Depends;

		const string Source =
			"""
			using System;
			using System.Collections.Generic;

			public class GlobalType
			{
				public void G() { }
			}

			namespace Shop
			{
				public class AuditedAttribute : Attribute { }
				public class NoLog : Attribute { }

				public class Outer<T>
				{
					public class Inner<U>
					{
						public void Nested(T t, U u) { }
					}
				}

				[Audited]
				public unsafe class Shapes
				{
					public int? NullableInt(int? x) { return x; }
					public string? NullableString(string? s) { return s; }
					public int[][,] Jagged(int[][,] a) { return a; }
					public (int Id, string Name) Tuple((int, string) t) { return t; }
					public (int, int, int, int, int, int, int, int, string) Long() { return default; }
					public dynamic Dyn(dynamic d) { return d; }
					public int* Pointer(int* p) { return p; }

					public void Fp(
						delegate*<int, void> managed,
						delegate* unmanaged<int, void> unmanaged,
						delegate* unmanaged[Cdecl]<int, void> cdecl,
						delegate* unmanaged[Cdecl, SuppressGCTransition]<ref int, int> two)
					{
					}

					public void Modifiers(ref int a, out int b, in int c, ref readonly int d, params int[] e) { b = 0; }
					public ref int RefReturn(ref int x) { return ref x; }
					public ref readonly int RefReadonlyReturn(ref int x) { return ref x; }
					[NoLog] public void Marked() { }
					public List<int?> ListOfNullable() { return null!; }
					public Outer<int>.Inner<string> NestedGeneric() { return null!; }
					public void Generic<T>(T[] items, List<T> list, T? maybe) where T : struct { }
				}

				public static class Ext
				{
					public static void E(this ref int x) { }
				}
			}
			""";

		static readonly Lazy<ProbeCompilation> _probe = new(() => new ProbeCompilation(Source));

		[TestMethod]
		public void EveryFormOfTypeIsMatchedByItsPatternTest()
		{
			const string Shapes = "Shop.Shapes";

			(string Filter, string Type, string Method, Tri Expected)[] rows =
			[
				("returns: int?",                         Shapes, "NullableInt",       T),
				("returns: int",                          Shapes, "NullableInt",       F),
				("returns: Nullable<int>",                Shapes, "NullableInt",       T),
				("returns: *?",                           Shapes, "NullableInt",       T),
				("params: (int?)",                        Shapes, "NullableInt",       T),

				("returns: string?",                      Shapes, "NullableString",    T),
				("returns: string",                       Shapes, "NullableString",    T),
				("returns: *?",                           Shapes, "NullableString",    T),

				("returns: int[][,]",                     Shapes, "Jagged",            T),
				("returns: int[,][]",                     Shapes, "Jagged",            F),
				("returns: *[]",                          Shapes, "Jagged",            T),
				("returns: *[,]",                         Shapes, "Jagged",            F),
				("returns: *[]?",                         Shapes, "Jagged",            T),

				("returns: (int, string)",                Shapes, "Tuple",             T),
				("returns: (string, int)",                Shapes, "Tuple",             F),
				("returns: ValueTuple<int, string>",      Shapes, "Tuple",             T),
				("params: ((int, string))",               Shapes, "Tuple",             T),
				("returns: (int, int, int, int, int, int, int, int, string)", Shapes, "Long", T),
				("returns: (*, *, *, *, *, *, *, *, *)",  Shapes, "Long",              T),
				("returns: (*, *)",                       Shapes, "Long",              F),

				("returns: dynamic",                      Shapes, "Dyn",               T),
				("returns: object",                       Shapes, "Dyn",               F),
				("returns: *",                            Shapes, "Dyn",               T),
				("returns: *?",                           Shapes, "Dyn",               T),

				// No pattern but `*` is a pointer: `int*` is a name that begins with int.
				("returns: *",                            Shapes, "Pointer",           T),
				("returns: int",                          Shapes, "Pointer",           F),
				("returns: int*",                         Shapes, "Pointer",           F),
				("returns: **",                           Shapes, "Pointer",           F),
				("returns: *?",                           Shapes, "Pointer",           F),

				("params: (delegate*<int, void>, delegate* unmanaged<int, void>, delegate* unmanaged[Cdecl]<int, void>, delegate* unmanaged[Cdecl, SuppressGCTransition]<ref int, int>)", Shapes, "Fp", T),
				("params: (delegate* managed<int, void>, ..)",                   Shapes, "Fp", T),
				("params: (delegate* unmanaged<int, void>, ..)",                 Shapes, "Fp", F),
				("params: (_, _, delegate* unmanaged[Stdcall]<int, void>, _)",   Shapes, "Fp", F),
				("params: (.., delegate* unmanaged[SuppressGCTransition, Cdecl]<int, int>)", Shapes, "Fp", T),
				("params: (.., delegate* unmanaged[Cdecl]<int, int>)",           Shapes, "Fp", F),
				("params: (.., delegate* unmanaged[SuppressGCTransition, Cdecl]<out int, int>)", Shapes, "Fp", F),
				("params: (*, *, *, *)",                  Shapes, "Fp",                T),

				("params: (ref int, out int, in int, ref readonly int, params int[])", Shapes, "Modifiers", T),
				("params: (int, int, int, int, int[])",   Shapes, "Modifiers",         T),
				("params: (in int, ..)",                  Shapes, "Modifiers",         F),
				("params: (.., int[])",                   Shapes, "Modifiers",         T),
				("param: params *[]",                     Shapes, "Modifiers",         T),
				("param: ref readonly int",               Shapes, "Modifiers",         T),

				("returns: ref int",                      Shapes, "RefReturn",         T),
				("returns: ref readonly int",             Shapes, "RefReturn",         F),
				("returns: int",                          Shapes, "RefReturn",         T),
				("returns: ref readonly int",             Shapes, "RefReadonlyReturn", T),
				("returns: ref int",                      Shapes, "RefReadonlyReturn", F),

				// The attributes of the method and of the types that contain it.
				("attribute: NoLog",                      Shapes, "Marked",            T),
				("attribute: Audited",                    Shapes, "Marked",            T),
				("attribute: global::Shop.Audited",       Shapes, "Marked",            T),
				("attribute: Obsolete",                   Shapes, "Marked",            F),
				("attribute: NoLog",                      Shapes, "Jagged",            F),
				("attribute: *",                          "GlobalType", "G",           F),

				("returns: List<int?>",                   Shapes, "ListOfNullable",    T),
				("returns: List<int>",                    Shapes, "ListOfNullable",    F),

				// A nested type carries the type arguments of each of its levels.
				("returns: Outer<int>.Inner<string>",     Shapes, "NestedGeneric",     T),
				("returns: Outer<*>.Inner<*>",            Shapes, "NestedGeneric",     T),
				("returns: Outer.Inner",                  Shapes, "NestedGeneric",     T),
				("returns: Inner<string>",                Shapes, "NestedGeneric",     T),
				("returns: Outer<string>.Inner<string>",  Shapes, "NestedGeneric",     F),
				("returns: Inner<int>",                   Shapes, "NestedGeneric",     F),
				("returns: global::Shop.Outer<int>.Inner<string>", Shapes, "NestedGeneric", T),
				("type: Outer<*>.Inner<*>",               "Shop.Outer`1+Inner`1", "Nested", T),
				("type: Outer<int>.Inner",                "Shop.Outer`1+Inner`1", "Nested", D),
				("type: Outer.Inner<string>",             "Shop.Outer`1+Inner`1", "Nested", D),
				("namespace: Shop",                       "Shop.Outer`1+Inner`1", "Nested", T),
				("params: (*, *)",                        "Shop.Outer`1+Inner`1", "Nested", T),

				// T? over a type parameter that is a struct is Nullable<T>.
				("params: (*[], List<*>, *?)",            Shapes, "Generic",           T),
				("params: (int[], ..)",                   Shapes, "Generic",           D),
				("params: (_, _, int?)",                  Shapes, "Generic",           D),
				("params: (_, _, int)",                   Shapes, "Generic",           F),
				("method: Generic<*>",                    Shapes, "Generic",           T),

				("namespace: \"\"",                       "GlobalType", "G",           T),
				("namespace: **",                         "GlobalType", "G",           T),
				("namespace: *",                          "GlobalType", "G",           F),
				("type: global::GlobalType",              "GlobalType", "G",           T),
				("method: global::GlobalType.G",          "GlobalType", "G",           T),
				("method: global::G",                     "GlobalType", "G",           F),
				("namespace: \"\"",                       Shapes, "Marked",            F),

				("params: (this ref int)",                "Shop.Ext",   "E",           T),
				("params: (this int)",                    "Shop.Ext",   "E",           T),
				("params: (ref int)",                     "Shop.Ext",   "E",           T),
				("params: (this in int)",                 "Shop.Ext",   "E",           F),
				("params: (params int)",                  "Shop.Ext",   "E",           F),
				("params: (int)",                         "Shop.Ext",   "E",           T),
				("extension and static and public",       "Shop.Ext",   "E",           T),
				("public and instance and project",       Shapes, "Marked",            T),
				("generated or extern or async or virtual", Shapes, "Marked",          F),
			];

			var failures = new List<string>();

			foreach (var (filter, type, method, expected) in rows)
			{
				var actual = FilterProbe.Answer(filter, _probe.Value.Definition(type, method), MatchPhase.Definition);

				if (actual != expected)
					failures.Add($"'{filter}' on {type}.{method}: {actual}, expected {expected}");
			}

			Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
		}
	}
}
