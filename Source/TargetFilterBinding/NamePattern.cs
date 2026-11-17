using System;
using System.Collections.Immutable;

namespace AspectGenerator.TargetFilterBinding
{
	/// <summary>
	/// The segments a dotted pattern is matched against: those of a namespace or a type, optionally followed by the
	/// segment of a method. The name of the last segment can be read shorter than it is, which is how an attribute class
	/// is matched without its <c>Attribute</c> suffix.
	/// </summary>
	readonly struct SegmentPath
	{
		readonly ImmutableArray<NameSegment> _head;
		readonly NameSegment?                _tail;
		readonly int                         _lastNameLength;

		public SegmentPath(ImmutableArray<NameSegment> head, NameSegment? tail = null, int lastNameLength = -1)
		{
			_head           = head;
			_tail           = tail;
			_lastNameLength = lastNameLength;
		}

		public int Count => _head.Length + (_tail is null ? 0 : 1);

		public NameSegment this[int index] => index < _head.Length ? _head[index] : _tail!;

		public int NameLength(int index)
		{
			return _lastNameLength >= 0 && index == Count - 1 ? _lastNameLength : this[index].Name.Length;
		}
	}

	/// <summary>
	/// The name of one segment as a pattern without alternatives writes it: literal text around its stars.
	/// <c>Get*By*Async</c> is the pieces <c>Get</c>, <c>By</c>, <c>Async</c>; a name without a star is one piece.
	/// </summary>
	sealed class SegmentGlob : IEquatable<SegmentGlob>
	{
		readonly ImmutableArray<string> _pieces;

		/// <param name="pieces">The text before the first star, between each two stars, and after the last: one more than there are stars.</param>
		public SegmentGlob(ImmutableArray<string> pieces)
		{
			_pieces = pieces;

			foreach (var piece in pieces)
				HasText |= piece.Length != 0;
		}

		/// <summary>No star: the whole name.</summary>
		public bool IsExact => _pieces.Length == 1;

		/// <summary>Some literal text: the glob does not take every name.</summary>
		public bool HasText { get; }

		/// <summary>The name of an exact glob.</summary>
		public string Text => _pieces[0];

		public bool Matches(string name)
		{
			return Matches(name, name.Length);
		}

		/// <summary>
		/// Whether the first <paramref name="length"/> characters of <paramref name="name"/> are a name of the glob. The
		/// first and the last piece stand at the ends; each piece between them is taken at the first place it is found
		/// after the one before, which leaves the most room for the pieces after it.
		/// </summary>
		public bool Matches(string name, int length)
		{
			var first = _pieces[0];

			if (_pieces.Length == 1)
				return length == first.Length && string.CompareOrdinal(name, 0, first, 0, length) == 0;

			var last = _pieces[_pieces.Length - 1];

			if (length < first.Length + last.Length ||
				string.CompareOrdinal(name, 0, first, 0, first.Length) != 0 ||
				string.CompareOrdinal(name, length - last.Length, last, 0, last.Length) != 0)
			{
				return false;
			}

			var position = first.Length;
			var limit    = length - last.Length;

			for (var i = 1; i < _pieces.Length - 1; i++)
			{
				var piece = _pieces[i];

				if (piece.Length == 0)
					continue;

				if (limit - position < piece.Length)
					return false;

				var found = name.IndexOf(piece, position, limit - position, StringComparison.Ordinal);

				if (found < 0)
					return false;

				position = found + piece.Length;
			}

			return true;
		}

		public bool Equals(SegmentGlob? other)
		{
			return other is not null && _pieces.Length == other._pieces.Length && ToString() == other.ToString();
		}

		public override bool Equals(object? obj)
		{
			return Equals(obj as SegmentGlob);
		}

		public override int GetHashCode()
		{
			return StringComparer.Ordinal.GetHashCode(ToString());
		}

		public override string ToString()
		{
			return string.Join("*", _pieces);
		}
	}

