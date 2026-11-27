using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;

namespace AspectGenerator.Tests.FilterV2Oracle
{
	// The oracle's own description of a method, derived from its symbol here and nowhere else: plain values, read the
	// way the reference text of the language describes each fact. Nothing in it comes from the product's fact records.

	/// <summary>A type as the oracle sees it.</summary>
	abstract class OType
	{
		/// <summary>The full text of the type, as a pointer's quoted pattern names it (<c>System.Int32*</c>).</summary>
		public abstract string Text { get; }

		public override string ToString()
		{
			return Text;
		}
	}

	/// <summary>One segment of a dotted name: a namespace, a type with its own type arguments, or a method.</summary>
	sealed class OSegment(string name, IReadOnlyList<OType> arguments)
	{
		public string              Name      { get; } = name;
		public IReadOnlyList<OType> Arguments { get; } = arguments;

		public string Text
		{
			get
			{
				return Arguments.Count == 0 ? Name : Name + "<" + string.Join(",", Arguments.Select(static a => a.Text)) + ">";
			}
		}
	}

	sealed class ONamed : OType
	{
		public ONamed(IReadOnlyList<OSegment> segments, bool isReference, OType? nullableOf, IReadOnlyList<OType>? tupleElements)
		{
			Segments      = segments;
			IsReference   = isReference;
			NullableOf    = nullableOf;
			TupleElements = tupleElements;
		}

		/// <summary>The namespace's segments, then the containing types, then the type.</summary>
		public IReadOnlyList<OSegment> Segments { get; }

		public bool IsReference { get; }

		/// <summary>The underlying type of <c>System.Nullable&lt;T&gt;</c>.</summary>
		public OType? NullableOf { get; }

		/// <summary>The elements of a tuple type.</summary>
		public IReadOnlyList<OType>? TupleElements { get; }

		public override string Text => string.Join(".", Segments.Select(static s => s.Text));

		/// <summary>The same type with the last segment renamed (an attribute class without its suffix).</summary>
		public ONamed WithLastName(string name)
		{
			var segments = Segments.ToList();

			segments[^1] = new OSegment(name, segments[^1].Arguments);

			return new ONamed(segments, IsReference, NullableOf, TupleElements);
		}
	}

	sealed class OArray(OType element, int rank) : OType
	{
		public OType Element { get; } = element;
		public int   Rank    { get; } = rank;

		public override string Text => Element.Text + "[" + new string(',', Rank - 1) + "]";
	}

	/// <summary>A type parameter, of the method's definition or of the code that calls it.</summary>
	sealed class OTypeParameter(string name) : OType
	{
		public string Name { get; } = name;

		public override string Text => Name;
	}

	sealed class OPointer(OType element) : OType
	{
		public OType Element { get; } = element;

		public override string Text => Element.Text + "*";
	}

	sealed class OFunctionPointer(string text, IReadOnlyList<OType> parameters, OType returns) : OType
	{
		public IReadOnlyList<OType> Parameters { get; } = parameters;
		public OType                Returns    { get; } = returns;

		public override string Text { get; } = text;
	}

	sealed class ODynamic : OType
	{
		public override string Text => "dynamic";
	}

	enum OKind { None, Ref, Out, In, RefReadonly }

	sealed class OParameter(OType type, OKind kind, bool isThis, bool isParams)
	{
		public OType Type     { get; } = type;
		public OKind Kind     { get; } = kind;
		public bool  IsThis   { get; } = isThis;
		public bool  IsParams { get; } = isParams;
	}

	/// <summary>A member a method is or implements: its type and its own segment.</summary>
	sealed class OMember(ONamed type, OSegment method)
	{
		public ONamed   Type   { get; } = type;
		public OSegment Method { get; } = method;

		public IReadOnlyList<OSegment> Path => [..Type.Segments, Method];
	}

	/// <summary>The facts of one call of a method (a definition seen from inside its own generic code is such a call too).</summary>
	sealed class OMethod
	{
		public required string                Display        { get; init; }
		public required IReadOnlyList<OSegment> Namespace    { get; init; }
		public required ONamed                ContainingType { get; init; }
		public required OSegment              Method         { get; init; }
		public required OType                 Returns        { get; init; }
		public required OKind                 ReturnKind     { get; init; }
		public required IReadOnlyList<OParameter> Parameters { get; init; }
		public required IReadOnlyList<ONamed> Attributes     { get; init; }
		public required IReadOnlyList<ONamed> Inherited      { get; init; }
		public required IReadOnlyList<OMember> Implemented   { get; init; }
		public required Accessibility         Accessibility  { get; init; }
		public required bool                  IsStatic       { get; init; }
		public required bool                  IsVirtual      { get; init; }
		public required bool                  IsOverride     { get; init; }
		public required bool                  IsAbstract     { get; init; }
		public required bool                  IsSealed       { get; init; }
		public required bool                  IsExtern       { get; init; }
		public required bool                  IsExtension    { get; init; }
		public required bool                  IsGenerated    { get; init; }
		public required bool                  IsProject      { get; init; }
		public required bool                  IsAsync        { get; init; }

