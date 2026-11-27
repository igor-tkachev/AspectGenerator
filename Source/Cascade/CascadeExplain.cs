using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AspectGenerator.Cascade
{
	/// <summary>
	/// Why a method gets an aspect or not: every combination of blocks the cascade considered for it, with the
	/// declaration that decided each property, and where each setting came from. It is built from the same plan and the
	/// same evaluation as <see cref="CascadeDecision"/>, so <see cref="Selected"/>, <see cref="Family"/>,
	/// <see cref="Settings"/> and <see cref="Conflicts"/> are what the decision says.
	/// </summary>
	sealed class CascadeExplanation
	{
		/// <summary>The length <see cref="ToTable()"/> keeps to.</summary>
		public const int DefaultTableLength = 16 * 1024;

		public CascadeExplanation(
			bool                                       selected,
			CascadeFamily                              family,
			ImmutableDictionary<string,CascadeSetting> settings,
			ImmutableArray<CascadeSettingConflict>     conflicts,
			ImmutableArray<CascadeCombination>         combinations,
			ImmutableArray<CascadeDiagnostic>          diagnostics,
			CascadeExplanation?                        library)
		{
			Selected     = selected;
			Family       = family;
			Settings     = settings;
			Conflicts    = conflicts;
			Combinations = combinations;
			Diagnostics  = diagnostics;
			Library      = library;
		}

		public bool                                       Selected     { get; }
		public CascadeFamily                              Family       { get; }
		public ImmutableDictionary<string,CascadeSetting> Settings     { get; }
		public ImmutableArray<CascadeSettingConflict>     Conflicts    { get; }

		/// <summary>
		/// The combinations considered, in the order the cascade goes through them: per method block, per type block, per
		/// assembly block. Empty when no assembly, type or method block covers the method, or when <see cref="Diagnostics"/>
		/// turned the aspect off.
		/// </summary>
		public ImmutableArray<CascadeCombination> Combinations { get; }

		/// <summary>The filter errors that turned the aspect off; empty when the cascade ran.</summary>
		public ImmutableArray<CascadeDiagnostic> Diagnostics { get; }

		/// <summary>
		/// For a method a library placed the aspect on, when the library's own blocks did not select it: what they
		/// decided. The rest of this explanation is then the compilation's assembly blocks deciding.
		/// </summary>
		public CascadeExplanation? Library { get; }

		internal CascadeExplanation WithLibrary(CascadeExplanation library)
		{
			return new(Selected, Family, Settings, Conflicts, Combinations, Diagnostics, library);
		}

		/// <summary>The explanation as plain ASCII text, at most <see cref="DefaultTableLength"/> characters.</summary>
		public string ToTable()
		{
			return ToTable(DefaultTableLength);
		}

		/// <summary>
		/// The explanation as plain ASCII text: one table per combination, property, deciding block and value, in a stable
		/// order. Text longer than <paramref name="maxLength"/> is cut at a line end and says how much was left out.
		/// </summary>
		public string ToTable(int maxLength)
		{
			if (maxLength < CascadeExplainText.MinLength)
				throw new ArgumentOutOfRangeException(nameof(maxLength), maxLength, $"The table needs at least {CascadeExplainText.MinLength} characters.");

			var text = new StringBuilder();

			CascadeExplainText.Write(text, this);

			return CascadeExplainText.Truncate(text.ToString(), maxLength);
		}

		public override string ToString()
		{
			return ToTable();
		}
	}

	/// <summary>One combination of blocks, one per level that has blocks for the method, and how it decided.</summary>
	sealed class CascadeCombination
	{
		public CascadeCombination(
			ImmutableArray<CascadeBlockRef>                 blocks,
			bool                                            selected,
			ImmutableArray<CascadeDeclarationExplanation>   declarations,
			ImmutableArray<CascadePropertyExplanation>      properties,
			ImmutableArray<CascadeSettingSource>            settings)
		{
			Blocks       = blocks;
			Selected     = selected;
			Declarations = declarations;
			Properties   = properties;
			Settings     = settings;
		}

		/// <summary>The blocks of the combination, the most specific first; the aspect's default is last.</summary>
		public ImmutableArray<CascadeBlockRef> Blocks { get; }

		/// <summary>Whether every declaration that applies in the combination is satisfied.</summary>
		public bool Selected { get; }

		/// <summary>The declarations that apply in the combination, block by block from the most specific.</summary>
		public ImmutableArray<CascadeDeclarationExplanation> Declarations { get; }

		/// <summary>
		/// Per property some applying declaration speaks about, in the order of <see cref="CascadeProperty"/>: the
		/// declarations that decide it. A property not listed is not restricted.
		/// </summary>
		public ImmutableArray<CascadePropertyExplanation> Properties { get; }

		/// <summary>Per setting any block of the aspect writes, by name: where the combination takes it from.</summary>
		public ImmutableArray<CascadeSettingSource> Settings { get; }
	}

	/// <summary>A block as an explanation names it.</summary>
	sealed record CascadeBlockRef(object Block, string Label, CascadeLevel Level, object? Source)
	{
		public override string ToString()
		{
			return Label;
		}
	}

	sealed record CascadePropertyExplanation(CascadeProperty Property, ImmutableArray<CascadeDeclarationExplanation> Declarations);

	/// <summary>
	/// A declaration and its value for the method. <see cref="Satisfied"/> is what the cascade needs of it: an include
	/// whose condition holds, a subtraction whose condition does not.
	/// </summary>
	sealed record CascadeDeclarationExplanation(
		object                                      Declaration,
		CascadeBlockRef                             Block,
		CascadeProperty                             Properties,
		string                                      Text,
		object?                                     Source,
		bool                                        IsImplicit,
		bool                                        IsSubtraction,
		bool                                        IsAny,
		bool                                        Satisfied,
		ImmutableArray<CascadeReferenceExplanation> References);

	/// <summary>
	/// What an <c>aspect.p</c> or <c>assembly.p</c> in a declaration stands for in its combination: the declarations of
	/// <c>p</c> at and above that level, and their value. No declarations means no restriction.
	/// </summary>
	sealed record CascadeReferenceExplanation(
		string                                        Text,
		CascadeLevel                                  Level,
		CascadeProperty                               Property,
		bool                                          Value,
		ImmutableArray<CascadeDeclarationExplanation> Declarations);

	/// <summary>Where a combination takes a setting from: the most specific of its blocks that writes it, or the aspect's default (<see cref="Block"/> is null).</summary>
	sealed record CascadeSettingSource(string Name, object? Value, CascadeBlockRef? Block);

	sealed partial class CascadeEvaluator<TFact>
	{
		/// <summary>Explains <see cref="Decide(TFact, CascadeContext, IReadOnlyList{CascadeBlock{TFact}})"/> for the same arguments.</summary>
		public CascadeExplanation Explain(TFact fact, CascadeContext context, IReadOnlyList<CascadeBlock<TFact>> methodBlocks)
		{
			if (context.Evaluator != this || context.Family != CascadeFamily.Consumer)
				throw new ArgumentException("The context is not a consumer context of this evaluator.", nameof(context));

			foreach (var block in methodBlocks)
				Expect(block, CascadeLevel.Method);

			if (SelectsNothing)
				return TurnedOff();

			return context.Explain(fact, methodBlocks);
		}

		/// <summary>Explains a method of the compilation; plans its type's context for this one call.</summary>
		public CascadeExplanation Explain(TFact fact, IReadOnlyList<CascadeBlock<TFact>> typeBlocks, IReadOnlyList<CascadeBlock<TFact>> methodBlocks)
		{
			return Explain(fact, Prepare(typeBlocks), methodBlocks);
		}

		/// <summary>Explains <see cref="DecideLibraryMethod(TFact, CascadeContext, IReadOnlyList{CascadeBlock{TFact}}, TFact)"/> for the same arguments.</summary>
		public CascadeExplanation ExplainLibraryMethod(
			TFact                              libraryFact,
			CascadeContext                     libraryContext,
			IReadOnlyList<CascadeBlock<TFact>> libraryMethodBlocks,
			TFact                              consumerFact)
		{
			if (libraryContext.Evaluator != this || libraryContext.Family != CascadeFamily.Library)
				throw new ArgumentException("The context is not a library context of this evaluator.", nameof(libraryContext));

			if (SelectsNothing)
				return TurnedOff();

			var library = libraryContext.Explain(libraryFact, [..Readable(libraryMethodBlocks).Select(b => Expect(b, CascadeLevel.Method))]);

			if (library.Selected)
				return library;

			_consumerAssemblyOnly ??= Prepare([]);

			return _consumerAssemblyOnly.Explain(consumerFact, []).WithLibrary(library);
		}

		/// <summary>As the overload with a context; plans the library type's context for this one call.</summary>
		public CascadeExplanation ExplainLibraryMethod(
			TFact                              libraryFact,
			IReadOnlyList<CascadeBlock<TFact>> libraryAssemblyBlocks,
			IReadOnlyList<CascadeBlock<TFact>> libraryTypeBlocks,
			IReadOnlyList<CascadeBlock<TFact>> libraryMethodBlocks,
			TFact                              consumerFact)
		{
			return ExplainLibraryMethod(libraryFact, PrepareLibrary(libraryAssemblyBlocks, libraryTypeBlocks), libraryMethodBlocks, consumerFact);
		}

		CascadeExplanation TurnedOff()
		{
			return new(false, CascadeFamily.Consumer, ImmutableDictionary<string,CascadeSetting>.Empty,
				ImmutableArray<CascadeSettingConflict>.Empty, ImmutableArray<CascadeCombination>.Empty, Diagnostics, null);
		}

		internal sealed partial class CascadeContext
		{
			/// <summary>
			/// Goes through the combinations <see cref="Decide"/> goes through, evaluating every declaration that applies
			/// (not only up to the first that fails): the same declarations apply, references resolve through the same
			/// scope and settings go through the same collector, so that the outcome is the decision's.
			/// </summary>
			public CascadeExplanation Explain(TFact fact, IReadOnlyList<CascadeBlock<TFact>> methodBlocks)
			{
				var family = Family;

				if (_assembly.IsEmpty && _types.IsEmpty && methodBlocks.Count == 0)
					return new(false, family, ImmutableDictionary<string,CascadeSetting>.Empty,
						ImmutableArray<CascadeSettingConflict>.Empty, ImmutableArray<CascadeCombination>.Empty,
						ImmutableArray<CascadeDiagnostic>.Empty, null);

				var scope        = new Scope(this, fact);
				var settings     = methodBlocks.Count == 0 ? _settingNames : [.._settingNames.Concat(methodBlocks.SelectMany(b => b.Settings.Keys)).Distinct()];
				var collector    = settings.Length == 0 ? null : new SettingCollector(settings, _assembly, _default);
				var combinations = ImmutableArray.CreateBuilder<CascadeCombination>();
				var refs         = new Dictionary<CascadeBlock<TFact>,CascadeBlockRef>();
				var selected     = false;
				var mCount       = Math.Max(1, methodBlocks.Count);
				var tCount       = Math.Max(1, _types.Length);
				var aCount       = Math.Max(1, _assembly.Length);

				for (var mi = 0; mi < mCount; mi++)
				{
					var m = methodBlocks.Count == 0 ? null : methodBlocks[mi];

					for (var ti = 0; ti < tCount; ti++)
					{
						var t  = _types.IsEmpty ? null : _types[ti];
						var ok = _assembly.IsEmpty ? null : new bool[_assembly.Length];
						var on = false;

						for (var a = 0; a < aCount; a++)
						{
							var combination = Combination(scope, refs, settings, m, t, _assembly.IsEmpty ? -1 : a);

							combinations.Add(combination);

							if (!combination.Selected)
								continue;

							on = true;

							if (ok is not null)
								ok[a] = true;
						}

						if (on)
						{
							selected = true;
							collector?.Add(m, t, ok);
						}
					}
				}

				var merged    = ImmutableDictionary<string,CascadeSetting>.Empty;
				var conflicts = ImmutableArray<CascadeSettingConflict>.Empty;

				if (selected && collector is not null)
					(merged, conflicts) = collector.Result();

				return new(selected, family, merged, conflicts, combinations.ToImmutable(),
					ImmutableArray<CascadeDiagnostic>.Empty, null);
			}

			CascadeCombination Combination(
				Scope                                           scope,
				Dictionary<CascadeBlock<TFact>,CascadeBlockRef> refs,
				string[]                                        settings,
				CascadeBlock<TFact>?                            m,
				CascadeBlock<TFact>?                            t,
				int                                             a)
			{
				var blocks       = ImmutableArray.CreateBuilder<CascadeBlockRef>(4);
				var declarations = ImmutableArray.CreateBuilder<CascadeDeclarationExplanation>();
				var assembly     = a < 0 ? null : _assembly[a];
				var selected     = true;

				// The declarations that apply are those of the parts Decide evaluates: all of the method block's, then of
				// each block above those that declare nothing the blocks below it declare. They are selected here as
				// GetPart selects them, without planning parts: an explanation leaves the context as it found it.
				var below = CascadeProperty.None;

				if (m is not null)
				{
					Add(m, m.Declarations);
					below = m.Declared;
				}

				if (t is not null)
				{
					Add(t, Applying(t, below));
					below |= t.Declared;
				}

				if (assembly is not null)
				{
					Add(assembly, Applying(assembly, below));
					below |= assembly.Declared;
				}

				Add(_default, Applying(_default, below));

				var properties = ImmutableArray.CreateBuilder<CascadePropertyExplanation>();

				foreach (CascadeProperty property in Enum.GetValues(typeof(CascadeProperty)))
				{
					if (property == CascadeProperty.None)
						continue;

					var deciding = declarations.Where(d => d.Properties.Overlaps(property)).ToImmutableArray();

					if (!deciding.IsEmpty)
						properties.Add(new(property, deciding));
				}

				var sources = ImmutableArray.CreateBuilder<CascadeSettingSource>(settings.Length);

				foreach (var name in settings.OrderBy(n => n, StringComparer.Ordinal))
				{
					var writer = Writer(m, name) ?? Writer(t, name) ?? Writer(assembly, name) ?? Writer(_default, name);

					sources.Add(writer is null ? new(name, null, null) : new(name, writer.Settings[name], Ref(refs, writer)));
				}

				return new(blocks.ToImmutable(), selected, declarations.ToImmutable(), properties.ToImmutable(), sources.MoveToImmutable());

				void Add(CascadeBlock<TFact> block, ImmutableArray<CascadeDeclaration<TFact>> applying)
				{
					blocks.Add(Ref(refs, block));

					foreach (var declaration in applying)
					{
						var explained = Explain(scope, refs, declaration, a);

						selected &= explained.Satisfied;
						declarations.Add(explained);
					}
				}

				static ImmutableArray<CascadeDeclaration<TFact>> Applying(CascadeBlock<TFact> block, CascadeProperty below)
				{
					return block.Declarations.Where(d => !d.Properties.Overlaps(below)).ToImmutableArray();
				}

				static CascadeBlock<TFact>? Writer(CascadeBlock<TFact>? block, string name)
				{
					return block is not null && block.Settings.ContainsKey(name) ? block : null;
				}
			}

			CascadeDeclarationExplanation Explain(
				Scope                                           scope,
				Dictionary<CascadeBlock<TFact>,CascadeBlockRef> refs,
				CascadeDeclaration<TFact>                       declaration,
				int                                             assembly)
			{
				var references = ImmutableArray.CreateBuilder<CascadeReferenceExplanation>();

				foreach (var leaf in declaration.Condition.Leaves())
				{
					if (leaf is not CascadeReference<TFact> reference)
						continue;

					// A reference resolves on the chain cut at its level, as Scope.Resolve reads it.
					var cut     = reference.Level == CascadeLevel.Assembly && assembly >= 0 ? 1 + assembly : 0;
					var expands = ImmutableArray.CreateBuilder<CascadeDeclarationExplanation>();

					foreach (var part in _cuts[cut])
						foreach (var above in part.Declarations)
							if (above.Properties.Overlaps(reference.Property))
								expands.Add(Explain(scope, refs, above, -1));

					references.Add(new(reference.ToString(), reference.Level, reference.Property,
						scope.ResolveFor(reference.Level, reference.Property, assembly), expands.ToImmutable()));
				}

				return new(
					declaration,
					Ref(refs, declaration.Block),
					declaration.Properties,
					declaration.ToString(),
					declaration.Line?.Source ?? declaration.Block.Source,
					declaration.IsImplicit,
					declaration.IsSubtraction,
					declaration.IsAny,
					scope.IsSatisfied(declaration, assembly),
					references.ToImmutable());
			}

			static CascadeBlockRef Ref(Dictionary<CascadeBlock<TFact>,CascadeBlockRef> refs, CascadeBlock<TFact> block)
			{
				if (!refs.TryGetValue(block, out var r))
					refs.Add(block, r = new(block, block.Label, block.Level, block.Source));

				return r;
			}

			sealed partial class Scope
			{
				/// <summary>Whether the declaration is satisfied in a chain through assembly block <paramref name="assembly"/> (-1: none).</summary>
				public bool IsSatisfied(CascadeDeclaration<TFact> declaration, int assembly)
				{
					var saved = _assembly;

					_assembly = assembly;

					var ok = declaration.IsSatisfied(_fact, this);

					_assembly = saved;

					return ok;
				}

				/// <summary>The value of a level reference in a chain through assembly block <paramref name="assembly"/> (-1: none).</summary>
				public bool ResolveFor(CascadeLevel level, CascadeProperty property, int assembly)
				{
					var saved = _assembly;

					_assembly = assembly;

					var ok = Resolve(level, property, _fact);

					_assembly = saved;

					return ok;
				}
			}
		}
	}

	/// <summary>The text form of <see cref="CascadeExplanation"/>.</summary>
	static class CascadeExplainText
	{
		public const int MinLength = 128;

		public static void Write(StringBuilder text, CascadeExplanation explanation)
		{
			if (explanation.Library is { } library)
			{
				text.Append("library blocks:\n");
				Write(text, library);
				text.Append("then the assembly blocks of the compilation:\n");
			}

			text.Append("family: ").Append(explanation.Family == CascadeFamily.Library ? "library" : "consumer")
				.Append("; selected: ").Append(explanation.Selected ? "yes" : "no").Append('\n');

			if (!explanation.Diagnostics.IsEmpty)
			{
				text.Append("the aspect is off: its filters have errors\n");

				foreach (var d in explanation.Diagnostics)
					text.Append("  ").Append(d.Id).Append(' ').Append(Ascii(d.Block?.ToString() ?? "")).Append(": ").Append(Ascii(d.Message)).Append('\n');

				return;
			}

			if (explanation.Combinations.IsEmpty)
			{
				text.Append("no assembly, type or method block covers the method\n");
				return;
			}

			var count = explanation.Combinations.Length;

			for (var i = 0; i < count; i++)
				WriteCombination(text, explanation.Combinations[i], i + 1, count);

			foreach (var setting in explanation.Settings.OrderBy(s => s.Key, StringComparer.Ordinal))
				text.Append("setting ").Append(Ascii(setting.Key)).Append(" = ").Append(Value(setting.Value.Value))
					.Append(" from ").Append(Ascii(setting.Value.Block.ToString() ?? "")).Append('\n');

			foreach (var conflict in explanation.Conflicts.OrderBy(c => c.Name, StringComparer.Ordinal))
			{
				text.Append("conflict ").Append(CascadeDiagnosticIds.SettingsConflict).Append(' ').Append(Ascii(conflict.Name)).Append(':');

				foreach (var (value, blocks) in conflict.Values)
				{
					text.Append(' ').Append(Value(value)).Append(" from ");
					text.Append(string.Join(", ", blocks.Select(b => Ascii(b.ToString() ?? ""))));
					text.Append(';');
				}

				text.Length--;
				text.Append(" (the setting takes the aspect's default)\n");
			}
		}

		static void WriteCombination(StringBuilder text, CascadeCombination combination, int number, int count)
		{
			text.Append("combination ").Append(number).Append('/').Append(count).Append(": ")
				.Append(string.Join(" > ", combination.Blocks.Select(b => Ascii(b.Label))))
				.Append(" -> ").Append(combination.Selected ? "selected" : "not selected").Append('\n');

			var rows = new List<(string Property, CascadeDeclarationExplanation Declaration)>();

			foreach (var p in combination.Properties)
				foreach (var d in p.Declarations)
					rows.Add((p.Property.ToFilterName(), d));

			var propertyWidth = Math.Max("property".Length, rows.Count == 0 ? 0 : rows.Max(r => r.Property.Length));
			var blockWidth    = Math.Max("block".Length,    rows.Count == 0 ? 0 : rows.Max(r => Ascii(r.Declaration.Block.Label).Length));

			if (rows.Count == 0)
			{
				text.Append("  no declarations\n");
			}
			else
			{
				text.Append("  ").Append("property".PadRight(propertyWidth)).Append("  ").Append("block".PadRight(blockWidth))
					.Append("  level     result  declaration\n");

				foreach (var (property, d) in rows)
				{
					text.Append("  ").Append(property.PadRight(propertyWidth)).Append("  ").Append(Ascii(d.Block.Label).PadRight(blockWidth)).Append("  ");
					WriteDeclaration(text, d, withBlock: false);

					foreach (var r in d.References)
						WriteReference(text, r, 6 + propertyWidth + blockWidth);
				}
			}

			foreach (var s in combination.Settings)
				text.Append("  setting ").Append(Ascii(s.Name)).Append(" = ")
					.Append(s.Block is null ? "the aspect's default" : Value(s.Value) + " from " + Ascii(s.Block.Label)).Append('\n');
		}

		static void WriteDeclaration(StringBuilder text, CascadeDeclarationExplanation d, bool withBlock)
		{
			if (withBlock)
				text.Append(Ascii(d.Block.Label)).Append(": ");

			text.Append(Level(d.Block.Level).PadRight(8)).Append("  ").Append((d.Satisfied ? "ok" : "fails").PadRight(6)).Append("  ")
				.Append(Ascii(d.Text));

			if (d.Source is not null)
				text.Append("  at ").Append(Ascii(d.Source.ToString() ?? ""));

			text.Append('\n');
		}

		static void WriteReference(StringBuilder text, CascadeReferenceExplanation r, int indent)
		{
			text.Append(' ', indent).Append(Ascii(r.Text)).Append(" = ").Append(r.Value ? "true" : "false");

			if (r.Declarations.IsEmpty)
			{
				text.Append(", no restriction\n");
				return;
			}

			text.Append(", from:\n");

			foreach (var d in r.Declarations)
			{
				text.Append(' ', indent + 2);
				WriteDeclaration(text, d, withBlock: true);

				foreach (var inner in d.References)
					WriteReference(text, inner, indent + 4);
			}
		}

		static string Level(CascadeLevel level)
		{
			return level.ToString().ToLowerInvariant();
		}

		/// <summary>A setting value as text: strings quoted, sequences in brackets, numbers in the invariant culture.</summary>
		public static string Value(object? value)
		{
			switch (value)
			{
				case null:
					return "null";
				case string s:
					return "\"" + Ascii(s) + "\"";
				case bool b:
					return b ? "true" : "false";
				case IEnumerable items:
					return "[" + string.Join(", ", items.Cast<object?>().Select(Value)) + "]";
				case IFormattable f:
					return Ascii(f.ToString(null, CultureInfo.InvariantCulture));
				default:
					return Ascii(value.ToString() ?? "");
			}
		}

		/// <summary>Keeps printable ASCII; any other character is written as <c>\uXXXX</c>.</summary>
		public static string Ascii(string text)
		{
			var plain = true;

			foreach (var c in text)
			{
				if (c < ' ' || c > '~')
				{
					plain = false;
					break;
				}
			}

			if (plain)
				return text;

			var result = new StringBuilder(text.Length + 16);

			foreach (var c in text)
			{
				if (c < ' ' || c > '~')
					result.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
				else
					result.Append(c);
			}

			return result.ToString();
		}

		/// <summary>Cuts text longer than <paramref name="maxLength"/> at a line end and says how many characters were left out.</summary>
		public static string Truncate(string text, int maxLength)
		{
			if (text.Length <= maxLength)
				return text;

			// The note is longest with the most digits: size the room for it by the whole text's length.
			var room = maxLength - Note(text.Length).Length;
			var end  = text.LastIndexOf('\n', room - 1);
			var kept = end < 0 ? room : end + 1;

			return text.Substring(0, kept) + Note(text.Length - kept);

			static string Note(int left)
			{
				return "... " + left.ToString(CultureInfo.InvariantCulture) + " more characters left out\n";
			}
		}
	}
}
