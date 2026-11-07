namespace AspectGenerator
{
	/// <summary>One line of a target filter as the grammar reads it.</summary>
	internal sealed class TargetFilterLine
	{
		public TargetFilterLine(string? @operator, string? matcherPrefix, string text)
		{
			Operator      = @operator;
			MatcherPrefix = matcherPrefix;
			Text          = text;
		}

		/// <summary>The leading <c>-</c>, <c>&amp;</c> or <c>|</c>, or <see langword="null"/>.</summary>
		public string? Operator { get; }

		/// <summary>The matcher prefix with its colon (<c>pattern:</c>, <c>contains:</c>, <c>regex:</c>), or <see langword="null"/>.</summary>
		public string? MatcherPrefix { get; }

		/// <summary>The rest of the line.</summary>
		public string Text { get; }
	}
}
