using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using AspectGenerator.FilterV2;

namespace AspectGenerator.TargetFilterBinding
{
	// To decide a call, the call has to be bound, which is the expensive part of a build. An anchor is something a
	// filter says about every method it can select that is cheap to check before binding: the name of the method as the
	// call writes it, the root of the method's full name, or that the method is declared in the project. The anchors of a
	// filter are a necessary condition only: a call that fails them cannot be selected by the filter, a call that passes
	// them is bound and then decided.

	/// <summary>
	/// One way a filter can select a method, as far as it can be checked before binding: every part that is set holds
	/// of the method. A term with nothing set holds of every method.
	/// </summary>
	sealed class AnchorTerm : IEquatable<AnchorTerm>
	{
		public static readonly AnchorTerm Unbounded = new(ImmutableArray<SegmentGlob>.Empty, null, false);

		public AnchorTerm(ImmutableArray<SegmentGlob> names, string? root, bool isProjectOnly)
		{
			Names         = names;
			Root          = root;
			IsProjectOnly = isProjectOnly;
		}

		/// <summary>
		/// What the conditions on the method say of its own name, each the last segment of a method pattern with some
		/// literal text in it (<c>Append*</c>, <c>*Async</c>, <c>*Internal*</c>, <c>Save</c>): the name a call is written
		/// with has to match every one of them. They are kept apart, as two of them may be met by the same characters:
		/// <c>Save*</c> and <c>*ave</c> both hold of <c>Save</c>.
		/// </summary>
		public ImmutableArray<SegmentGlob> Names { get; }

		/// <summary>
		/// The first segment of the method's full name: its root namespace, or its outermost type when that is declared
		/// in the global namespace. Where no referenced assembly declares that root, only methods of the project have it.
		/// </summary>
		public string? Root { get; }

		/// <summary>The method is declared in the project.</summary>
		public bool IsProjectOnly { get; }

		public bool IsUnbounded => Names.IsEmpty && Root is null && !IsProjectOnly;

		public bool MatchesName(string name)
		{
			foreach (var glob in Names)
				if (!glob.Matches(name))
					return false;

			return true;
		}

		/// <summary>The term of a method both terms hold of, or <see langword="null"/> where it is plain that no method is: two roots, or a whole name that another condition on the name refuses.</summary>
		public AnchorTerm? And(AnchorTerm other)
		{
			if (Root is not null && other.Root is not null && Root != other.Root)
				return null;

			var names = Names;

			foreach (var glob in other.Names)
				if (!names.Contains(glob))
					names = names.Add(glob);

			foreach (var exact in names)
			{
				if (!exact.IsExact)
					continue;

				foreach (var glob in names)
					if (!glob.Matches(exact.Text))
						return null;
			}

			return new AnchorTerm(names, Root ?? other.Root, IsProjectOnly || other.IsProjectOnly);
		}

		public bool Equals(AnchorTerm? other)
		{
			return other is not null && Root == other.Root && IsProjectOnly == other.IsProjectOnly && Names.Length == other.Names.Length && ToString() == other.ToString();
		}

		public override bool Equals(object? obj)
		{
			return Equals(obj as AnchorTerm);
		}

		public override int GetHashCode()
		{
			return StringComparer.Ordinal.GetHashCode(ToString());
		}

		public override string ToString()
		{
			if (IsUnbounded)
				return "every call";

			var parts = new List<string>();

			foreach (var name in Names)
				parts.Add("name " + name);

			if (Root is not null)
				parts.Add("root " + Root);

			if (IsProjectOnly)
				parts.Add("project");

			return string.Join(", ", parts);
		}
	}

	/// <summary>
	/// What can be checked of a call before it is bound for a filter: the call has to satisfy one of the terms. No
	/// term at all means the filter selects nothing; a term without anchors means every call is bound.
	/// </summary>
	sealed class BindingAnchors
	{
		/// <summary>Past this many terms a conjunction keeps the smaller of its two sides, which alone is still necessary.</summary>
		const int MaxTerms = 64;

