using System;

namespace AspectGenerator.TargetFilterFacts
{
	/// <summary>
	/// The properties of a method a target filter can ask about. A condition is always about exactly one property.
	/// </summary>
	[Flags]
	enum FactProperty
	{
		None          = 0,

		/// <summary><c>namespace:</c>, <c>type:</c>: the containing namespace and type.</summary>
		Location      = 1 << 0,

		/// <summary><c>method:</c>: the <c>Type.Method</c> path.</summary>
		Method        = 1 << 1,

		/// <summary><c>returns:</c>.</summary>
		Returns       = 1 << 2,

		/// <summary><c>params:</c> (the whole parameter list).</summary>
		Params        = 1 << 3,

		/// <summary><c>param:</c> (some parameter); reads the same parameter list as <see cref="Params"/>.</summary>
		Param         = 1 << 4,

		/// <summary><c>attribute:</c>.</summary>
		Attribute     = 1 << 5,

		/// <summary><c>inherits:</c>.</summary>
		Inherits      = 1 << 6,

		/// <summary><c>implements:</c>.</summary>
		Implements    = 1 << 7,

		/// <summary><c>overrides:</c>.</summary>
		Overrides     = 1 << 8,

		/// <summary>The accessibility words (<c>public</c>, <c>internal</c>, ...).</summary>
		Accessibility = 1 << 9,

		/// <summary><c>static</c>, <c>instance</c>.</summary>
		Static        = 1 << 10,

		/// <summary><c>async</c>.</summary>
		Async         = 1 << 11,

		/// <summary><c>virtual</c>, <c>override</c>, <c>abstract</c>, <c>sealed</c>.</summary>
		Virtuality    = 1 << 12,

		/// <summary><c>extern</c>.</summary>
		Extern        = 1 << 13,

		/// <summary><c>extension</c>.</summary>
		Extension     = 1 << 14,

		/// <summary><c>project</c>: the method is declared in the current compilation.</summary>
		Origin        = 1 << 15,

		/// <summary><c>generated</c>.</summary>
		Generated     = 1 << 16,

		All           = (1 << 17) - 1,
	}

	/// <summary>
	/// The properties a selection actually reads, decided before any method is looked at (from the declarations of
	/// every filter that can speak about the method). A fact outside the plan is never computed: reading it is a defect
	/// of the plan, so it throws instead of quietly paying for it.
	/// </summary>
	readonly struct FactPlan
	{
		public static readonly FactPlan All = new(FactProperty.All);

		public FactPlan(FactProperty properties)
		{
			Properties = properties;
		}

		public FactProperty Properties { get; }

		public bool Contains(FactProperty property)
		{
			return (Properties & property) == property;
		}

		public bool ContainsAny(FactProperty properties)
		{
			return (Properties & properties) != 0;
		}

		public static implicit operator FactPlan(FactProperty properties)
		{
			return new FactPlan(properties);
		}
	}
}
