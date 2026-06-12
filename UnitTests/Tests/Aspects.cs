using System;
using System.Threading.Tasks;

namespace Aspects
{
	using AspectGenerator;

	[Aspect]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class EmptyAspectAttribute : Attribute
	{
	}

	[Aspect(
		OnAfterCall      = nameof(OnAfterCall),
		UseInterceptType = true,
		UseInterceptData = true
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class TestAspectAttribute : Attribute
	{
		public static void OnAfterCall<T>(ref InterceptData<T> info)
		{
		}

		public static void OnAfterCall(ref InterceptData<string> info)
		{
			if (info.InterceptType == InterceptType.OnAfterCall)
			{
				info.ReturnValue += "__I__";
			}
		}
	}

	[Aspect(
		OnBeforeCall     = nameof(OnBeforeCall),
		OnAfterCall      = nameof(OnCall),
		UseInterceptType = true
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class TestAspect2Attribute : Attribute
	{
		public static void OnBeforeCall(InterceptInfo info)
		{
			info.Tag = "__X__";
		}

		public static void OnCall(InterceptInfo<Void> info)
		{
		}

		public static void OnCall(InterceptInfo<string> info)
		{
			if (info.InterceptType == InterceptType.OnAfterCall)
			{
				info.ReturnValue += (string)info.Tag!;
			}
		}
	}

	[Aspect(
		OnInit = nameof(OnInit)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class InitAspectAttribute : Attribute
	{
		public static int CallCount;

		public static InterceptInfo<T> OnInit<T>(InterceptInfo<T> info)
		{
			CallCount++;
			return info;
		}
	}

	[Aspect(
		OnCatch = nameof(OnCatch)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class IgnoreCatchAttribute : Attribute
	{
		public static void OnCatch(InterceptInfo info)
		{
			info.InterceptResult = InterceptResult.IgnoreThrow;
		}
	}

	[Aspect(
		OnFinally = nameof(OnFinally)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class FinallyAttribute : Attribute
	{
		public static void OnFinally(InterceptInfo<int> info)
		{
			info.ReturnValue = (int)info.ReturnValue! + 1;
		}
	}

	[Aspect(
		OnInit            = nameof(OnInit),
		OnUsing           = nameof(OnUsing),
		OnUsingAsync      = nameof(OnUsingAsync),
		OnBeforeCall      = nameof(OnBeforeCall),
		OnBeforeCallAsync = nameof(OnBeforeCallAsync),
		OnAfterCall       = nameof(OnAfterCall),
		OnAfterCallAsync  = nameof(OnAfterCallAsync),
		OnCatch           = nameof(OnCatch),
		OnCatchAsync      = nameof(OnCatchAsync),
		OnFinally         = nameof(OnFinally),
		OnFinallyAsync    = nameof(OnFinallyAsync)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class AllEventsAttribute : Attribute
	{
		public static int OnInitCounter;
		public static int OnUsingCounter;
		public static int OnUsingCounterAsync;
		public static int OnBeforeCallCounter;
		public static int OnBeforeCallCounterAsync;
		public static int OnAfterCallCounter;
		public static int OnAfterCallCounterAsync;
		public static int OnCatchCounter;
		public static int OnCatchCounterAsync;
		public static int OnFinallyCounter;
		public static int OnFinallyCounterAsync;

		public static void ClearCounters()
		{
			OnInitCounter            = 0;
			OnUsingCounter           = 0;
			OnUsingCounterAsync      = 0;
			OnBeforeCallCounter      = 0;
			OnBeforeCallCounterAsync = 0;
			OnAfterCallCounter       = 0;
			OnAfterCallCounterAsync  = 0;
			OnCatchCounter           = 0;
			OnCatchCounterAsync      = 0;
			OnFinallyCounter         = 0;
			OnFinallyCounterAsync    = 0;
		}

		public static InterceptInfo<T> OnInit<T>(InterceptInfo<T> info)
		{
			OnInitCounter++;
			return info;
		}

		public static IDisposable? OnUsing(InterceptInfo info)
		{
			OnUsingCounter++;
			return null;
		}

		public static IAsyncDisposable? OnUsingAsync(InterceptInfo info)
		{
			OnUsingCounterAsync++;
			return null;
		}

		public static void OnBeforeCall     (InterceptInfo info) => OnBeforeCallCounter++;
		public static void OnAfterCall      (InterceptInfo info) => OnAfterCallCounter++;
		public static void OnCatch          (InterceptInfo info) => OnCatchCounter++;
		public static void OnFinally        (InterceptInfo info) => OnFinallyCounter++;

		public static Task OnBeforeCallAsync(InterceptInfo info) => Task.FromResult(OnBeforeCallCounterAsync++);
		public static Task OnAfterCallAsync (InterceptInfo info) => Task.FromResult(OnAfterCallCounterAsync++);
		public static Task OnCatchAsync     (InterceptInfo info) => Task.FromResult(OnCatchCounterAsync++);
		public static Task OnFinallyAsync   (InterceptInfo info) => Task.FromResult(OnFinallyCounterAsync++);
	}

	[Aspect(
		OnUsingAsync      = nameof(OnUsingAsync),
		OnBeforeCallAsync = nameof(OnBeforeCallAsync),
		OnAfterCallAsync  = nameof(OnAfterCallAsync),
		OnCatchAsync      = nameof(OnCatchAsync),
		OnFinallyAsync    = nameof(OnFinallyAsync)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class ValueTaskFlowAttribute : Attribute
	{
		public static int OnUsingCounterAsync;
		public static int OnBeforeCallCounterAsync;
		public static int OnAfterCallCounterAsync;
		public static int OnCatchCounterAsync;
		public static int OnFinallyCounterAsync;
		public static int DisposeCounterAsync;

		public static void ClearCounters()
		{
			OnUsingCounterAsync      = 0;
			OnBeforeCallCounterAsync = 0;
			OnAfterCallCounterAsync  = 0;
			OnCatchCounterAsync      = 0;
			OnFinallyCounterAsync    = 0;
			DisposeCounterAsync      = 0;
		}

		public static IAsyncDisposable OnUsingAsync(InterceptInfo info)
		{
			OnUsingCounterAsync++;
			return new AsyncScope();
		}

		public static ValueTask OnBeforeCallAsync(InterceptInfo info)
		{
			OnBeforeCallCounterAsync++;
			return ValueTask.CompletedTask;
		}

		public static ValueTask OnAfterCallAsync(InterceptInfo info)
		{
			OnAfterCallCounterAsync++;
			return ValueTask.CompletedTask;
		}

		public static ValueTask OnAfterCallAsync(InterceptInfo<int> info)
		{
			OnAfterCallCounterAsync++;
			info.ReturnValue += 10;
			return ValueTask.CompletedTask;
		}

		public static ValueTask OnCatchAsync(InterceptInfo info)
		{
			OnCatchCounterAsync++;
			return ValueTask.CompletedTask;
		}

		public static ValueTask OnCatchAsync(InterceptInfo<int> info)
		{
			OnCatchCounterAsync++;
			info.ReturnValue     = 20;
			info.InterceptResult = InterceptResult.IgnoreThrow;
			return ValueTask.CompletedTask;
		}

		public static ValueTask OnFinallyAsync(InterceptInfo info)
		{
			OnFinallyCounterAsync++;
			return ValueTask.CompletedTask;
		}

		public static ValueTask OnFinallyAsync(InterceptInfo<int> info)
		{
			OnFinallyCounterAsync++;
			info.ReturnValue++;
			return ValueTask.CompletedTask;
		}

		sealed class AsyncScope : IAsyncDisposable
		{
			public ValueTask DisposeAsync()
			{
				DisposeCounterAsync++;
				return ValueTask.CompletedTask;
			}
		}
	}

	[Aspect(
		OnAfterCall = nameof(OnAfterCall)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class ArgsAttribute : Attribute
	{
		public string? Arg1 { get; set; }
		public int     Arg2 { get; set; }
		public int[]?  Arg3 { get; set; }
		public char    Arg4 { get; set; }
		public object? Arg5 { get; set; }

		public static void OnAfterCall(InterceptInfo<string> info)
		{
			if (info.AspectArguments.TryGetValue(nameof(Arg2), out var value))
			{
				info.ReturnValue += value?.ToString();
			}
		}
	}

	enum LiteralKind
	{
		None,
		Second
	}

	[Aspect(
		OnAfterCall = nameof(OnAfterCall)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
	sealed class LiteralArgsAttribute : Attribute
	{
		public string?      Text      { get; set; }
		public char         Character { get; set; }
		public double       Number    { get; set; }
		public float        Single    { get; set; }
		public LiteralKind  Kind      { get; set; }
		public string[]?    Values    { get; set; }

		public static void OnAfterCall(InterceptInfo<string> info)
		{
			var values = (string[])info.AspectArguments[nameof(Values)]!;

			info.ReturnValue =
				(string)info.AspectArguments[nameof(Text)]! == "quote\" slash\\ newline\n" &&
				(char)info.AspectArguments[nameof(Character)]! == '\'' &&
				(double)info.AspectArguments[nameof(Number)]! == 1.25d &&
				(float)info.AspectArguments[nameof(Single)]! == 3.5f &&
				(LiteralKind)info.AspectArguments[nameof(Kind)]! == LiteralKind.Second &&
				values is ["a\"b", "c\\d", "e\nf"]
					? "literal-ok"
					: "literal-failed";
		}
	}

	[Aspect(
		OnUsing          = nameof(OnUsing),
		OnUsingAsync     = nameof(OnUsingAsync),
		UseInterceptData = true
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class UsingAttribute : Attribute
	{
		public static IDisposable? OnUsing<T>(ref InterceptData<T> info)
		{
			return null;
		}

		public static IAsyncDisposable? OnUsingAsync<T>(ref InterceptData<T> info)
		{
			return null;
		}
	}

	[Aspect(
		OnAfterCall = nameof(OnAfterCall)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class OrderedAttribute : Attribute
	{
		public int     Order { get; set; } = int.MaxValue;
		public string? Value { get; set; }

		public static void OnAfterCall(InterceptInfo<string> info)
		{
			info.ReturnValue += info.AspectArguments["Value"];
		}

		public static void OnAfterCall(InterceptInfo<int> info)
		{
			info.ReturnValue = info.ReturnValue * 10 + int.Parse((string)info.AspectArguments["Value"]!);
		}
	}

	[Aspect(
		OnAfterCall   = nameof(OnAfterCall),
		PassArguments = true
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class ArgumentsAttribute : Attribute
	{
		public static void OnAfterCall<T>(InterceptInfo<T> info)
		{
		}
		public static void OnAfterCall(InterceptInfo<string> info)
		{
			info.ReturnValue += info.MethodArguments!.Length;
		}
	}

	[Aspect(
		OnAfterCall = nameof(OnAfterCall)
	)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
	sealed class CrossProjectAttribute : Attribute
	{
		public static void OnAfterCall(InterceptInfo<string> info)
		{
			info.ReturnValue += " + CrossProject aspect.";
		}
	}

	[Aspect(
		OnAfterCall = nameof(OnAfterCall),
		InterceptMethods =
		[
			"AspectGenerator.Tests.UnitTests.InterceptedMethod(string)",
			"AspectGenerator.Tests.UnitTests.InterceptedGenericMethod<string>(string)",
			"System.String.Substring(int)",
			"string.Substring(int)"
		]
	)]
	sealed class InterceptMethodsAttribute
	{
		public static void OnAfterCall(InterceptInfo<string> info)
		{
			info.ReturnValue += " + InterceptMethods aspect.";
		}
	}
}
