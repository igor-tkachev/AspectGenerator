using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspectGenerator.TargetFilterFacts
{
	/// <summary>
	/// Asks the compiler whether a type can be awaited: a local function that awaits a parameter of the type is bound
	/// speculatively in the consumer's compilation (its references, its friend assemblies, its constraint rules), and the
	/// type is awaitable when the await binds to an instance <c>GetAwaiter()</c> with <c>IsCompleted</c> and
	/// <c>GetResult()</c>. An extension <c>GetAwaiter</c> does not count: it depends on the usings of the call site. The
	/// answer is cached per type symbol in the context, and every probe of a context is bound in the one compilation the
	/// context forks once (a fork per probe costs tens of milliseconds on a large compilation).
	/// <para>
	/// A type that nothing in its hierarchy gives a <c>GetAwaiter</c> member cannot be awaited, and is answered without
	/// binding; so is <c>Task</c> and <c>ValueTask</c>.
	/// </para>
	/// </summary>
	static class AwaitProbe
	{
		public static bool IsAwaitable(ITypeSymbol type, FactContext context)
		{
			if (context.AwaitableCache.TryGetValue(type, out var cached))
				return cached;

			var result = Compute(type, context);

			context.AwaitableCache[type] = result;

			return result;
		}

		static bool Compute(ITypeSymbol type, FactContext context)
		{
			if (type is not (INamedTypeSymbol or ITypeParameterSymbol or IArrayTypeSymbol))
				return false;

			if (type is INamedTypeSymbol named &&
				named.OriginalDefinition.Name is "Task" or "ValueTask" &&
				named.OriginalDefinition.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks")
			{
				return true;
			}

			if (!HasGetAwaiter(type, 0))
				return false;

			return Bind(type, context);
		}

		internal static bool PassesFilter(ITypeSymbol type)
		{
			return HasGetAwaiter(type, 0);
		}

		internal static bool Bind(ITypeSymbol type, FactContext context)
		{
			var parameters = new List<ITypeParameterSymbol>();

			Collect(type, parameters);

			var names = new Dictionary<ITypeParameterSymbol, string>(SymbolEqualityComparer.Default);

			foreach (var parameter in parameters)
				names[parameter] = "P" + names.Count;

			// A local function that awaits a parameter of the type, with the type parameters (and their constraints) the type
			// mentions as its own: bound speculatively inside the one host method of the context's forked compilation.
			var text = new StringBuilder("static async void F");

			if (parameters.Count > 0)
				text.Append('<').Append(string.Join(", ", parameters.Select(p => names[p]))).Append('>');

			text.Append('(').Append(Render(type, names)).Append(" x)");

			foreach (var parameter in parameters)
				AppendConstraints(text, parameter, names);

			text.Append(" { await x; }");

			var host = context.ProbeHost;

			if (host is null)
				return false;

			var statement = SyntaxFactory.ParseStatement(text.ToString());

			if (statement.ContainsDiagnostics || !host.Model.TryGetSpeculativeSemanticModel(host.Position, statement, out var model) || model is null)
				return false;

			var await = statement.DescendantNodes().OfType<AwaitExpressionSyntax>().Single();
			var info  = model.GetAwaitExpressionInfo(await);

			return info.GetAwaiterMethod is { IsExtensionMethod: false } &&
				info.IsCompletedProperty is not null &&
				info.GetResultMethod is not null;
		}

		// A necessary condition for the compiler to find an instance GetAwaiter: some member of that name in the type's
		// hierarchy (a type parameter: in the hierarchy of a constraint).
		static bool HasGetAwaiter(ITypeSymbol type, int depth)
		{
			if (depth > 8)
				return false;

			switch (type)
			{
				case ITypeParameterSymbol parameter:
					return parameter.ConstraintTypes.Any(c => HasGetAwaiter(c, depth + 1));

				case INamedTypeSymbol named when named.TypeKind == TypeKind.Interface:
					return named.GetMembers("GetAwaiter").Length > 0 || named.AllInterfaces.Any(static i => i.GetMembers("GetAwaiter").Length > 0);

				case INamedTypeSymbol named:
					for (var current = named; current is not null; current = current.BaseType)
						if (current.GetMembers("GetAwaiter").Length > 0)
							return true;

					return false;

				default:
					return false;
			}
		}

		// The type parameters a type mentions, and those their constraints mention, in order of discovery.
		static void Collect(ITypeSymbol type, List<ITypeParameterSymbol> parameters)
		{
			switch (type)
			{
				case ITypeParameterSymbol parameter:
					if (parameters.Contains(parameter, SymbolEqualityComparer.Default))
						return;

					parameters.Add(parameter);

					foreach (var constraint in parameter.ConstraintTypes)
						Collect(constraint, parameters);

					break;

				case IArrayTypeSymbol array:
					Collect(array.ElementType, parameters);
					break;

				case INamedTypeSymbol named:
					if (named.ContainingType is not null)
						Collect(named.ContainingType, parameters);

					foreach (var argument in named.TypeArguments)
						Collect(argument, parameters);

					break;
			}
		}

		static void AppendConstraints(StringBuilder text, ITypeParameterSymbol parameter, Dictionary<ITypeParameterSymbol, string> names)
		{
			var constraints = new List<string>();

			if (parameter.HasReferenceTypeConstraint)
				constraints.Add("class");
			else if (parameter.HasUnmanagedTypeConstraint)
				constraints.Add("unmanaged");
			else if (parameter.HasValueTypeConstraint)
				constraints.Add("struct");
			else if (parameter.HasNotNullConstraint)
				constraints.Add("notnull");

			constraints.AddRange(parameter.ConstraintTypes.Select(c => Render(c, names)));

			if (parameter.HasConstructorConstraint)
				constraints.Add("new()");

			if (constraints.Count > 0)
				text.Append(" where ").Append(names[parameter]).Append(" : ").Append(string.Join(", ", constraints));
		}

		static string Render(ITypeSymbol type, Dictionary<ITypeParameterSymbol, string> names)
		{
			switch (type)
			{
				case ITypeParameterSymbol parameter:
					return names[parameter];

				case IArrayTypeSymbol array:
					return Render(array.ElementType, names) + "[" + new string(',', array.Rank - 1) + "]";

				case INamedTypeSymbol named:
				{
					if (named.IsTupleType && named.TupleUnderlyingType is { } underlying)
						named = underlying;

					var name = named.Name;

					if (named.TypeArguments.Length > 0)
						name += "<" + string.Join(", ", named.TypeArguments.Select(a => Render(a, names))) + ">";

					if (named.ContainingType is not null)
						return Render(named.ContainingType, names) + "." + name;

					return named.ContainingNamespace.IsGlobalNamespace
						? "global::" + name
						: "global::" + named.ContainingNamespace.ToDisplayString() + "." + name;
				}

				default:
					return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			}
		}
	}
}
