using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.FilterV2Oracle
{
	/// <summary>
	/// The records of the frozen 1.1 baselines that write <c>**</c> as a whole type pattern (<c>returns: **</c>), counted under
	/// both readings of it. As a name pattern, which is the default, no array or pointer matches it, and version 2 decides
	/// those records otherwise than 1.1; as any type it decides every target of the fixture as 1.1 did, once the project-only
	/// default of an assembly filter is out of the count. The counts are also pinned in <c>differential-summary.txt</c>.
	/// </summary>
	[TestClass]
	public class V1V2DifferentialAnyTypeTests
	{
		[TestMethod]
		public void RecordsWithAWholeTypeDoubleStarAreCountedUnderBothReadingsTest()
		{
			var differential = new V1V2Differential();
			var records      = new[] { "curated", "escapes", "mutations" }
				.SelectMany(static f => FrozenBaseline.Read(f).Records)
				.Select(differential.Classify)
				.Where(V1V2Differential.WritesWholeTypeDoubleStar)
				.ToList();

			Assert.AreEqual(4, records.Count);

			foreach (var record in records)
			{
				var label = $"{record.Record.File}:{record.Record.Line} '{record.Record.Input}'";

				Assert.IsTrue(differential.TargetsApart(record, false).Count > 0, $"{label}: read as a name, version 2 differs from 1.1.");
				Assert.AreEqual(0, differential.TargetsApart(record, true, beyondProject: true).Count, $"{label}: read as any type, it decides as 1.1 did where it reaches the same methods.");
				Assert.IsTrue(differential.TargetsApart(record, false, beyondProject: true).Count > 0, $"{label}: read as a name it differs there too.");
			}
		}
	}
}
