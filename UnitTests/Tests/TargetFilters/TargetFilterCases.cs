using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>
	/// Hand-written filter texts. The constants are the inputs of the named pin tests; every constant is also part of
	/// the curated corpus (see <see cref="All"/>), together with the extra cases below.
	/// </summary>
	static class TargetFilterCases
	{
		// returns: a trailing ? after a type name is a nullable annotation and is dropped
		public const string ReturnsNullableString = "returns: string?";
		public const string ReturnsString         = "returns: string";
		public const string ReturnsSystemString   = "returns: System.String";

		// in a type pattern, ? after an identifier is dropped ("Ta?g" reads as "Tag"), after * it stays a wildcard
		public const string AttributeQuestionMark = "attributes: Ta?g";
		public const string AttributePlain        = "attributes: Tag";
		public const string ReturnsStarQuestion   = "returns: Task*?";
		public const string ReturnsStar           = "returns: Task*";

		// in a segment pattern (method:) ? is a wildcard
		public const string MethodQuestionMark    = "method: Sa?e";
		public const string MethodStarQuestion    = "method: *?";
		public const string MethodStar            = "method: *";

		// a trailing escaped star after a leading wildcard is a literal star: the name ends with "a*"
		public const string MethodContainsEscaped = @"method: *a\*";
		public const string MethodContainsA       = "method: *a*";

		public const string PublicPrivate         = "public private";
		public const string PublicOnly            = "public";
		public const string SaveWithReturn        = "Save : System.Void";
		public const string PublicSaveWithReturn  = "public Save* : System.Void";
		public const string Misspelled            = "publik Save";

		public const string KeyUpperCase          = "METHOD: Save";
		public const string KeyLowerCase          = "method: Save";
		public const string KeyMixedCase          = "Method: Save";
		public const string RegexPrefixUpperCase  = "REGEX: Sav";
		public const string RegexPrefixLowerCase  = "regex: Sav";
		public const string ContainsPrefixUpper   = "CONTAINS: Save";
		public const string PatternPrefixUpper    = "PATTERN: Save";
		public const string PatternPrefixLower    = "pattern: Save";
		public const string ModifierUpperCase     = "PUBLIC Save";
		public const string ModifierLowerCase     = "public Save";
		public const string ModifierOnlyUpperCase = "Public Static";
		public const string ModifierOnlyLowerCase = "public static";

		public const string ExcludeAndPrefix      = "-& method: Save";
		public const string ExcludeOrPrefix       = "-| method: Save";

		public const string PatternStandalone     = "pattern: namespace: Acme.Core; method: Save";
		public const string ConditionEquivalent   = "namespace: Acme.Core; method: Save";
		public const string PatternThenBar        = "namespace: Acme.Core\npattern: method: Save\n| method: Load";
		public const string ConditionThenBar      = "namespace: Acme.Core\n| method: Load";

		public const string ParamsWithoutParens   = "params: ..., *CancellationToken";
		public const string ParamsWithParens      = "params: (..., *CancellationToken)";

		public const string SingleName            = "Save";
		public const string SingleNameExpanded    = "**.Save";

		public const string FinalStarNamespace    = "namespace: Acme.**";
		public const string FinalStarFullType     = "fulltype: Acme.Core.**";
		public const string FinalStarCompact      = "Acme.Core.**";

		public const string LeadingAnd            = "& method: Save";
		public const string LeadingOr             = "| method: Save";
		public const string LeadingAndCompact     = "& Save";
		public const string LeadingOrAfterRegex   = "namespace: Acme.Core\nregex: ^public\n| method: Save";
		public const string LeadingOrAfterContains = "namespace: Acme.Core\ncontains: Save\n| method: Update*";

		public const string EmptyOperandOrTrailing = "method: Save |";
		public const string EmptyOperandOrLeading  = "method: | Save";
		public const string EmptyOperandAnd        = "method: Save &";
		public const string EmptyBodyDash          = "-";
		public const string EmptyBodyRegex         = "regex:";
		public const string EmptyBodyContains      = "contains:";

		public const string EllipsisTwice         = "Save(..., ...)";
		public const string EllipsisTwiceParams   = "params: ..., *CancellationToken, ...";
		public const string ThisParameter         = "Save(this int)";

		public const string ContainsSave          = "contains: Save";
		public const string ContainsLowerSave     = "contains: save";
		public const string ContainsUpperSave     = "contains: SAVE";
		public const string ContainsWithSeparators = "contains: Save|Load";

		public const string RegexUnanchored       = "regex: Sav";
		public const string RegexAnchored         = "regex: ^Sav";
		public const string RegexAnchoredSignature = "regex: ^public System\\.Void Acme\\.Core\\.Service\\.Save\\(\\)$";
		public const string RegexLowerCase        = "regex: sav";
		public const string RegexInvalid          = "regex: [";

		public const string LastMatchIncludes     = "namespace: Acme.Core\n- method: Save\nmethod: Save";
		public const string LastMatchExcludes     = "namespace: Acme.Core\nmethod: Save\n- method: Save";
		public const string ExcludeNotLast        = "- method: Save\nmethod: Save*";
		public const string ExcludeLast           = "method: Save*\n- method: Save";

		/// <summary>Cases without a named test of their own: syntax that the documentation describes.</summary>
		static readonly string[] _extra =
		[
			"",
			"   ",
			"# only a comment",
			"# comment\nSave\n# another",
			"\n\nSave\n\n",
			"Save\r\nLoad",
			"Save\rLoad",
			"  \t Save \t ",
			"static",
			"instance",
			"abstract",
			"virtual",
			"override",
			"sealed",
			"extern",
			"unsafe",
			"public",
			"private",
			"protected",
			"internal",
			"protected internal",
			"private protected",
			"public static",
			"public static virtual",
			"public virtual",
			"public sealed override",
			"public abstract",
			"public extern",
			"public unsafe",
			"internal static",
			"Save()",
			"Save(...)",
			"Save(_)",
			"Save(_, _)",
			"Save(int)",
			"Save(string?)",
			"Save(int?)",
			"Save(System.String)",
			"Save(string, int?, bool)",
			"Save(ref int)",
			"Save(out int)",
			"Save(in *)",
			"Save(params string[])",
			"Save(params *)",
			"Save(*, ..., *)",
			"Save(..., *CancellationToken)",
			"Save*(..., *CancellationToken)",
			"Save(List<*>)",
			"Save(Dictionary<string, List<int?>>)",
			"Save(*,*)",
			"Save(",
			"Save)",
			"Save(int) trailing",
			"Save(int) : void",
			"Save : void",
			"Save: void",
			"Save : ",
			"Save :: void",
			"public Save*",
			"public static Acme.Core.*Service.Save*",
			"protected internal Acme.**.*Service.Save*(..., *CancellationToken) : System.Threading.Tasks.Task*",
			"Acme.Core.Repository<*>.Get<*>(_)",
			"Acme.Core.Repository<string>.Get(int)",
			"Acme.Core.Pair<*,*>.TryGet(*, out *)",
			"Acme.Core.Outer<*>.Inner<*>.Run(*, *)",
			"Acme.Core.Outer.Inner.Deep.Run",
			"*.TryGet(System.String, out System.Int32) : System.Boolean",
			"**.*Service.Save*",
			"**.Save**",
			"**",
			"**.**",
			"*",
			"*.*",
			"A.B.C.D.E.F.G.*",
			"A.**.G.Run",
			"GlobalService.Run",
			"Run",
			"Load<*>",
			"Load<T>",
			"Load<*,*>",
			"Load<T, U>",
			"Sa\\*ve",
			"Sa\\?ve",
			"Save\\.Item",
			"Save\\\\",
			"Save\\",
			"S?ve",
			"S*e",
			"*Async",
			"*ave*",
			"System.Math.Max",
			"System.Math.Max(int, int)",
			"System.Linq.Enumerable.Count<*>",
			"System.String.Format(string, params object[])",
			"System.**.Max",
			"Acme.Core.Service.Save",
			"Acme.Core.Service.",
			".Save",
			"Save.",
			"..",
			"Acme..Save",
			"method: Save",
			"method:Save",
			"method : Save",
			"method:",
			"method: Save*",
			"method: *Async",
			"method: Save* & *Async | Update* & *Async",
			"method: Save | Load | Run",
			"method: Save & Load",
			"method: Save\\|Item",
			"method: Save\\&Item",
			"method: Save|",
			"method: |",
			"method: &",
			"method: Save && Load",
			"method: Save || Load",
			"type: Service",
			"type: *Service",
			"type: Repository<*>",
			"type: Repository<string>",
			"type: Outer<*>",
			"path: Acme.Core.Service",
			"fulltype: Acme.Core.Service",
			"fulltype: **.Outer<*>.Inner<*>",
			"fulltype:Acme.Core.Service; method:Save; returns:System.String",
			"fullmethod: Acme.Core.Service.Save",
			"fullmethod: **.Save",
			"fullmethod: Acme.**",
			"namespace: Acme.Core",
			"namespace: Acme.*",
			"namespace: **",
			"namespace: A.B.C.D.E.F.G",
			"namespace: NoSuchNamespace.*",
			"namespace:",
			"namespace: Acme.Core; type: Service; method: Save*",
			"namespace: Acme.Core; method: Save; public",
			"namespace: Acme.Core; public static",
			"public; method: Save",
			"public static; method: Run",
			"returns: System.Void",
			"returns: void",
			"returns: int?",
			"returns: System.Nullable<*>",
			"returns: List<*>",
			"returns: System.Collections.Generic.List<*>",
			"returns: *[]",
			"returns: int[]",
			"returns: System.Int32[]",
			"returns: (int, string)",
			"returns: System.ValueTuple<*,*>",
			"returns: *Task*",
			"returns: System.Threading.Tasks.Task<*>",
			"returns: System.Threading.Tasks.ValueTask*",
			"returns: **",
			"returns: *?",
			"returns: S?ring",
			"returns:",
			"param: int",
			"param: out System.Int32",
			"param: out int",
			"param: ref *",
			"param: in *",
			"param: params *",
			"param: this int",
			"param: _",
			"param: *CancellationToken & *DbConnection",
			"params: ...",
			"params: ",
			"params: _",
			"params: _, _",
			"params: out int",
			"params: (...)",
			"params: ()",
			"params: (int)",
			"params: int, ...",
			"params: ..., int",
			"params: ..., ...",
			"params: *, *, *, *",
			"params: (string, int?, bool)",
			"attributes: Marker",
			"attributes: MarkerAttribute",
			"attributes: MarkerAttribute|Marker",
			"attributes: Tag | Marker",
			"attributes: GenericMarker",
			"attributes: GenericMarkerAttribute<*>",
			"attributes: GenericMarker<int>",
			"attributes: System.ObsoleteAttribute",
			"attributes: System.Obsolete",
			"attributes: *",
			"attribute: Marker",
			"-attributes: Marker",
			"-attributes: NoLog",
			"- attributes: MyCompany.Diagnostics.NoLogAttribute",
			"signature: public * Acme.*",
			"signature: *Save*",
			"signature: public System.Void Acme.Core.Service.Save()",
			"signature: *(System.Int32)",
			"signature:",
			"foo: bar",
			"foo: bar; method: Save",
			"foo:",
			":",
			": Save",
			"a b: c",
			"1: 2",
			"method: Save; foo: bar",
			"method: Save;",
			";",
			";;",
			"; method: Save",
			"method: Save;; type: Service",
			"pattern: Save",
			"pattern: public Save*(...)",
			"pattern: namespace: Acme.Core",
			"pattern:",
			"pattern: ",
			"- pattern: Save",
			"contains: Save",
			"contains: Acme.Core.Service",
			"contains: out ",
			"contains: ;",
			"contains: |",
			"contains: &",
			"contains: ",
			"- contains: Save",
			"- contains: Acme",
			"regex: ^public .* Acme\\.Core\\..*\\.Save.*\\(.*\\)$",
			"regex: ^public static ",
			"regex: Save(Async)?\\(",
			"regex: (",
			"regex: *",
			"regex: ",
			"regex: .*",
			"- regex: Async",
			"- regex: ^private",
			"RegEx: Save",
			"-Regex: Save",
			"unknown: Save",
			"- unknown: Save",
			"public Save; method: Run",
			"-",
			"- ",
			"--Save",
			"- -Save",
			"-Save",
			"&",
			"|",
			"&&",
			"||",
			"& ",
			"| ",
			"-&",
			"-|",
			"&-method: Save",
			"namespace: Acme.Core\n& type: Service\n& method: Save",
			"namespace: Acme.Core\nnamespace: Lib.Tasks\nmethod: Save*\nmethod: Start",
			"method: Save*\n& method: *Async",
			"method: Save*\n& method: *Async\nmethod: Update*",
			"namespace: Acme.Core\n& type: Service\n& method: Save*\n\n| namespace: Lib.Tasks\n& type: Worker\n& method: Start*",
			"- method: Ping\n| method: Run",
			"- method: Ping\n| method: Run\nmethod: Save",
			"method: Save\n- method: Ping\n| method: Run",
			"| method: Save\n& type: Service",
			"namespace: Acme.Core\n| method: Save\n| method: Load",
			"namespace: Acme.Core\n- method: Save\n| method: Load",
			"namespace: Acme.Core\n| - method: Save",
			"namespace: Acme.Core\n-| method: Save",
			"namespace: Acme.Core\n& Save",
			"namespace: Acme.Core\n& public",
			"namespace: Acme.Core\n& public static",
			"namespace: Acme.Core\nSave",
			"namespace: Acme.Core\nSave\n| method: Load",
			"namespace: Acme.Core\nregex: Save\n& method: Load",
			"namespace: Acme.Core\nregex: Save\n| method: Load",
			"namespace: Acme.Core\nfoo: bar\n| method: Save",
			"foo: bar\nnamespace: Acme.Core\n| method: Save",
			"namespace: Acme.Core\n\n\n| method: Save",
			"namespace: Acme.Core\n# comment\n| method: Save",
			"public static\nmethod: Run",
			"method: Run\npublic static",
			"public\nstatic",
			"public\n| static",
			"Acme.**.Save\nLoad\nacme.core.service.save",
			"acme.core.service.save",
			"ACME.CORE.SERVICE.SAVE",
			"METHOD: SAVE",
			"method: save",
			"Save*\n- Save\nSave",
			"-Save*\nSave",
			"Save\n-Save\n-Save",
			"Save\nSave\nSave",
			"method: Save\nmethod: Save",
			"- method: Save\n- method: Save",
			"namespace: Acme.Core\n- namespace: Acme.Core",
			"namespace: Acme.Core\n- type: Service\ntype: Service",
			"**.*\n- **.*",
			"- **.*\n**.*",
			"**.*",
			"**.*(...)",
			"public **.*(...)",
			"public **.*()",
			"public **.*(...) : **",
			"private **.*",
			"protected **.*",
			"internal **.*",
			"protected internal **.*",
			"private protected **.*",
			"public protected **.*",
			"static **.*",
			"instance **.*",
			"unsafe **.*",
			"sealed **.*",
			"abstract **.*",
			"virtual **.*",
			"override **.*",
			"extern **.*",
			"static extern **.*",
			"public static override **.*",
			"public abstract virtual **.*",
		];

		public static IReadOnlyList<string> Named { get; } = typeof(TargetFilterCases)
			.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Where(static f => f.IsLiteral && f.FieldType == typeof(string))
			.OrderBy(static f => f.MetadataToken)
			.Select(static f => (string)f.GetRawConstantValue()!)
			.ToArray();

		/// <summary>The named cases followed by the extra ones.</summary>
		public static IReadOnlyList<string> All { get; } = Named.Concat(_extra).Distinct(StringComparer.Ordinal).ToArray();
	}
}
