using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;

using AspectGenerator.TargetFilterFacts;

using Microsoft.CodeAnalysis;

namespace AspectGenerator.TargetFilterBinding
{
	/// <summary>
	/// The shapes of the types of one compilation, made once per type symbol: the same types come back in call after
	/// call. Nullable annotations and tuple element names do not tell two symbols apart here, as no pattern reads them.
	/// </summary>
	sealed class TypeShapes
	{
		/// <summary>How deep, and how wide, a type may be for the compiler's own hash code and equality to be called on it.</summary>
		const int MaxKeyDepth = 24;
		const int MaxKeyNodes = 256;

		readonly ConcurrentDictionary<ITypeSymbol, TypeShape>                        _types      = new(SymbolEqualityComparer.Default);
		readonly ConcurrentDictionary<ITypeSymbol, TypeShape>                        _deepTypes  = new(ByReference.Instance);
		readonly ConcurrentDictionary<INamespaceSymbol, ImmutableArray<NameSegment>> _namespaces = new(SymbolEqualityComparer.Default);

		/// <summary>
		/// The shape of a type. A type is nested as deep as its source nests it (type arguments, tuple elements, the
		/// parameters of a function pointer, outer types, array and pointer wrappers), so the shapes it is made of are
		/// made first, innermost first, from a stack of the types still to make and not by calls that follow it down.
		/// </summary>
		public TypeShape Get(ITypeSymbol type)
		{
			if (TryGetKnown(type, out var shape))
				return shape;

			var pending      = new Stack<ITypeSymbol>();
			var dependencies = new List<ITypeSymbol>();

			pending.Push(type);

			while (pending.Count > 0)
			{
				var current = pending.Peek();

				if (TryGetKnown(current, out _))
				{
					pending.Pop();
					continue;
				}

				dependencies.Clear();
				AddDependencies(current, dependencies);

				var waiting = false;

				foreach (var dependency in dependencies)
				{
					if (!TryGetKnown(dependency, out _))
					{
						pending.Push(dependency);
						waiting = true;
					}
				}

				if (waiting)
					continue;

				pending.Pop();

				// Everything it is made of is made: Create finds it by lookups.
				Cache(current).GetOrAdd(current, Create(current));
			}

			return Cache(type)[type];
		}

		sealed class ByReference : IEqualityComparer<ITypeSymbol>
		{
			public static readonly ByReference Instance = new();

			public bool Equals(ITypeSymbol? x, ITypeSymbol? y)
			{
				return ReferenceEquals(x, y);
			}

			public int GetHashCode(ITypeSymbol obj)
			{
				return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
			}
		}

		bool TryGetKnown(ITypeSymbol type, out TypeShape shape)
		{
			return Cache(type).TryGetValue(type, out shape!);
		}

		/// <summary>
		/// The compiler hashes and compares a constructed type by following its type arguments down by calls, so a type
		/// nested by the thousand is no key it can be asked about on a small stack. A type nested deeper than a pattern
		/// can be is kept by reference: the same shape is made again where the compiler gives another symbol for it.
		/// </summary>
		ConcurrentDictionary<ITypeSymbol, TypeShape> Cache(ITypeSymbol type)
		{
			return IsShallow(type) ? _types : _deepTypes;
		}

		static bool IsShallow(ITypeSymbol type)
		{
			var pending      = new Stack<(ITypeSymbol Type, int Depth)>();
			var dependencies = new List<ITypeSymbol>();
			var nodes        = 0;

			pending.Push((type, 0));

			while (pending.Count > 0)
			{
				var (current, depth) = pending.Pop();

				if (depth > MaxKeyDepth || ++nodes > MaxKeyNodes)
					return false;

				dependencies.Clear();
				AddDependencies(current, dependencies);

				foreach (var dependency in dependencies)
					pending.Push((dependency, depth + 1));
			}

			return true;
		}

		/// <summary>The types whose shapes <see cref="Create"/> reads.</summary>
		static void AddDependencies(ITypeSymbol type, List<ITypeSymbol> dependencies)
		{
			switch (type)
			{
				case IArrayTypeSymbol array:
					dependencies.Add(array.ElementType);
					break;

				case IPointerTypeSymbol pointer:
					dependencies.Add(pointer.PointedAtType);
					break;

				case IFunctionPointerTypeSymbol function:
					foreach (var parameter in function.Signature.Parameters)
						dependencies.Add(parameter.Type);

					dependencies.Add(function.Signature.ReturnType);
					break;

				case INamedTypeSymbol named:
					if (named.ContainingType is { } outer)
						dependencies.Add(outer);

					dependencies.AddRange(named.TypeArguments);

					if (named.IsTupleType)
						foreach (var element in named.TupleElements)
							dependencies.Add(element.Type);

					break;
			}
		}

