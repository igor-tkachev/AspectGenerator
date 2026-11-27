using System;
using System.Collections.Generic;
using System.Linq;

namespace AspectGenerator.Tests.FilterV2Oracle
{
	/// <summary>
	/// Seeded random filters over the names and shapes of the fixed targets: words, keys with their patterns, compact
	/// signatures, references and <c>any</c>, combined with <c>and</c>, <c>or</c>, <c>not</c> and parentheses over a few
	/// lines, now and then a subtraction. A filter is kept as a tree, so that one that two readers decide differently can
	/// be shrunk to the smallest filter that still shows the difference.
	/// </summary>
	sealed class OracleGenerator(ulong seed)
	{
		ulong _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

		int Next(int bound)
		{
			// xorshift64*: deterministic on every platform.
			_state ^= _state >> 12;
			_state ^= _state << 25;
			_state ^= _state >> 27;

			return (int)((_state * 0x2545F4914F6CDD1DUL >> 33) % (ulong)bound);
		}

		T Pick<T>(IReadOnlyList<T> items)
		{
			return items[Next(items.Count)];
		}

		public abstract class Node
		{
			public abstract string Render();

			/// <summary>Smaller trees that still say something: each child alone, the node without its decoration.</summary>
			public abstract IEnumerable<Node> Shrink();
		}

		public sealed class Atom(string text) : Node
		{
			public string Text { get; } = text;

			public override string Render()
			{
				return Text;
			}

			public override IEnumerable<Node> Shrink()
			{
				return [];
			}
		}

		public sealed class Binary(string op, Node left, Node right) : Node
		{
			public override string Render()
			{
				return left.Render() + " " + op + " " + right.Render();
			}

			public override IEnumerable<Node> Shrink()
			{
				yield return left;
				yield return right;

				foreach (var l in left.Shrink())
					yield return new Binary(op, l, right);

				foreach (var r in right.Shrink())
					yield return new Binary(op, left, r);
			}
		}

		public sealed class Not(Node inner) : Node
		{
			public override string Render()
			{
				return "not " + inner.Render();
			}

			public override IEnumerable<Node> Shrink()
			{
				yield return inner;
			}
		}

		public sealed class Paren(Node inner) : Node
		{
			public override string Render()
			{
				return "(" + inner.Render() + ")";
			}

			public override IEnumerable<Node> Shrink()
			{
				yield return inner;

				foreach (var i in inner.Shrink())
					yield return new Paren(i);
			}
		}

		public sealed class Filter(IReadOnlyList<(bool Subtraction, Node Expr)> lines)
		{
			public IReadOnlyList<(bool Subtraction, Node Expr)> Lines { get; } = lines;

			public string Render()
			{
				return string.Join("\n", Lines.Select(static l => (l.Subtraction ? "- " : "") + l.Expr.Render()));
			}

			public IEnumerable<Filter> Shrink()
			{
				for (var i = 0; i < Lines.Count && Lines.Count > 1; i++)
					yield return new Filter([..Lines.Where((_, j) => j != i)]);

				for (var i = 0; i < Lines.Count; i++)
				{
					if (Lines[i].Subtraction)
						yield return new Filter([..Lines.Select((l, j) => j == i ? (false, l.Expr) : l)]);

					foreach (var smaller in Lines[i].Expr.Shrink())
						yield return new Filter([..Lines.Select((l, j) => j == i ? (l.Subtraction, smaller) : l)]);
				}
			}

			public override string ToString()
			{
				return Render();
			}
		}

		static readonly string[] _words =
		[
			"public", "internal", "protected", "private", "protected internal", "private protected", "static", "instance",
			"async", "virtual", "override", "abstract", "sealed", "extern", "extension", "project", "generated",
			"public static", "public async", "private static", "public virtual", "internal protected", "protected private",
		];

		static readonly string[] _namespaces =
		[
			"Acme.Core", "Acme.**", "Acme", "A.B.C.**", "Lib.Tasks", "System.**", "System", "System.Collections.Generic", "\"\"",
			"{Acme.**,Lib.**}", "global::Acme.Core", "**.Core", "Acme.*", "*.Core", "System.Threading.Tasks", "**", "G", "{System,G}",
			"A.B.C.D.E.F.G", "System.Linq", "*",
		];

