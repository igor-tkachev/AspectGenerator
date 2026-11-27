using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;

namespace AspectGenerator.Tests.FilterV2Oracle
{
	/// <summary>The properties of the reference text.</summary>
	enum OProperty
	{
		Location, Method, Returns, Params, Param, Attribute, Inherits, Implements,
		Accessibility, Static, Async, Virtuality, Extern, Extension, Origin, Generated,
	}

	abstract class OExpr
	{
		/// <summary>The properties the expression mentions.</summary>
		public abstract IEnumerable<OProperty> Properties { get; }

		public abstract bool Evaluate(OMethod method);

		public virtual IEnumerable<OExpr> Descendants()
		{
			yield return this;
		}
	}

	sealed class OOr(IReadOnlyList<OExpr> items) : OExpr
	{
		public IReadOnlyList<OExpr> Items { get; } = items;

		public override IEnumerable<OProperty> Properties => Items.SelectMany(static i => i.Properties);

		public override bool Evaluate(OMethod method)
		{
			return Items.Any(i => i.Evaluate(method));
		}

		public override IEnumerable<OExpr> Descendants()
		{
			return Items.SelectMany(static i => i.Descendants()).Prepend(this);
		}
	}

	/// <summary>An <c>and</c>, or a run of words and a compact signature, which is read as one.</summary>
	sealed class OAnd(IReadOnlyList<OExpr> items, bool isRun) : OExpr
	{
		public IReadOnlyList<OExpr> Items { get; } = items;

		/// <summary>A run of words or a compact signature: several conditions written without <c>and</c>.</summary>
		public bool IsRun { get; } = isRun;

		public override IEnumerable<OProperty> Properties => Items.SelectMany(static i => i.Properties);

		public override bool Evaluate(OMethod method)
		{
			return Items.All(i => i.Evaluate(method));
		}

		public override IEnumerable<OExpr> Descendants()
		{
			return Items.SelectMany(static i => i.Descendants()).Prepend(this);
		}
	}

	sealed class ONot(OExpr inner) : OExpr
	{
		public OExpr Inner { get; } = inner;

		public override IEnumerable<OProperty> Properties => Inner.Properties;

		public override bool Evaluate(OMethod method)
		{
			return !Inner.Evaluate(method);
		}

		public override IEnumerable<OExpr> Descendants()
		{
			return Inner.Descendants().Prepend(this);
		}
	}

	sealed class OGroup(OExpr inner) : OExpr
	{
		public OExpr Inner { get; } = inner;

		public override IEnumerable<OProperty> Properties => Inner.Properties;

		public override bool Evaluate(OMethod method)
		{
			return Inner.Evaluate(method);
		}

		public override IEnumerable<OExpr> Descendants()
		{
			return Inner.Descendants().Prepend(this);
		}
	}

	sealed class OCondition(OProperty property, string text, Func<OMethod,bool> test) : OExpr
	{
		public OProperty Property { get; } = property;
		public string    Text     { get; } = text;

		public override IEnumerable<OProperty> Properties => [Property];

		public override bool Evaluate(OMethod method)
		{
			return test(method);
		}

		public override string ToString()
		{
			return Text;
		}
	}

	/// <summary><c>aspect.p</c> or <c>assembly.p</c>, standing alone or as the value of a key.</summary>
	sealed class OReference(string level, string name, OProperty? property, OProperty? declared) : OExpr
	{
		public string     Level    { get; } = level;
		public string     Name     { get; } = name;

		/// <summary>The property it names, if the name is one.</summary>
		public OProperty? Property { get; } = property;

		/// <summary>The property of the key it is the value of.</summary>
		public OProperty? Declared { get; } = declared;

		public override IEnumerable<OProperty> Properties => Declared is { } d ? [d] : Property is { } p ? [p] : [];

		/// <summary>In a block with nothing above it but an empty aspect default, the value from above is no restriction.</summary>
		public override bool Evaluate(OMethod method)
		{
			return true;
		}
	}

	/// <summary><c>p: any</c>.</summary>
	sealed class OAny(OProperty property) : OExpr
	{
		public OProperty Property { get; } = property;

		public override IEnumerable<OProperty> Properties => [Property];

		public override bool Evaluate(OMethod method)
		{
			return true;
		}
	}

	sealed class OLine(bool isSubtraction, OExpr expr, string text)
	{
		public bool   IsSubtraction { get; } = isSubtraction;
		public OExpr  Expr          { get; } = expr;
		public string Text          { get; } = text;
	}

	/// <summary>One part of a line after the split by property: its properties and its expressions.</summary>
	sealed class OPart(bool isSubtraction, IReadOnlyList<OExpr> items)
	{
		public bool                 IsSubtraction { get; } = isSubtraction;
		public IReadOnlyList<OExpr> Items         { get; } = items;

		public HashSet<OProperty> Properties { get; } = [..items.SelectMany(static i => i.Properties)];

		public bool Evaluate(OMethod method)
		{
			// An include part holds when all its conjuncts hold; a subtraction part when any of its disjuncts does.
			return IsSubtraction ? Items.Any(i => i.Evaluate(method)) : Items.All(i => i.Evaluate(method));
		}
	}

