using System;

using AspectGenerator;

namespace AspectLibrary
{
	[Aspect(
		OnBeforeCall     = nameof(OnBeforeCall),
		OnAfterCall      = nameof(OnAfterCall)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false)]
	public class LogAttribute : Attribute
	{
		public static void OnBeforeCall(InterceptInfo info)
		{
			Console.WriteLine($"Before {info.MemberInfo.Name}");
		}

		public static void OnAfterCall(InterceptInfo info)
		{
			Console.WriteLine($"After {info.MemberInfo.Name}");
		}
	}
}
