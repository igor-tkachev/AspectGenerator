using System;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	/// <summary>
	/// The interceptor constructs the applied aspect attribute again from its arguments. The aspect must receive the
	/// values the attribute itself holds, with their runtime types: an <c>object</c> slot keeps an enum, a byte, a nested
	/// array or a <c>typeof</c> as it is, not as the number or literal it is written with.
	/// </summary>
	public partial class CompilerTests
	{
		const string AttributeArgumentAspectSource =
			"""
			using System;
			using System.Globalization;
			using System.Linq;
			using System.Text;
			using AspectGenerator;

			public enum Mode { A, B }

			[Flags]
			public enum Options : byte { None = 0, X = 1, Y = 2 }

			public enum Signed : long { Negative = -5 }

			public static class Outer
			{
				public sealed class Nested<T>
				{
				}
			}

			[Aspect(OnBeforeCall = nameof(Before))]
			[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
			sealed class ArgsAttribute : Attribute
			{
				public ArgsAttribute(object? value)
				{
					Value = value;
				}

				public ArgsAttribute(object? value, params object?[] rest)
				{
					Value = value;
					Rest  = rest;
				}

				public object?   Value  { get; }
				public object?[]? Rest  { get; }
				public object?   Named  { get; set; }
				public Type?     Type   { get; set; }
				public string?   TargetFilter { get; set; }

				public static readonly StringBuilder Log = new();

				public static void Before(InterceptInfo info)
				{
					var aspect = (ArgsAttribute)info.Aspect;

					Log.Append(info.MemberInfo.Name).Append('=').Append(Describe(aspect.Value));

					if (aspect.Rest is not null)
						Log.Append(" rest ").Append(Describe(aspect.Rest));

					Log.Append(" named ").Append(Describe(aspect.Named)).Append(" type ").Append(Describe(aspect.Type)).Append(';');
				}

				static string Describe(object? value)
				{
					switch (value)
					{
						case null :
							return "null";
						case Type type :
							return $"typeof({type})";
						case Array array :
							return $"{value.GetType().Name}[{string.Join(",", array.Cast<object?>().Select(Describe))}]";
						case char c :
							return $"Char:U+{(int)c:X4}";
						case string s :
							return $"String:{string.Join(" ", s.Select(static ch => ((int)ch).ToString("X4", CultureInfo.InvariantCulture)))}";
						default :
							return $"{value.GetType().Name}:{Convert.ToString(value, CultureInfo.InvariantCulture)}";
					}
				}
			}
			""";

		[TestMethod]
		public void AspectReceivesAttributeArgumentsWithTheirTypesTest()
		{
			var result = RunGeneratorAndGetCompilation(
				[
					AttributeArgumentAspectSource,
					"""
					using System;
					using System.Collections.Generic;

					static class Calls
					{
						[Args(Mode.B)]
						public static int EnumInObject() { return 0; }

						[Args((Mode)(-1), Named = Options.X | Options.Y, Type = typeof(Outer.Nested<int>))]
						public static int NegativeAndFlagsEnum() { return 0; }

						[Args(Signed.Negative, Named = (Options)0)]
						public static int LongEnum() { return 0; }

						[Args(null, Named = null, Type = null)]
						public static int Nulls() { return 0; }

						[Args((byte)1, (sbyte)-2, (short)-3, (ushort)4, 5, 6u, -7L, 8ul, 'c', true, "s")]
						public static int Integers() { return 0; }

						[Args(1.5f, float.NaN, float.NegativeInfinity, float.MaxValue, -0.0f, 2.5d, double.PositiveInfinity, double.Epsilon, -0.0d, 1d)]
						public static int Floats() { return 0; }

						[Args('\0', '\'', '\u2028', "a\u2028b\"\\\0", "")]
						public static int Characters() { return 0; }

						[Args(new object?[] { Mode.A, (byte)2, null, typeof(int), typeof(List<>), typeof(int[]), typeof(Dictionary<,>), new int[] { 1, 2 }, new object?[] { Mode.B, new string[0], (string?)null }, (int[]?)null })]
						public static int ObjectArray() { return 0; }

						[Args(new Mode[] { Mode.B, Mode.A }, Named = new object?[0])]
						public static int EnumArray() { return 0; }

						[Args(typeof(Outer.Nested<>), Named = new Type[] { typeof(string), typeof(Mode?) })]
						public static int Types() { return 0; }

						public static string Run()
						{
							EnumInObject();
							NegativeAndFlagsEnum();
							LongEnum();
							Nulls();
							Integers();
							Floats();
							Characters();
							ObjectArray();
							EnumArray();
							Types();

							return ArgsAttribute.Log.ToString();
						}
					}
					"""
				],
				out var compilation);

			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");

			Assert.AreEqual(
				string.Join(
					"",
					"EnumInObject=Mode:B named null type null;",
					"NegativeAndFlagsEnum=Mode:-1 named Options:X, Y type typeof(Outer+Nested`1[System.Int32]);",
					"LongEnum=Signed:Negative named Options:None type null;",
					"Nulls=null named null type null;",
					"Integers=Byte:1 rest Object[][SByte:-2,Int16:-3,UInt16:4,Int32:5,UInt32:6,Int64:-7,UInt64:8,Char:U+0063,Boolean:True,String:0073] named null type null;",
					"Floats=Single:1.5 rest Object[][Single:NaN,Single:-Infinity,Single:3.4028235E+38,Single:-0,Double:2.5,Double:Infinity,Double:5E-324,Double:-0,Double:1] named null type null;",
					"Characters=Char:U+0000 rest Object[][Char:U+0027,Char:U+2028,String:0061 2028 0062 0022 005C 0000,String:] named null type null;",
					"ObjectArray=Object[][Mode:A,Byte:2,null,typeof(System.Int32),typeof(System.Collections.Generic.List`1[T]),typeof(System.Int32[]),typeof(System.Collections.Generic.Dictionary`2[TKey,TValue]),Int32[][Int32:1,Int32:2],Object[][Mode:B,String[][],null],null] named null type null;",
					"EnumArray=Mode[][Mode:B,Mode:A] named Object[][] type null;",
					"Types=typeof(Outer+Nested`1[T]) named Type[][typeof(System.String),typeof(System.Nullable`1[Mode])] type null;"),
				EmitAndRun(compilation, interceptors),
				interceptors);
		}

		[TestMethod]
		public void FilterAppliedAspectReceivesAttributeArgumentsWithTheirTypesTest()
		{
			var result = RunGeneratorAndGetCompilation(
				[
					AttributeArgumentAspectSource,
					"""
					using System;

					[assembly: Args(new object?[] { Mode.B, typeof(int) }, TargetFilter = "method: FromAssembly")]

					[Args(new object?[] { Mode.A, (short)1, new Mode[] { Mode.B } }, Named = Options.Y, TargetFilter = "method: FromType")]
					static class Calls
					{
						public static int FromType() { return 0; }

						public static int FromAssembly() { return 0; }

						public static string Run()
						{
							FromType();
							FromAssembly();

							return ArgsAttribute.Log.ToString();
						}
					}
					"""
				],
				out var compilation);

			var interceptors = GetGeneratedSourceOrEmpty(result, "Interceptors.g.cs");

			Assert.AreEqual(
				"FromType=Object[][Mode:A,Int16:1,Mode[][Mode:B]] named Options:Y type null;" +
				"FromAssembly=Object[][Mode:B,typeof(System.Int32)] named null type null;",
				EmitAndRun(compilation, interceptors),
				interceptors);
		}
	}
}
