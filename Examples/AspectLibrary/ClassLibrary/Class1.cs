using System;

using AspectLibrary;

namespace ClassLibrary
{
	public class Class1
	{
		[Log]
		public void InstanceMethod()
		{
			StaticMethod();
		}

		[Log]
		public static void StaticMethod()
		{
		}
	}
}
