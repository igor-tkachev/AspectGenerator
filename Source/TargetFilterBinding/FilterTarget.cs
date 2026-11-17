using System;

using AspectGenerator.Cascade;

namespace AspectGenerator.TargetFilterBinding
{
	/// <summary>
	/// The method one decision of the cascade is about, and the phase it is decided in.
	/// <para>
	/// A condition describes the call as written, with its type arguments. A decision is first asked of the
	/// <b>definition</b> of the method, once: where no condition it evaluates meets a type parameter of the definition
	/// that only a call fills in, its answer holds for every call. Otherwise it <see cref="DependsOnCall"/>, and each
	/// <b>call</b> is decided with its own facts; there a type parameter that is still left belongs to the calling code
	/// and is matched by <c>*</c> alone.
	/// </para>
	/// <para>
	/// The definition phase is cautious: a condition that cannot answer for every call makes the whole decision depend
	/// on the call, even where the conditions beside it would have decided alone. That costs a decision per call and
	/// never a wrong answer: the conditions that did answer on the definition answer the same in every call, so a
	/// decision that met none of the others runs the same in every call.
	/// </para>
	/// </summary>
	sealed class FilterTarget
	{
		FilterTarget(IMethodFacts facts, MatchPhase phase)
		{
			Facts = facts;
			Phase = phase;
		}

		public IMethodFacts Facts { get; }
		public MatchPhase   Phase { get; }

		/// <summary>A condition evaluated for this target could not answer for every call of the definition.</summary>
		public bool DependsOnCall { get; private set; }

		public static FilterTarget Definition(IMethodFacts definition)
		{
			return new FilterTarget(definition, MatchPhase.Definition);
		}

		public static FilterTarget Call(IMethodFacts call)
		{
			return new FilterTarget(call, MatchPhase.Call);
		}

		/// <summary>The answer of a condition as the cascade takes it; an answer that depends on the call is noted and read as no.</summary>
		internal bool Settle(Tri answer)
		{
			if (answer == Tri.Depends)
			{
				DependsOnCall = true;
				return false;
			}

			return answer == Tri.True;
		}

		/// <summary>
		/// Decides on the definition of a method. <see langword="false"/> when the decision depends on the call: then
		/// <paramref name="decision"/> means nothing, and each call is decided with <see cref="DecideCall"/>.
		/// </summary>
		/// <param name="decide">The decision of the cascade for a target (<c>CascadeEvaluator.Decide</c> with the blocks of the method).</param>
		public static bool TryDecideDefinition(IMethodFacts definition, Func<FilterTarget, CascadeDecision> decide, out CascadeDecision decision)
		{
			var target = Definition(definition);

			decision = decide(target);

			return !target.DependsOnCall;
		}

		/// <summary>Decides one call of a method, from the facts of the call.</summary>
		public static CascadeDecision DecideCall(IMethodFacts call, Func<FilterTarget, CascadeDecision> decide)
		{
			return decide(Call(call));
		}
	}
}
