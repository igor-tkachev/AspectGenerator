using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AspectGenerator.TargetFilterFacts;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// Awaitability is the compiler's answer where the type is written: at the call site for a call, in the declaring
	/// method for a source definition. Types that cannot be named from an unrelated place (file-local, private nested,
	/// keyword-named, anonymous) must still be answered.
	/// </summary>
	[TestClass]
	public class AwaitProbeTests
	{
		const string Awaiter =
			"""
			using System;
			using System.Runtime.CompilerServices;
			public class Awr : INotifyCompletion
			{
				public bool IsCompleted { get { return true; } }
				public void GetResult() { }
				public void OnCompleted(Action a) { }
			}
			""";

		static CSharpCompilation Compile(params string[] sources)
		{
			var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
			var refs    = trusted.Split(Path.PathSeparator).Where(static p => !Path.GetFileName(p).StartsWith("AspectGenerator.", StringComparison.OrdinalIgnoreCase)).Select(static p => (MetadataReference)MetadataReference.CreateFromFile(p));
			var parse   = new CSharpParseOptions(LanguageVersion.Preview);
			var trees   = sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, parse, $"File{i}.cs")).ToList();
			var compilation = CSharpCompilation.Create("Probe", trees, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
			var errors      = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).Select(static d => d.ToString()).ToList();

			Assert.AreEqual(0, errors.Count, string.Join(Environment.NewLine, errors));

			return compilation;
		}

		static (IMethodSymbol Method, InvocationExpressionSyntax Site) FindCall(Compilation compilation, string text)
		{
			foreach (var tree in compilation.SyntaxTrees)
			{
				var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(i => i.ToString() == text);

				if (invocation is not null)
					return ((IMethodSymbol)compilation.GetSemanticModel(tree).GetSymbolInfo(invocation).Symbol!, invocation);
			}

			throw new InvalidOperationException(text);
		}

		static bool Async(Compilation compilation, IMethodSymbol method, SyntaxNode? site)
		{
			return MethodFacts.Create(method, FactProperty.Async, new FactContext(compilation), site).IsAsync;
		}

		static void AssertAwaitable(Compilation compilation, string call, bool expected, bool alsoAsDefinition = true)
		{
			var (method, site) = FindCall(compilation, call);

			Assert.AreEqual(expected, Async(compilation, method, site), $"at the call site: {call}");

			if (alsoAsDefinition)
				Assert.AreEqual(expected, Async(compilation, method.OriginalDefinition, null), $"as a definition: {call}");
		}

		[TestMethod]
		public void AFileLocalAwaitableIsFoundNextToAGlobalTypeOfTheSameName()
		{
			var compilation = Compile(
				Awaiter,
				"""
				file class A { public Awr GetAwaiter() { return new Awr(); } }
				file static class U
				{
					static A Make() { return new A(); }
					static A MakeExpression() => new A();
					static readonly object Field = Make();
					public static void Use() { Make(); MakeExpression(); }
				}
				""",
				"public class A { }");

			AssertAwaitable(compilation, "Make()", true);
			AssertAwaitable(compilation, "MakeExpression()", true);
		}

		[TestMethod]
		public void AnAwaitableNamedLikeAKeywordIsFound()
		{
			var compilation = Compile(
				Awaiter,
				"""
				public class @event { public Awr GetAwaiter() { return new Awr(); } }
				public class Holder<T> { public T Get() { return default!; } }
				public static class V
				{
					public static @event Make() { return new @event(); }
					public static void Use() { Make(); new Holder<@event>().Get(); }
				}
				""");

			AssertAwaitable(compilation, "Make()", true);

			// Without a call site the constructed return type is all there is.
			var (method, site) = FindCall(compilation, "new Holder<@event>().Get()");

			Assert.IsTrue(Async(compilation, method, site), "at the call site");
			Assert.IsTrue(Async(compilation, method, null), "without a call site");
		}

		[TestMethod]
		public void AnAwaitableWithAnAnonymousTypeArgumentIsFoundAtTheCallSite()
		{
			var compilation = Compile(
				Awaiter,
				"""
				public class Aw<T> { public Awr GetAwaiter() { return new Awr(); } }
				static class W
				{
					public static Aw<T> Of<T>(T value) { return new Aw<T>(); }
					public static void Use() { Of(new { X = 1 }); }
				}
				""");

			var (method, site) = FindCall(compilation, "Of(new { X = 1 })");

			Assert.IsTrue(Async(compilation, method, site));
		}

		[TestMethod]
		public void APrivateNestedAwaitableIsAwaitedWhereItsOwnerSeesIt()
		{
			var compilation = Compile(
				Awaiter,
				"""
				public class Owner
				{
					class A { public Awr GetAwaiter() { return new Awr(); } }
					static A Make() { return new A(); }
					protected class P { public Awr GetAwaiter() { return new Awr(); } }
					static P MakeP() { return new P(); }
					public static void Use() { Make(); MakeP(); }
				}
				""");

			AssertAwaitable(compilation, "Make()", true);
			AssertAwaitable(compilation, "MakeP()", true);
		}

		[TestMethod]
		public void ADefinitionWithoutABodyIsReadWhereItIsDeclared()
		{
			var compilation = Compile(
				Awaiter,
				"""
				file class A { public Awr GetAwaiter() { return new Awr(); } }
				file abstract class Owner
				{
					public abstract A Make();
					public void Other() { }
				}
				""",
				"public class A { }");

			var method = compilation.GetTypeByMetadataName("Owner")?.GetMembers("Make").OfType<IMethodSymbol>().SingleOrDefault()
				?? compilation.GetSymbolsWithName("Owner").OfType<INamedTypeSymbol>().Single().GetMembers("Make").OfType<IMethodSymbol>().Single();

			Assert.IsTrue(Async(compilation, method, null));
		}
	}
}
