using System;
using System.Collections.Generic;
using Aspects;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	[TestClass]
	public class UnitTests
	{
		[Aspects.TestAspect]
		public static string StaticMethod()
		{
			return "hi";
		}

		[Aspects.TestAspect]
		public static string StaticMethod(string name)
		{
			return $"hi, {name}";
		}

		[TestMethod]
		public void StaticMethodTest()
		{
			var str = StaticMethod() + ".." + StaticMethod() + "++" +
			          StaticMethod("John");

			Assert.AreEqual("hi__I__..hi__I__++hi, John__I__", str);

			Console.WriteLine(str);
		}

		[TestMethod]
		public void GenericMethodTest()
		{
			var now = DateTime.Now;
			var str = TestCode.GenericMethod(1) + TestCode.GenericMethod(DateTime.Today) + TestCode.GenericMethod(now);

			Assert.AreEqual($"value is 1__X____I__value is {DateTime.Today}__X____I__value is {now}__X____I__", str);

			Console.WriteLine(str);
		}

		[Aspects.TestAspect, Aspects.TestAspect2]
		public static void ReturnVoidMethod()
		{
		}

		[TestMethod]
		public void ReturnVoidMethodTest()
		{
			ReturnVoidMethod();
		}

		[Aspects.EmptyAspect]
		public static int EmptyMethod()
		{
			return default;
		}

		[TestMethod]
		public void EmptyAspectTest()
		{
			EmptyMethod();
		}

		[Aspects.InitAspect]
		public static int InitMethod()
		{
			return default;
		}

		[TestMethod]
		public void InitAspectTest()
		{
			Aspects.InitAspectAttribute.CallCount = 0;

			InitMethod();

			Assert.AreEqual(1, Aspects.InitAspectAttribute.CallCount);
		}

		[Aspects.IgnoreCatch]
		public static int IgnoreCatchMethod()
		{
			throw new ();
		}

		[TestMethod]
		public void IgnoreCatchTest()
		{
			IgnoreCatchMethod();
		}

		[Aspects.Finally]
		public static int FinallyMethod()
		{
			return 1;
		}

		[TestMethod]
		public void FinallyTest()
		{
			var n = FinallyMethod();

			Assert.AreEqual(2, n);
		}

		[Aspects.Finally]
		public int MemberMethod(int n)
		{
			return n;
		}

		[TestMethod]
		public void MemberMethodTest()
		{
			var n = MemberMethod(1);

			Assert.AreEqual(2, n);
		}

		[TestMethod]
		public void ExtensionTest()
		{
			var str = this.ExtensionMethod(1);

			Assert.AreEqual("1__I__", str);

			Console.WriteLine(str);
		}

		[TestMethod]
		public void ClassExtensionTest()
		{
			var str = new TestClassExtension().ExtensionMethod(1);

			Assert.AreEqual("1__I__", str);

			Console.WriteLine(str);
		}

		[TestMethod]
		public void StructExtensionTest()
		{
			var str = new TestStructExtension().ExtensionMethod(2);

			Assert.AreEqual("2__I__", str);

			Console.WriteLine(str);
		}

		[AllEvents]
		internal void AllEventsMethod()
		{
		}

		[TestMethod]
		public void AllEventsTest()
		{
			AllEventsAttribute.OnInitCounter       = 0;
			AllEventsAttribute.OnUsingCounter      = 0;
			AllEventsAttribute.OnBeforeCallCounter = 0;
			AllEventsAttribute.OnAfterCallCounter  = 0;
			AllEventsAttribute.OnCatchCounter      = 0;
			AllEventsAttribute.OnFinallyCounter    = 0;

			AllEventsMethod();

			Assert.AreEqual(1, AllEventsAttribute.OnInitCounter);
			Assert.AreEqual(1, AllEventsAttribute.OnUsingCounter);
			Assert.AreEqual(1, AllEventsAttribute.OnBeforeCallCounter);
			Assert.AreEqual(1, AllEventsAttribute.OnAfterCallCounter);
			Assert.AreEqual(0, AllEventsAttribute.OnCatchCounter);
			Assert.AreEqual(1, AllEventsAttribute.OnFinallyCounter);
		}

		[Args(Arg1 = "1", Arg2 = 2, Arg3 = [ 1, 2, 3 ])]
		[Args(Arg3 = new int[] { 2 })]
		[Args(Arg3 = new int[0])]
		[Args(Arg1 = "xyz")]
		[Args(Arg4 = 'w')]
		[Args(Arg5 = null)]
		[Args(Arg5 = typeof(int))]
		[Args(Arg5 = typeof(List<>))]
		[Args(Arg5 = typeof(List<DateTime>))]
		[Args(Arg5 = 1.10d)]
		internal string ArgsMethod()
		{
			return "0";
		}

		[TestMethod]
		public void ArgsTest()
		{
			var args = ArgsMethod();

			Assert.AreEqual("02", args);

			Console.WriteLine(args);
		}

		[Using]
		internal string UsingMethod()
		{
			return "0";
		}

		[TestMethod]
		public void UsingTest()
		{
			var args = UsingMethod();
		}
	}
}
