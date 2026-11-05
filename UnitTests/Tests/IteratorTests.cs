using System;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// The interceptor wraps the call of an iterator, which only creates the sequence; the body runs while the sequence
	/// is enumerated. These tests pin down when the hooks run, and that AG0108 says so for the hooks that would be
	/// expected to surround the body.
	/// </summary>
	public partial class CompilerTests
	{
		const string IteratorGuardSource =
			"""
			using System;
			using System.Collections.Generic;
			using System.Text;
			using System.Threading.Tasks;
			using AspectGenerator;

			[Aspect(OnBeforeCall = nameof(Before), OnAfterCall = nameof(After), OnCatch = nameof(Catch), OnFinally = nameof(Finally))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, AllowMultiple = true)]
			sealed class GuardAttribute : Attribute
			{
				public string? TargetFilter { get; set; }

				public static readonly StringBuilder Log = new();

				public static void Before(InterceptInfo info)
				{
					Log.Append("before;");
				}

				public static void After(InterceptInfo info)
				{
					Log.Append("after;");
				}

				public static void Catch(InterceptInfo info)
				{
					Log.Append("catch;");
					info.InterceptResult = InterceptResult.IgnoreThrow;
				}

				public static void Finally(InterceptInfo info)
				{
					Log.Append("finally;");
				}
			}

			[Aspect(OnBeforeCall = nameof(Before))]
			[AttributeUsage(AttributeTargets.Method)]
			sealed class BeforeOnlyAttribute : Attribute
			{
				public static void Before(InterceptInfo info)
				{
					GuardAttribute.Log.Append("before-only;");
				}
			}
			""";

		[TestMethod]
		public void IteratorHooksRunWhenTheSequenceIsCreatedTest()
		{
			var result = RunGeneratorAndGetCompilation(
				[
					IteratorGuardSource,
					"""
					using System;
					using System.Collections.Generic;

					static class Calls
					{
						[Guard]
						public static IEnumerable<int> Items()
						{
							GuardAttribute.Log.Append("body;");
							yield return 1;
							throw new InvalidOperationException("boom");
						}

						public static string Run()
						{
							var items = Items();

							GuardAttribute.Log.Append("returned;");

							try
							{
								foreach (var item in items)
									GuardAttribute.Log.Append($"item{item};");
							}
							catch (InvalidOperationException ex)
							{
								GuardAttribute.Log.Append($"escaped:{ex.Message};");
							}

							return GuardAttribute.Log.ToString();
						}
					}
					"""
				],
				out var compilation);

			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");

			// Every hook runs during the call; the body and its exception come later and bypass OnCatch.
			Assert.AreEqual("before;after;finally;returned;body;item1;escaped:boom;", EmitAndRun(compilation, interceptors), interceptors);

			var diagnostic = result.Diagnostics.Single(static d => d.Id == AspectDiagnosticID.IteratorBodyNotCovered);

			Assert.AreEqual(DiagnosticSeverity.Warning, diagnostic.Severity);
			StringAssert.Contains(diagnostic.GetMessage(), "Aspect 'GuardAttribute' on iterator method 'Calls.Items()': OnAfterCall, OnCatch, OnFinally run when the call returns the sequence, before the iterator body runs.");
			Assert.AreEqual("Guard", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
		}

		[TestMethod]
		public void AsyncIteratorHooksRunWhenTheSequenceIsCreatedTest()
		{
			var result = RunGeneratorAndGetCompilation(
				[
					IteratorGuardSource,
					"""
					using System;
					using System.Collections.Generic;
					using System.Threading.Tasks;

					static class Calls
					{
						[Guard]
						public static async IAsyncEnumerable<int> ItemsAsync()
						{
							await Task.Yield();
							GuardAttribute.Log.Append("body;");
							yield return 1;
							throw new InvalidOperationException("boom");
						}

						static async Task<string> RunAsync()
						{
							var items = ItemsAsync();

							GuardAttribute.Log.Append("returned;");

							try
							{
								await foreach (var item in items)
									GuardAttribute.Log.Append($"item{item};");
							}
							catch (InvalidOperationException ex)
							{
								GuardAttribute.Log.Append($"escaped:{ex.Message};");
							}

							return GuardAttribute.Log.ToString();
						}

						public static string Run()
						{
							return RunAsync().GetAwaiter().GetResult();
						}
					}
					"""
				],
				out var compilation);

			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");

			Assert.AreEqual("before;after;finally;returned;body;item1;escaped:boom;", EmitAndRun(compilation, interceptors), interceptors);

			var diagnostic = result.Diagnostics.Single(static d => d.Id == AspectDiagnosticID.IteratorBodyNotCovered);

			Assert.AreEqual(DiagnosticSeverity.Warning, diagnostic.Severity);
			StringAssert.Contains(diagnostic.GetMessage(), "on iterator method 'Calls.ItemsAsync()': OnAfterCall, OnCatch, OnFinally run when");
		}

		[TestMethod]
		public void IteratorDiagnosticOnlyForIteratorsAndBodyHooksTest()
		{
			var result = RunGeneratorAndGetCompilation(
				[
					IteratorGuardSource,
					"""
					using System.Collections.Generic;
					using System.Linq;

					static class Calls
					{
						// OnBeforeCall runs before the call as it says, iterator or not.
						[BeforeOnly]
						public static IEnumerable<int> BeforeOnly()
						{
							yield return 1;
						}

						// Not an iterator: the sequence is built by the call itself.
						[Guard]
						public static IEnumerable<int> Query()
						{
							return new[] { 1 }.Select(static i => i + 1);
						}

						// Not an iterator either: the yield belongs to the local function.
						[Guard]
						public static IEnumerable<int> Local()
						{
							return Inner();

							static IEnumerable<int> Inner()
							{
								yield return 3;
							}
						}

						public static string Run()
						{
							return string.Concat(BeforeOnly().Concat(Query()).Concat(Local())) + GuardAttribute.Log;
						}
					}
					"""
				],
				out var compilation);

			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");

			Assert.AreEqual("123before-only;before;after;finally;before;after;finally;", EmitAndRun(compilation, interceptors), interceptors);
			AssertNoDiagnostic(result, AspectDiagnosticID.IteratorBodyNotCovered);
		}

		[TestMethod]
		public void FilterSelectedIteratorIsInformationAtTheCallTest()
		{
			var result = RunGenerator(
				[
					IteratorGuardSource,
					"""
					using System.Collections.Generic;

					[assembly: Guard(TargetFilter = "method: Items")]

					static class Calls
					{
						public static IEnumerable<int> Items()
						{
							yield return 1;
						}

						public static int Run()
						{
							var count = 0;

							foreach (var item in Items())
								count += item;

							return count;
						}
					}
					"""
				]);

			var diagnostic = result.Diagnostics.Single(static d => d.Id == AspectDiagnosticID.IteratorBodyNotCovered);

			Assert.AreEqual(DiagnosticSeverity.Info, diagnostic.Severity);
			Assert.AreEqual("Items()", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
		}

		[TestMethod]
		public void IteratorOfReferencedAssemblyIsReportedTest()
		{
			RunGeneratorAndGetCompilation(
				"""
				using System;
				using System.Collections.Generic;
				using AspectGenerator;

				namespace IteratorLibrary
				{
					[Aspect(OnCatch = nameof(Catch))]
					[AttributeUsage(AttributeTargets.Method)]
					public sealed class GuardAttribute : Attribute
					{
						public static void Catch(InterceptInfo info)
						{
						}
					}

					public static class Source
					{
						[Guard]
						public static IEnumerable<int> Items()
						{
							yield return 1;
						}

						[Guard]
						public static int[] Array()
						{
							return [1];
						}
					}
				}
				""",
				out var libraryCompilation,
				new()
				{
					["build_property.AspectGeneratorPublicApi"] = "true",
				});

			using var stream = new System.IO.MemoryStream();

			var emit = libraryCompilation.WithAssemblyName("AspectGenerator.Tests.IteratorLibrary").Emit(stream);

			Assert.IsTrue(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));

			var result = RunGenerator(
				"""
				static class Calls
				{
					public static int Run()
					{
						return System.Linq.Enumerable.Count(IteratorLibrary.Source.Items()) + IteratorLibrary.Source.Array().Length;
					}
				}
				""",
				new()
				{
					["build_property.AspectGeneratorGenerateApi"] = "false",
				},
				reference: MetadataReference.CreateFromImage(stream.ToArray()));

			var diagnostic = result.Diagnostics.Single(static d => d.Id == AspectDiagnosticID.IteratorBodyNotCovered);

			Assert.AreEqual(DiagnosticSeverity.Warning, diagnostic.Severity);
			StringAssert.Contains(diagnostic.GetMessage(), "on iterator method 'IteratorLibrary.Source.Items()': OnCatch runs when the call returns the sequence");
			Assert.AreEqual("IteratorLibrary.Source.Items()", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
		}

		[TestMethod]
		public void IteratorDiagnosticFollowsWhatTheCallCanRunTest()
		{
			// A virtual, abstract or interface target runs its own body or any override or implementation; the warning is
			// given when one of them is an iterator, and names it when it is not the target itself.
			var result = RunGenerator(
				[
					IteratorGuardSource,
					"""
					using System.Collections.Generic;

					class EagerBase
					{
						[Guard] public virtual IEnumerable<int> M() { return new[] { 0 }; }
					}

					sealed class IteratorOverride : EagerBase
					{
						public override IEnumerable<int> M() { yield return 1; }
					}

					abstract class AbstractBase
					{
						[Guard] public abstract IEnumerable<int> M();
					}

					sealed class EagerOverride : AbstractBase
					{
						public override IEnumerable<int> M() { return new[] { 2 }; }
					}

					interface ISource
					{
						[Guard] IEnumerable<int> M();
					}

					sealed class ExplicitIterator : ISource
					{
						IEnumerable<int> ISource.M() { yield return 3; }
					}

					static class Calls
					{
						public static void Run(EagerBase e, AbstractBase a, ISource s)
						{
							e.M();
							a.M();
							s.M();
						}
					}
					"""
				]);

			var messages = result.Diagnostics
				.Where(static d => d.Id == AspectDiagnosticID.IteratorBodyNotCovered)
				.Select(static d => d.GetMessage())
				.OrderBy(static m => m, StringComparer.Ordinal)
				.ToArray();

			Assert.AreEqual(2, messages.Length, string.Join(Environment.NewLine, messages));
			StringAssert.Contains(messages[0], "on method 'EagerBase.M()', whose call can run the iterator 'IteratorOverride.M()'");
			StringAssert.Contains(messages[1], "on method 'ISource.M()', whose call can run the iterator 'ExplicitIterator.ISource.M()'");
		}

		[TestMethod]
		public void IteratorDiagnosticOncePerAttributeTest()
		{
			// A generic extension iterator called in reduced and static form binds two constructed methods; the warning is
			// about the one attribute.
			var result = RunGenerator(
				[
					IteratorGuardSource,
					"""
					using System.Collections.Generic;

					static class Extensions
					{
						[Guard]
						public static IEnumerable<T> Twice<T>(this IEnumerable<T> items)
						{
							foreach (var item in items)
							{
								yield return item;
								yield return item;
							}
						}
					}

					static class Calls
					{
						public static void Run()
						{
							new[] { 1 }.Twice();
							Extensions.Twice(new[] { "a" });
						}
					}
					"""
				]);

			var diagnostic = result.Diagnostics.Single(static d => d.Id == AspectDiagnosticID.IteratorBodyNotCovered);

			StringAssert.Contains(diagnostic.GetMessage(), "on iterator method 'Extensions.Twice<T>(System.Collections.Generic.IEnumerable<T>)'");
		}

		// AG0108 is decided on the bound declaration; a virtual iterator whose override is eager is reported.
		[TestMethod]
		public void VirtualIteratorEagerOverrideTest()
		{
			var result = RunGenerator(
				[ProbeLogAspect,
				"""
				using System.Collections.Generic;

				class B { [After] public virtual IEnumerable<int> M() { yield return 0; } }
				sealed class D : B { public override IEnumerable<int> M() { return new[] { 1 }; } }
				abstract class X { [After] public abstract IEnumerable<int> M(); }
				sealed class Y : X { public override IEnumerable<int> M() { yield return 1; } }

				static class Calls
				{
					public static void Run(X x) { ((B)new D()).M(); x.M(); }
				}
				"""]);

			// Decision since 2f72cef: reported whenever the call can run an iterator (B.M's own body, Y.M overriding X.M).
			var ids = result.Diagnostics.Where(static d => d.Id == AspectDiagnosticID.IteratorBodyNotCovered).Select(static d => d.GetMessage()).ToArray();

			Assert.AreEqual(2, ids.Length, string.Join(Environment.NewLine, ids));
			Assert.IsTrue(ids.Any(static m => m.Contains("'B.M()'")), string.Join(Environment.NewLine, ids));
			Assert.IsTrue(ids.Any(static m => m.Contains("'Y.M()'")), string.Join(Environment.NewLine, ids));
		}
	}
}
