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
	/// What a method pattern says of the method's own name: text it starts with, text it ends with, or the whole name
	/// (<c>Append*</c>, <c>*Async</c>, <c>Get*Async</c>, <c>Save</c>). It is checked on the name a call is written with.
	/// </summary>
	readonly struct NameAnchor : IEquatable<NameAnchor>
	{
		public NameAnchor(string prefix, string suffix, bool isExact)
		{
			Prefix  = prefix;
			Suffix  = suffix;
			IsExact = isExact;
		}

		/// <summary>The text the name starts with; the whole name for an exact anchor.</summary>
		public string Prefix  { get; }
		public string Suffix  { get; }
		public bool   IsExact { get; }

		public static NameAnchor Exact(string name)
		{
			return new NameAnchor(name, "", true);
		}

		public bool Matches(string name)
		{
			if (IsExact)
				return string.Equals(name, Prefix, StringComparison.Ordinal);

			return
				name.Length >= Prefix.Length + Suffix.Length &&
				name.StartsWith(Prefix, StringComparison.Ordinal) &&
				name.EndsWith(Suffix, StringComparison.Ordinal);
		}

		/// <summary>The anchor of a name that both anchors hold of, or <see langword="null"/> when no name can satisfy both.</summary>
		public NameAnchor? And(NameAnchor other)
		{
			if (IsExact)
				return other.Matches(Prefix) ? this : null;

			if (other.IsExact)
				return Matches(other.Prefix) ? other : null;

			string prefix, suffix;

			if (Prefix.StartsWith(other.Prefix, StringComparison.Ordinal))
				prefix = Prefix;
			else if (other.Prefix.StartsWith(Prefix, StringComparison.Ordinal))
				prefix = other.Prefix;
			else
				return null;

			if (Suffix.EndsWith(other.Suffix, StringComparison.Ordinal))
				suffix = Suffix;
			else if (other.Suffix.EndsWith(Suffix, StringComparison.Ordinal))
				suffix = other.Suffix;
			else
				return null;

			return new NameAnchor(prefix, suffix, false);
		}

		public bool Equals(NameAnchor other)
		{
			return IsExact == other.IsExact && Prefix == other.Prefix && Suffix == other.Suffix;
		}

		public override bool Equals(object? obj)
		{
			return obj is NameAnchor other && Equals(other);
		}

		public override int GetHashCode()
		{
			return unchecked((StringComparer.Ordinal.GetHashCode(Prefix) * 397 ^ StringComparer.Ordinal.GetHashCode(Suffix)) * 2 + (IsExact ? 1 : 0));
		}

		public override string ToString()
		{
			return IsExact ? Prefix : Prefix + "*" + Suffix;
		}
	}

	/// <summary>
	/// One way a filter can select a method, as far as it can be checked before binding: every part that is set holds
	/// of the method. A term with nothing set holds of every method.
	/// </summary>
	sealed class AnchorTerm : IEquatable<AnchorTerm>
	{
		public static readonly AnchorTerm Unbounded = new(null, null, false);

		public AnchorTerm(NameAnchor? name, string? root, bool isProjectOnly)
		{
			Name          = name;
			Root          = root;
			IsProjectOnly = isProjectOnly;
		}

		/// <summary>What the method's name starts or ends with.</summary>
		public NameAnchor? Name { get; }

		/// <summary>
		/// The first segment of the method's full name: its root namespace, or its outermost type when that is declared
		/// in the global namespace. Where no referenced assembly declares that root, only methods of the project have it.
		/// </summary>
		public string? Root { get; }

		/// <summary>The method is declared in the project.</summary>
		public bool IsProjectOnly { get; }

		public bool IsUnbounded => Name is null && Root is null && !IsProjectOnly;

		public AnchorTerm? And(AnchorTerm other)
		{
			if (Root is not null && other.Root is not null && Root != other.Root)
				return null;

			var name = Name;

			if (name is { } mine && other.Name is { } theirs)
			{
				name = mine.And(theirs);

				if (name is null)
					return null;
			}

			return new AnchorTerm(name ?? other.Name, Root ?? other.Root, IsProjectOnly || other.IsProjectOnly);
		}

		public bool Equals(AnchorTerm? other)
		{
			return other is not null && Nullable.Equals(Name, other.Name) && Root == other.Root && IsProjectOnly == other.IsProjectOnly;
		}

		public override bool Equals(object? obj)
		{
			return Equals(obj as AnchorTerm);
		}

		public override int GetHashCode()
		{
			return unchecked((Name.GetHashCode() * 397 ^ (Root is null ? 0 : StringComparer.Ordinal.GetHashCode(Root))) * 2 + (IsProjectOnly ? 1 : 0));
		}

		public override string ToString()
		{
			if (IsUnbounded)
				return "every call";

			var parts = new List<string>();

			if (Name is { } name)
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
		public static readonly BindingAnchors Project    = new(ImmutableArray.Create(new AnchorTerm(null, null, true)));

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
		/// Whether a call written with this method name may be selected, as far as names tell: some term has no name
		/// anchor, or one that the name satisfies.
		/// </summary>
		public bool MayBindName(string name)
		{
			foreach (var term in Terms)
				if (term.Name is not { } anchor || anchor.Matches(name))
					return true;

			return false;
		}

		public override string ToString()
		{
			return SelectsNothing ? "no call" : string.Join(" | ", Terms);
		}

		// ---- from the syntax of a pattern ------------------------------------------------------------------------

		/// <summary>The anchors of a <c>method:</c> pattern or the name of a compact signature.</summary>
		public static BindingAnchors OfMethod(NamePatternSyntax pattern)
		{
			if (WholeAlternatives(pattern) is { IsDefault: false } alternatives)
				return OfAll(alternatives, OfMethod);

			if (pattern.Segments.IsEmpty)
				return Everything;

			var roots = pattern.Global is null ? default : Literals(pattern.Segments[0]);
			var names = NameAnchors(pattern.Segments[pattern.Segments.Length - 1]);

			// The last segment of the pattern is the method's own name, unless it is `**`, which says nothing of it.
			if (names.IsDefault)
				return OfRoots(roots);

			if (roots.IsDefault)
				return Of(names.Select(static name => new AnchorTerm(name, null, false)));

			return Of(names.SelectMany(name => roots.Select(root => new AnchorTerm(name, root, false))));
		}

		static BindingAnchors OfRoots(ImmutableArray<string> roots)
		{
			return roots.IsDefault ? Everything : Of(roots.Select(static root => new AnchorTerm(null, root, false)));
		}

		/// <summary>The anchors of a <c>namespace:</c> pattern, which is rooted whether it says <c>global::</c> or not.</summary>
		public static BindingAnchors OfNamespace(NamePatternSyntax pattern)
		{
			if (WholeAlternatives(pattern) is { IsDefault: false } alternatives)
				return OfAll(alternatives, OfNamespace);

			return pattern.Segments.IsEmpty ? Everything : OfRoots(Literals(pattern.Segments[0]));
		}

		/// <summary>The anchors of a <c>type:</c> pattern: a root, where the type is written from <c>global::</c>.</summary>
		public static BindingAnchors OfType(TypePatternSyntax pattern)
		{
			if (!pattern.Suffixes.IsEmpty)
				return Everything;

			switch (pattern.Core)
			{
				case TypeAlternativesSyntax alternatives:
					return OfAll(alternatives.Alternatives, OfType);

				case NamedTypeSyntax named when WholeAlternatives(named.Name) is { IsDefault: false } alternatives:
					return OfAll(alternatives, OfRootedType);

				case NamedTypeSyntax named:
					return OfRootedType(named.Name);

				default:
					return Everything;
			}
		}

		static BindingAnchors OfRootedType(NamePatternSyntax name)
		{
			return name.Global is null || name.Segments.IsEmpty ? Everything : OfRoots(Literals(name.Segments[0]));
		}

		static BindingAnchors OfAll<T>(ImmutableArray<T> alternatives, Func<T, BindingAnchors> anchors)
		{
			// Alternatives nest; the builder reports a filter nested deeper than the stack can follow.
			System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();

			var all = Nothing;

			foreach (var alternative in alternatives)
				all = all.Or(anchors(alternative));

			return all;
		}

		static ImmutableArray<NamePatternSyntax> WholeAlternatives(NamePatternSyntax pattern)
		{
			if (pattern.Global is null &&
				pattern.Segments.Length == 1 &&
				pattern.Segments[0] is { IsGlobstar: false, TypeArguments: null } segment &&
				segment.Parts.Length == 1 &&
				segment.Parts[0].Kind == NamePartKind.Alternatives)
			{
				return segment.Parts[0].Alternatives;
			}

			return default;
		}

		/// <summary>
		/// The names a first segment can be, where it is text and alternatives of text only; default for any other, and for
		/// the empty name: the global namespace is no root, a method of it is rooted in its type.
		/// </summary>
		static ImmutableArray<string> Literals(NameSegmentSyntax segment)
		{
			var anchors = NameAnchors(segment);

			if (anchors.IsDefault)
				return default;

			var names = ImmutableArray.CreateBuilder<string>(anchors.Length);

			foreach (var anchor in anchors)
			{
				if (!anchor.IsExact || anchor.Prefix.Length == 0)
					return default;

				names.Add(anchor.Prefix);
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
		/// What the last segment of a method pattern says of the method's name: one anchor per way its alternatives
		/// combine, or default when some way starts and ends with a wildcard and so says nothing.
		/// </summary>
		static ImmutableArray<NameAnchor> NameAnchors(NameSegmentSyntax segment)
		{
			if (segment.IsGlobstar)
				return default;

			// Each way to read the parts so far: the text the name starts with, whether a wildcard was passed, and the text
			// since the last wildcard.
			var ways = new List<(string Prefix, bool Open, string Tail)> { ("", false, "") };

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
							ways[i] = (ways[i].Prefix, true, "");

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

						var next = new List<(string, bool, string)>(ways.Count * texts.Count);

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

			var anchors = ImmutableArray.CreateBuilder<NameAnchor>(ways.Count);

			foreach (var (prefix, open, tail) in ways)
			{
				if (!open)
					anchors.Add(NameAnchor.Exact(prefix));
				else if (prefix.Length == 0 && tail.Length == 0)
					return default;
				else
					anchors.Add(new NameAnchor(prefix, tail, false));
			}

			return anchors.ToImmutable();

			static (string, bool, string) Append((string Prefix, bool Open, string Tail) way, string text)
			{
				return way.Open ? (way.Prefix, true, way.Tail + text) : (way.Prefix + text, false, "");
			}
		}
	}
}
