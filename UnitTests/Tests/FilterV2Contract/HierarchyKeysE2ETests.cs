using System;
using System.Collections.Generic;
using System.Linq;

using AspectGenerator.FilterV2;
using AspectGenerator.Tests.FilterV2Contract;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// The hierarchy keys through the generator, for the choice the language makes today about <c>overrides:</c>; the
	/// block-level tests run both. What depends on a key's name goes through <see cref="HierarchyKeys"/>.
	/// </summary>
	public partial class CompilerTests
	{
		const string HierarchySource =
			"""
			namespace AspectGenerator.Tests.GeneratorDriver
			{
				interface IHandler<T> { void Handle(T command); }
				interface IEntity { }
				interface IRun { void Run(); }

				class Base
				{
					public virtual void Save() { }
					public virtual void Load() { }
				}

				class Derived : Base, IEntity
				{
					public override void Save() { }
				}

				class OrderHandler : IHandler<string>
				{
					public void Handle(string command) { }
					public void Other() { }
				}

				abstract class HandlerBase<T> : IHandler<T>
				{
					public abstract void Handle(T command);
				}

				class IntHandler : HandlerBase<int>
				{
					public override void Handle(int command) { }
				}

				// Plan.Run is what implements IRun for Job; Plan itself does not implement IRun.
				class Plan
				{
					public void Run() { }
				}

				class Job : Plan, IRun { }

				static class Calls
				{
					public static void Run(Derived derived, OrderHandler concrete, IntHandler ints, Job job, IHandler<string> handler)
					{
						derived.Save();
						derived.Load();
						concrete.Handle("x");
						concrete.Other();
						ints.Handle(1);
						job.Run();
						handler.Handle("x");
					}
				}
			}
			""";

		static readonly string[] HierarchyCalls = ["derived.Save()", "derived.Load()", "concrete.Handle(\"x\")", "concrete.Other()", "ints.Handle(1)", "job.Run()", "handler.Handle(\"x\")"];

		/// <summary>The calls of <see cref="HierarchySource"/> that the assembly filter selects, in the order of <see cref="HierarchyCalls"/>.</summary>
		static string HierarchySelected(string filter, out string diagnostics)
		{
			var result = RunSelection("[assembly: AspectGenerator.Tests.GeneratorDriver.Log(TargetFilter = \"" + filter + "\")]\n" + HierarchySource);

			diagnostics = DescribeDiagnostics(result);

			return string.Join(", ", HierarchyCalls.Where(c => CountIntercepted(result, c) > 0));
		}

		static void AssertHierarchy(string filter, params string[] expected)
		{
			var selected = HierarchySelected(filter, out var diagnostics);

			Assert.AreEqual(string.Join(", ", expected), selected, filter + Environment.NewLine + diagnostics);
		}

		[TestMethod]
		public void InheritsSelectsByTheContainingTypeOfTheBoundMethodTest()
		{
			// The class, its bases, the interfaces: the type of the bound method (a base method is the base's).
			AssertHierarchy(HierarchyKeys.InheritsFilter("Base"),       "derived.Save()", "derived.Load()");
			AssertHierarchy(HierarchyKeys.InheritsFilter("Derived"),    "derived.Save()");
			AssertHierarchy(HierarchyKeys.InheritsFilter("IEntity"),    "derived.Save()");
			AssertHierarchy(HierarchyKeys.InheritsFilter("IHandler<*>"), "concrete.Handle(\"x\")", "concrete.Other()", "ints.Handle(1)", "handler.Handle(\"x\")");
			AssertHierarchy(HierarchyKeys.InheritsFilter("IHandler<int>"), "ints.Handle(1)");
			AssertHierarchy(HierarchyKeys.InheritsFilter("HandlerBase<*>"), "ints.Handle(1)");
			AssertHierarchy(HierarchyKeys.InheritsFilter("IRun"));
		}

		[TestMethod]
		public void ImplementsSelectsTheMethodsThatImplementAnInterfaceMemberTest()
		{
			AssertHierarchy(HierarchyKeys.ImplementsFilter("IHandler<*>.Handle"), "concrete.Handle(\"x\")", "ints.Handle(1)", "handler.Handle(\"x\")");
			AssertHierarchy(HierarchyKeys.ImplementsFilter("IHandler<string>.Handle"), "concrete.Handle(\"x\")", "handler.Handle(\"x\")");

			// The base method that only a derived type uses to implement an interface does not count for the base.
			AssertHierarchy(HierarchyKeys.ImplementsFilter("IRun.Run"));

			// The method itself counts.
			AssertHierarchy(HierarchyKeys.ImplementsFilter("OrderHandler.Other"), "concrete.Other()");
		}

		[TestMethod]
		public void OverridesAreCoveredAsTheChoiceSaysTest()
		{
			var coversOverrides = HierarchyKeys.ImplementsCoversOverrides(FilterV2Language.OverridesIsKey);

			AssertHierarchy(HierarchyKeys.ImplementsFilter("Base.Save"), coversOverrides ? ["derived.Save()"] : []);

			// Where overrides is a key of its own it selects what overrides a matching member; where it is not the key is an
			// error, and the aspect selects nothing.
			var selected = HierarchySelected(HierarchyKeys.OverridesFilter("Base.Save"), out var diagnostics);

			if (FilterV2Language.OverridesIsKey)
			{
				Assert.AreEqual("derived.Save()", selected, diagnostics);
			}
			else
			{
				Assert.AreEqual("", selected, diagnostics);
				StringAssert.Contains(diagnostics, AspectDiagnosticID.FilterUnknownKeyOrWord);
			}
		}
	}
}
