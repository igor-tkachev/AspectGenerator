using System;

namespace AspectGenerator.Tests
{
	internal class TestCode
	{
		[Aspects.TestAspect, Aspects.TestAspect2]
		public static string GenericMethod<T>(T value)
		{
			return $"value is {value}";
		}
	}

	class TestClassExtension
	{
	}

	struct TestStructExtension
	{
	}

	static class TestCodeExtensions
	{
		[Aspects.TestAspect]
		public static string ExtensionMethod<T>(this UnitTests? @object, T value)
		{
			return $"{value}";
		}

		[Aspects.TestAspect]
		public static string ExtensionMethod<T>(this TestClassExtension @object, T value)
		{
			return $"{value}";
		}

		[Aspects.TestAspect]
		public static string ExtensionMethod<T>(this TestStructExtension @object, T value)
		{
			return $"{value}";
		}
	}

	class OnCallObject
	{
		public int OnCall(int n)
		{
			return n * 2;
		}
	}

	[Aspect(
		OnCall = nameof(OnCall),
		InterceptedMethods = new[]
		{
			"AspectGenerator.Tests.OnCallObject.OnCall(int)"
		}
	)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class OnCallAttribute : Attribute
	{
		public static int OnCall(int n)
		{
			return n * 2;
		}

		public static int OnCall(OnCallObject obj, int n)
		{
			return n * 3;
		}
	}
}
