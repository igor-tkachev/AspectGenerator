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
}
