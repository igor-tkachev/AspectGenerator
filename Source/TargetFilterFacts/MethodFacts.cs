using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;

using Microsoft.CodeAnalysis;

namespace AspectGenerator.TargetFilterFacts
{
	/// <summary>
	/// One parameter of a method as a <c>params:</c> / <c>param:</c> pattern sees it.
	/// </summary>
	readonly struct ParameterFact
	{
		public ParameterFact(ITypeSymbol type, ParameterModifiers modifiers)
		{
			Type      = type;
			Modifiers = modifiers;
		}

		public ITypeSymbol        Type      { get; }
		public ParameterModifiers Modifiers { get; }
	}

	/// <summary>
	/// What a target filter can know about a method, computed on demand and only for the properties the plan names.
	/// <para>
	/// A record is either the <b>definition</b> of a method (<c>List&lt;T&gt;.Add</c>) or a <b>call</b> of it
	/// (<c>List&lt;int&gt;.Add</c>, <c>Enumerable.Any&lt;int&gt;</c>). The facts that cannot depend on a type argument
	/// (the words, the attributes, the origin) are read once, from the definition; a call record shares its definition
	/// record, and answers the facts that name types (the containing type, the method path, the return type, the
	/// parameters, the hierarchy) from the constructed symbols. An extension method called as <c>x.Any()</c> is
	/// described in its static form, constructed from the call, so <c>x.Any()</c> and <c>Enumerable.Any(x)</c> have
	/// the same record.
	/// </para>
	/// <para>
	/// Every fact reads the same from a method in source, in an implementation assembly and in a reference assembly,
	/// except where it is documented here. Members the metadata does not carry (private members, explicit interface
	/// implementations) never reach the record: they are not call targets.
	/// </para>
	/// </summary>
	sealed class MethodFacts
	{
		readonly FactPlan     _plan;
		readonly FactContext  _context;
		readonly MethodFacts? _definition;

		MethodFacts(IMethodSymbol method, FactPlan plan, FactContext context, MethodFacts? definition)
		{
			Method      = method;
			_plan       = plan;
			_context    = context;
			_definition = definition;
		}

		/// <summary>
		/// The record of a call: the definition of the method and the constructed method the call binds to are the same
		/// for a method that nothing constructs, and then the record is the definition record.
		/// </summary>
		public static MethodFacts Create(IMethodSymbol method, FactPlan plan, FactContext context)
		{
			var normalized = Normalize(method);
			var definition = CreateDefinition(normalized, plan, context);

			return SymbolEqualityComparer.Default.Equals(normalized, normalized.OriginalDefinition)
				? definition
				: definition.ForCall(normalized);
		}

		/// <summary>
		/// The record of the definition of a method, whatever symbol of it is given.
		/// </summary>
		public static MethodFacts CreateDefinition(IMethodSymbol method, FactPlan plan, FactContext context)
		{
			return new MethodFacts(Normalize(method).OriginalDefinition, plan, context, null);
		}

		/// <summary>
		/// The record of one call of this definition. <paramref name="call"/> is the constructed method a call binds to.
		/// </summary>
		public MethodFacts ForCall(IMethodSymbol call)
		{
			if (_definition is not null)
				return _definition.ForCall(call);

			var normalized = Normalize(call);

			if (!SymbolEqualityComparer.Default.Equals(normalized.OriginalDefinition, Method))
				throw new ArgumentException("The call is not a call of this definition.", nameof(call));

			return new MethodFacts(normalized, _plan, _context, this);
		}

		// A reduced extension method (x.Any()) is described in its static form, with the type arguments the call infers.
		static IMethodSymbol Normalize(IMethodSymbol method)
		{
			return method.ReducedFrom is null ? method : method.GetConstructedReducedFrom() ?? method.ReducedFrom;
		}

		/// <summary>
		/// The method this record describes: the unreduced definition, or the unreduced constructed method of a call.
		/// </summary>
		public IMethodSymbol Method { get; }

		public bool IsDefinition
		{
			get { return _definition is null; }
		}

		public MethodFacts Definition
		{
			get { return _definition ?? this; }
		}

		int _computed;

		T Computed<T>(T value)
		{
			_computed++;

			return value;
		}

		/// <summary>The number of facts this record has computed so far (each is computed once); tests pin that nothing is eager.</summary>
		internal int ComputedFacts
		{
			get { return _computed; }
		}

