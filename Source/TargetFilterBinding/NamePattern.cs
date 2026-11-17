using System;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

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
	/// A dotted name pattern, compiled: <c>*</c> matches within one segment, <c>**</c> whole segments, <c>{a,b}</c>
	/// either alternative, <c>&lt;…&gt;</c> the type arguments of a segment (a segment without them takes any arity).
	/// A rooted pattern matches the whole path; any other a trailing run of whole segments.
	/// </summary>
	/// <remarks>
	/// The pattern is a chain of steps, each knowing the step after it; an alternative is a chain of its own that ends
	/// in the step after the braces. Matching follows the chain and tries again only where a step has a choice
	/// (how much <c>*</c> or <c>**</c> takes, which alternative), so a pattern without wildcards is compared once. A
	/// pattern with several choices remembers what it found at a step and a position, so that its cost stays the
	/// product of its length and the length of the name however the choices combine.
	/// </remarks>
	sealed class NamePattern
	{
		internal enum StepKind : byte
		{
			/// <summary>Literal text at the current position of the segment's name.</summary>
			Text,
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
			public Step(StepKind kind, Step? next)
			{
				Kind = kind;
				Next = next;
			}

			public readonly StepKind Kind;
			public readonly Step?    Next;

			public int                         Index;
			public string                      Text      = "";
			public ImmutableArray<TypePattern> Arguments = ImmutableArray<TypePattern>.Empty;
			public ImmutableArray<Step>        Branches  = ImmutableArray<Step>.Empty;

			/// <summary>
			/// For a <c>*</c> followed only by text up to the end of its segment: the length of that text, which leaves
			/// the star one place to end; -1 otherwise.
			/// </summary>
			public int TailLength = -1;
		}

		/// <summary>The pattern of an empty path: the global namespace, written <c>""</c>.</summary>
		public static readonly NamePattern EmptyPath = new(new Step(StepKind.Accept, null), 1, isRooted: true, fixedSegments: 0, minSegments: 0, choices: 0);

		/// <summary>The answers one match may keep, a byte each.</summary>
		const int MaxRemembered = 1 << 20;

		readonly Step _start;
		readonly int  _stepCount;
		readonly int  _fixedSegments;
		readonly int  _minSegments;
		readonly bool _remembers;

		/// <param name="fixedSegments">The number of segments every match has, or -1 when <c>**</c> or an alternative makes it vary.</param>
		/// <param name="minSegments">A lower bound of the number of segments a match has.</param>
		/// <param name="choices">The number of steps with a choice.</param>
		internal NamePattern(Step start, int stepCount, bool isRooted, int fixedSegments, int minSegments, int choices)
		{
			_start         = start;
			_stepCount     = stepCount;
			IsRooted       = isRooted;
			_fixedSegments = fixedSegments;
			_minSegments   = minSegments;
			_remembers     = choices > 2;
		}

		public bool IsRooted { get; }

		public Tri Match(SegmentPath path, MatchPhase phase)
		{
			var count = path.Count;

			if (count < _minSegments || IsRooted && _fixedSegments >= 0 && count != _fixedSegments)
				return Tri.False;

			var run = new Run(this, path, phase);

			try
			{
				if (IsRooted)
					return run.From(_start, 0, 0);

				if (_fixedSegments >= 0)
					return run.From(_start, count - _fixedSegments, 0);

				var best = Tri.False;

				for (var start = count - _minSegments; start >= 0 && best != Tri.True; start--)
					best = best.Or(run.From(_start, start, 0));

				return best;
			}
			catch (InsufficientExecutionStackException)
			{
				// A pattern with more choices in a row than the stack can follow matches nothing.
				return Tri.False;
			}
		}

		struct Run
		{
			readonly NamePattern _pattern;
			readonly SegmentPath _path;
			readonly MatchPhase  _phase;
			readonly int         _count;

			// What a step answered at a position, plus one: zero is "not asked yet".
			byte[]? _answers;
			int[]?  _offsets;
			int     _positions;
			bool    _remembers;

			public Run(NamePattern pattern, SegmentPath path, MatchPhase phase)
			{
				_pattern   = pattern;
				_path      = path;
				_phase     = phase;
				_count     = path.Count;
				_answers   = null;
				_offsets   = null;
				_positions = 0;
				_remembers = pattern._remembers;
			}

			public Tri From(Step step, int segment, int position)
			{
				if (!_remembers)
					return Follow(step, segment, position);

				if (_answers is null)
				{
					_offsets = new int[_count + 1];

					for (var i = 0; i < _count; i++)
						_offsets[i + 1] = _offsets[i] + _path.NameLength(i) + 1;

					_positions = _offsets[_count] + 1;

					// A pattern and a name too long to keep an answer for every step and position are searched without.
					if ((long)_pattern._stepCount * _positions > MaxRemembered)
					{
						_remembers = false;

						return Follow(step, segment, position);
					}

					_answers = new byte[_pattern._stepCount * _positions];
				}

				var index = step.Index * _positions + _offsets![segment] + position;

				if (_answers[index] != 0)
					return (Tri)(_answers[index] - 1);

				var answer = Follow(step, segment, position);

				_answers[index] = (byte)((int)answer + 1);

				return answer;
			}

			Tri Follow(Step? step, int segment, int position)
			{
				RuntimeHelpers.EnsureSufficientExecutionStack();

				var answer = Tri.True;

				while (step is not null)
				{
					switch (step.Kind)
					{
						case StepKind.Text:
						{
							if (segment >= _count)
								return Tri.False;

							var text = step.Text;

							if (position + text.Length > _path.NameLength(segment) ||
								string.CompareOrdinal(_path[segment].Name, position, text, 0, text.Length) != 0)
							{
								return Tri.False;
							}

							position += text.Length;
							break;
						}

						case StepKind.Arguments:
						{
							if (segment >= _count || position != _path.NameLength(segment))
								return Tri.False;

							var arguments = _path[segment].TypeArguments;

							if (arguments.Length != step.Arguments.Length)
								return Tri.False;

							for (var i = 0; i < arguments.Length; i++)
							{
								answer = answer.And(step.Arguments[i].Match(arguments[i], _phase));

								if (answer == Tri.False)
									return Tri.False;
							}

							break;
						}

						case StepKind.SegmentEnd:
							if (segment >= _count || position != _path.NameLength(segment))
								return Tri.False;

							segment++;
							position = 0;
							break;

						case StepKind.Accept:
							return segment == _count && position == 0 ? answer : Tri.False;

						case StepKind.Star:
						{
							if (segment >= _count)
								return Tri.False;

							var length = _path.NameLength(segment);

							if (step.TailLength >= 0)
							{
								// Only text follows up to the end of the segment: the star ends where that text begins.
								if (length - step.TailLength < position)
									return Tri.False;

								position = length - step.TailLength;
								break;
							}

							var best = Tri.False;

							for (var end = length; end >= position && best != Tri.True; end--)
								best = best.Or(From(step.Next!, segment, end));

							return answer.And(best);
						}

						case StepKind.Globstar:
						{
							if (position != 0)
								return Tri.False;

							var best = Tri.False;

							for (var next = segment; next <= _count && best != Tri.True; next++)
								best = best.Or(From(step.Next!, next, 0));

							return answer.And(best);
						}

						case StepKind.Alternatives:
						{
							var best = Tri.False;

							foreach (var branch in step.Branches)
							{
								best = best.Or(From(branch, segment, position));

								if (best == Tri.True)
									break;
							}

							return answer.And(best);
						}
					}

					step = step.Next;
				}

				return Tri.False;
			}
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
