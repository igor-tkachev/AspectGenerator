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
		readonly System.Collections.Generic.Dictionary<ITypeSymbol, bool> _awaitable = new(SymbolEqualityComparer.Default);

		public FactContext(Compilation compilation, IAssemblySymbol? projectAssembly = null)
		{
			Compilation     = compilation;
			ProjectAssembly = projectAssembly ?? compilation.Assembly;
		}

		public Compilation Compilation { get; }

		/// <summary>
		/// The assembly <c>project</c> means: the compilation's own assembly, or, when a library's own filters are
		/// evaluated, the library's assembly.
		/// </summary>
		public IAssemblySymbol ProjectAssembly { get; }

		bool       _hostMade;
		ProbeHost? _host;

		/// <summary>
		/// The one forked compilation every <see cref="AwaitProbe"/> of this context is bound in: the compilation plus a
		/// tree with an empty async method to bind statements inside, speculatively. Made on first use; null when the
		/// compilation is not a C# one.
		/// </summary>
		internal ProbeHost? ProbeHost
		{
			get
			{
				if (!_hostMade)
				{
					_hostMade = true;

					if (Compilation is CSharpCompilation compilation)
					{
						var options = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions ?? CSharpParseOptions.Default;
						var tree    = CSharpSyntaxTree.ParseText("static class __AspectGeneratorAwaitProbe { static async void Host() { int position = 0; } }", options);
						var model   = compilation.AddSyntaxTrees(tree).GetSemanticModel(tree);
						var body    = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single().Body!;

						_host = new ProbeHost(model, body.Statements[0].SpanStart);
					}
				}

				return _host;
			}
		}

		/// <summary>The answers of <see cref="AwaitProbe"/>, one per type symbol.</summary>
		internal System.Collections.Generic.Dictionary<ITypeSymbol, bool> AwaitableCache
		{
			get { return _awaitable; }
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
}