		void Require(FactProperty property)
		{
			if (!_plan.Contains(property))
				throw new InvalidOperationException($"The fact '{property}' is not in the plan this record was made for.");
		}

		void RequireAny(FactProperty properties)
		{
			if (!_plan.ContainsAny(properties))
				throw new InvalidOperationException($"None of the facts '{properties}' is in the plan this record was made for.");
		}

		// ---- Location and method path ----

		string? _namespace;

		/// <summary><c>namespace:</c>: the containing namespace, empty for the global one.</summary>
		public string Namespace
		{
			get
			{
				Require(FactProperty.Location);

				return _namespace ??= Computed(FactNames.FormatNamespace(Method.ContainingType.ContainingNamespace));
			}
		}

		string? _typeName;

		/// <summary>
		/// <c>type:</c>: the containing type with its namespace and outer types, type arguments included
		/// (<c>System.Collections.Generic.List&lt;System.Int32&gt;</c>); a type parameter is its name.
		/// </summary>
		public string TypeName
		{
			get
			{
				Require(FactProperty.Location);

				return _typeName ??= Computed(FactNames.FormatType(Method.ContainingType));
			}
		}

		string? _methodPath;

		/// <summary>
		/// <c>method:</c>: <c>Type.Method</c> with the method's type arguments when it has any
		/// (<c>System.Linq.Enumerable.Any&lt;System.Int32&gt;</c>).
		/// </summary>
		public string MethodPath
		{
			get
			{
				Require(FactProperty.Method);

				return _methodPath ??= Computed(FactNames.FormatType(Method.ContainingType) + "." + FactNames.FormatMethodName(Method));
			}
		}

		// ---- Signature ----

		/// <summary><c>returns:</c>: the return type.</summary>
		public ITypeSymbol ReturnType
		{
			get
			{
				Require(FactProperty.Returns);

				return Method.ReturnType;
			}
		}

		/// <summary><c>returns:</c>: whether the method returns by reference, and how.</summary>
		public ReturnRef ReturnRef
		{
			get
			{
				Require(FactProperty.Returns);

				return Method.RefKind switch
				{
					RefKind.Ref         => ReturnRef.Ref,
					RefKind.RefReadOnly => ReturnRef.RefReadOnly,
					_                   => ReturnRef.None,
				};
			}
		}

		ImmutableArray<ParameterFact> _parameters;

		/// <summary><c>params:</c> and <c>param:</c>: the parameters in order, the <c>this</c> of an extension method included.</summary>
		public ImmutableArray<ParameterFact> Parameters
		{
			get
			{
				RequireAny(FactProperty.Params | FactProperty.Param);

				if (_parameters.IsDefault)
				{
					var builder = ImmutableArray.CreateBuilder<ParameterFact>(Method.Parameters.Length);

					foreach (var parameter in Method.Parameters)
						builder.Add(new ParameterFact(parameter.Type, GetModifiers(Method, parameter)));

					_parameters = Computed(builder.MoveToImmutable());
				}

				return _parameters;
			}
		}

		static ParameterModifiers GetModifiers(IMethodSymbol method, IParameterSymbol parameter)
		{
			var modifiers = parameter.RefKind switch
			{
				RefKind.Ref                  => ParameterModifiers.Ref,
				RefKind.RefReadOnlyParameter => ParameterModifiers.RefReadOnly,
				RefKind.Out                  => ParameterModifiers.Out,
				RefKind.In                   => ParameterModifiers.In,
				_                            => ParameterModifiers.None,
			};

			if (parameter.IsParams)
				modifiers |= ParameterModifiers.Params;

			// The method is the unreduced static form: its first parameter of an extension method is the one written with 'this'.
			if (method.IsExtensionMethod && parameter.Ordinal == 0)
				modifiers |= ParameterModifiers.This;

			return modifiers;
		}

		// ---- Words: the same for every call of a definition ----

		/// <summary>
		/// The declared accessibility, as the assembly the method is read from declares it (a reference assembly can
		/// declare a runtime <c>protected internal</c> member as <c>protected</c>).
		/// </summary>
		public Accessibility Accessibility
		{
			get
			{
				Require(FactProperty.Accessibility);

				return Definition.Method.DeclaredAccessibility;
			}
		}

		public bool IsStatic
		{
			get
			{
				Require(FactProperty.Static);

				return Definition.Method.IsStatic;
			}
		}

