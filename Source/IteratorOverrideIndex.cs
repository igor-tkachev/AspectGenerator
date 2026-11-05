using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;

namespace AspectGenerator
{
	/// <summary>
	/// Which iterators a call can run, for AG0108. A non-virtual method runs itself. A virtual, abstract or interface
	/// method runs its own body (when it has one) or any override or implementation in this compilation, so each of them
	/// that is an iterator counts; overrides in other assemblies cannot be seen. The overrides and implementations of
	/// methods that return a sequence or enumerator type (the only ones that can be iterators) are indexed in one pass over
	/// the source types, on the first query that needs them, and kept for the rest of the generator run.
	/// </summary>
	sealed class IteratorOverrideIndex(Compilation compilation, GeneratorTraceReport trace)
	{
		Dictionary<IMethodSymbol,List<IMethodSymbol>>? _overrides;

		readonly Dictionary<IMethodSymbol,bool> _isIterator = new(SymbolEqualityComparer.Default);

		public List<IMethodSymbol> GetIteratorsTheCallCanRun(IMethodSymbol method)
		{
			var target = (method.ReducedFrom ?? method).OriginalDefinition;
			var result = new List<IMethodSymbol>();

			if (!IsSequenceType(target.ReturnType))
				return result;

			if (!target.IsAbstract && IsIterator(target))
				result.Add(target);

			var isVirtual = !target.IsStatic && !target.IsSealed &&
				(target.IsVirtual || target.IsAbstract || target.IsOverride || target.ContainingType.TypeKind == TypeKind.Interface);

			if (!isVirtual)
				return result;

			var overrides = GetOverrides();
			var visited   = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { target };
			var pending   = new Stack<IMethodSymbol>();

			pending.Push(target);

			// An implementation of an interface member can itself be overridden, so the walk follows both kinds of edges.
			while (pending.Count > 0)
			{
				if (!overrides.TryGetValue(pending.Pop(), out var next))
					continue;

				foreach (var candidate in next)
				{
					if (!visited.Add(candidate))
						continue;

					pending.Push(candidate);

					if (!candidate.IsAbstract && IsIterator(candidate))
						result.Add(candidate);
				}
			}

			return result;
		}

		bool IsIterator(IMethodSymbol method)
		{
			if (!_isIterator.TryGetValue(method, out var value))
				_isIterator.Add(method, value = AspectSymbols.IsIterator(method));

			return value;
		}

		Dictionary<IMethodSymbol,List<IMethodSymbol>> GetOverrides()
		{
			if (_overrides is not null)
				return _overrides;

			using var _ = trace.TimePhase("Index iterator overrides");

			var overrides = new Dictionary<IMethodSymbol,List<IMethodSymbol>>(SymbolEqualityComparer.Default);
			var types     = 0;

			foreach (var type in GetSourceTypes(compilation.Assembly.GlobalNamespace))
			{
				types++;

				foreach (var member in type.GetMembers())
				{
					if (member is not IMethodSymbol { IsStatic: false, IsOverride: true } method || !IsSequenceType(method.ReturnType))
						continue;

					for (var overridden = method.OverriddenMethod; overridden is not null; overridden = overridden.OverriddenMethod)
						Add(overridden.OriginalDefinition, method.OriginalDefinition);
				}

				if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface))
					continue;

				foreach (var face in type.AllInterfaces)
				{
					foreach (var member in face.GetMembers())
					{
						if (member is not IMethodSymbol { IsStatic: false } interfaceMethod || !IsSequenceType(interfaceMethod.ReturnType))
							continue;

						// The implementation may be declared in a base type that does not implement the interface itself
						// (class B : A, I with A.M), so it is indexed for every type that implements the interface.
						if (type.FindImplementationForInterfaceMember(interfaceMethod) is IMethodSymbol implementation)
							Add(interfaceMethod.OriginalDefinition, implementation.OriginalDefinition);
					}
				}
			}

			trace.SetCounter("Iterator override index types", types);
			trace.SetCounter("Iterator override index methods", overrides.Count);

			return _overrides = overrides;

			void Add(IMethodSymbol target, IMethodSymbol candidate)
			{
				if (!overrides.TryGetValue(target, out var list))
					overrides.Add(target, list = new List<IMethodSymbol>());

				if (!list.Contains(candidate, SymbolEqualityComparer.Default))
					list.Add(candidate);
			}
		}

		static bool IsSequenceType(ITypeSymbol type)
		{
			return type is INamedTypeSymbol
			{
				Name               : "IEnumerable" or "IEnumerator" or "IAsyncEnumerable" or "IAsyncEnumerator",
				ContainingNamespace: { Name: "Generic" or "Collections" }
			};
		}

		static IEnumerable<INamedTypeSymbol> GetSourceTypes(INamespaceSymbol ns)
		{
			foreach (var member in ns.GetMembers())
			{
				if (member is INamespaceSymbol child)
				{
					foreach (var type in GetSourceTypes(child))
						yield return type;
				}
				else if (member is INamedTypeSymbol type)
				{
					foreach (var nested in GetNested(type))
						yield return nested;
				}
			}
		}

		static IEnumerable<INamedTypeSymbol> GetNested(INamedTypeSymbol type)
		{
			yield return type;

			foreach (var nested in type.GetTypeMembers())
				foreach (var inner in GetNested(nested))
					yield return inner;
		}
	}
}
