using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace AspectGenerator.Cascade
{
	/// <summary>A setting a method gets, and the block that wrote it.</summary>
	sealed record CascadeSetting(object? Value, object Block);

	/// <summary>Two or more selecting combinations write different values for one setting.</summary>
	sealed record CascadeSettingConflict(string Name, ImmutableArray<(object? Value, ImmutableArray<object> Blocks)> Values);

	enum CascadeFamily
	{
		/// <summary>The blocks of the compilation being built decided.</summary>
		Consumer,
		/// <summary>The blocks of the library that placed the aspect on its own method decided.</summary>
		Library,
	}

	sealed class CascadeDecision
	{
		public static readonly CascadeDecision NotSelected = new(false, CascadeFamily.Consumer,
			ImmutableDictionary<string,CascadeSetting>.Empty, ImmutableArray<CascadeSettingConflict>.Empty);

		public CascadeDecision(
			bool                                          selected,
			CascadeFamily                                 family,
			ImmutableDictionary<string,CascadeSetting>    settings,
			ImmutableArray<CascadeSettingConflict>        conflicts)
		{
			Selected  = selected;
			Family    = family;
			Settings  = settings;
			Conflicts = conflicts;
		}

		public bool          Selected { get; }
		public CascadeFamily Family   { get; }

		/// <summary>
		/// The settings the selecting combinations agree on, by name; a setting none of them writes is absent and takes
		/// the aspect's default. A setting in <see cref="Conflicts"/> is absent too.
		/// </summary>
		public ImmutableDictionary<string,CascadeSetting> Settings { get; }

		public ImmutableArray<CascadeSettingConflict> Conflicts { get; }
	}

	/// <summary>
	/// Decides, for one aspect, whether a method gets it and with which settings, from the blocks of the aspect and the
	/// facts of the method. The evaluator is pure: it reads only the blocks it is given and what the conditions read from
	/// the fact.
	/// </summary>
	/// <remarks>
	/// For a method, a combination (chain) takes one block per level that has blocks for it: the aspect's default, one
	/// assembly block, one block of the method's own type, one block on the method. Each property is decided by the most
	/// specific block of the chain that declares it; a compound declaration applies only while no more specific block
	/// declares any of its properties. The chain selects when every declaration that applies is satisfied and it holds a
	/// block below the aspect's default. The method gets the aspect when any chain selects it; its settings are those of
	/// the selecting chains, each from the most specific block of its chain that writes it, and selecting chains that
	/// disagree on a setting are a conflict.
	/// </remarks>
	sealed class CascadeEvaluator<TFact>
	{
		readonly CascadeBlock<TFact>                 _default;
		readonly ImmutableArray<CascadeBlock<TFact>> _assembly;
		readonly Func<TFact,bool>                    _isProject;

		/// <param name="aspectDefault">The aspect's <c>DefaultTargetFilter</c> (a block without lines when it has none).</param>
		/// <param name="assemblyBlocks">The assembly blocks of the compilation.</param>
		/// <param name="otherBlocks">
		/// Every other block of the aspect: of types, of methods and of libraries. They are read only for their errors, since
		/// an error in any filter of an aspect turns the aspect off.
		/// </param>
		/// <param name="isProject">Whether a method is declared in the compilation whose blocks are evaluated.</param>
		public CascadeEvaluator(
			CascadeBlock<TFact>              aspectDefault,
			IEnumerable<CascadeBlock<TFact>> assemblyBlocks,
			IEnumerable<CascadeBlock<TFact>> otherBlocks,
			Func<TFact,bool>                 isProject)
		{
			_default   = Expect(aspectDefault, CascadeLevel.Aspect);
			_assembly  = [..assemblyBlocks.Select(b => Expect(b, CascadeLevel.Assembly))];
			_isProject = isProject;

			Diagnostics = [.._default.Diagnostics, .._assembly.SelectMany(b => b.Diagnostics), ..otherBlocks.SelectMany(b => b.Diagnostics)];
		}

		/// <summary>The errors of every block. Any of them turns the aspect off.</summary>
		public ImmutableArray<CascadeDiagnostic> Diagnostics { get; }

		public bool SelectsNothing => !Diagnostics.IsEmpty;

		/// <summary>Decides a method from the compilation's blocks.</summary>
		/// <param name="typeBlocks">The blocks on the method's own type.</param>
		/// <param name="methodBlocks">The blocks on the method.</param>
		public CascadeDecision Decide(TFact fact, IReadOnlyList<CascadeBlock<TFact>> typeBlocks, IReadOnlyList<CascadeBlock<TFact>> methodBlocks)
		{
			if (SelectsNothing)
				return CascadeDecision.NotSelected;

			return DecideFamily(fact, _assembly, typeBlocks, methodBlocks, CascadeFamily.Consumer);
		}

		/// <summary>
		/// Decides a method of a referenced library that places the aspect on it. The library's own blocks decide first,
		/// as in the library's own build (<paramref name="libraryFact"/> sees the method as declared in the project); if
		/// they do not select it, the compilation's assembly blocks decide as for any referenced method.
		/// </summary>
		public CascadeDecision DecideLibraryMethod(
			TFact                             libraryFact,
			IReadOnlyList<CascadeBlock<TFact>> libraryAssemblyBlocks,
			IReadOnlyList<CascadeBlock<TFact>> libraryTypeBlocks,
			IReadOnlyList<CascadeBlock<TFact>> libraryMethodBlocks,
			TFact                             consumerFact)
		{
			if (SelectsNothing)
				return CascadeDecision.NotSelected;

			var library = DecideFamily(libraryFact, libraryAssemblyBlocks, libraryTypeBlocks, libraryMethodBlocks, CascadeFamily.Library);

			if (library.Selected)
				return library;

			return DecideFamily(consumerFact, _assembly, [], [], CascadeFamily.Consumer);
		}

		CascadeDecision DecideFamily(
			TFact                              fact,
			IReadOnlyList<CascadeBlock<TFact>> assemblyBlocks,
			IReadOnlyList<CascadeBlock<TFact>> typeBlocks,
			IReadOnlyList<CascadeBlock<TFact>> methodBlocks,
			CascadeFamily                      family)
		{
			var levels = new List<IReadOnlyList<CascadeBlock<TFact>>>(4) { new[] { _default } };

			foreach (var (blocks, level) in new[] { (assemblyBlocks, CascadeLevel.Assembly), (typeBlocks, CascadeLevel.Type), (methodBlocks, CascadeLevel.Method) })
			{
				foreach (var block in blocks)
					Expect(block, level);

				if (blocks.Count > 0)
					levels.Add(blocks);
			}

			if (levels.Count == 1)
				return CascadeDecision.NotSelected;

			var selecting = new List<CascadeBlock<TFact>[]>();
			var indexes   = new int[levels.Count];

			while (true)
			{
				var chain = new CascadeBlock<TFact>[levels.Count];

				for (var i = 0; i < chain.Length; i++)
					chain[i] = levels[i][indexes[i]];

				if (new Chain(chain, chain.Length, _isProject).Selects(fact))
					selecting.Add(chain);

				var k = levels.Count - 1;

				while (k >= 0 && ++indexes[k] == levels[k].Count)
					indexes[k--] = 0;

				if (k < 0)
					break;
			}

			if (selecting.Count == 0)
				return CascadeDecision.NotSelected;

			return MergeSettings(selecting, family);
		}

		static CascadeDecision MergeSettings(List<CascadeBlock<TFact>[]> selecting, CascadeFamily family)
		{
			var perChain = selecting.Select(ChainSettings).ToList();
			var names    = perChain.SelectMany(s => s.Keys).Distinct().OrderBy(n => n, StringComparer.Ordinal);
			var settings  = ImmutableDictionary.CreateBuilder<string,CascadeSetting>();
			var conflicts = ImmutableArray.CreateBuilder<CascadeSettingConflict>();

			foreach (var name in names)
			{
				var values = new List<(object? Value, List<object> Blocks)>();

				foreach (var chainSettings in perChain)
				{
					if (!chainSettings.TryGetValue(name, out var setting))
						continue;

					var index = values.FindIndex(v => Equals(v.Value, setting.Value));

					if (index < 0)
						values.Add((setting.Value, [setting.Block]));
					else if (!values[index].Blocks.Contains(setting.Block))
						values[index].Blocks.Add(setting.Block);
				}

				if (values.Count == 1)
					settings.Add(name, new(values[0].Value, values[0].Blocks[0]));
				else
					conflicts.Add(new(name, [..values.Select(v => (v.Value, v.Blocks.ToImmutableArray()))]));
			}

			return new(true, family, settings.ToImmutable(), conflicts.ToImmutable());
		}

		/// <summary>Each setting of a chain from its most specific block that writes it.</summary>
		static Dictionary<string,CascadeSetting> ChainSettings(CascadeBlock<TFact>[] chain)
		{
			var result = new Dictionary<string,CascadeSetting>(StringComparer.Ordinal);

			foreach (var block in chain)
				foreach (var setting in block.Settings)
					result[setting.Key] = new(setting.Value, block);

			return result;
		}

		static CascadeBlock<TFact> Expect(CascadeBlock<TFact> block, CascadeLevel level)
		{
			if (block.Level != level)
				throw new ArgumentException($"Block '{block.Label}' is a {block.Level} block where a {level} block is expected.");

			return block;
		}

		/// <summary>
		/// A chain, or the chain cut at a level (its first <see cref="_count"/> blocks), with the declarations that apply on it.
		/// </summary>
		sealed class Chain : CascadeScope<TFact>
		{
			readonly CascadeBlock<TFact>[] _blocks;
			readonly int                   _count;
			readonly Func<TFact,bool>      _isProject;

			List<CascadeDeclaration<TFact>>? _applying;
			Chain?[]?                        _cuts;

			public Chain(CascadeBlock<TFact>[] blocks, int count, Func<TFact,bool> isProject)
			{
				_blocks    = blocks;
				_count     = count;
				_isProject = isProject;
			}

			public bool Selects(TFact fact)
			{
				if (_count < 2)
					return false;

				foreach (var declaration in Applying())
					if (!declaration.IsSatisfied(fact, this))
						return false;

				return true;
			}

			public override bool Resolve(CascadeLevel level, CascadeProperty property, TFact fact)
			{
				var cut = Cut(level);

				foreach (var declaration in cut.Applying())
					if (declaration.Properties.Overlaps(property) && !declaration.IsSatisfied(fact, cut))
						return false;

				return true;
			}

			public override bool IsProject(TFact fact)
			{
				return _isProject(fact);
			}

			/// <summary>
			/// The declarations that apply: each of a block that no more specific block of the chain overrides in any of its
			/// properties.
			/// </summary>
			List<CascadeDeclaration<TFact>> Applying()
			{
				if (_applying is not null)
					return _applying;

				var below    = CascadeProperty.None;
				var applying = new List<CascadeDeclaration<TFact>>();

				for (var i = _count - 1; i >= 0; i--)
				{
					var block = _blocks[i];

					foreach (var declaration in block.Declarations)
						if (!declaration.Properties.Overlaps(below))
							applying.Add(declaration);

					below |= block.Declared;
				}

				return _applying = applying;
			}

			/// <summary>The chain without the blocks below <paramref name="level"/>.</summary>
			Chain Cut(CascadeLevel level)
			{
				_cuts ??= new Chain?[4];

				if (_cuts[(int)level] is { } cut)
					return cut;

				var count = 0;

				while (count < _count && _blocks[count].Level <= level)
					count++;

				return _cuts[(int)level] = count == _count ? this : new Chain(_blocks, count, _isProject);
			}
		}
	}
}