		public bool IsExtern
		{
			get
			{
				Require(FactProperty.Extern);

				// A method with the runtime's internal-call implementation is extern in source (the modifier) but only carries
				// the implementation flag in metadata; reading the flag makes both forms agree.
				var method = Definition.Method;

				return method.IsExtern || (method.MethodImplementationFlags & MethodImplAttributes.InternalCall) != 0;
			}
		}

		public bool IsExtension
		{
			get
			{
				Require(FactProperty.Extension);

				return Definition.Method.IsExtensionMethod;
			}
		}

		/// <summary>
		/// <c>project</c>: the method is declared in the assembly of <see cref="FactContext.ProjectAssembly"/>.
		/// </summary>
		public bool IsProject
		{
			get
			{
				Require(FactProperty.Origin);

				return SymbolEqualityComparer.Default.Equals(Definition.Method.ContainingAssembly, _context.ProjectAssembly);
			}
		}

		/// <summary>
		/// The virtuality words as C# declares them. An override is not <see cref="Virtuality.Virtual"/>; a
		/// <c>sealed override</c> is <see cref="Virtuality.Override"/> and <see cref="Virtuality.Sealed"/>; an interface
		/// member without a body is <see cref="Virtuality.Abstract"/> and one with a default body
		/// <see cref="Virtuality.Virtual"/>; an <c>abstract override</c> is <see cref="Virtuality.Override"/> and
		/// <see cref="Virtuality.Abstract"/>.
		/// </summary>
		public Virtuality Virtuality
		{
			get
			{
				Require(FactProperty.Virtuality);

				var method    = Definition.Method;
				var virtuality = Virtuality.None;

				if (method.IsOverride)
					virtuality |= Virtuality.Override;
				else if (method.IsVirtual && !method.IsAbstract)
					virtuality |= Virtuality.Virtual;

				if (method.IsAbstract)
					virtuality |= Virtuality.Abstract;

				if (method.IsSealed)
					virtuality |= Virtuality.Sealed;

				return virtuality;
			}
		}

		bool? _isAsync;

		/// <summary>
		/// <c>async</c>: the return type of the call (so <c>Box&lt;Task&gt;.Get()</c> is async and <c>Box&lt;int&gt;.Get()</c>
		/// is not) is <c>Task</c>, <c>ValueTask</c>, their generic forms, or another awaitable type: one with an accessible
		/// instance <c>GetAwaiter()</c> whose result has <c>IsCompleted</c>, <c>GetResult()</c> and implements
		/// <c>INotifyCompletion</c>. A type parameter is awaitable when one of its constraints is. The <c>async</c>
		/// modifier itself is not recorded in metadata, so it is not what is read: an <c>async void</c> method and an
		/// <c>IAsyncEnumerable&lt;T&gt;</c> iterator are not <c>async</c> here, in source or in a referenced assembly.
		/// </summary>
		public bool IsAsync
		{
			get
			{
				Require(FactProperty.Async);

				return _isAsync ??= Computed(IsAwaitable(Method.ReturnType, _context.ProjectAssembly));
			}
		}

