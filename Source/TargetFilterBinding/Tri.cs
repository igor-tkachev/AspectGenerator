namespace AspectGenerator.TargetFilterBinding
{
	/// <summary>
	/// The answer of a pattern. On the definition of a generic method a pattern that names a type where the definition
	/// has a type parameter cannot answer for every call: it <see cref="Depends"/> on the call. The values are ordered,
	/// so that <c>and</c> is the smaller and <c>or</c> the greater of two answers.
	/// </summary>
	enum Tri : byte
	{
		False   = 0,
		Depends = 1,
		True    = 2,
	}

	/// <summary>How a type parameter left in a type is read.</summary>
	enum MatchPhase : byte
	{
		/// <summary>
		/// The type is of the definition of a method: a type parameter stands for whatever a call puts there, so any
		/// pattern but <c>*</c> depends on the call.
		/// </summary>
		Definition,

		/// <summary>
		/// The type is of a call: a type parameter left in it is one of the calling code, which no pattern but
		/// <c>*</c> matches.
		/// </summary>
		Call,
	}

	static class TriExtensions
	{
		public static Tri And(this Tri left, Tri right)
		{
			return left < right ? left : right;
		}

		public static Tri Or(this Tri left, Tri right)
		{
			return left > right ? left : right;
		}

		public static Tri ToTri(this bool value)
		{
			return value ? Tri.True : Tri.False;
		}
	}
}
