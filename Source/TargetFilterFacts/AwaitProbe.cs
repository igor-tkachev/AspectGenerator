using System.Collections.Generic;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspectGenerator.TargetFilterFacts
{
	/// <summary>
	/// Asks the compiler whether the value of a call can be awaited, by binding an <c>await</c> speculatively.
	/// <list type="number">
	/// <item>A type no <c>GetAwaiter</c> member of which exists anywhere in its hierarchy, and <c>Task</c> and
	/// <c>ValueTask</c>, are answered without binding.</item>
	/// <item>A type the host can write (nothing in it anonymous, file-local, private or protected) is rendered with
	/// <c>global::</c> names and bound once in a forked compilation the context keeps; the answer is cached per type
	/// symbol, so identical calls cost one dictionary lookup. The type parameters it mentions and their constraints
	/// (<c>allows ref struct</c> included) become the probe's own. If the answer is no, and the hierarchy has a private
	/// or protected <c>GetAwaiter</c> that only its own or a derived type sees, the code that awaits decides (3).</item>
	/// <item>A type that cannot be written from an unrelated place is bound where it is written: for a call,
	/// <c>await (the invocation as written)</c> at its own position (not in a field or property initializer, a
	/// constructor initializer, or behind <c>?.</c>); for a definition in source, <c>await default(the return type as
	/// written)</c> inside its own body, or, for a method without one, inside another method of the same type that has
	/// no type parameters. The answer is cached per type and containing type of that code.</item>
	/// </list>
	/// Known limits: an anonymous or file-local type returned by a call in an initializer or behind <c>?.</c>, or by a
	/// bodiless method of a type whose methods are all generic or bodiless, is not awaitable here. An extension
	/// <c>GetAwaiter</c> does not count: it depends on the usings of the call site.
	/// </summary>
	static class AwaitProbe
	{
		/// <summary>What is known of a type wherever it is awaited.</summary>
		internal enum Answer
		{
			Yes,
			No,

			/// <summary>Not answered by the host: the code that awaits it decides (accessibility of what it names).</summary>
			NeedsContext,
		}

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

			if (!context.AwaitableCache.TryGetValue(type, out var answer))
				answer = context.AwaitableCache.GetOrAdd(type, t => Classify(t, context));

			if (answer != Answer.NeedsContext)
				return answer == Answer.Yes;

			// The type cannot be named from an unrelated place, or something it needs is not public: ask the code that
			// awaits it, once per containing type of that code (what that type sees is what its members see).
			INamedTypeSymbol? scope = null;
			var invocation          = callSite as InvocationExpressionSyntax;
			var atSite              = invocation is not null && context.Compilation.ContainsSyntaxTree(invocation.SyntaxTree) && IsInBody(invocation);

			if (atSite)
				scope = context.GetModel(invocation!.SyntaxTree).GetEnclosingSymbol(invocation.SpanStart)?.ContainingType;
			else if (isDefinition || SymbolEqualityComparer.Default.Equals(method, method.OriginalDefinition))
				scope = method.ContainingType;

			// A call of a method nothing constructs has its definition's return type.
			var useDeclaration = !atSite && (isDefinition || SymbolEqualityComparer.Default.Equals(method, method.OriginalDefinition));

			if (!atSite && !useDeclaration)
				return false;

			return context.ScopedAwaitableCache.GetOrAdd(
				(type, scope),
				_ => (atSite
					? Speculate(context, invocation!.SyntaxTree, invocation.SpanStart, "(" + invocation + ")")
					: TryDeclared(method.OriginalDefinition, context)) ?? false);
		}

		// The answer the host gives for a type that can be written from anywhere; otherwise what the host cannot decide.
		static Answer Classify(ITypeSymbol type, FactContext context)
		{
			if (!HasGetAwaiter(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), restricted: false))
				return Answer.No;

			if (!IsRenderable(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)))
				return Answer.NeedsContext;

			if (Render(type, context))
				return Answer.Yes;

			// The host sees what is public (and what its own assembly shares); a GetAwaiter that is private or protected is
			// visible only inside its own type or a derived one.
			return HasGetAwaiter(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), restricted: true) ? Answer.NeedsContext : Answer.No;
		}

		// A type the host can write: nothing in it is anonymous, file-local, or private or protected (where the host,
		// an unrelated top-level class, would not see it).
		static bool IsRenderable(ITypeSymbol type, HashSet<ITypeSymbol> visited)
		{
			if (!visited.Add(type))
				return true;

			switch (type)
			{
				case ITypeParameterSymbol parameter:
					return parameter.ConstraintTypes.All(c => IsRenderable(c, visited));

				case IArrayTypeSymbol array:
					return IsRenderable(array.ElementType, visited);

				case INamedTypeSymbol named:
					if (named.IsAnonymousType || named.IsFileLocal)
						return false;

					for (var outer = named; outer is not null; outer = outer.ContainingType)
						if (outer.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal || outer.IsFileLocal)
							return false;

					return named.TypeArguments.All(a => IsRenderable(a, visited));

				default:
					return false;
			}
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
			return Ask(context, context.GetModel(tree), position, "async void __AspectGenerator_Probe_8f3c1b6e5d7a4e2f9a0b() { await " + expression + "; }");
		}

		static bool? Ask(FactContext context, SemanticModel model, int position, string statementText)
		{
			context.CountProbe();

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
			// A partial method is declared in two places: the definition and the implementation.
			var references = method.DeclaringSyntaxReferences
				.Concat(method.PartialImplementationPart?.DeclaringSyntaxReferences ?? System.Collections.Immutable.ImmutableArray<SyntaxReference>.Empty)
				.Concat(method.PartialDefinitionPart?.DeclaringSyntaxReferences ?? System.Collections.Immutable.ImmutableArray<SyntaxReference>.Empty);

			var declarations = references
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

			// No body: the return type is read in a method of the same type that adds no names to the scope (the type
			// parameters of a generic method would shadow the type the return type names). The method's own type
			// parameters are not in that scope, so a return type that names one cannot be asked there.
			var parameters = new HashSet<string>(method.TypeParameters.Select(static p => p.Name));

			if (returnType.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(i => parameters.Contains(i.Identifier.ValueText)))
				return null;

			if (declarations[0].Parent is TypeDeclarationSyntax owner)
				foreach (var other in owner.Members.OfType<MethodDeclarationSyntax>())
					if (other.TypeParameterList is null && GetBodyPosition(other) is { } position)
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
		// With 'restricted', only a GetAwaiter that is private or protected counts.
		static bool HasGetAwaiter(ITypeSymbol type, HashSet<ITypeSymbol> visited, bool restricted)
		{
			if (!visited.Add(type))
				return false;

			bool Counts(ISymbol member)
			{
				return !restricted || member.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal;
			}

			switch (type)
			{
				case ITypeParameterSymbol parameter:
					return parameter.ConstraintTypes.Any(c => HasGetAwaiter(c, visited, restricted));

				case INamedTypeSymbol named when named.TypeKind == TypeKind.Interface:
					return named.GetMembers("GetAwaiter").Any(Counts) || named.AllInterfaces.Any(i => i.GetMembers("GetAwaiter").Any(Counts));

				case INamedTypeSymbol named:
					for (var current = named; current is not null; current = current.BaseType)
						if (current.GetMembers("GetAwaiter").Any(Counts))
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

			return Ask(context, host.Model, host.Position, text.ToString()) ?? false;
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
