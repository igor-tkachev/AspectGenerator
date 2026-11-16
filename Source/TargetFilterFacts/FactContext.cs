using Microsoft.CodeAnalysis;

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

		/// <summary>The answers of <see cref="AwaitProbe"/>, one per type symbol.</summary>
		internal System.Collections.Generic.Dictionary<ITypeSymbol, bool> AwaitableCache
		{
			get { return _awaitable; }
		}
	}
}
