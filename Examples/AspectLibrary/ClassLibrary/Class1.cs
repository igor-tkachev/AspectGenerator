using System;

using AspectLibrary;

namespace ClassLibrary
{
	public class Class1
	{
		[ConsoleLog]
		public void InstanceMethod()
		{
			StaticMethod();
		}

		[ConsoleLog]
		public static void StaticMethod()
		{
		}
	}
}
