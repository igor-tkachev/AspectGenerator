using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;

namespace AspectGenerator.Tests.FilterV2Oracle
{
	/// <summary>
	/// The rules a 1.1 filter compiled to, as a frozen baseline record lists them, rewritten into one version 2 filter
	/// the way the reference text's "Migrating from 1.1" does: the 1.1 keys and operators get their version 2 spelling,
	/// repeated keys become one <c>or</c>, the include rules one line and every exclude rule a subtraction.
	/// </summary>
	static class V1Rewrite
	{
		public sealed record Rule(bool Include, string Text, string? V2, bool UsesEscapes);

		static readonly Regex _notExpressible = new(@"(^|[\n;&|\-])[ \t]*(contains|regex|signature)[ \t]*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

		/// <summary>The record uses a matcher version 2 has no spelling for: <c>contains:</c>, <c>regex:</c>, <c>signature:</c>.</summary>
		public static bool IsNotExpressible(string input)
		{
			return _notExpressible.IsMatch(input);
		}

		/// <summary>The rules of the record's rules field (<c>count:</c> then <c>+ text</c> or <c>- text</c>, comma-separated).</summary>
		public static List<Rule> Rules(string field)
		{
			var colon = field.IndexOf(':');
			var count = int.Parse(field.AsSpan(0, colon), System.Globalization.CultureInfo.InvariantCulture);
			var rules = new List<Rule>();

			if (count == 0)
				return rules;

			var current = new StringBuilder();
			var texts   = new List<string>();

			for (var i = colon + 1; i < field.Length; i++)
			{
				var c = field[i];

				if (c == '\\' && i + 1 < field.Length)
				{
					var next = field[++i];

					switch (next)
					{
						case '\\': current.Append('\\'); break;
						case ',':  current.Append(',');  break;
						case '|':  current.Append('|');  break;
						case 't':  current.Append('\t'); break;
						case 'r':  current.Append('\r'); break;
						case 'n':  current.Append('\n'); break;
						case 'u':
							current.Append((char)int.Parse(field.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
							i += 4;
							break;
						default:   current.Append('\\').Append(next); break;
					}
				}
				else if (c == ',')
				{
					texts.Add(current.ToString());
					current.Clear();
				}
				else
				{
					current.Append(c);
				}
			}

			texts.Add(current.ToString());

			if (texts.Count != count)
				throw new FormatException($"'{field}': {count} rules announced, {texts.Count} read.");

			foreach (var text in texts)
			{
				var include = text[0] == '+';
				var body    = text[2..];
				var escapes = false;
				var v2      = Translate(body, ref escapes);

				rules.Add(new Rule(include, body, v2, escapes));
			}

			return rules;
		}

		/// <summary>The version 2 filter of the rules: the includes as one line of alternatives, each exclude a subtraction.</summary>
		public static string Filter(IReadOnlyList<Rule> rules)
		{
			var lines    = new List<string>();
			var includes = rules.Where(static r => r.Include).Select(static r => r.V2!).ToList();

			if (includes.Count == 1)
				lines.Add(includes[0]);
			else if (includes.Count > 1)
				lines.Add(string.Join(" or ", includes.Select(static i => "(" + i + ")")));

			foreach (var exclude in rules.Where(static r => !r.Include))
				lines.Add("- (" + exclude.V2 + ")");

			return string.Join("\n", lines);
		}

		static readonly Dictionary<string,string> _keys = new(StringComparer.Ordinal)
		{
			["namespace"] = "namespace", ["type"] = "type", ["path"] = "type", ["fulltype"] = "type", ["method"] = "method",
			["fullmethod"] = "method", ["returns"] = "returns", ["param"] = "param", ["params"] = "params", ["attributes"] = "attribute",
		};

		static readonly Regex _keyed = new(@"^\s*(&\s*)?([A-Za-z]+)\s*:(?!:)", RegexOptions.CultureInvariant);

		// One rule: a condition group (items separated by `;`, each `[&] key: value` or modifier words) or a compact pattern.
		static string Translate(string body, ref bool escapes)
		{
			var first   = _keyed.Match(body);
			var isGroup = first.Success && _keys.ContainsKey(first.Groups[2].Value.ToLowerInvariant()) || SplitUnescaped(body, ';').Skip(1).Any();

			if (!isGroup)
				return Values(body.Trim(), "", ref escapes);

			// Different keys are AND; the items of one key join its latest bucket by OR, and `&` opens a new bucket.
			var buckets = new List<(string Key, List<string> Items)>();

			foreach (var raw in SplitUnescaped(body, ';'))
			{
				var item = raw.Trim();

				if (item.Length == 0)
					continue;

				var and = item.StartsWith('&');

				if (and)
					item = item[1..].Trim();

				var match = _keyed.Match(item);
				var key   = match.Success && _keys.TryGetValue(match.Groups[2].Value.ToLowerInvariant(), out var k) ? match.Groups[2].Value.ToLowerInvariant() : "";
				var value = key.Length == 0 ? item : item[match.Length..].Trim();
				var text  = key.Length == 0 ? Values(value, "", ref escapes) : Item(key, value, ref escapes);
				var last  = buckets.FindLastIndex(b => b.Key == key);

				if (and || last < 0 || key.Length == 0)
					buckets.Add((key, [text]));
				else
					buckets[last].Items.Add(text);
			}

			return string.Join(" and ", buckets.Select(static b => b.Items.Count == 1 ? b.Items[0] : "(" + string.Join(" or ", b.Items) + ")"));
		}

		// `key: a & b | c` is (key: a and key: b) or key: c.
		static string Item(string key, string value, ref bool escapes)
		{
			var v2Key  = _keys[key];
			var prefix = key is "path" or "fulltype" or "fullmethod" ? "global::" : "";
			var terms  = new List<string>();

			foreach (var term in SplitUnescaped(value, '|'))
			{
				var atoms = new List<string>();

				foreach (var atom in SplitUnescaped(term, '&'))
				{
					var text = Unescape(atom.Trim(), ref escapes);

					if (v2Key == "params")
						text = (text.StartsWith('(') ? text : "(" + text + ")").Replace("...", "..");
					else if (prefix.Length > 0 && !text.StartsWith("global::", StringComparison.Ordinal))
						text = prefix + text;

					atoms.Add(v2Key + ": " + text);
				}

				terms.Add(atoms.Count == 1 ? atoms[0] : "(" + string.Join(" and ", atoms) + ")");
			}

			return terms.Count == 1 ? terms[0] : "(" + string.Join(" or ", terms) + ")";
		}

		// A compact pattern or modifier words: the same text, with `...` spelled `..`.
		static string Values(string text, string prefix, ref bool escapes)
		{
			return prefix + Unescape(text, ref escapes).Replace("...", "..");
		}

		static string Unescape(string text, ref bool escapes)
		{
			if (!text.Contains('\\'))
				return text;

			escapes = true;

			var sb = new StringBuilder();

			for (var i = 0; i < text.Length; i++)
			{
				if (text[i] == '\\' && i + 1 < text.Length)
					i++;

				sb.Append(text[i]);
			}

			return sb.ToString();
		}

		static IEnumerable<string> SplitUnescaped(string text, char separator)
		{
			var start = 0;

			for (var i = 0; i < text.Length; i++)
			{
				if (text[i] == '\\')
				{
					i++;
					continue;
				}

				if (text[i] == separator)
				{
					yield return text[start..i];
					start = i + 1;
				}
			}

			yield return text[start..];
		}
	}

	/// <summary>
	/// Compares, for every frozen 1.1 record, the 1.1 decision per target with what version 2 decides for the rewritten
	/// filter as an assembly attribute, and gives the record one class: the same; different as rows of the migration
	/// table predict (the rows named); different for a rule of the reference text that the table does not list (named,
	/// each checked by deciding the rewrite again with that rule undone); or different without an explanation.
	/// <para>
	/// A target's difference is put down to a cause only when undoing the cause gives back the 1.1 decision: the rules
	/// decided again where nothing keeps the filter in the project; each rule decided alone and the last match taken; the
	/// rules with <c>?</c> dropped, with the accessibility words widened as 1.1 matched them, with the patterns rooted
	/// as 1.1 matched them. Three causes are recognised by the target alone: a constructed call of a project method or
	/// an extension call (1.1 described those by the definition or the reduced form), a generic definition (a name
	/// without <c>&lt;…&gt;</c> matches any arity, and a type parameter's name is an ordinary name), and a record that
	/// uses backslash escapes.
	/// </para>
	/// </summary>
	sealed class V1V2Differential
	{
		public const string Same           = "same";
		public const string NotExpressible = "not expressible (contains:, regex: or signature:)";
		public const string NoRules        = "no comparison: 1.1 compiled no rule";

		public const string RowOrder    = "predicted, row 'rules ordered, the last match decides'";
		public const string RowQuestion = "predicted, row '`?` matched one character'";
		public const string RowNullable = "predicted, row '`int?` matched `int`'";
		public const string RowGeneric  = "predicted, row 'generic methods declared in the project were judged by their definition'";
		public const string RowRemoved  = "predicted, row 'backslash escapes: removed'";

		public const string RowProject       = "predicted, row 'an assembly filter without a place reached referenced methods too'";
		public const string RowExcludeOnly   = "predicted, row 'a filter of subtractions only selected nothing'";
		public const string RowAccessibility = "predicted, row 'accessibility words are exact (`private` also matched `private protected`)'";
		public const string RowAnchoring     = "predicted, row 'a name pattern matched the full name'";
		public const string RowArity         = "predicted, row '`Load<T>` named the arity and the type parameter'";
		public const string RowStrict        = "predicted, row 'text 1.1 read without an error could be meaningless'";
		public const string RowStructural    = "predicted, row 'a name pattern in a type position matched arrays and tuples by their text'";
		public const string RowMoreMatches   = "predicted, row 'tuples, generic attributes and `attribute: System.Obsolete` did not match'";
		public const string RowExtern        = "predicted, row '`extern` did not cover runtime-implemented methods'";

		/// <summary>
		/// <c>**</c> written as a whole type pattern (<c>returns: **</c>) is not decided yet: it matches any type like
		/// <c>*</c>, or stays a name pattern. Its records are counted apart until it is.
		/// </summary>
		public const string PendingAnyType = "pending: `**` as a whole type pattern (matches any type like `*`, or stays a name pattern)";

		/// <summary>
		/// A record whose frozen decisions differ from the 1.1.2 package's own, because the baselines were written by the
		/// 1.1 parser regenerated on master. The class is followed by what differs.
		/// </summary>
		public const string BaselineAfter112 = "baseline after 1.1.2";

		// The three records known to differ from 1.1.2: the baseline reads them as the later 1.1 code does.
		static readonly Dictionary<(string File, string Input), string> _afterBaseline = new()
		{
			[("curated", @"method: *a\*")] = "the `\\*` escape",
			[("curated", "returns: int[]")]  = "array ranks are ignored in 1.1.2",
			[("curated", "Load<T>")]         = "a generic definition and a call of it",
		};

		public const string Unexplained = "unexplained";

		readonly Dictionary<string,bool?[]> _decisions = new(StringComparer.Ordinal);

		public sealed record Result(FrozenBaseline.Record Record, string Class, string? Rewrite, string Detail);

		/// <summary>What version 2 decides for every target of the fixture; <see langword="null"/> for each when the filter has an error.</summary>
		public bool?[] Decide(string filter)
		{
			if (_decisions.TryGetValue(filter, out var cached))
				return cached;

			var block   = new ProductBlock(filter);
			var targets = OracleCases.Fixture;
			var result  = new bool?[targets.Length];

			for (var i = 0; i < targets.Length; i++)
				result[i] = block.Selects(targets[i]);

			return _decisions[filter] = result;
		}

		// An explicit interface implementation (`void IJob.Run()`) is not a target: a filter names the member of the
		// interface, and a call goes through the interface, never to a method no source can name. 1.1 selected it by its
		// compiled name (`Run`, `Ru*n`); version 2 has no such target, and the baselines keep its index.
		static bool IsExplicitImplementation(int target)
		{
			return OracleTargets.Entries[target].Symbol.MethodKind == MethodKind.ExplicitInterfaceImplementation;
		}

		static readonly Regex _questionInName = new(@"\?[\w*<.]", RegexOptions.CultureInvariant);

		public Result Classify(FrozenBaseline.Record record)
		{
			var result = ClassifyRecord(record);

			if (!_afterBaseline.TryGetValue((record.File, record.Input), out var reason))
				return result;

			return result with { Class = BaselineAfter112 + ": " + reason, Detail = result.Detail.Length == 0 ? result.Class : $"{result.Class}; {result.Detail}" };
		}

		Result ClassifyRecord(FrozenBaseline.Record record)
		{
			if (V1Rewrite.IsNotExpressible(record.Input))
				return new Result(record, NotExpressible, null, "");

			var rules = V1Rewrite.Rules(record.Rules);

			if (rules.Count == 0)
				return new Result(record, NoRules, null, record.Diagnostics);

			var rewrite = V1Rewrite.Filter(rules);
			var v2      = Decide(rewrite);
			var apart   = Enumerable.Range(0, v2.Length).Where(t => !IsExplicitImplementation(t) && record.Included(t) != (v2[t] == true)).ToList();

			if (apart.Count == 0)
				return new Result(record, Same, rewrite, "");

			var detail = $"targets apart: {string.Join(",", apart.Take(12))}{(apart.Count > 12 ? $",... ({apart.Count})" : "")}";

			if (v2[0] is null)
			{
				var error = OracleFilter.Assembly(rewrite).Error ?? string.Join("; ", new ProductBlock(rewrite).Block.Diagnostics.Select(static d => d.Message));

				if (rules.Any(static r => _questionInName.IsMatch(r.V2!)) || error.Contains("'?'", StringComparison.Ordinal))
					return new Result(record, RowQuestion, rewrite, detail + "; " + error);

				if (rules.Any(static r => r.UsesEscapes))
					return new Result(record, RowRemoved, rewrite, detail + "; " + error);

				return new Result(record, RowStrict, rewrite, detail + "; " + error);
			}

			var anywhere = Decide(rewrite + "\norigin: any");
			var perRule  = Matches(rules, static r => [r.V2!]);
			var causes   = new SortedSet<string>(StringComparer.Ordinal);
			var hints    = new List<string>();

			List<bool?[]>? withoutQuestion = null;
			List<bool?[]>? widened         = null;
			List<bool?[]>? rooted          = null;
			List<bool?[]>? anyType         = null;

			foreach (var t in apart)
			{
				var v1     = record.Included(t);
				var entry  = OracleTargets.Entries[t];
				var symbol = entry.Symbol.ReducedFrom ?? entry.Symbol;

				if (anywhere[t] == v1)
					causes.Add(RowProject);
				else if (!rules.Any(static r => r.Include))
					causes.Add(RowExcludeOnly);
				else if (Ordered(rules, perRule, t) == v1)
					causes.Add(RowOrder);
				else if (entry.Label is "constructed" or "constructed type" or "reduced extension" || entry.Symbol.ReducedFrom is not null)
					causes.Add(RowGeneric);
				else if (rules.Any(static r => r.V2!.Contains('?')) && Ordered(rules, withoutQuestion ??= Matches(rules, static r => [r.V2!.Replace("?", "")]), t) == v1)
					causes.Add(RowNullable);
				else if (Ordered(rules, widened ??= Matches(rules, static r => Widen(r.V2!)), t) == v1)
					causes.Add(RowAccessibility);
				else if (Ordered(rules, rooted ??= Matches(rules, static r => [Root(r.V2!)]), t) == v1)
					causes.Add(RowAnchoring);
				else if (rules.Any(static r => r.UsesEscapes))
					causes.Add(RowRemoved);
				else if (rules.Any(static r => WholeTypeAnyPatterns(r.V2!).Any()) && Ordered(rules, anyType ??= Matches(rules, static r => [AnyTypeAsStar(r.V2!)]), t) == v1)
					causes.Add(PendingAnyType);
				else if (rules.Any(static r => ReturnsPatterns(r.V2!).Any(static p => p.StartsWith("*.*", StringComparison.Ordinal))) && HasStructuralType(symbol))
					causes.Add(RowStructural);
				else if (!v1 && rules.Any(static r => r.V2!.Contains("attribute:", StringComparison.Ordinal)))
					causes.Add(RowMoreMatches);
				else if (!v1 && symbol.ReturnType.IsTupleType)
					causes.Add(RowMoreMatches);
				else if (!v1 && (symbol.IsExtern || (symbol.MethodImplementationFlags & System.Reflection.MethodImplAttributes.InternalCall) != 0) && rules.Any(static r => r.V2!.Contains("extern", StringComparison.Ordinal)))
					causes.Add(RowExtern);
				else if (symbol.IsGenericMethod || symbol.ContainingType.IsGenericType)
					causes.Add(RowArity);
				else
				{
					causes.Add(Unexplained);
					hints.Add($"#{t} {(v1 ? "1.1 selects, version 2 does not" : "version 2 selects, 1.1 does not")}");
				}
			}

			// A pending question keeps its records together, whatever else explains some of their targets.
			var name = causes.Contains(Unexplained) ? Unexplained : causes.Contains(PendingAnyType) ? PendingAnyType : string.Join(" + ", causes);

			return new Result(record, name, rewrite, detail + (hints.Count > 0 ? "; " + string.Join("; ", hints.Take(3)) : ""));
		}

		// The type patterns a rule writes after `returns:`, or after the colon of a compact signature (which has no key
		// before it: `Save(int) : **`, `*:*.**`).
		static readonly Regex _colon = new(@"(?<key>[\w*.>)]*)[ \t]*(?<!:):(?!:)[ \t]*(?<value>[^\s)]+)", RegexOptions.CultureInvariant);

		static readonly HashSet<string> _propertyKeys = ["namespace", "type", "method", "param", "params", "attribute", "inherits", "implements", "overrides"];

		static IEnumerable<string> ReturnsPatterns(string rule)
		{
			foreach (Match match in _colon.Matches(rule))
			{
				if (!_propertyKeys.Contains(match.Groups["key"].Value))
					yield return match.Groups["value"].Value;
			}
		}

		static readonly Regex _wholeAny = new(@"^[*.]*\*\*\??$", RegexOptions.CultureInvariant);

		// A returns pattern that is `**`, or `**` after only wildcard segments, as a whole.
		static IEnumerable<string> WholeTypeAnyPatterns(string rule)
		{
			return ReturnsPatterns(rule).Where(static p => _wholeAny.IsMatch(p));
		}

		static string AnyTypeAsStar(string rule)
		{
			var result = rule;

			foreach (var pattern in WholeTypeAnyPatterns(rule).Distinct())
				result = result.Replace(pattern, "*");

			return result;
		}

		// The types a name pattern cannot be: an array, a pointer, a tuple or a type parameter, in the returns or a parameter.
		static bool HasStructuralType(IMethodSymbol method)
		{
			static bool Structural(ITypeSymbol type)
			{
				return type.TypeKind is TypeKind.Array or TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.TypeParameter || type.IsTupleType;
			}

			return Structural(method.ReturnType) || method.Parameters.Any(static p => Structural(p.Type));
		}

		// Each rule decided alone wherever the method is declared; a rule given as several texts matches where any does.
		List<bool?[]> Matches(IReadOnlyList<V1Rewrite.Rule> rules, Func<V1Rewrite.Rule,IEnumerable<string>> texts)
		{
			var result = new List<bool?[]>();

			foreach (var rule in rules)
			{
				var decisions = texts(rule).Select(t => Decide("(" + t + ")\norigin: any")).ToList();

				result.Add([..Enumerable.Range(0, decisions[0].Length).Select(i => (bool?)decisions.Any(d => d[i] == true))]);
			}

			return result;
		}

		// 1.1: the last rule that matches decides.
		static bool Ordered(IReadOnlyList<V1Rewrite.Rule> rules, IReadOnlyList<bool?[]> matches, int target)
		{
			var last = -1;

			for (var r = 0; r < rules.Count; r++)
				if (matches[r][target] == true)
					last = r;

			return last >= 0 && rules[last].Include;
		}

		static readonly Regex _private   = new(@"(?<![\w@.]|protected )private(?! protected)(?![\w.*(<])", RegexOptions.CultureInvariant);
		static readonly Regex _protected = new(@"(?<![\w@.]|private |internal )protected(?! internal| private)(?![\w.*(<])", RegexOptions.CultureInvariant);
		static readonly Regex _internal  = new(@"(?<![\w@.]|protected )internal(?! protected)(?![\w.*(<])", RegexOptions.CultureInvariant);

		// The accessibility words as 1.1 matched them: a word matched every accessibility that holds its part.
		static IEnumerable<string> Widen(string rule)
		{
			yield return rule;
			yield return rule.Replace("protected and internal", "protected internal").Replace("internal and protected", "internal protected");
			yield return rule.Replace("private and protected", "private protected").Replace("protected and private", "protected private");
			yield return _private.Replace(rule, "private protected");
			yield return _protected.Replace(rule, "protected internal");
			yield return _protected.Replace(rule, "private protected");
			yield return _internal.Replace(rule, "protected internal");
		}

		static readonly HashSet<string> _words = ["public", "internal", "protected", "private", "static", "instance", "async", "virtual", "override", "abstract", "sealed", "extern", "unsafe"];

		static readonly Regex _keyed = new(@"\b(type|returns|attribute|inherits|method): (?!global::|\*\*|[({""]|ref )", RegexOptions.CultureInvariant);

		// The patterns rooted, as 1.1 matched type names and method paths: in full.
		static string Root(string rule)
		{
			if (_keyed.IsMatch(rule))
				return _keyed.Replace(rule, m => ONamePattern.IsAlias(Next(rule, m.Index + m.Length)) ? m.Value : m.Value + "global::");

			var tokens = rule.Split(' ');

			for (var i = 0; i < tokens.Length; i++)
			{
				if (_words.Contains(tokens[i]))
					continue;

				if (!tokens[i].StartsWith("global::", StringComparison.Ordinal) && !tokens[i].StartsWith("**", StringComparison.Ordinal) && tokens[i].Contains('.'))
					tokens[i] = "global::" + tokens[i];

				break;
			}

			return string.Join(" ", tokens);
		}

		static string Next(string text, int start)
		{
			var end = start;

			while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
				end++;

			return text[start..end];
		}
	}
}
