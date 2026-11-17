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

					public static void Calls()
					{
						Make<A>();
						Make<B>();
						int.Parse("1");
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
	}
}