	/// <summary>
	/// One filter as the oracle reads it, at one level: its lines, the parts they split into, the first error the
	/// oracle finds (any error turns the aspect off), and what it selects.
	/// </summary>
	sealed class OracleFilter
	{
		OracleFilter(string? error, IReadOnlyList<OLine> lines, IReadOnlyList<OPart> parts, bool implicitProject)
		{
			Error           = error;
			Lines           = lines;
			Parts           = parts;
			ImplicitProject = implicitProject;
		}

		public string?              Error           { get; }
		public IReadOnlyList<OLine> Lines           { get; }
		public IReadOnlyList<OPart> Parts           { get; }
		public bool                 ImplicitProject { get; }

		/// <summary>
		/// Reads <paramref name="text"/> as the filter of an assembly attribute of an aspect whose default filter is empty:
		/// the only references allowed are <c>aspect.p</c>, each of them no restriction.
		/// </summary>
		public static OracleFilter Assembly(string text)
		{
			List<OLine> lines;

			try
			{
				lines = [..OracleReader.Read(text)];
			}
			catch (OracleError e)
			{
				return new OracleFilter(e.Message, [], [], false);
			}

			var parts = new List<OPart>();

			foreach (var line in lines)
				parts.AddRange(Split(line));

			var error = Check(lines, parts);

			// The assembly level stays inside the project unless the filter declares origin or includes a place.
			var implicitProject = !parts.Any(static p => p.Properties.Contains(OProperty.Origin))
				&& !parts.Any(static p => !p.IsSubtraction && (p.Properties.Contains(OProperty.Location) || p.Properties.Contains(OProperty.Method)));

			return new OracleFilter(error, lines, parts, implicitProject);
		}

		/// <summary>Whether the block selects the method; <see langword="null"/> when the filter has an error.</summary>
		public bool? Selects(OMethod method)
		{
			if (Error is not null)
				return null;

			if (ImplicitProject && !method.IsProject)
				return false;

			foreach (var part in Parts)
				if (part.Evaluate(method) == part.IsSubtraction)
					return false;

			return true;
		}

		// An include splits at its top-level `and` (a run of words or a signature counts as one), a subtraction at its
		// top-level `or`, into the groups of items that share no property. Parentheses around an `and` inside an `and`
		// (or an `or` inside an `or`) change nothing, as the operator is associative, so they do not hide its operands.
		static IEnumerable<OPart> Split(OLine line)
		{
			var items = new List<OExpr>();

			void Flatten(OExpr e)
			{
				while (e is OGroup group && (group.Inner is OGroup || group.Inner is OAnd && !line.IsSubtraction || group.Inner is OOr && line.IsSubtraction))
					e = group.Inner;

				if (!line.IsSubtraction && e is OAnd and)
				{
					foreach (var i in and.Items)
						Flatten(i);
				}
				else if (line.IsSubtraction && e is OOr or)
				{
					foreach (var i in or.Items)
						Flatten(i);
				}
				else
				{
					items.Add(e);
				}
			}

			Flatten(line.Expr);

			var groups = items.Select(static i => (Items: new List<OExpr> { i }, Properties: new HashSet<OProperty>(i.Properties))).ToList();

			for (var merged = true; merged;)
			{
				merged = false;

				for (var a = 0; a < groups.Count && !merged; a++)
					for (var b = a + 1; b < groups.Count && !merged; b++)
						if (groups[a].Properties.Overlaps(groups[b].Properties))
						{
							groups[a].Items.AddRange(groups[b].Items);
							groups[a].Properties.UnionWith(groups[b].Properties);
							groups.RemoveAt(b);
							merged = true;
						}
			}

			return groups.Select(g => new OPart(line.IsSubtraction, g.Items));
		}

		static string? Check(List<OLine> lines, List<OPart> parts)
		{
			foreach (var line in lines)
			{
				foreach (var any in line.Expr.Descendants().OfType<OAny>())
				{
					if (line.IsSubtraction)
						return "'- p: any' is not allowed.";

					var alone = line.Expr;

					// Parentheses around it add no condition.
					while (alone is OGroup group)
						alone = group.Inner;

					if (!ReferenceEquals(alone, any))
						return "'any' stands alone on its line.";
				}
			}

			foreach (var part in parts)
			{
				foreach (var reference in part.Items.SelectMany(static i => i.Descendants()).OfType<OReference>())
				{
					if (reference.Property is null)
						return $"'{reference.Name}' is not a property.";

					if (reference.Declared is { } declared && declared != reference.Property)
						return $"'{reference.Level}.{reference.Name}' names another property than its key.";

					if (reference.Level != "aspect")
						return $"'{reference.Level}.' is not above an assembly filter.";

					if (part.Properties.Count != 1)
						return "A reference stands in a part about several properties.";
				}
			}

			var includes = new Dictionary<OProperty,int>();

			foreach (var part in parts.Where(static p => !p.IsSubtraction))
				foreach (var property in part.Properties)
					includes[property] = includes.GetValueOrDefault(property) + 1;

			if (includes.FirstOrDefault(static i => i.Value > 1) is { Value: > 1 } repeated)
				return $"'{repeated.Key}' is included {repeated.Value} times.";

			return null;
		}
	}

