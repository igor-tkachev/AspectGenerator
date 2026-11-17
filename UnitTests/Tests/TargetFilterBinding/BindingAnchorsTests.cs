using System.Linq;

using AspectGenerator.Cascade;
using AspectGenerator.TargetFilterBinding;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilterBinding
{
	/// <summary>
	/// What a filter says of the calls that have to be bound for it: the name a call is written with, the root of the
	/// target's name, and whether the target is declared in the project.
	/// </summary>
	[TestClass]
	public class BindingAnchorsTests
	{
		static string Anchors(string filter, CascadeLevel level = CascadeLevel.Assembly)
		{
			var block = FilterBlockBuilder.Build(level, [filter]);

			Assert.AreEqual(0, block.Diagnostics.Length, $"'{filter}': {string.Join("; ", block.Diagnostics)}");

			return block.Anchors.ToString();
		}

		[TestMethod]
		public void AMethodNameWithLiteralTextAtAnEndIsAnAnchorTest()
		{
			Assert.AreEqual("name Save",                    Anchors("method: Save"));
			Assert.AreEqual("name Append*",                 Anchors("method: Append*"));
			Assert.AreEqual("name *Async",                  Anchors("method: *Async"));
			Assert.AreEqual("name Get*Async",               Anchors("method: Get*Async"));
			Assert.AreEqual("name Get*By*Async",            Anchors("method: Get*By*Async"));
			Assert.AreEqual("name *Internal*",              Anchors("method: *Internal*"));
			Assert.AreEqual("name *Get*By*",                Anchors("method: *Get*By*"));
			Assert.AreEqual("name Save",                    Anchors("method: *Repository.Save"));
			Assert.AreEqual("name Save*",                   Anchors("method: **.*Service.Save*"));
			Assert.AreEqual("name Add",                     Anchors("method: List<int>.Add"));
			Assert.AreEqual("name Get",                     Anchors("method: Get<*>"));
			Assert.AreEqual("name Main",                    Anchors("method: \"Main\""));
			Assert.AreEqual("name any",                     Anchors("method: @any"));
			Assert.AreEqual("name Get* | name Find*",       Anchors("method: {Get,Find}*"));
			Assert.AreEqual("name GetAsync | name FindAsync", Anchors("method: {Get,Find}Async"));
			Assert.AreEqual("name Get*Async | name Get*Core | name Find*Async | name Find*Core", Anchors("method: {Get,Find}*{Async,Core}"));
			Assert.AreEqual("name Save* | name Load",       Anchors("method: {Save*,Repo.Load}"));
			Assert.AreEqual("name Save*",                   Anchors("Save*"));
			Assert.AreEqual("name Save*",                   Anchors("Save*(int): Task"));

			// No literal text in the method's own name, or a name that alternatives split over segments.
			Assert.AreEqual("every call",                   Anchors("method: *"));
			Assert.AreEqual("every call",                   Anchors("method: **"));
			Assert.AreEqual("every call",                   Anchors("method: Shop.**"));
			Assert.AreEqual("every call",                   Anchors("method: Shop.*"));
			Assert.AreEqual("every call",                   Anchors("method: Repo{A.Get,Find}"));
			Assert.AreEqual("every call",                   Anchors("method: {Get,*}"));
			Assert.AreEqual("every call",                   Anchors("method: {Get*,Find}*{Async,*}"));
		}

		[TestMethod]
		public void ARootedNameIsAnAnchorTest()
		{
			Assert.AreEqual("root MyApp",                   Anchors("namespace: MyApp.**"));
			Assert.AreEqual("root MyApp",                   Anchors("namespace: MyApp"));
			Assert.AreEqual("root MyApp",                   Anchors("namespace: global::MyApp.Data.*"));
			Assert.AreEqual("root A | root B",              Anchors("namespace: {A.**,B.C}"));
			Assert.AreEqual("root MyApp",                   Anchors("type: global::MyApp.**"));
			Assert.AreEqual("root MyApp",                   Anchors("type: global::MyApp.Data.Repo<*>"));
			Assert.AreEqual("root A | root B",              Anchors("type: {global::A.X, global::B.Y}"));
			Assert.AreEqual("root MyApp",                   Anchors("method: global::MyApp.**"));
			Assert.AreEqual("name Save*, root MyApp",       Anchors("method: global::MyApp.**.Save*"));
			Assert.AreEqual("root MyApp",                   Anchors("namespace: MyApp.** and type: *Service"));

			// A root that is no literal, a type that is not written from the root, the global namespace.
			Assert.AreEqual("every call",                   Anchors("namespace: My*.**"));
			Assert.AreEqual("every call",                   Anchors("namespace: **"));
			Assert.AreEqual("every call",                   Anchors("namespace: \"\""));
			Assert.AreEqual("every call",                   Anchors("type: MyApp.Order"));
			Assert.AreEqual("every call",                   Anchors("type: *Builder"));
			Assert.AreEqual("every call",                   Anchors("type: global::MyApp.Order[]"));
			Assert.AreEqual("every call",                   Anchors("type: {global::A.X, Y}"));
		}

		[TestMethod]
		public void AnAssemblyFilterWithoutAPlaceStaysInTheProjectTest()
		{
			Assert.AreEqual("project",                      Anchors(""));
			Assert.AreEqual("project",                      Anchors("public"));
			Assert.AreEqual("project",                      Anchors("async"));
			Assert.AreEqual("project",                      Anchors("returns: Task*"));
			Assert.AreEqual("project",                      Anchors("- method: *Internal*"));
			Assert.AreEqual("project",                      Anchors("project"));
			Assert.AreEqual("project",                      Anchors("public and project"));

			// A place or a word about the origin lifts the restriction: then only an anchor limits binding.
			Assert.AreEqual("every call",                   Anchors("origin: any\npublic"));
			Assert.AreEqual("every call",                   Anchors("not project"));
			Assert.AreEqual("every call",                   Anchors("not method: Save"));
			Assert.AreEqual("every call",                   Anchors("method: any"));
			Assert.AreEqual("every call",                   Anchors("method: aspect.method"));
			Assert.AreEqual("every call",                   Anchors("type: *Service"));
			Assert.AreEqual("name Save*",                   Anchors("public and method: Save*"));
			Assert.AreEqual("name Save*, project",          Anchors("project and method: Save*"));
			Assert.AreEqual("project | name Save*",         Anchors("project or method: Save*"));
		}

		[TestMethod]
		public void TheLinesOfAFilterCombineTest()
		{
			// Every include holds of a selected method; a subtraction only takes away.
			Assert.AreEqual("name Append*",                 Anchors("type: *Builder and method: Append*"));
			Assert.AreEqual("name Append*",                 Anchors("type: *Builder\nmethod: Append*"));
			Assert.AreEqual("name Save*",                   Anchors("method: Save*\n- method: SaveInternal"));
			Assert.AreEqual("name Save*, root MyApp",       Anchors("namespace: MyApp.**\nmethod: Save*"));
			Assert.AreEqual("name Save*, name *Async",      Anchors("method: Save* and method: *Async"));
			Assert.AreEqual("name Save*, name SaveAsync",   Anchors("method: Save* and method: SaveAsync"));
			Assert.AreEqual("name Save* | name Get*",       Anchors("method: Save* or Get*"));
			Assert.AreEqual("name Save*, root A | name Save*, root B", Anchors("namespace: {A,B}.** and method: Save*"));
			Assert.AreEqual("name Save*",                   Anchors("method: Save* and (static or method: *Async)"));
			Assert.AreEqual("name Save*, name *Async | name Save*, name *Core", Anchors("method: Save* and (method: *Async or method: *Core)"));

			// An alternative without an anchor leaves every call to bind.
			Assert.AreEqual("every call",                   Anchors("method: Save* or public"));
			Assert.AreEqual("every call",                   Anchors("method: Save* or *"));
			Assert.AreEqual("project",                      Anchors("method: Save* or assembly.method", CascadeLevel.Type));

			// Conditions no method satisfies together, where that is plain: a whole name that another condition refuses, two
			// roots. Two globs are kept as they are: whether a name meets both is asked of the name.
			Assert.AreEqual("no call",                      Anchors("method: Save and method: Load"));
			Assert.AreEqual("no call",                      Anchors("method: Save and method: Get*"));
			Assert.AreEqual("name Save*, name Get*",        Anchors("method: Save* and method: Get*"));
			Assert.AreEqual("no call",                      Anchors("namespace: A.** and method: global::B.**"));
		}

		[TestMethod]
		public void TypeAndMethodFiltersSpeakOfTheProjectTest()
		{
			Assert.AreEqual("project",                      Anchors("", CascadeLevel.Type));
			Assert.AreEqual("name Save*, project",          Anchors("method: Save*", CascadeLevel.Type));
			Assert.AreEqual("project",                      Anchors("public", CascadeLevel.Method));
			Assert.AreEqual("every call",                   Anchors("", CascadeLevel.Aspect));
			Assert.AreEqual("name *Async",                  Anchors("method: *Async", CascadeLevel.Aspect));
		}

		[TestMethod]
		public void TheDefaultFilterAnchorsWhatTheBlockLeavesInForceTest()
		{
			static string Under(string aspectDefault, string assembly)
			{
				return FilterBlockBuilder.Build(CascadeLevel.Assembly, [assembly]).AnchorsUnder(FilterBlockBuilder.Build(CascadeLevel.Aspect, [aspectDefault])).ToString();
			}

			Assert.AreEqual("project",                      Under("- attribute: NoLog", "public"));
			Assert.AreEqual("name *Async, project",         Under("method: *Async", "public"));
			Assert.AreEqual("name Save*",                   Under("method: *Async", "method: Save*"));
			Assert.AreEqual("every call",                   Under("method: *Async", "method: any"));
			Assert.AreEqual("name Save*, root MyApp",       Under("namespace: MyApp.**", "method: Save*"));

			// A compound line of the default is out of force once the block declares one of its properties.
			Assert.AreEqual("name Save*",                   Under("method: *Async or public", "method: Save*"));
			Assert.AreEqual("root MyApp",                   Under("namespace: MyApp.** and (method: *Async or public)", "method: any"));
		}

		[TestMethod]
		public void ANameAnchorIsCheckedOnTheNameOfACallTest()
		{
			var anchors = FilterBlockBuilder.Build(CascadeLevel.Assembly, ["method: Append* or *Async or Get*Core or Save"]).Anchors;

			Assert.AreEqual(4, anchors.Terms.Length);

			foreach (var name in new[] { "Append", "AppendLine", "RunAsync", "Async", "GetCore", "GetItCore", "Save" })
				Assert.IsTrue(anchors.MayBindName(name), name);

			foreach (var name in new[] { "Appen", "append", "AsyncRun", "GetCor", "Getore", "SaveAll", "Sav", "" })
				Assert.IsFalse(anchors.MayBindName(name), name);

			// The two ends of one name do not share characters; two conditions on the name may.
			var ends = FilterBlockBuilder.Build(CascadeLevel.Assembly, ["method: Get*tCore"]).Anchors;
			var both = FilterBlockBuilder.Build(CascadeLevel.Assembly, ["method: Get* and method: *tCore"]).Anchors;

			Assert.IsFalse(ends.MayBindName("GetCore"));
			Assert.IsTrue (ends.MayBindName("GettCore"));
			Assert.IsFalse(both.MayBindName("GeCore"));
			Assert.IsTrue (both.MayBindName("GetCore"));

			// The text in the middle of a name, in its order.
			var middle = FilterBlockBuilder.Build(CascadeLevel.Assembly, ["method: *Get*By*"]).Anchors;

			Assert.IsTrue (middle.MayBindName("TryGetItemByKey"));
			Assert.IsTrue (middle.MayBindName("GetBy"));
			Assert.IsFalse(middle.MayBindName("ByGet"));
			Assert.IsFalse(middle.MayBindName("GetB"));

			// A term without a name anchor says nothing of the name.
			Assert.IsTrue(FilterBlockBuilder.Build(CascadeLevel.Assembly, ["namespace: MyApp.**"]).Anchors.MayBindName("Anything"));
			Assert.IsTrue(FilterBlockBuilder.Build(CascadeLevel.Assembly, ["public"]).Anchors.MayBindName("Anything"));
			Assert.IsFalse(FilterBlockBuilder.Build(CascadeLevel.Assembly, ["method: A and method: B"]).Anchors.MayBindName("A"));
		}

		[TestMethod]
		public void AnchorsAreNecessaryForEveryMethodTheFilterSelectsTest()
		{
			string[] filters =
			[
				"method: Save*", "method: *Async", "method: {Get,Find}*", "method: Shop.**", "method: global::Shop.**.Save*", "namespace: Shop.**",
				"namespace: {Shop,Other}.**", "type: global::Shop.Services.*", "public", "method: Save* or public", "method: Save* and public",
				"namespace: Shop.** and method: *Async", "method: Save* and (static or method: *Async)", "not method: Save*", "project and method: Get*",
				"type: *Service and method: {Save,Load}*", "method: *Service.Save*\n- method: *Async", "Save*(int)", "method: Save and method: Load",
				"method: Save* and method: *ave", "method: *a*e*", "method: *Async and method: Save*", "method: S*c and method: *ave*",
			];

			var facts = from type in new[] { "Shop.Services.OrderService", "Shop.Repo", "Other.Service", "System.Text.StringBuilder", "Service" }
						from name in new[] { "Save", "SaveAsync", "GetAsync", "Find", "Load", "Append" }
						from project in new[] { true, false }
						select new RecordFacts(type, name, "void", name == "Save" ? ["int"] : []) { IsProject = project, IsStatic = name == "Find" };

			var checkedCount = 0;

			foreach (var filter in filters)
			{
				var block     = FilterBlockBuilder.Build(CascadeLevel.Assembly, [filter]);
				var evaluator = new CascadeEvaluator<FilterTarget>(FilterBlockBuilder.Build(CascadeLevel.Aspect, []).Block, [block.Block], [], static t => t.Facts.IsProject);

				foreach (var fact in facts)
				{
					if (!evaluator.Decide(FilterTarget.Call(fact), [], []).Selected)
						continue;

					checkedCount++;

					var name = fact.Method.Name;
					var root = fact.ContainingType.Segments[0].Name;

					Assert.IsTrue(
						block.Anchors.Terms.Any(t =>
							t.MatchesName(name) &&
							(t.Root is null || t.Root == root) &&
							(!t.IsProjectOnly || fact.IsProject)),
						$"'{filter}' selects {fact.ContainingType}.{name} (project: {fact.IsProject}) outside its anchors: {block.Anchors}");
				}
			}

			Assert.IsTrue(checkedCount > 100, $"Only {checkedCount} selections.");
		}
	}
}
