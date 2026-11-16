using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace AspectGenerator.Cascade
{
	/// <summary>One line of a filter: an include (<c>expr</c>) or a subtraction (<c>- expr</c>).</summary>
	sealed record CascadeLine<TFact>(bool IsSubtraction, CascadeCondition<TFact> Condition, object? Source = null);

	/// <summary>
	/// A part of a line that the cascade decides as a whole: a declaration of every property in
	/// <see cref="Properties"/>. More than one property makes it a compound declaration.
	/// </summary>
	sealed class CascadeDeclaration<TFact>
	{
		public CascadeDeclaration(CascadeBlock<TFact> block, bool isSubtraction, CascadeCondition<TFact> condition, CascadeLine<TFact>? line)
		{
			Block         = block;
			IsSubtraction = isSubtraction;
			Condition     = condition;
			Line          = line;
			Properties    = condition.Properties;
		}

		public CascadeBlock<TFact>     Block         { get; }
		public bool                    IsSubtraction { get; }
		public CascadeCondition<TFact> Condition     { get; }
		public CascadeProperty         Properties    { get; }

		/// <summary>The line it comes from; <see langword="null"/> for an implicit declaration.</summary>
		public CascadeLine<TFact>? Line { get; }

		public bool IsImplicit => Line is null;
		public bool IsAny      => Condition is CascadeAny<TFact>;

		public bool IsSatisfied(TFact fact, CascadeScope<TFact> scope)
		{
			return Condition.Evaluate(fact, scope) != IsSubtraction;
		}

		public override string ToString()
		{
			return (IsSubtraction ? "- " : "") + Condition;
		}
	}

	sealed record CascadeDiagnostic(string Id, string Message, object? Block, object? Source);

	/// <summary>
	/// The filter of one aspect attribute (or of the aspect's <c>DefaultTargetFilter</c>), read into per-property
	/// declarations with its settings. Building a block checks what can be checked on the block alone.
	/// </summary>
	sealed class CascadeBlock<TFact>
	{
		CascadeBlock(CascadeLevel level, string label, object? source, IReadOnlyDictionary<string,object?> settings)
		{
			Level    = level;
			Label    = label;
			Source   = source;
			Settings = settings;
		}

		public CascadeLevel Level  { get; }
		public string       Label  { get; }
		public object?      Source { get; }

		/// <summary>
		/// The arguments other than <c>TargetFilter</c> the attribute writes, by name (all constructor arguments together
		/// under one name). An argument not written is not in it.
		/// </summary>
		public IReadOnlyDictionary<string,object?> Settings { get; }

		/// <summary>The declarations of the lines, then the implicit declarations of the block's level.</summary>
		public ImmutableArray<CascadeDeclaration<TFact>> Declarations { get; private set; }

		/// <summary>The properties <see cref="Declarations"/> declare.</summary>
		public CascadeProperty Declared { get; private set; }

		public ImmutableArray<CascadeDiagnostic> Diagnostics { get; private set; }

		/// <summary>Whether a declaration of the block references the assembly level, so that its value depends on the assembly block.</summary>
		public bool ReferencesAssembly { get; private set; }

		public bool HasErrors => !Diagnostics.IsEmpty;

		public override string ToString()
		{
			return Label;
		}

		/// <param name="readDiagnostics">The errors the reader of the filter text found; their lines are not in <paramref name="lines"/>.</param>
		public static CascadeBlock<TFact> Create(
			CascadeLevel                         level,
			IEnumerable<CascadeLine<TFact>>      lines,
			IReadOnlyDictionary<string,object?>? settings        = null,
			string?                              label           = null,
			object?                              source          = null,
			IEnumerable<CascadeDiagnostic>?      readDiagnostics = null,
			AssemblyFilterReach                  assemblyReach   = CascadeDefaults.AssemblyReach)
		{
			var block        = new CascadeBlock<TFact>(level, label ?? level.ToString(), source, settings ?? ImmutableDictionary<string,object?>.Empty);
			var diagnostics  = ImmutableArray.CreateBuilder<CascadeDiagnostic>();
			var declarations = ImmutableArray.CreateBuilder<CascadeDeclaration<TFact>>();

			if (readDiagnostics is not null)
				diagnostics.AddRange(readDiagnostics.Select(d => d with { Block = block }));

			foreach (var line in lines)
			{
				var error = CheckLine(level, line);

				if (error is not null)
				{
					diagnostics.Add(new(error.Value.Id, error.Value.Message, block, line.Source));
					continue;
				}

				var parts = Split(line);

				error = CheckReferencePlacement(parts);

				if (error is not null)
				{
					diagnostics.Add(new(error.Value.Id, error.Value.Message, block, line.Source));
					continue;
				}

				foreach (var part in parts)
					declarations.Add(new(block, line.IsSubtraction, part, line));
			}

			var includes = declarations.Where(d => !d.IsSubtraction).ToList();

			for (var i = 0; i < includes.Count; i++)
			for (var j = i + 1; j < includes.Count; j++)
			{
				var a = includes[i];
				var b = includes[j];

				if (!a.Properties.Overlaps(b.Properties))
					continue;

				var lineSource = b.Line!.Source ?? a.Line!.Source;

				if (a.IsAny || b.IsAny)
					diagnostics.Add(new(CascadeDiagnosticIds.InvalidAny,
						$"'{(a.IsAny ? a : b)}' must be the only include of its property in the filter", block, lineSource));
				else
					diagnostics.Add(new(CascadeDiagnosticIds.RepeatedInclude,
						$"A filter includes each property once: '{a}' and '{b}' both include {PropertyNames(a.Properties & b.Properties)}; " +
						"join them into one line with 'or' or 'and'", block, lineSource));
			}

			AddImplicit(block, level, declarations, assemblyReach);

			block.Declarations = declarations.ToImmutable();
			block.Diagnostics  = diagnostics.ToImmutable();

			foreach (var declaration in block.Declarations)
			{
				block.Declared |= declaration.Properties;

				if (declaration.Condition.Leaves().Any(l => l is CascadeReference<TFact> { Level: CascadeLevel.Assembly }))
					block.ReferencesAssembly = true;
			}

			return block;
		}

		/// <summary>
		/// Splits a line into its declarations: an include at its top-level <c>and</c>, a subtraction at its top-level
		/// <c>or</c>, each into the connected groups of operands that share a property. Operands that share no property
		/// with the rest become declarations of their own; a group keeps its operands in their order. Parentheses do not
		/// count: <c>a and (b and c)</c> splits as <c>a and b and c</c>.
		/// </summary>
		public static ImmutableArray<CascadeCondition<TFact>> Split(CascadeLine<TFact> line)
		{
			var condition = line.Condition;

			if (condition is not CascadeJunction<TFact> junction || junction.IsAnd == line.IsSubtraction)
				return [condition];

			var flat = new List<CascadeCondition<TFact>>();

			Flatten(junction);

			var groups = new List<(CascadeProperty Properties, List<CascadeCondition<TFact>> Operands)>();

			foreach (var operand in flat)
			{
				var properties = operand.Properties;
				var members    = new List<CascadeCondition<TFact>>();

				for (var i = 0; i < groups.Count;)
				{
					if (groups[i].Properties.Overlaps(properties))
					{
						properties |= groups[i].Properties;
						members.AddRange(groups[i].Operands);
						groups.RemoveAt(i);
					}
					else
					{
						i++;
					}
				}

				members.Add(operand);
				groups.Add((properties, members));
			}

			var result = ImmutableArray.CreateBuilder<CascadeCondition<TFact>>(groups.Count);

			foreach (var (_, operands) in groups)
				result.Add(operands.Count == 1 ? operands[0] : new CascadeJunction<TFact>(junction.IsAnd, [..operands]));

			return result.MoveToImmutable();

			void Flatten(CascadeCondition<TFact> operand)
			{
				if (operand is CascadeJunction<TFact> inner && inner.IsAnd == junction.IsAnd)
				{
					foreach (var o in inner.Operands)
						Flatten(o);
				}
				else
				{
					flat.Add(operand);
				}
			}
		}

		static (string Id, string Message)? CheckLine(CascadeLevel level, CascadeLine<TFact> line)
		{
			if (line.Condition is CascadeAny<TFact>)
			{
				if (line.IsSubtraction)
					return (CascadeDiagnosticIds.InvalidAny, $"'- {line.Condition}' subtracts nothing: 'any' is an include only");
			}
			else if (line.Condition.Leaves().Any(l => l is CascadeAny<TFact>))
			{
				return (CascadeDiagnosticIds.InvalidAny, "'any' is a whole value: write 'p: any' as the only condition of the line");
			}

			foreach (var leaf in line.Condition.Leaves())
			{
				if (leaf is not CascadeReference<TFact> reference)
					continue;

				if (reference.KeyProperty is { } key && key != reference.Property)
					return (CascadeDiagnosticIds.InvalidReference,
						$"'{key.ToFilterName()}: {reference}' names another property; a reference names the property being declared");

				if (!IsAllowed(level, reference.Level))
					return (CascadeDiagnosticIds.InvalidReference, level == CascadeLevel.Aspect
						? $"'{reference}': a default target filter cannot reference a level"
						: $"'{reference}': a {level.ToString().ToLowerInvariant()} filter can reference only the levels above it");
			}

			return null;
		}

		static bool IsAllowed(CascadeLevel block, CascadeLevel reference)
		{
			return block switch
			{
				CascadeLevel.Assembly => reference == CascadeLevel.Aspect,
				CascadeLevel.Type     => reference is CascadeLevel.Aspect or CascadeLevel.Assembly,
				CascadeLevel.Method   => reference is CascadeLevel.Aspect or CascadeLevel.Assembly,
				_                     => false,
			};
		}

		/// <summary>A reference stands only in a declaration of its own property, never in a compound one.</summary>
		static (string Id, string Message)? CheckReferencePlacement(ImmutableArray<CascadeCondition<TFact>> parts)
		{
			foreach (var part in parts)
				foreach (var leaf in part.Leaves())
					if (leaf is CascadeReference<TFact> reference && part.Properties != reference.Property)
						return (CascadeDiagnosticIds.InvalidReference,
							$"'{reference}' stands in a condition that also speaks about {PropertyNames(part.Properties & ~reference.Property)}; " +
							"a reference stands only in a declaration of its own property");

			return null;
		}

		static void AddImplicit(
			CascadeBlock<TFact>                                      block,
			CascadeLevel                                             level,
			ImmutableArray<CascadeDeclaration<TFact>>.Builder        declarations,
			AssemblyFilterReach                                      assemblyReach)
		{
			var declared = CascadeProperty.None;
			var included = CascadeProperty.None;
			var anyInclude = false;

			foreach (var declaration in declarations)
			{
				declared |= declaration.Properties;

				if (!declaration.IsSubtraction)
				{
					included   |= declaration.Properties;
					anyInclude  = true;
				}
			}

			switch (level)
			{
				case CascadeLevel.Assembly:
					var lifted =
						declared.Overlaps(CascadeProperty.Origin) ||
						included.Overlaps(CascadeProperty.Location | CascadeProperty.Method) ||
						assemblyReach == AssemblyFilterReach.ProjectUnlessAnyInclude && anyInclude;

					if (!lifted)
						declarations.Add(Implicit(CascadeProperty.Origin));
					break;

				case CascadeLevel.Type:
					if (!declared.Overlaps(CascadeProperty.Location))
						declarations.Add(Implicit(CascadeProperty.Location));
					break;

				case CascadeLevel.Method:
					if (!declared.Overlaps(CascadeProperty.Location))
						declarations.Add(Implicit(CascadeProperty.Location));

					if (!declared.Overlaps(CascadeProperty.Method))
						declarations.Add(Implicit(CascadeProperty.Method));
					break;
			}

			CascadeDeclaration<TFact> Implicit(CascadeProperty property)
			{
				return new(block, false, new CascadeImplicit<TFact>(property), null);
			}
		}

		static string PropertyNames(CascadeProperty properties)
		{
			var names = new List<string>();

			foreach (CascadeProperty p in Enum.GetValues(typeof(CascadeProperty)))
				if (p != CascadeProperty.None && properties.Overlaps(p))
					names.Add("'" + p.ToFilterName() + "'");

			return string.Join(", ", names);
		}
	}
}
