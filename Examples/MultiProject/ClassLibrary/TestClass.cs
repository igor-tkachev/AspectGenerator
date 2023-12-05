using System;

using Aspects;

namespace ClassLibrary
{
	public class TestClass
	{
		[CrossProject]
		public static string TestMethod(string str)
		{
			return str + " TestMethod";
		}

		public static string MainMethod(string str)
		{
			return TestMethod(str + " MainMethod");
		}
	}
}