	/// <summary>
	/// A dotted name pattern, compiled: <c>*</c> matches within one segment, <c>**</c> whole segments, <c>{a,b}</c>
	/// either alternative, <c>&lt;…&gt;</c> the type arguments of a segment (a segment without them takes any arity).
	/// A rooted pattern matches the whole path; any other a trailing run of whole segments.
	/// </summary>
	/// <remarks>
	/// Nothing here recurses over the pattern or the name, and no match costs more than the length of the pattern times
	/// the length of the name.
	/// <para>
	/// A pattern without alternatives is a list of segments, each a <see cref="SegmentGlob"/>: its segments are
	/// compared one by one where their number is fixed, and where <c>**</c> stands between them the comparison goes back
	/// to the last <c>**</c> when it fails, as a glob does within a name. Such a match allocates nothing.
	/// </para>
	/// <para>
	/// A pattern with alternatives, or with both <c>**</c> and type arguments (where one way to place <c>**</c> can
	/// answer for every call and another only for some), is a chain of steps of one character each, followed in all its
	/// ways at once: the steps that can stand at a position of the name are a set, and the set moves along the name.
	/// </para>
	/// </remarks>
	sealed class NamePattern
	{
		// ---- a pattern without alternatives ------------------------------------------------------------------------

		/// <summary>One segment: <c>**</c> where <see cref="Glob"/> is null.</summary>
		internal readonly struct Segment
		{
			public Segment(SegmentGlob? glob, ImmutableArray<TypePattern> arguments)
			{
				Glob      = glob;
				Arguments = arguments;
			}

			public SegmentGlob? Glob { get; }

			/// <summary>The type arguments written after the name; default where none are, which takes any arity.</summary>
			public ImmutableArray<TypePattern> Arguments { get; }
		}

		// ---- a pattern with alternatives ---------------------------------------------------------------------------

		internal enum StepKind : byte
		{
			/// <summary>One character of the segment's name.</summary>
			Char,
			/// <summary><c>*</c>: any run of characters of the segment's name.</summary>
			Star,
			/// <summary><c>&lt;…&gt;</c> at the end of the segment's name.</summary>
			Arguments,
			/// <summary>The end of a segment: the next step reads the next one.</summary>
			SegmentEnd,
			/// <summary><c>**</c>: any run of whole segments.</summary>
			Globstar,
			/// <summary><c>{a,b}</c>.</summary>
			Alternatives,
			/// <summary>The end of the pattern: the end of the path.</summary>
			Accept,
		}

		internal sealed class Step
		{
			public Step(StepKind kind, Step? next, int index)
			{
				Kind  = kind;
				Next  = next;
				Index = index;
			}

			public readonly StepKind Kind;
			public readonly Step?    Next;
			public readonly int      Index;

			public char                        Char;
			public ImmutableArray<TypePattern> Arguments = ImmutableArray<TypePattern>.Empty;
			public ImmutableArray<Step>        Branches  = ImmutableArray<Step>.Empty;
		}

		/// <summary>The pattern of an empty path: the global namespace, written <c>""</c>.</summary>
		public static readonly NamePattern EmptyPath = new(ImmutableArray<Segment>.Empty, isRooted: true);

		readonly ImmutableArray<Segment> _segments;
		readonly bool                    _hasGlobstar;
		readonly Step?                   _start;
		readonly Step?                   _accept;
		readonly Step[]?                 _steps;
		readonly int                     _stepCount;
		readonly int                     _fixedSegments;
		readonly int                     _minSegments;

		/// <summary>A pattern without alternatives. With <c>**</c> among its segments none of them has type arguments.</summary>
		internal NamePattern(ImmutableArray<Segment> segments, bool isRooted)
		{
			_segments = segments;
			IsRooted  = isRooted;

			foreach (var segment in segments)
			{
				if (segment.Glob is null)
					_hasGlobstar = true;
				else
					_minSegments++;
			}

			_fixedSegments = _hasGlobstar ? -1 : segments.Length;
		}

		/// <summary>A pattern as a chain of steps.</summary>
		/// <param name="fixedSegments">The number of segments every match has, or -1 when <c>**</c> or an alternative makes it vary.</param>
		/// <param name="minSegments">A lower bound of the number of segments a match has.</param>
		internal NamePattern(Step start, Step accept, Step[] steps, bool isRooted, int fixedSegments, int minSegments)
		{
			_start         = start;
			_accept        = accept;
			_steps         = steps;
			_stepCount     = steps.Length;
			IsRooted       = isRooted;
			_fixedSegments = fixedSegments;
			_minSegments   = minSegments;
		}

		public bool IsRooted { get; }

		public Tri Match(SegmentPath path, MatchPhase phase)
		{
			var count = path.Count;

			if (count < _minSegments || IsRooted && _fixedSegments >= 0 && count != _fixedSegments)
				return Tri.False;

			if (_start is not null)
				return Follow(path, phase);

			return _hasGlobstar ? MatchAround(path).ToTri() : MatchInPlace(path, count - _fixedSegments, phase);
		}

