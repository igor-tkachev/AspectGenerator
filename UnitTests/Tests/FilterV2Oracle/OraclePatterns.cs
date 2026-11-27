using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AspectGenerator.Tests.FilterV2Oracle
{
	// The oracle's patterns: what each pattern form of the reference text matches, written as plainly as the text reads.
	// Name segments are matched by a regular expression made from their glob; dotted names by trying every way their
	// globstars can fall. Nothing is clever and nothing is fast.

	/// <summary>A glob over one name segment: literal text, <c>*</c>, and <c>{a,b}</c> alternatives.</summary>
	sealed class OGlob
	{
		public OGlob(string regex, bool isPlainName, string text)
		{
			Regex       = new Regex("^(?:" + regex + ")$", RegexOptions.CultureInvariant);
			IsPlainName = isPlainName;
			Text        = text;
		}

		public Regex  Regex       { get; }
		public string Text        { get; }

		/// <summary>The glob is one written identifier without wildcards or escapes (it may be a keyword or an alias).</summary>
		public bool IsPlainName { get; }

		public bool Matches(string name)
		{
			return Regex.IsMatch(name);
		}
	}

	abstract class OSegmentPattern;

	sealed class OGlobstar : OSegmentPattern;

	sealed class ONameSegment(OGlob name, IReadOnlyList<OTypePattern>? arguments) : OSegmentPattern
	{
		public OGlob                       Name      { get; } = name;
		public IReadOnlyList<OTypePattern>? Arguments { get; } = arguments;

		public bool Matches(OSegment segment)
		{
			if (!Name.Matches(segment.Name))
				return false;

			if (Arguments is null)
				return true;

			if (Arguments.Count != segment.Arguments.Count)
				return false;

			for (var i = 0; i < Arguments.Count; i++)
				if (!Arguments[i].Matches(segment.Arguments[i]))
					return false;

			return true;
		}
	}

	enum OAnchor { Rooted, Trailing }

	/// <summary>A dotted name: a path of segment patterns, or alternatives of whole paths.</summary>
	sealed class ONamePattern
	{
		public ONamePattern(bool rooted, IReadOnlyList<OSegmentPattern> segments)
		{
			Rooted   = rooted;
			Segments = segments;
		}

		public ONamePattern(IReadOnlyList<ONamePattern> alternatives)
		{
			Alternatives = alternatives;
			Segments     = [];
		}

		public bool                           Rooted       { get; }
		public IReadOnlyList<OSegmentPattern> Segments     { get; }
		public IReadOnlyList<ONamePattern>?   Alternatives { get; }

		/// <summary><c>namespace: ""</c>: the global namespace.</summary>
		public bool IsGlobalNamespace { get; init; }

		static readonly Dictionary<string,string> _aliases = new(StringComparer.Ordinal)
		{
			["bool"] = "Boolean", ["byte"] = "Byte", ["sbyte"] = "SByte", ["char"] = "Char", ["decimal"] = "Decimal",
			["double"] = "Double", ["float"] = "Single", ["int"] = "Int32", ["uint"] = "UInt32", ["nint"] = "IntPtr",
			["nuint"] = "UIntPtr", ["long"] = "Int64", ["ulong"] = "UInt64", ["short"] = "Int16", ["ushort"] = "UInt16",
			["object"] = "Object", ["string"] = "String", ["void"] = "Void",
		};

		public static bool IsAlias(string name)
		{
			return _aliases.ContainsKey(name);
		}

		/// <param name="aliasHead">
		/// Whether a C# alias as the first of several segments names the system type (<c>method: int.Parse</c>); a type
		/// pattern of one alias segment is always the system type.
		/// </param>
		public bool Matches(IReadOnlyList<OSegment> name, OAnchor anchor, bool aliasHead)
		{
			if (Alternatives is not null)
				return Alternatives.Any(a => a.Matches(name, anchor, aliasHead));

			if (IsGlobalNamespace)
				return name.Count == 0;

			var segments = Segments;
			var rooted   = Rooted || anchor == OAnchor.Rooted;

			if (!Rooted && segments.Count >= 1 && segments[0] is ONameSegment { Name.IsPlainName: true, Arguments: null } head && _aliases.TryGetValue(head.Name.Text, out var clr) && (aliasHead && segments.Count > 1 || !aliasHead && segments.Count == 1))
			{
				segments = [new ONameSegment(new OGlob("System", true, "System"), null), new ONameSegment(new OGlob(Regex.Escape(clr), true, clr), null), ..segments.Skip(1)];
				rooted   = true;
			}

			if (rooted)
				return Match(segments, 0, name, 0);

			for (var start = 0; start <= name.Count; start++)
				if (Match(segments, 0, name, start))
					return true;

			return false;
		}

		static bool Match(IReadOnlyList<OSegmentPattern> pattern, int i, IReadOnlyList<OSegment> name, int j)
		{
			if (i == pattern.Count)
				return j == name.Count;

			if (pattern[i] is OGlobstar)
			{
				for (var k = j; k <= name.Count; k++)
					if (Match(pattern, i + 1, name, k))
						return true;

				return false;
			}

			return j < name.Count && ((ONameSegment)pattern[i]).Matches(name[j]) && Match(pattern, i + 1, name, j + 1);
		}
	}

	abstract class OTypePattern
	{
		public abstract bool Matches(OType type);
	}

	/// <summary><c>*</c>: any type at all, a type parameter of the calling code included.</summary>
	sealed class OAnyType : OTypePattern
	{
		public override bool Matches(OType type)
		{
			return true;
		}
	}

	sealed class ONamedTypePattern(ONamePattern name) : OTypePattern
	{
		public ONamePattern Name { get; } = name;

		public override bool Matches(OType type)
		{
			if (type is ODynamic)
				return Name is { Alternatives: null, Rooted: false, Segments: [ONameSegment { Name.IsPlainName: true, Name.Text: "dynamic", Arguments: null }] };

			return type is ONamed named && Name.Matches(named.Segments, OAnchor.Trailing, aliasHead: false);
		}
	}

	/// <summary><c>X?</c>: a nullable value type over <c>X</c>, or a reference type <c>X</c> (its annotation is ignored).</summary>
	sealed class ONullablePattern(OTypePattern inner) : OTypePattern
	{
		public OTypePattern Inner { get; } = inner;

		public override bool Matches(OType type)
		{
			if (type is ONamed { NullableOf: { } underlying })
				return Inner.Matches(underlying);

			var isReference = type is ONamed { IsReference: true } or OArray or ODynamic;

			return isReference && Inner.Matches(type);
		}
	}

	sealed class OArrayPattern(OTypePattern element, int rank) : OTypePattern
	{
		public override bool Matches(OType type)
		{
			return type is OArray array && array.Rank == rank && element.Matches(array.Element);
		}
	}

	sealed class OTuplePattern(IReadOnlyList<OTypePattern> elements) : OTypePattern
	{
		public override bool Matches(OType type)
		{
			if (type is not ONamed { TupleElements: { } actual } || actual.Count != elements.Count)
				return false;

			for (var i = 0; i < elements.Count; i++)
				if (!elements[i].Matches(actual[i]))
					return false;

			return true;
		}
	}

	sealed class OTypeAlternatives(IReadOnlyList<OTypePattern> alternatives) : OTypePattern
	{
		public override bool Matches(OType type)
		{
			return alternatives.Any(a => a.Matches(type));
		}
	}

	/// <summary>Quoted text in a type position: the exact name of a named type's last segment, or the full text of a pointer.</summary>
	sealed class OQuotedType(string text) : OTypePattern
	{
		public override bool Matches(OType type)
		{
			return type switch
			{
				OPointer or OFunctionPointer => type.Text == text,
				ONamed named                 => named.Segments[^1].Name == text,
				_                            => false,
			};
		}
	}

	sealed class OFunctionPointerPattern(IReadOnlyList<(OKind? Kind, OTypePattern Type)> types) : OTypePattern
	{
		public override bool Matches(OType type)
		{
			if (type is not OFunctionPointer pointer || pointer.Parameters.Count + 1 != types.Count)
				return false;

			for (var i = 0; i < pointer.Parameters.Count; i++)
				if (!types[i].Type.Matches(pointer.Parameters[i]))
					return false;

			return types[^1].Type.Matches(pointer.Returns);
		}
	}

	/// <summary>One parameter of <c>params:</c> or the value of <c>param:</c>.</summary>
	sealed class OParameterPattern(OKind? kind, bool isThis, bool isParams, OTypePattern? type)
	{
		public OKind?        Kind     { get; } = kind;
		public bool          IsThis   { get; } = isThis;
		public bool          IsParams { get; } = isParams;
		public OTypePattern? Type     { get; } = type;

		public bool Matches(OParameter parameter)
		{
			if (Kind is { } kind && parameter.Kind != kind)
				return false;

			if (IsThis && !parameter.IsThis)
				return false;

			if (IsParams && !parameter.IsParams)
				return false;

			return Type is null || Type.Matches(parameter.Type);
		}
	}

	/// <summary><c>(int, .., CancellationToken)</c>: <c>..</c> stands for any run of parameters, at most once.</summary>
	sealed class OParameterListPattern(IReadOnlyList<OParameterPattern> before, IReadOnlyList<OParameterPattern>? after)
	{
		public bool Matches(IReadOnlyList<OParameter> parameters)
		{
			if (after is null)
				return parameters.Count == before.Count && before.Select((p, i) => p.Matches(parameters[i])).All(static b => b);

			if (parameters.Count < before.Count + after.Count)
				return false;

			for (var i = 0; i < before.Count; i++)
				if (!before[i].Matches(parameters[i]))
					return false;

			for (var i = 0; i < after.Count; i++)
				if (!after[i].Matches(parameters[parameters.Count - after.Count + i]))
					return false;

			return true;
		}
	}

	/// <summary>Builds the regular expression of a segment glob as it is read.</summary>
	sealed class OGlobBuilder
	{
		readonly StringBuilder _regex = new();
		readonly StringBuilder _text  = new();

		public bool HasWildcard { get; private set; }
		public bool IsEscaped   { get; private set; }
		public bool IsEmpty     => _text.Length == 0 && !HasWildcard && !_quoted;

		bool _quoted;

		public void Literal(string text)
		{
			_regex.Append(Regex.Escape(text));
			_text.Append(text);
		}

		public void Quoted(string text)
		{
			_quoted   = true;
			IsEscaped = true;
			Literal(text);
		}

		public void Escaped(string text)
		{
			IsEscaped = true;
			Literal(text);
		}

		public void Star()
		{
			HasWildcard = true;
			_regex.Append("[^.]*");
			_text.Append('*');
		}

		public void Alternatives(IReadOnlyList<OGlobBuilder> alternatives)
		{
			HasWildcard = true;
			_regex.Append("(?:").Append(string.Join("|", alternatives.Select(static a => a._regex.ToString()))).Append(')');
			_text.Append('{').Append(string.Join(",", alternatives.Select(static a => a._text.ToString()))).Append('}');
		}

		public string Text => _text.ToString();

		public OGlob Build()
		{
			return new OGlob(_regex.ToString(), !HasWildcard && !IsEscaped, _text.ToString());
		}
	}
}
