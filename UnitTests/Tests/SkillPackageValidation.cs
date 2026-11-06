using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests
{
	static class SkillPackageValidation
	{
		const string SkillRoot = "skills/aspectgenerator/";

		public static void Validate(ZipArchive archive)
		{
			Assert.IsNull(archive.GetEntry("SKILL.md"), "The skill must not be packed at the package root.");
			Assert.IsFalse(archive.Entries.Any(static entry => entry.FullName.StartsWith("Skills/", StringComparison.Ordinal)), "Skill sources must use the lowercase package layout.");

			var entryPoint = archive.GetEntry(SkillRoot + "SKILL.md");
			Assert.IsNotNull(entryPoint, "The package must contain the discoverable skill entry point.");
			ValidateFrontmatter(ReadText(entryPoint), "aspectgenerator");

			var guides = archive.Entries.Where(static entry => entry.FullName.StartsWith(SkillRoot + "docs/", StringComparison.Ordinal)).ToArray();
			Assert.IsTrue(guides.Length > 0, "The skill must include its task guides.");

			foreach (var entry in archive.Entries.Where(static entry => entry.FullName.StartsWith(SkillRoot, StringComparison.Ordinal)))
			{
				Assert.IsFalse(entry.FullName.EndsWith("README.md", StringComparison.OrdinalIgnoreCase), "Maintainer documentation must not ship inside the skill.");
				Assert.IsTrue(entry.FullName.EndsWith(".md", StringComparison.Ordinal), $"Unexpected skill asset: {entry.FullName}");
				ValidateLinks(archive, entry.FullName, ReadText(entry));
			}
		}

		public static void ValidateFrontmatter(string markdown, string folderName)
		{
			// This skill uses two plain YAML scalars; keep the contract explicit without a YAML dependency.
			var header = Regex.Match(markdown.Replace("\r\n", "\n"), @"\A---\nname: (?<name>[a-z0-9-]+)\ndescription: (?<description>[A-Za-z0-9][^\n]*)\n---(?:\n|\z)");
			Assert.IsTrue(header.Success, "Expected YAML frontmatter with a name and a one-line plain description.");
			Assert.AreEqual(folderName, header.Groups["name"].Value, "The skill name must match its folder.");

			var description = header.Groups["description"].Value;
			Assert.IsFalse(string.IsNullOrWhiteSpace(description), "A skill description is required.");
			Assert.IsTrue(description.Length <= 1024, "The description must have at most 1024 characters.");
			Assert.IsFalse(description.Contains(": ", StringComparison.Ordinal), "The plain description cannot contain a YAML mapping separator.");
			Assert.IsFalse(description.Contains(" #", StringComparison.Ordinal), "The plain description cannot contain a YAML comment.");
		}

		public static void ValidateLinks(ZipArchive archive, string sourcePath, string markdown)
		{
			// Include inline links/images and reference definitions. Code examples are not Markdown links.
			var prose = Regex.Replace(markdown.Replace("\r\n", "\n"), @"(?ms)^```[^\n]*\n.*?^```[^\n]*(?:\n|\z)", "");
			prose = Regex.Replace(prose, @"`[^`\n]*`", "");
			var inline = Regex.Matches(prose, @"!?\[[^\]\r\n]*\]\(\s*(?:<(?<target>[^>\r\n]+)>|(?<target>[^\s)]+))(?:\s+""[^""\r\n]*"")?\s*\)");
			var references = Regex.Matches(prose, @"(?m)^ {0,3}\[[^\]\r\n]+\]:\s*(?:<(?<target>[^>\r\n]+)>|(?<target>\S+))");

			foreach (var link in inline.Cast<Match>().Concat(references.Cast<Match>()))
			{
				var target = link.Groups["target"].Value;
				if (Regex.IsMatch(target, @"\A(?:https?://|mailto:)", RegexOptions.IgnoreCase))
					continue;

				Assert.IsFalse(target.StartsWith('/') || target.Contains('\\') || Regex.IsMatch(target, @"\A[A-Za-z][A-Za-z0-9+.-]*:"), $"Skill links must be portable relative paths: {sourcePath} -> {target}");

				var resolved = new Uri(new Uri("https://skill.invalid/" + sourcePath), target);
				var destination = Uri.UnescapeDataString(resolved.AbsolutePath.TrimStart('/'));
				Assert.IsTrue(destination.StartsWith(SkillRoot, StringComparison.Ordinal), $"Link escapes the skill folder: {sourcePath} -> {target}");
				Assert.IsNotNull(archive.GetEntry(destination), $"Broken skill link: {sourcePath} -> {target}");
			}
		}

		static string ReadText(ZipArchiveEntry entry)
		{
			using var stream = entry.Open();
			Assert.AreNotEqual(0xEF, stream.ReadByte(), $"Skill files must be UTF-8 without BOM: {entry.FullName}");
			using var reader = new StreamReader(entry.Open());
			return reader.ReadToEnd();
		}
	}

	[TestClass]
	public class SkillPackageValidationTests
	{
		[TestMethod]
		public void RejectsInvalidFrontmatter()
		{
			var invalid = new[]
			{
				"# No frontmatter\n",
				"---\nname: other\ndescription: Valid description\n---\n",
				"---\nname: aspectgenerator\n---\n",
				"---\nname: aspectgenerator\ndescription: \n---\n",
				"---\nname: aspectgenerator\ndescription: First line\nsecond line\n---\n",
				"---\nname: aspectgenerator\ndescription: Invalid: mapping\n---\n",
				"---\nname: aspectgenerator\ndescription: Invalid # comment\n---\n",
				"---\nname: aspectgenerator\ndescription: " + new string('x', 1025) + "\n---\n",
			};

			foreach (var markdown in invalid)
				Assert.ThrowsExactly<AssertFailedException>(() => SkillPackageValidation.ValidateFrontmatter(markdown, "aspectgenerator"));

			SkillPackageValidation.ValidateFrontmatter("---\nname: aspectgenerator\ndescription: " + new string('x', 1024) + "\n---\n", "aspectgenerator");
		}

		[TestMethod]
		public void ChecksLinksAgainstThePackedSkillBoundary()
		{
			using var stream = new MemoryStream();
			using var archive = new ZipArchive(stream, ZipArchiveMode.Update);
			archive.CreateEntry("skills/aspectgenerator/SKILL.md");
			archive.CreateEntry("skills/aspectgenerator/docs/task.md");
			archive.CreateEntry("README.md");

			const string source = "skills/aspectgenerator/docs/task.md";
			SkillPackageValidation.ValidateLinks(archive, source, "[Skill](../SKILL.md) [Self](task.md#section)\n[entry]: ../SKILL.md\n[Website](https://example.com)");

			foreach (var target in new[] { "missing.md", "../../../README.md", "/README.md", "..\\SKILL.md", "file:///README.md", "%2e%2e/%2e%2e/%2e%2e/README.md" })
				Assert.ThrowsExactly<AssertFailedException>(() => SkillPackageValidation.ValidateLinks(archive, source, $"[Link]({target})"));

			Assert.ThrowsExactly<AssertFailedException>(() => SkillPackageValidation.ValidateLinks(archive, source, "[entry]: missing.md\n"));
		}
	}
}
