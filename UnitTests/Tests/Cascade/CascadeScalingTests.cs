using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using AspectGenerator.Cascade;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.Cascade
{
	/// <summary>
	/// The cost of deciding many methods against many blocks: 10,000 methods in 100 types, 20 assembly blocks, 10 blocks
	/// on every type and 3 on every method, i.e. 600 combinations of blocks per method. The decisions of a type share
	/// one plan (<c>Prepare</c>), as selection will use it.
	/// </summary>
	[TestClass]
	public class CascadeScalingTests
	{
		public TestContext TestContext { get; set; } = null!;

		const int AssemblyBlocks = 20;
		const int TypeBlocks     = 10;
		const int MethodBlocks   = 3;
		const int Types          = 100;
		const int MethodsPerType = 100;

		static CascadeLine<CaseFact> Include(CascadeCondition<CaseFact> condition)
		{
			return new(false, condition);
		}

		static CascadeTest<CaseFact> Test(CascadeProperty property, string text, Func<CaseFact,bool> matches)
		{
			return new(property, text, matches);
		}

		sealed record Setup(
			CascadeEvaluator<CaseFact>                                            Evaluator,
			List<(string Type, List<CascadeBlock<CaseFact>> Blocks)>              TypeBlocks,
			List<CaseFact>                                                        Facts,
			Dictionary<string,List<CascadeBlock<CaseFact>>>                       MethodBlocks);

		static Setup Create()
		{
			var aspectDefault = CascadeBlock<CaseFact>.Create(CascadeLevel.Aspect,
				[new(true, Test(CascadeProperty.Attribute, "attribute: NoLog", f => f.Attributes.Contains("NoLogAttribute")))]);

			var assembly = Enumerable.Range(0, AssemblyBlocks).Select(i => CascadeBlock<CaseFact>.Create(
				CascadeLevel.Assembly,
				[Include(Test(CascadeProperty.Location, "namespace: Shop.**", f => f.Namespace.StartsWith("Shop", StringComparison.Ordinal)))],
				new Dictionary<string,object?> { ["Level"] = "Debug", ["Tags"] = new[] { "a", "b" } },
				$"assembly#{i}")).ToList();

			var types   = new List<(string, List<CascadeBlock<CaseFact>>)>();
			var methods = new Dictionary<string,List<CascadeBlock<CaseFact>>>();
			var facts   = new List<CaseFact>();
			var others  = new List<CascadeBlock<CaseFact>>();

			for (var t = 0; t < Types; t++)
			{
				var type = $"Shop.Services.Service{t}";

				var typeBlocks = Enumerable.Range(0, TypeBlocks).Select(j => CascadeBlock<CaseFact>.Create(
					CascadeLevel.Type,
					j % 2 == 0
						? [Include(Test(CascadeProperty.Accessibility, "public", f => f.Accessibility == "public")),
						   Include(new CascadeReference<CaseFact>(CascadeLevel.Aspect, CascadeProperty.Attribute, CascadeProperty.Attribute))]
						: [Include(Test(CascadeProperty.Static, "instance", f => !f.Static))],
					j % 3 == 0 ? new Dictionary<string,object?> { ["Format"] = "F" } : null,
					$"type:{type}#{j}")).ToList();

				types.Add((type, typeBlocks));
				others.AddRange(typeBlocks);

				for (var m = 0; m < MethodsPerType; m++)
				{
					var fact = new CaseFact
					{
						Id            = $"{type}.M{m}",
						Namespace     = "Shop.Services",
						Type          = type,
						Name          = $"M{m}",
						Accessibility = m % 4 == 0 ? "internal" : "public",
						Static        = m % 5 == 0,
						Project       = true,
						Attributes    = m % 7 == 0 ? ["NoLogAttribute"] : [],
					};

					var methodBlocks = Enumerable.Range(0, MethodBlocks).Select(k => CascadeBlock<CaseFact>.Create(
						CascadeLevel.Method,
						k == 0
							? [Include(new CascadeReference<CaseFact>(CascadeLevel.Assembly, CascadeProperty.Location, CascadeProperty.Location))]
							: [],
						k == 2 ? new Dictionary<string,object?> { ["Level"] = "Trace" } : null,
						$"method:{fact.Id}#{k}")).ToList();

					facts.Add(fact);
					methods[fact.Id] = methodBlocks;
					others.AddRange(methodBlocks);
				}
			}

			return new(new(aspectDefault, assembly, others, f => f.Project), types, facts, methods);
		}

		[TestMethod]
		public void TenThousandMethodsTest()
		{
			var setup    = Create();
			var selected = 0;
			var bytes    = GC.GetAllocatedBytesForCurrentThread();
			var clock    = Stopwatch.StartNew();

			foreach (var (type, typeBlocks) in setup.TypeBlocks)
			{
				var context = setup.Evaluator.Prepare(typeBlocks);

				foreach (var fact in setup.Facts.Where(f => f.Type == type))
				{
					var decision = setup.Evaluator.Decide(fact, context, setup.MethodBlocks[fact.Id]);

					if (decision.Selected)
						selected++;
				}
			}

			clock.Stop();

			bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;

			TestContext.WriteLine($"{setup.Facts.Count} methods, {AssemblyBlocks}x{TypeBlocks}x{MethodBlocks} blocks: {clock.ElapsedMilliseconds} ms, {bytes / (1024 * 1024)} MB allocated, {selected} selected");

			Assert.AreEqual(8100, selected);

			// The allocation is deterministic, unlike the time on a shared machine: a plan per combination of blocks
			// allocated 4.7 GB here, the plans per type and the parts per method about 120 MB.
			Assert.IsTrue(bytes < 512L * 1024 * 1024, $"{bytes / (1024 * 1024)} MB allocated");
		}
	}
}
