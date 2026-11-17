using System;
using System.Linq;

using AspectGenerator.FilterV2;

using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.FilterV2
{
	/// <summary>
	/// The version 2 grammar, one production at a time: what each reads, what it refuses and with which diagnostic, and
	/// where its nodes stand. The trees print as <see cref="FilterV2Print"/> writes them.
	/// </summary>
	[TestClass]
	public class FilterV2GrammarTests
	{
		static FilterItemReading Read(string text)
		{
			return FilterV2Reader.Read(text);
		}

		/// <summary>The text reads without a diagnostic, into the tree printed as <paramref name="expected"/>.</summary>
		static FilterItemSyntax Reads(string text, string expected)
		{
			var reading = Read(text);

			Assert.AreEqual(0, reading.Diagnostics.Length, $"'{text}': {string.Join("; ", reading.Diagnostics)}");
			Assert.IsNotNull(reading.Item);
			Assert.AreEqual(expected, FilterV2Print.Item(reading.Item), text);

			return reading.Item;
		}

		/// <summary>The text is reported with <paramref name="id"/> on <paramref name="at"/>, and with nothing else.</summary>
		static FilterDiagnostic Reports(string text, string id, string at, string? printed = null)
		{
			var reading = Read(text);

			Assert.AreEqual(1, reading.Diagnostics.Length, $"'{text}': {string.Join("; ", reading.Diagnostics)}");

			var diagnostic = reading.Diagnostics[0];

			Assert.AreEqual(id, diagnostic.Id, $"'{text}': {diagnostic}");
			Assert.AreEqual(at, Text(text, diagnostic.Span), $"'{text}': {diagnostic}");

			if (printed is not null)
				Assert.AreEqual(printed, FilterV2Print.Item(reading.Item!), text);

			return diagnostic;
		}

		static string Text(string text, TextSpan span)
		{
			return text.Substring(span.Start, span.Length);
		}

		static T Only<T>(FilterItemSyntax item)
			where T : FilterExpressionSyntax
		{
			return (T)item.Lines.Single().Expression;
		}

		// ---- an item -----------------------------------------------------------------------------------------------

		[TestMethod]
		public void ItemTest()
		{
			Reads("", "");
			Reads("   ", "");
			Reads("# only a comment", "");
			Reads("\n\n  # comment\r\n\t\n", "");

			var item = Reads("\n  namespace: Shop.**\n\n- method: *Internal*  # not these\n", "Namespace: Shop.** | - Method: *Internal*");

			Assert.AreEqual(2, item.Lines.Length);
			Assert.IsFalse(item.Lines[0].IsSubtraction);
			Assert.IsTrue(item.Lines[1].IsSubtraction);
		}

		[TestMethod]
		public void LinesRecoverTest()
		{
			// A bad line is stepped over to its logical end; the lines around it are read.
			var text    = "method: Save*\npublik Save\n- public";
			var reading = Read(text);

			Assert.AreEqual("Method: Save* | !Syntax | - [Public]", FilterV2Print.Item(reading.Item!));
			Assert.AreEqual("publik Save", Text(text, reading.Item!.Lines[1].Span));
			Assert.AreEqual(FilterV2DiagnosticID.SyntaxError, reading.Diagnostics.Single().Id);
			Assert.AreEqual("Save", Text(text, reading.Diagnostics.Single().Span));

			// Every bad line is reported.
			Assert.AreEqual(3, Read("a b c\nd e f\ng h i").Diagnostics.Length);

			// The logical end counts brackets and quotes, and a line continued with `or` belongs to the bad line.
			Reports("method: Save* and (public\n or bad bad)\nstatic", FilterV2DiagnosticID.SyntaxError, "bad)", "!Syntax | [Static]");
			Reports("bad bad\n or public\nstatic", FilterV2DiagnosticID.SyntaxError, "bad", "!Syntax | [Static]");

			// A comment inside brackets ends at its line: brackets and quotes in it are not counted.
			foreach (var comment in new[] { "# (", "# \"", "# )(", "# \"(\"" })
			{
				var bad = Read($"(public {comment}\n and bad bad)\nmethd: X\ntype: Task<>");

				CollectionAssert.AreEqual(
					new[] { FilterV2DiagnosticID.SyntaxError, FilterV2DiagnosticID.UnknownOrReserved, FilterV2DiagnosticID.SyntaxError },
					bad.Diagnostics.Select(static d => d.Id).ToArray(),
					$"{comment}: {string.Join("; ", bad.Diagnostics)}");
				Assert.AreEqual("!Syntax | !UnknownKey | Type: Task<!OpenGeneric>", FilterV2Print.Item(bad.Item!), comment);
			}

			// An unclosed bracket or quote runs to the end of the item: nothing after it is read.
			Reports("public\n(unclosed\nstatic", FilterV2DiagnosticID.SyntaxError, "static", "[Public] | !Syntax");
			Reports("method: \"unclosed\nstatic", FilterV2DiagnosticID.SyntaxError, "", "!Syntax");
		}

		[TestMethod]
		public void LineItemTest()
		{
			// A line ends at a line break, a comment or the end of the item; blank and comment lines between are nothing.
			Reads("public # a comment", "[Public]");
			Reads("public\r\nstatic\rinternal\nprivate", "[Public] | [Static] | [Internal] | [Private]");
			Reads("public\n\n# c\n\nstatic", "[Public] | [Static]");
		}

		[TestMethod]
		public void LeadingOperatorTest()
		{
			Reports("and public", FilterV2DiagnosticID.LeadingOperator, "and", "!LeadingOperator [Public]");
			Reports("  or public", FilterV2DiagnosticID.LeadingOperator, "or", "!LeadingOperator [Public]");
			Reports("\nand\npublic", FilterV2DiagnosticID.LeadingOperator, "and", "!LeadingOperator [Public]");

			// A name that begins with the letters is no operator.
			Reads("android", "[sig android]");
		}

		// ---- a line ------------------------------------------------------------------------------------------------

		[TestMethod]
		public void LineTest()
		{
			Reads("public", "[Public]");
			Reads("- public", "- [Public]");
			Reads("-public", "- [Public]");
			Reads("- method: X or public", "- or{Method: X, [Public]}");
		}

		[TestMethod]
		public void MinusTest()
		{
			var text = "  -  method: Save*";
			var line = Reads(text, "- Method: Save*").Lines[0];

			Assert.AreEqual("-", Text(text, line.Minus!.Value));
			Assert.AreEqual("-  method: Save*", Text(text, line.Span));
			Reports("-", FilterV2DiagnosticID.SyntaxError, "");
			Reports("- - public", FilterV2DiagnosticID.SyntaxError, "- public");
		}

		[TestMethod]
		public void LegacyLineOperatorTest()
		{
			var diagnostic = Reports("& method: Save*", FilterV2DiagnosticID.Legacy, "& method: Save*", "!Legacy");

			StringAssert.Contains(diagnostic.Message, "'and'");
			StringAssert.Contains(Reports("| method: Save*", FilterV2DiagnosticID.Legacy, "| method: Save*").Message, "'or'");
		}

		// ---- expressions -------------------------------------------------------------------------------------------

		[TestMethod]
		public void OrTest()
		{
			Reads("public or internal or private", "or{[Public], [Internal], [Private]}");
			Reads("method: Save*\nor method: Get*", "or{Method: Save*, Method: Get*}");
			Reads("method: Save* # saves\n\n  # and the gets\nor method: Get*", "or{Method: Save*, Method: Get*}");

			var text = "a or b";
			var or   = Only<OrExpressionSyntax>(Reads(text, "or{[sig a], [sig b]}"));

			Assert.AreEqual(text, Text(text, or.Span));
		}

		[TestMethod]
		public void AndTest()
		{
			// `not` > `and` > `or`.
			Reads("public and static or internal", "or{and{[Public], [Static]}, [Internal]}");
			Reads("public or static and internal", "or{[Public], and{[Static], [Internal]}}");
			Reads("method: Save*\n  and public", "and{Method: Save*, [Public]}");

			// A line break stands only before an operator, never after it.
			Reports("method: Save* and\npublic", FilterV2DiagnosticID.SyntaxError, "", "!Syntax | [Public]");
		}

		[TestMethod]
		public void AndOpLegacyTest()
		{
			Reports("method: Save*; static", FilterV2DiagnosticID.Legacy, ";", "and{Method: Save*, [Static]} !Legacy");
			Reports("method: Get* & Find*", FilterV2DiagnosticID.Legacy, "&");

			var diagnostic = Reports("method: Get*|Find*", FilterV2DiagnosticID.Legacy, "|");

			StringAssert.Contains(diagnostic.Message, "{Get,Find}*");
		}

		[TestMethod]
		public void UnaryTest()
		{
			Reads("not public", "not [Public]");
			Reads("not method: Save*", "not Method: Save*");
			Reads("not assembly.method", "not assembly->method");
			Reads("not public and not static", "and{not [Public], not [Static]}");

			// `not` stands before a single condition only.
			Reports("not (public or internal)", FilterV2DiagnosticID.SyntaxError, "not (public or internal)", "!NotBeforeGroup((or{[Public], [Internal]}))");
			Reports("not not public", FilterV2DiagnosticID.SyntaxError, "not not public");

			// What a refused `not` stands before is still read and reported.
			var reading = Read("not (methd: x)");

			CollectionAssert.AreEqual(new[] { FilterV2DiagnosticID.SyntaxError, FilterV2DiagnosticID.UnknownOrReserved }, reading.Diagnostics.Select(static d => d.Id).ToArray());

			// A run of words or a compact signature is several conditions.
			var several = Reports("not public static", FilterV2DiagnosticID.SyntaxError, "not public static");

			StringAssert.Contains(several.Message, "'not public and not static'");
			StringAssert.Contains(several.Message, "'not public or not static'");
			Reports("not public Save*()", FilterV2DiagnosticID.SyntaxError, "not public Save*()");
			Reports("not Save*(int)", FilterV2DiagnosticID.SyntaxError, "not Save*(int)");

			// A compact signature of a name alone is one condition.
			Reads("not Save*", "not [sig Save*]");
		}

		[TestMethod]
		public void GroupTest()
		{
			Reads("(public)", "([Public])");
			Reads("((public))", "(([Public]))");
			Reads("(public or internal) and static", "and{(or{[Public], [Internal]}), [Static]}");

			// Inside parentheses a line break or a comment stands between any two tokens.
			Reads("method: Save* and (\n  public # a comment\n  or\n  internal\n)", "and{Method: Save*, (or{[Public], [Internal]})}");
			Reads("(method:\n Save*)", "(Method: Save*)");
			Reads("(public\nstatic)", "([Public Static])");

			// But not inside a pattern or a compact signature.
			Reports("(type: List<int,\n string>)", FilterV2DiagnosticID.SyntaxError, "");
			Reports("(params: (int,\n string))", FilterV2DiagnosticID.SyntaxError, "");
			Reports("(Save*\n(int))", FilterV2DiagnosticID.SyntaxError, "(int))");

			var text  = "(public)";
			var group = Only<GroupExpressionSyntax>(Reads(text, "([Public])"));

			Assert.AreEqual(text, Text(text, group.Span));
			Reports("(public", FilterV2DiagnosticID.SyntaxError, "");
			Reports("public)", FilterV2DiagnosticID.SyntaxError, ")");
		}

		// ---- conditions --------------------------------------------------------------------------------------------

		[TestMethod]
		public void AtomTest()
		{
			Reads("method: Save*", "Method: Save*");
			Reads("assembly.method", "assembly->method");
			Reads("public", "[Public]");
			Reads("Save*", "[sig Save*]");
			Reports("/Save.*/", FilterV2DiagnosticID.UnknownOrReserved, "/Save.*/");
			Reports("any", FilterV2DiagnosticID.InvalidAny, "any", "!InvalidAny");
			Reports("method: Save* or any", FilterV2DiagnosticID.InvalidAny, "any");
		}

		[TestMethod]
		public void KeyConditionTest()
		{
			var text      = "method : Save*";
			var condition = Only<KeyConditionSyntax>(Reads(text, "Method: Save*"));

			Assert.AreEqual(FilterKey.Method, condition.Key);
			Assert.AreEqual("method", Text(text, condition.KeySpan));
			Assert.AreEqual("Save*", Text(text, condition.Value.Span));
			Assert.AreEqual(text, Text(text, condition.Span));

			Reads("method:Save", "Method: Save");
		}

		[TestMethod]
		public void KeysTest()
		{
			Reads("namespace: Shop.**", "Namespace: Shop.**");
			Reads("type: *Service", "Type: *Service");
			Reads("attribute: Obsolete", "Attribute: Obsolete");
			Reads("inherits: DataConnection", "Inherits: DataConnection");
			Reads("method: Save*", "Method: Save*");
			Reads("implements: ICommandHandler<*>.Handle", "Implements: ICommandHandler<*>.Handle");
			Reads("returns: Task<*>", "Returns: Task<*>");
			Reads("params: (int, ..)", "Params: (int, ..)");
			Reads("param: CancellationToken", "Param: CancellationToken");
		}

		[TestMethod]
		public void OverridesIsNoKeyOfItsOwnTest()
		{
			// Today's policy: `implements:` covers overriding, and `overrides:` is an unknown key that says so.
			// The switch is a constant: read it as a value, so that the assertion is about its value today.
			Assert.AreEqual(false, typeof(FilterV2Language).GetField(nameof(FilterV2Language.OverridesIsKey))!.GetValue(null));

			var reading = Read("overrides: Base.Save");

			Assert.AreEqual("Overrides: Base.Save", FilterV2Print.Item(reading.Item!));
			Assert.AreEqual(FilterV2DiagnosticID.UnknownOrReserved, reading.Diagnostics.Single().Id);
			StringAssert.Contains(reading.Diagnostics.Single().Message, "'implements:'");
			Assert.AreEqual("overrides", Text("overrides: Base.Save", reading.Diagnostics.Single().Span));
			Assert.AreEqual(FilterV2DiagnosticID.InvalidReference, Read("implements: assembly.overrides").Diagnostics.Single().Id);
			Assert.AreEqual(FilterV2DiagnosticID.InvalidReference, Read("assembly.overrides").Diagnostics.Single().Id);

			// The word stays a keyword: a name equal to it needs `@`.
			Reports("type: *.overrides", FilterV2DiagnosticID.SyntaxError, "overrides");
		}

		[TestMethod]
		public void OverridesAsAKeyOfItsOwnTest()
		{
			// The alternative policy: `overrides:` is a key of its own, and its references are valid.
			var reading = FilterV2Reader.Read("overrides: Base.Save", overridesIsKey: true);

			Assert.AreEqual("Overrides: Base.Save", FilterV2Print.Item(reading.Item!));
			Assert.AreEqual(0, reading.Diagnostics.Length, string.Join("; ", reading.Diagnostics));
			Assert.AreEqual(0, FilterV2Reader.Read("overrides: assembly.overrides", overridesIsKey: true).Diagnostics.Length);
			Assert.AreEqual(FilterV2DiagnosticID.InvalidReference, FilterV2Reader.Read("implements: assembly.overrides", overridesIsKey: true).Diagnostics.Single().Id);
		}

		[TestMethod]
		public void PropertyKeyTest()
		{
			foreach (var key in new[] { "accessibility", "static", "async", "virtuality", "extern", "extension", "location", "origin", "generated" })
			{
				var condition = Only<KeyConditionSyntax>(Read(key + ": any").Item!);

				Assert.IsInstanceOfType<AnyValueSyntax>(condition.Value, key);
				Assert.AreEqual(0, Read(key + ": any").Diagnostics.Length, key);
				Assert.AreEqual(0, Read($"{key}: assembly.{key}").Diagnostics.Length, key);

				// A property written with words takes only `any` or a reference.
				Reports(key + ": Save*", FilterV2DiagnosticID.UnknownOrReserved, key + ": Save*", "!WrongValue");
			}
		}

		[TestMethod]
		public void LegacyKeyTest()
		{
			StringAssert.Contains(Reports("contains: Save", FilterV2DiagnosticID.Legacy, "contains: Save", "!Legacy").Message, "removed");
			StringAssert.Contains(Reports("regex: ^Save.*$", FilterV2DiagnosticID.Legacy, "regex: ^Save.*$").Message, "removed");
			StringAssert.Contains(Reports("pattern: public Save*", FilterV2DiagnosticID.Legacy, "pattern: public Save*").Message, "'public Save*'");
			StringAssert.Contains(Reports("signature: public Save*", FilterV2DiagnosticID.Legacy, "signature: public Save*").Message, "'public Save*'");
			StringAssert.Contains(Reports("fulltype: Shop.OrderService", FilterV2DiagnosticID.Legacy, "fulltype: Shop.OrderService").Message, "'type: global::Shop.OrderService'");
			StringAssert.Contains(Reports("path: Shop.OrderService", FilterV2DiagnosticID.Legacy, "path: Shop.OrderService").Message, "'type: global::Shop.OrderService'");
			StringAssert.Contains(Reports("fullmethod: Shop.Order.Save", FilterV2DiagnosticID.Legacy, "fullmethod: Shop.Order.Save").Message, "'method: global::Shop.Order.Save'");
			StringAssert.Contains(Reports("attributes: Obsolete", FilterV2DiagnosticID.Legacy, "attributes: Obsolete").Message, "'attribute: Obsolete'");
		}

		[TestMethod]
		public void LegacyKeyInParenthesesTest()
		{
			// The value of a 1.1 key ends where the bracket around it closes.
			foreach (var key in new[] { "contains", "regex", "pattern", "fulltype", "fullmethod", "path", "attributes", "signature" })
			{
				Reports($"({key}: C.Save)", FilterV2DiagnosticID.Legacy, $"{key}: C.Save", "(!Legacy)");
				Reports($"public and ({key}: C.Save(int)) and static", FilterV2DiagnosticID.Legacy, $"{key}: C.Save(int)", "and{[Public], (!Legacy), [Static]}");
				Reports($"(public or\n {key}: Save # )\n)", FilterV2DiagnosticID.Legacy, $"{key}: Save", "(or{[Public], !Legacy})");
			}

			Reports("(within: Shop)", FilterV2DiagnosticID.UnknownOrReserved, "within: Shop", "(!Reserved)");
		}

		[TestMethod]
		public void CasedKeyTest()
		{
			// A key in another case is 1.1's spelling: keys are lowercase now.
			StringAssert.Contains(Reports("Method: Save*", FilterV2DiagnosticID.Legacy, "Method", "!Legacy").Message, "'method:'");
			StringAssert.Contains(Reports("NAMESPACE: Shop.**", FilterV2DiagnosticID.Legacy, "NAMESPACE").Message, "'namespace:'");
			Reports("Contains: Save", FilterV2DiagnosticID.Legacy, "Contains");
			Reports("(Type : *Service)", FilterV2DiagnosticID.Legacy, "Type", "(!Legacy)");
			StringAssert.Contains(Reports("Returns: Task", FilterV2DiagnosticID.Legacy, "Returns").Message, "'@Returns: …'");

			// A method of that name is written with `@` or a parameter list.
			Reads("@Method: Save*", "[sig Method : Save*]");
			Reads("Method(): Save*", "[sig Method() : Save*]");
			Reads("Methods: Save*", "[sig Methods : Save*]");
		}

		[TestMethod]
		public void ReservedKeyTest()
		{
			Reports("within: Shop", FilterV2DiagnosticID.UnknownOrReserved, "within: Shop", "!Reserved");
			Reports("assembly: Shop", FilterV2DiagnosticID.UnknownOrReserved, "assembly: Shop", "!Reserved");
		}

		[TestMethod]
		public void UnknownKeyTest()
		{
			var diagnostic = Reports("methd: Save*", FilterV2DiagnosticID.UnknownOrReserved, "methd", "!UnknownKey");

			StringAssert.Contains(diagnostic.Message, "'@methd: …'");
			Reports("custom: x and public", FilterV2DiagnosticID.UnknownOrReserved, "custom", "and{!UnknownKey, [Public]}");

			// A key is a lowercase identifier directly before `:`; anything else is a compact signature.
			Reads("Save: Task", "[sig Save : Task]");
			Reads("@save: Task", "[sig save : Task]");
			Reads("save(): Task", "[sig save() : Task]");

			// Any identifier with no uppercase letter is a key: digits, `_` and letters outside ASCII too.
			Reports("method2: X", FilterV2DiagnosticID.UnknownOrReserved, "method2", "!UnknownKey");
			Reports("foo_bar: Task", FilterV2DiagnosticID.UnknownOrReserved, "foo_bar");
			Reports("méthod: X", FilterV2DiagnosticID.UnknownOrReserved, "méthod");
			Reports("_x: y", FilterV2DiagnosticID.UnknownOrReserved, "_x");
			Reads("fooBar: Task", "[sig fooBar : Task]");

			// The key's span is the key: white space before `:` is not part of it.
			var text = "methd : X";

			Assert.AreEqual(new TextSpan(0, 5), Reports(text, FilterV2DiagnosticID.UnknownOrReserved, "methd").Span);
		}

		[TestMethod]
		public void ValuesTest()
		{
			// NameValue, TypeValue, ReturnsValue, ParamsValue, ParamValue: `any`, a reference, or the key's pattern.
			Reads("namespace: any", "Namespace: any");
			Reads("type: any", "Type: any");
			Reads("returns: any", "Returns: any");
			Reads("params: any", "Params: any");
			Reads("param: any", "Param: any");
			Reads("method: assembly.method", "Method: assembly->method");
			Reads("returns: aspect.returns", "Returns: aspect->returns");
			Reads("params: assembly.params", "Params: assembly->params");
			Reads("param: assembly.param", "Param: assembly->param");
			Reads("type: assembly.location", "Type: assembly->location");

			// The wrong kind of pattern for the key.
			StringAssert.Contains(Reports("params: int", FilterV2DiagnosticID.UnknownOrReserved, "params: int", "!WrongValue").Message, "'param: …'");
			Reports("params: int and public", FilterV2DiagnosticID.UnknownOrReserved, "params: int", "and{!WrongValue, [Public]}");
			Reports("method:", FilterV2DiagnosticID.SyntaxError, "");
		}

		[TestMethod]
		public void CommonValueTest()
		{
			// `any` is a whole value; a name that begins with it is a name.
			Reads("method: anyThing", "Method: anyThing");
			Reads("method: any*", "Method: any*");
			Reads("method: @any", "Method: any");
			Reports("- method: any", FilterV2DiagnosticID.InvalidAny, "any");
			Reports("not method: any", FilterV2DiagnosticID.InvalidAny, "any");
		}

		[TestMethod]
		public void ReferenceTest()
		{
			var text      = "method: Save* and assembly.method";
			var and       = Only<AndExpressionSyntax>(Reads(text, "and{Method: Save*, assembly->method}"));
			var reference = ((ReferenceExpressionSyntax)and.Operands[1]).Reference;

			Assert.AreEqual(FilterLevel.Assembly, reference.Level);
			Assert.AreEqual("assembly", Text(text, reference.LevelSpan));
			Assert.AreEqual("method", Text(text, reference.TargetSpan));
			Assert.AreEqual("assembly.method", Text(text, reference.Span));

			Reads("aspect.attribute", "aspect->attribute");
			Reads("- assembly.method", "- assembly->method");
			Reads("attribute: Audited and aspect.attribute", "and{Attribute: Audited, aspect->attribute}");

			// A reference names a property, and the one it stands in.
			StringAssert.Contains(Reports("assembly.Save*", FilterV2DiagnosticID.InvalidReference, "Save*").Message, "'@assembly.Save*'");
			StringAssert.Contains(Reports("method: assembly.returns", FilterV2DiagnosticID.InvalidReference, "returns").Message, "'assembly.method'");
			StringAssert.Contains(Reports("type: assembly.namespace", FilterV2DiagnosticID.InvalidReference, "namespace").Message, "'assembly.location'");
			StringAssert.Contains(Reports("assembly.type", FilterV2DiagnosticID.InvalidReference, "type").Message, "'assembly.location'");

			// No white space around the dot; a name that begins with a level is written with `@`.
			Reports("assembly .method", FilterV2DiagnosticID.SyntaxError, ".method");
			Reads("@assembly.Save*", "[sig assembly.Save*]");
		}

		[TestMethod]
		public void WordsOrSignatureTest()
		{
			var text  = "public static Save*(int) : Task";
			var words = Only<WordRunSyntax>(Reads(text, "[Public Static sig Save*(int) : Task]"));

			Assert.AreEqual(2, words.Words.Length);
			Assert.AreEqual("static", Text(text, words.Words[1].Span));
			Assert.AreEqual("Save*(int) : Task", Text(text, words.Signature!.Span));
			Assert.AreEqual(5, words.ConditionCount);

			Reads("public static async", "[Public Static Async]");
			Reads("Save*", "[sig Save*]");

			// A word misspelled is a name: `publik Save` is no condition.
			Reports("publik Save", FilterV2DiagnosticID.SyntaxError, "Save");
		}

		[TestMethod]
		public void WordTest()
		{
			var words = new (string Text, FilterWord Word)[]
			{
				("public", FilterWord.Public), ("internal", FilterWord.Internal), ("protected", FilterWord.Protected), ("private", FilterWord.Private),
				("protected internal", FilterWord.ProtectedInternal), ("internal protected", FilterWord.ProtectedInternal),
				("private protected", FilterWord.PrivateProtected), ("protected private", FilterWord.PrivateProtected),
				("static", FilterWord.Static), ("instance", FilterWord.Instance), ("async", FilterWord.Async), ("virtual", FilterWord.Virtual),
				("override", FilterWord.Override), ("abstract", FilterWord.Abstract), ("sealed", FilterWord.Sealed), ("extern", FilterWord.Extern),
				("extension", FilterWord.Extension), ("project", FilterWord.Project), ("generated", FilterWord.Generated),
			};

			foreach (var (text, word) in words)
			{
				var run = Only<WordRunSyntax>(Reads(text, $"[{word}]"));

				Assert.AreEqual(text, Text(text, run.Words.Single().Span));
			}

			// Keywords are lowercase and case-sensitive.
			Reads("Public", "[sig Public]");

			foreach (var reserved in new[] { "iterator", "unsafe", "partial", "readonly", "exposed" })
				Reports(reserved, FilterV2DiagnosticID.UnknownOrReserved, reserved, "[!Reserved]");

			Reports("public readonly", FilterV2DiagnosticID.UnknownOrReserved, "readonly", "[Public !Reserved]");
		}

		[TestMethod]
		public void SignatureTest()
		{
			Reads("public static **.*Service.Save*(.., CancellationToken) : Task*", "[Public Static sig **.*Service.Save*(.., CancellationToken) : Task*]");
			Reads("*Service.Save*(int)", "[sig *Service.Save*(int)]");
			Reads("Save* : Task<*>", "[sig Save* : Task<*>]");
			Reads("Save*(int, string):void", "[sig Save*(int, string) : void]");
			Reads("Save (int)", "[sig Save(int)]");
			Reads("global::MyApp.Save", "[sig global::MyApp.Save]");
			Reads("public Get*() : int?", "[Public sig Get*() : int?]");

			var text      = "Save* (int) : Task";
			var signature = Only<WordRunSyntax>(Reads(text, "[sig Save*(int) : Task]")).Signature!;

			Assert.AreEqual("Save*", Text(text, signature.Name.Span));
			Assert.AreEqual("(int)", Text(text, signature.Parameters!.Span));
			Assert.AreEqual("Task", Text(text, signature.Returns!.Span));
			Assert.AreEqual(text, Text(text, signature.Span));
		}

		[TestMethod]
		public void ReservedLiteralTest()
		{
			Reports("/Save.*/", FilterV2DiagnosticID.UnknownOrReserved, "/Save.*/", "!Reserved");
			Reports("public and /x/", FilterV2DiagnosticID.UnknownOrReserved, "/x/");
		}

		// ---- name patterns -----------------------------------------------------------------------------------------

		[TestMethod]
		public void NamePatternTest()
		{
			Reads("method: Save", "Method: Save");

			// `?` after a name is no nullable.
			var diagnostic = Reports("method: Get?", FilterV2DiagnosticID.InvalidPattern, "?", "Method: Get!QuestionOutsideType");

			StringAssert.Contains(diagnostic.Message, "'*'");
			Reports("Save?", FilterV2DiagnosticID.InvalidPattern, "?");
		}

		[TestMethod]
		public void DottedTest()
		{
			var text = "method: global::Shop.**.*Service.Save*";
			var name = (NamePatternSyntax)Only<KeyConditionSyntax>(Reads(text, "Method: global::Shop.**.*Service.Save*")).Value;

			Assert.AreEqual("global::", Text(text, name.Global!.Value));
			CollectionAssert.AreEqual(new[] { "Shop", "**", "*Service", "Save*" }, name.Segments.Select(s => Text(text, s.Span)).ToArray());
			Assert.IsTrue(name.Segments[1].IsGlobstar);

			Reads("method: Save.*", "Method: Save.*");
			Reports("method: Shop..Save", FilterV2DiagnosticID.SyntaxError, ".Save");
			Reports("method: Shop. Save", FilterV2DiagnosticID.SyntaxError, " Save");
		}

		[TestMethod]
		public void KeywordAsNameTest()
		{
			// A keyword where a name stands needs `@`.
			StringAssert.Contains(Reports("type: *.static", FilterV2DiagnosticID.SyntaxError, "static", "Type: *.!KeywordAsName").Message, "'@static'");
			Reports("method: Shop.and", FilterV2DiagnosticID.SyntaxError, "and");
			Reports("type: static", FilterV2DiagnosticID.SyntaxError, "static", "Type: !KeywordAsName");
			Reads("type: *.@static", "Type: *.static");
			Reads("method: @namespace.X", "Method: namespace.X");

			// A keyword followed by more of a name is a name; C#'s aliases are no keywords.
			Reads("method: public*", "Method: public*");
			Reads("type: *.staticFoo", "Type: *.staticFoo");
			Reads("param: int", "Param: int");
			Reads("returns: string", "Returns: string");
		}

		[TestMethod]
		public void GlobalTest()
		{
			Reads("type: global::Shop.OrderService", "Type: global::Shop.OrderService");
			Reads("namespace: global::MyApp.**", "Namespace: global::MyApp.**");
			Reports("type: global:: Shop", FilterV2DiagnosticID.SyntaxError, " Shop");
		}

		[TestMethod]
		public void SegmentTest()
		{
			Reads("namespace: Shop.**", "Namespace: Shop.**");
			Reads("method: **.Save", "Method: **.Save");

			// `**` is a whole segment; inside one it is refused.
			Reports("type: ***", FilterV2DiagnosticID.InvalidPattern, "**");
			Reports("method: Save**", FilterV2DiagnosticID.InvalidPattern, "**");
			Reports("method: a**b", FilterV2DiagnosticID.InvalidPattern, "**");
		}

		[TestMethod]
		public void PartTest()
		{
			var text    = "method: Get*Async";
			var segment = ((NamePatternSyntax)Only<KeyConditionSyntax>(Reads(text, "Method: Get*Async")).Value).Segments.Single();

			CollectionAssert.AreEqual(new[] { NamePartKind.Text, NamePartKind.Star, NamePartKind.Text }, segment.Parts.Select(static p => p.Kind).ToArray());
			CollectionAssert.AreEqual(new[] { "Get", "*", "Async" }, segment.Parts.Select(p => Text(text, p.Span)).ToArray());

			// `@` makes a keyword a name; the text is the name without it.
			Reads("method: @and", "Method: and");

			// Backslash escapes and `?` inside a name are refused.
			Reports(@"method: Sa\ve", FilterV2DiagnosticID.Legacy, @"\v", @"Method: Sa!Legacye");
			Reports("method: Ge?t", FilterV2DiagnosticID.InvalidPattern, "?");

			// Unicode letters, digits and connectors are name characters.
			Reads("method: Сохранить_2", "Method: Сохранить_2");
		}

		[TestMethod]
		public void NameAlternativesTest()
		{
			Reads("method: {Get,Find}*", "Method: {Get,Find}*");
			Reads("method: { Get , Find }*", "Method: {Get,Find}*");
			Reads("type: {Order,Invoice}Service", "Type: {Order,Invoice}Service");
			Reads("method: {Shop.Get,Find}", "Method: {Shop.Get,Find}");
			Reports("method: {}", FilterV2DiagnosticID.InvalidPattern, "{}", "Method: !EmptyAlternatives");
			Assert.AreEqual(FilterV2DiagnosticID.SyntaxError, Read("method: {Get,\nFind}").Diagnostics.First().Id);
		}

		[TestMethod]
		public void QuotedTest()
		{
			Reads("method: \"<Main>$\"", "Method: \"<Main>$\"");
			Reads("namespace: \"\"", "Namespace: \"\"");
			Reads("method: \"a\\\"b\\\\c\"", "Method: \"a\"b\\c\"");
			Reads("method: \"and\"", "Method: \"and\"");

			// Only `\"` and `\\` are escapes.
			Reports("method: \"a\\nb\"", FilterV2DiagnosticID.InvalidPattern, "\\n");

			// Quoted text stays on one line.
			Reports("method: \"a\nb\"", FilterV2DiagnosticID.SyntaxError, "");
		}

		[TestMethod]
		public void TypeArgsTest()
		{
			Reads("type: List<*>", "Type: List<*>");
			Reads("type: Dictionary<*,*>", "Type: Dictionary<*,*>");
			Reads("type: Dictionary< string , List<int> >", "Type: Dictionary<string,List<int>>");
			Reads("method: List<int>.Add", "Method: List<int>.Add");
			Reads("method: Get<*>", "Method: Get<*>");

			StringAssert.Contains(Reports("type: Task<>", FilterV2DiagnosticID.SyntaxError, "<>", "Type: Task<!OpenGeneric>").Message, "'<*>'");
			StringAssert.Contains(Reports("type: Dictionary<,>", FilterV2DiagnosticID.SyntaxError, "<,>").Message, "'<*,*>'");
			Reports("type: List<int", FilterV2DiagnosticID.SyntaxError, "");
			Assert.AreEqual(FilterV2DiagnosticID.SyntaxError, Read("type: List<int,\nstring>").Diagnostics.First().Id);
		}

		// ---- type patterns -----------------------------------------------------------------------------------------

		[TestMethod]
		public void TypePatternTest()
		{
			var text = "param: int?[][,]";
			var type = (TypePatternSyntax)((ParameterPatternSyntax)Only<KeyConditionSyntax>(Reads(text, "Param: int?[][,]")).Value).Type!;

			CollectionAssert.AreEqual(new[] { 0, 1, 2 }, type.Suffixes.Select(static s => s.Rank).ToArray());
			Assert.AreEqual("int?[][,]", Text(text, type.Span));
		}

		[TestMethod]
		public void TypeSuffixTest()
		{
			Reads("param: int[]", "Param: int[]");
			Reads("param: int[ , ]", "Param: int[,]");
			Reads("param: int[][,]", "Param: int[][,]");
			Reads("param: int?", "Param: int?");
			Reads("param: int[]?", "Param: int[]?");
			Reads("param: *?", "Param: *?");
			Reports("param: int??", FilterV2DiagnosticID.InvalidPattern, "?");
		}

		[TestMethod]
		public void TupleTest()
		{
			Reads("param: (int, string)", "Param: tuple(int,string)");
			Reads("param: (int, (string, bool))", "Param: tuple(int,tuple(string,bool))");
			Reads("returns: (int, *)[]", "Returns: tuple(int,*)[]");
		}

		[TestMethod]
		public void TypeAlternativesTest()
		{
			Reads("returns: {void,Task}", "Returns: {void,Task}");
			Reads("returns: {int[], string}", "Returns: either{int[],string}");
			Reads("param: {int, (int, int)}", "Param: either{int,tuple(int,int)}");

			// Alternatives that a name goes on after are alternatives of names.
			Reads("type: {Order,Invoice}Service<*>.Inner", "Type: {Order,Invoice}Service<*>.Inner");
			Reads("type: {A,{B,C}}D", "Type: {A,{B,C}}D");
			Reads("param: {int,string}[]", "Param: {int,string}[]");
			Reports("type: {int[],string}Foo", FilterV2DiagnosticID.InvalidPattern, "{int[],string}", "Type: !TypesInNameFoo");
			Reports("type: {}", FilterV2DiagnosticID.InvalidPattern, "{}", "Type: !EmptyAlternatives");

			var text  = "type: {Get,Find}Service";
			var name  = ((NamedTypeSyntax)((TypePatternSyntax)Only<KeyConditionSyntax>(Reads(text, "Type: {Get,Find}Service")).Value).Core).Name;
			var parts = name.Segments.Single().Parts;

			CollectionAssert.AreEqual(new[] { "{Get,Find}", "Service" }, parts.Select(p => Text(text, p.Span)).ToArray());
			Assert.AreEqual("{Get,Find}Service", Text(text, name.Segments[0].Span));
		}

		[TestMethod]
		public void FunctionPointerTest()
		{
			Reads("param: delegate*<int, void>", "Param: fnptr<int,void>");
			Reads("param: delegate* managed<List<int>, void>", "Param: fnptr managed<List<int>,void>");
			Reads("param: delegate* unmanaged<void>", "Param: fnptr unmanaged<void>");
			Reads("param: delegate* unmanaged[Cdecl]<int, int>", "Param: fnptr unmanaged[Cdecl]<int,int>");
			Reads("param: delegate*<ref int, out bool, in Guid, ref readonly int>", "Param: fnptr<ref int,out bool,in Guid,ref readonly int>");
			Reads("returns: delegate*<int, void>[]", "Returns: fnptr<int,void>[]");

			// The commas of the calling conventions do not split parameters.
			var text       = "params: (delegate* unmanaged[Stdcall, SuppressGCTransition]<int>, ..)";
			var list       = (ParameterListPatternSyntax)Only<KeyConditionSyntax>(Reads(text, "Params: (fnptr unmanaged[Stdcall,SuppressGCTransition]<int>, ..)")).Value;
			var pointer    = (FunctionPointerTypeSyntax)list.Parameters[0].Type!.Core;

			Assert.AreEqual(2, list.Parameters.Length);
			Assert.AreEqual("unmanaged", pointer.CallingConvention);
			CollectionAssert.AreEqual(new[] { "Stdcall", "SuppressGCTransition" }, pointer.UnmanagedConventions.ToArray());
			Assert.AreEqual("delegate* unmanaged[Stdcall, SuppressGCTransition]<int>", Text(text, list.Parameters[0].Span));

			// A name that begins with `delegate` is a name.
			Reads("type: delegate*", "Type: delegate*");
			Reads("type: Delegate", "Type: Delegate");
			Reports("param: delegate* unmanaged[]<int>", FilterV2DiagnosticID.SyntaxError, "]<int>");
		}

		[TestMethod]
		public void ReturnsTest()
		{
			Reads("returns: Task", "Returns: Task");
			Reads("returns: ref int", "Returns: ref int");
			Reads("returns: ref readonly Span<int>", "Returns: ref readonly Span<int>");

			var text    = "returns: ref readonly int";
			var returns = (ReturnsPatternSyntax)Only<KeyConditionSyntax>(Reads(text, "Returns: ref readonly int")).Value;

			Assert.AreEqual(FilterRefKind.RefReadonly, returns.RefKind);
			Assert.AreEqual("int", Text(text, returns.Type.Span));
			Reports("returns: ref", FilterV2DiagnosticID.SyntaxError, "");
		}

		[TestMethod]
		public void ParamListTest()
		{
			Reads("params: ()", "Params: ()");
			Reads("params: ( )", "Params: ()");
			Reads("params: (..)", "Params: (..)");
			Reads("params: (int, ..)", "Params: (int, ..)");
			Reads("params: (.., CancellationToken)", "Params: (.., CancellationToken)");
			Reads("params: (_, out bool)", "Params: (_, out bool)");
			Reads("params: ( int , string )", "Params: (int, string)");

			Reports("params: (.., int, ..)", FilterV2DiagnosticID.InvalidPattern, "..");
			Reports("params: (int", FilterV2DiagnosticID.SyntaxError, "");
		}

		[TestMethod]
		public void ParamTest()
		{
			Reads("param: _Foo", "Param: _Foo");
			Reads("params: (_)", "Params: (_)");
			Reads("param: this IEnumerable<*>", "Param: this IEnumerable<*>");

			var text      = "param: ref readonly int";
			var parameter = (ParameterPatternSyntax)Only<KeyConditionSyntax>(Reads(text, "Param: ref readonly int")).Value;

			Assert.AreEqual(ParameterPatternKind.Typed, parameter.Kind);
			CollectionAssert.AreEqual(new[] { FilterParameterModifier.RefReadonly }, parameter.Modifiers.ToArray());
			Assert.AreEqual(text.Length - "ref readonly int".Length, parameter.Span.Start);

			StringAssert.Contains(Reports("params: (int, ...)", FilterV2DiagnosticID.Legacy, "...", "Params: (int, !Legacy)").Message, "'..'");
		}

		[TestMethod]
		public void ParamModifierTest()
		{
			Reads("params: (ref int, ref readonly int, out bool, in Guid, params int[], this string)",
				"Params: (ref int, ref readonly int, out bool, in Guid, params int[], this string)");

			// A modifier is a whole word: `inner` is a type.
			Reads("param: inner", "Param: inner");
			Reads("param: ref  int", "Param: ref int");
		}

		// ---- the tree ----------------------------------------------------------------------------------------------

		[TestMethod]
		public void EveryNodeStandsWithinItsParentTest()
		{
			foreach (var text in new[]
			{
				"public static **.*Service.Save*(.., CancellationToken) : Task*\n- method: *Internal* and not (public)",
				"param: delegate* unmanaged[Stdcall, X]<ref int, void>[] or returns: {int, (int, string)}[]?",
				"attribute: Audited and aspect.attribute\n  or method: {Get,\"Find\"}<*>.Save",
			})
			{
				var item  = Read(text).Item!;
				var stack = new System.Collections.Generic.Stack<FilterSyntaxNode>();

				stack.Push(item);

				while (stack.Count > 0)
				{
					var node = stack.Pop();

					foreach (var child in node.Children)
					{
						Assert.IsTrue(node.Span.Contains(child.Span) || child.Span.IsEmpty && child.Span.Start == node.Span.End,
							$"'{text}': {child.GetType().Name} {child.Span} is outside {node.GetType().Name} {node.Span}");
						stack.Push(child);
					}
				}
			}
		}

		[TestMethod]
		public void DiagnosticsComeInTextOrderTest()
		{
			var text    = "methd: x\n- method: any\ntype: Task<>\nnot public static";
			var reading = Read(text);

			CollectionAssert.AreEqual(
				new[] { FilterV2DiagnosticID.UnknownOrReserved, FilterV2DiagnosticID.InvalidAny, FilterV2DiagnosticID.SyntaxError, FilterV2DiagnosticID.SyntaxError },
				reading.Diagnostics.Select(static d => d.Id).ToArray());
			CollectionAssert.AreEqual(new[] { "methd", "any", "<>", "not public static" }, reading.Diagnostics.Select(d => Text(text, d.Span)).ToArray());
		}
	}
}