	sealed class OracleError(string message) : Exception(message);

	/// <summary>Reads the text of a filter into lines; throws <see cref="OracleError"/> at the first error.</summary>
	static class OracleReader
	{
		public static IEnumerable<OLine> Read(string text)
		{
			var logical = new List<string>();
			var current = (System.Text.StringBuilder?)null;
			var depth   = 0;

			foreach (var raw in text.Split('\n'))
			{
				var physical = StripComment(raw.TrimEnd('\r'));
				var trimmed  = physical.Trim(' ', '\t', '\r');

				if (trimmed.Length == 0)
				{
					if (depth > 0)
						current!.Append('\n');

					continue;
				}

				var continues = StartsWithWord(trimmed, "and") || StartsWithWord(trimmed, "or");

				if (depth > 0 || continues)
				{
					if (current is null)
						throw new OracleError("A filter begins with 'and' or 'or'.");

					current.Append('\n').Append(physical);
				}
				else
				{
					if (current is not null)
						logical.Add(current.ToString());

					current = new System.Text.StringBuilder(physical);
				}

				depth += Depth(physical);
			}

			if (current is not null)
				logical.Add(current.ToString());

			if (depth > 0)
				throw new OracleError("A '(' is not closed.");

			foreach (var line in logical)
				yield return new OParser(line).Line();
		}

		static bool StartsWithWord(string text, string word)
		{
			return text.StartsWith(word, StringComparison.Ordinal) && (text.Length == word.Length || text[word.Length] is ' ' or '\t' or '(' or '\r');
		}

		// The change in open parentheses over a physical line, outside quoted text.
		static int Depth(string line)
		{
			var depth  = 0;
			var quoted = false;

			for (var i = 0; i < line.Length; i++)
			{
				var c = line[i];

				if (quoted)
				{
					if (c == '\\')
						i++;
					else if (c == '"')
						quoted = false;
				}
				else if (c == '"')
				{
					quoted = true;
				}
				else if (c == '(')
				{
					depth++;
				}
				else if (c == ')')
				{
					depth--;
				}
			}

			if (quoted)
				throw new OracleError("Quoted text is not closed on its line.");

			return depth;
		}

		static string StripComment(string line)
		{
			var quoted = false;

			for (var i = 0; i < line.Length; i++)
			{
				var c = line[i];

				if (quoted)
				{
					if (c == '\\')
						i++;
					else if (c == '"')
						quoted = false;
				}
				else if (c == '"')
				{
					quoted = true;
				}
				else if (c == '#')
				{
					return line[..i];
				}
			}

			return line;
		}
	}

	/// <summary>A recursive-descent reader of one logical line.</summary>
	sealed class OParser(string s)
	{
		int _p;

		static readonly HashSet<string> _keywords =
		[
			"and", "or", "not", "any", "aspect", "assembly",
			"namespace", "type", "method", "returns", "params", "param", "attribute", "inherits", "implements", "overrides",
			"location", "accessibility", "static", "async", "virtuality", "extern", "extension", "origin", "generated",
			"public", "internal", "protected", "private", "instance", "virtual", "override", "abstract", "sealed", "project",
			"within", "iterator", "unsafe", "partial", "readonly", "exposed", "signature",
			"ref", "out", "in", "this",
		];

		static readonly HashSet<string> _reservedWords = ["iterator", "unsafe", "partial", "readonly", "exposed"];

		static readonly HashSet<string> _legacyKeys = ["contains", "regex", "signature", "pattern", "fulltype", "fullmethod", "path", "attributes"];

		static readonly Dictionary<string,OProperty> _keys = new(StringComparer.Ordinal)
		{
			["namespace"] = OProperty.Location, ["type"] = OProperty.Location, ["method"] = OProperty.Method,
			["returns"] = OProperty.Returns, ["params"] = OProperty.Params, ["param"] = OProperty.Param,
			["attribute"] = OProperty.Attribute, ["inherits"] = OProperty.Inherits, ["implements"] = OProperty.Implements,
		};

		/// <summary>The property names: keys for <c>any</c> and references only (and the names references use).</summary>
		static readonly Dictionary<string,OProperty> _properties = new(StringComparer.Ordinal)
		{
			["location"] = OProperty.Location, ["method"] = OProperty.Method, ["returns"] = OProperty.Returns,
			["params"] = OProperty.Params, ["param"] = OProperty.Param, ["attribute"] = OProperty.Attribute,
			["inherits"] = OProperty.Inherits, ["implements"] = OProperty.Implements,
			["accessibility"] = OProperty.Accessibility, ["static"] = OProperty.Static, ["async"] = OProperty.Async,
			["virtuality"] = OProperty.Virtuality, ["extern"] = OProperty.Extern, ["extension"] = OProperty.Extension,
			["origin"] = OProperty.Origin, ["generated"] = OProperty.Generated,
		};

		static readonly HashSet<string> _wordOnlyKeys = ["location", "accessibility", "static", "async", "virtuality", "extern", "extension", "origin", "generated"];

		/// <summary><c>overrides:</c> is not a key of its own: <c>implements:</c> covers what a method overrides.</summary>
		public const bool OverridesIsKey = false;

