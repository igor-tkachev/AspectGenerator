using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

using AspectGenerator.Cascade;
using AspectGenerator.TargetFilterBinding;
using AspectGenerator.TargetFilterFacts;
using AspectGenerator.Tests.Cascade;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilterBinding
{
	/// <summary>
	/// The cases of <c>CascadeCases.json</c> once more, this time with every filter read by the grammar of the language
	/// and every condition matched by the matchers of the language, where <see cref="CascadeCaseTests"/> reads the
	/// filters with a reader of its own. The expectations are the table's, unchanged: the selection of every method,
	/// its settings, and the blocks with errors and their ids.
	/// </summary>
	[TestClass]
	public class CascadeCasesFromTextTests
	{
		static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

		static readonly Lazy<List<JsonObject>> _cases = new(() =>
		{
			for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
			{
				if (File.Exists(Path.Combine(dir.FullName, "AspectGenerator.slnx")))
				{
					var path = Path.Combine(dir.FullName, "UnitTests", "Tests", "Cascade", "CascadeCases.json");

					return JsonNode.Parse(File.ReadAllText(path))!["cases"]!.AsArray().Select(static c => c!.AsObject()).ToList();
				}
			}

			throw new InvalidOperationException("AspectGenerator.slnx was not found above " + AppContext.BaseDirectory);
		});

		/// <summary>Every case under the choices <see cref="CascadeCaseTests"/> runs it under.</summary>
		public static IEnumerable<object[]> Cases => _cases.Value.SelectMany(static c => new[]
		{
			new object[] { c["id"]!.GetValue<string>(), nameof(AssemblyFilterReach.ProjectUnlessOriginOrPlace), nameof(UnreadableLibraryFilter.TurnAspectOff) },
			new object[] { c["id"]!.GetValue<string>(), nameof(AssemblyFilterReach.ProjectUnlessAnyInclude),    nameof(UnreadableLibraryFilter.TurnAspectOff) },
			new object[] { c["id"]!.GetValue<string>(), nameof(AssemblyFilterReach.ProjectUnlessOriginOrPlace), nameof(UnreadableLibraryFilter.Ignored) },
		});

		static readonly Dictionary<AssemblyFilterReach,string> _reachAlternatives = new()
		{
			[AssemblyFilterReach.ProjectUnlessAnyInclude] = "AssemblyIncludeLiftsProject",
		};

		static readonly Dictionary<UnreadableLibraryFilter,string> _policyAlternatives = new()
		{
			[UnreadableLibraryFilter.Ignored] = "LibraryFilterWithErrorIgnored",
		};

		[TestMethod]
		public void EveryCaseOfTheTableIsRunTest()
		{
			Assert.AreEqual(66, _cases.Value.Count, "The number of cascade cases changed; update this count with the table.");
		}

		[TestMethod]
		[DynamicData(nameof(Cases))]
		public void CascadeCaseFromText(string id, string reachName, string policyName)
		{
			var reach    = Enum.Parse<AssemblyFilterReach>(reachName);
			var policy   = Enum.Parse<UnreadableLibraryFilter>(policyName);
			var c        = _cases.Value.Single(c => c["id"]!.GetValue<string>() == id);
			var expected = c["expected"]!.AsObject();

			if (_reachAlternatives.TryGetValue(reach, out var name) && c["alternatives"]?[name] is JsonObject alternative)
				expected = alternative;

			if (_policyAlternatives.TryGetValue(policy, out name) && c["alternatives"]?[name] is JsonObject policyAlternative)
				expected = policyAlternative;

			var (selected, settings, errors) = Run(c["input"]!.AsObject(), reach, policy);
			var title                        = $"{id} ({reach}, {policy}): {c["description"]}";

			CollectionAssert.AreEqual(
				expected["selected"]!.AsObject().OrderBy(static p => p.Key, StringComparer.Ordinal).Select(static p => p.Key + "=" + p.Value!.GetValue<bool>()).ToList(),
				selected.OrderBy(static p => p.Key, StringComparer.Ordinal).Select(static p => p.Key + "=" + p.Value).ToList(),
				title + ": selection");

			CollectionAssert.AreEqual(
				expected["settings"]!.AsObject()
					.SelectMany(static m => m.Value!.AsObject().Select(s => $"{m.Key}.{s.Key}={s.Value!.GetValue<string>()}"))
					.OrderBy(static s => s, StringComparer.Ordinal).ToList(),
				settings.OrderBy(static s => s, StringComparer.Ordinal).ToList(),
				title + ": settings");

			CollectionAssert.AreEqual(
				expected["errors"]!.AsArray().Select(static e => $"{e!["block"]!.GetValue<string>()} {e["id"]!.GetValue<string>()}").OrderBy(static s => s, StringComparer.Ordinal).ToList(),
				errors.OrderBy(static s => s, StringComparer.Ordinal).ToList(),
				title + ": errors");
		}

		/// <summary>A method of the table as facts. The records of the table say nothing a filter of the table does not read.</summary>
		static RecordFacts Facts(CaseFact fact, bool? project = null)
		{
			Assert.AreEqual(fact.Type + "." + fact.Name, fact.Id, "A method of the table is named by its type and its name.");

			var virtuality = Virtuality.None;

			foreach (var modifier in fact.Modifiers)
			{
				virtuality |= modifier switch
				{
					"virtual"  => Virtuality.Virtual,
					"override" => Virtuality.Override,
					"abstract" => Virtuality.Abstract,
					"sealed"   => Virtuality.Sealed,
					_          => Virtuality.None,
				};
			}

			return new RecordFacts(fact.Type, fact.Name, fact.Returns)
			{
				NamespaceSegments = fact.Namespace.Length == 0 ? 0 : fact.Namespace.Split('.').Length,
				Accessibility     = fact.Accessibility switch
				{
					"public"             => Accessibility.Public,
					"internal"           => Accessibility.Internal,
					"protected"          => Accessibility.Protected,
					"private"            => Accessibility.Private,
					"protected internal" => Accessibility.ProtectedOrInternal,
					"private protected"  => Accessibility.ProtectedAndInternal,
					_                    => throw new InvalidOperationException(fact.Accessibility),
				},
				Virtuality  = virtuality,
				IsStatic    = fact.Static,
				IsAsync     = fact.Async,
				IsProject   = project ?? fact.Project,
				IsExtern    = fact.Modifiers.Contains("extern"),
				IsExtension = fact.Modifiers.Contains("extension"),
				IsGenerated = fact.Modifiers.Contains("generated"),
				Attributes  = [..fact.Attributes.Select(ShapeText.ParseNamed)],
			};
		}

		static (Dictionary<string,bool> Selected, List<string> Settings, List<string> Errors) Run(JsonObject input, AssemblyFilterReach reach, UnreadableLibraryFilter policy)
		{
			var all = new List<FilterBlock>();

			FilterBlock Block(CascadeLevel level, JsonNode? node, string label)
			{
				var filter   = node?["filter"]?.GetValue<string>();
				var settings = node?["settings"]?.AsObject().ToDictionary(static p => p.Key, static p => (object?)p.Value!.GetValue<string>());
				var block    = FilterBlockBuilder.Build(level, filter is null ? [] : [filter], settings, label, assemblyReach: reach);

				// Every error is the reader's or the builder's, each with its place in the text; the cascade finds none of its own.
				Assert.IsTrue(block.Block.Diagnostics.All(static d => d.Source is FilterBlockDiagnostic), label);
				Assert.AreEqual(block.Diagnostics.Length, block.Block.Diagnostics.Length, label);

				foreach (var diagnostic in block.Diagnostics)
				{
					Assert.AreEqual(0, diagnostic.Item, label);
					Assert.IsTrue(diagnostic.Span.End <= filter!.Length, $"{label}: {diagnostic}");
				}

				all.Add(block);

				return block;
			}

			(List<FilterBlock> Assembly, List<(string Type, FilterBlock Block)> Types, List<(string Method, FilterBlock Block)> Methods) Family(JsonNode? source, string suffix)
			{
				var assembly = (source?["assembly"]?.AsArray() ?? []).Select((b, i) => Block(CascadeLevel.Assembly, b, $"assembly#{i}{suffix}")).ToList();

				var types = (source?["types"]?.AsArray() ?? []).Select((b, i) =>
				{
					var type = b!["type"]!.GetValue<string>();

					return (type, Block(CascadeLevel.Type, b, $"type:{type}#{i}{suffix}"));
				}).ToList();

				var methods = (source?["methods"]?.AsArray() ?? []).Select((b, i) =>
				{
					var method = b!["method"]!.GetValue<string>();

					return (method, Block(CascadeLevel.Method, b, $"method:{method}#{i}{suffix}"));
				}).ToList();

				return (assembly, types, methods);
			}

			var aspectDefault = Block(CascadeLevel.Aspect, input["default"] is JsonValue d ? new JsonObject { ["filter"] = d.GetValue<string>() } : null, "aspect");
			var consumer      = Family(input, "");
			var consumerAll   = all.ToList();
			var library       = input["library"] is JsonObject lib ? Family(lib, ":lib") : default;

			var evaluator = new CascadeEvaluator<FilterTarget>(
				aspectDefault.Block,
				consumer.Assembly.Select(static b => b.Block),
				consumerAll.Where(b => b != aspectDefault && !consumer.Assembly.Contains(b)).Select(static b => b.Block),
				static t => t.Facts.IsProject,
				all.Except(consumerAll).Select(static b => b.Block),
				policy);

			var selected = new Dictionary<string,bool>();
			var settings = new List<string>();
			var errors   = evaluator.Diagnostics.Concat(evaluator.Warnings).Select(static e => $"{((CascadeBlock<FilterTarget>)e.Block!).Label} {e.Id}").ToList();

			foreach (var fact in input["facts"]!.AsArray().Select(static f => f.Deserialize<CaseFact>(_json)!))
			{
				CascadeDecision Decide(Func<IMethodFacts, FilterTarget> target)
				{
					if (library.Methods is { } libraryMethods && libraryMethods.Any(m => m.Method == fact.Id))
					{
						// In the library's own filters `project` means the library's assembly, as in the library's own build.
						return evaluator.DecideLibraryMethod(
							target(Facts(fact, project: true)),
							[..library.Assembly.Select(static b => b.Block)],
							[..library.Types.Where(t => t.Type == fact.Type).Select(static t => t.Block.Block)],
							[..libraryMethods.Where(m => m.Method == fact.Id).Select(static m => m.Block.Block)],
							target(Facts(fact)));
					}

					return evaluator.Decide(
						target(Facts(fact)),
						[..consumer.Types.Where(t => t.Type == fact.Type).Select(static t => t.Block.Block)],
						[..consumer.Methods.Where(m => m.Method == fact.Id).Select(static m => m.Block.Block)]);
				}

				var decision = Decide(FilterTarget.Call);

				// No method of the table is generic: its definition decides for every call, and decides the same.
				var targets      = new List<FilterTarget>();
				var onDefinition = Decide(f =>
				{
					var target = FilterTarget.Definition(f);

					targets.Add(target);

					return target;
				});

				Assert.IsFalse(targets.Any(static t => t.DependsOnCall), fact.Id + ": the definition decides");
				Assert.AreEqual(decision.Selected, onDefinition.Selected, fact.Id + ": the definition decides the same");

				selected[fact.Id] = decision.Selected;

				foreach (var _ in decision.Conflicts)
					errors.Add($"selection:{fact.Id} {CascadeDiagnosticIds.SettingsConflict}");

				if (decision.Selected)
					settings.AddRange(decision.Settings.Select(s => $"{fact.Id}.{s.Key}={(string)s.Value.Value!}"));
			}

			return (selected, settings, errors);
		}
	}
}
