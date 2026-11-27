using System;
using System.IO;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>Where the repository and its baseline files are, for the tests that read or write them.</summary>
	static class TargetFilterCuratedInputs
	{
		public static string RepositoryRoot
		{
			get
			{
				for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
					if (File.Exists(Path.Combine(dir.FullName, "AspectGenerator.slnx")))
						return dir.FullName;

				throw new InvalidOperationException("AspectGenerator.slnx was not found above " + AppContext.BaseDirectory);
			}
		}

		public static string BaselinePath(string name)
		{
			return Path.Combine(RepositoryRoot, "Baselines", "TargetFilters", name);
		}
	}
}