		public NamedTypeShape GetNamed(INamedTypeSymbol type)
		{
			return (NamedTypeShape)Get(type);
		}

		public ImmutableArray<NameSegment> GetNamespace(INamespaceSymbol @namespace)
		{
			// The namespaces above the first one made are taken off one by one and put back from the outside, as nested
			// as the source nests them.
			var missing = new Stack<INamespaceSymbol>();
			var current = @namespace;
			var known   = ImmutableArray<NameSegment>.Empty;

			while (!current.IsGlobalNamespace)
			{
				if (_namespaces.TryGetValue(current, out known))
					break;

				missing.Push(current);
				current = current.ContainingNamespace;
			}

			var segments = current.IsGlobalNamespace ? ImmutableArray<NameSegment>.Empty : known;

			while (missing.Count > 0)
			{
				var next = missing.Pop();

				segments = _namespaces.GetOrAdd(next, segments.Add(new NameSegment(next.Name)));
			}

			return segments;
		}

		public NameSegment GetMethodSegment(IMethodSymbol method)
		{
			return new NameSegment(method.Name, GetAll(method.TypeArguments));
		}

		public MethodShape GetMethod(IMethodSymbol method)
		{
			return new MethodShape(GetNamed(method.ContainingType), GetMethodSegment(method));
		}

		ImmutableArray<TypeShape> GetAll(ImmutableArray<ITypeSymbol> types)
		{
			if (types.IsEmpty)
				return ImmutableArray<TypeShape>.Empty;

			var shapes = ImmutableArray.CreateBuilder<TypeShape>(types.Length);

			foreach (var type in types)
				shapes.Add(Get(type));

			return shapes.MoveToImmutable();
		}

		/// <summary>The shape of a type whose parts have their shapes made, which it finds by lookups; <see cref="Get"/> makes them first.</summary>
		TypeShape Create(ITypeSymbol type)
		{
			switch (type)
			{
				case IArrayTypeSymbol array:
					return new ArrayTypeShape(Get(array.ElementType), array.Rank);

				case IPointerTypeSymbol pointer:
					return new PointerTypeShape(Get(pointer.PointedAtType));

				case ITypeParameterSymbol parameter:
					return new TypeParameterShape(parameter.Name);

				case IFunctionPointerTypeSymbol pointer:
					return CreateFunctionPointer(pointer.Signature);

				case INamedTypeSymbol named:
					return CreateNamed(named);

				default:
					// `dynamic`, and whatever else has a name only.
					return new NamedTypeShape(ImmutableArray.Create(new NameSegment(type.ToDisplayString())), type.IsReferenceType);
			}
		}

		NamedTypeShape CreateNamed(INamedTypeSymbol type)
		{
			var own = new NameSegment(type.Name, GetAll(type.TypeArguments));

			// A nested type carries the segments of its outer type, constructed as the nested type sees it.
			var segments = type.ContainingType is { } outer
				? GetNamed(outer).Segments.Add(own)
				: GetNamespace(type.ContainingNamespace).Add(own);

			var underlying = type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && type.TypeArguments.Length == 1
				? Get(type.TypeArguments[0])
				: null;

			var elements = default(ImmutableArray<TypeShape>);

			if (type.IsTupleType)
			{
				var builder = ImmutableArray.CreateBuilder<TypeShape>(type.TupleElements.Length);

				foreach (var element in type.TupleElements)
					builder.Add(Get(element.Type));

				elements = builder.MoveToImmutable();
			}

			return new NamedTypeShape(segments, type.IsReferenceType, underlying, elements);
		}

