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

			foreach (var group in aspectedMethods.GroupBy(static m => m.Method, SymbolEqualityComparer.Default).OrderBy(static m => m.Key!.Name))
			{
				var method      = (IMethodSymbol)group.Key!;
				var invocations = group.ToList();

				result.Add(new InterceptorInfo(
					method,
					invocations,
					GetInterceptorName(GetBaseName(method)),
					GetOrderedAttributes(invocations[0].Attributes)));
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

	record InterceptorInfo(
		IMethodSymbol            Method,
		List<AnalyzedInvocation> Invocations,
		string                   Name,
		List<AttributeInfo>      Attributes);
}