		/// <summary>A fixed number of segments: each stands against one segment of the path.</summary>
		Tri MatchInPlace(SegmentPath path, int first, MatchPhase phase)
		{
			// The names first: they cost less than the type arguments, and most paths fail on them.
			for (var i = _segments.Length - 1; i >= 0; i--)
				if (!_segments[i].Glob!.Matches(path[first + i].Name, path.NameLength(first + i)))
					return Tri.False;

			var answer = Tri.True;

			for (var i = 0; i < _segments.Length && answer != Tri.False; i++)
			{
				var patterns = _segments[i].Arguments;

				if (patterns.IsDefault)
					continue;

				var arguments = path[first + i].TypeArguments;

				if (arguments.Length != patterns.Length)
					return Tri.False;

				for (var a = 0; a < patterns.Length && answer != Tri.False; a++)
					answer = answer.And(patterns[a].Match(arguments[a], phase));
			}

			return answer;
		}

		/// <summary>
		/// Segments around <c>**</c>, none with type arguments: the segments are taken in order, and where one fails the
		/// last <c>**</c> takes one segment more. A pattern that is not rooted has a <c>**</c> before its first segment.
		/// </summary>
		bool MatchAround(SegmentPath path)
		{
			var count     = path.Count;
			var p         = 0;
			var s         = 0;
			var starP     = IsRooted ? -1 : 0;
			var starS     = 0;

			while (s < count)
			{
				if (p < _segments.Length && _segments[p].Glob is null)
				{
					starP = ++p;
					starS = s;
				}
				else if (p < _segments.Length && _segments[p].Glob!.Matches(path[s].Name, path.NameLength(s)))
				{
					p++;
					s++;
				}
				else if (starP >= 0)
				{
					p = starP;
					s = ++starS;
				}
				else
				{
					return false;
				}
			}

			while (p < _segments.Length && _segments[p].Glob is null)
				p++;

			return p == _segments.Length;
		}

		// ---- following the steps -----------------------------------------------------------------------------------

		/// <summary>The steps that stand at one position, each with the best answer of the ways that lead to it.</summary>
		sealed class StepSet
		{
			public StepSet(int size)
			{
				Answers = new byte[size];
				Members = new int[size];
			}

			public readonly byte[] Answers;
			public readonly int[]  Members;
			public int             Count;

			public void Clear()
			{
				for (var i = 0; i < Count; i++)
					Answers[Members[i]] = 0;

				Count = 0;
			}
		}

		/// <summary>What following a pattern writes on: kept for the next match of the thread, so that a match allocates nothing.</summary>
		sealed class Scratch
		{
			public Scratch(int size)
			{
				Size        = size;
				Here        = new StepSet(size);
				NextChar    = new StepSet(size);
				NextSegment = new StepSet(size);
				Pending     = new int[size * 2];
			}

			public readonly int Size;
			public StepSet      Here;
			public StepSet      NextChar;
			public StepSet      NextSegment;
			public readonly int[] Pending;
			public int            PendingCount;
		}

		[ThreadStatic]
		static Scratch? _scratch;

		Tri Follow(SegmentPath path, MatchPhase phase)
		{
			// A match inside a match (a type argument that is a pattern of this kind) finds the scratch taken and makes
			// its own.
			var scratch = _scratch;

			_scratch = null;

			if (scratch is null || scratch.Size < _stepCount)
				scratch = new Scratch(Math.Max(_stepCount, 64));

			try
			{
				return Follow(path, phase, scratch);
			}
			finally
			{
				scratch.Here.Clear();
				scratch.NextChar.Clear();
				scratch.NextSegment.Clear();
				scratch.PendingCount = 0;

				_scratch = scratch;
			}
		}

