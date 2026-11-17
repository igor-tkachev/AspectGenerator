using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using AspectGenerator.TargetFilterBinding;
using AspectGenerator.TargetFilterFacts;

using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilterBinding
{
	/// <summary>
	/// What each kind of pattern matches, row by row: a filter of one condition, the facts of a method written by hand,
	/// and the answer for a call and for the definition (the same unless the row says otherwise). In the facts
	/// <c>!T</c> is a type parameter.
	/// </summary>
	[TestClass]
	public class PatternTruthTableTests
	{
		const Tri T = Tri.True;
		const Tri F = Tri.False;
		const Tri D = Tri.Depends;

		static RecordFacts M(string type, string method = "M", string returns = "void", params string[] parameters)
		{
			return new RecordFacts(type, method, returns, parameters);
		}

		static RecordFacts Returns(string returns, ReturnRef returnRef = ReturnRef.None)
		{
			return new RecordFacts("Shop.C", "M", returns) { ReturnRef = returnRef };
		}

		static RecordFacts Params(params string[] parameters)
		{
			return new RecordFacts("Shop.C", "M", "void", parameters);
		}

		static RecordFacts Attributed(params string[] attributes)
		{
			return new RecordFacts("Shop.C", "M") { Attributes = [..attributes.Select(ShapeText.ParseNamed)] };
		}

		static void Check(IEnumerable<(string Filter, RecordFacts Facts, Tri Call, Tri? Definition)> rows)
		{
			var failures = new List<string>();
			var count    = 0;

			foreach (var (filter, facts, call, definition) in rows)
			{
				count++;

				var name = $"{facts.ContainingType}.{facts.Method}({string.Join(", ", facts.Parameters.Select(static p => $"{p.Modifiers} {p.Type}"))}) : {facts.ReturnRef} {facts.ReturnType}";

				var actualCall       = FilterProbe.Answer(filter, facts, MatchPhase.Call);
				var actualDefinition = FilterProbe.Answer(filter, facts, MatchPhase.Definition);

				if (actualCall != call)
					failures.Add($"'{filter}' on call {name}: {actualCall}, expected {call}");

				if (actualDefinition != (definition ?? call))
					failures.Add($"'{filter}' on definition {name}: {actualDefinition}, expected {definition ?? call}");
			}

			Assert.AreEqual(0, failures.Count, $"{failures.Count} of {count} rows:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
		}

		[TestMethod]
		public void NamespaceIsRootedTest()
		{
			Check(
			[
				("namespace: Shop",                M("Shop.Order"),              T, null),
				("namespace: Shop",                M("Shop.Data.Order"),         F, null),
				("namespace: Shop",                M("My.Shop.Order"),           F, null),
				("namespace: Data",                M("Shop.Data.Order"),         F, null),
				("namespace: Shop.**",             M("Shop.Order"),              T, null),
				("namespace: Shop.**",             M("Shop.Data.Deep.Order"),    T, null),
				("namespace: Shop.**",             M("Shopping.Order"),          F, null),
				("namespace: Shop.**",             M("My.Shop.Order"),           F, null),
				("namespace: Shop.*",              M("Shop.Data.Order"),         T, null),
				("namespace: Shop.*",              M("Shop.Order"),              F, null),
				("namespace: Shop.*",              M("Shop.Data.Deep.Order"),    F, null),
				("namespace: *.Data",              M("Shop.Data.Order"),         T, null),
				("namespace: **.Data",             M("A.B.Data.Order"),          T, null),
				("namespace: **.Data",             M("Data.Order"),              T, null),
				("namespace: **.Data.**",          M("A.Data.B.Order"),          T, null),
				("namespace: **",                  M("Order"),                   T, null),
				("namespace: **",                  M("A.B.Order"),               T, null),
				("namespace: *",                   M("Order"),                   F, null),
				("namespace: *",                   M("A.Order"),                 T, null),
				("namespace: \"\"",                M("Order"),                   T, null),
				("namespace: \"\"",                M("Shop.Order"),              F, null),
				("namespace: global::Shop.**",     M("Shop.X.Order"),            T, null),
				("namespace: global::Shop.**",     M("My.Shop.X.Order"),         F, null),
				("namespace: Sh*p",                M("Shop.Order"),              T, null),
				("namespace: Sh*p",                M("Sharp.Order"),             T, null),
				("namespace: Sh*p",                M("Shops.Order"),             F, null),
				("namespace: Sh*",                 M("Sh.Order"),                T, null),
				("namespace: {Shop,Billing}.**",   M("Billing.X.Order"),         T, null),
				("namespace: {Shop,Billing}.**",   M("Other.Order"),             F, null),
				("namespace: {Shop.Data,Billing}", M("Shop.Data.Order"),         T, null),
				("namespace: {Shop.Data,Billing}", M("Shop.Order"),              F, null),
				("namespace: {Shop.Data,Billing}", M("Billing.Order"),           T, null),
				("namespace: Shop.{Data,Web}.**",  M("Shop.Web.Pages.Order"),    T, null),
				("namespace: Shop.{Data,Web}.**",  M("Shop.Core.Order"),         F, null),
				("namespace: Shop.{Da,We}*",       M("Shop.Web.Order"),          T, null),
				("namespace: {Sh.A,B}x.C",         M("Sh.Ax.C.Order"),           T, null),
				("namespace: {Sh.A,B}x.C",         M("Bx.C.Order"),              T, null),
				("namespace: {Sh.A,B}x.C",         M("Sh.A.C.Order"),            F, null),
				("namespace: @static.x",           M("static.x.Order"),          T, null),

				// A type nested in a type: the namespace ends where the types begin.
				("namespace: Shop",                new RecordFacts("Shop.Outer.Inner", "M") { NamespaceSegments = 1 }, T, null),
				("namespace: Shop.Outer",          new RecordFacts("Shop.Outer.Inner", "M") { NamespaceSegments = 1 }, F, null),
			]);
		}

		[TestMethod]
		public void TypeMatchesATrailingRunOfWholeSegmentsTest()
		{
			Check(
			[
				("type: Order",                      M("Shop.Order"),                                   T, null),
				("type: Order",                      M("Order"),                                        T, null),
				("type: Order",                      M("Shop.PreOrder"),                                F, null),
				("type: *Service",                   M("Shop.Services.OrderService"),                   T, null),
				("type: *Service",                   M("Shop.Services.OrderServices"),                  F, null),
				("type: Data.*Repository",           M("Shop.Data.OrderRepository"),                    T, null),
				("type: Data.*Repository",           M("Shop.MyData.OrderRepository"),                  F, null),
				("type: Data.*Repository",           M("Data.Sub.OrderRepository"),                     F, null),
				("type: global::Shop.Order",         M("Shop.Order"),                                   T, null),
				("type: global::Shop.Order",         M("Outer.Shop.Order"),                             F, null),
				("type: global::Order",              M("Order"),                                        T, null),
				("type: global::Order",              M("Shop.Order"),                                   F, null),
				("type: Outer.Inner",                M("Shop.Outer.Inner"),                             T, null),
				("type: Inner",                      M("Shop.Outer.Inner"),                             T, null),
				("type: Outer",                      M("Shop.Outer.Inner"),                             F, null),
				("type: Shop.*.Order",               M("Shop.A.Order"),                                 T, null),
				("type: Shop.*.Order",               M("Shop.Order"),                                   F, null),
				("type: Shop.*.Order",               M("Shop.A.B.Order"),                               F, null),
				("type: Shop.**.Order",              M("Shop.Order"),                                   T, null),
				("type: Shop.**.Order",              M("Shop.A.B.Order"),                               T, null),
				("type: Shop.**.Order",              M("My.Shop.A.Order"),                              T, null),
				("type: global::Shop.**.Order",      M("My.Shop.A.Order"),                              F, null),
				("type: *Order*",                    M("Shop.PreOrderPost"),                            T, null),
				("type: O*r*r",                      M("Order"),                                        T, null),
				("type: O*r*r",                      M("Orr"),                                          T, null),
				("type: O*r*r",                      M("Or"),                                           F, null),
				("type: *",                          M("Shop.Order"),                                   T, null),
				("type: **",                         M("Shop.Order"),                                   T, null),
				("type: *.@static",                  M("Shop.static"),                                  T, null),
				("type: {Order,Invoice}Service",     M("Shop.InvoiceService"),                          T, null),
				("type: {Order,Invoice}Service",     M("Shop.PaymentService"),                          F, null),
				("type: {Order,Invoice}",            M("Shop.Invoice"),                                 T, null),
				("type: {Shop.Order,Billing.Bill}",  M("Shop.Order"),                                   T, null),
				("type: {Shop.Order,Billing.Bill}",  M("Billing.Order"),                                F, null),
				("type: {global::Order,Bill}",       M("Shop.Order"),                                   F, null),
				("type: {global::Order,Bill}",       M("Shop.Bill"),                                    T, null),

				// Type arguments: none written takes any arity, written ones are matched one by one.
				("type: List",                       M("System.Collections.Generic.List<int>"),         T, null),
				("type: List<*>",                    M("System.Collections.Generic.List<int>"),         T, null),
				("type: List<*,*>",                  M("System.Collections.Generic.List<int>"),         F, null),
				("type: List<*>",                    M("Shop.List"),                                    F, null),
				("type: List<int>",                  M("System.Collections.Generic.List<int>"),         T, null),
				("type: List<int>",                  M("System.Collections.Generic.List<string>"),      F, null),
				("type: List<System.Int32>",         M("System.Collections.Generic.List<int>"),         T, null),
				("type: List<Int32>",                M("System.Collections.Generic.List<int>"),         T, null),
				("type: List<global::Int32>",        M("System.Collections.Generic.List<int>"),         F, null),
				("type: Dictionary<string,*>",       M("System.Collections.Generic.Dictionary<string,int>"), T, null),
				("type: Dictionary<string, *>",      M("System.Collections.Generic.Dictionary<int,int>"),    F, null),
				("type: Outer<int>.Inner",           M("Shop.Outer<int>.Inner"),                        T, null),
				("type: Outer<int>.Inner",           M("Shop.Outer<string>.Inner"),                     F, null),
				("type: Outer.Inner",                M("Shop.Outer<int>.Inner"),                        T, null),
				("type: List<List<*>>",              M("System.Collections.Generic.List<System.Collections.Generic.List<int>>"), T, null),
				("type: List<List<int>>",            M("System.Collections.Generic.List<System.Collections.Generic.List<string>>"), F, null),

				// C#'s names of the system types are rooted; with `@` the word is an ordinary name.
				("type: int",                        M("System.Int32"),                                 T, null),
				("type: int",                        M("My.Int32"),                                     F, null),
				("type: Int32",                      M("My.Int32"),                                     T, null),
				("type: @int",                       M("System.Int32"),                                 F, null),
				("type: string",                     M("System.String"),                                T, null),
				("type: object",                     M("System.Object"),                                T, null),

				// A type parameter of the definition where the pattern names a type: the call decides.
				("type: List<int>",                  M("System.Collections.Generic.List<!T>"),          F, D),
				("type: List<*>",                    M("System.Collections.Generic.List<!T>"),          T, null),
				("type: List",                       M("System.Collections.Generic.List<!T>"),          T, null),
				("type: Set<int>",                   M("System.Collections.Generic.List<!T>"),          F, null),
				("type: Dictionary<int,string>",     M("System.Collections.Generic.Dictionary<!K,int>"), F, null),
				("type: Dictionary<int,int>",        M("System.Collections.Generic.Dictionary<!K,int>"), F, D),
				("type: Outer<int>.Inner<*>",        M("Shop.Outer<!T>.Inner<!U>"),                     F, D),
				("type: List<List<int>>",            M("System.Collections.Generic.List<System.Collections.Generic.List<!T>>"), F, D),
				("type: List<int[]>",                M("System.Collections.Generic.List<!T[]>"),        F, D),
				("type: List<*[]>",                  M("System.Collections.Generic.List<!T[]>"),        T, null),
				("type: List<int[]>",                M("System.Collections.Generic.List<!T>"),          F, D),
				("type: List<Set<*>>",               M("System.Collections.Generic.List<!T[]>"),        F, null),
			]);
		}

		[TestMethod]
		public void QuotedTextIsExactTest()
		{
			var main = new RecordFacts(new NamedTypeShape([new NameSegment("Shop"), new NameSegment("<Main>$")], true), new NameSegment("Run"), ShapeText.Parse("void"), []);

			Assert.AreEqual(T, FilterProbe.Answer("type: \"<Main>$\"", main));
			Assert.AreEqual(T, FilterProbe.Answer("type: Shop.\"<Main>$\"", main));
			Assert.AreEqual(F, FilterProbe.Answer("type: \"<Main>\"", main));
			Assert.AreEqual(F, FilterProbe.Answer("type: \"*\"", main));
			Assert.AreEqual(T, FilterProbe.Answer("method: \"<Main>$\".Run", main));
			Assert.AreEqual(T, FilterProbe.Answer("method: \"R\"*", main));

			// An escaped quote and backslash are the characters themselves.
			var odd = new RecordFacts(new NamedTypeShape([new NameSegment("a\"b\\c")], true), new NameSegment("Run"), ShapeText.Parse("void"), []);

			Assert.AreEqual(T, FilterProbe.Answer("type: \"a\\\"b\\\\c\"", odd));
		}

		[TestMethod]
		public void MethodMatchesTrailingSegmentsOfItsPathTest()
		{
			Check(
			[
				("method: Save",                        M("Shop.OrderService", "Save"),                        T, null),
				("method: Save",                        M("Shop.OrderService", "SaveOrder"),                   F, null),
				("method: Save*",                       M("Shop.OrderService", "SaveOrder"),                   T, null),
				("method: Save*",                       M("Shop.OrderService", "Save"),                        T, null),
				("method: Save*",                       M("Shop.OrderService", "Sav"),                         F, null),
				("method: *Async",                      M("Shop.OrderService", "SaveAsync"),                   T, null),
				("method: *Async",                      M("Shop.OrderService", "AsyncSave"),                   F, null),
				("method: *Internal*",                  M("Shop.OrderService", "RunInternalNow"),              T, null),
				("method: Get*Async",                   M("Shop.OrderService", "GetAsync"),                    T, null),
				("method: Get*Async",                   M("Shop.OrderService", "GetAsyn"),                     F, null),
				("method: Get*Async",                   M("Shop.OrderService", "GeAsync"),                     F, null),
				("method: OrderService.Save",           M("Shop.OrderService", "Save"),                        T, null),
				("method: Service.Save",                M("Shop.OrderService", "Save"),                        F, null),
				("method: *Service.Save",               M("Shop.OrderService", "Save"),                        T, null),
				("method: **.*Service.Save*",           M("Shop.OrderService", "SaveAll"),                     T, null),
				("method: Shop.**.Save",                M("Shop.OrderService", "Save"),                        T, null),
				("method: Shop.**.Save",                M("Shop", "Save"),                                     T, null),
				("method: Shop.**",                     M("Shop.OrderService", "Save"),                        T, null),
				("method: Shop.**",                     M("My.Shop.OrderService", "Save"),                     T, null),
				("method: global::Shop.**",             M("My.Shop.OrderService", "Save"),                     F, null),
				("method: global::Shop.OrderService.Save", M("Shop.OrderService", "Save"),                     T, null),
				("method: global::OrderService.Save",   M("Shop.OrderService", "Save"),                        F, null),
				("method: System.Console.WriteLine",    M("System.Console", "WriteLine"),                      T, null),
				("method: {Get,Find}*",                 M("Shop.Repo", "FindAll"),                             T, null),
				("method: {Get,Find}*",                 M("Shop.Repo", "Load"),                                F, null),
				("method: *Repository.{Get,Find}*",     M("Shop.OrderRepository", "GetOne"),                   T, null),
				("method: {Repo.Get,Cache.Find}",       M("Shop.Cache", "Find"),                               T, null),
				("method: {Repo.Get,Cache.Find}",       M("Shop.Cache", "Get"),                                F, null),
				("method: @any",                        M("Shop.Repo", "any"),                                 T, null),

				// Type arguments of the type and of the method.
				("method: List<int>.Add",               M("System.Collections.Generic.List<int>", "Add"),      T, null),
				("method: List<int>.Add",               M("System.Collections.Generic.List<string>", "Add"),   F, null),
				("method: List.Add",                    M("System.Collections.Generic.List<string>", "Add"),   T, null),
				("method: Get",                         M("Shop.Repo", "Get<int>"),                            T, null),
				("method: Get<*>",                      M("Shop.Repo", "Get<int>"),                            T, null),
				("method: Get<*>",                      M("Shop.Repo", "Get"),                                 F, null),
				("method: Get<*>",                      M("Shop.Repo", "Get<int,string>"),                     F, null),
				("method: Get<int>",                    M("Shop.Repo", "Get<int>"),                            T, null),
				("method: Get<int>",                    M("Shop.Repo", "Get<string>"),                         F, null),
				("method: Get<TKey>",                   M("Shop.Repo", "Get<Shop.TKey>"),                      T, null),

				// On the definition a type parameter stands where the pattern names a type: the call decides. What the
				// names alone decide stays decided.
				("method: List<int>.Add",               M("System.Collections.Generic.List<!T>", "Add"),       F, D),
				("method: List<int>.Remove",            M("System.Collections.Generic.List<!T>", "Add"),       F, null),
				("method: List<*>.Add",                 M("System.Collections.Generic.List<!T>", "Add"),       T, null),
				("method: Get<int>",                    M("Shop.Repo", "Get<!T>"),                             F, D),
				("method: Get<*>",                      M("Shop.Repo", "Get<!T>"),                             T, null),
				("method: Get",                         M("Shop.Repo", "Get<!T>"),                             T, null),
				("method: Get<int,string>",             M("Shop.Repo", "Get<!T>"),                             F, null),
				("method: Get<TKey>",                   M("Shop.Repo", "Get<!TKey>"),                          F, D),
				("method: {Get<int>,Find}",             M("Shop.Repo", "Get<!T>"),                             F, D),
				("method: {Get<int>,Get<*>}",           M("Shop.Repo", "Get<!T>"),                             T, null),
				("method: Enumerable.Any<int>",         M("System.Linq.Enumerable", "Any<!TSource>"),          F, D),
			]);
		}

		[TestMethod]
		public void ReturnTypesAreMatchedByTheirFormTest()
		{
			Check(
			[
				("returns: Task",                    Returns("System.Threading.Tasks.Task"),                    T, null),
				("returns: Task",                    Returns("System.Threading.Tasks.Task<int>"),               T, null),
				("returns: Task<*>",                 Returns("System.Threading.Tasks.Task"),                    F, null),
				("returns: Task<*>",                 Returns("System.Threading.Tasks.Task<int>"),               T, null),
				("returns: Task*",                   Returns("System.Threading.Tasks.TaskLike"),                T, null),
				("returns: void",                    Returns("void"),                                           T, null),
				("returns: void",                    Returns("int"),                                            F, null),
				("returns: {void,Task}",             Returns("System.Threading.Tasks.Task<int>"),               T, null),
				("returns: {void,Task}",             Returns("void"),                                           T, null),
				("returns: {void,Task}",             Returns("int"),                                            F, null),
				("returns: {int[], string}",         Returns("int[]"),                                          T, null),
				("returns: {int[], string}",         Returns("string"),                                         T, null),
				("returns: {int[], string}",         Returns("int"),                                            F, null),

				// The pattern alone takes any way of returning; with `ref` it takes that way only.
				("returns: int",                     Returns("int", ReturnRef.Ref),                             T, null),
				("returns: int",                     Returns("int", ReturnRef.RefReadOnly),                     T, null),
				("returns: ref int",                 Returns("int", ReturnRef.Ref),                             T, null),
				("returns: ref int",                 Returns("int"),                                            F, null),
				("returns: ref int",                 Returns("int", ReturnRef.RefReadOnly),                     F, null),
				("returns: ref readonly int",        Returns("int", ReturnRef.RefReadOnly),                     T, null),
				("returns: ref readonly int",        Returns("int", ReturnRef.Ref),                             F, null),
				("returns: ref readonly Span<int>",  Returns("System.Span<int>", ReturnRef.RefReadOnly),        T, null),

				// Arrays carry their rank, jagged ones in C# order.
				("returns: int[]",                   Returns("int[]"),                                          T, null),
				("returns: int[]",                   Returns("int[,]"),                                         F, null),
				("returns: int[]",                   Returns("int[][]"),                                        F, null),
				("returns: int[]",                   Returns("int"),                                            F, null),
				("returns: int[,]",                  Returns("int[,]"),                                         T, null),
				("returns: int[][,]",                Returns("int[][,]"),                                       T, null),
				("returns: int[][,]",                Returns("int[,][]"),                                       F, null),
				("returns: *[]",                     Returns("string[]"),                                       T, null),
				("returns: *[]",                     Returns("string[][]"),                                     T, null),
				("returns: *[][]",                   Returns("string[]"),                                       F, null),
				("returns: List<int[]>",             Returns("System.Collections.Generic.List<int[]>"),         T, null),

				// `?` over a value type is Nullable<>; on a reference type the annotation is not looked at.
				("returns: int?",                    Returns("System.Nullable<int>"),                           T, null),
				("returns: int?",                    Returns("int"),                                            F, null),
				("returns: int",                     Returns("System.Nullable<int>"),                           F, null),
				("returns: Nullable<int>",           Returns("System.Nullable<int>"),                           T, null),
				("returns: System.Nullable<*>",      Returns("System.Nullable<int>"),                           T, null),
				("returns: System.Nullable<*>",      Returns("string"),                                         F, null),
				("returns: *?",                      Returns("string"),                                         T, null),
				("returns: *?",                      Returns("System.Nullable<int>"),                           T, null),
				("returns: *?",                      Returns("int"),                                            F, null),
				("returns: *?",                      Returns("int[]"),                                          T, null),
				("returns: string?",                 Returns("string"),                                         T, null),
				("returns: string",                  Returns("string"),                                         T, null),
				("returns: int?[]",                  Returns("System.Nullable<int>[]"),                         T, null),
				("returns: int?[]",                  Returns("int[]"),                                          F, null),
				("returns: int[]?",                  Returns("int[]"),                                          T, null),
				("returns: List<string?>",           Returns("System.Collections.Generic.List<string>"),        T, null),

				// Tuples by their elements.
				("returns: (int, string)",           Returns("(int, string)"),                                  T, null),
				("returns: (int, string)",           Returns("(int, int)"),                                     F, null),
				("returns: (int, string)",           Returns("(int, string, bool)"),                            F, null),
				("returns: (*, *)",                  Returns("(int, string)"),                                  T, null),
				("returns: (*, *)",                  Returns("int"),                                            F, null),
				("returns: ValueTuple<int,string>",  Returns("(int, string)"),                                  T, null),
				("returns: (int, (string, bool))",   Returns("(int, (string, bool))"),                          T, null),
				("returns: (int, string)[]",         Returns("(int, string)[]"),                                T, null),

				// A type parameter: `*` alone matches it; on the definition anything else is for the call to decide,
				// except where the form already says no.
				("returns: *",                       Returns("!T"),                                             T, null),
				("returns: int",                     Returns("!T"),                                             F, D),
				("returns: T",                       Returns("!T"),                                             F, D),
				("returns: **",                      Returns("!T"),                                             F, D),
				("returns: *?",                      Returns("!T"),                                             F, D),
				("returns: {int, *}",                Returns("!T"),                                             T, null),
				("returns: {int, string}",           Returns("!T"),                                             F, D),
				("returns: *[]",                     Returns("!T[]"),                                           T, null),
				("returns: int[]",                   Returns("!T[]"),                                           F, D),
				("returns: int[]",                   Returns("!T"),                                             F, D),
				("returns: int[,]",                  Returns("!T[]"),                                           F, null),
				("returns: List<*>",                 Returns("!T[]"),                                           F, null),
				("returns: List<*>",                 Returns("System.Collections.Generic.List<!T>"),            T, null),
				("returns: List<int>",               Returns("System.Collections.Generic.List<!T>"),            F, D),
				("returns: Set<int>",                Returns("System.Collections.Generic.List<!T>"),            F, null),
				("returns: (int, *)",                Returns("(!T, string)"),                                   F, D),
				("returns: (*, *)",                  Returns("(!T, string)"),                                   T, null),
				("returns: (*, int)",                Returns("(!T, string)"),                                   F, null),
				("returns: int?",                    Returns("System.Nullable<!T>"),                            F, D),
				("returns: *?",                      Returns("System.Nullable<!T>"),                            T, null),
				("returns: ref int",                 Returns("!T"),                                             F, null),
			]);
		}

		[TestMethod]
		public void ParameterListsTest()
		{
			const string Token = "System.Threading.CancellationToken";

			Check(
			[
				("params: ()",                         Params(),                                        T, null),
				("params: ()",                         Params("int"),                                   F, null),
				("params: (..)",                       Params(),                                        T, null),
				("params: (..)",                       Params("int", "string"),                         T, null),
				("params: (int, ..)",                  Params("int"),                                   T, null),
				("params: (int, ..)",                  Params("int", "string"),                         T, null),
				("params: (int, ..)",                  Params("string"),                                F, null),
				("params: (int, ..)",                  Params(),                                        F, null),
				("params: (.., CancellationToken)",    Params("int", Token),                            T, null),
				("params: (.., CancellationToken)",    Params(Token),                                   T, null),
				("params: (.., CancellationToken)",    Params(Token, "int"),                            F, null),
				("params: (.., CancellationToken)",    Params(),                                        F, null),
				("params: (int, .., string)",          Params("int", "string"),                         T, null),
				("params: (int, .., string)",          Params("int", "bool", "bool", "string"),         T, null),
				("params: (int, .., string)",          Params("int"),                                   F, null),
				("params: (_, out bool)",              Params("int", "out bool"),                       T, null),
				("params: (_, out bool)",              Params("int", "bool"),                           F, null),
				("params: (_, out bool)",              Params("out bool"),                              F, null),
				("params: (_)",                        Params("ref int"),                               T, null),
				("params: (int, string)",              Params("int", "string"),                         T, null),
				("params: (int, string)",              Params("string", "int"),                         F, null),

				// A modifier in the pattern is matched exactly; a pattern without one takes any.
				("params: (this string)",              Params("this string"),                           T, null),
				("params: (this string)",              Params("string"),                                F, null),
				("params: (string)",                   Params("this string"),                           T, null),
				("params: (int)",                      Params("ref int"),                               T, null),
				("params: (ref int)",                  Params("ref int"),                               T, null),
				("params: (ref int)",                  Params("in int"),                                F, null),
				("params: (ref int)",                  Params("int"),                                   F, null),
				("params: (in int)",                   Params("ref readonly int"),                      F, null),
				("params: (ref readonly int, in Guid, params int[])", Params("ref readonly int", "in System.Guid", "params int[]"), T, null),
				("params: (ref readonly int, in Guid, params int[])", Params("ref readonly int", "in System.Guid", "int[]"),        F, null),
				("params: (this ref int)",             Params("this ref int"),                          T, null),
				("params: (ref int)",                  Params("this ref int"),                          F, null),

				("param: CancellationToken",           Params("int", Token),                            T, null),
				("param: CancellationToken",           Params("int"),                                   F, null),
				("param: CancellationToken",           Params(),                                        F, null),
				("param: out bool",                    Params("int", "out bool"),                       T, null),
				("param: out bool",                    Params("int", "bool"),                           F, null),
				("param: this IEnumerable<*>",         Params("this System.Collections.Generic.IEnumerable<int>"), T, null),
				("param: this IEnumerable<*>",         Params("System.Collections.Generic.IEnumerable<int>"),      F, null),
				("param: _",                           Params("int"),                                   T, null),
				("param: _",                           Params(),                                        F, null),
				("param: {int, string}",               Params("bool", "string"),                        T, null),

				// Type parameters in the list.
				("params: (int)",                      Params("!T"),                                    F, D),
				("params: (*)",                        Params("!T"),                                    T, null),
				("params: (_)",                        Params("!T"),                                    T, null),
				("params: (int, string)",              Params("!T"),                                    F, null),
				("params: (_, int)",                   Params("!T", "!U"),                              F, D),
				("params: (int, bool)",                Params("!T", "string"),                          F, null),
				("params: (*, ..)",                    Params("!T", "!U"),                              T, null),
				("params: (.., int)",                  Params("string", "!T"),                          F, D),
				("params: (ref int)",                  Params("!T"),                                    F, null),
				("params: (IEnumerable<int>)",         Params("this System.Collections.Generic.IEnumerable<!T>"), F, D),
				("param: int",                         Params("!T", "string"),                          F, D),
				("param: int",                         Params("string", "int", "!T"),                   T, null),
				("param: int",                         Params("string"),                                F, null),
				("param: out int",                     Params("!T"),                                    F, null),
			]);
		}

		/// <summary>
		/// The two ways to match the modifiers a pattern writes. They agree where the pattern writes none, or all the
		/// parameter has; they differ where the parameter has more.
		/// </summary>
		[TestMethod]
		public void ParameterModifiersInBothMatchingsTest()
		{
			(string Filter, string[] Parameters, Tri ExactSet, Tri PerKind)[] rows =
			[
				("params: (int)",                 ["this ref int"],        T, T),
				("params: (this ref int)",        ["this ref int"],        T, T),
				("params: (ref int)",             ["ref int"],             T, T),
				("params: (ref int)",             ["in int"],              F, F),
				("params: (ref int)",             ["int"],                 F, F),
				("params: (this int)",            ["int"],                 F, F),
				("params: (params int[])",        ["int[]"],               F, F),
				("params: (ref int)",             ["this ref int"],        F, T),
				("params: (this int)",            ["this ref int"],        F, T),
				("params: (this int)",            ["this int"],            T, T),
				("params: (in int)",              ["this ref int"],        F, F),
				("params: (this in int)",         ["this ref int"],        F, F),
				("params: (int[])",               ["params int[]"],        T, T),
				("param: ref readonly int",       ["ref readonly int"],    T, T),
				("param: ref readonly int",       ["ref int"],             F, F),
				("param: out *",                  ["int", "out bool"],     T, T),
			];

			foreach (var (filter, parameters, exactSet, perKind) in rows)
			{
				var facts = Params(parameters);

				Assert.AreEqual(exactSet, FilterProbe.Answer(filter, facts, modifiers: ParameterModifierMatching.ExactSet), $"{filter} on ({string.Join(", ", parameters)}), exact set");
				Assert.AreEqual(perKind,  FilterProbe.Answer(filter, facts, modifiers: ParameterModifierMatching.PerKind),  $"{filter} on ({string.Join(", ", parameters)}), per kind");
			}
		}

		[TestMethod]
		public void AttributesMatchWithOrWithoutTheSuffixTest()
		{
			Check(
			[
				("attribute: Obsolete",                Attributed("System.ObsoleteAttribute"),             T, null),
				("attribute: ObsoleteAttribute",       Attributed("System.ObsoleteAttribute"),             T, null),
				("attribute: System.ObsoleteAttribute", Attributed("System.ObsoleteAttribute"),            T, null),
				("attribute: System.Obsolete",         Attributed("System.ObsoleteAttribute"),             T, null),
				("attribute: global::System.Obsolete", Attributed("System.ObsoleteAttribute"),             T, null),
				("attribute: global::Obsolete",        Attributed("System.ObsoleteAttribute"),             F, null),
				("attribute: Obsolet",                 Attributed("System.ObsoleteAttribute"),             F, null),
				("attribute: *Log",                    Attributed("Shop.NoLogAttribute"),                  T, null),
				("attribute: No*",                     Attributed("Shop.NoLogAttribute"),                  T, null),
				("attribute: NoLog",                   Attributed("Shop.NoLog"),                           T, null),
				("attribute: NoLogAttribute",          Attributed("Shop.NoLog"),                           F, null),
				("attribute: Attribute",               Attributed("Shop.NoLogAttribute"),                  F, null),
				("attribute: Attribute",               Attributed("System.Attribute"),                     T, null),
				("attribute: {Obsolete, NoLog}",       Attributed("Shop.NoLogAttribute"),                  T, null),
				("attribute: {Obsolete, NoLog}",       Attributed("Shop.AuditedAttribute"),                F, null),
				("attribute: NoLog",                   Attributed("Shop.AuditedAttribute", "Shop.NoLogAttribute"), T, null),
				("attribute: *",                       Attributed(),                                       F, null),
				("attribute: *",                       Attributed("Shop.NoLogAttribute"),                  T, null),
				("attribute: Table<*>",                Attributed("Shop.TableAttribute<int>"),             T, null),
				("attribute: Table<int>",              Attributed("Shop.TableAttribute<string>"),          F, null),
				("attribute: NoLog[]",                 Attributed("Shop.NoLogAttribute"),                  F, null),
			]);
		}

		[TestMethod]
		public void WordsTest()
		{
			static RecordFacts W(
				Accessibility accessibility = Accessibility.Public,
				Virtuality    virtuality    = Virtuality.None,
				bool          isStatic      = false,
				bool          isExtern      = false,
				bool          isExtension   = false,
				bool          isGenerated   = false,
				bool          isProject     = true,
				bool          isAsync       = false)
			{
				return new RecordFacts("Shop.C", "M")
				{
					Accessibility = accessibility,
					Virtuality    = virtuality,
					IsStatic      = isStatic,
					IsExtern      = isExtern,
					IsExtension   = isExtension,
					IsGenerated   = isGenerated,
					IsProject     = isProject,
					IsAsync       = isAsync,
				};
			}

			Check(
			[
				("public",             W(Accessibility.Public),                T, null),
				("public",             W(Accessibility.Internal),              F, null),
				("internal",           W(Accessibility.Internal),              T, null),
				("protected",          W(Accessibility.Protected),             T, null),
				("protected",          W(Accessibility.ProtectedOrInternal),   F, null),
				("private",            W(Accessibility.Private),               T, null),
				("protected internal", W(Accessibility.ProtectedOrInternal),   T, null),
				("internal protected", W(Accessibility.ProtectedOrInternal),   T, null),
				("protected internal", W(Accessibility.Protected),             F, null),
				("private protected",  W(Accessibility.ProtectedAndInternal),  T, null),
				("protected private",  W(Accessibility.ProtectedAndInternal),  T, null),
				("private protected",  W(Accessibility.Private),               F, null),
				("not public",         W(Accessibility.Internal),              T, null),
				("static",             W(isStatic: true),                      T, null),
				("static",             W(),                                    F, null),
				("instance",           W(),                                    T, null),
				("instance",           W(isStatic: true),                      F, null),
				("virtual",            W(virtuality: Virtuality.Virtual),      T, null),
				("virtual",            W(virtuality: Virtuality.Override),     F, null),
				("override",           W(virtuality: Virtuality.Override),     T, null),
				("override",           W(virtuality: Virtuality.Override | Virtuality.Sealed),   T, null),
				("sealed",             W(virtuality: Virtuality.Override | Virtuality.Sealed),   T, null),
				("sealed",             W(virtuality: Virtuality.Override),     F, null),
				("abstract",           W(virtuality: Virtuality.Abstract),     T, null),
				("abstract",           W(virtuality: Virtuality.Override | Virtuality.Abstract), T, null),
				("abstract",           W(),                                    F, null),
				("extern",             W(isExtern: true),                      T, null),
				("extern",             W(),                                    F, null),
				("extension",          W(isExtension: true),                   T, null),
				("extension",          W(),                                    F, null),
				("generated",          W(isGenerated: true),                   T, null),
				("generated",          W(),                                    F, null),
				("project",            W(),                                    T, null),
				("project",            W(isProject: false),                    F, null),
				("not project",        W(isProject: false),                    T, null),
				("async",              W(isAsync: true),                       T, null),
				("async",              W(),                                    F, null),
				("public static",      W(isStatic: true),                      T, null),
				("public static",      W(),                                    F, null),
				("public static",      W(Accessibility.Internal, isStatic: true), F, null),

				// Whether a call is awaitable can be for the call to say.
				("async",              new RecordFacts("Shop.C", "M") { IsAsyncDecidedByCall = true },                 F, D),
				("async",              new RecordFacts("Shop.C", "M") { IsAsync = true, IsAsyncDecidedByCall = true }, T, D),
				("not async",          new RecordFacts("Shop.C", "M") { IsAsyncDecidedByCall = true },                 T, D),
			]);
		}

		[TestMethod]
		public void CompactSignatureIsItsConditionsTest()
		{
			const string Token = "System.Threading.CancellationToken";

			static RecordFacts Save(string returns, params string[] parameters)
			{
				return new RecordFacts("Shop.Services.OrderService", "SaveOrder", returns, parameters);
			}

			Check(
			[
				("public **.*Service.Save*(.., CancellationToken) : Task*", Save("System.Threading.Tasks.Task<int>", "int", Token), T, null),
				("public **.*Service.Save*(.., CancellationToken) : Task*", Save("System.Threading.Tasks.Task<int>", "int"),        F, null),
				("public **.*Service.Save*(.., CancellationToken) : Task*", Save("int", Token),                                     F, null),
				("Save*",                                                    Save("void"),                                           T, null),
				("Load*",                                                    Save("void"),                                           F, null),
				("SaveOrder()",                                              Save("void"),                                           T, null),
				("SaveOrder()",                                              Save("void", "int"),                                    F, null),
				("SaveOrder: int",                                           Save("int"),                                            T, null),
				("SaveOrder: int",                                           Save("void"),                                           F, null),
				("SaveOrder(int): Task",                                     Save("System.Threading.Tasks.Task", "int"),             T, null),
				("static Save*",                                             Save("void"),                                           F, null),
				("not Load*",                                                Save("void"),                                           T, null),
				("SaveOrder(int): int",                                      Save("!T", "int"),                                      F, D),
			]);
		}

		[TestMethod]
		public void FunctionPointersTest()
		{
			static RecordFacts P(TypeShape parameter)
			{
				return new RecordFacts(ShapeText.ParseNamed("Shop.C"), new NameSegment("M"), ShapeText.Parse("void"), [new ParameterShape(parameter, ParameterModifiers.None)]);
			}

			var intType  = ShapeText.Parse("int");
			var voidType = ShapeText.Parse("void");

			static FunctionPointerTypeShape Pointer(bool managed, string[] conventions, TypeShape @return, params (TypeShape Type, ParameterModifiers Modifiers)[] parameters)
			{
				return new FunctionPointerTypeShape(
					managed,
					[..conventions],
					[..parameters.Select(static p => new FunctionPointerParameterShape(p.Type, p.Modifiers))],
					new FunctionPointerParameterShape(@return, ParameterModifiers.None));
			}

			var managed   = Pointer(true,  [],                                  voidType, (intType, ParameterModifiers.None));
			var unmanaged = Pointer(false, [],                                  voidType, (intType, ParameterModifiers.None));
			var cdecl     = Pointer(false, ["Cdecl"],                           voidType, (intType, ParameterModifiers.None));
			var two       = Pointer(false, ["Cdecl", "SuppressGCTransition"],   voidType, (intType, ParameterModifiers.None));
			var byRef     = Pointer(true,  [],                                  intType,  (intType, ParameterModifiers.Ref));

			(string Filter, TypeShape Type, Tri Expected)[] rows =
			[
				("param: delegate*<int, void>",                                          managed,   T),
				("param: delegate* managed<int, void>",                                  managed,   T),
				("param: delegate*<int, void>",                                          unmanaged, F),
				("param: delegate* unmanaged<int, void>",                                unmanaged, T),
				("param: delegate* unmanaged<int, void>",                                cdecl,     F),
				("param: delegate* unmanaged[Cdecl]<int, void>",                         cdecl,     T),
				("param: delegate* unmanaged[Cdecl]<int, void>",                         two,       F),
				("param: delegate* unmanaged[Cdecl, SuppressGCTransition]<int, void>",   two,       T),
				("param: delegate* unmanaged[SuppressGCTransition, Cdecl]<int, void>",   two,       T),
				("param: delegate* unmanaged[Stdcall]<int, void>",                       cdecl,     F),
				("param: delegate*<*, void>",                                            managed,   T),
				("param: delegate*<string, void>",                                       managed,   F),
				("param: delegate*<int, int, void>",                                     managed,   F),
				("param: delegate*<void>",                                               managed,   F),
				("param: delegate*<int, int>",                                           managed,   F),
				("param: delegate*<ref int, int>",                                       byRef,     T),
				("param: delegate*<int, int>",                                           byRef,     T),
				("param: delegate*<out int, int>",                                       byRef,     F),

				// `*` is any type, a function pointer included; no name is one.
				("param: *",                                                             managed,   T),
				("param: **",                                                            managed,   F),
				("param: Action",                                                        managed,   F),
			];

			foreach (var (filter, type, expected) in rows)
				Assert.AreEqual(expected, FilterProbe.Answer(filter, P(type)), filter);
		}
	}
}
