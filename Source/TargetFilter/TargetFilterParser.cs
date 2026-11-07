using DotGram;

namespace AspectGenerator
{
	/// <summary>
	/// The parser of a target filter line. Its members are generated from <c>TargetFilter.gram</c> and committed under
	/// <c>Generated</c>; the build does not run the generator. Nothing in the generator uses it yet: it is compiled and
	/// exercised by the unit tests, and filters are still parsed by the hand-written parser.
	/// </summary>
	[Gram("TargetFilter.gram")]
	internal partial class TargetFilterParser
	{
	}
}
