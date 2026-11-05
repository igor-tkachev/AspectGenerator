using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// The generated interceptor lives in its own file-local class, so it can only call what that class can access. Every
	/// call of the accessibility matrix below either gets an interceptor that compiles and runs, or is left as it is and
	/// reported with AG0003; none may produce code that does not compile.
	/// </summary>
	public partial class CompilerTests
	{
		[TestMethod]
		public void AccessPrivateMethodsCalledFromTheirOwnTypeTest()
		{
			AssertAccess(
				"""
				sealed class Handler
				{
					public string Fire() => Instance() + Static() + this.Instance() + Handler.Static();

					[Log] string Instance() => "i";
					[Log] private static string Static() => "s";
				}

				static class Calls
				{
					public static string Run() => new Handler().Fire();
				}
				""",
				"isis",
				4);
		}

		[TestMethod]
		public void AccessPrivateMethodCalledFromNestedTypeTest()
		{
			AssertAccess(
				"""
				class Outer
				{
					[Log] static string Hidden() => "h";

					public class Inner
					{
						public static string Go() => Hidden();
					}
				}

				static class Calls
				{
					public static string Run() => Outer.Inner.Go();
				}
				""",
				"h",
				1);
		}

		[TestMethod]
		public void AccessProtectedMembersFromDerivedTypeTest()
		{
			AssertAccess(
				"""
				class Base
				{
					[Log] protected          string Protected()          => "a";
					[Log] private protected  string PrivateProtected()   => "b";
					[Log] protected internal string ProtectedInternal()  => "c";
					[Log] internal           string Internal()           => "d";
					[Log] public             string Public()             => "e";
				}

				sealed class Derived : Base
				{
					public string Go() => Protected() + PrivateProtected() + ProtectedInternal() + Internal() + Public();
				}

				static class Calls
				{
					public static string Run() => new Derived().Go() + new Base().ProtectedInternal();
				}
				""",
				"Base.ProtectedInternal<>;Base.Internal<>;Base.Public<>;Base.ProtectedInternal<>;abcdec",
				2);
		}

		[TestMethod]
		public void AccessMembersOfNestedTypesTest()
		{
			AssertAccess(
				"""
				class Outer
				{
					private            class Private           { [Log] public static string M() => "1"; }
					protected          class Protected         { [Log] public static string M() => "2"; }
					private protected  class PrivateProtected  { [Log] public static string M() => "3"; }
					internal           class Internal          { [Log] public static string M() => "4"; }
					protected internal class ProtectedInternal { [Log] public static string M() => "5"; }

					public class Public
					{
						private class Deep { [Log] public static string M() => "6"; }

						[Log] public static string M() => "7";

						public static string Go() => Deep.M() + M();
					}

					public static string Go() => Private.M() + Protected.M() + PrivateProtected.M() + Internal.M() + ProtectedInternal.M() + Public.Go();
				}

				static class Calls
				{
					public static string Run() => Outer.Go();
				}
				""",
				"Internal.M<>;ProtectedInternal.M<>;Public.M<>;1234567",
				4);
		}

		[TestMethod]
		public void AccessFileLocalTypesTest()
		{
			AssertAccess(
				"""
				static class Calls
				{
					public static string Run() => Bridge.Go();
				}
				""",
				"flnx",
				4,
				"""
				using AspectGenerator.Tests.GeneratorDriver;

				file class Local
				{
					[Log] public static string M() => "f";

					[Log] public string Instance() => "l";

					public class Nested
					{
						[Log] public static string M() => "n";
					}
				}

				file static class Extensions
				{
					[Log] public static string Twice(this string text) => text;
				}

				static class Bridge
				{
					public static string Go() => Local.M() + new Local().Instance() + Local.Nested.M() + "x".Twice();
				}
				""");
		}

		[TestMethod]
		public void AccessPartialTypesTest()
		{
			AssertAccess(
				"""
				partial class Part
				{
					[Log] private string Hidden() => "h";
					[Log] public  string Shown()  => "s";

					[Log] public  partial string Declared();
					[Log] private partial string PrivateDeclared();
				}

				static class Calls
				{
					public static string Run() => new Part().Go();
				}
				""",
				"Part.Shown<>;Part.Declared<>;hsdp",
				2,
				"""
				partial class Part
				{
					public string Go() => Hidden() + Shown() + Declared() + PrivateDeclared();

					public  partial string Declared()        => "d";
					private partial string PrivateDeclared() => "p";
				}
				""");
		}

		[TestMethod]
		public void AccessGenericContainingTypesTest()
		{
			AssertAccess(
				"""
				class Outer<T>
				{
					public class Inner
					{
						[Log] public static string M() => typeof(T).Name;
					}

					private class Hidden
					{
						[Log] public static string M() => "h";
					}

					[Log] string Own() => "o";

					public static string Go() => Hidden.M() + new Outer<T>().Own();
				}

				class Caller
				{
					private class Secret
					{
					}

					public static string Go() => Outer<int>.Inner.M() + Outer<Secret>.Inner.M() + Outer<string>.Go();
				}

				static class Calls
				{
					public static string Run() => Caller.Go();
				}
				""",
				"Inner[Int32].M<>;Inner[Secret].M<>;Int32Secretho",
				2);
		}

		[TestMethod]
		public void AccessStructMembersTest()
		{
			AssertAccess(
				"""
				struct Counter
				{
					int _count;

					[Log] void   Increment() => _count++;
					[Log] public int Get()   => _count;

					public string Go()
					{
						Increment();
						return $"{Get()}";
					}
				}

				static class Calls
				{
					public static string Run() => new Counter().Go();
				}
				""",
				"Counter.Get<>;1",
				1);
		}

		[TestMethod]
		public void AccessFileLocalTypeArgumentsTest()
		{
			// A file-local type argument cannot be named by the interceptor, so the call gets a generic interceptor.
			var result = RunGeneratorAndGetCompilation(
				[
					"""
					using System;
					using AspectGenerator;

					[Aspect(OnBeforeCall = nameof(Before))]
					[AttributeUsage(AttributeTargets.Method)]
					sealed class LogAttribute : Attribute
					{
						public static readonly System.Text.StringBuilder Text = new();

						public static void Before(InterceptInfo info) => Text.Append("before;");
					}

					static class Helper
					{
						[Log] public static string Name<T>(T value) => typeof(T).IsArray ? "array;" : "single;";
					}

					static class Calls
					{
						public static string Run() => Bridge.Go() + LogAttribute.Text;
					}
					""",
					"""
					file class Local
					{
					}

					static class Bridge
					{
						public static string Go() => Helper.Name(new Local()) + Helper.Name(new Local[0]);
					}
					"""
				],
				out var compilation);

			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");

			Assert.AreEqual("single;array;before;before;", EmitAndRun(compilation, interceptors), interceptors);
			AssertNoDiagnostic(result, AspectDiagnosticID.CannotIntercept);
		}

		[TestMethod]
		public void AccessAspectTypesTest()
		{
			var result = RunGeneratorAndGetCompilation(
				[
					"""
					using System;
					using AspectGenerator;

					static class Log
					{
						public static readonly System.Text.StringBuilder Text = new();
					}

					class Host
					{
						[Aspect(OnBeforeCall = nameof(Before))]
						[AttributeUsage(AttributeTargets.Method)]
						private sealed class NestedAttribute : Attribute
						{
							public static void Before(InterceptInfo info) => Log.Text.Append("nested;");
						}

						private enum Mode { A, B }

						[Aspect(OnBeforeCall = nameof(Before))]
						[AttributeUsage(AttributeTargets.Method)]
						internal sealed class ArgumentAttribute : Attribute
						{
							public ArgumentAttribute(object value) { }

							public object? Named { get; set; }

							public static void Before(InterceptInfo info) => Log.Text.Append("argument;");
						}

						[Nested]                              public static string N() => "n";
						[Argument(Mode.B)]                    public static string E() => "e";
						[Argument(typeof(Mode))]              public static string T() => "t";
						[Argument(1, Named = Mode.A)]         public static string P() => "p";
						[Argument(1, Named = typeof(int))]    public static string I() => "i";

						public static string Go() => N() + E() + T() + P() + I();
					}

					[Aspect(OnBeforeCall = nameof(Before), OnAfterCall = nameof(After))]
					[AttributeUsage(AttributeTargets.Method)]
					sealed class HooksAttribute : Attribute
					{
						internal static void Before(InterceptInfo info) => Log.Text.Append("before;");
						private  static void After (InterceptInfo info) => Log.Text.Append("after;");
					}

					[Aspect(OnBeforeCall = nameof(Before))]
					[AttributeUsage(AttributeTargets.Method)]
					sealed class InternalHookAttribute : Attribute
					{
						internal static void Before(InterceptInfo info) => Log.Text.Append("internal;");
					}

					static class Calls
					{
						[Hooks]        public static string H() => "h";
						[InternalHook] public static string K() => "k";

						public static string Run() => Host.Go() + H() + K() + Bridge.Go() + Log.Text;
					}
					""",
					"""
					using System;
					using AspectGenerator;

					[Aspect(OnBeforeCall = nameof(Before))]
					[AttributeUsage(AttributeTargets.Method)]
					file sealed class LocalAttribute : Attribute
					{
						public static void Before(InterceptInfo info) => Log.Text.Append("local;");
					}

					static class Bridge
					{
						[Local] public static string L() => "l";

						public static string Go() => L();
					}
					"""
				],
				out var compilation);

			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");
			var value        = EmitAndRun(compilation, interceptors);

			Assert.AreEqual("netpihkl" + "argument;internal;", value, interceptors);

			var skips = result.Diagnostics.Where(static d => d.Id == AspectDiagnosticID.CannotIntercept).Select(static d => d.GetMessage()).ToArray();

			Assert.AreEqual(6, skips.Length, string.Join(System.Environment.NewLine, skips));
			Assert.AreEqual(1, skips.Count(static m => m.Contains("'Host.NestedAttribute'")),   string.Join(System.Environment.NewLine, skips));
			Assert.AreEqual(3, skips.Count(static m => m.Contains("'Host.Mode'")),              string.Join(System.Environment.NewLine, skips));
			Assert.AreEqual(1, skips.Count(static m => m.Contains("'HooksAttribute.After")),     string.Join(System.Environment.NewLine, skips));
			Assert.AreEqual(1, skips.Count(static m => m.Contains("'LocalAttribute'")),          string.Join(System.Environment.NewLine, skips));
		}

		static string EmitAndRun(Compilation compilation, string interceptors)
		{
			var errors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();

			Assert.AreEqual(0, errors.Length, string.Join(System.Environment.NewLine, errors.Select(static e => e.ToString())) + System.Environment.NewLine + interceptors);

			using var stream = new System.IO.MemoryStream();

			var emit = compilation.Emit(stream);

			Assert.IsTrue(emit.Success, string.Join(System.Environment.NewLine, emit.Diagnostics) + System.Environment.NewLine + interceptors);

			var context = new System.Runtime.Loader.AssemblyLoadContext("AccessibilityTests", isCollectible: true);

			try
			{
				stream.Position = 0;

				var assembly = context.LoadFromStream(stream);
				var run      = assembly.GetType("Calls")!.GetMethod("Run", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;

				return (string)run.Invoke(null, null)!;
			}
			finally
			{
				context.Unload();
			}
		}

		static void AssertAccess(string body, string expected, int notIntercepted, params string[] otherSources)
		{
			var result = AssertRunsWithResult(body, expected, null, otherSources);
			var skips  = result.Diagnostics.Where(static d => d.Id == AspectDiagnosticID.CannotIntercept).ToArray();

			Assert.AreEqual(notIntercepted, skips.Length, string.Join(System.Environment.NewLine, result.Diagnostics));

			foreach (var skip in skips)
			{
				Assert.AreEqual(DiagnosticSeverity.Warning, skip.Severity);
				StringAssert.Contains(skip.GetMessage(), "not accessible");
			}
		}
	}
}
