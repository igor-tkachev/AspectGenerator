using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AspectGenerator.Tests.FilterV2Oracle
{
	/// <summary>
	/// Reads the frozen 1.1 baselines (<c>Baselines/TargetFilters/*.txt</c>): what the 1.1 language read from every input
	/// of its corpus and what it decided for each of the targets of <see cref="OracleTargets"/>. The files are data of
	/// the past and are never written: nothing here can change them.
	/// <para>
	/// A file starts with comment lines (<c># key value</c>, one <c># target i TAB label TAB signature</c> per target),
	/// then holds one record per line, its fields separated by TAB: the input, the diagnostics (<c>id@line</c>, or
	/// <c>-</c>), the plan, the compiled rules, one decision per target (<c>n</c> none, <c>i</c> include, <c>e</c>
	/// exclude) and the index of the rule that decided each decided target. The large generated sets keep only a hash
	/// per block of records (<c>records a..b TAB hash</c>), which <see cref="Hashes"/> lists.
	/// </para>
	/// </summary>
	sealed class FrozenBaseline
	{
		public static readonly string[] FileNames = ["curated", "escapes", "generated", "mutations", "spaced"];

		public sealed record Record(string File, int Line, string Input, string Diagnostics, string Plan, string Rules, string Decisions, string Matched)
		{
			/// <summary>The ids of the diagnostics the 1.1 reader reported, without their line.</summary>
			public string[] DiagnosticIds
			{
				get
				{
					return Diagnostics == "-" ? [] : [..Diagnostics.Split(',').Select(static d => d.Split('@')[0])];
				}
			}

			/// <summary>Whether 1.1 included the target: its decision was <c>i</c>.</summary>
			public bool Included(int target)
			{
				return Decisions[target] == 'i';
			}
		}

		FrozenBaseline(string name, List<string> header, List<string> targets, List<Record> records, List<(string Range, string Hash)> hashes)
		{
			Name    = name;
			Header  = header;
			Targets = targets;
			Records = records;
			Hashes  = hashes;
		}

		public string Name { get; }

		/// <summary>The comment lines other than the target lines, as written.</summary>
		public IReadOnlyList<string> Header { get; }

		/// <summary>The <c># framework</c> line and the <c># target</c> lines, as written.</summary>
		public IReadOnlyList<string> Targets { get; }

		public IReadOnlyList<Record> Records { get; }

		public IReadOnlyList<(string Range, string Hash)> Hashes { get; }

		static readonly Dictionary<string,FrozenBaseline> _cache = [];

		public static FrozenBaseline Read(string name)
		{
			lock (_cache)
			{
				if (_cache.TryGetValue(name, out var cached))
					return cached;

				var path    = Path.Combine(RepositoryRoot(), "Baselines", "TargetFilters", name + ".txt");
				var header  = new List<string>();
				var targets = new List<string>();
				var records = new List<Record>();
				var hashes  = new List<(string, string)>();
				var number  = 0;

				foreach (var raw in File.ReadLines(path))
				{
					var line = raw.TrimEnd('\r');

					number++;

					if (line.Length == 0)
						continue;

					if (line.StartsWith("# target ", StringComparison.Ordinal) && line.Length > 9 && char.IsDigit(line[9]) || line.StartsWith("# framework ", StringComparison.Ordinal))
					{
						targets.Add(line);
						continue;
					}

					var fields = line.Split('\t');

					if (fields.Length == 1 && line.StartsWith("# ", StringComparison.Ordinal))
					{
						header.Add(line);
						continue;
					}

					if (fields.Length == 2 && fields[0].StartsWith("records ", StringComparison.Ordinal))
					{
						hashes.Add((fields[0], fields[1]));
						continue;
					}

					if (fields.Length != 6)
						throw new InvalidDataException($"{name}.txt:{number}: a record has {fields.Length} fields, expected 6.");

					records.Add(new Record(name, number, Unescape(fields[0]), fields[1], fields[2], fields[3], fields[4], fields[5]));
				}

				return _cache[name] = new FrozenBaseline(name, header, targets, records, hashes);
			}
		}

		public static IEnumerable<Record> AllRecords()
		{
			return FileNames.SelectMany(static n => Read(n).Records);
		}

		public static string RepositoryRoot()
		{
			for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
				if (File.Exists(Path.Combine(dir.FullName, "AspectGenerator.slnx")))
					return dir.FullName;

			throw new InvalidOperationException("AspectGenerator.slnx was not found above " + AppContext.BaseDirectory);
		}

		/// <summary>The escaping of one field: backslash, TAB, CR, LF, other control characters and non-ASCII.</summary>
		public static string Escape(string text)
		{
			var sb = new StringBuilder(text.Length + 8);

			foreach (var ch in text)
			{
				switch (ch)
				{
					case '\\': sb.Append("\\\\"); break;
					case '\t': sb.Append("\\t");  break;
					case '\r': sb.Append("\\r");  break;
					case '\n': sb.Append("\\n");  break;
					default:
						if (ch < 0x20 || ch >= 0x7f)
							sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
						else
							sb.Append(ch);
						break;
				}
			}

			return sb.ToString();
		}

		public static string Unescape(string text)
		{
			var sb = new StringBuilder(text.Length);

			for (var i = 0; i < text.Length; i++)
			{
				var ch = text[i];

				if (ch != '\\' || i + 1 == text.Length)
				{
					sb.Append(ch);
					continue;
				}

				var next = text[++i];

				switch (next)
				{
					case '\\': sb.Append('\\'); break;
					case 't':  sb.Append('\t'); break;
					case 'r':  sb.Append('\r'); break;
					case 'n':  sb.Append('\n'); break;
					case 'u':
						sb.Append((char)int.Parse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
						i += 4;
						break;
					default:
						throw new InvalidDataException($"An unknown escape \\{next} in '{text}'.");
				}
			}

			return sb.ToString();
		}
	}
}
