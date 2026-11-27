using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

using AspectGenerator.Cascade;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.Cascade
{
	/// <summary>The explanation of a decision: the same answer as the decision, and the declaration behind each part of it.</summary>
	[TestClass]
	public class CascadeExplainTests
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

		static CascadeBlock<CaseFact> Block(CascadeLevel level, string? filter, string? label = null, params (string Name, object? Value)[] settings)
		{
			var (lines, errors) = CascadeTextReader.Read(filter);

			return CascadeBlock<CaseFact>.Create(level, lines, settings.ToDictionary(s => s.Name, s => s.Value), label ?? level.ToString(), readDiagnostics: errors);
		}

		static CascadeEvaluator<CaseFact> Evaluator(CascadeBlock<CaseFact> aspectDefault, IEnumerable<CascadeBlock<CaseFact>> assembly, params CascadeBlock<CaseFact>[] others)
		{
			return new(aspectDefault, assembly, others, f => f.Project);
		}

		static CascadeDeclarationExplanation Decider(CascadeCombination combination, CascadeProperty property)
		{
			return combination.Properties.Single(p => p.Property == property).Declarations.Single();
		}

		[TestMethod]
		public void WrittenIncludeTest()
		{
			var assembly    = Block(CascadeLevel.Assembly, "namespace: Shop.**");
			var type        = Block(CascadeLevel.Type,     "method: Save*");
			var evaluator   = Evaluator(Block(CascadeLevel.Aspect, null), [assembly], type);
			var explanation = evaluator.Explain(Fact("SaveOrder"), [type], []);

			Assert.IsTrue(explanation.Selected);
			Assert.AreEqual(1, explanation.Combinations.Length);

			var combination = explanation.Combinations[0];
			var method      = Decider(combination, CascadeProperty.Method);

			Assert.IsTrue(combination.Selected);
			CollectionAssert.AreEqual(new[] { "Type", "Assembly", "Aspect" }, combination.Blocks.Select(b => b.Label).ToList());
			Assert.AreSame(type, method.Block.Block);
			Assert.AreEqual(CascadeLevel.Type, method.Block.Level);
			Assert.AreEqual("method: Save*", method.Text);
			Assert.IsFalse(method.IsImplicit);
			Assert.IsFalse(method.IsSubtraction);
			Assert.IsTrue(method.Satisfied);

			Assert.IsFalse(evaluator.Explain(Fact("Load"), [type], []).Combinations[0].Selected);
		}

		[TestMethod]
		public void ImplicitDeclarationTest()
		{
			// The type's implicit location replaces the assembly's namespace, which therefore applies nowhere.
			var assembly    = Block(CascadeLevel.Assembly, "namespace: Other.**");
			var type        = Block(CascadeLevel.Type,     null);
			var method      = Block(CascadeLevel.Method,   null);
			var evaluator   = Evaluator(Block(CascadeLevel.Aspect, null), [assembly], type, method);
			var explanation = evaluator.Explain(Fact("Load"), [type], [method]);
			var combination = explanation.Combinations.Single();
			var location    = Decider(combination, CascadeProperty.Location);
			var name        = Decider(combination, CascadeProperty.Method);

			Assert.IsTrue(explanation.Selected);
			Assert.AreSame(method, location.Block.Block);
			Assert.IsTrue(location.IsImplicit);
			Assert.AreEqual("location: here (implicit)", location.Text);
			Assert.IsTrue(name.IsImplicit);
			Assert.AreEqual("method: here (implicit)", name.Text);
			Assert.IsFalse(combination.Declarations.Any(d => d.Block.Block == assembly && d.Properties == CascadeProperty.Location));

			// An assembly block alone keeps its implicit project.
			var alone  = Evaluator(Block(CascadeLevel.Aspect, null), [Block(CascadeLevel.Assembly, "public")]);
			var origin = Decider(alone.Explain(Fact("Load", project: false), [], []).Combinations.Single(), CascadeProperty.Origin);

			Assert.IsTrue(origin.IsImplicit);
			Assert.IsFalse(origin.Satisfied);
		}

		[TestMethod]
		public void SubtractionTest()
		{
			var assembly    = Block(CascadeLevel.Assembly, "namespace: Shop.**\n- method: *Internal*");
			var evaluator   = Evaluator(Block(CascadeLevel.Aspect, null), [assembly]);
			var explanation = evaluator.Explain(Fact("RunInternal"), [], []);
			var method      = Decider(explanation.Combinations.Single(), CascadeProperty.Method);

			Assert.IsFalse(explanation.Selected);
			Assert.IsTrue(method.IsSubtraction);
			Assert.IsFalse(method.Satisfied);
			Assert.AreEqual("- method: *Internal*", method.Text);
			Assert.IsTrue(Decider(explanation.Combinations[0], CascadeProperty.Location).Satisfied);
		}

		[TestMethod]
		public void AnyTest()
		{
			var aspectDefault = Block(CascadeLevel.Aspect,   "- attribute: NoLog");
			var assembly      = Block(CascadeLevel.Assembly, "namespace: Shop.**");
			var type          = Block(CascadeLevel.Type,     "attribute: any");
			var evaluator     = Evaluator(aspectDefault, [assembly], type);
			var explanation   = evaluator.Explain(Fact("Ping", attributes: "NoLogAttribute"), [type], []);
			var attribute     = Decider(explanation.Combinations.Single(), CascadeProperty.Attribute);

			Assert.IsTrue(explanation.Selected);
			Assert.AreSame(type, attribute.Block.Block);
			Assert.IsTrue(attribute.IsAny);
			Assert.IsTrue(attribute.Satisfied);
		}

		[TestMethod]
		public void ReferenceExpansionTest()
		{
			var assembly    = Block(CascadeLevel.Assembly, "namespace: Shop.**\n- method: *Internal*");
			var type        = Block(CascadeLevel.Type,     "method: Save* and assembly.method\nattribute: aspect.attribute");
			var evaluator   = Evaluator(Block(CascadeLevel.Aspect, null), [assembly], type);
			var explanation = evaluator.Explain(Fact("SaveInternal"), [type], []);
			var combination = explanation.Combinations.Single();
			var method      = Decider(combination, CascadeProperty.Method);
			var reference   = method.References.Single();

			Assert.IsFalse(explanation.Selected);
			Assert.IsFalse(method.Satisfied);
			Assert.AreEqual("assembly.method", reference.Text);
			Assert.AreEqual(CascadeLevel.Assembly, reference.Level);
			Assert.IsFalse(reference.Value);
			Assert.AreSame(assembly, reference.Declarations.Single().Block.Block);
			Assert.AreEqual("- method: *Internal*", reference.Declarations.Single().Text);
			Assert.IsFalse(reference.Declarations.Single().Satisfied);

			// Nothing at or above the aspect level declares attribute: no restriction.
			var attribute = Decider(combination, CascadeProperty.Attribute).References.Single();

			Assert.IsTrue(attribute.Value);
			Assert.AreEqual(0, attribute.Declarations.Length);
			StringAssert.Contains(explanation.ToTable(), "aspect.attribute = true, no restriction");

			// A reference inside the expansion is expanded on its own cut.
			var nested = Evaluator(
				Block(CascadeLevel.Aspect,   "- attribute: NoLog"),
				[Block(CascadeLevel.Assembly, "attribute: Audited and aspect.attribute")]);
			var inner  = Decider(
				nested.Explain(Fact("A", attributes: ["AuditedAttribute", "NoLogAttribute"]), [], [Block(CascadeLevel.Method, "attribute: assembly.attribute")]).Combinations.Single(),
				CascadeProperty.Attribute).References.Single().Declarations.Single().References.Single();

			Assert.AreEqual("aspect.attribute", inner.Text);
			Assert.IsFalse(inner.Value);
			Assert.AreEqual("- attribute: NoLog", inner.Declarations.Single().Text);
		}

		[TestMethod]
		public void AuthorDefaultIsWeakestTest()
		{
			var aspectDefault = Block(CascadeLevel.Aspect,   "- attribute: NoLog", "aspect");
			var assembly      = Block(CascadeLevel.Assembly, "namespace: Shop.**");
			var audited       = Block(CascadeLevel.Type,     "attribute: Audited");
			var evaluator     = Evaluator(aspectDefault, [assembly], audited);

			var kept = Decider(evaluator.Explain(Fact("Ping", attributes: "NoLogAttribute"), [], []).Combinations.Single(), CascadeProperty.Attribute);

			Assert.AreSame(aspectDefault, kept.Block.Block);
			Assert.AreEqual(CascadeLevel.Aspect, kept.Block.Level);
			Assert.IsFalse(kept.Satisfied);

			var replaced = evaluator.Explain(Fact("Ping", attributes: ["AuditedAttribute", "NoLogAttribute"]), [audited], []);

			Assert.IsTrue(replaced.Selected);
			Assert.AreSame(audited, Decider(replaced.Combinations.Single(), CascadeProperty.Attribute).Block.Block);
			Assert.IsFalse(replaced.Combinations[0].Declarations.Any(d => d.Block.Block == aspectDefault));
		}

		[TestMethod]
		public void LibraryBlockTest()
		{
			var library   = Block(CascadeLevel.Assembly, "static", "assembly:lib", ("Level", "Info"));
			var method    = Block(CascadeLevel.Method,   null,     "method:lib");
			var consumer  = Block(CascadeLevel.Assembly, "origin: any\npublic", "assembly");
			var evaluator = Evaluator(Block(CascadeLevel.Aspect, null), [consumer], library, method);
			var fact      = Fact("Write", project: false);

			fact.Static = true;

			var own = evaluator.ExplainLibraryMethod(fact.AsProject(), [library], [], [method], fact);

			Assert.IsTrue(own.Selected);
			Assert.AreEqual(CascadeFamily.Library, own.Family);
			Assert.IsNull(own.Library);
			CollectionAssert.AreEqual(new[] { "method:lib", "assembly:lib", "Aspect" }, own.Combinations.Single().Blocks.Select(b => b.Label).ToList());
			Assert.AreSame(library, own.Settings["Level"].Block);
			StringAssert.StartsWith(own.ToTable(), "family: library; selected: yes\n");

			// Not selected by its own blocks, the method is decided by the compilation's assembly blocks.
			fact.Static = false;

			var fallback = evaluator.ExplainLibraryMethod(fact.AsProject(), [library], [], [method], fact);

			Assert.IsTrue(fallback.Selected);
			Assert.AreEqual(CascadeFamily.Consumer, fallback.Family);
			Assert.IsNotNull(fallback.Library);
			Assert.IsFalse(fallback.Library!.Selected);
			Assert.IsFalse(Decider(fallback.Library.Combinations.Single(), CascadeProperty.Static).Satisfied);
			Assert.AreSame(consumer, fallback.Combinations.Single().Blocks[0].Block);
			StringAssert.StartsWith(fallback.ToTable(), "library blocks:\nfamily: library; selected: no\n");
		}

		[TestMethod]
		public void SettingInheritedFromTheAssemblyTest()
		{
			var a           = Block(CascadeLevel.Assembly, "public",   "assembly#0", ("Level", "Debug"));
			var b           = Block(CascadeLevel.Assembly, "internal", "assembly#1");
			var type        = Block(CascadeLevel.Type,     "accessibility: any", "type", ("Format", "Long"));
			var evaluator   = Evaluator(Block(CascadeLevel.Aspect, null), [a, b], type);
			var explanation = evaluator.Explain(Fact("Load"), [type], []);

			Assert.AreEqual(2, explanation.Combinations.Length);

			var first  = explanation.Combinations[0].Settings.ToDictionary(s => s.Name);
			var second = explanation.Combinations[1].Settings.ToDictionary(s => s.Name);

			Assert.AreSame(a,    first["Level"].Block!.Block);
			Assert.AreEqual("Debug", first["Level"].Value);
			Assert.AreSame(type, first["Format"].Block!.Block);
			Assert.IsNull(second["Level"].Block, "assembly#1 writes no Level: the aspect's default");

			// The aspect's default is no value of its own: Debug from the other chain is no conflict with it.
			Assert.AreEqual("Debug", explanation.Settings["Level"].Value);

			var table = explanation.ToTable();

			StringAssert.Contains(table, "setting Level = \"Debug\" from assembly#0");
			StringAssert.Contains(table, "setting Level = the aspect's default");
			StringAssert.Contains(table, "setting Format = \"Long\" from type");
		}

		[TestMethod]
		public void ConflictTest()
		{
			var a           = Block(CascadeLevel.Assembly, "public",   "assembly#0", ("Level", "Debug"));
			var b           = Block(CascadeLevel.Assembly, "internal", "assembly#1", ("Level", "Info"));
			var type        = Block(CascadeLevel.Type,     "accessibility: any");
			var evaluator   = Evaluator(Block(CascadeLevel.Aspect, null), [a, b], type);
			var explanation = evaluator.Explain(Fact("Load"), [type], []);

			Assert.IsTrue(explanation.Selected);
			Assert.IsFalse(explanation.Settings.ContainsKey("Level"));
			Assert.AreEqual("Level", explanation.Conflicts.Single().Name);
			StringAssert.Contains(explanation.ToTable(), "conflict AG0235 Level: \"Debug\" from assembly#0; \"Info\" from assembly#1");
		}

		[TestMethod]
		public void ErrorsTurnTheAspectOffTest()
		{
			var type        = Block(CascadeLevel.Type, "method: Save*\nmethod: Load*");
			var evaluator   = Evaluator(Block(CascadeLevel.Aspect, null), [], type);
			var explanation = evaluator.Explain(Fact("SaveA"), [type], []);

			Assert.IsFalse(explanation.Selected);
			Assert.AreEqual(0, explanation.Combinations.Length);
			Assert.AreNotEqual(0, explanation.Diagnostics.Length);
			StringAssert.Contains(explanation.ToTable(), "the aspect is off");

			var uncovered = Evaluator(Block(CascadeLevel.Aspect, null), []).Explain(Fact("SaveA"), [], []);

			Assert.AreEqual(0, uncovered.Combinations.Length);
			StringAssert.Contains(uncovered.ToTable(), "no assembly, type or method block covers the method");
		}

		[TestMethod]
		public void TableIsAsciiStableAndBoundedTest()
		{
			var assemblies = Enumerable.Range(0, 20).Select(i => Block(CascadeLevel.Assembly, "namespace: Shop.**", $"assembly#{i}", ("Tag", "été"))).ToList();
			var evaluator  = Evaluator(Block(CascadeLevel.Aspect, "- attribute: NoLog"), assemblies);
			var full       = evaluator.Explain(Fact("Load"), [], []).ToTable(int.MaxValue);

			Assert.AreEqual(full, evaluator.Explain(Fact("Load"), [], []).ToTable(int.MaxValue));
			Assert.IsTrue(full.All(c => c == '\n' || c >= ' ' && c <= '~'), "plain ASCII");
			StringAssert.Contains(full, "\"\\u00E9t\\u00E9\"");

			var cut = evaluator.Explain(Fact("Load"), [], []).ToTable(500);

			Assert.IsTrue(cut.Length <= 500, $"{cut.Length} characters");
			StringAssert.EndsWith(cut, " more characters left out\n");
			StringAssert.StartsWith(full, cut.Substring(0, cut.LastIndexOf("...", StringComparison.Ordinal)));
			Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => evaluator.Explain(Fact("Load"), [], []).ToTable(10));
		}

		[TestMethod]
		public void ExplainingLeavesDecideAsItWasTest()
		{
			var assembly  = Block(CascadeLevel.Assembly, "namespace: Shop.**", "assembly", ("Level", "Debug"));
			var type      = Block(CascadeLevel.Type,     "public and aspect.attribute");
			var method    = Block(CascadeLevel.Method,   "method: assembly.method");
			var evaluator = Evaluator(Block(CascadeLevel.Aspect, "- attribute: NoLog"), [assembly], type, method);
			var context   = evaluator.Prepare([type]);
			var fact      = Fact("Load");

			evaluator.Decide(fact, context, [method]);

			var before = Allocated();

			evaluator.Explain(fact, context, [method]);

			Assert.AreEqual(before, Allocated(), "Decide allocates as much after an explanation as before it");

			long Allocated()
			{
				var bytes = GC.GetAllocatedBytesForCurrentThread();

				evaluator.Decide(fact, context, [method]);

				return GC.GetAllocatedBytesForCurrentThread() - bytes;
			}
		}

		// The case table, explained: the explanation agrees with the decision on every method of every case.

		static string CasesPath
		{
			get
			{
				for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
					if (File.Exists(Path.Combine(dir.FullName, "AspectGenerator.slnx")))
						return Path.Combine(dir.FullName, "UnitTests", "Tests", "Cascade", "CascadeCases.json");

				throw new InvalidOperationException("AspectGenerator.slnx was not found above " + AppContext.BaseDirectory);
			}
		}

		[TestMethod]
		public void ExplanationAgreesWithTheDecisionOnEveryCaseTest()
		{
			var json    = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
			var cases   = JsonNode.Parse(File.ReadAllText(CasesPath))!["cases"]!.AsArray().Select(c => c!.AsObject()).ToList();
			var methods = 0;

			Assert.AreEqual(68, cases.Count);

			foreach (var c in cases)
			{
				foreach (var (reach, policy) in new[]
				{
					(AssemblyFilterReach.ProjectUnlessOriginOrPlace, UnreadableLibraryFilter.TurnAspectOff),
					(AssemblyFilterReach.ProjectUnlessAnyInclude,    UnreadableLibraryFilter.TurnAspectOff),
					(AssemblyFilterReach.ProjectUnlessOriginOrPlace, UnreadableLibraryFilter.Ignored),
				})
				{
					methods += Agree(c["id"]!.GetValue<string>() + $" ({reach}, {policy})", c["input"]!.AsObject(), reach, policy, json);
				}
			}

			Assert.IsTrue(methods > 68 * 3, $"{methods} methods");
		}

		static int Agree(string name, JsonObject input, AssemblyFilterReach reach, UnreadableLibraryFilter policy, JsonSerializerOptions json)
		{
			var allBlocks = new List<CascadeBlock<CaseFact>>();

			CascadeBlock<CaseFact> Read(CascadeLevel level, JsonNode? node, string label)
			{
				var (lines, errors) = CascadeTextReader.Read(node?["filter"]?.GetValue<string>());
				var settings        = node?["settings"]?.AsObject().ToDictionary(p => p.Key, p => (object?)p.Value!.GetValue<string>());
				var block           = CascadeBlock<CaseFact>.Create(level, lines, settings, label, readDiagnostics: errors, assemblyReach: reach);

				allBlocks.Add(block);

				return block;
			}

			(List<CascadeBlock<CaseFact>> Assembly, List<(string Type, CascadeBlock<CaseFact> Block)> Types, List<(string Method, CascadeBlock<CaseFact> Block)> Methods)
				Family(JsonNode? source, string suffix)
			{
				var assembly = (source?["assembly"]?.AsArray() ?? []).Select((b, i) => Read(CascadeLevel.Assembly, b, $"assembly#{i}{suffix}")).ToList();
				var types    = (source?["types"]?.AsArray() ?? []).Select((b, i) => (b!["type"]!.GetValue<string>(), Read(CascadeLevel.Type, b, $"type#{i}{suffix}"))).ToList();
				var methods  = (source?["methods"]?.AsArray() ?? []).Select((b, i) => (b!["method"]!.GetValue<string>(), Read(CascadeLevel.Method, b, $"method#{i}{suffix}"))).ToList();

				return (assembly, types, methods);
			}

			var aspectDefault = Read(CascadeLevel.Aspect, input["default"] is JsonValue d ? new JsonObject { ["filter"] = d.GetValue<string>() } : null, "aspect");
			var consumer      = Family(input, "");
			var consumerAll   = allBlocks.ToList();
			var library       = input["library"] is JsonObject lib ? Family(lib, ":lib") : default;
			var evaluator     = new CascadeEvaluator<CaseFact>(
				aspectDefault,
				consumer.Assembly,
				consumerAll.Where(b => b != aspectDefault && !consumer.Assembly.Contains(b)),
				f => f.Project,
				allBlocks.Except(consumerAll),
				policy);

			var count = 0;

			foreach (var fact in input["facts"]!.AsArray().Select(f => f.Deserialize<CaseFact>(json)!))
			{
				CascadeDecision    decision;
				CascadeExplanation explanation;

				if (library.Methods is { } libraryMethods && libraryMethods.Any(m => m.Method == fact.Id))
				{
					var types   = library.Types.Where(t => t.Type == fact.Type).Select(t => t.Block).ToList();
					var methods = libraryMethods.Where(m => m.Method == fact.Id).Select(m => m.Block).ToList();

					decision    = evaluator.DecideLibraryMethod(fact.AsProject(), library.Assembly, types, methods, fact);
					explanation = evaluator.ExplainLibraryMethod(fact.AsProject(), library.Assembly, types, methods, fact);
				}
				else
				{
					var context = evaluator.Prepare([..consumer.Types.Where(t => t.Type == fact.Type).Select(t => t.Block)]);
					var methods = consumer.Methods.Where(m => m.Method == fact.Id).Select(m => m.Block).ToList();

					decision    = evaluator.Decide(fact, context, methods);
					explanation = evaluator.Explain(fact, context, methods);
				}

				var at = name + ": " + fact.Id;

				Assert.AreEqual(decision.Selected, explanation.Selected, at + ": selected");
				Assert.AreEqual(decision.Family,   explanation.Family,   at + ": family");
				Assert.AreEqual(decision.Selected, explanation.Combinations.Any(x => x.Selected), at + ": a combination selects");
				CollectionAssert.AreEqual(Settings(decision.Settings), Settings(explanation.Settings), at + ": settings");
				CollectionAssert.AreEqual(
					decision.Conflicts.Select(Conflict).ToList(),
					explanation.Conflicts.Select(Conflict).ToList(),
					at + ": conflicts");

				var table = explanation.ToTable(int.MaxValue);

				Assert.IsTrue(table.Length > 0 && table.All(ch => ch == '\n' || ch >= ' ' && ch <= '~'), at + ": table");

				count++;
			}

			return count;

			static List<string> Settings(IReadOnlyDictionary<string,CascadeSetting> settings)
			{
				return [..settings.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Value.Value} from {s.Value.Block}")];
			}

			static string Conflict(CascadeSettingConflict conflict)
			{
				return conflict.Name + ":" + string.Join(";", conflict.Values.Select(v => v.Value + " from " + string.Join(",", v.Blocks)));
			}
		}
	}
}
