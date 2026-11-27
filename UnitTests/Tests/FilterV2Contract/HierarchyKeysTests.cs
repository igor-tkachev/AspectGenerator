using System;
using System.Collections.Generic;
using System.Linq;

using AspectGenerator.Cascade;
using AspectGenerator.FilterV2;
using AspectGenerator.TargetFilterBinding;
using AspectGenerator.Tests.TargetFilterBinding;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.FilterV2Contract
{
	/// <summary>
	/// The names of the hierarchy keys, in one place: what a name decides is open, so every expectation that depends on one
	/// is written through these, and a change of a name is a change here.
	/// </summary>
	static class HierarchyKeys
	{
		public const string Inherits   = "inherits";
		public const string Implements = "implements";
		public const string Overrides  = "overrides";

		public static string InheritsFilter(string pattern)
		{
			return Inherits + ": " + pattern;
		}

		public static string ImplementsFilter(string pattern)
		{
			return Implements + ": " + pattern;
		}

		public static string OverridesFilter(string pattern)
		{
			return Overrides + ": " + pattern;
		}

		/// <summary>Whether <c>implements:</c> covers a method that overrides what a matching member implements: it does where <c>overrides:</c> is no key.</summary>
		public static bool ImplementsCoversOverrides(bool overridesIsKey)
		{
			return !overridesIsKey;
		}
	}

	/// <summary>
	/// The hierarchy keys at the level of a block, under both values of the choice whether <c>overrides:</c> is a key of
	/// its own. The facts of the real symbols come from a compilation; the row lists say what each key matches and what it
	/// does not: the base method that only a derived type uses to implement an interface does not count for the base.
	/// The rows of <c>TypeRelationTests</c> hold the other cases of each key (inherits over a class, a chain and an
	/// interface; implements over an interface member, an override chain, a constructed call and the method itself).
	/// </summary>
	[TestClass]
	public class HierarchyKeysTests
	{
		const Tri T = Tri.True;
		const Tri F = Tri.False;

		const string Source =
			"""
			namespace Hier
			{
				public interface IRun  { void Run(); }
				public interface IPing { void Ping(); }

				public class Plan
				{
					public void Run() { }
					public virtual void Ping() { }
				}

				// Plan.Run is what implements IRun for Job; Plan itself does not implement IRun.
				public class Job : Plan, IRun { }

				public class Pinger : Plan, IPing
				{
					public override void Ping() { }
				}

				public class Both : IRun
				{
					public void Run() { }
				}

				public static class Calls
				{
					public static void Go(Job job, Plan plan, Both both, Pinger pinger)
					{
						job.Run();
						plan.Run();
						both.Run();
						pinger.Ping();
						plan.Ping();
					}
				}
			}
			""";

		static readonly Lazy<ProbeCompilation> _probe = new(() => new ProbeCompilation(Source));

		static void Check(bool overridesIsKey, IEnumerable<(string Filter, string Call, Tri Expected)> rows)
		{
			var failures = new List<string>();

			foreach (var (filter, call, expected) in rows)
			{
				var actual = FilterProbe.Answer(filter, _probe.Value.Call(call), MatchPhase.Call, overridesIsKey);

				if (actual != expected)
					failures.Add($"overrides is a key: {overridesIsKey}; '{filter}' on {call}: {actual}, expected {expected}");
			}

			Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
		}

		[TestMethod]
		public void ABaseMethodThatOnlyADerivedTypeUsesToImplementDoesNotCountTest()
		{
			// The same under both values: Plan.Run is the bound method, and Plan does not implement IRun.
			foreach (var overridesIsKey in new[] { false, true })
			{
				Check(overridesIsKey,
				[
					(HierarchyKeys.ImplementsFilter("IRun.Run"), "job.Run()",     F),
					(HierarchyKeys.ImplementsFilter("IRun.Run"), "plan.Run()",    F),
					(HierarchyKeys.ImplementsFilter("IRun.Run"), "both.Run()",    T),
					(HierarchyKeys.InheritsFilter("IRun"),       "job.Run()",     F),
					(HierarchyKeys.InheritsFilter("IRun"),       "both.Run()",    T),
					(HierarchyKeys.InheritsFilter("Plan"),       "job.Run()",     T),

					// An override implements through its own declaring type; the base method it replaces does not.
					(HierarchyKeys.ImplementsFilter("IPing.Ping"), "pinger.Ping()", T),
					(HierarchyKeys.ImplementsFilter("IPing.Ping"), "plan.Ping()",   F),
					(HierarchyKeys.InheritsFilter("IPing"),        "pinger.Ping()", T),
					(HierarchyKeys.InheritsFilter("IPing"),        "plan.Ping()",   F),
				]);
			}
		}

		[TestMethod]
		public void OverridesDependsOnTheChoiceOfTheKeyTest()
		{
			// Where it is a key it matches the method that overrides a matching member, and `implements:` leaves overriding alone.
			Check(true,
			[
				(HierarchyKeys.OverridesFilter("Plan.Ping"),   "pinger.Ping()", T),
				(HierarchyKeys.OverridesFilter("Plan.Ping"),   "plan.Ping()",   T),
				(HierarchyKeys.OverridesFilter("Plan.Run"),    "job.Run()",     T),
				(HierarchyKeys.OverridesFilter("IPing.Ping"),  "pinger.Ping()", F),
				(HierarchyKeys.ImplementsFilter("Plan.Ping"),  "pinger.Ping()", F),
			]);

			// Where it is not, `implements:` covers it and the other key is unknown.
			Check(false,
			[
				(HierarchyKeys.ImplementsFilter("Plan.Ping"),  "pinger.Ping()", T),
				(HierarchyKeys.ImplementsFilter("Plan.Ping"),  "plan.Ping()",   T),
				(HierarchyKeys.ImplementsFilter("IPing.Ping"), "pinger.Ping()", T),
			]);
		}

		[TestMethod]
		public void TheKeysAreReadAndRefusedAsTheChoiceSaysTest()
		{
			foreach (var overridesIsKey in new[] { false, true })
			{
				foreach (var filter in new[] { HierarchyKeys.InheritsFilter("Base"), HierarchyKeys.ImplementsFilter("IHandler<*>.Handle"), HierarchyKeys.ImplementsFilter("Base.Save and public") })
				{
					var reading = FilterV2Reader.Read(filter, overridesIsKey);

					Assert.AreEqual(0, reading.Diagnostics.Length, $"{filter}, overrides is a key: {overridesIsKey}: {string.Join("; ", reading.Diagnostics)}");

					var block = FilterBlockBuilder.Build(CascadeLevel.Assembly, [filter], overridesIsKey: overridesIsKey);

					Assert.AreEqual(0, block.Diagnostics.Length, $"{filter}, overrides is a key: {overridesIsKey}");
				}

				var overrides = FilterV2Reader.Read(HierarchyKeys.OverridesFilter("Base.Save"), overridesIsKey);
				var built     = FilterBlockBuilder.Build(CascadeLevel.Assembly, [HierarchyKeys.OverridesFilter("Base.Save")], overridesIsKey: overridesIsKey);

				if (overridesIsKey)
				{
					Assert.AreEqual(0, overrides.Diagnostics.Length);
					Assert.AreEqual(0, built.Diagnostics.Length);
				}
				else
				{
					// An unknown key, and the message names the key that covers it.
					Assert.AreEqual(FilterV2DiagnosticID.UnknownOrReserved, overrides.Diagnostics.Single().Id);
					StringAssert.Contains(overrides.Diagnostics.Single().Message, $"'{HierarchyKeys.Implements}:'");
					Assert.AreEqual(AspectDiagnosticID.FilterUnknownKeyOrWord, built.Diagnostics.Single().Id);
				}

				// The reference to the property of the key follows the same choice: a valid reference only where it is a key.
				var reference = FilterV2Reader.Read(HierarchyKeys.Overrides + ": assembly." + HierarchyKeys.Overrides, overridesIsKey);

				Assert.AreEqual(overridesIsKey, reference.Diagnostics.Length == 0, string.Join("; ", reference.Diagnostics));
			}
		}
	}
}
