using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;

using AspectGenerator.Cascade;

namespace AspectGenerator.Tests.Cascade
{
	/// <summary>A method as the cascade cases describe it.</summary>
	public sealed class CaseFact
	{
		public string       Id            { get; set; } = "";
		public string       Namespace     { get; set; } = "";
		public string       Type          { get; set; } = "";
		public string       Name          { get; set; } = "";
		public string       Accessibility { get; set; } = "public";
		public bool         Static        { get; set; }
		public bool         Async         { get; set; }
		public List<string> Attributes    { get; set; } = [];
		public string       Returns       { get; set; } = "System.Void";
		public bool         Project       { get; set; }
		public List<string> Modifiers     { get; set; } = [];

		public CaseFact AsProject()
		{
			var copy = (CaseFact)MemberwiseClone();

			copy.Project = true;

			return copy;
		}
	}

	/// <summary>
	/// Reads the subset of the filter language the cascade cases use into cascade lines. It stands in for the filter
	/// grammar, so that the cascade can be tested on its own: keys with simple dotted patterns, words, references,
	/// <c>any</c>, <c>not</c>, <c>and</c>, <c>or</c>, parentheses, one line per text line and <c>#</c> comments.
	/// </summary>
	static class CascadeTextReader
	{
		static readonly Regex _token = new(@"\G\s*(\(|\)|-(?=\s)|[A-Za-z_@*.{},<>\[\]?]+:|[^\s()]+)");
		static readonly Regex _ref   = new(@"^(aspect|assembly)\.([A-Za-z]+)$");

		static readonly Dictionary<string,CascadeProperty> _keys = new()
		{
			["namespace"]     = CascadeProperty.Location,
			["type"]          = CascadeProperty.Location,
			["method"]        = CascadeProperty.Method,
			["returns"]       = CascadeProperty.Returns,
			["attribute"]     = CascadeProperty.Attribute,
			["params"]        = CascadeProperty.Params,
			["param"]         = CascadeProperty.Param,
			["location"]      = CascadeProperty.Location,
			["accessibility"] = CascadeProperty.Accessibility,
			["static"]        = CascadeProperty.Static,
			["async"]         = CascadeProperty.Async,
			["virtuality"]    = CascadeProperty.Virtuality,
			["origin"]        = CascadeProperty.Origin,
			["extern"]        = CascadeProperty.Extern,
			["extension"]     = CascadeProperty.Extension,
			["generated"]     = CascadeProperty.Generated,
		};

		static readonly HashSet<string> _patternKeys = ["namespace", "type", "method", "returns", "attribute"];

		static readonly Dictionary<string,CascadeProperty> _words = new()
		{
			["public"]    = CascadeProperty.Accessibility,
			["internal"]  = CascadeProperty.Accessibility,
			["protected"] = CascadeProperty.Accessibility,
			["private"]   = CascadeProperty.Accessibility,
			["static"]    = CascadeProperty.Static,
			["instance"]  = CascadeProperty.Static,
			["async"]     = CascadeProperty.Async,
			["project"]   = CascadeProperty.Origin,
			["virtual"]   = CascadeProperty.Virtuality,
			["override"]  = CascadeProperty.Virtuality,
			["abstract"]  = CascadeProperty.Virtuality,
			["sealed"]    = CascadeProperty.Virtuality,
			["extern"]    = CascadeProperty.Extern,
			["extension"] = CascadeProperty.Extension,
			["generated"] = CascadeProperty.Generated,
		};

		static readonly Dictionary<string,CascadeProperty> _properties =
			Enum.GetValues<CascadeProperty>().Where(p => p != CascadeProperty.None).ToDictionary(p => p.ToFilterName());

		sealed class ReadException(string id, string message) : Exception(message)
		{
			public string Id { get; } = id;
		}

		public static (List<CascadeLine<CaseFact>> Lines, List<CascadeDiagnostic> Errors) Read(string? text)
		{
			var lines  = new List<CascadeLine<CaseFact>>();
			var errors = new List<CascadeDiagnostic>();

			foreach (var raw in (text ?? "").Split('\n'))
			{
				var line = raw.Split('#')[0].Trim();

				if (line.Length == 0)
					continue;

				try
				{
					lines.Add(new Parser(Tokenize(line), line).Line());
				}
				catch (ReadException ex)
				{
					errors.Add(new(ex.Id, ex.Message, null, line));
				}
			}

			return (lines, errors);
		}

		static List<string> Tokenize(string line)
		{
			var tokens = new List<string>();
			var pos    = 0;

			while (pos < line.Length)
			{
				var m = _token.Match(line, pos);

				if (!m.Success)
					throw new ReadException(CascadeDiagnosticIds.SyntaxError, "cannot read " + line[pos..]);

				tokens.Add(m.Groups[1].Value);
				pos = m.Index + m.Length;
			}

			return tokens;
		}

		sealed class Parser(List<string> tokens, string text)
		{
			int _i;

			string? Peek()
			{
				return _i < tokens.Count ? tokens[_i] : null;
			}

			string? Take()
			{
				var token = Peek();

				_i++;

				return token;
			}

			public CascadeLine<CaseFact> Line()
			{
				var subtraction = false;

				if (Peek() == "-")
				{
					Take();
					subtraction = true;
				}

				var condition = Expr();

				if (Peek() is { } rest)
					throw new ReadException(CascadeDiagnosticIds.SyntaxError, "unexpected " + rest);

				return new(subtraction, condition, text);
			}

