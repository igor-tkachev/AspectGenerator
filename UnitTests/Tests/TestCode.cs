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

	static class TestCodeExtensionBlocks
	{
		extension(string text)
		{
			// The generator reports the call (AG0003) and leaves it as it is: members of extension blocks are not intercepted.
			[Aspects.TestAspect]
			public string ExtensionBlockMethod()
			{
				return text;
			}
		}
	}

	class OnCallObject
	{
		public int OnCall(int n)
		{
			return n * 2;
		}
	}

	[Aspect(OnCall = nameof(OnCall))]
	[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
	sealed class OnCallAttribute : Attribute
	{
		public string[]?        TargetFilter     { get; set; }
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