		static readonly string[] _types =
		[
			"Service", "*Service", "Acme.Core.Service", "Repository<*>", "Repository<string>", "Repository", "Outer.Inner", "Inner",
			"Inner<*>", "Outer<int>.Inner<*>", "global::Acme.Core.Base", "List<*>", "List<int>", "String", "string", "Enumerable",
			"{Service,Derived}", "Pair<*,*>", "*.Deep", "Task", "Math", "Int32", "int", "Extensions", "Box<int>", "Box<*>", "Box",
			"Outer<*>.Inner<string>", "Pair<string,List<int>>", "{Order,Invoice}Service", "*", "Dictionary<*,*>", "Person", "IJob",
			"Outer.*", "**.Outer.**", "Counter", "Point", "Use", "Ext",
		];

		static readonly string[] _methods =
		[
			"Save", "Save*", "*Async", "Load<*>", "Load<int>", "Load", "Load<*,*>", "Get<*>", "Service.Save", "*.Save", "**.Save",
			"Acme.**.Run", "Run", "{Get,Find}*", "global::Acme.Core.Service.Save", "List<*>.Add", "List<int>.Add", "List.Add",
			"Repository<string>.Get", "Repository<*>.Get<int>", "int.Parse", "string.Join", "Enumerable.Any<int>", "Any", "Count",
			"Max", "*", "@event", "@params", "Save_Item", "Save2", "Outer.Inner.Run", "Outer<*>.Inner<*>.Run", "Dictionary<*,*>.Add",
			"Math.*", "String.*", "ToString", "Equals", "op_Addition", "Box<int>.Put", "Box<*>.Put", "Take<string>", "Take<*>",
			"Make<*>", "Make<int>", "Has<int>", "Ext.Has", "{**.Save,Other.Save}", "Сохранить", "Shout", "Repeat", "Deconstruct",
			"\"<Main>$\"", "Task.FromResult<int>", "Interlocked.*", "Array.Resize<int>", "**.Inner.*", "Get", "TryGet",
		];

		static readonly string[] _returns =
		[
			"void", "int", "string", "Task", "Task<*>", "Task<int>", "ValueTask*", "{void,Task}", "int?", "*?", "int[]", "string[][]",
			"int[,]", "(int, string)", "*", "ref int", "ref readonly int", "List<*>", "IEnumerable<*>", "bool", "T", "*[]", "object",
			"Dictionary<string, List<int?>>", "System.Nullable<*>", "\"System.Int32*\"", "\"System.Void*\"", "TResult", "string?",
			"{int[], string}", "Task<List<int?>>", "ReadOnlySpan<char>", "System.Void", "KeyValuePair<*,*>", "Repository<*>", "dynamic",
		];

		static readonly string[] _paramLists =
		[
			"()", "(..)", "(int)", "(int, ..)", "(.., CancellationToken)", "(_, out bool)", "(this string)", "(string)", "(int, int)",
			"(ref int)", "(in Big)", "(ref readonly int)", "(params string[])", "(string, params object[])", "(_, _)", "(.., int)",
			"(*)", "(this *)", "(T)", "(string?)", "(int?)", "(_)", "(this IEnumerable<*>, ..)", "(params *)", "(out int)", "(ref *, ..)",
			"(this IEnumerable<int>)", "(this ref int)", "(ref int, ref int)", "(*[])", "(int[], string?[])", "((int, string?), ..)",
			"(params ReadOnlySpan<int>)", "(this string, int)", "(in *)", "(.., params *)", "(TKey, out TValue)",
		];

		static readonly string[] _params =
		[
			"int", "string", "out *", "ref int", "in *", "this string", "params *", "CancellationToken", "_", "int?", "*?", "int[]",
			"List<*>", "T", "object", "this *", "params int[]", "ref readonly *", "*[]", "ref *", "out int", "(int, string)",
			"Func<*,bool>", "IEnumerable<*>", "this IEnumerable<int>", "TKey", "string?[]", "\"System.Int32*\"", "U",
		];

