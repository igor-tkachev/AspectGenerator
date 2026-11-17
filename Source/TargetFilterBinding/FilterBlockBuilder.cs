using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using AspectGenerator.Cascade;
using AspectGenerator.FilterV2;
using AspectGenerator.TargetFilterFacts;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace AspectGenerator.TargetFilterBinding
{
	/// <summary>A diagnostic of a filter: its id, the item of the filter it stands in and where in that item, and what it says.</summary>
	sealed class FilterBlockDiagnostic
	{
		public FilterBlockDiagnostic(string id, int item, TextSpan span, string message)
		{
			Id      = id;
			Item    = item;
			Span    = span;
			Message = message;
		}

		public string Id { get; }

		/// <summary>The index of the item: 0 for a filter written as one string, the element of a <c>string[]</c> otherwise.</summary>
		public int Item { get; }

		/// <summary>The characters of the item the diagnostic is about.</summary>
		public TextSpan Span    { get; }
		public string   Message { get; }

		public override string ToString()
		{
			return $"{Id} [{Item}]@{Span.Start}+{Span.Length}: {Message}";
		}
	}

	/// <summary>Where a line of a block was written: the source of the cascade's line.</summary>
	sealed class FilterLineOrigin
	{
		public FilterLineOrigin(int item, FilterLineSyntax syntax, string text)
		{
			Item   = item;
			Syntax = syntax;
			Text   = text;
		}

		public int              Item   { get; }
		public FilterLineSyntax Syntax { get; }

		/// <summary>The line as written.</summary>
		public string Text { get; }

		public override string ToString()
		{
			return Text;
		}
	}

	/// <summary>
	/// The filter of one aspect attribute, read from its text into a block of the cascade, with everything else the
	/// text decides: its diagnostics, the facts its conditions read, and what can be checked of a call before binding.
	/// </summary>
	sealed class FilterBlock
	{
		readonly ImmutableArray<(CascadeProperty Properties, BindingAnchors Anchors)> _includes;

		internal FilterBlock(
			CascadeBlock<FilterTarget>                                           block,
			ImmutableArray<FilterBlockDiagnostic>                                diagnostics,
			FactProperty                                                         facts,
			ImmutableArray<(CascadeProperty Properties, BindingAnchors Anchors)> includes)
		{
			Block       = block;
			Diagnostics = diagnostics;
			Facts       = facts;
			_includes   = includes;

			var anchors = BindingAnchors.Everything;

			foreach (var include in includes)
				anchors = anchors.And(include.Anchors);

			Anchors = anchors;
		}

		/// <summary>The block as the cascade evaluates it. It holds the lines without errors only.</summary>
		public CascadeBlock<FilterTarget> Block { get; }

		/// <summary>Every error of the filter, in the order of its items and of the text in each. Any of them turns the aspect off.</summary>
		public ImmutableArray<FilterBlockDiagnostic> Diagnostics { get; }

		public bool HasErrors => !Diagnostics.IsEmpty;

		/// <summary>The facts the conditions of the block read: what a fact record has to be planned for.</summary>
		public FactProperty Facts { get; }

		/// <summary>
		/// What every method the block can select satisfies, as far as it can be checked before binding: each include
		/// of the block holds of a selected method, a subtraction only takes away. "The project" in it is the assembly
		/// the block was written in: for a block read from a library, the library.
		/// </summary>
		public BindingAnchors Anchors { get; }

		/// <summary>
		/// As <see cref="Anchors"/>, with the includes of the aspect's default filter that this block leaves in force:
		/// those of properties the block does not declare.
		/// </summary>
		public BindingAnchors AnchorsUnder(FilterBlock aspectDefault)
		{
			var anchors = Anchors;

			foreach (var include in aspectDefault._includes)
				if (!include.Properties.Overlaps(Block.Declared))
					anchors = anchors.And(include.Anchors);

			return anchors;
		}
	}

	/// <summary>
	/// Reads the text of a filter into a block of the cascade: every item through the grammar, every line into a
	/// condition whose leaves match the facts of a method, and the checks that need the whole block.
	/// <list type="bullet">
	/// <item>A line with an error of its own is left out; the reader of the item has reported it.</item>
	/// <item><c>any</c> is a whole line: beside another condition it is an error (AG0234).</item>
	/// <item>A reference names a level above the block: none in a default filter, <c>aspect</c> in an assembly filter,
	/// <c>aspect</c> and <c>assembly</c> in a type or method filter (AG0233).</item>
	/// <item>A line is split into the parts that share no property; a reference in a part that speaks of another
	/// property as well is an error (AG0233).</item>
	/// <item>Over all items: a property is included once. A second include of it is an error (AG0225), or AG0234 when
	/// one of the two is <c>any</c>.</item>
	/// </list>
	/// </summary>
	static class FilterBlockBuilder
	{
		/// <param name="items">The filter: one string, or the elements of a <c>string[]</c>.</param>
		/// <param name="settings">The arguments other than the filter that the attribute writes.</param>
		public static FilterBlock Build(
			CascadeLevel                         level,
			IReadOnlyList<string>                items,
			IReadOnlyDictionary<string,object?>? settings       = null,
			string?                              label          = null,
			object?                              source         = null,
			bool                                 overridesIsKey   = FilterV2Language.OverridesIsKey,
			AssemblyFilterReach                  assemblyReach    = CascadeDefaults.AssemblyReach,
			ParameterModifierMatching            modifierMatching = PatternDefaults.ModifierMatching)
		{
			var state = new State(level, overridesIsKey, modifierMatching);

			for (var i = 0; i < items.Count; i++)
				state.ReadItem(i, items[i]);

			state.CheckIncludes();

			var diagnostics = state.Diagnostics
				.OrderBy(static d => d.Item)
				.ThenBy(static d => d.Span.Start)
				.ThenBy(static d => d.Span.Length)
				.ToList();

			var block = CascadeBlock<FilterTarget>.Create(
				level,
				state.Lines.Where(static l => !l.Dropped).Select(static l => l.Line),
				settings,
				label,
				source,
				diagnostics.Select(static d => new CascadeDiagnostic(d.Id, d.Message, null, d)),
				assemblyReach);

			// The checks above are those of the cascade, made here where the text is at hand. Should the cascade refuse a
			// line all the same, its error is kept and stands on the line.
			foreach (var extra in block.Diagnostics)
				if (extra.Source is FilterLineOrigin origin)
					diagnostics.Add(new FilterBlockDiagnostic(extra.Id, origin.Item, origin.Syntax.Span, extra.Message));

			var facts    = state.Facts;
			var includes = ImmutableArray.CreateBuilder<(CascadeProperty, BindingAnchors)>();

			foreach (var declaration in block.Declarations)
			{
				if (declaration.IsImplicit && declaration.Properties == CascadeProperty.Origin)
					facts |= FactProperty.Origin;

				if (!declaration.IsSubtraction)
					includes.Add((declaration.Properties, state.Anchors(declaration.Condition)));
			}

			return new FilterBlock(block, [..diagnostics], facts, includes.ToImmutable());
		}

		internal const string UnreadLine = "The line cannot be read.";

		/// <summary>The same property under its three names: of the language, of the cascade, of the fact record.</summary>
		internal static CascadeProperty ToCascade(FilterProperty property)
		{
			return property switch
			{
				FilterProperty.Location      => CascadeProperty.Location,
				FilterProperty.Method        => CascadeProperty.Method,
				FilterProperty.Returns       => CascadeProperty.Returns,
				FilterProperty.Params        => CascadeProperty.Params,
				FilterProperty.Param         => CascadeProperty.Param,
				FilterProperty.Attribute     => CascadeProperty.Attribute,
				FilterProperty.Inherits      => CascadeProperty.Inherits,
				FilterProperty.Implements    => CascadeProperty.Implements,
				FilterProperty.Overrides     => CascadeProperty.Overrides,
				FilterProperty.Accessibility => CascadeProperty.Accessibility,
				FilterProperty.Static        => CascadeProperty.Static,
				FilterProperty.Async         => CascadeProperty.Async,
				FilterProperty.Virtuality    => CascadeProperty.Virtuality,
				FilterProperty.Extern        => CascadeProperty.Extern,
				FilterProperty.Extension     => CascadeProperty.Extension,
				FilterProperty.Origin        => CascadeProperty.Origin,
				FilterProperty.Generated     => CascadeProperty.Generated,
				_                            => throw new ArgumentOutOfRangeException(nameof(property), property, null),
			};
		}

		sealed class ReadLine
		{
			public ReadLine(CascadeLine<FilterTarget> line, ImmutableArray<CascadeCondition<FilterTarget>> parts)
			{
				Line  = line;
				Parts = parts;
			}

			public CascadeLine<FilterTarget>                      Line  { get; }
			public ImmutableArray<CascadeCondition<FilterTarget>> Parts { get; }

			/// <summary>The line repeats an include of a line before it and is left out of the block.</summary>
			public bool Dropped { get; set; }
		}

		/// <summary>Where a condition was written, and the text of its item.</summary>
		readonly struct Origin
		{
			public Origin(int item, string text, TextSpan span, TextSpan valueSpan)
			{
				Item      = item;
				Text      = text;
				Span      = span;
				ValueSpan = valueSpan;
			}

			public int      Item      { get; }
			public string   Text      { get; }
			public TextSpan Span      { get; }

			/// <summary>The value of a key condition (<c>any</c>, a reference); the condition itself otherwise.</summary>
			public TextSpan ValueSpan { get; }

			public string Written => Text.Substring(Span.Start, Span.Length);
		}

		/// <summary>A condition on a fact: asks its matcher and gives the cascade a yes or a no.</summary>
		sealed class Leaf(Func<FilterTarget, Tri> match)
		{
			public bool Test(FilterTarget target)
			{
				return target.Settle(match(target));
			}
		}

		sealed class State(CascadeLevel level, bool overridesIsKey, ParameterModifierMatching modifierMatching)
		{
			readonly Dictionary<CascadeCondition<FilterTarget>, Origin>         _origins = [];
			readonly Dictionary<CascadeCondition<FilterTarget>, BindingAnchors> _anchors = [];

			public readonly List<FilterBlockDiagnostic> Diagnostics = [];
			public readonly List<ReadLine>              Lines       = [];

			public FactProperty Facts;

			int    _item;
			string _text = "";

			// ---- items and lines -----------------------------------------------------------------------------------

			public void ReadItem(int index, string text)
			{
				_item = index;
				_text = text;

				var reading = FilterV2Reader.Read(text, overridesIsKey);

				foreach (var diagnostic in reading.Diagnostics)
					Diagnostics.Add(new FilterBlockDiagnostic(diagnostic.Id, index, diagnostic.Span, diagnostic.Message));

				if (reading.Item is null)
					return;

				foreach (var line in reading.Item.Lines)
				{
					if (HasErrorIn(reading.Diagnostics, line.Span))
						continue;

					CascadeCondition<FilterTarget>? condition;

					try
					{
						condition = Convert(line.Expression);
					}
					catch (InsufficientExecutionStackException)
					{
						Diagnostics.Add(new FilterBlockDiagnostic(FilterV2DiagnosticID.NestedTooDeeply, index, line.Span, "The filter is nested too deeply to be read."));
						continue;
					}
					catch (PatternException refused)
					{
						// A pattern refused for what it is as a whole: its line is no declaration.
						Diagnostics.Add(new FilterBlockDiagnostic(refused.Id, index, refused.Span, refused.Message));
						continue;
					}

					// The reader reports every form it refuses, so a line that is no condition has an error already. No line
					// is left out in silence: one that has none gets its own.
					if (condition is null)
					{
						if (!reading.HasErrors)
							Diagnostics.Add(new FilterBlockDiagnostic(FilterV2DiagnosticID.SyntaxError, index, line.Span, UnreadLine));

						continue;
					}

					var cascadeLine = new CascadeLine<FilterTarget>(line.IsSubtraction, condition, new FilterLineOrigin(index, line, text.Substring(line.Span.Start, line.Span.Length)));

					if (CheckLine(cascadeLine) is { } parts)
						Lines.Add(new ReadLine(cascadeLine, parts));
				}
			}

			static bool HasErrorIn(ImmutableArray<FilterDiagnostic> diagnostics, TextSpan line)
			{
				// An error at the very end of a line is the line's: what it still expected there.
				foreach (var diagnostic in diagnostics)
					if (diagnostic.Span.Start >= line.Start && diagnostic.Span.Start <= line.End)
						return true;

				return false;
			}

			/// <summary>The checks of one line; its parts when it passes them.</summary>
			ImmutableArray<CascadeCondition<FilterTarget>>? CheckLine(CascadeLine<FilterTarget> line)
			{
				var ok = true;

				if (line.Condition is not CascadeAny<FilterTarget>)
				{
					foreach (var leaf in line.Condition.Leaves())
					{
						if (leaf is not CascadeAny<FilterTarget> any)
							continue;

						var name = any.Property.ToFilterName();

						Report(CascadeDiagnosticIds.InvalidAny, _origins[any].ValueSpan,
							$"'any' is a whole value: '{_origins[any].Written}' stands alone on a line, as the only include of '{name}' in the filter.");

						ok = false;
					}
				}

				foreach (var leaf in line.Condition.Leaves())
				{
					if (leaf is not CascadeReference<FilterTarget> reference || IsAllowed(reference.Level))
						continue;

					Report(CascadeDiagnosticIds.InvalidReference, _origins[reference].ValueSpan, level == CascadeLevel.Aspect
						? $"'{reference}': a default target filter is the weakest level and has nothing above it to reference."
						: $"'{reference}': an assembly filter can reference only the level above it; write 'aspect.{reference.Property.ToFilterName()}'.");

					ok = false;
				}

				if (!ok)
					return null;

				var parts = CascadeBlock<FilterTarget>.Split(line);

				foreach (var part in parts)
				{
					foreach (var leaf in part.Leaves())
					{
						if (leaf is not CascadeReference<FilterTarget> reference || part.Properties == reference.Property)
							continue;

						Report(CascadeDiagnosticIds.InvalidReference, _origins[reference].ValueSpan,
							$"'{reference}' stands in a condition that also speaks about {PropertyNames(part.Properties & ~reference.Property)}; " +
							"a reference stands only in a declaration of its own property.");

						ok = false;
					}
				}

				return ok ? parts : null;
			}

			bool IsAllowed(CascadeLevel reference)
			{
				return level switch
				{
					CascadeLevel.Aspect   => false,
					CascadeLevel.Assembly => reference == CascadeLevel.Aspect,
					_                     => true,
				};
			}

			/// <summary>
			/// A property is included once in a filter, over all of its items; a compound part counts as an include of each
			/// of its properties. The later include is reported and its line left out; what else that line includes still
			/// counts against the lines after it.
			/// </summary>
			public void CheckIncludes()
			{
				var seen = new List<CascadeCondition<FilterTarget>>();

				foreach (var line in Lines)
				{
					if (line.Line.IsSubtraction)
						continue;

					foreach (var part in line.Parts)
					{
						var first = seen.FirstOrDefault(p => p.Properties.Overlaps(part.Properties));

						if (first is null)
						{
							seen.Add(part);
							continue;
						}

						var (item, span) = Extent(part);
						var shared       = PropertyNames(first.Properties & part.Properties);

						if (first is CascadeAny<FilterTarget> || part is CascadeAny<FilterTarget>)
						{
							var any   = first is CascadeAny<FilterTarget> ? first : part;
							var other = ReferenceEquals(any, first) ? part : first;

							Diagnostics.Add(new FilterBlockDiagnostic(CascadeDiagnosticIds.InvalidAny, item, span,
								$"'{Describe(any)}' removes every restriction on {shared} and is its only include in a filter; '{Describe(other)}' includes it as well."));
						}
						else
						{
							var a = Describe(first);
							var b = Describe(part);

							Diagnostics.Add(new FilterBlockDiagnostic(CascadeDiagnosticIds.RepeatedInclude, item, span,
								$"A filter includes each property once: {shared} is included by '{a}' and again by '{b}'. " +
								$"Write them as one line: '{Operand(first, false)} or {Operand(part, false)}' (either) or '{Operand(first, true)} and {Operand(part, true)}' (both)."));
						}

						line.Dropped = true;
					}
				}
			}

			void Report(string id, TextSpan span, string message)
			{
				Diagnostics.Add(new FilterBlockDiagnostic(id, _item, span, message));
			}

			// ---- the text of a condition ---------------------------------------------------------------------------

			/// <summary>A condition as it can be written on one line: its leaves as written, joined as the condition joins them.</summary>
			string Describe(CascadeCondition<FilterTarget> condition)
			{
				switch (condition)
				{
					case CascadeJunction<FilterTarget> junction:
						return string.Join(junction.IsAnd ? " and " : " or ", junction.Operands.Select(o => Operand(o, junction.IsAnd)));

					case CascadeNot<FilterTarget> not:
						return "not " + Describe(not.Operand);

					default:
						return _origins.TryGetValue(condition, out var origin) ? origin.Written : condition.ToString()!;
				}
			}

			/// <summary>A condition as an operand of <c>and</c> or of <c>or</c>: in parentheses where it would read otherwise.</summary>
			string Operand(CascadeCondition<FilterTarget> condition, bool ofAnd)
			{
				var text = Describe(condition);

				return condition is CascadeJunction<FilterTarget> junction && junction.IsAnd != ofAnd && ofAnd ? "(" + text + ")" : text;
			}

			/// <summary>
			/// The characters a part of a line covers: the condition as written, or, for conditions of a line that the split
			/// brought together, from the first of them to the last.
			/// </summary>
			(int Item, TextSpan Span) Extent(CascadeCondition<FilterTarget> condition)
			{
				if (_origins.TryGetValue(condition, out var origin))
					return (origin.Item, origin.Span);

				var item  = 0;
				var start = int.MaxValue;
				var end   = 0;

				foreach (var operand in ((CascadeJunction<FilterTarget>)condition).Operands)
				{
					var (operandItem, span) = Extent(operand);

					item  = operandItem;
					start = Math.Min(start, span.Start);
					end   = Math.Max(end,   span.End);
				}

				return (item, TextSpan.FromBounds(start, end));
			}

			static string PropertyNames(CascadeProperty properties)
			{
				var names = new List<string>();

				foreach (CascadeProperty property in Enum.GetValues(typeof(CascadeProperty)))
					if (property != CascadeProperty.None && properties.Overlaps(property))
						names.Add("'" + property.ToFilterName() + "'");

				return string.Join(", ", names);
			}

			// ---- anchors -------------------------------------------------------------------------------------------

			public BindingAnchors Anchors(CascadeCondition<FilterTarget> condition)
			{
				switch (condition)
				{
					case CascadeJunction<FilterTarget> junction:
					{
						var anchors = junction.IsAnd ? BindingAnchors.Everything : BindingAnchors.Nothing;

						foreach (var operand in junction.Operands)
							anchors = junction.IsAnd ? anchors.And(Anchors(operand)) : anchors.Or(Anchors(operand));

						return anchors;
					}

					// The implicit declarations: an assembly filter that stays in the project, and "here" of a type or
					// method filter, which speaks about methods declared in the project only.
					case CascadeImplicit<FilterTarget>:
						return BindingAnchors.Project;

					// A negation, a reference and `any` say nothing that can be checked before binding.
					default:
						return _anchors.TryGetValue(condition, out var known) ? known : BindingAnchors.Everything;
				}
			}

			// ---- syntax to conditions ------------------------------------------------------------------------------

			CascadeCondition<FilterTarget>? Convert(FilterExpressionSyntax expression)
			{
				System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();

				switch (expression)
				{
					case OrExpressionSyntax or:
						return Junction(false, or.Operands, or.Span);

					case AndExpressionSyntax and:
						return and.OperatorErrors.IsEmpty ? Junction(true, and.Operands, and.Span) : null;

					case GroupExpressionSyntax group:
						return Convert(group.Expression);

					case NotExpressionSyntax not:
					{
						// `not` stands before one condition; the reader has refused anything else.
						var operand = Convert(not.Operand);

						if (operand is null or CascadeJunction<FilterTarget>)
							return null;

						var negation = new CascadeNot<FilterTarget>(operand);

						_origins[negation] = new Origin(_item, _text, not.Span, not.Span);

						return negation;
					}

					case KeyConditionSyntax condition:
						return Key(condition);

					case ReferenceExpressionSyntax reference:
						return Reference(reference.Reference, null, reference.Span);

					case WordRunSyntax run:
						return Words(run);

					default:
						return null;
				}
			}

			CascadeCondition<FilterTarget>? Junction(bool isAnd, ImmutableArray<FilterExpressionSyntax> operands, TextSpan span)
			{
				var conditions = ImmutableArray.CreateBuilder<CascadeCondition<FilterTarget>>(operands.Length);

				foreach (var operand in operands)
				{
					if (Convert(operand) is not { } condition)
						return null;

					conditions.Add(condition);
				}

				var junction = new CascadeJunction<FilterTarget>(isAnd, conditions.MoveToImmutable());

				_origins[junction] = new Origin(_item, _text, span, span);

				return junction;
			}

			CascadeCondition<FilterTarget>? Reference(ReferenceSyntax reference, CascadeProperty? keyProperty, TextSpan span)
			{
				if (!FilterV2Language.TryGetProperty(reference.Target, overridesIsKey, out var property))
					return null;

				var condition = new CascadeReference<FilterTarget>(
					reference.Level == FilterLevel.Aspect ? CascadeLevel.Aspect : CascadeLevel.Assembly,
					ToCascade(property),
					keyProperty);

				_origins[condition] = new Origin(_item, _text, span, reference.Span);

				return condition;
			}

			CascadeCondition<FilterTarget>? Key(KeyConditionSyntax condition)
			{
				var property = ToCascade(FilterV2Language.Property(condition.Key));

				switch (condition.Value)
				{
					case AnyValueSyntax any:
					{
						var result = new CascadeAny<FilterTarget>(property);

						_origins[result] = new Origin(_item, _text, condition.Span, any.Span);

						return result;
					}

					case ReferenceSyntax reference:
						return Reference(reference, property, condition.Span);
				}

				switch (condition.Key)
				{
					case FilterKey.Namespace when condition.Value is NamePatternSyntax name:
					{
						var pattern = PatternCompiler.CompileName(name, PatternCompiler.Anchoring.Rooted);

						return Test(property, FactProperty.Location, condition.Span, BindingAnchors.OfNamespace(name),
							t => pattern.Match(new SegmentPath(t.Facts.Namespace), t.Phase));
					}

					case FilterKey.Type when condition.Value is TypePatternSyntax type:
					{
						var pattern = PatternCompiler.CompileType(type);

						return Test(property, FactProperty.Location, condition.Span, BindingAnchors.OfType(type),
							t => pattern.Match(t.Facts.ContainingType, t.Phase));
					}

					case FilterKey.Method when condition.Value is NamePatternSyntax name:
						return Method(name, condition.Span);

					case FilterKey.Returns when condition.Value is ReturnsPatternSyntax returns:
					{
						var pattern = PatternCompiler.CompileReturns(returns);

						return Test(property, FactProperty.Returns, condition.Span, null,
							t => pattern.Match(t.Facts.ReturnType, t.Facts.ReturnRef, t.Phase));
					}

					case FilterKey.Params when condition.Value is ParameterListPatternSyntax list:
						return Parameters(list, condition.Span);

					case FilterKey.Param when condition.Value is ParameterPatternSyntax parameter:
					{
						if (parameter.Kind == ParameterPatternKind.AnyRun)
						{
							throw new PatternException(FilterV2DiagnosticID.InvalidPattern, parameter.Span,
								"'..' stands for a run of parameters in a parameter list ('params: (int, ..)'); 'param: _' is a method with at least one parameter.");
						}

						var pattern = PatternCompiler.CompileParameter(parameter, modifierMatching);

						return Test(property, FactProperty.Param, condition.Span, null, t =>
						{
							var best = Tri.False;

							foreach (var shape in t.Facts.Parameters)
							{
								best = best.Or(pattern.Match(shape, t.Phase));

								if (best == Tri.True)
									break;
							}

							return best;
						});
					}

					case FilterKey.Attribute when condition.Value is TypePatternSyntax type:
					{
						var pattern = PatternCompiler.CompileType(type);

						return Test(property, FactProperty.Attribute, condition.Span, null, t =>
						{
							foreach (var attribute in t.Facts.Attributes)
								if (pattern.MatchAttribute(attribute))
									return Tri.True;

							return Tri.False;
						});
					}

					case FilterKey.Inherits when condition.Value is TypePatternSyntax type:
					{
						var pattern = PatternCompiler.CompileType(type);

						return Test(property, FactProperty.Inherits, condition.Span, null, t =>
						{
							var best = Tri.False;

							foreach (var inherited in t.Facts.InheritedTypes)
							{
								best = best.Or(pattern.Match(inherited, t.Phase));

								if (best == Tri.True)
									break;
							}

							return best;
						});
					}

					case FilterKey.Implements when condition.Value is NamePatternSyntax name:
					{
						var pattern = PatternCompiler.CompileName(name, PatternCompiler.Anchoring.Trailing, isMethodPath: true);

						// The method itself counts, whatever it is. Where `overrides:` is no key of its own, `implements:`
						// covers the members the method overrides too (the first of which is the method).
						if (overridesIsKey)
						{
							return Test(property, FactProperty.Implements | FactProperty.Method, condition.Span, null, t =>
							{
								var itself = pattern.Match(new SegmentPath(t.Facts.ContainingType.Segments, t.Facts.Method), t.Phase);

								return itself == Tri.True ? itself : itself.Or(MatchAny(pattern, t.Facts.ImplementedMembers, t.Phase));
							});
						}

						return Test(property, FactProperty.Implements | FactProperty.Overrides, condition.Span, null, t =>
						{
							var implemented = MatchAny(pattern, t.Facts.ImplementedMembers, t.Phase);

							return implemented == Tri.True ? implemented : implemented.Or(MatchAny(pattern, t.Facts.OverrideChain, t.Phase));
						});
					}

					case FilterKey.Overrides when overridesIsKey && condition.Value is NamePatternSyntax name:
					{
						var pattern = PatternCompiler.CompileName(name, PatternCompiler.Anchoring.Trailing, isMethodPath: true);

						return Test(property, FactProperty.Overrides, condition.Span, null,
							t => MatchAny(pattern, t.Facts.OverrideChain, t.Phase));
					}

					default:
						return null;
				}
			}

			static Tri MatchAny(NamePatternSet pattern, ImmutableArray<MethodShape> methods, MatchPhase phase)
			{
				var best = Tri.False;

				foreach (var method in methods)
				{
					best = best.Or(pattern.Match(new SegmentPath(method.ContainingType.Segments, method.Method), phase));

					if (best == Tri.True)
						break;
				}

				return best;
			}

			CascadeCondition<FilterTarget> Method(NamePatternSyntax name, TextSpan span)
			{
				var pattern = PatternCompiler.CompileName(name, PatternCompiler.Anchoring.Trailing, isMethodPath: true);

				return Test(CascadeProperty.Method, FactProperty.Method, span, BindingAnchors.OfMethod(name),
					t => pattern.Match(new SegmentPath(t.Facts.ContainingType.Segments, t.Facts.Method), t.Phase));
			}

			CascadeCondition<FilterTarget> Parameters(ParameterListPatternSyntax list, TextSpan span)
			{
				var pattern = PatternCompiler.CompileParameters(list, modifierMatching);

				return Test(CascadeProperty.Params, FactProperty.Params, span, null,
					t => pattern.Match(t.Facts.Parameters, t.Phase));
			}

			/// <summary>A run of words and a compact signature: the conjunction of the conditions they are.</summary>
			CascadeCondition<FilterTarget>? Words(WordRunSyntax run)
			{
				var conditions = ImmutableArray.CreateBuilder<CascadeCondition<FilterTarget>>(run.ConditionCount);

				foreach (var word in run.Words)
				{
					if (word.Error is not null)
						return null;

					conditions.Add(Word(word));
				}

				if (run.Signature is { } signature)
				{
					conditions.Add(Method(signature.Name, signature.Name.Span));

					if (signature.Parameters is { } parameters)
						conditions.Add(Parameters(parameters, parameters.Span));

					if (signature.Returns is { } returns)
					{
						var pattern = new ReturnsPattern(FilterRefKind.Any, PatternCompiler.CompileType(returns));

						conditions.Add(Test(CascadeProperty.Returns, FactProperty.Returns, returns.Span, null,
							t => pattern.Match(t.Facts.ReturnType, t.Facts.ReturnRef, t.Phase)));
					}
				}

				if (conditions.Count == 0)
					return null;

				if (conditions.Count == 1)
					return conditions[0];

				var junction = new CascadeJunction<FilterTarget>(true, conditions.ToImmutable());

				_origins[junction] = new Origin(_item, _text, run.Span, run.Span);

				return junction;
			}

			CascadeCondition<FilterTarget> Word(WordSyntax word)
			{
				var property = ToCascade(FilterV2Language.Property(word.Word));

				switch (word.Word)
				{
					case FilterWord.Public:            return Accessible(Accessibility.Public);
					case FilterWord.Internal:          return Accessible(Accessibility.Internal);
					case FilterWord.Protected:         return Accessible(Accessibility.Protected);
					case FilterWord.Private:           return Accessible(Accessibility.Private);
					case FilterWord.ProtectedInternal: return Accessible(Accessibility.ProtectedOrInternal);
					case FilterWord.PrivateProtected:  return Accessible(Accessibility.ProtectedAndInternal);
					case FilterWord.Static:            return Test(property, FactProperty.Static,     word.Span, null, static t => t.Facts.IsStatic.ToTri());
					case FilterWord.Instance:          return Test(property, FactProperty.Static,     word.Span, null, static t => (!t.Facts.IsStatic).ToTri());
					case FilterWord.Virtual:           return Virtual(Virtuality.Virtual);
					case FilterWord.Override:          return Virtual(Virtuality.Override);
					case FilterWord.Abstract:          return Virtual(Virtuality.Abstract);
					case FilterWord.Sealed:            return Virtual(Virtuality.Sealed);
					case FilterWord.Extern:            return Test(property, FactProperty.Extern,     word.Span, null, static t => t.Facts.IsExtern.ToTri());
					case FilterWord.Extension:         return Test(property, FactProperty.Extension,  word.Span, null, static t => t.Facts.IsExtension.ToTri());
					case FilterWord.Generated:         return Test(property, FactProperty.Generated,  word.Span, null, static t => t.Facts.IsGenerated.ToTri());
					case FilterWord.Project:           return Test(property, FactProperty.Origin,     word.Span, BindingAnchors.Project, static t => t.Facts.IsProject.ToTri());

					// What a call returns can differ from what the definition returns: then the definition does not answer.
					case FilterWord.Async:
						return Test(property, FactProperty.Async, word.Span, null,
							static t => t.Phase == MatchPhase.Definition && t.Facts.IsAsyncDecidedByCall ? Tri.Depends : t.Facts.IsAsync.ToTri());

					default:
						throw new ArgumentOutOfRangeException(nameof(word), word.Word, null);
				}

				CascadeCondition<FilterTarget> Accessible(Accessibility accessibility)
				{
					return Test(property, FactProperty.Accessibility, word.Span, null, t => (t.Facts.Accessibility == accessibility).ToTri());
				}

				CascadeCondition<FilterTarget> Virtual(Virtuality virtuality)
				{
					return Test(property, FactProperty.Virtuality, word.Span, null, t => ((t.Facts.Virtuality & virtuality) != 0).ToTri());
				}
			}

			CascadeCondition<FilterTarget> Test(CascadeProperty property, FactProperty facts, TextSpan span, BindingAnchors? anchors, Func<FilterTarget, Tri> match)
			{
				var condition = new CascadeTest<FilterTarget>(property, _text.Substring(span.Start, span.Length), new Leaf(match).Test);

				_origins[condition] = new Origin(_item, _text, span, span);

				if (anchors is not null)
					_anchors[condition] = anchors;

				Facts |= facts;

				return condition;
			}
		}
	}
}
