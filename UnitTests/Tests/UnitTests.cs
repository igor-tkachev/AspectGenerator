using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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

		[Aspects.AllEvents]
		internal void AllEventsMethod()
		{
		}

		[TestMethod]
		public void AllEventsTest()
		{
			Aspects.AllEventsAttribute.ClearCounters();

			AllEventsMethod();

			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnInitCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnUsingCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnUsingCounterAsync);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnBeforeCallCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnBeforeCallCounterAsync);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnAfterCallCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnAfterCallCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnCatchCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnCatchCounterAsync);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnFinallyCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnFinallyCounterAsync);
		}

		[Aspects.AllEvents]
		internal async Task AllEventsMethodAsync()
		{
			await Task.FromResult(0);
		}

		[TestMethod]
		public async Task AllEventsTestAsync()
		{
			Aspects.AllEventsAttribute.ClearCounters();

			await AllEventsMethodAsync();

			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnInitCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnUsingCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnUsingCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnBeforeCallCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnBeforeCallCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnAfterCallCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnAfterCallCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnCatchCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnCatchCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnFinallyCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnFinallyCounterAsync);
		}

		[Aspects.AllEvents]
		internal async Task<int> AllEventsMethodAsync2()
		{
			return await Task.FromResult(4);
		}

		[TestMethod]
		public async Task AllEventsTestAsync2()
		{
			Aspects.AllEventsAttribute.ClearCounters();

			var r = await AllEventsMethodAsync2();

			Assert.AreEqual(4, r);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnInitCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnUsingCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnUsingCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnBeforeCallCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnBeforeCallCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnAfterCallCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnAfterCallCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnCatchCounter);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnCatchCounterAsync);
			Assert.AreEqual(0, Aspects.AllEventsAttribute.OnFinallyCounter);
			Assert.AreEqual(1, Aspects.AllEventsAttribute.OnFinallyCounterAsync);
		}

		[Aspects.Args(Arg1 = "1", Arg2 = 2, Arg3 = [ 1, 2, 3 ])]
		[Aspects.Args(Arg3 = new int[] { 2 })]
		[Aspects.Args(Arg3 = new int[0])]
		[Aspects.Args(Arg1 = "xyz")]
		[Aspects.Args(Arg4 = 'w')]
		[Aspects.Args(Arg5 = null)]
		[Aspects.Args(Arg5 = typeof(int))]
		[Aspects.Args(Arg5 = typeof(List<>))]
		[Aspects.Args(Arg5 = typeof(List<DateTime>))]
		[Aspects.Args(Arg5 = 1.10d)]
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

		[Aspects.Using]
		internal string UsingMethod()
		{
			return "0";
		}

		[TestMethod]
		public void UsingTest()
		{
			var args = UsingMethod();
		}

		[Aspects.Using]
		internal Task<string> UsingMethodAsync()
		{
			return Task.FromResult("0");
		}

		[TestMethod]
		public async Task UsingAsyncTest()
		{
			var args = await UsingMethodAsync();
		}

		[Aspects.Ordered(Order = 2, Value = "1")]
		[Aspects.Ordered(Order = 1, Value = "2")]
		internal static string OrderedMethod()
		{
			return "0";
		}

		[Aspects.Ordered(Order = 2, Value = "2")]
		[Aspects.Ordered(Order = 3, Value = "3")]
		internal static int OrderedMethod2()
		{
			return 1;
		}

		[TestMethod]
		public void OrderedTest()
		{
			var s1 = OrderedMethod();

			Assert.AreEqual("012", s1);

			var i2 = OrderedMethod2();

			Assert.AreEqual(132, i2);
		}

		[Aspects.Arguments]
		internal static string ArgumentsMethod()
		{
			return "_";
		}


		[TestMethod]
		public void ArgumentsTest()
		{
			var s = ArgumentsMethod();
			Assert.AreEqual("_0", s);
		}

		[Aspects.Arguments]
		internal static string ArgumentsMethod(string s, int i)
		{
			return s + i;
		}

		[TestMethod]
		public void Arguments2Test()
		{
			var s = ArgumentsMethod("_", 1);
			Assert.AreEqual("_12", s);
		}

		[Aspects.Arguments]
		internal static string ArgumentsRefMethod(string s, int i, ref bool b)
		{
			return s + i + b;
		}

		[TestMethod]
		public void ArgumentsRefTest()
		{
			var b = true;
			var s = ArgumentsRefMethod("_", 1, ref b);

			Assert.AreEqual("_1True3", s);
		}

		[Aspects.Arguments]
		internal static string ArgumentsInMethod(string s, int i, in bool b, ref int? _)
		{
			return s + i + b;
		}

		[TestMethod]
		public void ArgumentsInTest()
		{
			var  b = true;
			int? n = 1;
			var  s = ArgumentsInMethod("_", 1, in b, ref n);

			Assert.AreEqual("_1True4", s);
		}

		[Aspects.Arguments]
		internal static string ArgumentsInMethod(string s, int i, bool b, ref int? _)
		{
			return s + i + b;
		}

		[TestMethod]
		public void ArgumentsIn1Test()
		{
			var  b = true;
			int? n = 1;
			var  s = ArgumentsInMethod("_", 1, b, ref n);

			Assert.AreEqual("_1True4", s);
		}

		[Aspects.Arguments]
		internal static string ArgumentsOutMethod(out string s)
		{
			s = "1";
			return "_" + s;
		}

		[TestMethod]
		public void ArgumentsOutTest()
		{
			var s1 = ArgumentsOutMethod(out _);

			Assert.AreEqual("_11", s1);
		}

		[TestMethod]
		public void MultiProjectMainTest()
		{
			var s = ClassLibrary.TestClass.MainMethod("Main");

			Console.WriteLine(s);
			Assert.AreEqual("Main MainMethod TestMethod + CrossProject aspect.", s);
		}

		[TestMethod]
		public void MultiProjectTestTest()
		{
			var s = ClassLibrary.TestClass.TestMethod("Test");

			Console.WriteLine(s);
			Assert.AreEqual("Test TestMethod + CrossProject aspect.", s);
		}

		public static string InterceptedMethod(string str)
		{
			return str + " InterceptedMethod";
		}

		[TestMethod]
		public void InterceptedMethodTest()
		{
			var s = InterceptedMethod("Intercepted");

			Console.WriteLine(s);
			Assert.AreEqual("Intercepted InterceptedMethod + InterceptMethods aspect.", s);
		}

		public static T InterceptedGenericMethod<T>(T p)
		{
			return p;
		}

		[TestMethod]
		public void InterceptedGenericMethodTest()
		{
			var s = InterceptedGenericMethod("Intercepted");

			Console.WriteLine(s);
			Assert.AreEqual("Intercepted + InterceptMethods aspect.", s);

			var i = InterceptedGenericMethod(10);

			Console.WriteLine(i);

			Assert.AreEqual(10, i);
		}

		[TestMethod]
		public void SubstringInterceptTest()
		{
			var s = "test string".Substring(5);

			Console.WriteLine(s);
			Assert.AreEqual("string + InterceptMethods aspect.", s);
		}

		[OnCall]
		public static int OnCallTestMethod(int n)
		{
			return n;
		}

		[TestMethod]
		public void OnCallTest()
		{
			var n = OnCallTestMethod(2);

			Console.WriteLine(n);
			Assert.AreEqual(4, n);
		}

		[TestMethod]
		public void OnCallMemberTest()
		{
			var obj = new OnCallObject();
			var n   = obj.OnCall(2);

			Console.WriteLine(n);
			Assert.AreEqual(6, n);
		}
	}
}
