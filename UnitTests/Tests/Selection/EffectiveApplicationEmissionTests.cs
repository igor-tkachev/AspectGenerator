using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using AspectGenerator.Tests.TargetFilterBinding;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.Selection
{
	/// <summary>
	/// What the generated code builds an aspect from once the target filters have decided its settings: the constructor
	/// arguments of one attribute, each named argument of the attribute that writes it. The applications are built by hand
	/// here, as the selection that makes them is not what is tested.
	/// </summary>
	[TestClass]
	public class EffectiveApplicationEmissionTests
	{
		const string Source = """
			using System;

			[assembly: Shop.Log(Level = 2, Order = 5, Mode = Shop.Mode.Slow)]

			namespace Shop
			{
				public enum Mode { Fast, Slow }

				public sealed class LogAttribute : Attribute
				{
					public LogAttribute()
					{
					}

					public LogAttribute(int code, string name = "x")
					{
					}

					public LogAttribute(object value)
					{
					}

					LogAttribute(long hidden, long hiddenToo)
					{
					}

					public string[]? TargetFilter { get; set; }
					public int       Level        { get; set; }
					public int       Order        { get; set; }
					public Mode      Mode         { get; set; }
					public Type?     Kind         { get; set; }
					public int[]?    Codes        { get; set; }
					public object?   Value        { get; set; }
				}
			}

			[Shop.Log(7, "seven", Level = 3, TargetFilter = ["public"])]
			static class First
			{
				public static void Run<T>(T value) { }

				[Shop.Log(Level = 9)]
				public static void Own() { }
			}

			[Shop.Log(7, "seven", Level = 4, TargetFilter = ["public"])]
			static class Second
			{
			}

			[Shop.Log(null)]
			static class NullObject
			{
			}

			[Shop.Log(1, Kind = typeof(string), Codes = new[] { 1, 2 }, Value = new object[] { Shop.Mode.Fast, typeof(int), null }, Mode = Shop.Mode.Fast, Order = 3, TargetFilter = ["x"])]
			static class Everything
			{
			}
			""";

		static readonly CSharpCompilation _compilation = FilterProbe.Compile(Source);

		static INamedTypeSymbol Type(string name)
		{
			return _compilation.GetTypeByMetadataName(name) ?? throw new InvalidOperationException(name);
		}

		static AttributeData TypeAttribute(string type)
		{
			return Type(type).GetAttributes().Single();
		}

		static AttributeData AssemblyAttribute()
		{
			return _compilation.Assembly.GetAttributes().Single(static a => a.AttributeClass?.Name == "LogAttribute");
		}

		static EffectiveNamedArgument Named(AttributeData source, string name)
		{
			var argument = source.NamedArguments.Single(a => a.Key == name);

			return new EffectiveNamedArgument(name, argument.Value, source);
		}

		static AttributeInfo Info(EffectiveApplication? effective = null, AttributeData? applied = null)
		{
			return new AttributeInfo(applied, null, null, Type("Shop.LogAttribute"), null, null, null, null) { EffectiveApplication = effective };
		}

		/// <summary>The application a type attribute writes the constructor of, with the named arguments given.</summary>
		static EffectiveApplication Application(AttributeData constructorSource, AttributeData reportAt, params EffectiveNamedArgument[] named)
		{
			return new EffectiveApplication(
				reportAt,
				constructorSource.AttributeConstructor,
				constructorSource.ConstructorArguments,
				constructorSource,
				[..named]);
		}

		static string Construction(AttributeInfo attribute)
		{
			return AspectSourceGenerator.GetAppliedAspectConstruction(attribute);
		}

		[TestMethod]
		public void ConstructorFromTheTypeAndLevelFromTheAssemblyBuildOneInstanceTest()
		{
			var type     = TypeAttribute("First");
			var assembly = AssemblyAttribute();

			var effective = Application(type, type, Named(assembly, "Level"));

			Assert.AreEqual("new global::Shop.LogAttribute(7, \"seven\") { Level = 2 }", Construction(Info(effective)));
		}

		[TestMethod]
		public void NamedArgumentOfTheMoreSpecificLevelIsTheOneBuiltTest()
		{
			var type     = TypeAttribute("First");
			var assembly = AssemblyAttribute();

			// Level is written on the assembly (2) and on the type (3); the effective application holds the type's, and
			// Order, which only the assembly writes, from the assembly. Named arguments go from the least specific level to
			// the most specific one.
			var effective = Application(type, type, Named(assembly, "Order"), Named(type, "Level"));

			Assert.AreEqual("new global::Shop.LogAttribute(7, \"seven\") { Order = 5, Level = 3 }", Construction(Info(effective)));
		}

		[TestMethod]
		public void TargetFilterIsNeverBuiltTest()
		{
			var type = TypeAttribute("First");

			var effective = new EffectiveApplication(
				type,
				type.AttributeConstructor,
				type.ConstructorArguments,
				type,
				[Named(type, "Level"), Named(type, "TargetFilter")]);

			Assert.AreEqual("new global::Shop.LogAttribute(7, \"seven\") { Level = 3 }", Construction(Info(effective)));
		}

		[TestMethod]
		public void OrderComesFromTheEffectiveApplicationTest()
		{
			var assembly   = AssemblyAttribute();
			var first      = TypeAttribute("First");
			var second     = TypeAttribute("Second");
			var everything = TypeAttribute("Everything");

			// None of the attributes the aspects are applied with (First, Second) writes Order. The effective applications
			// take it from the assembly (5) and from another attribute (3); the third has none and goes last.
			var late  = Info(Application(first,  first,  Named(assembly,   "Order")), first);
			var early = Info(Application(second, second, Named(everything, "Order")), second);
			var none  = Info(Application(second, second), second);

			var ordered = InterceptorNamingService.GetOrderedAttributes([late, none, early]);

			CollectionAssert.AreEqual(new[] { early, late, none }, ordered.ToArray());
		}

		[TestMethod]
		public void OrderOfAnAspectWithoutAnEffectiveApplicationIsStillReadFromItsAttributeTest()
		{
			var everything = TypeAttribute("Everything");
			var first      = TypeAttribute("First");

			var ordered = InterceptorNamingService.GetOrderedAttributes([Info(applied: first), Info(applied: everything)]);

			// Everything writes Order = 3; First writes none and goes last.
			Assert.AreSame(everything, ordered[0].AppliedAttributeData);
			Assert.AreSame(first,      ordered[1].AppliedAttributeData);
		}

		[TestMethod]
		public void EnumTypeofAndArrayArgumentsPrintAsTheAttributePrintsThemTest()
		{
			var everything = TypeAttribute("Everything");

			var named = everything.NamedArguments.Where(static a => a.Key != "TargetFilter").Select(a => Named(everything, a.Key)).ToArray();
			var viaEffective = Construction(Info(Application(everything, everything, named)));
			var viaAttribute = Construction(Info(applied: everything));

			Assert.AreEqual(viaAttribute, viaEffective);
			StringAssert.Contains(viaEffective, "Kind = typeof(string)");
			StringAssert.Contains(viaEffective, "Codes = new int[] { 1, 2 }");
			StringAssert.Contains(viaEffective, "Value = new object[] { (global::Shop.Mode)0, typeof(int), null }");
		}

		[TestMethod]
		public void NullObjectArgumentKeepsItsTypeTest()
		{
			var attribute = TypeAttribute("NullObject");

			var effective = Application(attribute, attribute);

			Assert.AreEqual("new global::Shop.LogAttribute((object)null)", Construction(Info(effective)));
			Assert.AreEqual(Construction(Info(applied: attribute)), Construction(Info(effective)));
		}

		[TestMethod]
		public void AspectAppliedAsWrittenIsBuiltAsBeforeTest()
		{
			var type = TypeAttribute("First");

			// TargetFilter is not built; the attribute's own constructor and named arguments are.
			Assert.AreEqual("new global::Shop.LogAttribute(7, \"seven\") { Level = 3 }", Construction(Info(applied: type)));
			Assert.AreEqual("new global::Shop.LogAttribute()", Construction(Info()));
		}

		[TestMethod]
		public void ConstructorIsTheEffectiveOneNotTheAppliedOneTest()
		{
			var type    = TypeAttribute("First");
			var hidden  = Type("Shop.LogAttribute").InstanceConstructors.Single(static c => c.Parameters.Length == 2 && c.Parameters[0].Type.SpecialType == SpecialType.System_Int64);

			// An effective application whose constructor cannot be named from the interceptors is reported, whatever the
			// attribute the aspect is applied with calls.
			var effective = new EffectiveApplication(type, hidden, type.ConstructorArguments, type, []);

			Assert.IsNull(InterceptorShape.GetInaccessibleAspectReason(Info(applied: type), _compilation, false));
			StringAssert.Contains(InterceptorShape.GetInaccessibleAspectReason(Info(effective, type), _compilation, false), "constructor");
			Assert.IsNull(InterceptorShape.GetInaccessibleAspectReason(Info(Application(type, type), type), _compilation, false));
		}

		[TestMethod]
		public void AspectIsAppliedToTheTargetWhenItsReportingAttributeIsTest()
		{
			var own    = Type("First").GetMembers("Own").OfType<IMethodSymbol>().Single();
			var run    = Type("First").GetMembers("Run").OfType<IMethodSymbol>().Single();
			var onType = TypeAttribute("First");
			var onOwn  = own.GetAttributes().Single();

			// The method carries its own attribute: it is the most specific one, so it reports the aspect.
			Assert.IsTrue (InterceptorShape.IsAppliedToTarget(own, [Info(Application(onType, onOwn), onType)]));
			Assert.IsTrue (InterceptorShape.IsAppliedToTarget(own, [Info(applied: onOwn)]));

			// Selected by the attribute of its type: not applied to the method itself, whatever else is on it.
			Assert.IsFalse(InterceptorShape.IsAppliedToTarget(run, [Info(Application(onType, onType), onType)]));
			Assert.IsFalse(InterceptorShape.IsAppliedToTarget(own, [Info(Application(onType, onType), onOwn)]));
			Assert.IsFalse(InterceptorShape.IsAppliedToTarget(run, [Info()]));
		}

		[TestMethod]
		public void ReportingAttributeIsTheEffectiveOneTest()
		{
			var onType     = TypeAttribute("First");
			var assembly   = AssemblyAttribute();
			var effective  = Application(onType, assembly);

			Assert.AreSame(assembly, AspectSymbols.GetReportingAttribute(Info(effective, onType)));
			Assert.AreSame(onType,   AspectSymbols.GetReportingAttribute(Info(applied: onType)));
			Assert.IsNull(AspectSymbols.GetReportingAttribute(Info()));
		}

		[TestMethod]
		public void CallsWithEqualEffectiveSettingsShareAnInterceptorAndDifferentOnesDoNotTest()
		{
			var compilation = FilterProbe.Compile(Source + """

				static class Calls
				{
					public static void Run()
					{
						First.Own();
						First.Own();
						First.Own();
					}
				}
				""", "Grouping");

			var tree   = compilation.SyntaxTrees[0];
			var model  = compilation.GetSemanticModel(tree);
			var calls  = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Where(static c => c.Expression.ToString().StartsWith("First.Own", StringComparison.Ordinal)).ToArray();
			var method = (IMethodSymbol)model.GetSymbolInfo(calls[0]).Symbol!;
			var first  = compilation.GetTypeByMetadataName("First")!.GetAttributes().Single();
			var second = compilation.GetTypeByMetadataName("Second")!.GetAttributes().Single();

			AttributeInfo Level(AttributeData source)
			{
				var effective = new EffectiveApplication(source, source.AttributeConstructor, source.ConstructorArguments, source, [new EffectiveNamedArgument("Level", source.NamedArguments.Single(static a => a.Key == "Level").Value, source)]);

				return new AttributeInfo(null, null, null, compilation.GetTypeByMetadataName("Shop.LogAttribute")!, null, null, null, null) { EffectiveApplication = effective };
			}

			// Three calls of one target (two different methods are never grouped, whatever their settings): one decided from the first type attribute (Level 3), one from the second (Level 4), one more from the first again,
			// built separately from the first call's: equal by value, so the same interceptor.
			var invocations = ImmutableArray.Create(
				new AnalyzedInvocation(calls[0], method, [Level(first)]),
				new AnalyzedInvocation(calls[1], method, [Level(second)]),
				new AnalyzedInvocation(calls[2], method, [Level(first)]));

			Assert.AreEqual(Level(first), Level(first));
			Assert.AreNotEqual(Level(first), Level(second));
			Assert.AreEqual(Level(first).GetHashCode(), Level(first).GetHashCode());

			Assert.IsFalse(InterceptorGroupComparer.Instance.Equals(invocations[0], invocations[1]));
			Assert.IsTrue (InterceptorGroupComparer.Instance.Equals(invocations[0], invocations[2]));

			var interceptors = InterceptorNamingService.Create(invocations);

			Assert.AreEqual(2, interceptors.Count);
			Assert.AreEqual(2, interceptors[0].Invocations.Count);
			Assert.AreEqual(1, interceptors[1].Invocations.Count);
		}
	}
}