		static bool IsAwaitable(ITypeSymbol type, IAssemblySymbol project)
		{
			if (type is ITypeParameterSymbol typeParameter)
			{
				foreach (var constraint in typeParameter.ConstraintTypes)
					if (IsAwaitable(constraint, project))
						return true;

				return false;
			}

			if (type is not INamedTypeSymbol named)
				return false;

			if (named.OriginalDefinition.Name is "Task" or "ValueTask" &&
				named.OriginalDefinition.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks")
			{
				return true;
			}

			foreach (var candidate in InstanceMembers(named, "GetAwaiter"))
			{
				if (candidate is IMethodSymbol { IsStatic: false, Parameters.Length: 0, TypeParameters.Length: 0, ReturnType: INamedTypeSymbol awaiter } getAwaiter &&
					getAwaiter.ExplicitInterfaceImplementations.Length == 0 &&
					IsAccessible(getAwaiter, project) &&
					IsAwaiter(awaiter, project))
				{
					return true;
				}
			}

			return false;
		}

		// What the compiler requires of an awaiter: IsCompleted (bool), GetResult(), and INotifyCompletion.
		static bool IsAwaiter(INamedTypeSymbol awaiter, IAssemblySymbol project)
		{
			var notify = false;

			foreach (var @interface in awaiter.TypeKind == TypeKind.Interface ? awaiter.AllInterfaces.Add(awaiter) : awaiter.AllInterfaces)
			{
				if (@interface.Name == "INotifyCompletion" && @interface.ContainingNamespace.ToDisplayString() == "System.Runtime.CompilerServices")
				{
					notify = true;
					break;
				}
			}

			if (!notify)
				return false;

			var completed = false;

			foreach (var member in InstanceMembers(awaiter, "IsCompleted"))
			{
				if (member is IPropertySymbol { IsStatic: false, Type.SpecialType: SpecialType.System_Boolean, GetMethod: { } getter } &&
					IsAccessible(getter, project))
				{
					completed = true;
					break;
				}
			}

			if (!completed)
				return false;

			foreach (var member in InstanceMembers(awaiter, "GetResult"))
				if (member is IMethodSymbol { IsStatic: false, Parameters.Length: 0, TypeParameters.Length: 0 } getResult && IsAccessible(getResult, project))
					return true;

			return false;
		}

		// The members a member access on a value of the type finds: the type's own and its base classes' (an interface's
		// own and its base interfaces'). A class does not find the members of its interfaces, which an explicit
		// implementation would otherwise appear to provide.
		static System.Collections.Generic.IEnumerable<ISymbol> InstanceMembers(INamedTypeSymbol type, string name)
		{
			if (type.TypeKind == TypeKind.Interface)
			{
				foreach (var member in type.GetMembers(name))
					yield return member;

				foreach (var @interface in type.AllInterfaces)
					foreach (var member in @interface.GetMembers(name))
						yield return member;

				yield break;
			}

			for (var current = type; current is not null; current = current.BaseType)
				foreach (var member in current.GetMembers(name))
					yield return member;
		}

		// Public always; internal members only inside the project's own assembly.
		static bool IsAccessible(ISymbol symbol, IAssemblySymbol project)
		{
			return symbol.DeclaredAccessibility switch
			{
				Accessibility.Public                                        => true,
				Accessibility.Internal or Accessibility.ProtectedOrInternal => SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, project),
				_                                                           => false,
			};
		}

		bool? _isGenerated;

		/// <summary>
		/// <c>generated</c>: declared by the compiler (a source symbol that is implicitly declared, such as a record's
		/// <c>ToString</c>) or marked <c>[CompilerGenerated]</c>; a method read from metadata has only the mark, which
		/// the compiler puts on everything it declares.
		/// </summary>
		public bool IsGenerated
		{
			get
			{
				Require(FactProperty.Generated);

				var definition = Definition;

				return definition._isGenerated ??= definition.Computed(IsGeneratedCore(definition.Method));
			}
		}

		static bool IsGeneratedCore(IMethodSymbol method)
		{
			if (method.IsImplicitlyDeclared)
				return true;

			foreach (var attribute in method.GetAttributes())
			{
				var type = attribute.AttributeClass;

				if (type is { Name: "CompilerGeneratedAttribute" } && type.ContainingNamespace.ToDisplayString() == "System.Runtime.CompilerServices")
					return true;
			}

			return false;
		}

		ImmutableArray<INamedTypeSymbol> _attributes;

		/// <summary>
		/// <c>attribute:</c>: the classes of the attributes written on the method or on any type that contains it
		/// (outer types included), as <see cref="WrittenAttributes"/> defines "written". Attributes an override
		/// inherits from the method it overrides do not count.
		/// </summary>
		public ImmutableArray<INamedTypeSymbol> Attributes
		{
			get
			{
				Require(FactProperty.Attribute);

				var definition = Definition;

				if (definition._attributes.IsDefault)
				{
					var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();

					WrittenAttributes.AddTo(definition.Method, builder);

					for (var type = definition.Method.ContainingType; type is not null; type = type.ContainingType)
						WrittenAttributes.AddTo(type, builder);

					definition._attributes = definition.Computed(builder.ToImmutable());
				}

				return definition._attributes;
			}
		}

		// ---- Hierarchy: judged on the constructed symbols of the call ----

		ImmutableArray<INamedTypeSymbol> _inheritedTypes;

		/// <summary>
		/// <c>inherits:</c>: the containing type, every base class up to <c>object</c>, and every interface the
		/// containing type implements (or, for an interface member, extends).
		/// </summary>
		public ImmutableArray<INamedTypeSymbol> InheritedTypes
		{
			get
			{
				Require(FactProperty.Inherits);

				if (_inheritedTypes.IsDefault)
				{
					var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
					var type    = Method.ContainingType;

					for (var current = type; current is not null; current = current.BaseType)
						builder.Add(current);

					builder.AddRange(type.AllInterfaces);

					_inheritedTypes = Computed(builder.ToImmutable());
				}

				return _inheritedTypes;
			}
		}

