using System;
using System.Collections.Generic;
using System.Linq;

using AspectGenerator.Cascade;
using AspectGenerator.TargetFilterBinding;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilterBinding
{
	/// <summary>
	/// The conditions over the methods and calls of a compilation: the hierarchy keys, and the rule that a condition
	/// describes the call. A generic method is asked once as a definition, where a pattern that names a type in the
	/// place of a type parameter leaves the answer to the call, and then call by call.
	/// </summary>
	[TestClass]
	public class TypeRelationTests
	{
		const Tri T = Tri.True;
		const Tri F = Tri.False;
		const Tri D = Tri.Depends;

		const string Source =
			"""
			using System;
			using System.Collections.Generic;
			using System.Linq;
			using System.Runtime.CompilerServices;
			using System.Threading.Tasks;

			namespace Shop
			{
				public interface IHandler<T> { void Handle(T command); }
				public interface IEntity { }

				public class Base
				{
					public virtual void Save() { }
					public virtual void Load() { }
					public void Plain() { }
				}

				public class Derived : Base, IEntity
				{
					public override void Save() { }
				}

				public sealed class Leaf : Derived
				{
					public sealed override void Save() { }
					public new void Plain() { }
				}

				public class OrderHandler : IHandler<string>
				{
					public void Handle(string command) { }
					public void Other() { }
				}

				public class GenericHandler<T> : IHandler<T>
				{
					public void Handle(T command) { }
				}

				public abstract class HandlerBase<T> : IHandler<T>
				{
					public abstract void Handle(T command);
				}

				public class IntHandler : HandlerBase<int>
				{
					public override void Handle(int command) { }
				}

				public class Box<T>
				{
					public T Get() { return default!; }
					public void Put(T item) { }
					public U Map<U>(Func<T, U> map) { return default!; }

					public class Inner
					{
						public void Touch(T item) { }
					}
				}

				public static class Repo
				{
					public static T Find<T>(int id) { return default!; }
					public static List<T> All<T>() { return new List<T>(); }
					public static Task<T> LoadAsync<T>() { return null!; }
				}

				// Awaitable where its type argument is an awaiter.
				public class Aw<T>
				{
					public T GetAwaiter() { return default!; }
				}

				public static class AwFactory
				{
					public static Aw<T> Make<T>() { return null!; }
				}

				// Awaitable for its own code only.
				public class Priv
				{
					TaskAwaiter GetAwaiter() { return default; }

					public static Priv Make() { return new Priv(); }

					public void Inside()
					{
						Priv.Make();
					}
				}

				public static class Calls
				{
					public static void Run(
						Derived derived, Leaf leaf, IHandler<string> handler, OrderHandler concrete, IntHandler ints32,
						Box<int> ints, Box<string> strings, List<int> list, IEnumerable<int> numbers, Box<int>.Inner inner)
					{
						derived.Save();
						derived.Load();
						derived.Plain();
						leaf.Save();
						leaf.Plain();
						handler.Handle("x");
						concrete.Handle("x");
						concrete.Other();
						ints32.Handle(1);
						new GenericHandler<int>().Handle(1);
						new GenericHandler<string>().Handle("x");
						ints.Get();
						strings.Get();
						ints.Put(1);
						ints.Map(i => i.ToString());
						inner.Touch(1);
						list.Add(1);
						numbers.Any();
						Enumerable.Any<int>(numbers);
						Repo.Find<int>(1);
						Repo.Find<string>(1);
						Repo.All<int>();
						Repo.LoadAsync<int>();
						AwFactory.Make<TaskAwaiter>();
						AwFactory.Make<int>();
						new Box<Task>().Get();
						Priv.Make();
					}

					public static T Generic<T>(Box<T> box, List<T> list, IEnumerable<T> items, T value)
					{
						box.Put(value);
						list.Add(value);
						items.Any();
						Repo.Find<T>(1);
						Repo.All<T[]>();

						return box.Get();
					}
				}
			}
			""";

		static readonly Lazy<ProbeCompilation> _probe = new(() => new ProbeCompilation(Source));

		static ProbeCompilation Probe => _probe.Value;

		/// <summary>The answer of a filter on the call written <paramref name="call"/>.</summary>
		static Tri OnCall(string filter, string call, bool? overridesIsKey = null)
		{
			return FilterProbe.Answer(filter, Probe.Call(call), MatchPhase.Call, overridesIsKey);
		}

		/// <summary>The answer of a filter on the definition of the method the call written <paramref name="call"/> binds to.</summary>
		static Tri OnDefinition(string filter, string call, bool? overridesIsKey = null)
		{
			var (_, method) = Probe.Calls().First(c => c.Syntax.ToString() == call);

			return FilterProbe.Answer(filter, Probe.Definition(method), MatchPhase.Definition, overridesIsKey);
		}

		static void Check(IEnumerable<(string Filter, string Call, Tri Expected)> rows, bool? overridesIsKey = null)
		{
			var failures = new List<string>();

			foreach (var (filter, call, expected) in rows)
			{
				var actual = OnCall(filter, call, overridesIsKey);

				if (actual != expected)
					failures.Add($"'{filter}' on {call}: {actual}, expected {expected}");
			}

			Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
		}

		[TestMethod]
		public void ACallIsJudgedByTheMethodItBindsToTest()
		{
			Check(
			[
				// An override is the bound method; a method that is not overridden is the base's.
				("type: Derived",                  "derived.Save()",   T),
				("type: Base",                     "derived.Save()",   F),
				("override",                       "derived.Save()",   T),
				("virtual",                        "derived.Save()",   F),
				("type: Derived",                  "derived.Load()",   F),
				("type: Base",                     "derived.Load()",   T),
				("virtual",                        "derived.Load()",   T),
				("type: Base",                     "derived.Plain()",  T),
				("sealed",                         "leaf.Save()",      T),
				("override",                       "leaf.Save()",      T),
				("type: Leaf",                     "leaf.Plain()",     T),

				// A call through an interface is a call of the interface member.
				("method: IHandler<*>.Handle",     "handler.Handle(\"x\")",  T),
				("type: IHandler<string>",         "handler.Handle(\"x\")",  T),
				("type: IHandler<int>",            "handler.Handle(\"x\")",  F),
				("abstract",                       "handler.Handle(\"x\")",  T),
				("method: IHandler<*>.Handle",     "concrete.Handle(\"x\")", F),
				("method: OrderHandler.Handle",    "concrete.Handle(\"x\")", T),
			]);
		}

		[TestMethod]
		public void InheritsIsAboutTheContainingTypeTest()
		{
			Check(
			[
				("inherits: Base",                 "derived.Save()",         T),
				("inherits: Derived",              "derived.Save()",         T),
				("inherits: IEntity",              "derived.Save()",         T),
				("inherits: Leaf",                 "derived.Save()",         F),
				("inherits: object",               "derived.Save()",         T),
				("inherits: global::Shop.Base",    "derived.Save()",         T),
				("inherits: global::Base",         "derived.Save()",         F),

				// The method is the base's: the type that inherits it is not looked at.
				("inherits: Derived",              "derived.Load()",         F),
				("inherits: IEntity",              "derived.Load()",         F),
				("inherits: Base",                 "derived.Load()",         T),
				("inherits: IEntity",              "leaf.Save()",            T),
				("inherits: {IEntity, IHandler<*>}", "concrete.Handle(\"x\")", T),
				("inherits: IHandler<*>",          "concrete.Handle(\"x\")", T),
				("inherits: IHandler<string>",     "concrete.Handle(\"x\")", T),
				("inherits: IHandler<int>",        "concrete.Handle(\"x\")", F),
				("inherits: IHandler<*>",          "handler.Handle(\"x\")",  T),
				("inherits: HandlerBase<int>",     "ints32.Handle(1)",       T),
				("inherits: HandlerBase<*>",       "ints32.Handle(1)",       T),
				("inherits: HandlerBase<string>",  "ints32.Handle(1)",       F),
				("inherits: IHandler<int>",        "ints32.Handle(1)",       T),
				("inherits: IHandler<int>",        "new GenericHandler<int>().Handle(1)",        T),
				("inherits: IHandler<int>",        "new GenericHandler<string>().Handle(\"x\")", F),
			]);
		}

		[TestMethod]
		public void ImplementsCoversOverridesWhereOverridesIsNoKeyTest()
		{
			Check(
			[
				// The interface member itself, and the methods that implement it.
				("implements: IHandler<*>.Handle",       "handler.Handle(\"x\")",  T),
				("implements: IHandler<string>.Handle",  "handler.Handle(\"x\")",  T),
				("implements: IHandler<int>.Handle",     "handler.Handle(\"x\")",  F),
				("implements: IHandler<*>.Handle",       "concrete.Handle(\"x\")", T),
				("implements: IHandler<string>.Handle",  "concrete.Handle(\"x\")", T),
				("implements: IHandler<int>.Handle",     "concrete.Handle(\"x\")", F),
				("implements: IHandler<*>.Handle",       "concrete.Other()",       F),

				// An override implements what the method it overrides implements.
				("implements: IHandler<int>.Handle",     "ints32.Handle(1)",       T),
				("implements: IHandler<string>.Handle",  "ints32.Handle(1)",       F),
				("implements: IHandler<int>.Handle",     "new GenericHandler<int>().Handle(1)",        T),
				("implements: IHandler<int>.Handle",     "new GenericHandler<string>().Handle(\"x\")", F),

				// The members a method overrides, and the method itself.
				("implements: Base.Save",                "derived.Save()",         T),
				("implements: Derived.Save",             "derived.Save()",         T),
				("implements: Base.Load",                "derived.Save()",         F),
				("implements: Base.Save",                "leaf.Save()",            T),
				("implements: Derived.Save",             "leaf.Save()",            T),
				("implements: Base.Load",                "derived.Load()",         T),
				("implements: HandlerBase<int>.Handle",  "ints32.Handle(1)",       T),
				("implements: HandlerBase<*>.Handle",    "ints32.Handle(1)",       T),

				// Hiding is no overriding.
				("implements: Base.Plain",               "leaf.Plain()",           F),
				("implements: Leaf.Plain",               "leaf.Plain()",           T),
			], overridesIsKey: false);
		}

		[TestMethod]
		public void ImplementsAndOverridesAreApartWhereOverridesIsAKeyTest()
		{
			Check(
			[
				("implements: IHandler<*>.Handle",       "handler.Handle(\"x\")",  T),
				("implements: IHandler<*>.Handle",       "concrete.Handle(\"x\")", T),
				("implements: IHandler<int>.Handle",     "ints32.Handle(1)",       T),
				("implements: Base.Save",                "derived.Save()",         F),
				("implements: Derived.Save",             "derived.Save()",         F),
				("implements: OrderHandler.Other",       "concrete.Other()",       F),
				("overrides: Base.Save",                 "derived.Save()",         T),
				("overrides: Derived.Save",              "derived.Save()",         T),
				("overrides: Base.Save",                 "leaf.Save()",            T),
				("overrides: Base.Load",                 "derived.Save()",         F),
				("overrides: Base.Plain",                "leaf.Plain()",           F),
				("overrides: HandlerBase<int>.Handle",   "ints32.Handle(1)",       T),
				("overrides: IHandler<*>.Handle",        "ints32.Handle(1)",       F),
				("overrides: IHandler<*>.Handle",        "handler.Handle(\"x\")",  T),
			], overridesIsKey: true);
		}

		[TestMethod]
		public void OverridesIsRefusedWhereItIsNoKeyTest()
		{
			var block = FilterBlockBuilder.Build(CascadeLevel.Assembly, ["overrides: Base.Save"], overridesIsKey: false);

			Assert.AreEqual("AG0221", block.Diagnostics.Single().Id);

			block = FilterBlockBuilder.Build(CascadeLevel.Assembly, ["overrides: Base.Save"], overridesIsKey: true);

			Assert.AreEqual(0, block.Diagnostics.Length);
		}

		[TestMethod]
		public void AConditionDescribesTheCallTest()
		{
			Check(
			[
				("method: Box<int>.Get",            "ints.Get()",                 T),
				("method: Box<int>.Get",            "strings.Get()",              F),
				("method: Box<*>.Get",              "strings.Get()",              T),
				("type: Box<string>",               "strings.Get()",              T),
				("returns: int",                    "ints.Get()",                 T),
				("returns: int",                    "strings.Get()",              F),
				("returns: string",                 "strings.Get()",              T),
				("params: (int)",                   "ints.Put(1)",                T),
				("params: (string)",                "ints.Put(1)",                F),
				("returns: string",                 "ints.Map(i => i.ToString())", T),
				("method: Box<int>.Map<string>",    "ints.Map(i => i.ToString())", T),
				("method: Box<int>.Map<int>",       "ints.Map(i => i.ToString())", F),
				("params: (Func<int, string>)",     "ints.Map(i => i.ToString())", T),
				("type: Box<int>.Inner",            "inner.Touch(1)",             T),
				("type: Box<string>.Inner",         "inner.Touch(1)",             F),
				("type: Box.Inner",                 "inner.Touch(1)",             T),
				("method: List<int>.Add",           "list.Add(1)",                T),
				("method: List<string>.Add",        "list.Add(1)",                F),
				("method: global::System.Collections.Generic.List<int>.Add", "list.Add(1)", T),
				("method: Find<int>",               "Repo.Find<int>(1)",          T),
				("method: Find<int>",               "Repo.Find<string>(1)",       F),
				("returns: List<int>",              "Repo.All<int>()",            T),
				("returns: Task<int>",              "Repo.LoadAsync<int>()",      T),

				// An extension method is one target in either form of the call, in its static form.
				("method: Enumerable.Any<int>",     "numbers.Any()",              T),
				("method: Enumerable.Any<int>",     "Enumerable.Any<int>(numbers)", T),
				("method: Enumerable.Any<string>",  "numbers.Any()",              F),
				("extension",                       "numbers.Any()",              T),
				("static",                          "numbers.Any()",              T),
				("params: (this IEnumerable<int>)", "numbers.Any()",              T),
				("params: (this IEnumerable<int>)", "Enumerable.Any<int>(numbers)", T),
				("params: ()",                      "numbers.Any()",              F),
			]);
		}

		[TestMethod]
		public void InGenericCodeTheCallersTypeParameterMatchesOnlyAStarTest()
		{
			Check(
			[
				("method: Box<int>.Put",            "box.Put(value)",    F),
				("method: Box<T>.Put",              "box.Put(value)",    F),
				("method: Box<*>.Put",              "box.Put(value)",    T),
				("method: Box.Put",                 "box.Put(value)",    T),
				("params: (int)",                   "box.Put(value)",    F),
				("params: (*)",                     "box.Put(value)",    T),
				("params: (_)",                     "box.Put(value)",    T),
				("method: List<int>.Add",           "list.Add(value)",   F),
				("method: List<*>.Add",             "list.Add(value)",   T),
				("method: Any<int>",                "items.Any()",       F),
				("method: Any<*>",                  "items.Any()",       T),
				("params: (this IEnumerable<*>)",   "items.Any()",       T),
				("method: Find<int>",               "Repo.Find<T>(1)",   F),
				("method: Find<*>",                 "Repo.Find<T>(1)",   T),
				("returns: *",                      "Repo.Find<T>(1)",   T),
				("returns: int",                    "Repo.Find<T>(1)",   F),

				// What is known of the form still counts.
				("method: All<*[]>",                "Repo.All<T[]>()",   T),
				("method: All<int[]>",              "Repo.All<T[]>()",   F),
				("method: All<*[,]>",               "Repo.All<T[]>()",   F),
				("returns: List<*[]>",              "Repo.All<T[]>()",   T),
				("returns: *",                      "box.Get()",         T),
				("returns: int",                    "box.Get()",         F),
			]);
		}

		[TestMethod]
		public void TheDefinitionAnswersWhereNoTypeParameterMeetsANamedTypeTest()
		{
			(string Filter, string Call, Tri Definition)[] rows =
			[
				("method: Box<int>.Get",            "ints.Get()",                 D),
				("method: Box<*>.Get",              "ints.Get()",                 T),
				("method: Box.Get",                 "ints.Get()",                 T),
				("method: Box<int>.Put",            "ints.Get()",                 F),
				("method: Cup<int>.Get",            "ints.Get()",                 F),
				("type: Box<int>",                  "ints.Get()",                 D),
				("type: Box<int, int>",             "ints.Get()",                 F),
				("returns: int",                    "ints.Get()",                 D),
				("returns: *",                      "ints.Get()",                 T),
				("returns: *[]",                    "ints.Get()",                 D),
				("params: (int)",                   "ints.Put(1)",                D),
				("params: (*)",                     "ints.Put(1)",                T),
				("params: (int, int)",              "ints.Put(1)",                F),
				("param: int",                      "ints.Put(1)",                D),
				("method: Map<string>",             "ints.Map(i => i.ToString())", D),
				("method: Map<*>",                  "ints.Map(i => i.ToString())", T),
				("method: Map<*, *>",               "ints.Map(i => i.ToString())", F),
				("params: (Func<*, *>)",            "ints.Map(i => i.ToString())", T),
				("params: (Func<int, *>)",          "ints.Map(i => i.ToString())", D),
				("params: (Action<int>)",           "ints.Map(i => i.ToString())", F),
				("type: Box<int>.Inner",            "inner.Touch(1)",             D),
				("type: Box<*>.Inner",              "inner.Touch(1)",             T),
				("method: List<int>.Add",           "list.Add(1)",                D),
				("method: List<int>.Remove",        "list.Add(1)",                F),
				("method: Enumerable.Any<int>",     "numbers.Any()",              D),
				("method: Enumerable.Any",          "numbers.Any()",              T),
				("params: (this IEnumerable<int>)", "numbers.Any()",              D),
				("params: (this IEnumerable<*>)",   "numbers.Any()",              T),
				("returns: List<int>",              "Repo.All<int>()",            D),
				("returns: List<*>",                "Repo.All<int>()",            T),
				("returns: Task<*>",                "Repo.LoadAsync<int>()",      T),

				// The hierarchy of a generic type is constructed with the call.
				("inherits: IHandler<int>",         "new GenericHandler<int>().Handle(1)", D),
				("inherits: IHandler<*>",           "new GenericHandler<int>().Handle(1)", T),
				("inherits: IEntity",               "new GenericHandler<int>().Handle(1)", F),
				("implements: IHandler<int>.Handle", "new GenericHandler<int>().Handle(1)", D),
				("implements: IHandler<*>.Handle",  "new GenericHandler<int>().Handle(1)", T),
				("implements: IHandler<int>.Other", "new GenericHandler<int>().Handle(1)", F),

				// A method that nothing constructs is decided by its definition.
				("implements: IHandler<int>.Handle", "ints32.Handle(1)",          T),
				("method: IHandler<string>.Handle", "concrete.Handle(\"x\")",     F),
				("public and type: Derived",        "derived.Save()",             T),
			];

			var failures = new List<string>();

			foreach (var (filter, call, expected) in rows)
			{
				var actual = OnDefinition(filter, call);

				if (actual != expected)
					failures.Add($"'{filter}' on the definition of {call}: {actual}, expected {expected}");
			}

			Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
		}

		[TestMethod]
		public void AsyncIsAskedOfTheCallWhereTheDefinitionCannotSayTest()
		{
			// What a type parameter brings can be awaitable.
			Assert.AreEqual(D, OnDefinition("async", "ints.Get()"));
			Assert.AreEqual(F, OnCall("async", "ints.Get()"));
			Assert.AreEqual(T, OnCall("async", "new Box<Task>().Get()"));
			Assert.AreEqual(D, OnDefinition("not async", "ints.Get()"));

			// Task<T> is awaitable whatever T is, and a List<T> is not.
			Assert.AreEqual(T, OnDefinition("async", "Repo.LoadAsync<int>()"));
			Assert.AreEqual(T, OnCall("async", "Repo.LoadAsync<int>()"));
			Assert.AreEqual(F, OnDefinition("async", "Repo.All<int>()"));
			Assert.AreEqual(F, OnCall("async", "Repo.All<int>()"));

			// A type whose awaiter is its type argument.
			Assert.AreEqual(D, OnDefinition("async", "AwFactory.Make<int>()"));
			Assert.AreEqual(F, OnCall("async", "AwFactory.Make<int>()"));
			Assert.AreEqual(T, OnCall("async", "AwFactory.Make<TaskAwaiter>()"));

			// A method of no type parameter whose awaiter only its own type may use: the code that awaits decides.
			Assert.AreEqual(D, OnDefinition("async", "Priv.Make()"));

			var calls = Probe.Calls().Where(static c => c.Syntax.ToString() == "Priv.Make()").ToList();

			Assert.AreEqual(2, calls.Count);
			Assert.AreEqual(T, FilterProbe.Answer("async", Probe.Call(calls[0].Syntax, calls[0].Method)), "inside Priv");
			Assert.AreEqual(F, FilterProbe.Answer("async", Probe.Call(calls[1].Syntax, calls[1].Method)), "outside Priv");

			// A plain method is decided by its definition.
			Assert.AreEqual(F, OnDefinition("async", "derived.Save()"));
		}

		/// <summary>
		/// Whenever the definition answers, every call answers the same: over every filter of this class's tables and
		/// every call of the source.
		/// </summary>
		[TestMethod]
		public void WhatTheDefinitionDecidesEveryCallDecidesTheSameTest()
		{
			string[] filters =
			[
				"method: Box<int>.Get", "method: Box<*>.Get", "method: Box.Get", "type: Box<int>", "type: Box<string>", "type: Box<*>.Inner",
				"type: Box<int>.Inner", "returns: int", "returns: string", "returns: *", "returns: *[]", "returns: *?", "returns: List<int>",
				"returns: List<*>", "returns: List<*[]>", "returns: Task<int>", "returns: Task<*>", "returns: {int, string}", "returns: void",
				"params: (int)", "params: (*)", "params: (_)", "params: ()", "params: (.., int)", "params: (string)", "params: (this IEnumerable<int>)",
				"params: (this IEnumerable<*>)", "params: (Func<int, *>)", "params: (Func<*, *>)", "param: int", "param: string", "param: *",
				"param: this *", "method: Map<string>", "method: Map<*>", "method: Find<int>", "method: Find<*>", "method: Find", "method: All<*[]>",
				"method: All<int[]>", "method: List<int>.Add", "method: List<*>.Add", "method: Enumerable.Any<int>", "method: Any<*>", "method: *",
				"method: Handle", "method: IHandler<*>.Handle", "method: IHandler<string>.Handle", "method: {Get,Put}", "method: {Box<int>.Get,Find<*>}",
				"inherits: IHandler<int>", "inherits: IHandler<*>", "inherits: IHandler<string>", "inherits: Base", "inherits: HandlerBase<int>",
				"inherits: object", "inherits: Box<int>", "implements: IHandler<int>.Handle", "implements: IHandler<*>.Handle",
				"implements: IHandler<string>.Handle", "implements: Base.Save", "implements: Box<int>.Get", "implements: Box<*>.Get",
				"async", "not async", "public", "static", "extension", "virtual", "override", "abstract", "sealed", "project", "generated", "extern",
				"namespace: Shop", "namespace: System.**", "attribute: *", "not method: Box<int>.Get", "not returns: int",
				"returns: int and method: Get", "method: Get and returns: int", "returns: int or public", "public or returns: int",
				"Get: int", "Box<int>.Put(int)", "public Find<int>(int): int", "static Any<int>(this IEnumerable<int>): bool",
			];

			var failures = new List<string>();
			var decided  = 0;
			var depends  = 0;
			var calls    = Probe.Calls().ToList();

			Assert.IsTrue(calls.Count >= 30, $"Only {calls.Count} calls.");

			foreach (var filter in filters)
			{
				foreach (var (syntax, method) in calls)
				{
					var onDefinition = FilterProbe.Answer(filter, Probe.Definition(method), MatchPhase.Definition);
					var onCall       = FilterProbe.Answer(filter, Probe.Call(syntax, method), MatchPhase.Call);

					Assert.AreNotEqual(D, onCall, $"'{filter}' on {syntax}: a call always answers");

					if (onDefinition == D)
					{
						depends++;
						continue;
					}

					decided++;

					if (onDefinition != onCall)
						failures.Add($"'{filter}' on {syntax}: the definition says {onDefinition}, the call {onCall}");
				}
			}

			Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
			Assert.IsTrue(decided > 1000, $"Only {decided} answers of a definition.");
			Assert.IsTrue(depends > 100,  $"Only {depends} answers left to the call.");
		}

		/// <summary>The same through the cascade: a decision made on the definition is the decision of every call.</summary>
		[TestMethod]
		public void ADecisionOfTheDefinitionHoldsForEveryCallTest()
		{
			var aspect   = FilterBlockBuilder.Build(CascadeLevel.Aspect,   ["- returns: string"]);
			var assembly = FilterBlockBuilder.Build(CascadeLevel.Assembly, ["method: {Box<int>.*, Find<*>, List<*>.Add, IHandler<*>.Handle}\n- params: (.., string)"], new Dictionary<string, object?> { ["Level"] = "Debug" });

			Assert.AreEqual(0, aspect.Diagnostics.Length + assembly.Diagnostics.Length);

			var evaluator = new CascadeEvaluator<FilterTarget>(aspect.Block, [assembly.Block], [], static t => t.Facts.IsProject);
			var context   = evaluator.Prepare([]);
			var decided   = 0;
			var perCall   = 0;
			var selected  = new List<string>();

			foreach (var (syntax, method) in Probe.Calls())
			{
				var call = FilterTarget.DecideCall(Probe.Call(syntax, method), t => evaluator.Decide(t, context, []));

				if (call.Selected)
					selected.Add(syntax.ToString());

				if (!FilterTarget.TryDecideDefinition(Probe.Definition(method), t => evaluator.Decide(t, context, []), out var definition))
				{
					perCall++;
					continue;
				}

				decided++;

				Assert.AreEqual(definition.Selected, call.Selected, syntax.ToString());
			}

			Assert.IsTrue(decided > 10 && perCall > 5, $"{decided} decided on the definition, {perCall} per call.");

			CollectionAssert.AreEqual(
				new[] { "ints.Get()", "ints.Put(1)", "list.Add(1)", "Repo.Find<int>(1)", "list.Add(value)", "Repo.Find<T>(1)" },
				selected);
		}
	}
}
