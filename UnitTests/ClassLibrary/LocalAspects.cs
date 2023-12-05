using System;

namespace AspectGenerator.ClassLibrary
{
	[Aspect(
		OnAfterCall = nameof(OnAfterCall)
	)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
	public sealed class LocalAspectAttribute : Attribute
	{
		public static void OnAfterCall(dynamic info)
		{
			info.ReturnValue += " + LocalAspect.";
		}
	}
}