		/// <summary>The path <c>method:</c> matches: namespace, containing types, method.</summary>
		public IReadOnlyList<OSegment> Path => [..ContainingType.Segments, Method];

		public override string ToString()
		{
			return Display;
		}
	}

	/// <summary>Reads the oracle's facts from a symbol.</summary>
	static class OracleFacts
	{
		/// <summary>The facts of a call that binds to <paramref name="symbol"/>, in a compilation whose project is <paramref name="project"/>.</summary>
		public static OMethod Read(IMethodSymbol symbol, IAssemblySymbol project)
		{
			// An extension method is judged in its static form, with the type arguments the call infers.
			var method = symbol.ReducedFrom is null ? symbol : symbol.GetConstructedReducedFrom() ?? symbol.ReducedFrom;

			return new OMethod
			{
				Display        = method.ToDisplayString(),
				Namespace      = NamespaceSegments(method.ContainingType.ContainingNamespace),
				ContainingType = Named(method.ContainingType),
				Method         = new OSegment(method.Name, [..method.TypeArguments.Select(Type)]),
				Returns        = Type(method.ReturnType),
				ReturnKind     = method.RefKind switch { RefKind.Ref => OKind.Ref, RefKind.RefReadOnly => OKind.RefReadonly, _ => OKind.None },
				Parameters     = [..method.Parameters.Select((p, i) => new OParameter(Type(p.Type), Kind(p.RefKind), i == 0 && method.IsExtensionMethod, p.IsParams))],
				Attributes     = Attributes(method),
				Inherited      = Inherited(method.ContainingType),
				Implemented    = Implemented(method),
				Accessibility  = method.DeclaredAccessibility,
				IsStatic       = method.IsStatic,
				IsVirtual      = method.IsVirtual,
				IsOverride     = method.IsOverride,
				IsAbstract     = method.IsAbstract,
				IsSealed       = method.IsSealed,
				IsExtern       = method.IsExtern || (method.MethodImplementationFlags & System.Reflection.MethodImplAttributes.InternalCall) != 0,
				IsExtension    = method.IsExtensionMethod,
				IsGenerated    = method.OriginalDefinition.IsImplicitlyDeclared || method.GetAttributes().Any(static a => FullName(a.AttributeClass) == "System.Runtime.CompilerServices.CompilerGeneratedAttribute"),
				IsProject      = SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, project),
				IsAsync        = Awaitable(method.ReturnType, project, 0),
			};
		}

		static OKind Kind(RefKind kind)
		{
			return kind switch
			{
				RefKind.Ref                  => OKind.Ref,
				RefKind.Out                  => OKind.Out,
				RefKind.In                   => OKind.In,
				RefKind.RefReadOnlyParameter => OKind.RefReadonly,
				_                            => OKind.None,
			};
		}

		static IReadOnlyList<OSegment> NamespaceSegments(INamespaceSymbol ns)
		{
			var names = new List<OSegment>();

			for (var n = ns; n is not null && !n.IsGlobalNamespace; n = n.ContainingNamespace)
				names.Insert(0, new OSegment(n.Name, []));

			return names;
		}

		public static OType Type(ITypeSymbol type)
		{
			switch (type)
			{
				case IArrayTypeSymbol array:
					return new OArray(Type(array.ElementType), array.Rank);

				case ITypeParameterSymbol parameter:
					return new OTypeParameter(parameter.Name);

				case IPointerTypeSymbol pointer:
					return new OPointer(Type(pointer.PointedAtType));

				case IFunctionPointerTypeSymbol function:
				{
					var signature = function.Signature;

					return new OFunctionPointer(function.ToDisplayString(), [..signature.Parameters.Select(p => Type(p.Type))], Type(signature.ReturnType));
				}

				case IDynamicTypeSymbol:
					return new ODynamic();

				case INamedTypeSymbol named:
					return Named(named);

				default:
					throw new NotSupportedException("A type the oracle does not read: " + type.ToDisplayString());
			}
		}

