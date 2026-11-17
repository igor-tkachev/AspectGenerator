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
	/// <summary>
	/// Runs the cascade over the cases of <c>CascadeCases.json</c>: the blocks of one aspect (its default, assembly, type
	/// and method blocks, and the blocks of a library that placed it), the facts of the methods, and per method whether it
	/// is selected and with which settings, and which blocks have errors. A case with a filter error selects nothing.
	/// </summary>
	[TestClass]
	public class CascadeCaseTests
	{
		static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

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

		static readonly Lazy<List<JsonObject>> _cases = new(() =>
			JsonNode.Parse(File.ReadAllText(CasesPath))!["cases"]!.AsArray().Select(c => c!.AsObject()).ToList());

		/// <summary>
		/// Every case under both reaches of an assembly block and both policies for a library filter with an error. The
		/// table's expectations are for the defaults; a case whose result differs under another choice carries it as an
		/// alternative named after that choice. Only one choice at a time leaves its default.
		/// </summary>
		public static IEnumerable<object[]> Cases => _cases.Value.SelectMany(c => new[]
		{
			new object[] { c["id"]!.GetValue<string>(), nameof(AssemblyFilterReach.ProjectUnlessOriginOrPlace), nameof(UnreadableLibraryFilter.TurnAspectOff) },
			new object[] { c["id"]!.GetValue<string>(), nameof(AssemblyFilterReach.ProjectUnlessAnyInclude),    nameof(UnreadableLibraryFilter.TurnAspectOff) },
			new object[] { c["id"]!.GetValue<string>(), nameof(AssemblyFilterReach.ProjectUnlessOriginOrPlace), nameof(UnreadableLibraryFilter.Ignored) },
		});

		static readonly Dictionary<AssemblyFilterReach,string> _alternativeNames = new()
		{
			[AssemblyFilterReach.ProjectUnlessAnyInclude] = "AssemblyIncludeLiftsProject",
		};

		static readonly Dictionary<UnreadableLibraryFilter,string> _policyAlternativeNames = new()
		{
			[UnreadableLibraryFilter.Ignored] = "LibraryFilterWithErrorIgnored",
		};

		static JsonObject Case(string id)
		{
			return _cases.Value.Single(c => c["id"]!.GetValue<string>() == id);
		}

		[TestMethod]
		public void CaseTableIsComplete()
		{
			var ids = _cases.Value.Select(c => c["id"]!.GetValue<string>()).ToList();

			Assert.AreEqual(68, ids.Count, "The number of cascade cases changed; update this count with the table.");
			Assert.AreEqual(ids.Count, ids.Distinct().Count(), "Case ids are not unique.");

			var alternatives = _cases.Value.SelectMany(c => c["alternatives"]?.AsObject().Select(a => a.Key) ?? []).Distinct().ToList();

			CollectionAssert.IsSubsetOf(alternatives, _alternativeNames.Values.Concat(_policyAlternativeNames.Values).ToList(), "An alternative names no choice the tests know.");
			Assert.AreNotEqual(0, alternatives.Count, "No case has an alternative.");
		}

		[TestMethod]
		[DynamicData(nameof(Cases))]
		public void CascadeCase(string id, string reachName, string policyName)
		{
			var reach    = Enum.Parse<AssemblyFilterReach>(reachName);
			var policy   = Enum.Parse<UnreadableLibraryFilter>(policyName);
			var c        = Case(id);
			var actual   = Run(c["input"]!.AsObject(), reach, policy);
			var expected = c["expected"]!.AsObject();

			if (_alternativeNames.TryGetValue(reach, out var name) && c["alternatives"]?[name] is JsonObject alternative)
				expected = alternative;

			if (_policyAlternativeNames.TryGetValue(policy, out name) && c["alternatives"]?[name] is JsonObject policyAlternative)
				expected = policyAlternative;

			Compare($"{id} ({reach}, {policy}): {c["description"]}", expected, actual);
		}

		sealed record Result(
			Dictionary<string,bool>                      Selected,
			Dictionary<string,Dictionary<string,string>> Settings,
			List<(string Block, string Id)>              Errors);

		/// <summary>
		/// The cases with a library filter that has an error, when such a filter turns the aspect off: nothing is selected,
		/// and the errors are one AG0236 per library block that cannot be read.
		/// </summary>
		[TestMethod]
		public void LibraryFilterWithErrorTurnsTheAspectOff()
		{
			var count = 0;

			foreach (var c in _cases.Value)
			{
				var input = c["input"]!.AsObject();

				if (input["library"] is null)
					continue;

				var ignored = Run(input, AssemblyFilterReach.ProjectUnlessOriginOrPlace, UnreadableLibraryFilter.Ignored);
				var off     = Run(input, AssemblyFilterReach.ProjectUnlessOriginOrPlace, UnreadableLibraryFilter.TurnAspectOff);
				var dropped = ignored.Errors.Where(e => e.Id == CascadeDiagnosticIds.UnreadableLibrary).Select(e => e.Block).ToList();

				if (dropped.Count == 0)
				{
					// Without a library filter error the two policies agree.
					Assert.AreEqual(Describe(ignored), Describe(off), c["id"]!.GetValue<string>());
					continue;
				}

				Assert.IsTrue(off.Selected.Values.All(v => !v), c["id"] + ": nothing is selected");
				Assert.AreEqual(0, off.Settings.Count, c["id"] + ": settings");
				CollectionAssert.AreEquivalent(dropped, off.Errors.Select(e => e.Block).Distinct().ToList(), c["id"] + ": error blocks");
				Assert.IsTrue(off.Errors.All(e => e.Id == CascadeDiagnosticIds.UnreadableLibrary), c["id"] + ": the errors are AG0236");

				count++;
			}

			Assert.AreNotEqual(0, count, "No case has a library filter with an error.");

			static string Describe(Result r)
			{
				return string.Join(";", r.Selected.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) + "|" +
					string.Join(";", r.Settings.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(m => m.Value.Select(v => m.Key + "." + v.Key + "=" + v.Value))) + "|" +
					string.Join(";", r.Errors.Select(e => e.Block + " " + e.Id).OrderBy(e => e, StringComparer.Ordinal));
			}
		}

		static Result Run(JsonObject input, AssemblyFilterReach reach, UnreadableLibraryFilter libraryFilterWithError = CascadeDefaults.LibraryFilterWithError)
		{
			var allBlocks = new List<CascadeBlock<CaseFact>>();
			var labels    = new Dictionary<CascadeBlock<CaseFact>,string>();

			CascadeBlock<CaseFact> Block(CascadeLevel level, JsonNode? node, string label)
			{
				var (lines, errors) = CascadeTextReader.Read(node?["filter"]?.GetValue<string>());
				var settings        = node?["settings"]?.AsObject().ToDictionary(p => p.Key, p => (object?)p.Value!.GetValue<string>());
				var block           = CascadeBlock<CaseFact>.Create(level, lines, settings, label, readDiagnostics: errors, assemblyReach: reach);

				allBlocks.Add(block);
				labels[block] = label;

				return block;
			}

			(List<CascadeBlock<CaseFact>> Assembly, List<(string Type, CascadeBlock<CaseFact> Block)> Types, List<(string Method, CascadeBlock<CaseFact> Block)> Methods)
				Family(JsonNode? source, string suffix)
			{
				var assembly = (source?["assembly"]?.AsArray() ?? []).Select((b, i) => Block(CascadeLevel.Assembly, b, $"assembly#{i}{suffix}")).ToList();
				var types    = (source?["types"]?.AsArray() ?? []).Select((b, i) =>
				{
					var type = b!["type"]!.GetValue<string>();

					return (type, Block(CascadeLevel.Type, b, $"type:{type}#{i}{suffix}"));
				}).ToList();
				var methods  = (source?["methods"]?.AsArray() ?? []).Select((b, i) =>
				{
					var method = b!["method"]!.GetValue<string>();

					return (method, Block(CascadeLevel.Method, b, $"method:{method}#{i}{suffix}"));
				}).ToList();

				return (assembly, types, methods);
			}

			var aspectDefault = Block(CascadeLevel.Aspect, input["default"] is JsonValue d ? new JsonObject { ["filter"] = d.GetValue<string>() } : null, "aspect");
			var consumer      = Family(input, "");
			var consumerAll   = allBlocks.ToList();
			var library       = input["library"] is JsonObject lib ? Family(lib, ":lib") : default;
			var evaluator     = new CascadeEvaluator<CaseFact>(
				aspectDefault,
				consumer.Assembly,
				consumerAll.Where(b => b != aspectDefault && !consumer.Assembly.Contains(b)),
				f => f.Project,
				allBlocks.Except(consumerAll),
				libraryFilterWithError);

			var result = new Result([], [], [..evaluator.Diagnostics.Concat(evaluator.Warnings).Select(e => (labels[(CascadeBlock<CaseFact>)e.Block!], e.Id))]);
			var facts  = input["facts"]!.AsArray().Select(f => f.Deserialize<CaseFact>(_json)!).ToList();

			foreach (var fact in facts)
			{
				CascadeDecision decision;

				if (library.Methods is { } libraryMethods && libraryMethods.Any(m => m.Method == fact.Id))
				{
					// In the library's own filters `project` means the library's assembly, as in the library's own build.
					decision = evaluator.DecideLibraryMethod(
						fact.AsProject(),
						library.Assembly,
						[..library.Types.Where(t => t.Type == fact.Type).Select(t => t.Block)],
						[..libraryMethods.Where(m => m.Method == fact.Id).Select(m => m.Block)],
						fact);
				}
				else
				{
					decision = evaluator.Decide(
						fact,
						[..consumer.Types.Where(t => t.Type == fact.Type).Select(t => t.Block)],
						[..consumer.Methods.Where(m => m.Method == fact.Id).Select(m => m.Block)]);
				}

				result.Selected[fact.Id] = decision.Selected;

				foreach (var _ in decision.Conflicts)
					result.Errors.Add(("selection:" + fact.Id, CascadeDiagnosticIds.SettingsConflict));

				if (decision.Selected && !decision.Settings.IsEmpty)
					result.Settings[fact.Id] = decision.Settings.ToDictionary(s => s.Key, s => (string)s.Value.Value!);
			}

			return result;
		}

		static IEnumerable<string> Selected(JsonObject expected)
		{
			return expected["selected"]!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value!.GetValue<bool>());
		}

		static void Compare(string name, JsonObject expected, Result actual)
		{
			CollectionAssert.AreEqual(
				Selected(expected).ToList(),
				actual.Selected.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value).ToList(),
				name + ": selection");

			CollectionAssert.AreEqual(
				expected["settings"]!.AsObject()
					.SelectMany(m => m.Value!.AsObject().Select(s => $"{m.Key}.{s.Key}={s.Value!.GetValue<string>()}"))
					.OrderBy(s => s, StringComparer.Ordinal).ToList(),
				actual.Settings
					.SelectMany(m => m.Value.Select(s => $"{m.Key}.{s.Key}={s.Value}"))
					.OrderBy(s => s, StringComparer.Ordinal).ToList(),
				name + ": settings");

			// The order of the diagnostics is not part of the cascade; their blocks and ids are.
			CollectionAssert.AreEqual(
				expected["errors"]!.AsArray().Select(e => $"{e!["block"]!.GetValue<string>()} {e["id"]!.GetValue<string>()}").OrderBy(s => s, StringComparer.Ordinal).ToList(),
				actual.Errors.Select(e => $"{e.Block} {e.Id}").OrderBy(s => s, StringComparer.Ordinal).ToList(),
				name + ": errors");
		}
	}
}
