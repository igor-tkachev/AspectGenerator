using System;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	public partial class CompilerTests
	{
		[TestMethod]
		[DataRow(true, true, false, 2)]
		[DataRow(true, false, true, 2)]
		[DataRow(true, true, true, 2)]
		[DataRow(false, true, false, 2)]
		[DataRow(false, false, true, 2)]
		[DataRow(false, true, true, 2)]
		[DataRow(true, true, true, 3)]
		[DataRow(false, true, true, 3)]
		public void Review_MultipleAsyncAspectsTest(bool returnsValue, bool outerAsync, bool innerAsync, int count)
		{
			var applications = $"[{(outerAsync ? "Async" : "Sync")}(\"A\")][{(innerAsync ? "Async" : "Sync")}(\"B\")]";

			if (count == 3)
				applications += "[Async(\"C\")]";

			var source = AsyncChainSource
				.Replace("APPLICATIONS", applications, StringComparison.Ordinal)
				.Replace("RETURNTYPE", returnsValue ? "Task<int>" : "Task", StringComparison.Ordinal)
				.Replace("RETURNVALUE", returnsValue ? "return 42;" : "", StringComparison.Ordinal)
				.Replace("INVOKE", returnsValue ? "var result = Target().GetAwaiter().GetResult();" : "Target().GetAwaiter().GetResult(); var result = 0;", StringComparison.Ordinal);
			var expected = count == 3 ? "A.before;B.before;C.before;target;C.after;B.after;A.after;" : "A.before;B.before;target;B.after;A.after;";

			Assert.AreEqual("AG0003=0 | " + expected + (returnsValue ? "42" : "0"), RunReviewProbe([source]));
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public void Review_TaskChainWaitsForTargetTest(bool valueTask)
		{
			Assert.AreEqual("AG0003=0 | A.before;B.before;target;pending;B.after;A.after;", RunReviewProbe([
				"""
				using System;
				using System.Threading.Tasks;
				using AspectGenerator;
				[Aspect(OnBeforeCallAsync = nameof(Before), OnAfterCallAsync = nameof(After))]
				[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
				sealed class AwaitingAttribute(string name) : Attribute
				{
					public string Name = name;
					public static Task Before(AwaitingAttribute aspect, InterceptInfo info) { Calls.Events += aspect.Name + ".before;"; return Task.CompletedTask; }
					public static Task After(AwaitingAttribute aspect, InterceptInfo info) { Calls.Events += aspect.Name + (Calls.Gate.Task.IsCompleted ? ".after;" : ".early;"); return Task.CompletedTask; }
				}
				static class Calls
				{
					public static string Events = "";
					public static readonly TaskCompletionSource<bool> Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
					[Awaiting("A")][Awaiting("B")]
					public static RETURNTYPE Target() { Events += "target;"; return RETURNVALUE; }
					public static string Run()
					{
						var task = Target();
						Events += task.IsCompleted ? "completed;" : "pending;";
						Gate.SetResult(true);
						task.GetAwaiter().GetResult();
						return Events;
					}
				}
				""".Replace("RETURNTYPE", valueTask ? "ValueTask" : "Task", StringComparison.Ordinal)
					.Replace("RETURNVALUE", valueTask ? "new ValueTask(Gate.Task)" : "Gate.Task", StringComparison.Ordinal)
			]));
		}

		[TestMethod]
		public void Review_OnCallValidAfterSortingTest()
		{
			var source = OrderedOnCallSource.Replace("APPLICATIONS", "[Replacement(Order = 2)][Observer(Order = 1)]", StringComparison.Ordinal);
			var result = RunGenerator(source);

			AssertNoDiagnostic(result, AspectDiagnosticID.OnCallNotLastAspect);
			Assert.AreEqual("AG0003=0 | before;call;after;42", RunReviewProbe([source]));
		}

		[TestMethod]
		public void Review_OnCallInvalidAfterSortingTest()
		{
			var source = OrderedOnCallSource.Replace("APPLICATIONS", "[Observer(Order = 2)][Replacement(Order = 1)]", StringComparison.Ordinal);
			var result = RunGeneratorAndGetCompilation(source, out var compilation);

			Assert.AreEqual(1, result.Diagnostics.Count(static d => d.Id == AspectDiagnosticID.OnCallNotLastAspect));
			Assert.AreEqual(0, compilation.GetDiagnostics().Count(static d => d.Severity == DiagnosticSeverity.Error));
		}

		[TestMethod]
		[DataRow("normal", "A.init;A.using;A.before;B.init;B.using;B.before;C.init;C.using;C.before;target;C.after;C.finally;C.dispose;B.after;B.finally;B.dispose;A.after;A.finally;A.dispose;3")]
		[DataRow("inner-return", "A.init;A.using;A.before;B.init;B.using;B.before;C.init;C.using;C.before;C.finally;C.dispose;B.after;B.finally;B.dispose;A.after;A.finally;A.dispose;7")]
		[DataRow("outer-return", "A.init;A.using;A.before;A.finally;A.dispose;7")]
		[DataRow("inner-catch", "A.init;A.using;A.before;B.init;B.using;B.before;C.init;C.using;C.before;target;C.catch:boom;C.finally;C.dispose;B.after;B.finally;B.dispose;A.after;A.finally;A.dispose;7")]
		[DataRow("outer-after", "A.init;A.using;A.before;B.init;B.using;B.before;C.init;C.using;C.before;target;C.after;C.finally;C.dispose;B.after;B.finally;B.dispose;A.after;A.finally;A.dispose;13")]
		[DataRow("oncall", "A.init;A.using;A.before;B.init;B.using;B.before;call;B.after;B.finally;B.dispose;A.after;A.finally;A.dispose;5")]
		[DataRow("normal-two", "A.init;A.using;A.before;C.init;C.using;C.before;target;C.after;C.finally;C.dispose;A.after;A.finally;A.dispose;3")]
		[DataRow("inner-catch-two", "A.init;A.using;A.before;C.init;C.using;C.before;target;C.catch:boom;C.finally;C.dispose;A.after;A.finally;A.dispose;7")]
		public void Review_NestedLifecycleContractTest(string scenario, string expected)
		{
			var twoAspects = scenario.EndsWith("-two", StringComparison.Ordinal);
			var source = NestedLifecycleSource.Replace("SCENARIO", twoAspects ? scenario[..^4] : scenario, StringComparison.Ordinal);

			if (twoAspects)
				source = source.Replace("[Layer(\"B\")]", "", StringComparison.Ordinal);

			if (scenario == "oncall")
				source = source.Replace("[Layer(\"C\")]", "[Replacement]", StringComparison.Ordinal);

			Assert.AreEqual("AG0003=0 | " + expected, RunReviewProbe([source]));
		}

		[TestMethod]
		[DataRow(false)]
		[DataRow(true)]
		public void Review_MixedInfoAndRefDataChainTest(bool dataOutside)
		{
			var source = MixedCarrierSource.Replace("APPLICATIONS", dataOutside ? "[Data][Info][Data]" : "[Info][Data][Info]", StringComparison.Ordinal);
			// PreviousInfo links only reference carriers; a preceding InterceptData<T> cannot occupy that field.
			var expected = dataOutside ? "D.before;I.before;D.before:I;target;D.after:1;I.after:101;D.after:111;211" : "I.before;D.before:I;I.before;target;I.after:1;D.after:11;I.after:111;121";

			Assert.AreEqual("AG0003=0 | " + expected, RunReviewProbe([source]));
		}

		[TestMethod]
		public void Review_TargetFilterIsCompileTimeMetadataTest()
		{
			Assert.AreEqual("AG0003=0 | default:configured;42", RunReviewProbe([
				"""
				using System;
				using AspectGenerator;
				[assembly: Filtered(TargetFilter = "method: Target", Name = "configured")]
				[Aspect(OnBeforeCall = nameof(Before))]
				[AttributeUsage(AttributeTargets.Assembly)]
				sealed class FilteredAttribute : Attribute
				{
					public string? TargetFilter { get; set; } = "default";
					public string? Name { get; set; }
					public static void Before(FilteredAttribute aspect, InterceptInfo info)
					{
						Calls.Events += aspect.TargetFilter + ":" + aspect.Name + ";";
					}
				}
				static class Calls
				{
					public static string Events = "";
					public static int Target() { return 42; }
					public static string Run() { var value = Target(); return Events + value; }
				}
				"""
			]));
		}

		[TestMethod]
		public void Review_StaticConstructionFailureTest()
		{
			Assert.AreEqual("AG0003=0 | TypeInitializationException:InvalidOperationException:ctor", RunReviewProbe([
				"""
				using System;
				using AspectGenerator;
				[Aspect(OnBeforeCall = nameof(Before), Lifetime = AspectInstanceLifetime.Static)]
				sealed class BrokenAttribute : Attribute
				{
					public BrokenAttribute() { throw new InvalidOperationException("ctor"); }
					public static void Before(InterceptInfo info) { }
				}
				static class Calls
				{
					[Broken] public static int Target() { return 42; }
					public static string Run()
					{
						try { return Target().ToString(); }
						catch (TypeInitializationException ex) { return ex.GetType().Name + ":" + ex.InnerException!.GetType().Name + ":" + ex.InnerException.Message; }
					}
				}
				"""
			]));
		}

		[TestMethod]
		public void Review_StaticInstanceSharedAcrossThreadsTest()
		{
			Assert.AreEqual("AG0003=0 | True:2", RunReviewProbe([
				"""
				using System;
				using System.Linq;
				using System.Threading;
				using System.Collections.Concurrent;
				using AspectGenerator;
				[Aspect(OnBeforeCall = nameof(Before), Lifetime = AspectInstanceLifetime.Static)]
				sealed class SharedAttribute : Attribute
				{
					public static readonly ConcurrentBag<(SharedAttribute Aspect, int Thread)> Seen = new();
					public static void Before(SharedAttribute aspect, InterceptInfo info) { Seen.Add((aspect, Environment.CurrentManagedThreadId)); }
				}
				static class Calls
				{
					[Shared] public static void Target() { }
					static void Worker() { Target(); }
					public static string Run()
					{
						var first = new Thread(Worker);
						var second = new Thread(Worker);
						first.Start(); second.Start(); first.Join(); second.Join();
						var seen = SharedAttribute.Seen.ToArray();
						return ReferenceEquals(seen[0].Aspect, seen[1].Aspect) + ":" + seen.Select(x => x.Thread).Distinct().Count();
					}
				}
				"""
			]));
		}

		static string RunReviewProbe(string[] sources)
		{
			var result = RunGeneratorAndGetCompilation(sources, out var compilation);
			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");
			var diagnostics = result.Diagnostics.Concat(compilation.GetDiagnostics().Where(static d =>
				d.Severity == DiagnosticSeverity.Error ||
				d.Severity == DiagnosticSeverity.Warning && d.Location.SourceTree?.FilePath.EndsWith("Interceptors.g.cs", StringComparison.Ordinal) == true))
				.Where(static d => d.Severity >= DiagnosticSeverity.Warning).ToArray();

			Assert.AreEqual(0, diagnostics.Length, string.Join(Environment.NewLine, diagnostics.Select(static d => d.ToString())) + Environment.NewLine + interceptors);

			return "AG0003=0 | " + EmitAndRun(compilation, interceptors);
		}

		const string AsyncChainSource =
			"""
			using System;
			using System.Threading.Tasks;
			using AspectGenerator;
			[Aspect(OnBeforeCall = nameof(Before), OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
			sealed class SyncAttribute(string name) : Attribute
			{
				public static void Before(SyncAttribute aspect, InterceptInfo info) { Calls.Events += aspect.Name + ".before;"; }
				public static void After(SyncAttribute aspect, InterceptInfo info) { Calls.Events += aspect.Name + ".after;"; }
				public string Name = name;
			}
			[Aspect(OnBeforeCallAsync = nameof(Before), OnAfterCallAsync = nameof(After))]
			[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
			sealed class AsyncAttribute(string name) : Attribute
			{
				public static async Task Before(AsyncAttribute aspect, InterceptInfo info) { await Task.Yield(); Calls.Events += aspect.Name + ".before;"; }
				public static async Task After(AsyncAttribute aspect, InterceptInfo info) { await Task.Yield(); Calls.Events += aspect.Name + ".after;"; }
				public string Name = name;
			}
			static class Calls
			{
				public static string Events = "";
				APPLICATIONS
				public static async RETURNTYPE Target() { await Task.Yield(); Events += "target;"; RETURNVALUE }
				public static string Run() { INVOKE return Events + result; }
			}
			""";

		const string OrderedOnCallSource =
			"""
			using System;
			using AspectGenerator;
			[Aspect(OnBeforeCall = nameof(Before), OnAfterCall = nameof(After))]
			sealed class ObserverAttribute : Attribute
			{
				public int Order { get; set; }
				public static void Before(InterceptInfo info) { Calls.Events += "before;"; }
				public static void After(InterceptInfo info) { Calls.Events += "after;"; }
			}
			[Aspect(OnCall = nameof(Call))]
			sealed class ReplacementAttribute : Attribute
			{
				public int Order { get; set; }
				public static int Call() { Calls.Events += "call;"; return 42; }
			}
			static class Calls
			{
				public static string Events = "";
				APPLICATIONS
				public static int Target() { throw new Exception("should be replaced"); }
				public static string Run() { var result = Target(); return Events + result; }
			}
			""";

		const string NestedLifecycleSource =
			"""
			using System;
			using AspectGenerator;
			[Aspect(OnInit = nameof(Init), OnUsing = nameof(Using), OnBeforeCall = nameof(Before), OnAfterCall = nameof(After), OnCatch = nameof(Catch), OnFinally = nameof(Finally))]
			[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
			sealed class LayerAttribute(string name) : Attribute
			{
				public string Name = name;
				public static InterceptInfo<int> Init(LayerAttribute aspect, InterceptInfo<int> info) { Calls.Events += aspect.Name + ".init;"; return info; }
				public static IDisposable Using(LayerAttribute aspect, InterceptInfo<int> info) { Calls.Events += aspect.Name + ".using;"; return new Scope(aspect.Name); }
				public static void Before(LayerAttribute aspect, InterceptInfo<int> info)
				{
					Calls.Events += aspect.Name + ".before;";
					if (Calls.Scenario == "inner-return" && aspect.Name == "C" || Calls.Scenario == "outer-return" && aspect.Name == "A")
					{
						info.ReturnValue = 7;
						info.InterceptResult = InterceptResult.Return;
					}
				}
				public static void After(LayerAttribute aspect, InterceptInfo<int> info)
				{
					Calls.Events += aspect.Name + ".after;";
					if (Calls.Scenario == "outer-after" && aspect.Name == "A") { info.ReturnValue += 10; }
				}
				public static void Catch(LayerAttribute aspect, InterceptInfo<int> info)
				{
					Calls.Events += aspect.Name + ".catch:" + info.Exception!.Message + ";";
					if (aspect.Name == "C") { info.ReturnValue = 7; info.InterceptResult = InterceptResult.IgnoreThrow; }
				}
				public static void Finally(LayerAttribute aspect, InterceptInfo<int> info) { Calls.Events += aspect.Name + ".finally;"; }
				sealed class Scope(string name) : IDisposable
				{
					public void Dispose() { Calls.Events += name + ".dispose;"; }
				}
			}
			[Aspect(OnCall = nameof(Call))]
			sealed class ReplacementAttribute : Attribute
			{
				public static int Call() { Calls.Events += "call;"; return 5; }
			}
			static class Calls
			{
				public const string Scenario = "SCENARIO";
				public static string Events = "";
				[Layer("A")][Layer("B")][Layer("C")]
				public static int Target() { Events += "target;"; if (Scenario == "inner-catch") { throw new InvalidOperationException("boom"); } return 3; }
				public static string Run() { var result = Target(); return Events + result; }
			}
			""";

		const string MixedCarrierSource =
			"""
			using System;
			using AspectGenerator;
			[Aspect(OnBeforeCall = nameof(Before), OnAfterCall = nameof(After))]
			[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
			sealed class InfoAttribute : Attribute
			{
				public static void Before(InterceptInfo<int> info) { Calls.Events += "I.before" + (info.PreviousInfo == null ? "" : ":" + info.PreviousInfo.Tag) + ";"; info.Tag = "I"; }
				public static void After(InterceptInfo<int> info) { Calls.Events += "I.after:" + info.ReturnValue + ";"; info.ReturnValue += 10; }
			}
			[Aspect(OnBeforeCall = nameof(Before), OnAfterCall = nameof(After), UseInterceptData = true)]
			[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
			sealed class DataAttribute : Attribute
			{
				public static void Before(ref InterceptData<int> info) { Calls.Events += "D.before" + (info.PreviousInfo == null ? "" : ":" + info.PreviousInfo.Tag) + ";"; info.Tag = "D"; }
				public static void After(ref InterceptData<int> info) { Calls.Events += "D.after:" + info.ReturnValue + ";"; info.ReturnValue += 100; }
			}
			static class Calls
			{
				public static string Events = "";
				APPLICATIONS
				public static int Target() { Events += "target;"; return 1; }
				public static string Run() { var result = Target(); return Events + result; }
			}
			""";
	}
}
