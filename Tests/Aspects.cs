using System;

namespace Aspects
{
	using AspectGenerator;

	[Aspect]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class EmptyAspectAttribute : Attribute
	{
	}

	[Aspect(
		OnAfterCall  = nameof(OnAfterCall)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class TestAspectAttribute : Attribute
	{
		public static void OnAfterCall<T>(InterceptInfo<T> info)
		{
		}

		public static void OnAfterCall(InterceptInfo<string> info)
		{
			if (info.InterceptType == InterceptType.OnAfterCall)
			{
				info.ReturnValue += "__I__";
			}
		}
	}

	[Aspect(
		OnBeforeCall = nameof(OnBeforeCall),
		OnAfterCall  = nameof(OnCall)
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
		OnInit       = nameof(OnInit),
		OnUsing      = nameof(OnUsing),
		OnBeforeCall = nameof(OnBeforeCall),
		OnAfterCall  = nameof(OnAfterCall),
		OnCatch      = nameof(OnCatch),
		OnFinally    = nameof(OnFinally)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class AllEventsAttribute : Attribute
	{
		public static int OnInitCounter;
		public static int OnUsingCounter;
		public static int OnBeforeCallCounter;
		public static int OnAfterCallCounter;
		public static int OnCatchCounter;
		public static int OnFinallyCounter;

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

		public static void OnBeforeCall(InterceptInfo info) => OnBeforeCallCounter++;
		public static void OnAfterCall (InterceptInfo info) => OnAfterCallCounter++;
		public static void OnCatch     (InterceptInfo info) => OnCatchCounter++;
		public static void OnFinally   (InterceptInfo info) => OnFinallyCounter++;
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

	[Aspect(
		OnUsing      = nameof(OnUsing),
		OnAsyncUsing = nameof(OnAsyncUsing)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class UsingAttribute : Attribute
	{
		public static IDisposable? OnUsing(InterceptInfo info)
		{
			return null;
		}

		public static IAsyncDisposable? OnAsyncUsing(InterceptInfo info)
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
	}
}
