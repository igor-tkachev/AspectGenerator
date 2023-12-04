﻿// <auto-generated/>
#pragma warning disable
#nullable enable

using System;

using SR  = System.Reflection;
using SLE = System.Linq.Expressions;
using SCG = System.Collections.Generic;

namespace AspectGenerator
{
	using AspectGenerator = AspectGenerator;

	static partial class Interceptors
	{
		static SR.MethodInfo GetMethodInfo(SLE.Expression expr)
		{
			return expr switch
			{
				SLE.MethodCallExpression mc => mc.Method,
				_                           => throw new InvalidOperationException()
			};
		}

		static SR.MethodInfo MethodOf<T>(SLE.Expression<Func<T>> func) => GetMethodInfo(func.Body);
		static SR.MethodInfo MethodOf   (SLE.Expression<Action>  func) => GetMethodInfo(func.Body);

		static SR. MemberInfo                 AsyncMethod_Interceptor_MemberInfo        = MethodOf(() => OpenTelemetryAspect.Program.AsyncMethod());
		static SCG.Dictionary<string,object?> AsyncMethod_Interceptor_AspectArguments_0 = new()
		{
		};
		//
		/// <summary>
		/// Intercepts OpenTelemetryAspect.Program.AsyncMethod().
		/// </summary>
		//
		// Intercepts AsyncMethod().
		[System.Runtime.CompilerServices.InterceptsLocation(@"P:\AspectGenerator\Examples\OpenTelemetryAspect\Program.cs", line: 19, character: 8)]
		//
		[System.Runtime.CompilerServices.CompilerGenerated]
		//[System.Diagnostics.DebuggerStepThrough]
		public static async System.Threading.Tasks.Task<string> AsyncMethod_Interceptor()
		{
			// Aspects.MetricsAttribute
			//
			var __info__0 = new AspectGenerator.InterceptInfo<string>
			{
				MemberInfo      = AsyncMethod_Interceptor_MemberInfo,
				AspectType      = typeof(Aspects.MetricsAttribute),
				AspectArguments = AsyncMethod_Interceptor_AspectArguments_0,
			};

			await using (Aspects.MetricsAttribute.OnUsingAsync(__info__0))
			{
				try
				{
					__info__0.ReturnValue = await OpenTelemetryAspect.Program.AsyncMethod();
				}
				catch (Exception __ex__)
				{
					__info__0.Exception = __ex__;
					throw;
				}
				finally
				{
					__info__0.InterceptType = AspectGenerator.InterceptType.OnFinally;
					Aspects.MetricsAttribute.OnFinally(__info__0);
				}
			}

			return __info__0.ReturnValue;
		}

		static SR. MemberInfo                 Method1_Interceptor_MemberInfo        = MethodOf(() => OpenTelemetryAspect.Program.Method1());
		static SCG.Dictionary<string,object?> Method1_Interceptor_AspectArguments_0 = new()
		{
		};
		//
		/// <summary>
		/// Intercepts OpenTelemetryAspect.Program.Method1().
		/// </summary>
		//
		// Intercepts Method1().
		[System.Runtime.CompilerServices.InterceptsLocation(@"P:\AspectGenerator\Examples\OpenTelemetryAspect\Program.cs", line: 15, character: 4)]
		//
		// Intercepts Method1().
		[System.Runtime.CompilerServices.InterceptsLocation(@"P:\AspectGenerator\Examples\OpenTelemetryAspect\Program.cs", line: 17, character: 4)]
		//
		[System.Runtime.CompilerServices.CompilerGenerated]
		//[System.Diagnostics.DebuggerStepThrough]
		public static void Method1_Interceptor()
		{
			// Aspects.MetricsAttribute
			//
			var __info__0 = new AspectGenerator.InterceptInfo<AspectGenerator.Void>
			{
				MemberInfo      = Method1_Interceptor_MemberInfo,
				AspectType      = typeof(Aspects.MetricsAttribute),
				AspectArguments = Method1_Interceptor_AspectArguments_0,
			};

			using (Aspects.MetricsAttribute.OnUsing(__info__0))
			{
				try
				{
					OpenTelemetryAspect.Program.Method1();
				}
				catch (Exception __ex__)
				{
					__info__0.Exception = __ex__;
					throw;
				}
				finally
				{
					__info__0.InterceptType = AspectGenerator.InterceptType.OnFinally;
					Aspects.MetricsAttribute.OnFinally(__info__0);
				}
			}
		}