		bool End => _p >= s.Length;
		char C   => _p < s.Length ? s[_p] : '\0';

		char At(int offset)
		{
			return _p + offset < s.Length ? s[_p + offset] : '\0';
		}

		static OracleError Error(string message)
		{
			return new OracleError(message);
		}

		void SkipSpace()
		{
			while (!End && C is ' ' or '\t' or '\r' or '\n')
				_p++;
		}

		// White space inside a pattern (between the items of a list): no line break.
		void SkipInline()
		{
			while (!End && C is ' ' or '\t')
				_p++;
		}

		static bool IsIdentStart(char c)
		{
			return char.IsLetter(c) || c == '_';
		}

		static bool IsIdentPart(char c)
		{
			return char.IsLetterOrDigit(c) || c == '_';
		}

		bool IsWordAt(string word)
		{
			if (string.CompareOrdinal(s, _p, word, 0, word.Length) != 0)
				return false;

			var next = _p + word.Length < s.Length ? s[_p + word.Length] : '\0';

			return next is '\0' or ' ' or '\t' or '\r' or '\n' or '(' or ')';
		}

		string PeekIdent()
		{
			var q = _p;

			while (q < s.Length && IsIdentPart(s[q]))
				q++;

			return s[_p..q];
		}

		public OLine Line()
		{
			SkipSpace();

			var sub = false;

			if (C == '-')
			{
				sub = true;
				_p++;
				SkipSpace();
			}

			if (End)
				throw Error("A line holds no condition.");

			var expr = Or();

			SkipSpace();

			if (!End)
				throw Error($"Unexpected '{s[_p..]}'.");

			return new OLine(sub, expr, s);
		}

		OExpr Or()
		{
			var items = new List<OExpr> { And() };

			while (true)
			{
				SkipSpace();

				if (!IsWordAt("or"))
					break;

				_p += 2;
				items.Add(And());
			}

			return items.Count == 1 ? items[0] : new OOr(items);
		}

		OExpr And()
		{
			var items = new List<OExpr> { Unary() };

			while (true)
			{
				SkipSpace();

				if (!IsWordAt("and"))
					break;

				_p += 3;
				items.Add(Unary());
			}

			return items.Count == 1 ? items[0] : new OAnd(items, false);
		}

		OExpr Unary()
		{
			SkipSpace();

			if (IsWordAt("not"))
			{
				_p += 3;
				SkipSpace();

				if (C == '(')
					throw Error("'not' stands before one condition, not before a group.");

				if (IsWordAt("not"))
					throw Error("'not not'.");

				var atom = Atom();

				if (atom is OAnd { IsRun: true })
					throw Error("'not' stands before one condition, not before a run of words or a signature.");

				return new ONot(atom);
			}

			if (C == '(')
			{
				_p++;

				var inner = Or();

				SkipSpace();

				if (C != ')')
					throw Error("')' expected.");

				_p++;

				return new OGroup(inner);
			}

			return Atom();
		}

		// A key with its value, a reference, or a run of words that a compact signature may end.
		OExpr Atom()
		{
			SkipSpace();

			var items = new List<OExpr>();

			while (true)
			{
				SkipSpace();

				if (End || C == ')' || IsWordAt("and") || IsWordAt("or"))
					break;

				if (items.Count > 0 && items[^1] is not OCondition { Property: OProperty.Accessibility or OProperty.Static or OProperty.Async or OProperty.Virtuality or OProperty.Extern or OProperty.Extension or OProperty.Origin or OProperty.Generated })
					throw Error($"Unexpected '{s[_p..]}' after a condition.");

				if (IsIdentStart(C))
				{
					var start = _p;
					var ident = PeekIdent();
					var q     = _p + ident.Length;

					while (q < s.Length && s[q] is ' ' or '\t')
						q++;

					var isKey = q < s.Length && s[q] == ':' && (q + 1 >= s.Length || s[q + 1] != ':');

					if (isKey && char.IsAsciiLetterLower(ident[0]))
					{
						if (items.Count > 0)
							throw Error("A key stands after a word.");

						_p = q + 1;

						return KeyValue(ident);
					}

					if (isKey && (_keys.ContainsKey(ident.ToLowerInvariant()) || _wordOnlyKeys.Contains(ident.ToLowerInvariant())))
						throw Error($"Keys are lowercase: '{ident.ToLowerInvariant()}:'.");

					if (ident is "aspect" or "assembly" && At(ident.Length) == '.')
					{
						if (items.Count > 0)
							throw Error("A reference stands after a word.");

						return Reference(null);
					}

					var next = At(ident.Length);
					var ends = next is '\0' or ' ' or '\t' or '\r' or '\n' or ')';

					if (ends && Word(ident) is { } word)
					{
						items.Add(word);
						continue;
					}

					if (ends && _keywords.Contains(ident))
						throw Error($"'{ident}' is a keyword here.");

					_p = start;
				}
				else if (C == '/')
				{
					throw Error("'/…/' literals are reserved.");
				}
				else if (C is not ('*' or '{' or '@' or '"'))
				{
					throw Error($"Unexpected '{C}'.");
				}

				items.AddRange(Signature());

				// A signature ends the run.
				SkipSpace();

				if (!End && C != ')' && !IsWordAt("and") && !IsWordAt("or"))
					throw Error($"Unexpected '{s[_p..]}' after a signature.");

				break;
			}

			if (items.Count == 0)
				throw Error("A condition is expected.");

			return items.Count == 1 ? items[0] : new OAnd(items, true);
		}

