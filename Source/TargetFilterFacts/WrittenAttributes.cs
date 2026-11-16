using System.Collections.Generic;
using System.Collections.Immutable;

using Microsoft.CodeAnalysis;

namespace AspectGenerator.TargetFilterFacts
{
	/// <summary>
	/// The attributes an author wrote, as opposed to the ones the compiler adds to a method or a type it emits.
	/// A symbol read from source and the same symbol read from an implementation or a reference assembly then show the
	/// same list: the compiler adds some attributes only when it emits (<c>AsyncStateMachine</c>, <c>Nullable</c>), and
	/// source symbols carry some that are not stored as attributes at all (<c>DllImport</c>, <c>MethodImpl</c>).
	/// </summary>
	static class WrittenAttributes
	{
		// Full names, namespace included. The first group is what the compiler emits on methods and types on its own;
		// the second group is the pseudo-attributes: written in source, stored as flags in metadata.
		static readonly HashSet<string> _ignored = new()
		{
			"System.Runtime.CompilerServices.AsyncStateMachineAttribute",
			"System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute",
			"System.Runtime.CompilerServices.IteratorStateMachineAttribute",
			"System.Runtime.CompilerServices.NullableAttribute",
			"System.Runtime.CompilerServices.NullableContextAttribute",
			"System.Runtime.CompilerServices.NullablePublicOnlyAttribute",
			"System.Runtime.CompilerServices.CompilerGeneratedAttribute",
			"System.Runtime.CompilerServices.ExtensionAttribute",
			"System.Runtime.CompilerServices.IsReadOnlyAttribute",
			"System.Runtime.CompilerServices.IsByRefLikeAttribute",
			"System.Runtime.CompilerServices.IsUnmanagedAttribute",
			"System.Runtime.CompilerServices.NativeIntegerAttribute",
			"System.Runtime.CompilerServices.ScopedRefAttribute",
			"System.Runtime.CompilerServices.RefSafetyRulesAttribute",
			"System.Runtime.CompilerServices.RequiresLocationAttribute",
			"System.Runtime.CompilerServices.ParamCollectionAttribute",
			"System.Runtime.CompilerServices.PreserveBaseOverridesAttribute",
			"System.Runtime.CompilerServices.CompilerFeatureRequiredAttribute",
			"System.Runtime.CompilerServices.DynamicAttribute",
			"System.Runtime.CompilerServices.TupleElementNamesAttribute",
			"System.Reflection.DefaultMemberAttribute",

			"System.Runtime.InteropServices.DllImportAttribute",
			"System.Runtime.InteropServices.StructLayoutAttribute",
			"System.Runtime.InteropServices.FieldOffsetAttribute",
			"System.Runtime.InteropServices.MarshalAsAttribute",
			"System.Runtime.InteropServices.InAttribute",
			"System.Runtime.InteropServices.OutAttribute",
			"System.Runtime.InteropServices.OptionalAttribute",
			"System.Runtime.InteropServices.PreserveSigAttribute",
			"System.Runtime.InteropServices.ComImportAttribute",
			"System.Runtime.CompilerServices.MethodImplAttribute",
			"System.Runtime.CompilerServices.SpecialNameAttribute",
			"System.SerializableAttribute",
			"System.NonSerializedAttribute",
		};

		public static bool IsWritten(AttributeData attribute)
		{
			var type = attribute.AttributeClass;

			return type is not null && !_ignored.Contains(GetFullName(type));
		}

		/// <summary>
		/// The attribute classes written on a symbol, in declaration order (an attribute repeated on several partial
		/// declarations appears once per use).
		/// </summary>
		public static void AddTo(ISymbol symbol, ImmutableArray<INamedTypeSymbol>.Builder result)
		{
			var attributes = symbol.GetAttributes();

			// The compiler puts DebuggerStepThrough on the stub of an async method when it emits one, and a source
			// symbol does not show it. Metadata cannot tell that one from a written one, so on an async method neither
			// form reports it.
			var asyncMethod = symbol is IMethodSymbol { IsAsync: true } || HasAsyncStateMachine(attributes);

			foreach (var attribute in attributes)
			{
				if (!IsWritten(attribute))
					continue;

				if (asyncMethod && attribute.AttributeClass!.Name == "DebuggerStepThroughAttribute" && GetFullName(attribute.AttributeClass) == "System.Diagnostics.DebuggerStepThroughAttribute")
					continue;

				result.Add(attribute.AttributeClass!);
			}
		}

		static bool HasAsyncStateMachine(ImmutableArray<AttributeData> attributes)
		{
			foreach (var attribute in attributes)
			{
				var type = attribute.AttributeClass;

				if (type is not null && type.Name is "AsyncStateMachineAttribute" or "AsyncIteratorStateMachineAttribute")
					return true;
			}

			return false;
		}

		static string GetFullName(INamedTypeSymbol type)
		{
			var @namespace = type.ContainingNamespace;

			if (type.ContainingType is not null || @namespace is null || @namespace.IsGlobalNamespace)
				return type.Name;

			return @namespace.ToDisplayString() + "." + type.Name;
		}
	}
}
