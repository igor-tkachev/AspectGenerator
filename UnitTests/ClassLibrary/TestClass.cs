using System;

using Aspects;

namespace AspectGenerator.ClassLibrary
{
	public class TestClass
	{
		public static string MainMethod(string str)
		{
			return TestMethod(str + " MainMethod");
		}

		[CrossProject]
		public static string TestMethod(string str)
		{
			return str + " TestMethod";
		}

		[LocalAspect]
		public static string LocalMethod(string str)
		{
			return str + " LocalMethod";
		}
	}
}
