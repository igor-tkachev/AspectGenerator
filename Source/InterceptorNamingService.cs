using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Microsoft.CodeAnalysis;

namespace AspectGenerator
{
	static class InterceptorNamingService
	{
		public static List<InterceptorInfo> Create(ImmutableArray<AnalyzedInvocation> aspectedMethods)
		{
			var nameCounter = 0;
			var nameSet     = new HashSet<string>();
			var result      = new List<InterceptorInfo>();

			string GetInterceptorName(string methodName)
			{
				return nameSet.Add(methodName) ? methodName : GetInterceptorName($"{methodName}_{++nameCounter}");
			}

			// Calls that need a generic interceptor share one per target and aspect list, whatever their type arguments:
			// a filter evaluated on a referenced method's constructed signature can select different aspects per call.
			foreach (var group in aspectedMethods.GroupBy(static m => m, InterceptorGroupComparer.Instance).OrderBy(static m => (m.Key.GenericShape ?? m.Key.Method).Name))
			{
				var method      = group.Key.GenericShape ?? group.Key.Method;
				var invocations = group.ToList();

				result.Add(new InterceptorInfo(
					method,
					invocations,
					GetInterceptorName(GetBaseName(method)),
					GetOrderedAttributes(invocations[0].Attributes),
					invocations[0].GenericShape is null ? [] : InterceptorShape.GetTypeParameters(method)));
			}

			return result;
		}

		public static string GetBaseName(IMethodSymbol method)
		{
			return $"{method.Name}_Interceptor";
		}

		static List<AttributeInfo> GetOrderedAttributes(List<AttributeInfo> attributes)
		{
			if (!attributes.Any(static a => a.AppliedAttributeData?.NamedArguments.Any(static na => na.Key == "Order") is true))
				return attributes;

			return
			(
				from a in attributes
				let o = a.AppliedAttributeData?.NamedArguments.Select(static na => (KeyValuePair<string,TypedConstant>?)na).FirstOrDefault(static na => na!.Value.Key == "Order")
				let n = o is null ? int.MaxValue : o.Value.Value.Value switch
				{
					string s => int.TryParse(s, out var n) ? n : null,
					int    n => (int?)n,
					_        => null
				}
				orderby n
				select a
			).ToList();
		}
	}

	sealed class InterceptorGroupComparer : IEqualityComparer<AnalyzedInvocation>
	{
		public static readonly InterceptorGroupComparer Instance = new();

		public bool Equals(AnalyzedInvocation? x, AnalyzedInvocation? y)
		{
			if (x is null || y is null)
				return ReferenceEquals(x, y);

			if (!SymbolEqualityComparer.Default.Equals(x.GenericShape ?? x.Method, y.GenericShape ?? y.Method))
				return false;

			return x.GenericShape is null || x.Attributes.SequenceEqual(y.Attributes);
		}

		public int GetHashCode(AnalyzedInvocation obj)
		{
			return SymbolEqualityComparer.Default.GetHashCode(obj.GenericShape ?? obj.Method);
		}
	}

	record InterceptorInfo(
		IMethodSymbol              Method,
		List<AnalyzedInvocation>   Invocations,
		string                     Name,
		List<AttributeInfo>        Attributes,
		List<ITypeParameterSymbol> TypeParameters);
}