		public static ONamed Named(INamedTypeSymbol type)
		{
			var segments = new List<OSegment>();

			for (var t = type; t is not null; t = t.ContainingType)
			{
				var name = t.Name;
				var tick = name.IndexOf('`');

				if (tick >= 0)
					name = name[..tick];

				segments.Insert(0, new OSegment(name, [..t.TypeArguments.Select(Type)]));
			}

			segments.InsertRange(0, NamespaceSegments(type.ContainingNamespace));

			var nullableOf = type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T ? Type(type.TypeArguments[0]) : null;
			var tuple      = type.IsTupleType ? (IReadOnlyList<OType>)[..type.TupleElements.Select(e => Type(e.Type))] : null;

			return new ONamed(segments, type.IsReferenceType, nullableOf, tuple);
		}

		static string? FullName(INamedTypeSymbol? type)
		{
			return type is null ? null : Named(type.OriginalDefinition).Text;
		}

		static IReadOnlyList<ONamed> Attributes(IMethodSymbol method)
		{
			var result = new List<ONamed>();

			foreach (var attribute in method.GetAttributes())
				if (attribute.AttributeClass is { } c)
					result.Add(Named(c));

			for (var type = method.ContainingType; type is not null; type = type.ContainingType)
				foreach (var attribute in type.GetAttributes())
					if (attribute.AttributeClass is { } c)
						result.Add(Named(c));

			return result;
		}

		static IReadOnlyList<ONamed> Inherited(INamedTypeSymbol type)
		{
			var result = new List<ONamed>();

			for (var t = type; t is not null; t = t.BaseType)
				result.Add(Named(t));

			foreach (var i in type.AllInterfaces)
				result.Add(Named(i));

			return result;
		}

		/// <summary>
		/// The method itself, the methods it overrides, and the interface members each of them implements as its own
		/// declaring type maps the interface.
		/// </summary>
		static IReadOnlyList<OMember> Implemented(IMethodSymbol method)
		{
			var result = new List<OMember>();

			for (var m = method; m is not null; m = m.OverriddenMethod)
			{
				result.Add(Member(m));

				foreach (var i in m.ContainingType.AllInterfaces)
				{
					foreach (var member in i.GetMembers().OfType<IMethodSymbol>())
					{
						var implementation = m.ContainingType.FindImplementationForInterfaceMember(member) as IMethodSymbol;

						if (implementation is not null && SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, m.OriginalDefinition))
							result.Add(Member(member.IsGenericMethod && m.IsGenericMethod && m.TypeArguments.Length == member.TypeParameters.Length ? member.Construct([..m.TypeArguments]) : member));
					}
				}
			}

			return result;
		}

		static OMember Member(IMethodSymbol method)
		{
			return new OMember(Named(method.ContainingType), new OSegment(method.Name, [..method.TypeArguments.Select(Type)]));
		}

		// What the call returns can be awaited: Task, ValueTask, their generic forms, or a type with an accessible instance
		// GetAwaiter() whose result follows the awaiter pattern; a type parameter by its constraints.
		static bool Awaitable(ITypeSymbol type, IAssemblySymbol project, int depth)
		{
			if (depth > 8)
				return false;

			if (type is ITypeParameterSymbol parameter)
				return parameter.ConstraintTypes.Any(c => Awaitable(c, project, depth + 1));

			if (type is not INamedTypeSymbol named)
				return false;

			var name = FullName(named);

			if (name is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask" || name is not null && (name.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal) || name.StartsWith("System.Threading.Tasks.ValueTask<", StringComparison.Ordinal)))
				return true;

			for (var t = named; t is not null; t = t.BaseType)
			{
				foreach (var getAwaiter in t.GetMembers("GetAwaiter").OfType<IMethodSymbol>())
				{
					if (getAwaiter.IsStatic || getAwaiter.Parameters.Length != 0 || getAwaiter.TypeParameters.Length != 0)
						continue;

					var accessible = getAwaiter.DeclaredAccessibility == Accessibility.Public
						|| getAwaiter.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal && SymbolEqualityComparer.Default.Equals(getAwaiter.ContainingAssembly, project);

					if (accessible && IsAwaiter(getAwaiter.ReturnType))
						return true;
				}
			}

			return false;
		}

		static bool IsAwaiter(ITypeSymbol awaiter)
		{
			var members = new List<ISymbol>();

			for (var t = awaiter; t is not null; t = t.BaseType)
				members.AddRange(t.GetMembers());

			var isCompleted = members.OfType<IPropertySymbol>().Any(static p => p.Name == "IsCompleted" && !p.IsStatic && p.GetMethod is not null && p.Type.SpecialType == SpecialType.System_Boolean);
			var getResult   = members.OfType<IMethodSymbol>().Any(static m => m.Name == "GetResult" && !m.IsStatic && m.Parameters.Length == 0);
			var notifies    = awaiter.AllInterfaces.Any(static i => FullName(i) == "System.Runtime.CompilerServices.INotifyCompletion");

			return isCompleted && getResult && notifies;
		}
	}
}