		OExpr? Word(string ident)
		{
			switch (ident)
			{
				case "public":
					_p += ident.Length;
					return Accessibility("public", Microsoft.CodeAnalysis.Accessibility.Public);

				case "internal":
				case "protected":
				case "private":
				{
					_p += ident.Length;

					var save = _p;

					SkipInline();

					var second = PeekIdent();
					var after  = At(second.Length);
					var ends   = after is '\0' or ' ' or '\t' or '\r' or '\n' or ')';

					if (ends && (ident, second) is ("protected", "internal") or ("internal", "protected"))
					{
						_p += second.Length;
						return Accessibility(ident + " " + second, Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal);
					}

					if (ends && (ident, second) is ("private", "protected") or ("protected", "private"))
					{
						_p += second.Length;
						return Accessibility(ident + " " + second, Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal);
					}

					_p = save;

					return Accessibility(ident, ident switch
					{
						"internal"  => Microsoft.CodeAnalysis.Accessibility.Internal,
						"protected" => Microsoft.CodeAnalysis.Accessibility.Protected,
						_           => Microsoft.CodeAnalysis.Accessibility.Private,
					});
				}
			}

			Func<OMethod,bool>? test;
			OProperty           property;

			switch (ident)
			{
				case "static":    property = OProperty.Static;     test = static m => m.IsStatic;    break;
				case "instance":  property = OProperty.Static;     test = static m => !m.IsStatic;   break;
				case "async":     property = OProperty.Async;      test = static m => m.IsAsync;     break;
				case "virtual":   property = OProperty.Virtuality; test = static m => m.IsVirtual;   break;
				case "override":  property = OProperty.Virtuality; test = static m => m.IsOverride;  break;
				case "abstract":  property = OProperty.Virtuality; test = static m => m.IsAbstract;  break;
				case "sealed":    property = OProperty.Virtuality; test = static m => m.IsSealed;    break;
				case "extern":    property = OProperty.Extern;     test = static m => m.IsExtern;    break;
				case "extension": property = OProperty.Extension;  test = static m => m.IsExtension; break;
				case "project":   property = OProperty.Origin;     test = static m => m.IsProject;   break;
				case "generated": property = OProperty.Generated;  test = static m => m.IsGenerated; break;
				default:
					if (_reservedWords.Contains(ident))
						throw Error($"'{ident}' is reserved.");

					return null;
			}

			_p += ident.Length;

			return new OCondition(property, ident, test);
		}

		static OCondition Accessibility(string text, Accessibility accessibility)
		{
			return new OCondition(OProperty.Accessibility, text, m => m.Accessibility == accessibility);
		}

		OExpr Reference(OProperty? declared)
		{
			var level = PeekIdent();

			_p += level.Length + 1;

			var name = PeekIdent();

			_p += name.Length;

			if (!End && C is not (' ' or '\t' or '\r' or '\n' or ')'))
				throw Error($"'{level}.{name}{C}' is not a reference.");

			return new OReference(level, name, _properties.TryGetValue(name, out var p) ? p : null, declared);
		}

		OExpr KeyValue(string key)
		{
			if (_legacyKeys.Contains(key))
				throw Error($"'{key}:' is 1.1 syntax.");

			if (key is "within" or "assembly")
				throw Error($"'{key}:' is reserved.");

			if (key == "overrides" && !OverridesIsKey)
				throw Error("'overrides:' is not a key; 'implements:' covers overrides.");

			var isKey      = _keys.TryGetValue(key, out var property);
			var isWordOnly = _wordOnlyKeys.Contains(key);

			if (!isKey && !isWordOnly)
				throw Error($"'{key}:' is not a key.");

			if (isWordOnly)
				property = _properties[key];

			SkipSpace();

			if (IsWordAt("any"))
			{
				_p += 3;
				return new OAny(property);
			}

			if ((PeekIdent() is "aspect" or "assembly") && At(PeekIdent().Length) == '.')
				return Reference(property);

			if (isWordOnly)
				throw Error($"'{key}:' takes only 'any' or a reference.");

			var text  = s[_p..];
			var start = _p;

			OExpr result;

			switch (key)
			{
				case "namespace":
				{
					var pattern = Name(isNamespace: true);
					result = new OCondition(property, "namespace: " + Written(start), m => pattern.Matches(m.Namespace, OAnchor.Rooted, aliasHead: false));
					break;
				}

				case "type":
				{
					var pattern = Type();
					result = new OCondition(property, "type: " + Written(start), m => pattern.Matches(m.ContainingType));
					break;
				}

				case "method":
				{
					var pattern = Name(isNamespace: false);
					result = new OCondition(property, "method: " + Written(start), m => pattern.Matches(m.Path, OAnchor.Trailing, aliasHead: true));
					break;
				}

				case "returns":
					result = Returns(start);
					break;

				case "params":
				{
					if (C != '(')
						throw Error("'params:' takes a parameter list in parentheses.");

					var list = ParameterList();
					result = new OCondition(property, "params: " + Written(start), m => list.Matches(m.Parameters));
					break;
				}

				case "param":
				{
					if (C == '.' && At(1) == '.')
						throw Error("'..' stands only inside a 'params:' list.");

					var parameter = Parameter();
					result = new OCondition(property, "param: " + Written(start), m => m.Parameters.Any(parameter.Matches));
					break;
				}

				case "attribute":
				{
					var pattern = Type();
					result = new OCondition(property, "attribute: " + Written(start), m => m.Attributes.Any(a => pattern.Matches(a) || a.Segments[^1].Name is { Length: > 9 } n && n.EndsWith("Attribute", StringComparison.Ordinal) && pattern.Matches(a.WithLastName(n[..^9]))));
					break;
				}

				case "inherits":
				{
					var pattern = Type();
					result = new OCondition(property, "inherits: " + Written(start), m => m.Inherited.Any(pattern.Matches));
					break;
				}

				case "implements":
				{
					var pattern = Name(isNamespace: false);
					result = new OCondition(property, "implements: " + Written(start), m => m.Implemented.Any(i => pattern.Matches(i.Path, OAnchor.Trailing, aliasHead: true)));
					break;
				}

				default:
					throw Error($"'{key}:' is not a key ({text}).");
			}

			return result;
		}

