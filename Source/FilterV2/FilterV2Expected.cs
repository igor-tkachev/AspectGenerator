using System;
using System.Collections.Generic;
using System.Text;

namespace AspectGenerator.FilterV2
{
	/// <summary>
	/// Says what the parser expected where a line stopped in the words of the filter language rather than the grammar's:
	/// the grammar's rules and character classes become "a key", "a word", "a name pattern"; lookarounds and the 1.1 forms
	/// the grammar reads only to report them are left out.
	/// </summary>
	internal static class FilterV2Expected
	{
		static readonly HashSet<string> _keyRules = new(StringComparer.Ordinal)
		{
			"NameKey", "TypeKey", "MethodKey", "ReturnsKey", "ParamsKey", "ParamKey", "PropertyKey", "LegacyKey", "ReservedKey", "UnknownKey", "KeyCondition",
		};

		static readonly HashSet<string> _words = new(StringComparer.Ordinal)
		{
			"public", "internal", "protected", "private", "static", "instance", "async", "virtual", "override", "abstract", "sealed", "extern",
			"extension", "project", "generated",
		};

		static readonly HashSet<string> _left = new(StringComparer.Ordinal)
		{
			// Read only to be reported: the reserved words and literals, and the 1.1 operators.
			"iterator", "unsafe", "partial", "readonly", "exposed", "/", "\\", "...", "['&' | ';' | '|']", "Join", "Sp", "Lex.Join",
		};

		static readonly HashSet<string> _namePattern = new(StringComparer.Ordinal)
		{
			"IdChar", "@", "*", "**", "{", "?", "delegate", "Lex.IdChar",
		};

		/// <summary>
		/// The keywords as the grammar lists them where a name was expected: a keyword there is read only to say it needs
		/// <c>@</c>, so the list says nothing the reader wants to know. <c>signature</c> appears in a list through that
		/// reading alone.
		/// </summary>
		static readonly HashSet<string> _keywords = new(StringComparer.Ordinal)
		{
			"and", "or", "not", "namespace", "type", "method", "returns", "params", "param", "attribute", "inherits",
			"implements", "overrides", "accessibility", "virtuality", "location", "origin", "within", "signature", "ref", "out", "in", "this",
		};

		public static string Describe(string expected)
		{
			var result   = new List<string>();
			var items    = Split(expected);
			var keywords = items.Exists(static i => i.Trim() == "\"signature\"");

			foreach (var raw in items)
			{
				var item = raw.Trim();

				if (item.Length == 0 || item.StartsWith("?", StringComparison.Ordinal))
					continue;

				var text = Unquote(item);

				if (keywords && item.StartsWith("\"", StringComparison.Ordinal) && (_keywords.Contains(text) || _words.Contains(text)))
					continue;

				string? said;

				if (_keyRules.Contains(item))
					said = "a key";
				else if (_left.Contains(text) || _left.Contains(item))
					said = null;
				else if (_words.Contains(text))
					said = "a word";
				else if (_namePattern.Contains(text))
					said = "a name pattern";
				else if (text == "\"")
					said = "quoted text";
				else if (text is ".." or "_")
					said = "a parameter pattern";
				else if (text is "aspect" or "assembly")
					said = "a reference";
				else if (item == "end of input")
					said = "the end of the line";
				else if (item == "Sp1")
					said = "a space";
				else if (item.StartsWith("[", StringComparison.Ordinal) || char.IsUpper(item[0]))
					said = "a condition";
				else
					said = "'" + text + "'";

				if (said is not null && !result.Contains(said))
					result.Add(said);
			}

			if (result.Count == 0)
				return "a condition";

			if (result.Count == 1)
				return result[0];

			return string.Join(", ", result.GetRange(0, result.Count - 1)) + " or " + result[result.Count - 1];
		}

		static string Unquote(string item)
		{
			if (item.Length >= 2 && (item[0] == '\'' && item[item.Length - 1] == '\'' || item[0] == '"' && item[item.Length - 1] == '"'))
			{
				var inner = item.Substring(1, item.Length - 2);

				return inner.Replace("\\\\", "\\").Replace("\\'", "'").Replace("\\\"", "\"");
			}

			return item;
		}

		/// <summary>The items of the list, split at <c>, </c> and at the last <c> or </c>, outside quotes and brackets.</summary>
		static List<string> Split(string expected)
		{
			var items   = new List<string>();
			var current = new StringBuilder();
			var depth   = 0;
			var quote   = '\0';

			for (var i = 0; i < expected.Length; i++)
			{
				var ch = expected[i];

				if (quote != '\0')
				{
					current.Append(ch);

					if (ch == '\\' && i + 1 < expected.Length)
						current.Append(expected[++i]);
					else if (ch == quote)
						quote = '\0';

					continue;
				}

				switch (ch)
				{
					case '\'' or '"':
						quote = ch;
						break;
					case '[' or '(':
						depth++;
						break;
					case ']' or ')':
						depth--;
						break;
				}

				if (depth == 0 && quote == '\0' && ch == ',' && i + 1 < expected.Length && expected[i + 1] == ' ')
				{
					items.Add(current.ToString());
					current.Clear();
					i++;
					continue;
				}

				if (depth == 0 && quote == '\0' && string.CompareOrdinal(expected, i, " or ", 0, 4) == 0)
				{
					items.Add(current.ToString());
					current.Clear();
					i += 3;
					continue;
				}

				current.Append(ch);
			}

			items.Add(current.ToString());

			return items;
		}
	}
}