		FunctionPointerTypeShape CreateFunctionPointer(IMethodSymbol signature)
		{
			var parameters = ImmutableArray.CreateBuilder<FunctionPointerParameterShape>(signature.Parameters.Length);

			foreach (var parameter in signature.Parameters)
			{
				parameters.Add(new FunctionPointerParameterShape(Get(parameter.Type), parameter.RefKind switch
				{
					RefKind.Ref                  => ParameterModifiers.Ref,
					RefKind.RefReadOnlyParameter => ParameterModifiers.RefReadOnly,
					RefKind.Out                  => ParameterModifiers.Out,
					RefKind.In                   => ParameterModifiers.In,
					_                            => ParameterModifiers.None,
				}));
			}

			var @return = new FunctionPointerParameterShape(Get(signature.ReturnType), signature.RefKind switch
			{
				RefKind.Ref         => ParameterModifiers.Ref,
				RefKind.RefReadOnly => ParameterModifiers.RefReadOnly,
				_                   => ParameterModifiers.None,
			});

			// C# writes the four conventions the metadata encodes by their short names, and any other, or several, as
			// the names of the CallConv types without that prefix.
			var conventions = signature.CallingConvention switch
			{
				SignatureCallingConvention.CDecl    => ImmutableArray.Create("Cdecl"),
				SignatureCallingConvention.StdCall  => ImmutableArray.Create("Stdcall"),
				SignatureCallingConvention.ThisCall => ImmutableArray.Create("Thiscall"),
				SignatureCallingConvention.FastCall => ImmutableArray.Create("Fastcall"),
				_                                   => ImmutableArray<string>.Empty,
			};

			foreach (var type in signature.UnmanagedCallingConventionTypes)
				conventions = conventions.Add(type.Name.StartsWith("CallConv", System.StringComparison.Ordinal) ? type.Name.Substring("CallConv".Length) : type.Name);

			return new FunctionPointerTypeShape(
				isManaged: signature.CallingConvention == SignatureCallingConvention.Default,
				conventions,
				parameters.MoveToImmutable(),
				@return);
		}
	}

	/// <summary>
	/// The facts of a method of a compilation, in the structure the patterns match. Every member is read from the fact
	/// record when a condition first asks for it and kept; the record itself refuses a fact outside the plan it was made
	/// for, so the plan has to cover what the filters read (<see cref="FilterBlock.Facts"/>).
	/// </summary>
	sealed class SymbolMethodFacts : IMethodFacts
	{
		readonly MethodFacts _facts;
		readonly FactContext _context;
		readonly TypeShapes  _shapes;

		public SymbolMethodFacts(MethodFacts facts, FactContext context, TypeShapes shapes)
		{
			_facts   = facts;
			_context = context;
			_shapes  = shapes;
		}

		public MethodFacts Record => _facts;

		public ImmutableArray<NameSegment> Namespace => _shapes.GetNamespace(_facts.Method.ContainingType.ContainingNamespace);

		NamedTypeShape? _containingType;

		public NamedTypeShape ContainingType => _containingType ??= _shapes.GetNamed(_facts.Method.ContainingType);

		NameSegment? _method;

		public NameSegment Method => _method ??= _shapes.GetMethodSegment(_facts.Method);

		TypeShape? _returnType;

		public TypeShape ReturnType => _returnType ??= _shapes.Get(_facts.ReturnType);

		public ReturnRef ReturnRef => _facts.ReturnRef;

		ImmutableArray<ParameterShape> _parameters;

		public ImmutableArray<ParameterShape> Parameters
		{
			get
			{
				if (_parameters.IsDefault)
				{
					var facts   = _facts.Parameters;
					var builder = ImmutableArray.CreateBuilder<ParameterShape>(facts.Length);

					foreach (var parameter in facts)
						builder.Add(new ParameterShape(_shapes.Get(parameter.Type), parameter.Modifiers));

					_parameters = builder.MoveToImmutable();
				}

				return _parameters;
			}
		}

		ImmutableArray<NamedTypeShape> _attributes;

		public ImmutableArray<NamedTypeShape> Attributes
		{
			get
			{
				if (_attributes.IsDefault)
					_attributes = Named(_facts.Attributes);

				return _attributes;
			}
		}

		ImmutableArray<NamedTypeShape> _inheritedTypes;

		public ImmutableArray<NamedTypeShape> InheritedTypes
		{
			get
			{
				if (_inheritedTypes.IsDefault)
					_inheritedTypes = Named(_facts.InheritedTypes);

				return _inheritedTypes;
			}
		}

		ImmutableArray<MethodShape> _implementedMembers;

		public ImmutableArray<MethodShape> ImplementedMembers
		{
			get
			{
				if (_implementedMembers.IsDefault)
					_implementedMembers = Methods(_facts.ImplementedMembers);

				return _implementedMembers;
			}
		}