		Tri Follow(SegmentPath path, MatchPhase phase, Scratch scratch)
		{
			var count      = path.Count;
			var firstStart = IsRooted ? 0 : _fixedSegments >= 0 ? count - _fixedSegments : 0;
			var lastStart  = IsRooted ? 0 : _fixedSegments >= 0 ? count - _fixedSegments : count - _minSegments;

			for (var segment = 0; segment <= count; segment++)
			{
				// The steps that the segment before left for the start of this one.
				var here = scratch.NextSegment;

				scratch.NextSegment = scratch.Here;
				scratch.Here        = here;
				scratch.NextSegment.Clear();

				for (var i = 0; i < here.Count; i++)
					scratch.Pending[scratch.PendingCount++] = here.Members[i];

				if (segment >= firstStart && segment <= lastStart)
					Add(scratch, here, _start!, (byte)Tri.True, pending: true);

				if (here.Count == 0)
				{
					if (segment > lastStart)
						return Tri.False;

					continue;
				}

				var atEnd  = segment == count;
				var name   = atEnd ? "" : path[segment].Name;
				var length = atEnd ? 0  : path.NameLength(segment);

				for (var position = 0; ; position++)
				{
					// What follows without reading a character.
					while (scratch.PendingCount > 0)
					{
						var step   = StepAt(scratch.Pending[--scratch.PendingCount]);
						var answer = here.Answers[step.Index];

						switch (step.Kind)
						{
							case StepKind.Alternatives:
								foreach (var branch in step.Branches)
									Add(scratch, here, branch, answer, pending: true);

								break;

							case StepKind.Star:
								if (!atEnd)
									Add(scratch, here, step.Next!, answer, pending: true);

								break;

							case StepKind.Globstar:
								if (position == 0)
								{
									Add(scratch, here, step.Next!, answer, pending: true);

									if (!atEnd)
										Add(scratch, scratch.NextSegment, step, answer, pending: false);
								}

								break;

							case StepKind.Arguments:
								if (!atEnd && position == length)
								{
									var arguments = path[segment].TypeArguments;

									if (arguments.Length != step.Arguments.Length)
										break;

									var all = (Tri)answer;

									for (var a = 0; a < arguments.Length && all != Tri.False; a++)
										all = all.And(step.Arguments[a].Match(arguments[a], phase));

									if (all != Tri.False)
										Add(scratch, here, step.Next!, (byte)all, pending: true);
								}

								break;

							case StepKind.SegmentEnd:
								if (!atEnd && position == length)
									Add(scratch, scratch.NextSegment, step.Next!, answer, pending: false);

								break;
						}
					}

					if (atEnd)
						return (Tri)here.Answers[_accept!.Index];

					if (position == length)
						break;

					// What follows by reading the character at the position.
					var next = scratch.NextChar;
					var ch   = name[position];

					for (var i = 0; i < here.Count; i++)
					{
						var index = here.Members[i];
						var step  = StepAt(index);

						if (step.Kind == StepKind.Star)
							Add(scratch, next, step, here.Answers[index], pending: false);
						else if (step.Kind == StepKind.Char && step.Char == ch)
							Add(scratch, next, step.Next!, here.Answers[index], pending: false);
					}

					here.Clear();

					scratch.NextChar = here;
					scratch.Here     = next;
					here             = next;

					for (var i = 0; i < here.Count; i++)
						scratch.Pending[scratch.PendingCount++] = here.Members[i];
				}

				here.Clear();
			}

			return Tri.False;
		}

		Step StepAt(int index)
		{
			return _steps![index];
		}

		/// <summary>Puts a step into a set, or raises its answer there; a step whose answer rose at this position is followed again.</summary>
		static void Add(Scratch scratch, StepSet set, Step step, byte answer, bool pending)
		{
			var known = set.Answers[step.Index];

			if (known >= answer)
				return;

			if (known == 0)
				set.Members[set.Count++] = step.Index;

			set.Answers[step.Index] = answer;

			// A step is followed at most twice at a position, as its answer rises from "depends" to "true" once: the
			// list of steps to follow has room for that.
			if (pending)
				scratch.Pending[scratch.PendingCount++] = step.Index;
		}
	}

	/// <summary>
	/// What stands where a dotted name is expected: one pattern, or the alternatives of <c>{a,b}</c> written as the
	/// whole name, each with its own root.
	/// </summary>
	sealed class NamePatternSet
	{
		readonly ImmutableArray<NamePattern> _patterns;

		public NamePatternSet(ImmutableArray<NamePattern> patterns)
		{
			_patterns = patterns;
		}

		public Tri Match(SegmentPath path, MatchPhase phase)
		{
			var best = Tri.False;

			foreach (var pattern in _patterns)
			{
				best = best.Or(pattern.Match(path, phase));

				if (best == Tri.True)
					break;
			}

			return best;
		}
	}
}