		string Written(int start)
		{
			return s[start.._p];
		}

		OExpr Returns(int start)
		{
			OKind? kind = null;

			if (IsWordAt("ref"))
			{
				_p += 3;
				SkipInline();
				kind = OKind.Ref;

				if (IsWordAt("readonly"))
				{
					_p += 8;
					SkipInline();
					kind = OKind.RefReadonly;
				}

				if (End || C is ' ' or '\t' or '\r' or '\n' or ')')
					throw Error("'returns: ref' needs a type.");
			}

			var pattern = Type();

			return new OCondition(OProperty.Returns, "returns: " + Written(start), m => (kind is null || m.ReturnKind == kind) && pattern.Matches(m.Returns));
		}

		// [words] path [(parameters)] [: return type]; the words were read by the caller.
		IEnumerable<OExpr> Signature()
		{
			var start   = _p;
			var path    = Name(isNamespace: false);
			var written = Written(start);
			var result  = new List<OExpr> { new OCondition(OProperty.Method, "method: " + written, m => path.Matches(m.Path, OAnchor.Trailing, aliasHead: true)) };

			SkipInline();

			if (C == '(')
			{
				var listStart = _p;
				var list      = ParameterList();

				result.Add(new OCondition(OProperty.Params, "params: " + Written(listStart), m => list.Matches(m.Parameters)));
				SkipInline();
			}

			if (C == ':' && At(1) != ':')
			{
				_p++;
				SkipInline();
				result.Add(Returns(_p));
			}

			return result;
		}

		// ------------------------------------------------------------------------------------------------- patterns

		static bool IsPatternEnd(char c)
		{
			return c is '\0' or ' ' or '\t' or '\r' or '\n' or ')' or '(' or ':' or ',' or '>' or ']' or '}';
		}

		/// <summary>A dotted name: <c>namespace:</c>, <c>method:</c>, <c>implements:</c>, the path of a signature.</summary>
		ONamePattern Name(bool isNamespace)
		{
			if (isNamespace && C == '"' && At(1) == '"' && IsPatternEnd(At(2)))
			{
				_p += 2;
				return new ONamePattern(false, []) { IsGlobalNamespace = true };
			}

			if (C == '{' && WholeAlternatives())
			{
				_p++;

				var alternatives = new List<ONamePattern>();

				while (true)
				{
					SkipInline();

					if (isNamespace && C == '"' && At(1) == '"')
					{
						_p += 2;
						alternatives.Add(new ONamePattern(false, []) { IsGlobalNamespace = true });
					}
					else
					{
						alternatives.Add(Path(isNamespace, allowTypeArguments: !isNamespace));
					}

					SkipInline();

					if (C == ',')
					{
						_p++;
						continue;
					}

					if (C != '}')
						throw Error("'}' expected.");

					_p++;
					break;
				}

				return new ONamePattern(alternatives);
			}

			return Path(isNamespace, allowTypeArguments: !isNamespace);
		}

		// `{…}` holds whole patterns when it is the whole pattern: nothing but the end of the pattern follows its `}`.
		bool WholeAlternatives()
		{
			var depth = 0;

			for (var q = _p; q < s.Length; q++)
			{
				var c = s[q];

				if (c == '"')
				{
					for (q++; q < s.Length && s[q] != '"'; q++)
						if (s[q] == '\\')
							q++;
				}
				else if (c is '{' or '<')
				{
					depth++;
				}
				else if (c is '}' or '>')
				{
					depth--;

					if (depth == 0)
					{
						if (c != '}')
							return false;

						var next = q + 1 < s.Length ? s[q + 1] : '\0';

						return IsPatternEnd(next) || next is '?' or '[';
					}
				}
			}

			return false;
		}

