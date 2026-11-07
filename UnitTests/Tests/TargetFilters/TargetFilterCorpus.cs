using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// The deterministic input sets: the curated inputs, a fixed sample of mutations of them, and the generated set.
	/// Nothing here uses <see cref="Random"/> or hash codes: the same seed gives the same inputs on every machine.
	/// </summary>
	static class TargetFilterCorpus
	{
		public const ulong MutationSeed  = 0x4147_5446_4D55_0001UL;
		public const int   MutationsPerInput = 10;

		public const ulong GeneratedSeed = 0x4147_5446_4745_0001UL;
		public const int   GeneratedCount = 50_000;

		/// <summary>The characters the mutations insert, delete or duplicate.</summary>
		public static readonly string[] MutationTokens = [";", ":", "|", "&", ",", ".", "(", ")", "<", ">", "\\", "*", "?", "**"];

		/// <summary>The list file first, then the hand cases it does not hold yet.</summary>
		public static List<TargetFilterCuratedInputs.Input> Curated()
		{
			var result = TargetFilterCuratedInputs.ReadList();
			var seen   = new HashSet<string>(result.Select(static i => i.Text), StringComparer.Ordinal);

			foreach (var text in TargetFilterCases.All)
				if (seen.Add(text))
					result.Add(new TargetFilterCuratedInputs.Input(TargetFilterCuratedInputs.ClassHand, text));

			return result;
		}

		// ---- mutations ----

		public static List<string> Mutations(IReadOnlyList<string> curated, int perInput, ulong seed)
		{
			return MutationsWithSources(curated, perInput, seed).Select(static m => m.Text).ToList();
		}

		/// <summary>
		/// Up to <paramref name="perInput"/> mutations of every curated input that has a position to mutate, together with
		/// the index of the input each one was made from. The stream of an input is seeded from its text, so adding,
		/// removing or reordering other inputs never changes the mutations of this one.
		/// </summary>
		public static List<(int Source, string Text)> MutationsWithSources(IReadOnlyList<string> curated, int perInput, ulong seed)
		{
			var result  = new List<(int, string)>(curated.Count * perInput);
			var curatedSet = new HashSet<string>(curated, StringComparer.Ordinal);
			var seen    = new HashSet<string>(StringComparer.Ordinal);

			for (var index = 0; index < curated.Count; index++)
			{
				var text = curated[index];
				var mask = ForbiddenPositions(text);

				if (!HasEligiblePosition(text, mask))
					continue;

				var random   = new SplitMix64(seed ^ Fnv1a64(text));
				var produced = new HashSet<string>(StringComparer.Ordinal);

				for (var attempt = 0; produced.Count < perInput && attempt < perInput * 20; attempt++)
				{
					var operation = random.Next(3);
					var token     = MutationTokens[random.Next(MutationTokens.Length)];
					var mutated   = Mutate(text, mask, operation, token, random);

					if (curatedSet.Contains(mutated) || !produced.Add(mutated))
						continue;

					// The same text can come from two inputs; the first in list order keeps it.
					if (seen.Add(mutated))
						result.Add((index, mutated));
				}
			}

			return result;
		}

		/// <summary>A fixed hash of the UTF-8 text (FNV-1a, 64 bits); <see cref="string.GetHashCode()"/> is randomised per process.</summary>
		static ulong Fnv1a64(string text)
		{
			var hash = 0xCBF29CE484222325UL;

			foreach (var b in Encoding.UTF8.GetBytes(text))
				hash = unchecked((hash ^ b) * 0x100000001B3UL);

			return hash;
		}

		static string Mutate(string text, bool[] forbidden, int operation, string token, SplitMix64 random)
		{
			// operation 0 inserts the token, 1 deletes an occurrence, 2 duplicates one; a missing occurrence falls back to an insert.
			if (operation != 0)
			{
				var occurrences = new List<int>();

				for (var i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + 1, StringComparison.Ordinal))
				{
					var allowed = true;

					for (var k = 0; k < token.Length; k++)
						if (forbidden[i + k])
							allowed = false;

					if (allowed)
						occurrences.Add(i);
				}

				if (occurrences.Count > 0)
				{
					var at = occurrences[random.Next(occurrences.Count)];

					return operation == 1
						? text.Remove(at, token.Length)
						: text.Insert(at, token);
				}
			}

			var positions = new List<int>();

			for (var i = 0; i <= text.Length; i++)
				if (!forbidden[i])
					positions.Add(i);

			return text.Insert(positions[random.Next(positions.Count)], token);
		}

		static bool HasEligiblePosition(string text, bool[] forbidden)
		{
			for (var i = 0; i <= text.Length; i++)
				if (!forbidden[i])
					return true;

			return false;
		}

		/// <summary>
		/// Positions (insertion points 0..length, which also stand for the character at that index) that a mutation
		/// must not touch: the body of a regex: rule, from just after its colon to the end of its line.
		/// </summary>
		static bool[] ForbiddenPositions(string text)
		{
			var forbidden = new bool[text.Length + 1];
			var lineStart = 0;

			while (lineStart <= text.Length)
			{
				var lineEnd = text.IndexOf('\n', lineStart);

				if (lineEnd < 0)
					lineEnd = text.Length;

				var bodyStart = FindRegexBodyStart(text, lineStart, lineEnd);

				if (bodyStart >= 0)
					for (var i = bodyStart; i <= lineEnd; i++)
						forbidden[i] = true;

				lineStart = lineEnd + 1;
			}

			return forbidden;
		}

		/// <summary>The text of every regex: rule body (from after its colon to the end of its line).</summary>
		public static IEnumerable<string> RegexBodiesOf(string text)
		{
			var lineStart = 0;

			while (lineStart <= text.Length)
			{
				var lineEnd = text.IndexOf('\n', lineStart);

				if (lineEnd < 0)
					lineEnd = text.Length;

				var bodyStart = FindRegexBodyStart(text, lineStart, lineEnd);

				if (bodyStart >= 0)
					yield return text[bodyStart..lineEnd];

				lineStart = lineEnd + 1;
			}
		}

		static int FindRegexBodyStart(string text, int start, int end)
		{
			var line = text[start..end];
			var i    = 0;

			while (i < line.Length && char.IsWhiteSpace(line[i]))
				i++;

			if (i < line.Length && line[i] is '-' or '&' or '|')
				i++;

			// First colon outside angle brackets and not escaped, as the filter reader finds it.
			var depth   = 0;
			var escaped = false;

			for (var k = i; k < line.Length; k++)
			{
				var ch = line[k];

				if (escaped)
				{
					escaped = false;
					continue;
				}

				if (ch == '\\')
				{
					escaped = true;
					continue;
				}

				if (ch == '<')
					depth++;
				else if (ch == '>' && depth > 0)
					depth--;
				else if (depth == 0 && ch == ':')
				{
					var name = line[i..k].Trim();

					return string.Equals(name, "regex", StringComparison.OrdinalIgnoreCase) ? start + k + 1 : -1;
				}
			}

			return -1;
		}

		// ---- generated ----

		static readonly string[] Prefixes = ["", "", "", "", "-", "", "-", "&", "|", "- ", "& ", "| ", "-&", "  ", "\t-", " | "];

		static readonly string[] Modifiers =
		[
			"", "public ", "static ", "public static ", "", "protected internal ", "private protected ", "internal ", "private ",
			"protected ", "virtual ", "override ", "public abstract ", "sealed ", "extern ", "unsafe ", "instance ", "public virtual ",
			"async ", "Public ", "public  static "
		];

		static readonly string[] Paths =
		[
			"", "Acme.Core.", "**.", "Acme.**.", "Acme.Core.Service.", "*.", "**.*Service.", "System.Math.", "Acme.Core.Repository<*>.", "Acme.Core.Pair<*,*>.",
			"Acme.Core.Outer<*>.Inner<*>.", "A.B.C.D.E.F.G.", "Lib.Tasks.*.", "Acme.Core.S?rvice.", "Acme.*.Service.",
			"Acme.Core.Outer.Inner.Deep.", "GlobalService.", "**.Tagged*.", "Acme.Core.Repository<string>.", "System.**.",
			"System.Linq.Enumerable.", "acme.core.", "Acme..Core.", ".Acme.", "**.**."
		];

		static readonly string[] Names =
		[
			"Save", "Save*", "*Async", "*", "Run", "Max", "Load<*>", "*Save*", "Ping", "S?ve", "Save?", "L*d", "Load<T>", "Load<*,*>", "Get<*>",
			"Count", "Format", "ToString", "Equals", "Deconstruct", "Сохранить", "event", "Sa\\*ve", "Save\\.Item", "Start", "Try*",
			"**", "", "Save Item", "Save<", "Save>", "Sa*v*e", "?", "??", "*?"
		];

		static readonly string[] Generics = ["", "", "", "", "<*>", "<T>", "<*,*>", "<string>", "<List<int>>", "<Dictionary<string, List<*>>>", "<>", "<*"];

		static readonly string[] Escapes = ["", "", "", "", "", "\\*", "\\?", "\\.", "\\|", "\\&", "\\\\", "\\<", "\\>", "\\,", "\\;", "\\"];

		static readonly string[] Parameters =
		[
			"", "()", "(...)", "(_)", "(int)", "(..., *CancellationToken)", "(string)", "", "(_, _)", "(string?)", "(System.String)", "(int?)",
			"(ref int, ref int)", "(out int)", "(in *)", "(params string[])", "(params *)", "(*, ..., *)", "(..., ...)", "(List<*>)",
			"(Dictionary<string,*>)", "(string, int?, bool)", "(this int)", "(", ")", "(,)", "(int,)", "(,int)", "(_ _)", "(ref)", "(out *, _)"
		];

		static readonly string[] Returns =
		[
			"", " : void", " : System.Void", " : string", " : *", " : Task*", "", " : string?", " : System.Threading.Tasks.Task<*>", " : int?",
			" : List<*>", " : Dictionary<string,List<*>>", " : T", " : int[]", " : (int, string)", " : *[]", " : ", " : **", " :: int", " : Ta?k", " : *?"
		];

		static readonly string[] Keys =
		[
			"namespace", "type", "path", "fulltype", "method", "fullmethod", "returns", "param", "params", "attributes", "signature",
			"Namespace", "METHOD", "Returns", "foo", "attribute", "", "pattern", "contains"
		];

		static readonly string[] TypeValues =
		[
			"string", "System.String", "int", "void", "Task*", "*", "Tag", "Marker", "string?", "System.Void", "int?", "Task*?", "System.Threading.Tasks.Task<*>",
			"List<*>", "**", "*[]", "int[]", "Ta?k", "MarkerAttribute", "GenericMarker<*>", "System.Obsolete", "bool", "object",
			"(int, string)", "System.ValueTuple<*,*>", "*Task*", "S?ring", "Dictionary<string, List<int?>>"
		];

		static readonly string[] ParameterValues =
		[
			"int", "_", "...", "..., *CancellationToken", "out int", "*CancellationToken", "ref *", "in *", "params *", "(...)", "()", "(int)", "*, *", "_, _", "..., ...",
			"this int", "string, int?, bool", "out System.Int32", "ref int, ref int", "int, ..., int", "", ","
		];

		static readonly string[] SignatureValues =
		[
			"public *", "*Save*", "public System.Void *", "*(System.Int32)", "*Acme.Core.*", "public System.Void Acme.Core.Service.Save()", "*", "static *",
			"* System.String *", "*out *", "*this *", "*?*"
		];

		static readonly string[] Operators = [" | ", " & ", "|", "&", " | | ", " &", "| ", " && ", " || "];

		static readonly string[] ContainsBodies = ["Save", "Acme.Core", "out ", "System.Void", "(System.Int32)", "save", "|", ";", "", " ", "Save|Load", "this ", "ref "];

		// Every regex here is bounded and compiles (or is invalid on purpose): the corpus never depends on a match timeout.
		static readonly string[] RegexBodies =
		[
			"^public .*$", "Save", "System\\.Void", "^.*\\.Save\\(.*$", "(?<n>Sa)ve", "[", "(", "*", "^public static ", "Async\\(", "\\bSave\\b", "^private", "", ".*", "Save|Load"
		];

		static readonly string[] Weird =
		[
			"foo: bar", "attribute: x", "::", "method::Save", ":", ";", ",", "(", ")", "<", ">", "\\", "*", "?", "**", ".", "a.b", "...", "this", "_", "-", "&", "|",
			"# comment", "   ", "1: 2", "a b: c", "method: ", "public ;", "; public"
		];

		static readonly string[] Whitespace = ["", "", "", ";", ", ", "tight", "spaced", "tabbed"];

		public static IEnumerable<string> Generate(int count, ulong seed = GeneratedSeed)
		{
			for (var i = 0; i < count; i++)
				yield return Build(i, seed);
		}

		static string Build(int index, ulong seed)
		{
			var random = new SplitMix64(seed + (ulong)index * 0x9E3779B97F4A7C15UL);
			var lines  = random.Next(6) switch { 0 or 1 or 2 => 1, 3 or 4 => 2, _ => 3 };
			var eol    = random.Next(5) == 0 ? "\r\n" : "\n";
			var parts  = new List<string>(lines);

			for (var i = 0; i < lines; i++)
				parts.Add(BuildLine(random));

			return string.Join(eol, parts);
		}

		static string BuildLine(SplitMix64 random)
		{
			var prefix = Pick(random, Prefixes);
			var body   = random.Next(100) switch
			{
				< 30 => Compact(random),
				< 55 => KeyValue(random),
				< 65 => string.Join("; ", Enumerable.Range(0, 2 + random.Next(2)).Select(_ => KeyValue(random))),
				< 72 => "contains: " + Pick(random, ContainsBodies),
				< 78 => "regex: " + Pick(random, RegexBodies),
				< 84 => "pattern: " + (random.Next(2) == 0 ? Compact(random) : KeyValue(random)),
				< 90 => (Pick(random, Modifiers) + Pick(random, Modifiers)).Trim(),
				_    => Pick(random, Weird)
			};

			return Space(random, prefix + body);
		}

		static string Compact(SplitMix64 random)
		{
			var name = Pick(random, Names) + Pick(random, Generics);

			if (random.Next(4) == 0)
				name = name.Insert(random.Next(name.Length + 1), Pick(random, Escapes));

			var path = Pick(random, Paths);

			if (random.Next(6) == 0)
				path = path.Replace(".", Pick(random, Generics) + ".");

			var bare = random.Next(3) == 0;

			return Pick(random, Modifiers) + path + name + (bare ? "" : Pick(random, Parameters) + Pick(random, Returns));
		}

		static string KeyValue(SplitMix64 random)
		{
			var key = Pick(random, Keys);

			string Value()
			{
				var value = key.ToLowerInvariant() switch
				{
					"namespace" or "path" or "fulltype" or "fullmethod" => Pick(random, Paths).TrimEnd('.') + (random.Next(3) == 0 ? "." + Pick(random, Names) : ""),
					"type" or "method"                                   => Pick(random, Names) + Pick(random, Generics),
					"returns" or "attributes"                            => Pick(random, TypeValues),
					"param" or "params"                                  => Pick(random, ParameterValues),
					"signature"                                          => Pick(random, SignatureValues),
					_                                                    => Pick(random, Names)
				};

				if (random.Next(5) == 0)
					value = value.Insert(random.Next(value.Length + 1), Pick(random, Escapes));

				return value;
			}

			var text = Value();

			for (var extra = random.Next(4) == 0 ? 1 + random.Next(2) : 0; extra > 0; extra--)
				text += Pick(random, Operators) + Value();

			return key + ": " + text;
		}

		static string Space(SplitMix64 random, string line)
		{
			switch (Pick(random, Whitespace))
			{
				case ";":      return line.Replace("; ", ";");
				case ", ":     return line.Replace(", ", ",");
				case "tight":  return line.Replace(": ", ":").Replace("; ", ";").Replace(" | ", "|").Replace(" & ", "&");
				case "spaced": return line.Replace(":", " : ").Replace(";", " ;  ");
				case "tabbed": return "\t" + line.Replace(": ", ":\t") + " \t";
				default:       return line;
			}
		}

		// The first entries of every pool are the ones that select real targets; they are drawn about twice as often as the rest.
		static string Pick(SplitMix64 random, string[] values)
		{
			var likely = Math.Min(8, values.Length);

			return random.Next(3) == 0 ? values[random.Next(values.Length)] : values[random.Next(likely)];
		}

		// ---- records and hashes ----

		public static string Sha256(IEnumerable<string> records)
		{
			var sb = new StringBuilder();

			foreach (var record in records)
				sb.Append(record).Append('\n');

			return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
		}

		/// <summary>The SplitMix64 generator: a fixed algorithm, so seeds mean the same on every runtime.</summary>
		public sealed class SplitMix64(ulong seed)
		{
			ulong _state = seed;

			public ulong NextUInt64()
			{
				unchecked
				{
					var z = _state += 0x9E3779B97F4A7C15UL;

					z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
					z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;

					return z ^ (z >> 31);
				}
			}

			public int Next(int exclusiveMax)
			{
				return (int)(NextUInt64() % (ulong)exclusiveMax);
			}
		}
	}
}
