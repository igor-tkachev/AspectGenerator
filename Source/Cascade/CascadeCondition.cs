using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace AspectGenerator.Cascade
{
	/// <summary>
	/// What a condition needs from the cascade while it is evaluated for one method: the value a level reference stands
	/// for, and whether the method counts as declared in the project of the filters being evaluated.
	/// </summary>
	abstract class CascadeScope<TFact>
	{
		/// <summary>The value property <paramref name="property"/> has for the method on the chain cut at <paramref name="level"/>.</summary>
		public abstract bool Resolve(CascadeLevel level, CascadeProperty property, TFact fact);

		public abstract bool IsProject(TFact fact);
	}

	/// <summary>
	/// A condition of a filter line, read into a tree whose leaves know their property. <c>not</c> stands before a single
	/// leaf only; the reader of the filter text rejects anything else.
	/// </summary>
	abstract class CascadeCondition<TFact>
	{
		/// <summary>The properties the condition mentions.</summary>
		public abstract CascadeProperty Properties { get; }

		public abstract bool Evaluate(TFact fact, CascadeScope<TFact> scope);

		/// <summary>Every leaf, in order.</summary>
		public virtual IEnumerable<CascadeCondition<TFact>> Leaves()
		{
			yield return this;
		}
	}

	/// <summary>A condition on a fact of the method: a key with a pattern, or a word.</summary>
	sealed class CascadeTest<TFact> : CascadeCondition<TFact>
	{
		readonly Func<TFact,bool> _matches;

		public CascadeTest(CascadeProperty property, string text, Func<TFact,bool> matches)
		{
			Property = property;
			Text     = text;
			_matches = matches;
		}

		public CascadeProperty Property { get; }
		public string          Text     { get; }

		public override CascadeProperty Properties => Property;

		public override bool Evaluate(TFact fact, CascadeScope<TFact> scope)
		{
			return _matches(fact);
		}

		public override string ToString()
		{
			return Text;
		}
	}

	/// <summary>
	/// <c>aspect.p</c> or <c>assembly.p</c>: the value <c>p</c> would have for the method if there were no attributes
	/// below that level. <see cref="KeyProperty"/> is the property of the key it is written under
	/// (<c>method: assembly.method</c>), or <see langword="null"/> when it stands alone.
	/// </summary>
	sealed class CascadeReference<TFact> : CascadeCondition<TFact>
	{
		public CascadeReference(CascadeLevel level, CascadeProperty property, CascadeProperty? keyProperty)
		{
			Level       = level;
			Property    = property;
			KeyProperty = keyProperty;
		}

		public CascadeLevel     Level       { get; }
		public CascadeProperty  Property    { get; }
		public CascadeProperty? KeyProperty { get; }

		public override CascadeProperty Properties => Property;

		public override bool Evaluate(TFact fact, CascadeScope<TFact> scope)
		{
			return scope.Resolve(Level, Property, fact);
		}

		public override string ToString()
		{
			return $"{Level.ToString().ToLowerInvariant()}.{Property.ToFilterName()}";
		}
	}

	/// <summary><c>p: any</c>: no restriction on <c>p</c>, and a declaration of it.</summary>
	sealed class CascadeAny<TFact> : CascadeCondition<TFact>
	{
		public CascadeAny(CascadeProperty property)
		{
			Property = property;
		}

		public CascadeProperty Property { get; }

		public override CascadeProperty Properties => Property;

		public override bool Evaluate(TFact fact, CascadeScope<TFact> scope)
		{
			return true;
		}

		public override string ToString()
		{
			return $"{Property.ToFilterName()}: any";
		}
	}

	sealed class CascadeNot<TFact> : CascadeCondition<TFact>
	{
		public CascadeNot(CascadeCondition<TFact> operand)
		{
			Operand = operand;
		}

		public CascadeCondition<TFact> Operand { get; }

		public override CascadeProperty Properties => Operand.Properties;

		public override bool Evaluate(TFact fact, CascadeScope<TFact> scope)
		{
			return !Operand.Evaluate(fact, scope);
		}

		public override IEnumerable<CascadeCondition<TFact>> Leaves()
		{
			return Operand.Leaves();
		}

		public override string ToString()
		{
			return "not " + Operand;
		}
	}

	/// <summary>An <c>and</c> (<see cref="IsAnd"/>) or an <c>or</c> of two or more operands.</summary>
	sealed class CascadeJunction<TFact> : CascadeCondition<TFact>
	{
		public CascadeJunction(bool isAnd, ImmutableArray<CascadeCondition<TFact>> operands)
		{
			IsAnd    = isAnd;
			Operands = operands;

			foreach (var operand in operands)
				Properties |= operand.Properties;
		}

		public bool                                    IsAnd    { get; }
		public ImmutableArray<CascadeCondition<TFact>> Operands { get; }

		public override CascadeProperty Properties { get; }

		public override bool Evaluate(TFact fact, CascadeScope<TFact> scope)
		{
			foreach (var operand in Operands)
				if (operand.Evaluate(fact, scope) != IsAnd)
					return !IsAnd;

			return IsAnd;
		}

		public override IEnumerable<CascadeCondition<TFact>> Leaves()
		{
			foreach (var operand in Operands)
				foreach (var leaf in operand.Leaves())
					yield return leaf;
		}

		public override string ToString()
		{
			return "(" + string.Join(IsAnd ? " and " : " or ", Operands) + ")";
		}
	}

	/// <summary>
	/// The condition of an implicit declaration: "here" of a type or method block, which every method its chain is built
	/// for satisfies, or the implicit <c>project</c> of an assembly block.
	/// </summary>
	sealed class CascadeImplicit<TFact> : CascadeCondition<TFact>
	{
		public CascadeImplicit(CascadeProperty property)
		{
			Property = property;
		}

		public CascadeProperty Property { get; }

		public override CascadeProperty Properties => Property;

		public override bool Evaluate(TFact fact, CascadeScope<TFact> scope)
		{
			return Property != CascadeProperty.Origin || scope.IsProject(fact);
		}

		public override string ToString()
		{
			return Property == CascadeProperty.Origin ? "project (implicit)" : $"{Property.ToFilterName()}: here (implicit)";
		}
	}
}