		ImmutableArray<IMethodSymbol> _implementedMembers;

		/// <summary>
		/// <c>implements:</c>: the interface members the method is: itself when it is an interface member, otherwise
		/// every member of an interface of the containing type that the containing type implements with this method.
		/// </summary>
		public ImmutableArray<IMethodSymbol> ImplementedMembers
		{
			get
			{
				Require(FactProperty.Implements);

				if (_implementedMembers.IsDefault)
					_implementedMembers = Computed(FindImplementedMembers(Method));

				return _implementedMembers;
			}
		}

		// A method implements an interface member when it, or a method it overrides, is what its type maps the member to.
		static ImmutableArray<IMethodSymbol> FindImplementedMembers(IMethodSymbol method)
		{
			var builder = ImmutableArray.CreateBuilder<IMethodSymbol>();
			var seen    = new System.Collections.Generic.HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

			for (var current = method; current is not null; current = GetOverridden(current))
			{
				var type = current.ContainingType;

				if (type.TypeKind == TypeKind.Interface)
				{
					if (seen.Add(current))
						builder.Add(current);

					continue;
				}

				foreach (var @interface in type.AllInterfaces)
				{
					foreach (var member in @interface.GetMembers(current.Name))
					{
						if (member is not IMethodSymbol interfaceMethod ||
							type.FindImplementationForInterfaceMember(interfaceMethod) is not IMethodSymbol implementation ||
							!SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, current.OriginalDefinition))
						{
							continue;
						}

						// The call of a generic method implements the interface member with the same type arguments.
						if (current.IsGenericMethod && !SymbolEqualityComparer.Default.Equals(current, current.ConstructedFrom) &&
							interfaceMethod.TypeParameters.Length == current.TypeArguments.Length)
						{
							interfaceMethod = interfaceMethod.Construct(current.TypeArguments.ToArray());
						}

						if (seen.Add(interfaceMethod))
							builder.Add(interfaceMethod);
					}
				}

				foreach (var explicitImplementation in current.ExplicitInterfaceImplementations)
					if (seen.Add(explicitImplementation))
						builder.Add(explicitImplementation);
			}

			return builder.ToImmutable();
		}

		/// <summary>
		/// The method <paramref name="method"/> overrides. Roslyn gives no <c>OverriddenMethod</c> for a method
		/// constructed from a generic type or method, so it is found from the definition's and then taken from the
		/// constructed base type, which carries the substitutions.
		/// </summary>
		static IMethodSymbol? GetOverridden(IMethodSymbol method)
		{
			if (method.OverriddenMethod is { } direct)
				return direct;

			var definition = method.OriginalDefinition;

			if (definition.OverriddenMethod?.OriginalDefinition is not { } overriddenDefinition)
				return null;

			for (var type = method.ContainingType.BaseType; type is not null; type = type.BaseType)
			{
				if (!SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, overriddenDefinition.ContainingType))
					continue;

				foreach (var member in type.GetMembers(overriddenDefinition.Name))
				{
					if (member is IMethodSymbol candidate && SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, overriddenDefinition))
					{
						// A generic method keeps its own type arguments.
						if (candidate.IsGenericMethod && !SymbolEqualityComparer.Default.Equals(method, method.ConstructedFrom))
							return candidate.Construct(method.TypeArguments.ToArray());

						return candidate;
					}
				}
			}

			return null;
		}

		ImmutableArray<IMethodSymbol> _overrideChain;

		/// <summary>
		/// <c>overrides:</c>: the method itself, then the method it overrides, then the one that one overrides, up to
		/// the method that introduced the slot. Bases that are not public are walked too.
		/// </summary>
		public ImmutableArray<IMethodSymbol> OverrideChain
		{
			get
			{
				Require(FactProperty.Overrides);

				if (_overrideChain.IsDefault)
				{
					var builder = ImmutableArray.CreateBuilder<IMethodSymbol>();

					for (var current = Method; current is not null; current = GetOverridden(current))
						builder.Add(current);

					_overrideChain = Computed(builder.ToImmutable());
				}

				return _overrideChain;
			}
		}
	}
}
