using System;
using System.Threading.Tasks;

using AspectGenerator;

[assembly: AspectGeneratorOptions(InterceptorsNamespace = "AspectGenerator.Diagnostics")]

namespace AspectGenerator.Diagnostics
{
	[Aspect(OnAfterCall = nameof(WrongParameter))]
	sealed class InvalidParameterAttribute : Attribute
	{
		public static void WrongParameter(string info)
		{
		}
	}

	[Aspect(OnAfterCall = nameof(WrongReturn))]
	sealed class InvalidReturnAttribute : Attribute
	{
		public static int WrongReturn(InterceptInfo<string> info)
		{
			return 0;
		}
	}

	[Aspect(OnCall = nameof(Call))]
	sealed class InvalidOnCallAttribute : Attribute
	{
		public static string Call(string value)
		{
			return value;
		}
	}

	[Aspect(
		OnAfterCall      = nameof(Data),
		UseInterceptData = true)]
	sealed class InvalidInterceptDataAttribute : Attribute
	{
		public static void Data(InterceptInfo<string> info)
		{
		}
	}

	[Aspect(OnAfterCallAsync = nameof(AfterAsync))]
	sealed class InvalidAsyncAttribute : Attribute
	{
		public static Task AfterAsync(InterceptInfo<string> info)
		{
			return Task.CompletedTask;
		}
	}

	static class Target
	{
		[InvalidParameter]
		public static string InvalidParameterTarget()
		{
			return "";
		}

		[InvalidReturn]
		public static string InvalidReturnTarget()
		{
			return "";
		}

		[InvalidOnCall]
		public static string InvalidOnCallTarget(int value)
		{
			return value.ToString();
		}

		[InvalidInterceptData]
		public static string InvalidInterceptDataTarget()
		{
			return "";
		}

		[InvalidAsync]
		public static string InvalidAsyncTarget()
		{
			return "";
		}

		public static void Invoke()
		{
			InvalidParameterTarget();
			InvalidReturnTarget();
			InvalidOnCallTarget(1);
			InvalidInterceptDataTarget();
			InvalidAsyncTarget();
		}
	}
}
