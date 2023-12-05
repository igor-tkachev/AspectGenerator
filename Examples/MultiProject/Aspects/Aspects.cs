using System;

using AspectGenerator;

namespace Aspects
{
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
}