		public static readonly BindingAnchors Everything = new(ImmutableArray.Create(AnchorTerm.Unbounded));
		public static readonly BindingAnchors Nothing    = new(ImmutableArray<AnchorTerm>.Empty);
		public static readonly BindingAnchors Project    = new(ImmutableArray.Create(new AnchorTerm(ImmutableArray<SegmentGlob>.Empty, null, true)));

		BindingAnchors(ImmutableArray<AnchorTerm> terms)
		{
			Terms = terms;
		}

		public ImmutableArray<AnchorTerm> Terms { get; }

		/// <summary>Nothing limits binding: one of the terms holds of every call.</summary>
		public bool BindsEveryCall => Terms.Length == 1 && Terms[0].IsUnbounded;

		public bool SelectsNothing => Terms.IsEmpty;

		public static BindingAnchors Of(IEnumerable<AnchorTerm> terms)
		{
			var distinct = ImmutableArray.CreateBuilder<AnchorTerm>();

			foreach (var term in terms)
			{
				if (term.IsUnbounded)
					return Everything;

				if (!distinct.Contains(term))
					distinct.Add(term);
			}

			return new BindingAnchors(distinct.ToImmutable());
		}

		public BindingAnchors Or(BindingAnchors other)
		{
			if (BindsEveryCall || other.BindsEveryCall)
				return Everything;

			return Of(Terms.Concat(other.Terms));
		}

		public BindingAnchors And(BindingAnchors other)
		{
			if (BindsEveryCall)
				return other;

			if (other.BindsEveryCall)
				return this;

			if ((long)Terms.Length * other.Terms.Length > MaxTerms)
				return Terms.Length <= other.Terms.Length ? this : other;

			var terms = new List<AnchorTerm>();

			foreach (var left in Terms)
				foreach (var right in other.Terms)
					if (left.And(right) is { } both)
						terms.Add(both);

			return Of(terms);
		}

		/// <summary>
		/// Whether a call written with this method name may be selected, as far as names tell: the name meets every
		/// condition on it of some term.
		/// </summary>
		public bool MayBindName(string name)
		{
			foreach (var term in Terms)
				if (term.MatchesName(name))
					return true;

			return false;
		}

		public override string ToString()
		{
			return SelectsNothing ? "no call" : string.Join(" | ", Terms);
		}

		// ---- from the syntax of a pattern ------------------------------------------------------------------------

		static readonly ImmutableArray<SegmentGlob> NoNames = ImmutableArray<SegmentGlob>.Empty;

		/// <summary>The anchors of a <c>method:</c> pattern or the name of a compact signature.</summary>
		public static BindingAnchors OfMethod(NamePatternSyntax pattern)
		{
			var all = Nothing;

			foreach (var whole in PatternCompiler.WholeAlternatives(pattern))
				all = all.Or(OfOneMethod(whole));

			return all;
		}

		static BindingAnchors OfOneMethod(NamePatternSyntax pattern)
		{
			if (pattern.Segments.IsEmpty)
				return Everything;

			var roots = pattern.Global is null ? default : Literals(pattern.Segments[0]);
			var names = NameGlobs(pattern.Segments[pattern.Segments.Length - 1]);

			// The last segment of the pattern is the method's own name, unless it is `**`, which says nothing of it.
			if (names.IsDefault)
				return OfRoots(roots);

			if (roots.IsDefault)
				return Of(names.Select(static name => new AnchorTerm(ImmutableArray.Create(name), null, false)));

			return Of(names.SelectMany(name => roots.Select(root => new AnchorTerm(ImmutableArray.Create(name), root, false))));
		}

		static BindingAnchors OfRoots(ImmutableArray<string> roots)
		{
			return roots.IsDefault ? Everything : Of(roots.Select(static root => new AnchorTerm(NoNames, root, false)));
		}

		/// <summary>The anchors of a <c>namespace:</c> pattern, which is rooted whether it says <c>global::</c> or not.</summary>
		public static BindingAnchors OfNamespace(NamePatternSyntax pattern)
		{
			var all = Nothing;

			foreach (var whole in PatternCompiler.WholeAlternatives(pattern))
				all = all.Or(whole.Segments.IsEmpty ? Everything : OfRoots(Literals(whole.Segments[0])));

			return all;
		}

