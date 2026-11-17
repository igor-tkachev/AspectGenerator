using System.Collections.Immutable;

using AspectGenerator.TargetFilterFacts;

using Microsoft.CodeAnalysis;

namespace AspectGenerator.TargetFilterBinding
{
	/// <summary>
	/// What the conditions of a target filter read of a method: one member per property of the filter language, in the
	/// structure the patterns match. A record describes either the definition of a method or one call of it (with the
	/// type arguments of the call); the conditions do not know which, the phase they are evaluated in does
	/// (<see cref="FilterTarget"/>).
	/// <para>
	/// The conditions read nothing else, so the same filter runs over the methods of a compilation
	/// (<see cref="SymbolMethodFacts"/>) and over records written by hand.
	/// </para>
	/// </summary>
	interface IMethodFacts
	{
		/// <summary><c>namespace:</c>: the segments of the containing namespace; none for the global namespace.</summary>
		ImmutableArray<NameSegment> Namespace { get; }

		/// <summary><c>type:</c>, and the head of the path <c>method:</c> matches.</summary>
		NamedTypeShape ContainingType { get; }

		/// <summary><c>method:</c>: the method's own segment, its name with its type arguments.</summary>
		NameSegment Method { get; }

		TypeShape ReturnType { get; }
		ReturnRef ReturnRef  { get; }

		/// <summary><c>params:</c>, <c>param:</c>: the parameters in order, the <c>this</c> of an extension method included.</summary>
		ImmutableArray<ParameterShape> Parameters { get; }

		/// <summary><c>attribute:</c>: the classes of the attributes on the method and on the types that contain it.</summary>
		ImmutableArray<NamedTypeShape> Attributes { get; }

		/// <summary><c>inherits:</c>: the containing type, its base classes and the interfaces it implements.</summary>
		ImmutableArray<NamedTypeShape> InheritedTypes { get; }

		/// <summary><c>implements:</c>: the interface members the method is or implements.</summary>
		ImmutableArray<MethodShape> ImplementedMembers { get; }

		/// <summary>The method, then the method it overrides, up to the one that introduced the slot.</summary>
		ImmutableArray<MethodShape> OverrideChain { get; }

		Accessibility Accessibility { get; }
		Virtuality    Virtuality    { get; }
		bool          IsStatic      { get; }
		bool          IsExtern      { get; }
		bool          IsExtension   { get; }
		bool          IsGenerated   { get; }

		/// <summary><c>project</c>: declared in the compilation whose filters are evaluated.</summary>
		bool IsProject { get; }

		/// <summary><c>async</c>: what the method returns can be awaited.</summary>
		bool IsAsync { get; }

		/// <summary>
		/// On the record of a definition: a call may answer <see cref="IsAsync"/> otherwise than the definition does,
		/// because what it returns depends on the type arguments of the call or on the code that awaits it. Then no
		/// answer about <c>async</c> holds for every call.
		/// </summary>
		bool IsAsyncDecidedByCall { get; }
	}
}
