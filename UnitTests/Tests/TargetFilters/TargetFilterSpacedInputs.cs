using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// Generated filters that put white space where a filter may hold it without saying anything about a name: inside
	/// <c>&lt;…&gt;</c>, around the commas of a parameter list or of type arguments, before <c>?</c> and <c>[]</c> in a type
	/// pattern, after a parameter modifier and between modifier words. White space is a dimension of the generator here
	/// rather than a token a mutation inserts, so that every input is a filter someone could write.
	/// </summary>
	static class TargetFilterSpacedInputs
	{
		public const ulong Seed  = 0x4147_5446_5350_0001UL;
		public const int   Count = 5_000;

		static readonly string[] Modifiers =
		[
			"", "public ", "public  static ", "protected internal ", "internal protected ", "private protected ", "protected private ",
			"internal ", "private ", "protected ", "static ", "public\tvirtual ", "instance "
		];

		static readonly string[] Paths = ["", "Acme.Core.", "**.", "Acme.Core.Repository<*>.", "Acme.Core.Pair<*,*>.", "Acme.Core.Outer<*>.Inner<*>.", "System.Collections.Generic.List<int>."];

		static readonly string[] Names = ["Save", "Run", "Get<*>", "Load<T>", "Load<*,*>", "Load<string,int>", "Max", "*Async", "Save*", "Get<List<int>>"];

		static readonly string[] Types =
		[
			"int", "string", "int?", "string[]", "int[,]", "int[][,]", "List<int>", "List<int>?", "Dictionary<string,List<int?>>", "Task<int>[]",
			"System.ValueTuple<int,string>", "System.Collections.Generic.List<System.Int32>", "T", "T?", "*", "*[]", "List<*>?", "Task*",
			"System.Threading.Tasks.Task<System.Int32>", "Pair<*,*>[]"
		];

		static readonly string[] ParameterModifiers = ["", "", "", "ref ", "ref readonly ", "out ", "in ", "params ", "ref  readonly  ", "out  "];

		/// <summary>The deterministic inputs: the same seed gives the same inputs on every machine.</summary>
		public static IEnumerable<string> Generate(int count = Count, ulong seed = Seed)
		{
			for (var i = 0; i < count; i++)
			{
				var random = new TargetFilterCorpus.SplitMix64(seed + (ulong)i * 0x9E3779B97F4A7C15UL);

				yield return Build(random);
			}
		}

		static string Build(TargetFilterCorpus.SplitMix64 random)
		{
			return random.Next(9) switch
			{
				0 or 1 or 2 => Space(random, Pick(random, Modifiers)) + Space(random, Pick(random, Paths) + Pick(random, Names)) + Parameters(random) + Returns(random),
				3           => "returns: " + Type(random),
				4           => "param: " + Parameter(random),
				5           => "params: " + Parameters(random),
				6           => "method: " + Space(random, Pick(random, Names)),
				7           => "fulltype: " + Space(random, "Acme.Core.Repository<" + Type(random) + ">"),
				_           => "attributes: " + Type(random) + "; " + Space(random, Pick(random, Modifiers)).Trim() + "; returns: " + Type(random)
			};
		}

		static string Parameters(TargetFilterCorpus.SplitMix64 random)
		{
			var count = random.Next(4);
			var items = Enumerable.Range(0, count).Select(_ => random.Next(6) == 0 ? (random.Next(2) == 0 ? "..." : "_") : Parameter(random));

			return Space(random, "(" + string.Join(",", items) + ")");
		}

		static string Parameter(TargetFilterCorpus.SplitMix64 random)
		{
			return Pick(random, ParameterModifiers) + Type(random);
		}

		static string Returns(TargetFilterCorpus.SplitMix64 random)
		{
			return random.Next(2) == 0 ? "" : " : " + Type(random);
		}

		static string Type(TargetFilterCorpus.SplitMix64 random)
		{
			return Space(random, Pick(random, Types));
		}

		/// <summary>White space after each <c>&lt;</c>, before each <c>&gt;</c>, around each comma and before each <c>?</c> and <c>[</c>, each by chance.</summary>
		static string Space(TargetFilterCorpus.SplitMix64 random, string text)
		{
			var sb = new StringBuilder(text.Length * 2);

			foreach (var ch in text)
			{
				if (ch is '>' or ',' or '?' or '[' && random.Next(2) == 0)
					sb.Append(Blank(random));

				sb.Append(ch);

				if (ch is '<' or ',' && random.Next(2) == 0)
					sb.Append(Blank(random));
			}

			return sb.ToString();
		}

		static string Blank(TargetFilterCorpus.SplitMix64 random)
		{
			return random.Next(4) switch { 0 => "\t", 1 => "  ", _ => " " };
		}

		static string Pick(TargetFilterCorpus.SplitMix64 random, string[] values)
		{
			return values[random.Next(values.Length)];
		}
	}
}