		ONamePattern Path(bool isNamespace, bool allowTypeArguments, bool isType = false)
		{
			var rooted = false;

			if (string.CompareOrdinal(s, _p, "global::", 0, 8) == 0)
			{
				rooted = true;
				_p += 8;
			}

			var segments = new List<OSegmentPattern>();

			while (true)
			{
				segments.Add(Segment(allowTypeArguments));

				if (C == '.' && At(1) != '.')
				{
					_p++;
					continue;
				}

				if (C == '.')
					throw Error("'..' in a name.");

				break;
			}

			if (C == '?' && !isType)
				throw Error("'?' stands only after a type.");

			if (C == '\\')
				throw Error("There are no backslash escapes.");

			if (string.CompareOrdinal(s, _p, "::", 0, 2) == 0)
				throw Error("'global::' stands only at the start of a pattern.");

			return new ONamePattern(rooted, segments);
		}

		OSegmentPattern Segment(bool allowTypeArguments)
		{
			if (C == '*' && At(1) == '*')
			{
				_p += 2;

				if (!IsPatternEnd(C) && C != '.')
					throw Error("'**' is a whole segment.");

				return new OGlobstar();
			}

			var glob = Glob(inAlternative: false);

			if (glob.IsEmpty)
				throw Error($"A name is expected at '{s[_p..]}'.");

			if (!glob.HasWildcard && !glob.IsEscaped && _keywords.Contains(glob.Text))
				throw Error($"'{glob.Text}' is a keyword; write '@{glob.Text}'.");

			IReadOnlyList<OTypePattern>? arguments = null;

			if (C == '<')
			{
				if (!allowTypeArguments)
					throw Error("A namespace has no type arguments.");

				_p++;
				arguments = TypeList('>');
			}

			return new ONameSegment(glob.Build(), arguments);
		}

		OGlobBuilder Glob(bool inAlternative)
		{
			var glob = new OGlobBuilder();

			while (!End)
			{
				var c = C;

				if (IsIdentPart(c))
				{
					var q = _p;

					while (q < s.Length && IsIdentPart(s[q]))
						q++;

					glob.Literal(s[_p..q]);
					_p = q;
				}
				else if (c == '*')
				{
					if (At(1) == '*')
						throw Error("'**' inside a segment.");

					glob.Star();
					_p++;
				}
				else if (c == '@')
				{
					_p++;

					var ident = PeekIdent();

					if (ident.Length == 0)
						throw Error("'@' before nothing.");

					glob.Escaped(ident);
					_p += ident.Length;
				}
				else if (c == '"')
				{
					glob.Quoted(Quoted());
				}
				else if (c == '{')
				{
					if (inAlternative)
						throw Error("Nested '{'.");

					_p++;

					var alternatives = new List<OGlobBuilder>();

					while (true)
					{
						SkipInline();

						var alternative = Glob(inAlternative: true);

						if (alternative.IsEmpty)
							throw Error("An empty alternative.");

						alternatives.Add(alternative);
						SkipInline();

						if (C == ',')
						{
							_p++;
							continue;
						}

						if (C != '}')
							throw Error("'}' expected in a name.");

						_p++;
						break;
					}

					glob.Alternatives(alternatives);
				}
				else if (c == '\\')
				{
					throw Error("There are no backslash escapes.");
				}
				else
				{
					break;
				}
			}

			return glob;
		}

		string Quoted()
		{
			_p++;

			var sb = new System.Text.StringBuilder();

			while (true)
			{
				if (End || C == '\n')
					throw Error("Quoted text is not closed.");

				if (C == '"')
				{
					_p++;
					return sb.ToString();
				}

				if (C == '\\')
				{
					if (At(1) is not ('"' or '\\'))
						throw Error("Only '\\\"' and '\\\\' escape in quoted text.");

					sb.Append(At(1));
					_p += 2;
					continue;
				}

				sb.Append(C);
				_p++;
			}
		}

		IReadOnlyList<OTypePattern> TypeList(char close)
		{
			var items = new List<OTypePattern>();

			while (true)
			{
				SkipInline();

				if (C == close || C == ',')
					throw Error("A type is expected.");

				items.Add(Type());
				SkipInline();

				if (C == ',')
				{
					_p++;
					continue;
				}

				if (C != close)
					throw Error($"'{close}' expected.");

				_p++;
				return items;
			}
		}

		/// <summary>A type pattern with its suffixes: <c>?</c> and array ranks, read as C# reads them.</summary>
		OTypePattern Type()
		{
			var type   = BaseType();
			var ranks  = new List<int>();
			var marked = false;

			while (C is '?' or '[')
			{
				if (C == '?')
				{
					if (marked)
						throw Error("'??'.");

					type   = new ONullablePattern(ApplyRanks(type, ranks));
					ranks  = [];
					marked = true;
					_p++;
					continue;
				}

				_p++;

				var rank = 1;

				while (C == ',')
				{
					rank++;
					_p++;
				}

				if (C != ']')
					throw Error("']' expected.");

				_p++;
				ranks.Add(rank);
				marked = false;
			}

			return ApplyRanks(type, ranks);
		}

