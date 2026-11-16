using System.Collections.Generic;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspectGenerator.TargetFilterFacts
{
	/// <summary>
	/// Asks the compiler whether the value of a call can be awaited, binding an <c>await</c> speculatively where the type
	/// is written, so that scope, accessibility and friend assemblies are the compiler's own:
	/// <list type="number">
	/// <item>for a call: <c>await (the invocation as written)</c> at the invocation's own position in its own tree;</item>
	/// <item>for a definition in source: <c>await default(the return type as written)</c> inside the declaring method (or,
	/// for a method without a body, inside another method of the same type, in the same file's scope);</item>
	/// <item>otherwise (a definition from metadata, a call without syntax): a local function that awaits a parameter of the
	/// type rendered with <c>global::</c>-qualified names, bound in one forked compilation the context keeps; the type
	/// parameters it mentions and their constraints become the local function's own.</item>
	/// </list>
	/// An extension <c>GetAwaiter</c> does not count: it depends on the usings of the call site. A type with no
	/// <c>GetAwaiter</c> member anywhere in its hierarchy, and <c>Task</c> and <c>ValueTask</c>, are answered without binding.
	/// A call of a constructed method that has no usable syntax (a field initializer, a record made without a site) is
	/// answered from the rendered, constructed return type: a file-local, private or anonymous type there is not found.
	/// </summary>
	static class AwaitProbe
	{
		public static bool IsAwaitable(IMethodSymbol method, bool isDefinition, SyntaxNode? callSite, FactContext context)
		{
			var type = method.ReturnType;

			if (type is not (INamedTypeSymbol or ITypeParameterSymbol))
				return false;

			if (type is INamedTypeSymbol named &&
				named.OriginalDefinition.Name is "Task" or "ValueTask" &&
				named.OriginalDefinition.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks")
			{
				return true;
			}

			if (!HasGetAwaiter(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)))
				return false;

			if (callSite is InvocationExpressionSyntax invocation && context.Compilation.ContainsSyntaxTree(invocation.SyntaxTree) && IsInBody(invocation))
			{
				var atSite = Speculate(context, invocation.SyntaxTree, invocation.SpanStart, "(" + invocation.ToString() + ")");

				if (atSite is { } siteResult)
					return siteResult;
			}

			// A call of a method nothing constructs has its definition's return type.
			if ((isDefinition || SymbolEqualityComparer.Default.Equals(method, method.OriginalDefinition)) && TryDeclared(method.OriginalDefinition, context) is { } declared)
				return declared;

			return context.AwaitableCache.GetOrAdd(type, t => Render(t, context));
		}

		// Statements are bound speculatively inside code that has a body: not in a field or property initializer, a
		// constructor initializer, a parameter default or an attribute, which have no statement scope to speculate in.
		static bool IsInBody(SyntaxNode node)
		{
			for (var current = node.Parent; current is not null; current = current.Parent)
			{
				switch (current)
				{
					case AnonymousFunctionExpressionSyntax:
					case LocalFunctionStatementSyntax:
					case AccessorDeclarationSyntax:
					case GlobalStatementSyntax:
					case ArrowExpressionClauseSyntax:
					case BlockSyntax:
						return true;

					case ConstructorInitializerSyntax:
					case FieldDeclarationSyntax:
					case PropertyDeclarationSyntax:
					case EventFieldDeclarationSyntax:
					case Microsoft.CodeAnalysis.CSharp.Syntax.ParameterSyntax:
					case AttributeSyntax:
					case BaseTypeDeclarationSyntax:
						return false;
				}
			}

			return false;
		}

		// What the compiler says of `await <expression>` at a position of a tree; null when it cannot be asked there.
		// The await sits in a local function of its own so that it parses and binds as an await wherever the position is.
		internal static bool? Speculate(FactContext context, SyntaxTree tree, int position, string expression)
		{
			return Ask(context.GetModel(tree), position, "async void __AspectGeneratorProbe() { await " + expression + "; }");
		}

		static bool? Ask(SemanticModel model, int position, string statementText)
		{
			var statement = SyntaxFactory.ParseStatement(statementText);

			if (statement.ContainsDiagnostics || !model.TryGetSpeculativeSemanticModel(position, statement, out var speculative) || speculative is null)
				return null;

			var await = statement.DescendantNodes().OfType<AwaitExpressionSyntax>().First();

			return IsAwaitInfo(speculative.GetAwaitExpressionInfo(await));
		}

		static bool IsAwaitInfo(AwaitExpressionInfo info)
		{
			return info.GetAwaiterMethod is { IsExtensionMethod: false } &&
				info.IsCompletedProperty is not null &&
				info.GetResultMethod is not null;
		}

		// A source method: the return type as the declaring code wrote it, bound where that code can see it.
		internal static bool? TryDeclared(IMethodSymbol method, FactContext context)
		{
			var declarations = method.DeclaringSyntaxReferences
				.Select(static r => r.GetSyntax())
				.OfType<MethodDeclarationSyntax>()
				.Where(d => context.Compilation.ContainsSyntaxTree(d.SyntaxTree))
				.ToList();

			if (declarations.Count == 0)
				return null;

			var returnType = declarations[0].ReturnType is RefTypeSyntax reference ? reference.Type : declarations[0].ReturnType;

			foreach (var declaration in declarations)
				if (GetBodyPosition(declaration) is { } position)
					return Speculate(context, declaration.SyntaxTree, position, "(default(" + returnType + "))");

			// No body: another method of the same type shares the type's and the file's scope; the method's own type
			// parameters are not in that scope, so a return type that names one cannot be asked there.
			var declaring  = declarations[0];
			var parameters = new HashSet<string>(method.TypeParameters.Select(static p => p.Name));

			if (returnType.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(i => parameters.Contains(i.Identifier.ValueText)))
				return null;

			if (declaring.Parent is TypeDeclarationSyntax owner)
				foreach (var other in owner.Members.OfType<MethodDeclarationSyntax>())
					if (GetBodyPosition(other) is { } position)
						return Speculate(context, other.SyntaxTree, position, "(default(" + returnType + "))");

			return null;
		}

		static int? GetBodyPosition(MethodDeclarationSyntax declaration)
		{
			if (declaration.Body is { } body)
				return body.OpenBraceToken.Span.End;

			if (declaration.ExpressionBody is { } expression)
				return expression.Expression.SpanStart;

			return null;
		}

		// A necessary condition for the compiler to find an instance GetAwaiter: some member of that name in the type's
		// hierarchy (a type parameter: in the hierarchy of a constraint). Only a cycle of constraints ends the walk.
		static bool HasGetAwaiter(ITypeSymbol type, HashSet<ITypeSymbol> visited)
		{
			if (!visited.Add(type))
				return false;

			switch (type)
			{
				case ITypeParameterSymbol parameter:
					return parameter.ConstraintTypes.Any(c => HasGetAwaiter(c, visited));

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

		// ---- Rendering: for types that have no syntax to read ----

		internal static bool Render(ITypeSymbol type, FactContext context)
		{
			if (context.ProbeHost is not { } host || ContainsAnonymous(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)))
				return false;

			var parameters = new List<ITypeParameterSymbol>();

			Collect(type, parameters);

			var names = new Dictionary<ITypeParameterSymbol, string>(SymbolEqualityComparer.Default);

			foreach (var parameter in parameters)
				names[parameter] = "P" + names.Count;

			var text = new StringBuilder("static async void F");

			if (parameters.Count > 0)
				text.Append('<').Append(string.Join(", ", parameters.Select(p => names[p]))).Append('>');

			text.Append('(').Append(RenderType(type, names)).Append(" x)");

			foreach (var parameter in parameters)
				AppendConstraints(text, parameter, names);

			text.Append(" { await x; }");

			return Ask(host.Model, host.Position, text.ToString()) ?? false;
		}

		static bool ContainsAnonymous(ITypeSymbol type, HashSet<ITypeSymbol> visited)
		{
			if (!visited.Add(type))
				return false;

			switch (type)
			{
				case IArrayTypeSymbol array:
					return ContainsAnonymous(array.ElementType, visited);

				case INamedTypeSymbol named:
					return named.IsAnonymousType || named.TypeArguments.Any(a => ContainsAnonymous(a, visited)) || (named.ContainingType is { } outer && ContainsAnonymous(outer, visited));

				case ITypeParameterSymbol parameter:
					return parameter.ConstraintTypes.Any(c => ContainsAnonymous(c, visited));

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

			constraints.AddRange(parameter.ConstraintTypes.Select(c => RenderType(c, names)));

			if (parameter.HasConstructorConstraint)
				constraints.Add("new()");

			// The anti-constraint comes last; dropping it would make a ref struct a possible awaiter.
			if (parameter.AllowsRefLikeType)
				constraints.Add("allows ref struct");

			if (constraints.Count > 0)
				text.Append(" where ").Append(names[parameter]).Append(" : ").Append(string.Join(", ", constraints));
		}

		static string RenderType(ITypeSymbol type, Dictionary<ITypeParameterSymbol, string> names)
		{
			switch (type)
			{
				case ITypeParameterSymbol parameter:
					return names[parameter];

				case IArrayTypeSymbol array:
					return RenderType(array.ElementType, names) + "[" + new string(',', array.Rank - 1) + "]";

				case INamedTypeSymbol named:
				{
					if (named.IsTupleType && named.TupleUnderlyingType is { } underlying)
						named = underlying;

					var name = Escape(named.Name);

					if (named.TypeArguments.Length > 0)
						name += "<" + string.Join(", ", named.TypeArguments.Select(a => RenderType(a, names))) + ">";

					if (named.ContainingType is not null)
						return RenderType(named.ContainingType, names) + "." + name;

					return named.ContainingNamespace.IsGlobalNamespace
						? "global::" + name
						: named.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + name;
				}

				default:
					return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			}
		}

		static string Escape(string name)
		{
			return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
		}
	}
}
