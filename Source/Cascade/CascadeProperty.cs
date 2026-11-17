using System;

namespace AspectGenerator.Cascade
{
	/// <summary>
	/// The properties of a method a target filter condition speaks about. Every key and word of the filter language maps
	/// to exactly one of them; a set of properties is a combination of the flags.
	/// </summary>
	[Flags]
	enum CascadeProperty
	{
		None          = 0,
		Location      = 1 << 0,
		Method        = 1 << 1,
		Returns       = 1 << 2,
		Params        = 1 << 3,
		Param         = 1 << 4,
		Attribute     = 1 << 5,
		Inherits      = 1 << 6,
		Implements    = 1 << 7,
		Overrides     = 1 << 8,
		Accessibility = 1 << 9,
		Static        = 1 << 10,
		Async         = 1 << 11,
		Virtuality    = 1 << 12,
		Extern        = 1 << 13,
		Extension     = 1 << 14,
		Origin        = 1 << 15,
		Generated     = 1 << 16,
	}

	/// <summary>The level an aspect attribute is placed on, from the least to the most specific.</summary>
	enum CascadeLevel
	{
		/// <summary><c>DefaultTargetFilter</c> of the aspect's declaration.</summary>
		Aspect,
		/// <summary>An <c>[assembly: X]</c> attribute.</summary>
		Assembly,
		/// <summary>An attribute on the method's own type (each attribute on each partial declaration is a block).</summary>
		Type,
		/// <summary>An attribute on the method itself.</summary>
		Method,
	}

	/// <summary>Which methods an assembly-level block reaches when it does not say so itself.</summary>
	enum AssemblyFilterReach
	{
		/// <summary>
		/// An assembly block selects only methods declared in the project, unless it declares <c>origin</c> (<c>project</c>,
		/// <c>origin: any</c>) or includes a <c>location</c> or <c>method</c> condition. <c>public</c>, <c>async</c> and
		/// subtractions keep the restriction, so <c>public</c> alone does not select every public call into a reference.
		/// </summary>
		ProjectUnlessOriginOrPlace,

		/// <summary>As <see cref="ProjectUnlessOriginOrPlace"/>, and any include line lifts the restriction as well.</summary>
		ProjectUnlessAnyInclude,
	}

	/// <summary>What happens to a filter read from a referenced assembly that has an error.</summary>
	enum UnreadableLibraryFilter
	{
		/// <summary>The block's errors count as the aspect's own and turn the aspect off.</summary>
		TurnAspectOff,

		/// <summary>The block is left out with a warning naming it, and the rest of the aspect works.</summary>
		Ignored,
	}

	static class CascadeDefaults
	{
		/// <summary>
		/// The reach of an assembly block. The rule is not final yet; this is the one place that chooses it, and the
		/// cascade tests run the other choice as well.
		/// </summary>
		public const AssemblyFilterReach AssemblyReach = AssemblyFilterReach.ProjectUnlessOriginOrPlace;

		/// <summary>
		/// What a library filter with an error does: a filter that cannot be read (one written for another version of
		/// the filter language, say) is an error like any other and turns the aspect off. The cascade tests run the
		/// other choice as well.
		/// </summary>
		public const UnreadableLibraryFilter LibraryFilterWithError = UnreadableLibraryFilter.TurnAspectOff;
	}

	/// <summary>The ids of the diagnostics the cascade reports.</summary>
	static class CascadeDiagnosticIds
	{
		public const string SyntaxError       = "AG0220";
		public const string UnknownKeyOrWord  = "AG0221";
		public const string RepeatedInclude   = "AG0225";
		public const string InvalidReference  = "AG0233";
		public const string InvalidAny        = "AG0234";
		public const string SettingsConflict  = "AG0235";
		public const string UnreadableLibrary = "AG0236";
	}

	static class CascadePropertyExtensions
	{
		public static bool Overlaps(this CascadeProperty left, CascadeProperty right)
		{
			return (left & right) != 0;
		}

		/// <summary>The property name as a filter writes it (<c>accessibility</c>, <c>origin</c>, …).</summary>
		public static string ToFilterName(this CascadeProperty property)
		{
			var name = property.ToString();

			return char.ToLowerInvariant(name[0]) + name.Substring(1);
		}
	}
}