			CascadeCondition<CaseFact> Expr()
			{
				return Junction(false, () => Junction(true, Unary));
			}

			CascadeCondition<CaseFact> Junction(bool isAnd, Func<CascadeCondition<CaseFact>> operand)
			{
				var word  = isAnd ? "and" : "or";
				var items = new List<CascadeCondition<CaseFact>> { operand() };

				while (Peek() == word)
				{
					Take();
					items.Add(operand());
				}

				return items.Count == 1 ? items[0] : new CascadeJunction<CaseFact>(isAnd, [..items]);
			}

			CascadeCondition<CaseFact> Unary()
			{
				if (Peek() == "not")
				{
					Take();

					if (Peek() is "(" or "not")
						throw new ReadException(CascadeDiagnosticIds.SyntaxError, "'not' stands only before a single condition");

					return new CascadeNot<CaseFact>(Atom());
				}

				if (Peek() == "(")
				{
					Take();

					var e = Expr();

					if (Take() != ")")
						throw new ReadException(CascadeDiagnosticIds.SyntaxError, "missing )");

					return e;
				}

				return Atom();
			}

			CascadeCondition<CaseFact> Atom()
			{
				var token = Take() ?? throw new ReadException(CascadeDiagnosticIds.SyntaxError, "condition expected");

				if (token.EndsWith(':'))
				{
					var key = token[..^1];

					if (!_keys.TryGetValue(key, out var property))
						throw new ReadException(CascadeDiagnosticIds.UnknownKeyOrWord, "unknown key " + key);

					var value = Take() ?? throw new ReadException(CascadeDiagnosticIds.SyntaxError, "value expected");

					if (value == "any")
						return new CascadeAny<CaseFact>(property);

					if (Reference(value, property) is { } keyed)
						return keyed;

					if (!_patternKeys.Contains(key))
						throw new ReadException(CascadeDiagnosticIds.UnknownKeyOrWord, key + ": takes only any or a reference");

					return KeyTest(property, key, value.TrimStart('@'), token + " " + value);
				}

				if (Reference(token, null) is { } reference)
					return reference;

				if (_words.TryGetValue(token, out var wordProperty))
					return WordTest(wordProperty, token);

				if (token == "any")
					throw new ReadException(CascadeDiagnosticIds.InvalidAny, "'any' is a whole value: write p: any");

				throw new ReadException(CascadeDiagnosticIds.UnknownKeyOrWord, "unknown word " + token);
			}
		}

		static CascadeReference<CaseFact>? Reference(string token, CascadeProperty? keyProperty)
		{
			var m = _ref.Match(token);

			if (!m.Success)
				return null;

			if (!_properties.TryGetValue(m.Groups[2].Value, out var property))
				throw new ReadException(CascadeDiagnosticIds.InvalidReference, "unknown property " + m.Groups[2].Value);

			return new(m.Groups[1].Value == "aspect" ? CascadeLevel.Aspect : CascadeLevel.Assembly, property, keyProperty);
		}

		static CascadeTest<CaseFact> WordTest(CascadeProperty property, string word)
		{
			Func<CaseFact,bool> matches = word switch
			{
				_ when property == CascadeProperty.Accessibility => f => f.Accessibility == word,
				"static"   => f => f.Static,
				"instance" => f => !f.Static,
				"async"    => f => f.Async,
				"project"  => f => f.Project,
				_          => f => f.Modifiers.Contains(word),
			};

			return new(property, word, matches);
		}

		static CascadeTest<CaseFact> KeyTest(CascadeProperty property, string key, string pattern, string text)
		{
			Func<CaseFact,bool> matches = key switch
			{
				"namespace" => f => MatchDotted(pattern, f.Namespace, rooted: true),
				"type"      => f => MatchDotted(pattern, f.Type,      rooted: false),
				"method"    => f => MatchDotted(pattern, f.Id,        rooted: false),
				"returns"   => f => MatchDotted(pattern, f.Returns,   rooted: false),
				"attribute" => f => AttributeNames(f).Any(n => MatchDotted(pattern, n, rooted: false)),
				_           => throw new InvalidOperationException(key),
			};

			return new(property, text, matches);
		}

		static IEnumerable<string> AttributeNames(CaseFact fact)
		{
			foreach (var name in fact.Attributes)
			{
				yield return name;
				yield return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name + "Attribute";
			}
		}

		/// <summary>
		/// A dotted pattern: <c>*</c> within a segment, <c>**</c> for any number of whole segments, rooted or matching a
		/// trailing run of segments; <c>global::</c> roots.
		/// </summary>
		static bool MatchDotted(string pattern, string name, bool rooted)
		{
			if (pattern.StartsWith("global::", StringComparison.Ordinal))
			{
				pattern = pattern["global::".Length..];
				rooted  = true;
			}

			var rx    = "";
			var first = true;

			foreach (var segment in pattern.Split('.'))
			{
				if (segment == "**")
					rx += first ? @"(?:(?:^|\.)[^.]+)*" : @"(?:\.[^.]+)*";
				else
					rx += (first ? "" : @"\.") + string.Concat(segment.Select(c => c == '*' ? "[^.]*" : Regex.Escape(c.ToString())));

				first = false;
			}

			return Regex.IsMatch(name, (rooted ? "^" : @"(?:^|\.)") + rx + "$");
		}
	}
}
