using Microsoft.CodeAnalysis;

namespace AspectGenerator.TargetFilterFacts
{
	/// <summary>
	/// What a fact depends on besides the method itself.
	/// </summary>
	sealed class FactContext
	{
		public FactContext(IAssemblySymbol projectAssembly)
		{
			ProjectAssembly = projectAssembly;
		}

		/// <summary>
		/// The assembly <c>project</c> means: the compilation's own assembly, or, when a library's own filters are
		/// evaluated, the library's assembly.
		/// </summary>
		public IAssemblySymbol ProjectAssembly { get; }
	}
}