		/// <summary>The anchors of a <c>type:</c> pattern: a root, where the type is written from <c>global::</c>.</summary>
		public static BindingAnchors OfType(TypePatternSyntax pattern)
		{
			// Alternatives of types stand in one another; the compiler of patterns has refused a nesting too deep to follow.
			if (!pattern.Suffixes.IsEmpty)
				return Everything;

			var all = Nothing;

			switch (pattern.Core)
			{
				case TypeAlternativesSyntax alternatives:
					foreach (var alternative in alternatives.Alternatives)
						all = all.Or(OfType(alternative));

					return all;

				case NamedTypeSyntax named:
					foreach (var whole in PatternCompiler.WholeAlternatives(named.Name))
						all = all.Or(whole.Global is null || whole.Segments.IsEmpty ? Everything : OfRoots(Literals(whole.Segments[0])));

					return all;

				default:
					return Everything;
			}
		}

		/// <summary>
		/// The names a first segment can be, where it is text and alternatives of text only; default for any other, and for
		/// the empty name: the global namespace is no root, a method of it is rooted in its type.
		/// </summary>
		static ImmutableArray<string> Literals(NameSegmentSyntax segment)
		{
			var globs = NameGlobs(segment);

			if (globs.IsDefault)
				return default;

			var names = ImmutableArray.CreateBuilder<string>(globs.Length);

			foreach (var glob in globs)
			{
				if (!glob.IsExact || glob.Text.Length == 0)
					return default;

				names.Add(glob.Text);
			}

			return names.ToImmutable();
		}

		/// <summary>The text of a segment that is text only; <see langword="null"/> for any other.</summary>
		static string? Literal(NameSegmentSyntax segment)
		{
			if (segment.IsGlobstar)
				return null;

			var text = "";

			foreach (var part in segment.Parts)
			{
				if (part.Kind != NamePartKind.Text)
					return null;

				text += part.Text;
			}

			return text;
		}

		/// <summary>
		/// What a segment says of a name: one glob per way its alternatives combine, or default when some way holds no
		/// literal text and so says nothing, or the alternatives are more than text.
		/// </summary>
		static ImmutableArray<SegmentGlob> NameGlobs(NameSegmentSyntax segment)
		{
			if (segment.IsGlobstar)
				return default;

			// Each way to read the parts so far: the pieces of text around the stars passed.
			var ways = new List<ImmutableArray<string>> { ImmutableArray.Create("") };

			foreach (var part in segment.Parts)
			{
				switch (part.Kind)
				{
					case NamePartKind.Text:
						for (var i = 0; i < ways.Count; i++)
							ways[i] = Append(ways[i], part.Text!);

						break;

					case NamePartKind.Star:
						for (var i = 0; i < ways.Count; i++)
							ways[i] = ways[i].Add("");

						break;

					case NamePartKind.Alternatives:
					{
						var texts = new List<string>();

						foreach (var alternative in part.Alternatives)
						{
							// An alternative of several segments moves the text before it to another segment: then the last
							// segment of the pattern is not the whole of the method's name.
							if (alternative.Global is not null || alternative.Segments.Length != 1 || alternative.Segments[0].TypeArguments is not null ||
								Literal(alternative.Segments[0]) is not { } text)
							{
								return default;
							}

							texts.Add(text);
						}

						if ((long)ways.Count * texts.Count > MaxTerms)
							return default;

						var next = new List<ImmutableArray<string>>(ways.Count * texts.Count);

						foreach (var way in ways)
							foreach (var text in texts)
								next.Add(Append(way, text));

						ways = next;
						break;
					}

					default:
						return default;
				}
			}

			var globs = ImmutableArray.CreateBuilder<SegmentGlob>(ways.Count);

			foreach (var way in ways)
			{
				var glob = new SegmentGlob(way);

				if (!glob.HasText)
					return default;

				globs.Add(glob);
			}

			return globs.ToImmutable();

			static ImmutableArray<string> Append(ImmutableArray<string> way, string text)
			{
				return way.SetItem(way.Length - 1, way[way.Length - 1] + text);
			}
		}
	}
}
