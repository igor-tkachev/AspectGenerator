using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// <c>AG0210</c>, a valid rule that can match no method (an accessibility mask no method has), and <c>AG0211</c>, a rule
	/// that names white space where no C# name has it (a likely typo; F# and IL names can hold it, so it says nothing about
	/// matching). <c>AG0210</c> is measured: every rule it names matches none of the targets of the golden corpus nor of the
	/// methods with unusual names added here. Both are silent where a filter may hold white space, and in the curated set
	/// they are reported only for the rules listed below.
	/// </summary>
	[TestClass]
	public class TargetFilterRuleMatchesNothingTests
	{
		public TestContext TestContext { get; set; } = null!;

		const string MatchesNothing = AspectDiagnosticID.AspectFilterRuleMatchesNothing;
		const string WhiteSpace     = AspectDiagnosticID.AspectFilterNameWithWhiteSpace;

		/// <summary>The rules of the curated set <c>AG0210</c> is reported for, written to pin contradictory accessibility.</summary>
		static readonly string[] CuratedMatchesNothing =
		[
			"public private",
			"public protected **.*",
		];

		/// <summary>
		/// The rules of the curated set <c>AG0211</c> is reported for. The hand cases were written to pin what the parser does
		/// with such a rule, and the documentation lines are not filters at all (canonical signatures, the grammar of the
		/// language, prose); they are in the curated set because every string of the documentation is.
		/// </summary>
		static readonly string[] CuratedWhiteSpace =
		[
			// Written (here and in the design-time test) with a misspelled or capitalized modifier word that becomes part of
			// the name, a second operator read as part of a method name, a word key with a space.
			"publik Save",
			"publik Invoke",
			"PUBLIC Save",
			"Public Static",
			"- & method: Save",
			"- | method: Save",
			"a b: c",

			// Documentation: canonical signatures as contains: and regex: see them, which are not method patterns.
			"public System.String MyApp.Services.UserService.GetName(System.Int32)",
			"public static System.Void MyApp.Services.Cache.Clear()",
			"public virtual System.Threading.Tasks.Task<System.String> MyApp.Services.UserService.GetNameAsync(System.Int32,System.Threading.CancellationToken)",
			"public System.Boolean MyApp.Services.UserService.TryGet(System.String,out System.Int32)",
			"public System.Void MyApp.Services.UserService.Update(ref MyApp.Models.User)",
			"public System.Int32 MyApp.Services.UserService.Peek(ref readonly System.Int32)",
			"public System.Int32[,] MyApp.Services.UserService.Matrix(System.Int32[][,])",
			"public MyApp.Models.User MyApp.Data.Repository<TEntity>.Get<TKey>(TKey)",
			"public System.Void System.Collections.Generic.List<System.Int32>.Add(System.Int32)",

			// Documentation: the grammar of the language and prose around it.
			"EffectiveTargetFilter = DefaultTargetFilter + TargetFilter",
			"unprefixed  native AspectGenerator target pattern syntax",
			"native AspectGenerator target pattern syntax",
			"[-] [pattern:|regex:|contains:] rule-body",
			"method matches Save*",
			"AND method matches (*Async OR Update*)",
		];

		[TestMethod]
		public void ReportedRuleMatchesNoTargetOfTheCorpusTest()
		{
			var curated = TargetFilterCorpus.Curated().Select(static i => i.Text).ToArray();
			var sets    = new (string Name, IEnumerable<string> Inputs)[]
			{
				("curated",   curated),
				("mutations", TargetFilterCorpus.Mutations(curated, TargetFilterCorpus.MutationsPerInput, TargetFilterCorpus.MutationSeed)),
				("generated", TargetFilterCorpus.Generate(TargetFilterCorpus.GeneratedCount)),
				("spaced",    TargetFilterSpacedInputs.Generate()),
				("names with white space and wildcards", NamesWithWhiteSpace()),
			};

			var targets  = TargetFilterTargets.Entries.Select(static e => e.Target).Concat(ExtraTargets.Value).ToArray();
			var failures = new List<string>();

			foreach (var (name, inputs) in sets)
			{
				var inputsReported = 0;
				var matchesNothing = 0;
				var whiteSpace     = 0;
				var total          = 0;

				foreach (var input in inputs)
				{
					total++;

					var reported = new List<AspectFilters.TargetFilterDiagnostic>();
					var set      = AspectFilters.GetFilters(input, d =>
					{
						if (d.Id == MatchesNothing)
							reported.Add(d);
						else if (d.Id == WhiteSpace)
							whiteSpace++;
					});

					if (reported.Count == 0)
						continue;

					inputsReported++;

					foreach (var diagnostic in reported)
					{
						matchesNothing++;

						Assert.IsTrue(diagnostic.RuleIndex >= 0 && diagnostic.RuleIndex < set.Rules.Count, $"AG0210 for '{TargetFilterProbe.Escape(input)}' names no compiled rule.");

						var rule    = set.Rules[diagnostic.RuleIndex];
						var matched = targets.Where(t => rule.IsMatch(t)).Select(static t => t.Signature).FirstOrDefault();

						if (matched is not null && failures.Count < 20)
							failures.Add($"{name}: '{TargetFilterProbe.Escape(input)}', rule '{rule.RuleText}' matches {matched}");
					}
				}

				TestContext.WriteLine($"{name}: {total} inputs, {inputsReported} with AG0210 ({matchesNothing} rules); AG0211 for {whiteSpace} rules");
			}

			Assert.AreEqual(0, failures.Count, "AG0210 named rules that match a target:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
		}

		/// <summary>
		/// Methods whose names hold white space, which the golden targets do not have: a function pointer type argument
		/// formats as <c>delegate* unmanaged&lt;System.Void&gt;</c>, and a referenced assembly written in F# or IL can name a
		/// namespace, a type or a method with spaces (<c>Weird.Plain.My Type</c>, <c>do it</c>), even with brackets.
		/// </summary>
		static readonly Lazy<AspectFilters.MethodTarget[]> ExtraTargets = new(static () =>
		{
			const string source =
				"""
				using System.Collections.Generic;

				namespace Acme.Pointers
				{
					unsafe class Box<T>
					{
						public void Put(T value) { }
						public static void Make<U>() { }
					}

					unsafe static class Calls
					{
						static void Run()
						{
							System.Array.Empty<delegate* unmanaged<void>[]>();
							new List<delegate* unmanaged<void>[]>().Clear();
							new Box<delegate*<int, string>[]>().Put(null!);
							Box<int>.Make<delegate* managed<System.Collections.Generic.List<int>, void>[]>();
							Weird.Plain.Factory.Make().Run();
							Weird.Plain.Factory.MakeLt().Run();
							Weird.Plain.Factory.MakeGt().Run();
							Weird.Plain.Factory.MakeNs().Run();
						}
					}
				}
				""";

			var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
				"ExtraTargets",
				[Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)],
				TargetFilterTargets.Compilation.References.Append(MetadataReference.CreateFromImage(WeirdNamesAssembly())),
				new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
			var errors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();

			Assert.AreEqual(0, errors.Length, string.Join(Environment.NewLine, errors.Select(static e => e.ToString())));

			var tree  = compilation.SyntaxTrees[0];
			var model = compilation.GetSemanticModel(tree);
			var calls = tree.GetRoot().DescendantNodes()
				.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
				.Select(i => (IMethodSymbol)model.GetSymbolInfo(i).Symbol!)
				.ToList();

			// Methods called in C# of types named by other languages, and their spaced static methods.
			var weird = compilation.GetTypeByMetadataName("Weird.Plain.My Type")!;

			calls.Add(weird.GetMembers("do it").OfType<IMethodSymbol>().Single());

			return calls.Select(static m => AspectTargetFactory.Create(m)).ToArray();
		});

		/// <summary>An assembly with names C# cannot write, as F# (double backticks) or IL would write them.</summary>
		static byte[] WeirdNamesAssembly()
		{
			var assembly = new System.Reflection.Emit.PersistedAssemblyBuilder(new System.Reflection.AssemblyName("WeirdNames"), typeof(object).Assembly);
			var module   = assembly.DefineDynamicModule("WeirdNames");
			var types    = new List<System.Reflection.Emit.TypeBuilder>();

			System.Reflection.Emit.TypeBuilder MakeType(string name)
			{
				var type = module.DefineType(name, System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Class, typeof(object));

				type.DefineDefaultConstructor(System.Reflection.MethodAttributes.Public);
				type.DefineMethod("Run", System.Reflection.MethodAttributes.Public, typeof(void), Type.EmptyTypes).GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ret);
				type.DefineMethod("do it", System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, typeof(void), Type.EmptyTypes).GetILGenerator().Emit(System.Reflection.Emit.OpCodes.Ret);
				types.Add(type);

				return type;
			}

			var factory = module.DefineType("Weird.Plain.Factory", System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Class | System.Reflection.TypeAttributes.Abstract | System.Reflection.TypeAttributes.Sealed, typeof(object));

			foreach (var (maker, name) in new[] { ("Make", "Weird.Plain.My Type"), ("MakeLt", "Weird.Plain.A<B C"), ("MakeGt", "Weird.Plain.A>B C"), ("MakeNs", "Weird.My Ns.T") })
			{
				var il = factory.DefineMethod(maker, System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, MakeType(name), Type.EmptyTypes).GetILGenerator();

				il.Emit(System.Reflection.Emit.OpCodes.Ldnull);
				il.Emit(System.Reflection.Emit.OpCodes.Ret);
			}

			foreach (var type in types)
				type.CreateType();

			factory.CreateType();

			using var stream = new System.IO.MemoryStream();

			assembly.Save(stream);
			return stream.ToArray();
		}

		/// <summary>Name segments with white space and wildcards in every arrangement, for each key that reads a name.</summary>
		static IEnumerable<string> NamesWithWhiteSpace()
		{
			string[] pieces = ["*", "?", "Empty", "Put", "Clear", "Make", "delegate", "unmanaged", "<", ">", "[]", "Box", "List", "Acme", "\\<", "\\>", "My", "Type", "do", "it", "A", "B", "C", "Ns", "Weird"];
			string[] keys   = ["method: ", "type: ", "fullmethod: **.", "fulltype: **.", "namespace: ", "Acme.Pointers.Box<*>.", "Weird.Plain.", ""];
			var random      = new TargetFilterCorpus.SplitMix64(0x4147_5446_5753_0001UL);

			for (var i = 0; i < 5_000; i++)
			{
				var count   = 2 + random.Next(4);
				var segment = string.Join("", Enumerable.Range(0, count).Select(_ => pieces[random.Next(pieces.Length)] + (random.Next(3) == 0 ? " " : "")));

				yield return keys[random.Next(keys.Length)] + segment.Trim();
			}
		}

		[TestMethod]
		public void FunctionPointerTypeArgumentsAreMatchedWithoutAG0210OrAG0211Test()
		{
			var targets = ExtraTargets.Value;

			foreach (var filter in new[] { "method: * *", "type: * *", "method: Empty<delegate* unmanaged<System.Void>[]>", "type: List<delegate* unmanaged<*>[]>", "Put", "fulltype: **.Box<delegate* *>" })
			{
				var ids = new List<string>();
				var set = AspectFilters.GetFilters(filter, d => ids.Add(d.Id));

				Assert.IsTrue(targets.Any(t => set.Evaluate(t).IsMatch), $"'{filter}' matches none of {string.Join(" | ", targets.Select(static t => t.Signature))}");
				Assert.AreEqual(0, ids.Count, $"'{filter}', which matches a method: {string.Join(", ", ids)}");
			}
		}

		[TestMethod]
		public void NamesOfOtherLanguagesWithWhiteSpaceAreMatchedAndToldAboutTest()
		{
			// F# and IL can name a type, a namespace or a method with spaces, and C# calls them: such a rule matches, so it
			// is no AG0210; AG0211 tells the author that a C# name would not have the space, which is information only.
			var targets = ExtraTargets.Value;

			foreach (var filter in new[]
			{
				"type: My Type", "fulltype: Weird.Plain.My Type", "path: Weird.Plain.My Type", "fullmethod: Weird.Plain.My Type.Run", "namespace: Weird.My Ns",
				"method: do it", "Weird.Plain.My Type.Run", "public Weird.Plain.My Type.Run() : System.Void", "public static do it",
			})
			{
				var diagnostics = new List<AspectFilters.TargetFilterDiagnostic>();
				var set         = AspectFilters.GetFilters(filter, diagnostics.Add);

				Assert.IsTrue(targets.Any(t => set.Evaluate(t).IsMatch), $"'{filter}' matches none of {string.Join(" | ", targets.Select(static t => t.Signature))}");
				CollectionAssert.AreEqual(new[] { WhiteSpace }, diagnostics.Select(static d => d.Id).ToArray(), filter);
				Assert.AreEqual(Microsoft.CodeAnalysis.DiagnosticSeverity.Info, diagnostics[0].Severity, filter);
			}
		}

		[TestMethod]
		public void CuratedInputsReportOnlyTheListedRulesTest()
		{
			var reported = new HashSet<string>(StringComparer.Ordinal);

			foreach (var input in TargetFilterCorpus.Curated())
			{
				var diagnostics = new List<AspectFilters.TargetFilterDiagnostic>();
				var set         = AspectFilters.GetFilters(input.Text, diagnostics.Add);

				foreach (var diagnostic in diagnostics.Where(static d => d.Id is MatchesNothing or WhiteSpace))
					reported.Add(diagnostic.Id + " " + set.Rules[diagnostic.RuleIndex].RuleText);
			}

			var expected = CuratedMatchesNothing.Select(static r => MatchesNothing + " " + r).Concat(CuratedWhiteSpace.Select(static r => WhiteSpace + " " + r)).ToArray();

			CollectionAssert.AreEquivalent(expected, reported.ToArray(), "Curated rules with AG0210 or AG0211: " + string.Join(" | ", reported.Select(TargetFilterProbe.Escape)));
		}

		[TestMethod]
		public void WhiteSpaceThatFiltersMayHoldIsNotReportedTest()
		{
			var reported = TargetFilterSpacedInputs.Generate().Where(static text => Reports(text, MatchesNothing) || Reports(text, WhiteSpace)).Take(20).ToArray();

			Assert.AreEqual(0, reported.Length, "AG0210 or AG0211 for white space a filter may hold:" + Environment.NewLine + string.Join(Environment.NewLine, reported.Select(TargetFilterProbe.Escape)));
		}

		[TestMethod]
		public void AccessibilityAMethodCanHaveIsNotReportedTest()
		{
			foreach (var words in new[] { "public", "protected", "internal", "private", "protected internal", "internal protected", "private protected", "protected private", "public public", "internal  protected" })
				foreach (var shape in new[] { "{0}", "{0} Save", "{0} static Save(...) : *", "{0}; method: Save", "method: Save; {0}", "pattern: {0} Save" })
				{
					var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, words);

					Assert.IsFalse(Reports(text), $"AG0210 for '{text}'.");
				}
		}

		[TestMethod]
		public void AccessibilityNoMethodCanHaveIsReportedTest()
		{
			foreach (var words in new[] { "public private", "public internal", "public protected", "internal private", "protected internal private", "public protected internal private" })
				foreach (var shape in new[] { "{0}", "{0} Save", "{0} static Save(...) : *", "{0}; method: Save", "pattern: {0} Save", "- {0} Save" })
				{
					var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, words);

					Assert.IsTrue(Reports(text), $"No AG0210 for '{text}'.");
				}

			// The words of one rule add up across the lines of a condition group.
			Assert.IsTrue(Reports("public; method: Save\nprivate"));
			Assert.IsFalse(Reports("public; method: Save\n-private"));
		}

		[TestMethod]
		public void WhiteSpaceInANameIsReportedTest()
		{
			foreach (var text in new[]
			{
				"publik Save", "Acme.Core Service.Save", "Sa ve*", "Save\\ Item", "method: Save Item", "type: My Service", "namespace: Acme Core.**",
				"fullmethod: Acme.Core.Save Item", "path: Acme.Core.My Service", "method: Save Item | Save Items", "method: Save* & Sa ve",
			})
			{
				Assert.IsTrue(Reports(text, WhiteSpace), $"No AG0211 for '{text}'.");
				Assert.IsFalse(Reports(text, MatchesNothing), $"AG0210 for '{text}', which another language's name can match.");
			}

			foreach (var text in new[]
			{
				"method: Save Item | Save", "Load<string, int>", "* *", "method: * *", "type: * *", "Sa*ve Item", "method: Empty<delegate* unmanaged<System.Void>[]>",
				"method: *unmanaged<System.Void>[]>", "type: List<delegate* unmanaged<System.Void>[]>", "method: Sa?ve Item", @"method: Box\<x y\>", "Acme.Core.Pair< *, * >.Get", "type: Dictionary<string, int>", "public  static  Save",
				"Save(ref readonly int, params string [])", "Save(int ?) : int ?", "returns: string []", "signature: public *", "Save( int , int )",
				"contains: Save Item", "regex: Save Item",
			})
				Assert.IsFalse(Reports(text, WhiteSpace) || Reports(text, MatchesNothing), $"AG0210 or AG0211 for '{text}'.");
		}

		[TestMethod]
		public void InvalidRuleIsNotReportedAsMatchingNothingTest()
		{
			Assert.IsFalse(Reports("public private Save(..., ...)"));
			Assert.IsFalse(Reports("publik Save.**", WhiteSpace));
		}

		[TestMethod]
		public void RuleMatchingNothingIsAWarningAndAWhiteSpaceNameInformationTest()
		{
			var diagnostics = new List<AspectFilters.TargetFilterDiagnostic>();
			var set         = AspectFilters.GetFilters("method: Save\npublik Load\npublic private\npublic private publik Save", diagnostics.Add);

			CollectionAssert.AreEqual(new[] { WhiteSpace, MatchesNothing, MatchesNothing, WhiteSpace }, diagnostics.Select(static d => d.Id).ToArray());
			CollectionAssert.AreEqual(
				new[] { Microsoft.CodeAnalysis.DiagnosticSeverity.Info, Microsoft.CodeAnalysis.DiagnosticSeverity.Warning, Microsoft.CodeAnalysis.DiagnosticSeverity.Warning, Microsoft.CodeAnalysis.DiagnosticSeverity.Info },
				diagnostics.Select(static d => d.Severity).ToArray());
			CollectionAssert.AreEqual(new[] { 1, 2, 3, 3 }, diagnostics.Select(static d => d.RuleIndex).ToArray());
			Assert.AreEqual("publik Load",    set.Rules[1].RuleText);
			Assert.AreEqual("public private", set.Rules[2].RuleText);
			Assert.AreEqual(
				"Target filter rule 'publik Load' names 'publik Load', with white space outside '<...>': a C# name never has white space there, so the rule probably holds a typo, such as a misspelled modifier word",
				diagnostics[0].Message);
			Assert.AreEqual(
				"Target filter rule 'public private' can match no method: no method is public and private at once; a method must have every accessibility word of a rule",
				diagnostics[1].Message);
		}

		static bool Reports(string text, string id = MatchesNothing)
		{
			var reported = false;

			AspectFilters.GetFilters(text, d => reported |= d.Id == id);

			return reported;
		}
	}
}
