using System;

namespace Aspects
{
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
		public static void OnAfterCall(InterceptCallInfo info)
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
		public static void OnBeforeCall(InterceptCallInfo info)
		{
			info.Tag = "__X__";
		}

		public static void OnCall(InterceptCallInfo info)
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

		public static InterceptCallInfo OnInit(InterceptCallInfo info)
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
		public static void OnCatch(InterceptCallInfo info)
		{
			info.InterceptResult = InterceptResult.Return;
		}
	}

	[Aspect(
		OnFinally = nameof(OnFinally)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class FinallyAttribute : Attribute
	{
		public static void OnFinally(InterceptCallInfo info)
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

		public static InterceptCallInfo OnInit(InterceptCallInfo info)
		{
			OnInitCounter++;
			return info;
		}

		public static IDisposable? OnUsing(InterceptCallInfo info)
		{
			OnUsingCounter++;
			return null;
		}

		public static void OnBeforeCall(InterceptCallInfo info) => OnBeforeCallCounter++;
		public static void OnAfterCall (InterceptCallInfo info) => OnAfterCallCounter++;
		public static void OnCatch     (InterceptCallInfo info) => OnCatchCounter++;
		public static void OnFinally   (InterceptCallInfo info) => OnFinallyCounter++;
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

		public static void OnAfterCall(InterceptCallInfo info)
		{
			if (info.AspectArguments.TryGetValue(nameof(Arg2), out var value))
			{
				info.ReturnValue += value?.ToString();
			}
		}
	}

	[Aspect(
		OnUsing = nameof(OnUsing)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class UsingAttribute : Attribute
	{
		public static IDisposable? OnUsing(InterceptCallInfo info)
		{
			return null;
		}
	}
}
