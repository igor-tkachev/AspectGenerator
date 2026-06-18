using System;
using System.Threading.Tasks;

namespace Aspects
{
	[AspectGenerator.Aspect]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class EmptyAspectAttribute : Attribute
	{
	}

	[AspectGenerator.Aspect(
		OnAfterCall      = nameof(OnAfterCall),
		UseInterceptType = true,
		UseInterceptData = true
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class TestAspectAttribute : Attribute
	{
		public static void OnAfterCall<T>(ref AspectGenerator.InterceptData<T> info)
		{
		}

		public static void OnAfterCall(ref AspectGenerator.InterceptData<string> info)
		{
			if (info.InterceptType == AspectGenerator.InterceptType.OnAfterCall)
			{
				info.ReturnValue += "__I__";
			}
		}
	}

	[AspectGenerator.Aspect(
		OnBeforeCall     = nameof(OnBeforeCall),
		OnAfterCall      = nameof(OnCall),
		UseInterceptType = true
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class TestAspect2Attribute : Attribute
	{
		public static void OnBeforeCall(AspectGenerator.InterceptInfo info)
		{
			info.Tag = "__X__";
		}

		public static void OnCall(AspectGenerator.InterceptInfo<AspectGenerator.Void> info)
		{
		}

		public static void OnCall(AspectGenerator.InterceptInfo<string> info)
		{
			if (info.InterceptType == AspectGenerator.InterceptType.OnAfterCall)
			{
				info.ReturnValue += (string)info.Tag!;
			}
		}
	}

	[AspectGenerator.Aspect(
		OnInit = nameof(OnInit)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class InitAspectAttribute : Attribute
	{
		public static int CallCount;

		public static AspectGenerator.InterceptInfo<T> OnInit<T>(AspectGenerator.InterceptInfo<T> info)
		{
			CallCount++;
			return info;
		}
	}

	[AspectGenerator.Aspect(
		OnCatch = nameof(OnCatch)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class IgnoreCatchAttribute : Attribute
	{
		public static void OnCatch(AspectGenerator.InterceptInfo info)
		{
			info.InterceptResult = AspectGenerator.InterceptResult.IgnoreThrow;
		}
	}

	[AspectGenerator.Aspect(
		OnFinally = nameof(OnFinally)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class FinallyAttribute : Attribute
	{
		public static void OnFinally(AspectGenerator.InterceptInfo<int> info)
		{
			info.ReturnValue = (int)info.ReturnValue! + 1;
		}
	}

	[AspectGenerator.Aspect(
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

		public static AspectGenerator.InterceptInfo<T> OnInit<T>(AspectGenerator.InterceptInfo<T> info)
		{
			OnInitCounter++;
			return info;
		}

		public static IDisposable? OnUsing(AspectGenerator.InterceptInfo info)
		{
			OnUsingCounter++;
			return null;
		}

		public static IAsyncDisposable? OnUsingAsync(AspectGenerator.InterceptInfo info)
		{
			OnUsingCounterAsync++;
			return null;
		}

		public static void OnBeforeCall     (AspectGenerator.InterceptInfo info) => OnBeforeCallCounter++;
		public static void OnAfterCall      (AspectGenerator.InterceptInfo                 info) => OnAfterCallCounter++;
		public static void OnCatch          (AspectGenerator.InterceptInfo                 info) => OnCatchCounter++;
		public static void OnFinally        (AspectGenerator.InterceptInfo                 info) => OnFinallyCounter++;

		public static Task OnBeforeCallAsync(AspectGenerator.InterceptInfo info) => Task.FromResult(OnBeforeCallCounterAsync++);
		public static Task OnAfterCallAsync (AspectGenerator.InterceptInfo info) => Task.FromResult(OnAfterCallCounterAsync++);
		public static Task OnCatchAsync     (AspectGenerator.InterceptInfo info) => Task.FromResult(OnCatchCounterAsync++);
		public static Task OnFinallyAsync   (AspectGenerator.InterceptInfo info) => Task.FromResult(OnFinallyCounterAsync++);
	}

	[AspectGenerator.Aspect(
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

		public static IAsyncDisposable OnUsingAsync(AspectGenerator.InterceptInfo info)
		{
			OnUsingCounterAsync++;
			return new AsyncScope();
		}

		public static ValueTask OnBeforeCallAsync(AspectGenerator.InterceptInfo info)
		{
			OnBeforeCallCounterAsync++;
			return ValueTask.CompletedTask;
		}

		public static ValueTask OnAfterCallAsync(AspectGenerator.InterceptInfo info)
		{
			OnAfterCallCounterAsync++;
			return ValueTask.CompletedTask;
		}

		public static ValueTask OnAfterCallAsync(AspectGenerator.InterceptInfo<int> info)
		{
			OnAfterCallCounterAsync++;
			info.ReturnValue += 10;
			return ValueTask.CompletedTask;
		}

		public static ValueTask OnCatchAsync(AspectGenerator.InterceptInfo info)
		{
			OnCatchCounterAsync++;
			return ValueTask.CompletedTask;
		}

		public static ValueTask OnCatchAsync(AspectGenerator.InterceptInfo<int> info)
		{
			OnCatchCounterAsync++;
			info.ReturnValue     = 20;
			info.InterceptResult = AspectGenerator.InterceptResult.IgnoreThrow;
			return ValueTask.CompletedTask;
		}

		public static ValueTask OnFinallyAsync(AspectGenerator.InterceptInfo info)
		{
			OnFinallyCounterAsync++;
			return ValueTask.CompletedTask;
		}

		public static ValueTask OnFinallyAsync(AspectGenerator.InterceptInfo<int> info)
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

	[AspectGenerator.Aspect(
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

		public static void OnAfterCall(AspectGenerator.InterceptInfo<string> info)
		{
			if (info.Aspect is ArgsAttribute { Arg2: not 0 } aspect)
			{
				info.ReturnValue += aspect.Arg2.ToString();
			}
		}
	}

	enum LiteralKind
	{
		None,
		Second
	}

	[AspectGenerator.Aspect(
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

		public static void OnAfterCall(AspectGenerator.InterceptInfo<string> info)
		{
			var aspect = (LiteralArgsAttribute)info.Aspect!;
			var values = aspect.Values!;

			info.ReturnValue =
				aspect.Text == "quote\" slash\\ newline\n" &&
				aspect.Character == '\'' &&
				aspect.Number == 1.25d &&
				aspect.Single == 3.5f &&
				aspect.Kind == LiteralKind.Second &&
				values is ["a\"b", "c\\d", "e\nf"]
					? "literal-ok"
					: "literal-failed";
		}
	}

	[AspectGenerator.Aspect(
		OnAfterCall = nameof(OnAfterCall)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
	sealed class TypedAspectAttribute : Attribute
	{
		public TypedAspectAttribute(string category)
		{
			Category = category;
		}

		public string      Category { get; }
		public LiteralKind Kind     { get; set; }

		public static void OnAfterCall(TypedAspectAttribute aspect, AspectGenerator.InterceptInfo<string> info)
		{
			info.ReturnValue =
				ReferenceEquals(info.Aspect, aspect) &&
				aspect.Category == "typed" &&
				aspect.Kind == LiteralKind.Second
					? "typed-ok"
					: "typed-failed";
		}
	}

	[AspectGenerator.Aspect(
		OnUsing          = nameof(OnUsing),
		OnUsingAsync     = nameof(OnUsingAsync),
		UseInterceptData = true
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class UsingAttribute : Attribute
	{
		public static IDisposable? OnUsing<T>(ref AspectGenerator.InterceptData<T> info)
		{
			return null;
		}

		public static IAsyncDisposable? OnUsingAsync<T>(ref AspectGenerator.InterceptData<T> info)
		{
			return null;
		}
	}

	[AspectGenerator.Aspect(
		OnAfterCall = nameof(OnAfterCall)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class OrderedAttribute : Attribute
	{
		public int     Order { get; set; } = int.MaxValue;
		public string? Value { get; set; }

		public static void OnAfterCall(AspectGenerator.InterceptInfo<string> info)
		{
			info.ReturnValue += ((OrderedAttribute)info.Aspect!).Value;
		}

		public static void OnAfterCall(AspectGenerator.InterceptInfo<int> info)
		{
			info.ReturnValue = info.ReturnValue * 10 + int.Parse(((OrderedAttribute)info.Aspect!).Value!);
		}
	}

	[AspectGenerator.Aspect(
		OnAfterCall   = nameof(OnAfterCall),
		PassArguments = true
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class ArgumentsAttribute : Attribute
	{
		public static void OnAfterCall<T>(AspectGenerator.InterceptInfo<T> info)
		{
		}

		public static void OnAfterCall(AspectGenerator.InterceptInfo<string> info)
		{
			info.ReturnValue += info.MethodArguments!.Length;
		}
	}

	[AspectGenerator.Aspect(OnAfterCall = nameof(OnAfterCall))]
	[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class LogAttribute : Attribute
	{
		public string[]? TargetFilter { get; set; }

		public static void OnAfterCall(AspectGenerator.InterceptInfo<string> info)
		{
			info.ReturnValue += " + log.";
		}
	}

	[AspectGenerator.Aspect(
		OnAfterCall = nameof(OnAfterCall)
	)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
	sealed class CrossProjectAttribute : Attribute
	{
		public static void OnAfterCall(AspectGenerator.InterceptInfo<string> info)
		{
			info.ReturnValue += " + CrossProject aspect.";
		}
	}

}
