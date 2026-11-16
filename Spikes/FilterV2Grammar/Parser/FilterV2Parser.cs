using System;
using System.Collections.Generic;

using DotGram;

namespace AspectGenerator.FilterV2
{
	/// <summary>The v2 target filter grammar: a reader of one filter item into a <see cref="Tree"/> of spans.</summary>
	[Gram("FilterV2.gram")]
	public partial class FilterV2Parser
	{
		/// <summary>A reading nested deeper than the stack allows fails instead of moving to a thread of its own.</summary>
		static partial void OnHandingOff(ref object? state)
		{
			throw new InsufficientExecutionStackException();
		}

		static Tree N(string name, SourceSpan span, params object?[] children)
		{
			var list = new List<Tree>();

			foreach (var child in children)
			{
				switch (child)
				{
					case Tree tree:
						list.Add(tree);
						break;
					case Tree[] trees:
						list.AddRange(trees);
						break;
				}
			}

			return new Tree(name, (int)span.Start, (int)span.Length, list);
		}
	}

	/// <summary>A node of what the grammar read: a rule's name, where it stands and what it holds.</summary>
	public sealed class Tree
	{
		public Tree(string name, int position, int length, IReadOnlyList<Tree> children)
		{
			Name     = name;
			Position = position;
			Length   = length;
			Children = children;
		}

		public string             Name     { get; }
		public int                Position { get; }
		public int                Length   { get; }
		public IReadOnlyList<Tree> Children { get; }

		/// <summary>Every node of the tree, without recursion: a tree is as deep as the brackets of the filter.</summary>
		public List<Tree> DescendantsAndSelf()
		{
			var result = new List<Tree>();
			var stack  = new Stack<Tree>();

			stack.Push(this);

			while (stack.Count > 0)
			{
				var node = stack.Pop();

				result.Add(node);

				for (var i = node.Children.Count - 1; i >= 0; i--)
					stack.Push(node.Children[i]);
			}

			return result;
		}

		public override string ToString()
		{
			return Children.Count == 0 ? $"{Name}@{Position}+{Length}" : $"{Name}@{Position}+{Length}({string.Join(", ", Children)})";
		}
	}
}