		// The rank specifiers of one run stand in C# order, the outermost first.
		static OTypePattern ApplyRanks(OTypePattern element, List<int> ranks)
		{
			for (var i = ranks.Count - 1; i >= 0; i--)
				element = new OArrayPattern(element, ranks[i]);

			return element;
		}

		OTypePattern BaseType()
		{
			if (string.CompareOrdinal(s, _p, "delegate*", 0, 9) == 0)
				return FunctionPointer();

			if (C == '*' && At(1) != '*' && (IsPatternEnd(At(1)) || At(1) is '?' or '['))
			{
				_p++;
				return new OAnyType();
			}

			if (C == '(')
			{
				_p++;

				var elements = TypeList(')');

				if (elements.Count < 2)
					throw Error("A tuple has two elements or more.");

				return new OTuplePattern(elements);
			}

			if (C == '{' && WholeAlternatives())
			{
				_p++;

				var alternatives = TypeList('}');

				return new OTypeAlternatives(alternatives);
			}

			if (C == '"')
			{
				var save = _p;
				var text = Quoted();

				if (IsPatternEnd(C) || C is '?' or '[')
					return new OQuotedType(text);

				_p = save;
			}

			return new ONamedTypePattern(Path(isNamespace: false, allowTypeArguments: true, isType: true));
		}

		OTypePattern FunctionPointer()
		{
			_p += 9;
			SkipInline();

			if (IsWordAt("managed") || PeekIdent() == "managed")
			{
				_p += 7;
				SkipInline();
			}
			else if (PeekIdent() == "unmanaged")
			{
				_p += 9;
				SkipInline();

				if (C == '[')
				{
					while (!End && C != ']')
						_p++;

					if (C != ']')
						throw Error("']' expected.");

					_p++;
					SkipInline();
				}
			}

			if (C != '<')
				throw Error("'<' expected after 'delegate*'.");

			_p++;

			var types = new List<(OKind?, OTypePattern)>();

			while (true)
			{
				SkipInline();

				OKind? kind = null;

				if (IsWordAt("ref"))
				{
					_p += 3;
					SkipInline();
					kind = OKind.Ref;

					if (IsWordAt("readonly"))
					{
						_p += 8;
						SkipInline();
						kind = OKind.RefReadonly;
					}
				}
				else if (IsWordAt("in"))
				{
					_p += 2;
					SkipInline();
					kind = OKind.In;
				}
				else if (IsWordAt("out"))
				{
					_p += 3;
					SkipInline();
					kind = OKind.Out;
				}

				types.Add((kind, Type()));
				SkipInline();

				if (C == ',')
				{
					_p++;
					continue;
				}

				if (C != '>')
					throw Error("'>' expected.");

				_p++;
				return new OFunctionPointerPattern(types);
			}
		}

		OParameterListPattern ParameterList()
		{
			_p++;

			var before = new List<OParameterPattern>();
			var after  = (List<OParameterPattern>?)null;

			SkipInline();

			if (C == ')')
			{
				_p++;
				return new OParameterListPattern(before, null);
			}

			while (true)
			{
				SkipInline();

				if (C == '.' && At(1) == '.')
				{
					if (At(2) == '.')
						throw Error("'...' is 1.1 syntax; write '..'.");

					if (after is not null)
						throw Error("'..' twice in a list.");

					_p += 2;
					after = [];
				}
				else
				{
					(after ?? before).Add(Parameter());
				}

				SkipInline();

				if (C == ',')
				{
					_p++;
					continue;
				}

				if (C != ')')
					throw Error("')' expected in a parameter list.");

				_p++;
				return new OParameterListPattern(before, after);
			}
		}

		OParameterPattern Parameter()
		{
			OKind? kind     = null;
			var    isThis   = false;
			var    isParams = false;

			while (true)
			{
				OKind? next = null;
				var    length = 0;

				if (IsWordAt("ref"))
				{
					next   = OKind.Ref;
					length = 3;
				}
				else if (IsWordAt("out"))
				{
					next   = OKind.Out;
					length = 3;
				}
				else if (IsWordAt("in"))
				{
					next   = OKind.In;
					length = 2;
				}
				else if (IsWordAt("this"))
				{
					if (isThis)
						throw Error("'this' twice.");

					isThis = true;
					_p += 4;
					SkipInline();
					continue;
				}
				else if (IsWordAt("params"))
				{
					if (isParams)
						throw Error("'params' twice.");

					isParams = true;
					_p += 6;
					SkipInline();
					continue;
				}

				if (next is null)
					break;

				if (kind is not null)
					throw Error("Two kinds on a parameter.");

				_p += length;
				SkipInline();

				if (next == OKind.Ref && IsWordAt("readonly"))
				{
					_p += 8;
					SkipInline();
					next = OKind.RefReadonly;
				}

				kind = next;
			}

			if (C == '_' && IsPatternEnd(At(1)))
			{
				_p++;
				return new OParameterPattern(kind, isThis, isParams, null);
			}

			if (End || C != '(' && IsPatternEnd(C))
				throw Error("A parameter type is expected.");

			return new OParameterPattern(kind, isThis, isParams, Type());
		}
	}
}