		static readonly string[] _attributes =
		[
			"Marker", "MarkerAttribute", "Tag", "TagAttribute", "Obsolete", "GenericMarker<*>", "GenericMarker<int>", "*Marker",
			"Conditional", "System.ObsoleteAttribute", "CompilerGenerated", "Nullable*", "*", "GenericMarker", "{Marker,Tag}",
			"Intrinsic", "*Attribute", "NullableContext",
		];

		static readonly string[] _inherits =
		[
			"Base", "Derived", "IJob", "Object", "object", "Service", "*", "IEquatable<*>", "Person", "ValueType", "Acme.Core.Base",
			"IEquatable<Person>", "Leaf", "IEnumerable<*>", "ICollection<int>", "List<*>", "Box<*>",
		];

		static readonly string[] _implements =
		[
			"IJob.Run", "IJob.*", "Base.Virtual", "Base.*", "Object.ToString", "*.ToString", "Base.Abstract", "IEquatable<*>.Equals",
			"Derived.Virtual", "*", "Object.*", "ICollection<int>.Add", "ICollection<*>.Add", "IJob.Log", "Base.Name",
		];

		static readonly string[] _signatures =
		[
			"public Save*", "Save*(int)", "Save*(..) : void", "static *(..) : int", "*Async : Task*",
			"public **.*Service.Save*(.., CancellationToken) : Task*", "Get*() : int?", "Load<*>(*)", "**.Run()", "Save: void",
			"Save (int)", "public static *", "public Save*(..)", "Max(int, int) : int", "*.Add(*)", "@event()", "*(this string)",
			"global::System.**.Parse(string)", "Run : void", "virtual *", "public static Acme.Core.Service.Create",
		];

		static readonly string[] _properties =
		[
			"location", "method", "returns", "params", "param", "attribute", "inherits", "implements", "accessibility", "static",
			"async", "virtuality", "extern", "extension", "origin", "generated",
		];

		static readonly string[] _invalid =
		[
			"method: Get?", "type: Task<>", "not (public or internal)", "foo: bar", "Method: Save*", "params: int", "publik Save",
			"method: Save*; static", "contains: Save", "method: Sa\\ve", "assembly.method", "method: assembly.returns",
			"overrides: Base.Save", "method: Save**", "type: *.static", "unsafe", "within: Acme", "params: (int, ...)",
		];

		Node RandomAtom()
		{
			var roll = Next(100);

			string text = roll switch
			{
				< 16 => Pick(_words),
				< 24 => "namespace: " + Pick(_namespaces),
				< 32 => "type: " + Pick(_types),
				< 46 => "method: " + Pick(_methods),
				< 53 => "returns: " + Pick(_returns),
				< 59 => "params: " + Pick(_paramLists),
				< 65 => "param: " + Pick(_params),
				< 70 => "attribute: " + Pick(_attributes),
				< 74 => "inherits: " + Pick(_inherits),
				< 78 => "implements: " + Pick(_implements),
				< 88 => Pick(_signatures),
				< 92 => "aspect." + Pick(_properties),
				< 95 => Pick(_properties) + ": aspect." + Pick(_properties),
				< 98 => Pick(_methods),
				_    => Pick(_invalid),
			};

			return new Atom(text);
		}

		Node Expr(int depth)
		{
			if (depth == 0)
				return Next(6) == 0 ? new Not(RandomAtom()) : RandomAtom();

			return Next(10) switch
			{
				< 3 => new Binary("and", Expr(depth - 1), Expr(depth - 1)),
				< 5 => new Binary("or", Expr(depth - 1), Expr(depth - 1)),
				5   => new Paren(Expr(depth - 1)),
				6   => new Not(RandomAtom()),
				_   => RandomAtom(),
			};
		}

		public Filter Next()
		{
			var count = Next(10) switch { < 6 => 1, < 9 => 2, _ => 3 };
			var lines = new List<(bool, Node)>();

			for (var i = 0; i < count; i++)
			{
				var subtraction = i > 0 ? Next(4) != 0 : Next(5) == 0;

				if (Next(40) == 0 && !subtraction)
				{
					lines.Add((false, new Atom(Pick(_properties) + ": any")));
					continue;
				}

				lines.Add((subtraction, Expr(Next(3))));
			}

			return new Filter(lines);
		}
	}
}
