using System;

namespace MainApp
{
	internal class Program
	{
		static void Main(string[] args)
		{
			var s1 = ClassLibrary.TestClass.MainMethod("Main");
			Console.WriteLine(s1);

			var s2 = ClassLibrary.TestClass.TestMethod("Test");
			Console.WriteLine(s2);
		}
	}
}