		static SR. MemberInfo                 Method2_Interceptor_MemberInfo        = MethodOf(() => OpenTelemetryAspect.Program.Method2());
		static SCG.Dictionary<string,object?> Method2_Interceptor_AspectArguments_0 = new()
		{
		};
		//
		/// <summary>
		/// Intercepts OpenTelemetryAspect.Program.Method2().
		/// </summary>
		//
		// Intercepts Method2().
		[System.Runtime.CompilerServices.InterceptsLocation(@"P:\AspectGenerator\Examples\OpenTelemetryAspect\Program.cs", line: 16, character: 4)]
		//
		[System.Runtime.CompilerServices.CompilerGenerated]
		//[System.Diagnostics.DebuggerStepThrough]
		public static void Method2_Interceptor()
		{
			// Aspects.MetricsAttribute
			//
			var __info__0 = new AspectGenerator.InterceptInfo<AspectGenerator.Void>
			{
				MemberInfo      = Method2_Interceptor_MemberInfo,
				AspectType      = typeof(Aspects.MetricsAttribute),
				AspectArguments = Method2_Interceptor_AspectArguments_0,
			};

			using (Aspects.MetricsAttribute.OnUsing(__info__0))
			{
				try
				{
					OpenTelemetryAspect.Program.Method2();
				}
				catch (Exception __ex__)
				{
					__info__0.Exception = __ex__;
					throw;
				}
				finally
				{
					__info__0.InterceptType = AspectGenerator.InterceptType.OnFinally;
					Aspects.MetricsAttribute.OnFinally(__info__0);
				}
			}
		}

		static SR. MemberInfo                 MethodException_Interceptor_MemberInfo        = MethodOf(() => OpenTelemetryAspect.Program.MethodException());
		static SCG.Dictionary<string,object?> MethodException_Interceptor_AspectArguments_0 = new()
		{
		};
		static SCG.Dictionary<string,object?> MethodException_Interceptor_AspectArguments_1 = new()
		{
		};
		//
		/// <summary>
		/// Intercepts OpenTelemetryAspect.Program.MethodException().
		/// </summary>
		//
		// Intercepts MethodException().
		[System.Runtime.CompilerServices.InterceptsLocation(@"P:\AspectGenerator\Examples\OpenTelemetryAspect\Program.cs", line: 18, character: 4)]
		//
		[System.Runtime.CompilerServices.CompilerGenerated]
		//[System.Diagnostics.DebuggerStepThrough]
		public static void MethodException_Interceptor()
		{
			// Aspects.IgnoreCatchAttribute
			//
			var __info__0 = new AspectGenerator.InterceptInfo<AspectGenerator.Void>
			{
				MemberInfo      = MethodException_Interceptor_MemberInfo,
				AspectType      = typeof(Aspects.IgnoreCatchAttribute),
				AspectArguments = MethodException_Interceptor_AspectArguments_0,
			};

			try
			{
				{
					// Aspects.MetricsAttribute
					//
					var __info__1 = new AspectGenerator.InterceptInfo<AspectGenerator.Void>
					{
						MemberInfo      = MethodException_Interceptor_MemberInfo,
						AspectType      = typeof(Aspects.MetricsAttribute),
						AspectArguments = MethodException_Interceptor_AspectArguments_1,
						PreviousInfo    = __info__0
					};

					using (Aspects.MetricsAttribute.OnUsing(__info__1))
					{
						try
						{
							OpenTelemetryAspect.Program.MethodException();
						}
						catch (Exception __ex__)
						{
							__info__1.Exception = __ex__;
							throw;
						}
						finally
						{
							__info__1.InterceptType = AspectGenerator.InterceptType.OnFinally;
							Aspects.MetricsAttribute.OnFinally(__info__1);
						}
					}

					__info__0.ReturnValue = __info__1.ReturnValue;
				}
			}
			catch (Exception __ex__)
			{
				__info__0.Exception       = __ex__;
				__info__0.InterceptResult = AspectGenerator.InterceptResult.ReThrow;
				__info__0.InterceptType   = AspectGenerator.InterceptType.OnCatch;

				Aspects.IgnoreCatchAttribute.OnCatch(__info__0);

				if (__info__0.InterceptResult == AspectGenerator.InterceptResult.ReThrow)
					throw;
			}
		}
	}
}
