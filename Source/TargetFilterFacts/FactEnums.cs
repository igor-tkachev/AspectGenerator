using System;

namespace AspectGenerator.TargetFilterFacts
{
	/// <summary>
	/// The virtuality words. A <c>sealed override</c> is both <see cref="Override"/> and <see cref="Sealed"/>; an
	/// override is never <see cref="Virtual"/>, whatever the metadata says about its slot.
	/// </summary>
	[Flags]
	enum Virtuality
	{
		None     = 0,
		Virtual  = 1,
		Override = 2,
		Abstract = 4,
		Sealed   = 8,
	}

	/// <summary>
	/// The modifiers of a parameter that a <c>params:</c> / <c>param:</c> pattern matches exactly.
	/// </summary>
	[Flags]
	enum ParameterModifiers
	{
		None        = 0,
		Ref         = 1,
		RefReadOnly = 2,
		Out         = 4,
		In          = 8,
		Params      = 16,
		This        = 32,
	}

	enum ReturnRef
	{
		None,
		Ref,
		RefReadOnly,
	}
}
