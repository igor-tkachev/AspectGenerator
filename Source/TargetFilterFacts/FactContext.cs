using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspectGenerator.TargetFilterFacts
{
	/// <summary>
	/// What a fact depends on besides the method itself: the compilation the facts are read for, which answers the
	/// questions only the compiler can answer (what an <c>await</c> accepts, with the references and friend
	/// assemblies of that compilation).
	/// </summary>
	sealed class FactContext
	{
		readonly System.Collections.Concurrent.ConcurrentDictionary<ITypeSymbol, AwaitProbe.Answer> _awaitable = new(SymbolEqualityComparer.Default);
		readonly System.Collections.Concurrent.ConcurrentDictionary<(ITypeSymbol, INamedTypeSymbol?), bool> _scoped = new(new ScopedKeyComparer());

		public FactContext(Compilation compilation, IAssemblySymbol? projectAssembly = null)
		{
			Compilation     = compilation;
			_host           = new System.Lazy<ProbeHost?>(() => CreateHost(compilation), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
			ProjectAssembly = projectAssembly ?? compilation.Assembly;
		}

		public Compilation Compilation { get; }

		/// <summary>
		/// The assembly <c>project</c> means: the compilation's own assembly, or, when a library's own filters are
		/// evaluated, the library's assembly.
		/// </summary>
		public IAssemblySymbol ProjectAssembly { get; }

		int _probes;

		/// <summary>How many times the compiler was asked; tests pin that identical calls are not asked again.</summary>
		internal int ProbeCount
		{
			get { return _probes; }
		}

		internal void CountProbe()
		{
			System.Threading.Interlocked.Increment(ref _probes);
		}

		readonly System.Lazy<ProbeHost?> _host;
		readonly System.Collections.Concurrent.ConcurrentDictionary<SyntaxTree, SemanticModel> _models = new();

		/// <summary>The semantic model of a tree of the compilation, made once: its binders are reused by every probe in the tree.</summary>
		internal SemanticModel GetModel(SyntaxTree tree)
		{
			return _models.GetOrAdd(tree, t => Compilation.GetSemanticModel(t));
		}


		/// <summary>
		/// The one forked compilation the probes of types that cannot be read from syntax are bound in: the compilation
		/// plus a tree with an empty async method to bind statements inside, speculatively. Made on first use, once, whoever
		/// asks first; null when the compilation is not a C# one.
		/// </summary>
		internal ProbeHost? ProbeHost
		{
			get { return _host.Value; }
		}

		static ProbeHost? CreateHost(Compilation compilation)
		{
			if (compilation is not CSharpCompilation csharp)
				return null;

			var options = csharp.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions ?? CSharpParseOptions.Default;
			var tree    = CSharpSyntaxTree.ParseText("static class __AspectGeneratorAwaitProbe { static async void Host() { int position = 0; } }", options);
			var model   = csharp.AddSyntaxTrees(tree).GetSemanticModel(tree);
			var body    = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single().Body!;

			return new ProbeHost(model, body.Statements[0].SpanStart);
		}

		/// <summary>The answers of <see cref="AwaitProbe"/> that hold wherever the type is awaited, one per type symbol.</summary>
		internal System.Collections.Concurrent.ConcurrentDictionary<ITypeSymbol, AwaitProbe.Answer> AwaitableCache
		{
			get { return _awaitable; }
		}

		/// <summary>The answers that depend on where the type is awaited: per type symbol and containing type of the code.</summary>
		internal System.Collections.Concurrent.ConcurrentDictionary<(ITypeSymbol, INamedTypeSymbol?), bool> ScopedAwaitableCache
		{
			get { return _scoped; }
		}
	}

	sealed class ProbeHost
	{
		public ProbeHost(SemanticModel model, int position)
		{
			Model    = model;
			Position = position;
		}

		public SemanticModel Model { get; }

		public int Position { get; }
	}

	sealed class ScopedKeyComparer : System.Collections.Generic.IEqualityComparer<(ITypeSymbol, INamedTypeSymbol?)>
	{
		public bool Equals((ITypeSymbol, INamedTypeSymbol?) x, (ITypeSymbol, INamedTypeSymbol?) y)
		{
			return SymbolEqualityComparer.Default.Equals(x.Item1, y.Item1) && SymbolEqualityComparer.Default.Equals(x.Item2, y.Item2);
		}

		public int GetHashCode((ITypeSymbol, INamedTypeSymbol?) key)
		{
			return unchecked(SymbolEqualityComparer.Default.GetHashCode(key.Item1) * 397 ^ (key.Item2 is null ? 0 : SymbolEqualityComparer.Default.GetHashCode(key.Item2)));
		}
	}
}
