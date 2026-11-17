using System;
using System.Collections;
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
	/// Compares setting values as an attribute constructor would tell them apart: two values are equal when they have the
	/// same runtime type and, for arrays and other sequences (a constructor argument list, an array argument, an array
	/// inside an argument list), equal elements in order, compared the same way; anything else by
	/// <see cref="object.Equals(object, object)"/>. So <c>new int[] { 1 }</c> and <c>new object[] { 1 }</c> differ, and so
	/// do <c>1</c> and <c>1L</c>.
	/// </summary>
	sealed class CascadeSettingComparer : IEqualityComparer<object?>
	{
		public static readonly CascadeSettingComparer Instance = new();

		public new bool Equals(object? x, object? y)
		{
			if (ReferenceEquals(x, y))
				return true;

			if (x is null || y is null || x.GetType() != y.GetType())
				return false;

			if (x is IEnumerable xs && x is not string)
			{
				var xe = xs.GetEnumerator();
				var ye = ((IEnumerable)y).GetEnumerator();

				while (true)
				{
					var xm = xe.MoveNext();
					var ym = ye.MoveNext();

					if (xm != ym)
						return false;

					if (!xm)
						return true;

					if (!Equals(xe.Current, ye.Current))
						return false;
				}
			}

			return x.Equals(y);
		}

		public int GetHashCode(object? obj)
		{
			if (obj is null)
				return 0;

			if (obj is IEnumerable items && obj is not string)
			{
				var hash = obj.GetType().GetHashCode();

				foreach (var item in items)
					hash = unchecked(hash * 31 + GetHashCode(item));

				return hash;
			}

			return obj.GetHashCode();
		}
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
	/// <para>
	/// Which declarations of a block apply depends only on what the blocks below it in the chain declare, so a chain is
	/// evaluated as up to four parts — the declarations of each of its blocks that apply — and a part is evaluated once
	/// per method however many chains share it. The parts are planned once per context (<see cref="Prepare"/>: the
	/// assembly blocks and the blocks of one type) and reused for every method of that context.
	/// </para>
	/// </remarks>
	sealed class CascadeEvaluator<TFact>
	{
		readonly CascadeBlock<TFact>                 _default;
		readonly ImmutableArray<CascadeBlock<TFact>> _assembly;
		readonly Func<TFact,bool>                    _isProject;
		readonly UnreadableLibraryFilter             _libraryFilterWithError;

		CascadeContext? _consumerAssemblyOnly;

		/// <param name="aspectDefault">The aspect's <c>DefaultTargetFilter</c> (a block without lines when it has none).</param>
		/// <param name="assemblyBlocks">The assembly blocks of the compilation.</param>
		/// <param name="otherBlocks">
		/// The blocks of the compilation's types and methods. They are read only for their errors, since an error in any
		/// filter of an aspect turns the aspect off.
		/// </param>
		/// <param name="isProject">Whether a method is declared in the compilation whose blocks are evaluated.</param>
		/// <param name="libraryBlocks">
		/// The blocks read from referenced libraries, read here only for their errors: see <see cref="UnreadableLibraryFilter"/>.
		/// </param>
		public CascadeEvaluator(
			CascadeBlock<TFact>               aspectDefault,
			IEnumerable<CascadeBlock<TFact>>  assemblyBlocks,
			IEnumerable<CascadeBlock<TFact>>  otherBlocks,
			Func<TFact,bool>                  isProject,
			IEnumerable<CascadeBlock<TFact>>? libraryBlocks          = null,
			UnreadableLibraryFilter           libraryFilterWithError = CascadeDefaults.LibraryFilterWithError)
		{
			_default                = Expect(aspectDefault, CascadeLevel.Aspect);
			_assembly               = [..assemblyBlocks.Select(b => Expect(b, CascadeLevel.Assembly))];
			_isProject              = isProject;
			_libraryFilterWithError = libraryFilterWithError;

			var library = libraryBlocks?.ToList() ?? [];

			Diagnostics = [.._default.Diagnostics, .._assembly.SelectMany(b => b.Diagnostics), ..otherBlocks.SelectMany(b => b.Diagnostics)];

			if (libraryFilterWithError == UnreadableLibraryFilter.TurnAspectOff)
			{
				Diagnostics = Diagnostics.AddRange(library.Where(b => b.HasErrors).Select(b => new CascadeDiagnostic(
					CascadeDiagnosticIds.UnreadableLibrary,
					$"The target filter of '{b.Label}' read from a referenced assembly cannot be read: {b.Diagnostics[0].Message}",
					b,
					b.Source)));
				Warnings    = [];
			}
			else
			{
				Warnings = [..library.Where(b => b.HasErrors).Select(b => new CascadeDiagnostic(
					CascadeDiagnosticIds.UnreadableLibrary,
					$"The target filter of '{b.Label}' read from a referenced assembly cannot be read and is ignored: {b.Diagnostics[0].Message}",
					b,
					b.Source))];
			}
		}

		/// <summary>
		/// The errors of every block; a library block that cannot be read counts as one error of its own. Any of them turns
		/// the aspect off.
		/// </summary>
		public ImmutableArray<CascadeDiagnostic> Diagnostics { get; }

		/// <summary>The library blocks left out because they have errors, one warning each.</summary>
		public ImmutableArray<CascadeDiagnostic> Warnings { get; }

		public bool SelectsNothing => !Diagnostics.IsEmpty;

		/// <summary>Plans the decisions for the methods of one type of the compilation.</summary>
		/// <param name="typeBlocks">The blocks on the type.</param>
		public CascadeContext Prepare(IReadOnlyList<CascadeBlock<TFact>> typeBlocks)
		{
			return new(this, CascadeFamily.Consumer, _assembly, [..typeBlocks.Select(b => Expect(b, CascadeLevel.Type))]);
		}

		/// <summary>Plans the decisions for the methods of one type of a library that places the aspect on its own methods.</summary>
		public CascadeContext PrepareLibrary(IReadOnlyList<CascadeBlock<TFact>> libraryAssemblyBlocks, IReadOnlyList<CascadeBlock<TFact>> libraryTypeBlocks)
		{
			return new(this, CascadeFamily.Library,
				[..Readable(libraryAssemblyBlocks).Select(b => Expect(b, CascadeLevel.Assembly))],
				[..Readable(libraryTypeBlocks).Select(b => Expect(b, CascadeLevel.Type))]);
		}

		/// <summary>Decides a method of the compilation.</summary>
		/// <param name="context">The plan of the method's type (<see cref="Prepare"/>).</param>
		/// <param name="methodBlocks">The blocks on the method.</param>
		public CascadeDecision Decide(TFact fact, CascadeContext context, IReadOnlyList<CascadeBlock<TFact>> methodBlocks)
		{
			if (context.Evaluator != this || context.Family != CascadeFamily.Consumer)
				throw new ArgumentException("The context is not a consumer context of this evaluator.", nameof(context));

			foreach (var block in methodBlocks)
				Expect(block, CascadeLevel.Method);

			if (SelectsNothing)
				return CascadeDecision.NotSelected;

			return context.Decide(fact, methodBlocks);
		}

		/// <summary>Decides a method of the compilation; plans its type's context for this one call.</summary>
		public CascadeDecision Decide(TFact fact, IReadOnlyList<CascadeBlock<TFact>> typeBlocks, IReadOnlyList<CascadeBlock<TFact>> methodBlocks)
		{
			return Decide(fact, Prepare(typeBlocks), methodBlocks);
		}

		/// <summary>
		/// Decides a method of a referenced library that places the aspect on it. The library's own blocks decide first,
		/// as in the library's own build (<paramref name="libraryFact"/> sees the method as declared in the project); if
		/// they do not select it, the compilation's assembly blocks decide as for any referenced method.
		/// </summary>
		/// <param name="libraryContext">The plan of the method's type in the library (<see cref="PrepareLibrary"/>).</param>
		public CascadeDecision DecideLibraryMethod(
			TFact                              libraryFact,
			CascadeContext                     libraryContext,
			IReadOnlyList<CascadeBlock<TFact>> libraryMethodBlocks,
			TFact                              consumerFact)
		{
			if (libraryContext.Evaluator != this || libraryContext.Family != CascadeFamily.Library)
				throw new ArgumentException("The context is not a library context of this evaluator.", nameof(libraryContext));

			if (SelectsNothing)
				return CascadeDecision.NotSelected;

			var library = libraryContext.Decide(libraryFact, [..Readable(libraryMethodBlocks).Select(b => Expect(b, CascadeLevel.Method))]);

			if (library.Selected)
				return library;

			_consumerAssemblyOnly ??= Prepare([]);

			return _consumerAssemblyOnly.Decide(consumerFact, []);
		}

		/// <summary>As the overload with a context; plans the library type's context for this one call.</summary>
		public CascadeDecision DecideLibraryMethod(
			TFact                              libraryFact,
			IReadOnlyList<CascadeBlock<TFact>> libraryAssemblyBlocks,
			IReadOnlyList<CascadeBlock<TFact>> libraryTypeBlocks,
			IReadOnlyList<CascadeBlock<TFact>> libraryMethodBlocks,
			TFact                              consumerFact)
		{
			return DecideLibraryMethod(libraryFact, PrepareLibrary(libraryAssemblyBlocks, libraryTypeBlocks), libraryMethodBlocks, consumerFact);
		}

		IEnumerable<CascadeBlock<TFact>> Readable(IEnumerable<CascadeBlock<TFact>> libraryBlocks)
		{
			return _libraryFilterWithError == UnreadableLibraryFilter.Ignored ? libraryBlocks.Where(b => !b.HasErrors) : libraryBlocks;
		}

		static CascadeBlock<TFact> Expect(CascadeBlock<TFact> block, CascadeLevel level)
		{
			if (block.Level != level)
				throw new ArgumentException($"Block '{block.Label}' is a {block.Level} block where a {level} block is expected.");

			return block;
		}

		/// <summary>
		/// The declarations of one block that apply under a set of properties declared below it, evaluated as one
		/// conjunction. <see cref="Assembly"/> is the assembly block its references to the assembly level resolve
		/// against, or -1. A part of a context is numbered in the context; a part of a method block lives for one decision
		/// (<see cref="Transient"/>) and is numbered in it.
		/// </summary>
		sealed class Part(int index, ImmutableArray<CascadeDeclaration<TFact>> declarations, int assembly, bool transient = false)
		{
			public readonly int                                       Index        = index;
			public readonly ImmutableArray<CascadeDeclaration<TFact>> Declarations = declarations;
			public readonly int                                       Assembly     = assembly;
			public readonly bool                                      Transient    = transient;
		}

		/// <summary>For one set of properties declared below the assembly level: the part of every assembly block and the default's part under it.</summary>
		sealed class AssemblyRow(Part[] assembly, Part[] aspectDefault)
		{
			public readonly Part[] Assembly      = assembly;
			public readonly Part[] AspectDefault = aspectDefault;
		}

		/// <summary>
		/// The plan for the methods of one type: the assembly blocks of a family and the blocks of the type, with the parts
		/// of their chains, planned as methods need them and kept for the next method.
		/// </summary>
		internal sealed class CascadeContext
		{
			readonly CascadeBlock<TFact>                 _default;
			readonly ImmutableArray<CascadeBlock<TFact>> _assembly;
			readonly ImmutableArray<CascadeBlock<TFact>> _types;
			readonly Func<TFact,bool>                    _isProject;

			readonly object                                                    _lock  = new();
			readonly Dictionary<(CascadeBlock<TFact>, CascadeProperty, int),Part> _parts = [];
			readonly Dictionary<CascadeProperty,AssemblyRow>                    _rows  = [];
			readonly Dictionary<(CascadeBlock<TFact>, CascadeProperty),Part[]>  _perAssembly = [];

			/// <summary>The cut at the aspect level, then the cut at the assembly level per assembly block.</summary>
			readonly Part[][] _cuts;
			readonly string[] _settingNames;

			int _partCount;

			public CascadeContext(
				CascadeEvaluator<TFact>             evaluator,
				CascadeFamily                       family,
				ImmutableArray<CascadeBlock<TFact>> assembly,
				ImmutableArray<CascadeBlock<TFact>> types)
			{
				Evaluator  = evaluator;
				Family     = family;
				_default   = evaluator._default;
				_isProject = evaluator._isProject;
				_assembly  = assembly;
				_types     = types;

				_cuts    = new Part[1 + assembly.Length][];
				_cuts[0] = [GetPart(_default, CascadeProperty.None, -1)];

				for (var a = 0; a < assembly.Length; a++)
					_cuts[1 + a] = [GetPart(_default, assembly[a].Declared, -1), GetPart(assembly[a], CascadeProperty.None, -1)];

				_settingNames = [.._default.Settings.Keys.Concat(assembly.SelectMany(b => b.Settings.Keys)).Concat(types.SelectMany(b => b.Settings.Keys)).Distinct()];
			}

			public CascadeEvaluator<TFact> Evaluator { get; }
			public CascadeFamily           Family    { get; }

			Part GetPart(CascadeBlock<TFact> block, CascadeProperty below, int assembly)
			{
				if (!block.ReferencesAssembly)
					assembly = -1;

				lock (_lock)
				{
					if (_parts.TryGetValue((block, below, assembly), out var part))
						return part;

					var declarations = block.Declarations.Where(d => !d.Properties.Overlaps(below)).ToImmutableArray();

					part = new(_partCount++, declarations, assembly);

					_parts.Add((block, below, assembly), part);

					return part;
				}
			}

			/// <summary>The parts of a block that references the assembly level, one per assembly block.</summary>
			Part[] GetPartsPerAssembly(CascadeBlock<TFact> block, CascadeProperty below)
			{
				lock (_lock)
				{
					if (_perAssembly.TryGetValue((block, below), out var parts))
						return parts;
				}

				var result = new Part[_assembly.Length];

				for (var a = 0; a < result.Length; a++)
					result[a] = GetPart(block, below, a);

				lock (_lock)
				{
					if (!_perAssembly.TryGetValue((block, below), out var parts))
						_perAssembly.Add((block, below), parts = result);

					return parts;
				}
			}

			AssemblyRow GetRow(CascadeProperty below)
			{
				lock (_lock)
				{
					if (_rows.TryGetValue(below, out var row))
						return row;
				}

				var assembly      = new Part[_assembly.Length];
				var aspectDefault = new Part[_assembly.Length];

				for (var a = 0; a < _assembly.Length; a++)
				{
					assembly[a]      = GetPart(_assembly[a], below, -1);
					aspectDefault[a] = GetPart(_default, below | _assembly[a].Declared, -1);
				}

				lock (_lock)
				{
					if (!_rows.TryGetValue(below, out var row))
						_rows.Add(below, row = new(assembly, aspectDefault));

					return row;
				}
			}

			public CascadeDecision Decide(TFact fact, IReadOnlyList<CascadeBlock<TFact>> methodBlocks)
			{
				if (_assembly.IsEmpty && _types.IsEmpty && methodBlocks.Count == 0)
					return CascadeDecision.NotSelected;

				var scope      = new Scope(this, fact);
				var settings   = methodBlocks.Count == 0 ? _settingNames : [.._settingNames.Concat(methodBlocks.SelectMany(b => b.Settings.Keys)).Distinct()];
				var collector  = settings.Length == 0 ? null : new SettingCollector(settings, _assembly, _default);
				var selected   = false;
				var assemblyOk = new bool[_assembly.Length];
				var rows       = new List<(CascadeProperty Below, bool[] Ok, bool Any)>();
				var mCount     = Math.Max(1, methodBlocks.Count);
				var tCount     = Math.Max(1, _types.Length);

				for (var mi = 0; mi < mCount; mi++)
				{
					var m        = methodBlocks.Count == 0 ? null : methodBlocks[mi];
					var mDepends = m is not null && m.ReferencesAssembly && !_assembly.IsEmpty;

					// A part that does not depend on the assembly block decides here for every chain through it. Nothing
					// is declared below a method block, so all of its declarations apply.
					if (m is not null && !mDepends && !scope.Evaluate(scope.Transient(m.Declarations, -1)))
						continue;

					Part[]? mParts = null;

					if (mDepends)
					{
						mParts = new Part[_assembly.Length];

						for (var a = 0; a < mParts.Length; a++)
							mParts[a] = scope.Transient(m!.Declarations, a);
					}

					for (var ti = 0; ti < tCount; ti++)
					{
						var t        = _types.IsEmpty ? null : _types[ti];
						var tBelow   = m?.Declared ?? CascadeProperty.None;
						var tDepends = t is not null && t.ReferencesAssembly && !_assembly.IsEmpty;

						if (t is not null && !tDepends && !scope.Evaluate(GetPart(t, tBelow, -1)))
							continue;

						var tParts = tDepends ? GetPartsPerAssembly(t!, tBelow) : null;

						var below = tBelow | (t?.Declared ?? CascadeProperty.None);

						if (_assembly.IsEmpty)
						{
							if (!scope.Evaluate(GetPart(_default, below, -1)))
								continue;

							selected = true;

							if (collector is null)
								return Selected(null);

							collector.Add(m, t, null);
							continue;
						}

						// Which assembly blocks complete a selecting chain depends on the method and type blocks only through
						// what they declare, unless one of them references the assembly level.
						var (_, rowOk, any) = Row(below);
						var ok              = rowOk;

						if (any && (mParts is not null || tParts is not null))
						{
							ok  = assemblyOk;
							any = false;

							for (var a = 0; a < _assembly.Length; a++)
							{
								ok[a] =
									rowOk[a] &&
									(mParts is null || scope.Evaluate(mParts[a])) &&
									(tParts is null || scope.Evaluate(tParts[a]));

								any |= ok[a];
							}
						}

						if (!any)
							continue;

						selected = true;

						if (collector is null)
							return Selected(null);

						collector.Add(m, t, ok, stable: ok == rowOk);
					}
				}

				return selected ? Selected(collector) : CascadeDecision.NotSelected;

				(CascadeProperty, bool[], bool) Row(CascadeProperty below)
				{
					foreach (var r in rows)
						if (r.Below == below)
							return r;

					var row = GetRow(below);
					var ok  = new bool[_assembly.Length];
					var any = false;

					for (var a = 0; a < ok.Length; a++)
					{
						ok[a] = scope.Evaluate(row.Assembly[a]) && scope.Evaluate(row.AspectDefault[a]);
						any  |= ok[a];
					}

					rows.Add((below, ok, any));

					return (below, ok, any);
				}

				CascadeDecision Selected(SettingCollector? c)
				{
					if (c is null)
						return new(true, Family, ImmutableDictionary<string,CascadeSetting>.Empty, ImmutableArray<CascadeSettingConflict>.Empty);

					var (merged, conflicts) = c.Result();

					return new(true, Family, merged, conflicts);
				}
			}

			/// <summary>The evaluation of one method: the value of each part and of each level reference, computed once.</summary>
			sealed class Scope : CascadeScope<TFact>
			{
				readonly CascadeContext _context;
				readonly TFact          _fact;
				readonly sbyte[]        _references;

				sbyte[] _parts;
				sbyte[] _transient = new sbyte[8];
				int     _transientCount;
				int     _assembly = -1;

				public Scope(CascadeContext context, TFact fact)
				{
					_context    = context;
					_fact       = fact;
					_references = new sbyte[context._cuts.Length * 32];
					_parts      = new sbyte[Math.Max(16, context._partCount)];
				}

				public Part Transient(ImmutableArray<CascadeDeclaration<TFact>> declarations, int assembly)
				{
					if (_transientCount == _transient.Length)
						Array.Resize(ref _transient, _transient.Length * 2);

					return new(_transientCount++, declarations, assembly, transient: true);
				}

				public bool Evaluate(Part part)
				{
					if (!part.Transient && part.Index >= _parts.Length)
						Array.Resize(ref _parts, Math.Max(part.Index + 1, _parts.Length * 2));

					var memo = part.Transient ? _transient : _parts;

					if (memo[part.Index] != 0)
						return memo[part.Index] > 0;

					var saved = _assembly;

					_assembly = part.Assembly;

					var ok = true;

					foreach (var declaration in part.Declarations)
					{
						if (!declaration.IsSatisfied(_fact, this))
						{
							ok = false;
							break;
						}
					}

					_assembly = saved;

					memo[part.Index] = ok ? (sbyte)1 : (sbyte)-1;

					return ok;
				}

				public override bool Resolve(CascadeLevel level, CascadeProperty property, TFact fact)
				{
					// A reference resolves on the chain cut at its level: the default alone, or the default and the
					// assembly block of the chain. Declarations on a cut reference only the aspect level.
					var cut   = level == CascadeLevel.Assembly && _assembly >= 0 ? 1 + _assembly : 0;
					var index = cut * 32 + Bit(property);

					if (_references[index] != 0)
						return _references[index] > 0;

					var ok = true;

					foreach (var part in _context._cuts[cut])
					{
						foreach (var declaration in part.Declarations)
						{
							if (declaration.Properties.Overlaps(property) && !declaration.IsSatisfied(fact, this))
							{
								ok = false;
								break;
							}
						}

						if (!ok)
							break;
					}

					_references[index] = ok ? (sbyte)1 : (sbyte)-1;

					return ok;
				}

				public override bool IsProject(TFact fact)
				{
					return _context._isProject(fact);
				}

				static int Bit(CascadeProperty property)
				{
					var bit = 0;

					for (var value = (int)property; value > 1; value >>= 1)
						bit++;

					return bit;
				}
			}
		}

		/// <summary>
		/// The distinct values each setting takes over the selecting chains: in a chain, from its most specific block that
		/// writes it.
		/// </summary>
		sealed class SettingCollector
		{
			readonly string[]                                       _names;
			readonly ImmutableArray<CascadeBlock<TFact>>             _assembly;
			readonly CascadeBlock<TFact>                            _default;
			readonly List<(object? Value, List<object> Blocks)>?[] _values;
			readonly HashSet<object>?[]                           _writers;

			/// <summary>Per setting, which assembly blocks write it and which of them are placed already.</summary>
			readonly bool[]?[] _assemblyWrites;
			readonly bool[]?[] _assemblyPlaced;
			readonly bool[]?[] _lastStable;

			public SettingCollector(string[] names, ImmutableArray<CascadeBlock<TFact>> assembly, CascadeBlock<TFact> aspectDefault)
			{
				_names          = names;
				_assembly       = assembly;
				_default        = aspectDefault;
				_values         = new List<(object? Value, List<object> Blocks)>?[names.Length];
				_writers        = new HashSet<object>?[names.Length];
				_assemblyWrites = new bool[]?[names.Length];
				_assemblyPlaced = new bool[]?[names.Length];
				_lastStable     = new bool[]?[names.Length];
			}

			/// <summary>
			/// Adds the chains through <paramref name="method"/> and <paramref name="type"/> whose assembly block is marked
			/// in <paramref name="assemblyOk"/>, or the one chain without an assembly block. A <paramref name="stable"/> array
			/// keeps its content for the whole decision, so the same array twice adds nothing new.
			/// </summary>
			public void Add(CascadeBlock<TFact>? method, CascadeBlock<TFact>? type, bool[]? assemblyOk, bool stable = false)
			{
				for (var i = 0; i < _names.Length; i++)
				{
					var name   = _names[i];
					var writer = Writer(method, name) ?? Writer(type, name);

					// Written below the assembly level, the value is the same for every assembly block.
					if (writer is not null || assemblyOk is null)
					{
						Place(i, writer ?? Writer(_default, name));
						continue;
					}

					if (stable && ReferenceEquals(_lastStable[i], assemblyOk))
						continue;

					_lastStable[i] = stable ? assemblyOk : null;

					var writes = _assemblyWrites[i] ??= [.._assembly.Select(a => a.Settings.ContainsKey(name))];
					var placed = _assemblyPlaced[i] ??= new bool[_assembly.Length];
					var silent = false;

					for (var a = 0; a < assemblyOk.Length; a++)
					{
						if (!assemblyOk[a])
							continue;

						if (!writes[a])
						{
							silent = true;
						}
						else if (!placed[a])
						{
							placed[a] = true;
							Place(i, _assembly[a]);
						}
					}

					if (silent)
						Place(i, Writer(_default, name));
				}
			}

			void Place(int i, CascadeBlock<TFact>? writer)
			{
				// A block writes one value: once its value is placed, it is placed for every chain.
				if (writer is null || !(_writers[i] ??= []).Add(writer))
					return;

				var value  = writer.Settings[_names[i]];
				var values = _values[i] ??= [];
				var index  = -1;

				for (var v = 0; v < values.Count && index < 0; v++)
					if (CascadeSettingComparer.Instance.Equals(values[v].Value, value))
						index = v;

				if (index < 0)
					values.Add((value, [writer]));
				else
					values[index].Blocks.Add(writer);
			}

			static CascadeBlock<TFact>? Writer(CascadeBlock<TFact>? block, string name)
			{
				return block is not null && block.Settings.ContainsKey(name) ? block : null;
			}

			public (ImmutableDictionary<string,CascadeSetting>, ImmutableArray<CascadeSettingConflict>) Result()
			{
				var settings  = ImmutableDictionary.CreateBuilder<string,CascadeSetting>();
				var conflicts = ImmutableArray.CreateBuilder<CascadeSettingConflict>();

				for (var i = 0; i < _names.Length; i++)
				{
					var values = _values[i];

					if (values is null)
						continue;

					if (values.Count == 1)
						settings.Add(_names[i], new(values[0].Value, values[0].Blocks[0]));
					else
						conflicts.Add(new(_names[i], [..values.Select(v => (v.Value, v.Blocks.ToImmutableArray()))]));
				}

				return (settings.ToImmutable(), conflicts.ToImmutable());
			}
		}
	}
}
