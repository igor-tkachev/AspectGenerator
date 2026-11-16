using System;
using System.Collections.Generic;
using System.Linq;

using AspectGenerator.Cascade;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.Cascade
{
	/// <summary>Rules of the cascade the case table does not reach: settings within one chain, nested references, the API.</summary>
	[TestClass]
	public class CascadeEvaluatorTests
	{
		const string Type = "Shop.Services.OrderService";

		static CaseFact Fact(string name, string accessibility = "public", bool project = true, params string[] attributes)
		{
			return new()
			{
				Id            = Type + "." + name,
				Namespace     = "Shop.Services",
				Type          = Type,
				Name          = name,
				Accessibility = accessibility,
				Project       = project,
				Attributes    = [..attributes],
			};
		}

		static CascadeBlock<CaseFact> Block(CascadeLevel level, string? filter, params (string Name, object? Value)[] settings)
		{
			var (lines, errors) = CascadeTextReader.Read(filter);

			return CascadeBlock<CaseFact>.Create(level, lines, settings.ToDictionary(s => s.Name, s => s.Value), level.ToString(), readDiagnostics: errors);
		}

		static CascadeEvaluator<CaseFact> Evaluator(CascadeBlock<CaseFact> aspectDefault, IEnumerable<CascadeBlock<CaseFact>> assembly, params CascadeBlock<CaseFact>[] others)
		{
			return new(aspectDefault, assembly, others, f => f.Project);
		}

		[TestMethod]
		public void MostSpecificWriterOfASettingWinsTest()
		{
			var assembly  = Block(CascadeLevel.Assembly, "namespace: Shop.**", ("Level", "Debug"), ("Format", "Short"));
			var type      = Block(CascadeLevel.Type,     null,                 ("Level", "Info"));
			var method    = Block(CascadeLevel.Method,   null,                 ("Format", "Long"));
			var evaluator = Evaluator(Block(CascadeLevel.Aspect, "- attribute: NoLog"), [assembly], type, method);

			var onType = evaluator.Decide(Fact("Load"), [type], []);

			Assert.IsTrue(onType.Selected);
			Assert.AreEqual("Info",  onType.Settings["Level"].Value);
			Assert.AreSame (type,    onType.Settings["Level"].Block);
			Assert.AreEqual("Short", onType.Settings["Format"].Value);
			Assert.AreSame (assembly, onType.Settings["Format"].Block);

			var onMethod = evaluator.Decide(Fact("Save"), [type], [method]);

			Assert.IsTrue(onMethod.Selected);
			Assert.AreEqual("Info", onMethod.Settings["Level"].Value);
			Assert.AreEqual("Long", onMethod.Settings["Format"].Value);
		}

		[TestMethod]
		public void SelectingChainsThatAgreeAreNoConflictTest()
		{
			var a         = Block(CascadeLevel.Assembly, "public",   ("Level", "Debug"));
			var b         = Block(CascadeLevel.Assembly, "internal", ("Level", "Debug"));
			var type      = Block(CascadeLevel.Type,     "accessibility: any");
			var evaluator = Evaluator(Block(CascadeLevel.Aspect, null), [a, b], type);
			var decision  = evaluator.Decide(Fact("Load"), [type], []);

			Assert.IsTrue(decision.Selected);
			Assert.AreEqual(0, decision.Conflicts.Length);
			Assert.AreEqual("Debug", decision.Settings["Level"].Value);
		}

		[TestMethod]
		public void ConflictNamesEveryValueAndItsBlocksTest()
		{
			var a         = Block(CascadeLevel.Assembly, "public",   ("Level", "Debug"));
			var b         = Block(CascadeLevel.Assembly, "internal", ("Level", "Info"));
			var type      = Block(CascadeLevel.Type,     "accessibility: any");
			var evaluator = Evaluator(Block(CascadeLevel.Aspect, null), [a, b], type);
			var decision  = evaluator.Decide(Fact("Load"), [type], []);

			Assert.IsTrue(decision.Selected);
			Assert.IsFalse(decision.Settings.ContainsKey("Level"));
			Assert.AreEqual(1, decision.Conflicts.Length);
			Assert.AreEqual("Level", decision.Conflicts[0].Name);
			CollectionAssert.AreEquivalent(new object?[] { "Debug", "Info" }, decision.Conflicts[0].Values.Select(v => v.Value).ToList());
			CollectionAssert.AreEquivalent(new object[] { a, b }, decision.Conflicts[0].Values.SelectMany(v => v.Blocks).ToList());
		}

		[TestMethod]
		public void ReferenceInsideAReferencedLevelIsResolvedOnItsOwnCutTest()
		{
			// The method's assembly.attribute is the assembly's value, which in turn keeps the author's.
			var aspectDefault = Block(CascadeLevel.Aspect,   "- attribute: NoLog");
			var assembly      = Block(CascadeLevel.Assembly, "attribute: Audited and aspect.attribute");
			var method        = Block(CascadeLevel.Method,   "attribute: assembly.attribute");
			var evaluator     = Evaluator(aspectDefault, [assembly], method);

			Assert.IsTrue (evaluator.Decide(Fact("A", attributes: "AuditedAttribute"),                  [], [method]).Selected);
			Assert.IsFalse(evaluator.Decide(Fact("B", attributes: ["AuditedAttribute", "NoLogAttribute"]), [], [method]).Selected);
			Assert.IsFalse(evaluator.Decide(Fact("C"),                                                    [], [method]).Selected);
		}

		[TestMethod]
		public void LibraryConflictIsReportedForTheLibraryFamilyTest()
		{
			var a         = Block(CascadeLevel.Assembly, "public",   ("Level", "Debug"));
			var b         = Block(CascadeLevel.Assembly, "static",   ("Level", "Info"));
			var method    = Block(CascadeLevel.Method,   null);
			var evaluator = Evaluator(Block(CascadeLevel.Aspect, null), [], a, b, method);
			var fact      = Fact("Write", project: false);

			fact.Static = true;

			var decision = evaluator.DecideLibraryMethod(fact.AsProject(), [a, b], [], [method], fact);

			Assert.IsTrue(decision.Selected);
			Assert.AreEqual(CascadeFamily.Library, decision.Family);
			Assert.AreEqual(1, decision.Conflicts.Length);
		}

		[TestMethod]
		public void ReferenceLevelsAllowedPerBlockLevelTest()
		{
			Assert.IsFalse(Block(CascadeLevel.Method,   "method: aspect.method").HasErrors);
			Assert.IsFalse(Block(CascadeLevel.Method,   "method: assembly.method").HasErrors);
			Assert.IsFalse(Block(CascadeLevel.Type,     "- assembly.method").HasErrors);
			Assert.IsFalse(Block(CascadeLevel.Assembly, "attribute: aspect.attribute").HasErrors);
			Assert.IsTrue (Block(CascadeLevel.Assembly, "method: assembly.method").HasErrors);
			Assert.IsTrue (Block(CascadeLevel.Aspect,   "attribute: aspect.attribute").HasErrors);
		}

		[TestMethod]
		public void SplitKeepsConnectedOperandsTogetherTest()
		{
			var (lines, _) = CascadeTextReader.Read("method: Save* and public and static and (static or method: *A)");
			var parts      = CascadeBlock<CaseFact>.Split(lines[0]);

			Assert.AreEqual(2, parts.Length);
			Assert.AreEqual(CascadeProperty.Accessibility,                  parts[0].Properties);
			Assert.AreEqual(CascadeProperty.Method | CascadeProperty.Static, parts[1].Properties);
		}

		[TestMethod]
		public void ParenthesesInAnIncludeDoNotChangeTheSplitTest()
		{
			var (flat,   _) = CascadeTextReader.Read("static and method: Save* and public");
			var (nested, _) = CascadeTextReader.Read("static and (method: Save* and public)");
			var (deeper, _) = CascadeTextReader.Read("(static and (method: Save* and (public)))");

			foreach (var lines in new[] { flat, nested, deeper })
			{
				var parts = CascadeBlock<CaseFact>.Split(lines[0]);

				CollectionAssert.AreEqual(
					new[] { CascadeProperty.Static, CascadeProperty.Method, CascadeProperty.Accessibility },
					parts.Select(p => p.Properties).ToArray());
			}
		}

		[TestMethod]
		public void ParenthesesInASubtractionDoNotChangeTheSplitTest()
		{
			var (flat,   _) = CascadeTextReader.Read("- static or method: Get* or public");
			var (nested, _) = CascadeTextReader.Read("- static or (method: Get* or public)");

			foreach (var lines in new[] { flat, nested })
			{
				var parts = CascadeBlock<CaseFact>.Split(lines[0]);

				CollectionAssert.AreEqual(
					new[] { CascadeProperty.Static, CascadeProperty.Method, CascadeProperty.Accessibility },
					parts.Select(p => p.Properties).ToArray());
			}
		}

		[TestMethod]
		public void ParenthesesDoNotChangeTheCascadeTest()
		{
			// The type's internal replaces the assembly's public only: method: Save* still applies.
			foreach (var filter in new[] { "static and method: Save* and public", "static and (method: Save* and public)" })
			{
				var assembly  = Block(CascadeLevel.Assembly, filter);
				var type      = Block(CascadeLevel.Type,     "internal");
				var evaluator = Evaluator(Block(CascadeLevel.Aspect, null), [assembly], type);
				var save      = Fact("SaveA", "internal");
				var load      = Fact("Load",  "internal");

				save.Static = true;
				load.Static = true;

				Assert.IsTrue (evaluator.Decide(save, [type], []).Selected, filter);
				Assert.IsFalse(evaluator.Decide(load, [type], []).Selected, filter);
			}

			foreach (var filter in new[] { "- static or method: Get* or public", "- static or (method: Get* or public)" })
			{
				var assembly  = Block(CascadeLevel.Assembly, "namespace: Shop.**\n" + filter);
				var type      = Block(CascadeLevel.Type,     "- internal");
				var evaluator = Evaluator(Block(CascadeLevel.Aspect, null), [assembly], type);

				Assert.IsTrue (evaluator.Decide(Fact("Load"),           [type], []).Selected, filter);
				Assert.IsFalse(evaluator.Decide(Fact("GetA"),           [type], []).Selected, filter);
				Assert.IsFalse(evaluator.Decide(Fact("Run", "internal"), [type], []).Selected, filter);
			}
		}

		[TestMethod]
		public void EqualArraySettingsAgreeTest()
		{
			var a         = Block(CascadeLevel.Assembly, "public",   ("Tags", new[] { "x", "y" }));
			var b         = Block(CascadeLevel.Assembly, "internal", ("Tags", new[] { "x", "y" }));
			var type      = Block(CascadeLevel.Type,     "accessibility: any");
			var evaluator = Evaluator(Block(CascadeLevel.Aspect, null), [a, b], type);
			var decision  = evaluator.Decide(Fact("Load"), [type], []);

			Assert.AreEqual(0, decision.Conflicts.Length);
			CollectionAssert.AreEqual(new[] { "x", "y" }, (string[])decision.Settings["Tags"].Value!);

			var c = Block(CascadeLevel.Assembly, "internal", ("Tags", new[] { "y", "x" }));

			Assert.AreEqual(1, Evaluator(Block(CascadeLevel.Aspect, null), [a, c], type).Decide(Fact("Load"), [type], []).Conflicts.Length);
		}

		[TestMethod]
		public void EqualConstructorArgumentBundlesAgreeTest()
		{
			// All constructor arguments together are one setting; an array argument nests inside the bundle.
			object?[] Bundle(int level, params string[] tags)
			{
				return [level, tags, null];
			}

			var a         = Block(CascadeLevel.Assembly, "public",   ("()", Bundle(1, "x")));
			var b         = Block(CascadeLevel.Assembly, "internal", ("()", Bundle(1, "x")));
			var c         = Block(CascadeLevel.Assembly, "internal", ("()", Bundle(1, "z")));
			var type      = Block(CascadeLevel.Type,     "accessibility: any");

			Assert.AreEqual(0, Evaluator(Block(CascadeLevel.Aspect, null), [a, b], type).Decide(Fact("Load"), [type], []).Conflicts.Length);
			Assert.AreEqual(1, Evaluator(Block(CascadeLevel.Aspect, null), [a, c], type).Decide(Fact("Load"), [type], []).Conflicts.Length);
		}

		[TestMethod]
		public void SettingsOfAssemblyBlocksChosenPerMethodBlockTest()
		{
			// Each method block references the assembly level, so each picks its own assembly block: two values, a conflict.
			var a         = Block(CascadeLevel.Assembly, "method: Save*", ("Level", "Debug"));
			var b         = Block(CascadeLevel.Assembly, "method: Load*", ("Level", "Info"));
			var first     = Block(CascadeLevel.Method,   "method: assembly.method");
			var second    = Block(CascadeLevel.Method,   "not assembly.method");
			var evaluator = Evaluator(Block(CascadeLevel.Aspect, null), [a, b], first, second);

			var both = evaluator.Decide(Fact("SaveA"), [], [first, second]);

			Assert.IsTrue(both.Selected);
			Assert.AreEqual(1, both.Conflicts.Length);

			var one = evaluator.Decide(Fact("SaveA"), [], [first]);

			Assert.AreEqual(0, one.Conflicts.Length);
			Assert.AreEqual("Debug", one.Settings["Level"].Value);
		}

		[TestMethod]
		public void ABlockOfAnotherLevelIsRejectedTest()
		{
			var type = Block(CascadeLevel.Type, null);

			Assert.ThrowsExactly<ArgumentException>(() => Evaluator(Block(CascadeLevel.Aspect, null), [type]));
			Assert.ThrowsExactly<ArgumentException>(() => Evaluator(Block(CascadeLevel.Aspect, null), []).Decide(Fact("A"), [], [type]));
		}
	}
}