		ImmutableArray<MethodShape> _overrideChain;

		public ImmutableArray<MethodShape> OverrideChain
		{
			get
			{
				if (_overrideChain.IsDefault)
					_overrideChain = Methods(_facts.OverrideChain);

				return _overrideChain;
			}
		}

		public Accessibility Accessibility => _facts.Accessibility;
		public Virtuality    Virtuality    => _facts.Virtuality;
		public bool          IsStatic      => _facts.IsStatic;
		public bool          IsExtern      => _facts.IsExtern;
		public bool          IsExtension   => _facts.IsExtension;
		public bool          IsGenerated   => _facts.IsGenerated;
		public bool          IsProject     => _facts.IsProject;
		public bool          IsAsync       => _facts.IsAsync;

		bool? _isAsyncDecidedByCall;

		public bool IsAsyncDecidedByCall => _isAsyncDecidedByCall ??= _facts.IsDefinition && AsyncVaries();

		/// <summary>
		/// Whether a call of this definition can answer <c>async</c> otherwise than the definition.
		/// <list type="bullet">
		/// <item>A type whose awaiter only some code may use is answered by the code that awaits it, call by call.</item>
		/// <item>A return type that names no type parameter is the same in every call, and so are <c>Task&lt;T&gt;</c> and
		/// <c>ValueTask&lt;T&gt;</c>, awaitable whatever <c>T</c> is.</item>
		/// <item>A return type that is a type parameter is whatever the call puts there. Its constraints promise nothing:
		/// a type that satisfies them can hide the awaiter they bring with a member that is none.</item>
		/// <item>Any other return type that names a type parameter is decided by the call when it has a
		/// <c>GetAwaiter</c> at all, whose result the type arguments may decide; without one no call is awaitable.</item>
		/// </list>
		/// </summary>
		bool AsyncVaries()
		{
			var type    = _facts.Method.ReturnType;
			var isAsync = _facts.IsAsync;

			if (_context.AwaitableCache.TryGetValue(type, out var answer) && answer == AwaitProbe.Answer.NeedsContext)
				return true;

			if (!MentionsTypeParameter(type, 0))
				return false;

			if (type is ITypeParameterSymbol)
				return true;

			if (isAsync &&
				type.OriginalDefinition is INamedTypeSymbol { Name: "Task" or "ValueTask" } named &&
				named.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks")
			{
				return false;
			}

			return HasGetAwaiter(type);
		}

		static bool MentionsTypeParameter(ITypeSymbol type, int depth)
		{
			// Deeper than any type is written: a type that mentions itself through its arguments is taken to mention one.
			if (depth > 64)
				return true;

			switch (type)
			{
				case ITypeParameterSymbol:
					return true;

				case IArrayTypeSymbol array:
					return MentionsTypeParameter(array.ElementType, depth + 1);

				case IPointerTypeSymbol pointer:
					return MentionsTypeParameter(pointer.PointedAtType, depth + 1);

				case INamedTypeSymbol named:
					for (var current = named; current is not null; current = current.ContainingType)
						foreach (var argument in current.TypeArguments)
							if (MentionsTypeParameter(argument, depth + 1))
								return true;

					return false;

				default:
					return false;
			}
		}

		static bool HasGetAwaiter(ITypeSymbol type)
		{
			for (var current = type; current is not null; current = current.BaseType)
				if (!current.GetMembers(WellKnownMemberNames.GetAwaiter).IsEmpty)
					return true;

			foreach (var @interface in type.AllInterfaces)
				if (!@interface.GetMembers(WellKnownMemberNames.GetAwaiter).IsEmpty)
					return true;

			return false;
		}

		ImmutableArray<NamedTypeShape> Named(ImmutableArray<INamedTypeSymbol> types)
		{
			var builder = ImmutableArray.CreateBuilder<NamedTypeShape>(types.Length);

			foreach (var type in types)
				builder.Add(_shapes.GetNamed(type));

			return builder.MoveToImmutable();
		}

		ImmutableArray<MethodShape> Methods(ImmutableArray<IMethodSymbol> methods)
		{
			var builder = ImmutableArray.CreateBuilder<MethodShape>(methods.Length);

			foreach (var method in methods)
				builder.Add(_shapes.GetMethod(method));

			return builder.MoveToImmutable();
		}
	}
}
