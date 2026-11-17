using System;
using System.Diagnostics;
using System.Linq;

using AspectGenerator.Cascade;
using AspectGenerator.TargetFilterBinding;
using AspectGenerator.TargetFilterFacts;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilterBinding
{
	/// <summary>Defects a review found in the matchers and the anchors, each as the filter and the code that showed it.</summary>
	[TestClass]
	public class ReviewFindingsTests
	{
		const Tri T = Tri.True;
		const Tri F = Tri.False;
		const Tri D = Tri.Depends;

		public TestContext TestContext { get; set; } = null!;

		const string Source =
			"""
			using System.Runtime.CompilerServices;

			public class C
			{
				public void Save() { }
			}

			namespace N
			{
				public class C
				{
					public void Save() { }
				}

				public class A
				{
					public TaskAwaiter GetAwaiter() { return default; }
				}

				public class B : A
				{
					public new int GetAwaiter() { return 0; }
				}

				public unsafe class Shapes
				{
					public static T Make<T>() where T : A { return default!; }
					public int* Pointer() { return null; }
					public string[]?[,] Mixed() { return null!; }
					public void Convention(delegate* unmanaged[Cdecl, SuppressGCTransition]<int, void> p) { }
					public void Other() { }
					public static T[] Arr<T>() { return null!; }
					public static T* Ptr<T>() where T : unmanaged { return null; }
					public static System.Collections.Generic.List<T> Lst<T>() { return null!; }
					public static int[] Plain() { return null!; }

					public static void Calls()
					{
						Make<A>();
						Make<B>();
						int.Parse("1");
						Arr<int>();
						Ptr<int>();
						Lst<int>();
					}
				}
			}
			""";

		static readonly Lazy<ProbeCompilation> _probe = new(() => new ProbeCompilation(Source));

		static string[] Errors(string filter)
		{
			return [..FilterBlockBuilder.Build(CascadeLevel.Assembly, [filter]).Diagnostics.Select(static d => d.Id)];
		}

		[TestMethod]
		public void TwoConditionsOnTheNameMayBeMetByTheSameCharactersTest()
		{
			var block = FilterBlockBuilder.Build(CascadeLevel.Assembly, ["method: Save* and method: *ave"]);
			var save  = _probe.Value.Definition("N.C", "Save");

			Assert.AreEqual(T, FilterProbe.Answer("method: Save* and method: *ave", save));
			Assert.IsTrue(block.Anchors.MayBindName("Save"), block.Anchors.ToString());
			Assert.IsTrue(block.Anchors.MayBindName("Saveave"));
			Assert.IsFalse(block.Anchors.MayBindName("Saved"));
			Assert.IsFalse(block.Anchors.MayBindName("ave"));
		}

		[TestMethod]
		public void ATypeParameterReturnedIsAwaitableAsTheCallSaysTest()
		{
			// The constraint brings an awaiter, and a type that satisfies it hides that awaiter with a member that is none.
			var make = _probe.Value.Method("N.Shapes", "Make");

			Assert.AreEqual(D, FilterProbe.Answer("async", _probe.Value.Definition(make), MatchPhase.Definition));
			Assert.AreEqual(T, FilterProbe.Answer("async", _probe.Value.Call("Make<A>()")));
			Assert.AreEqual(F, FilterProbe.Answer("async", _probe.Value.Call("Make<B>()")));
		}

		[TestMethod]
		public void GlobstarIsNoAlternativeInsideANameTest()
		{
			CollectionAssert.AreEqual(new[] { "AG0224" }, Errors("method: {**,Other}.Save"));
			CollectionAssert.AreEqual(new[] { "AG0224" }, Errors("type: N.{**.C,D}x"));

			// Alternatives that are the whole pattern are patterns of their own, `**` and all.
			var save = _probe.Value.Definition("N.C", "Save");

			Assert.AreEqual(T, FilterProbe.Answer("method: {**.Save,Other.Save}", save));
			Assert.AreEqual(T, FilterProbe.Answer("namespace: {N.**,Other}", save));
			Assert.AreEqual(F, FilterProbe.Answer("method: {**.Load,Other.Save}", save));
		}

		[TestMethod]
		public void GlobalRootsAWholeAlternativeHoweverDeepTest()
		{
			var nested = _probe.Value.Definition("N.C", "Save");
			var global = _probe.Value.Definition("C", "Save");

			foreach (var filter in new[] { "method: global::C.Save", "method: {global::C.Save}", "method: {{global::C.Save}}", "method: {Load,{{global::C.Save},Other.*}}" })
			{
				Assert.AreEqual(F, FilterProbe.Answer(filter, nested), filter);
				Assert.AreEqual(T, FilterProbe.Answer(filter, global), filter);
			}

			Assert.AreEqual(F, FilterProbe.Answer("type: {{global::C}}", nested));
			Assert.AreEqual(T, FilterProbe.Answer("type: {{global::C}}", global));
			Assert.AreEqual(T, FilterProbe.Answer("returns: List<{{global::System.Int32},string}>", new RecordFacts("X", "M", "System.Collections.Generic.List<int>")));

			// Anywhere else it roots nothing and is refused.
			CollectionAssert.AreEqual(new[] { "AG0224" }, Errors("type: {A,global::B}x"));
			CollectionAssert.AreEqual(new[] { "AG0224" }, Errors("method: N.{global::C,D}.Save"));
		}

		[TestMethod]
		public void ALongNameCostsItsLengthTimesThePatternsTest()
		{
			var name  = new string('a', 20_000);
			var facts = new RecordFacts("Shop." + name, name);
			var stars = string.Concat(Enumerable.Repeat("*a", 20)) + "b";
			var clock = Stopwatch.StartNew();

			Assert.AreEqual(F, FilterProbe.Answer("method: " + stars, facts));
			Assert.AreEqual(F, FilterProbe.Answer("type: " + stars, facts));
			Assert.AreEqual(F, FilterProbe.Answer("method: " + string.Concat(Enumerable.Repeat("{a,aa}", 20)) + "b", facts));
			Assert.AreEqual(F, FilterProbe.Answer("method: **." + string.Concat(Enumerable.Repeat("{a*,*a}", 20)) + "b", facts));
			Assert.AreEqual(T, FilterProbe.Answer("method: " + string.Concat(Enumerable.Repeat("{a*,*a}", 20)), facts));

			clock.Stop();

			TestContext.WriteLine($"{clock.ElapsedMilliseconds} ms");

			// Twenty choices over twenty thousand characters: a search that tried their combinations would not end.
			Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(3), $"{clock.ElapsedMilliseconds} ms");
		}

		[TestMethod]
		public void APatternPastTheLimitIsAnErrorNotASilentNoTest()
		{
			CollectionAssert.AreEqual(new[] { "AG0227" }, Errors("method: " + string.Concat(Enumerable.Repeat("{a}", 20_000))));
			CollectionAssert.AreEqual(new[] { "AG0227" }, Errors("not method: " + string.Concat(Enumerable.Repeat("*a", 300))));
			CollectionAssert.AreEqual(new[] { "AG0227" }, Errors("returns: " + string.Concat(Enumerable.Repeat("List<", 40)) + "int" + new string('>', 40)));

			// Under the limit a pattern of many choices is matched for what it says.
			var name = new string('a', 200);

			Assert.AreEqual(T, FilterProbe.Answer("method: " + string.Concat(Enumerable.Repeat("{a}", 200)), new RecordFacts("Shop.C", name)));
			Assert.AreEqual(F, FilterProbe.Answer("not method: " + string.Concat(Enumerable.Repeat("{a}", 200)), new RecordFacts("Shop.C", name)));
			Assert.AreEqual(F, FilterProbe.Answer("method: " + string.Concat(Enumerable.Repeat("{a}", 200)), new RecordFacts("Shop.C", name + "a")));
		}

		[TestMethod]
		public void CallingConventionsAreComparedNameByNameTest()
		{
			var convention = _probe.Value.Definition("N.Shapes", "Convention");

			Assert.AreEqual(F, FilterProbe.Answer("params: (delegate* unmanaged[Cdecl,Cdecl]<int,void>)", convention));
			Assert.AreEqual(F, FilterProbe.Answer("params: (delegate* unmanaged[SuppressGCTransition,SuppressGCTransition]<int,void>)", convention));
			Assert.AreEqual(T, FilterProbe.Answer("params: (delegate* unmanaged[SuppressGCTransition,Cdecl]<int,void>)", convention));
		}

		[TestMethod]
		public void ImplementsTakesTheMethodItselfTest()
		{
			var other = _probe.Value.Definition("N.Shapes", "Other");

			Assert.AreEqual(T, FilterProbe.Answer("implements: Shapes.Other", other, overridesIsKey: true));
			Assert.AreEqual(T, FilterProbe.Answer("implements: Shapes.Other", other, overridesIsKey: false));
			Assert.AreEqual(F, FilterProbe.Answer("implements: Shapes.Another", other, overridesIsKey: true));
		}

		[TestMethod]
		public void QuotedTextNamesATypeNoPatternSpellsTest()
		{
			var pointer = _probe.Value.Definition("N.Shapes", "Pointer");

			Assert.AreEqual(T, FilterProbe.Answer("returns: \"System.Int32*\"", pointer));
			Assert.AreEqual(F, FilterProbe.Answer("returns: \"System.Int64*\"", pointer));
			Assert.AreEqual(F, FilterProbe.Answer("returns: \"Int32*\"", pointer));
			Assert.AreEqual(T, FilterProbe.Answer("returns: *", pointer));

			// Quoted text is still the name of a type where one has it, and never a type parameter.
			Assert.AreEqual(T, FilterProbe.Answer("returns: \"Int32\"", new RecordFacts("X", "M", "int")));
			Assert.AreEqual(F, FilterProbe.Answer("returns: \"T\"", new RecordFacts("X", "M", "!T")));
			Assert.AreEqual(T, FilterProbe.Answer("returns: \"System.Int32[]\"", new RecordFacts("X", "M", "int[]")));
		}

		[TestMethod]
		public void AMethodPathBeginsWithTheCSharpNameOfItsTypeTest()
		{
			var parse = _probe.Value.Call("int.Parse(\"1\")");

			Assert.AreEqual(T, FilterProbe.Answer("method: int.Parse", parse));
			Assert.AreEqual(F, FilterProbe.Answer("method: long.Parse", parse));
			Assert.AreEqual(T, FilterProbe.Answer("method: int.*", parse));
			Assert.AreEqual(T, FilterProbe.Answer("int.Parse(string): int", parse));

			// It is System.Int32 and no other: a type called int is written @int.
			Assert.AreEqual(F, FilterProbe.Answer("method: int.Parse", new RecordFacts("My.Int32", "Parse")));
			Assert.AreEqual(F, FilterProbe.Answer("method: int.Parse", new RecordFacts("My.int", "Parse")));
			Assert.AreEqual(T, FilterProbe.Answer("method: @int.Parse", new RecordFacts("My.int", "Parse")));
			Assert.AreEqual(T, FilterProbe.Answer("method: My.int", new RecordFacts("My", "int")));
		}

		[TestMethod]
		public void AnArrayTypeIsReadAsTheCompilerReadsItTest()
		{
			// `string[]?[,]` is to the compiler an array of two dimensions whose elements are `string[]?`: the `?` closes
			// the array before it, and the ranks after it wrap that. Without the `?` the same type is written
			// `string[,][]`. A pattern is read the same way, so the type's own spelling matches it; whether an array may
			// be null is not looked at.
			var mixed = _probe.Value.Definition("N.Shapes", "Mixed");

			Assert.AreEqual(T, FilterProbe.Answer("returns: string[]?[,]", mixed));
			Assert.AreEqual(T, FilterProbe.Answer("returns: string[,][]", mixed));
			Assert.AreEqual(T, FilterProbe.Answer("returns: string[,][]?", mixed));
			Assert.AreEqual(T, FilterProbe.Answer("returns: string?[]?[,]?", mixed));
			Assert.AreEqual(T, FilterProbe.Answer("returns: *[]?[,]", mixed));
			Assert.AreEqual(F, FilterProbe.Answer("returns: string[][,]", mixed));
			Assert.AreEqual(F, FilterProbe.Answer("returns: string[,]?[]", mixed));
		}

		[TestMethod]
		public void ARunOfParametersIsNoSingleParameterTest()
		{
			CollectionAssert.AreEqual(new[] { "AG0224" }, Errors("param: .."));
			CollectionAssert.AreEqual(Array.Empty<string>(), Errors("param: _"));
			CollectionAssert.AreEqual(Array.Empty<string>(), Errors("params: (..)"));
		}

		[TestMethod]
		public void AnyTextOfTheMethodsNameIsAnAnchorTest()
		{
			var anchors = FilterBlockBuilder.Build(CascadeLevel.Assembly, ["method: *Internal*"]).Anchors;

			Assert.IsFalse(anchors.BindsEveryCall);
			Assert.IsTrue(anchors.MayBindName("RunInternalNow"));
			Assert.IsTrue(anchors.MayBindName("Internal"));
			Assert.IsFalse(anchors.MayBindName("RunInternaNow"));
		}

		[TestMethod]
		public void AnyBesideAnotherConditionIsOneErrorWhereverItStandsTest()
		{
			foreach (var filter in new[] { "method: any and public", "public and method: any", "method: any or public", "public and (method: any or static)", "method: any\nand public" })
				CollectionAssert.AreEqual(new[] { "AG0234" }, Errors(filter), filter);

			// The grammar reads `any` as the value wherever it stands; that it has company is the block's error.
			Assert.AreEqual(0, AspectGenerator.FilterV2.FilterV2Reader.Read("method: any and public").Diagnostics.Length);
			Assert.AreEqual(0, AspectGenerator.FilterV2.FilterV2Reader.Read("public or method: any or static").Diagnostics.Length);

			CollectionAssert.AreEqual(Array.Empty<string>(), Errors("method: any # every method"));
			CollectionAssert.AreEqual(Array.Empty<string>(), Errors("(method: any)"));
			CollectionAssert.AreEqual(Array.Empty<string>(), Errors("method: any*"));
		}

		[TestMethod]
		public void EveryWrapperOfATypeCountsTowardsTheDepthTest()
		{
			// Ranks wrap a type one in another like type arguments do.
			CollectionAssert.AreEqual(new[] { "AG0227" }, Errors("returns: int" + string.Concat(Enumerable.Repeat("[]", 12_000))));
			CollectionAssert.AreEqual(new[] { "AG0227" }, Errors("returns: int" + string.Concat(Enumerable.Repeat("[]", 40))));
			CollectionAssert.AreEqual(new[] { "AG0227" }, Errors("returns: List<int" + string.Concat(Enumerable.Repeat("[]", 20)) + ">" + string.Concat(Enumerable.Repeat("[,]", 20))));
			CollectionAssert.AreEqual(Array.Empty<string>(), Errors("returns: int?" + string.Concat(Enumerable.Repeat("[]", 20))));
		}

		[TestMethod]
		public void ATypeNestedByTheThousandIsMatchedOnASmallStackTest()
		{
			// A pattern is no deeper than its limit, so no match goes deeper into a type than that, however deep the type.
			TypeShape deep = ShapeText.Parse("int");

			for (var i = 0; i < 12_000; i++)
				deep = new ArrayTypeShape(deep, 1);

			TypeShape pointers = ShapeText.Parse("int");

			for (var i = 0; i < 12_000; i++)
				pointers = new PointerTypeShape(pointers);

			var twenty  = string.Concat(Enumerable.Repeat("[]", 20));
			var results = new System.Collections.Generic.List<string>();
			Exception? failure = null;

			var thread = new System.Threading.Thread(() =>
			{
				try
				{
					foreach (var type in new[] { deep, pointers })
					{
						var facts = new RecordFacts(ShapeText.ParseNamed("Shop.C"), new NameSegment("M"), type, [new ParameterShape(type, ParameterModifiers.None)]);

						foreach (var filter in new[] { "returns: *", "returns: *[]", "returns: *" + twenty, "returns: int" + twenty, "returns: List<*>", "returns: \"System.Int32[]\"", "params: (*[]?)", "returns: {int[], *[][]}" })
						{
							results.Add($"{filter.Replace(twenty, "[]x20")} = {FilterProbe.Answer(filter, facts)}/{FilterProbe.Answer(filter, facts, MatchPhase.Definition)}");
						}
					}
				}
				catch (Exception ex)
				{
					failure = ex;
				}
			}, 256 * 1024);

			thread.Start();
			thread.Join();

			Assert.IsNull(failure, failure?.ToString());

			CollectionAssert.AreEqual(
				new[]
				{
					"returns: * = True/True", "returns: *[] = True/True", "returns: *[]x20 = True/True", "returns: int[]x20 = False/False",
					"returns: List<*> = False/False", "returns: \"System.Int32[]\" = False/False", "params: (*[]?) = True/True", "returns: {int[], *[][]} = True/True",
					"returns: * = True/True", "returns: *[] = False/False", "returns: *[]x20 = False/False", "returns: int[]x20 = False/False",
					"returns: List<*> = False/False", "returns: \"System.Int32[]\" = False/False", "params: (*[]?) = False/False", "returns: {int[], *[][]} = False/False",
				},
				results);

			// The same for a type the compiler holds: its shape is made without following it down by calls.
			var probe = new ProbeCompilation("public class Deep { public int" + string.Concat(Enumerable.Repeat("[]", 300)) + " M() { return null!; } }");

			Assert.AreEqual(T, FilterProbe.Answer("returns: *" + twenty, probe.Definition("Deep", "M")));
			Assert.AreEqual(F, FilterProbe.Answer("returns: int" + twenty, probe.Definition("Deep", "M")));
		}

		[TestMethod]
		public void QuotedTextOfATypeWithATypeParameterIsForTheCallToSayTest()
		{
			(string Filter, string Call, Tri Definition, Tri OnCall)[] rows =
			[
				("returns: \"System.Int32[]\"",                                    "Arr<int>()", D, T),
				("returns: \"System.Int64[]\"",                                    "Arr<int>()", D, F),
				("not returns: \"System.Int32[]\"",                                "Arr<int>()", D, F),
				("returns: \"System.Int32*\"",                                     "Ptr<int>()", D, T),
				("not returns: \"System.Int32*\"",                                 "Ptr<int>()", D, F),
				("returns: \"System.Collections.Generic.List<System.Int32>\"",     "Lst<int>()", D, T),
				("returns: \"System.Collections.Generic.List<System.Int64>\"",     "Lst<int>()", D, F),
				("returns: \"List\"",                                              "Lst<int>()", T, T),
				("returns: {\"System.Int32[]\", void}",                            "Arr<int>()", D, T),
			];

			foreach (var (filter, call, definition, onCall) in rows)
			{
				var (syntax, method) = _probe.Value.Calls().First(c => c.Syntax.ToString() == call);

				Assert.AreEqual(definition, FilterProbe.Answer(filter, _probe.Value.Definition(method), MatchPhase.Definition), filter + " on the definition");
				Assert.AreEqual(onCall,     FilterProbe.Answer(filter, _probe.Value.Call(syntax, method)),                     filter + " on " + call);
			}

			// A type that names no type parameter is decided where it is declared.
			Assert.AreEqual(T, FilterProbe.Answer("returns: \"System.Int32[]\"", _probe.Value.Definition("N.Shapes", "Plain"), MatchPhase.Definition));
			Assert.AreEqual(F, FilterProbe.Answer("returns: \"System.Int64[]\"", _probe.Value.Definition("N.Shapes", "Plain"), MatchPhase.Definition));
		}

		[TestMethod]
		public void AlternativesInsideAlternativesAllocateNothingTest()
		{
			var facts = new RecordFacts("Shop.C<Shop.A>", "M");

			foreach (var (filter, expected) in new[] { ("type: {C,D}<{A,B}*>", T), ("type: {C,D}<{X,Y}<{A,B}*>>", F), ("type: {C,D}<{A,Shop.{A,B}}>", T) })
			{
				var block  = FilterBlockBuilder.Build(CascadeLevel.Aspect, [filter]);
				var target = FilterTarget.Call(facts);

				Assert.AreEqual(expected, FilterProbe.Answer(filter, facts), filter);

				block.Block.Declarations[0].IsSatisfied(target, null!);

				var bytes = GC.GetAllocatedBytesForCurrentThread();

				for (var i = 0; i < 10_000; i++)
					block.Block.Declarations[0].IsSatisfied(target, null!);

				bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;

				Assert.AreEqual(0, bytes / 10_000, $"{filter}: {bytes / 10_000} bytes a decision");
			}
		}
	}
}
