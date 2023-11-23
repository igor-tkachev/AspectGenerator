using System;
using System.Threading;
using System.Threading.Tasks;

using Aspects;

namespace OpenTelemetryAspect
{
	static class Program
	{
		static void Main()
		{
			using var __ = OpenTelemetryFactory.Create();

			Method1();
			Method2();
			Method1();
			MethodException();
			_ = AsyncMethod().Result;
		}

		[Metrics]
		public static void Method1()
		{
			Thread.Sleep(100);
		}

		[Metrics]
		public static void Method2()
		{
			Thread.Sleep(200);
		}

		[IgnoreCatch, Metrics]
		public static void MethodException()
		{
			throw new();
		}

		[Metrics]
		public static Task<string> AsyncMethod()
		{
			Thread.Sleep(150);
			return Task.FromResult("123");
		}
	}
}
