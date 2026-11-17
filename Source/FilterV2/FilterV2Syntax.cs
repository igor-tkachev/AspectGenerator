using System;
using System.Collections.Generic;
using System.Collections.Immutable;

using Microsoft.CodeAnalysis.Text;

namespace AspectGenerator.FilterV2
{
	// The syntax of a target filter, version 2, as the generated FilterV2Parser reads it from one item (a whole string, or
	// one element of a string[]). Every node carries the span it was read from, relative to the item.
	//
	// The tree holds what the text says, including what it says wrong: a form the language refuses is read by an error
	// production into a node that carries a FilterSyntaxError where the form stands, so that the reader reports it with
	// what to write instead. What needs more than one condition to decide (which property a line declares, whether a
	// property is included twice, which levels a reference may name in its block) is decided after this tree, by the
	// reader of declarations.

	/// <summary>Why a piece of a filter cannot be read: the diagnostic it is reported as.</summary>
	internal enum FilterSyntaxErrorKind
	{
		/// <summary>A line the grammar cannot read; the error says what was expected where reading stopped.</summary>
		Syntax,
		/// <summary><c>not</c> before parentheses or another <c>not</c>.</summary>
		NotBeforeGroup,
		/// <summary><c>not</c> before a run of words or a compact signature: several conditions.</summary>
		NotBeforeSeveral,
		/// <summary><c>Task&lt;&gt;</c>, <c>Dictionary&lt;,&gt;</c>.</summary>
		OpenGeneric,
		/// <summary>A keyword where a name stands: <c>type: *.static</c> (<c>*.@static</c>).</summary>
		KeywordAsName,
		/// <summary>A key nobody defined: <c>methd: Save*</c>.</summary>
		UnknownKey,
		/// <summary>A key, word or literal reserved for a later version.</summary>
		Reserved,
		/// <summary>A key whose value is of the wrong kind: <c>static: Save*</c>.</summary>
		WrongValue,
		/// <summary><c>**</c> inside a segment: <c>Save**</c>.</summary>
		GlobstarInSegment,
		/// <summary><c>..</c> twice in one parameter list.</summary>
		AnyRunTwice,
		/// <summary><c>?</c> that does not follow a type.</summary>
		QuestionOutsideType,
		/// <summary><c>{}</c>.</summary>
		EmptyAlternatives,
		/// <summary>Alternatives of types other than names that a name goes on after: <c>{int[], string}Foo</c>.</summary>
		TypesInName,
		/// <summary>A backslash in quoted text before anything but <c>"</c> or <c>\</c>.</summary>
		InvalidEscape,
		/// <summary>A form of the 1.1 language; the error gives the version 2 spelling.</summary>
		Legacy,
		/// <summary>A reading nested deeper than the parser can follow.</summary>
		NestedTooDeeply,
		/// <summary>An item that begins with <c>and</c> or <c>or</c>.</summary>
		LeadingOperator,
		/// <summary>A reference that names no property, or not the property it stands in.</summary>
		InvalidReference,
		/// <summary><c>any</c> where it is no whole value.</summary>
		InvalidAny,
	}

	/// <summary>A form the language refuses, where it stands, and what to write instead.</summary>
	internal sealed class FilterSyntaxError
	{
		public FilterSyntaxError(FilterSyntaxErrorKind kind, TextSpan span, string message)
		{
			Kind    = kind;
			Span    = span;
			Message = message;
		}

		public FilterSyntaxErrorKind Kind    { get; }
		public TextSpan              Span    { get; }
		public string                Message { get; }

		public override string ToString()
		{
			return $"{Kind}@{Span.Start}+{Span.Length}: {Message}";
		}
	}

	/// <summary>A node of the tree.</summary>
	internal abstract class FilterSyntaxNode
	{
		protected FilterSyntaxNode(TextSpan span)
		{
			Span = span;
		}

		public TextSpan Span { get; }

		/// <summary>The error this node is, or carries for itself; <see langword="null"/> for a well-formed node.</summary>
		public virtual FilterSyntaxError? Error => null;

		/// <summary>The nodes directly below this one, in text order.</summary>
		public abstract IEnumerable<FilterSyntaxNode> Children { get; }

		protected static IEnumerable<FilterSyntaxNode> None => Array.Empty<FilterSyntaxNode>();
	}

	/// <summary>One item of a filter: its lines.</summary>
	internal sealed class FilterItemSyntax : FilterSyntaxNode
	{
		public FilterItemSyntax(TextSpan span, FilterSyntaxError? leadingOperator, ImmutableArray<FilterLineSyntax> lines)
			: base(span)
		{
			LeadingOperator = leadingOperator;
			Lines           = lines;
		}

		/// <summary>An <c>and</c> or <c>or</c> the item begins with: it would continue a line of another item.</summary>
		public FilterSyntaxError?               LeadingOperator { get; }
		public ImmutableArray<FilterLineSyntax> Lines           { get; }

		public override FilterSyntaxError? Error => LeadingOperator;

		public override IEnumerable<FilterSyntaxNode> Children => Lines;
	}

	/// <summary>A line: an include (<c>expr</c>) or a subtraction (<c>- expr</c>).</summary>
	internal sealed class FilterLineSyntax : FilterSyntaxNode
	{
		public FilterLineSyntax(TextSpan span, TextSpan? minus, FilterExpressionSyntax expression)
			: base(span)
		{
			Minus      = minus;
			Expression = expression;
		}

		/// <summary>The span of <c>-</c>, for a subtraction.</summary>
		public TextSpan?              Minus         { get; }
		public FilterExpressionSyntax Expression    { get; }
		public bool                   IsSubtraction => Minus is not null;

		public override IEnumerable<FilterSyntaxNode> Children => new FilterSyntaxNode[] { Expression };
	}

	// ---- expressions ---------------------------------------------------------------------------------------------

	internal abstract class FilterExpressionSyntax : FilterSyntaxNode
	{
		protected FilterExpressionSyntax(TextSpan span)
			: base(span)
		{
		}
	}

	/// <summary>A form that cannot be read where an expression stands: a bad line, a 1.1 form, a reserved word.</summary>
	internal sealed class ErrorExpressionSyntax : FilterExpressionSyntax
	{
		public ErrorExpressionSyntax(FilterSyntaxError error, ImmutableArray<FilterSyntaxNode> children = default)
			: base(error.Span)
		{
			Error       = error;
			ErrorParts  = children.IsDefault ? ImmutableArray<FilterSyntaxNode>.Empty : children;
		}

		public override FilterSyntaxError Error { get; }

		/// <summary>What was read inside the refused form, where it was read at all.</summary>
		public ImmutableArray<FilterSyntaxNode> ErrorParts { get; }

		public override IEnumerable<FilterSyntaxNode> Children => ErrorParts;
	}

	/// <summary><c>a or b or …</c>: two operands or more.</summary>
	internal sealed class OrExpressionSyntax : FilterExpressionSyntax
	{
		public OrExpressionSyntax(TextSpan span, ImmutableArray<FilterExpressionSyntax> operands)
			: base(span)
		{
			Operands = operands;
		}

		public ImmutableArray<FilterExpressionSyntax> Operands { get; }

		public override IEnumerable<FilterSyntaxNode> Children => Operands;
	}

	/// <summary><c>a and b and …</c>: two operands or more.</summary>
	internal sealed class AndExpressionSyntax : FilterExpressionSyntax
	{
		public AndExpressionSyntax(TextSpan span, ImmutableArray<FilterExpressionSyntax> operands, ImmutableArray<FilterSyntaxError> operatorErrors)
			: base(span)
		{
			Operands       = operands;
			OperatorErrors = operatorErrors;
		}

		public ImmutableArray<FilterExpressionSyntax> Operands       { get; }

		/// <summary>1.1 operators (<c>;</c>, <c>&amp;</c>, <c>|</c>) read where <c>and</c> stands.</summary>
		public ImmutableArray<FilterSyntaxError>      OperatorErrors { get; }

		public override IEnumerable<FilterSyntaxNode> Children => Operands;
	}

	/// <summary><c>not condition</c>.</summary>
	internal sealed class NotExpressionSyntax : FilterExpressionSyntax
	{
		public NotExpressionSyntax(TextSpan span, FilterExpressionSyntax operand)
			: base(span)
		{
			Operand = operand;
		}

		public FilterExpressionSyntax Operand { get; }

		public override IEnumerable<FilterSyntaxNode> Children => new FilterSyntaxNode[] { Operand };
	}

	/// <summary><c>( expr )</c>.</summary>
	internal sealed class GroupExpressionSyntax : FilterExpressionSyntax
	{
		public GroupExpressionSyntax(TextSpan span, FilterExpressionSyntax expression)
			: base(span)
		{
			Expression = expression;
		}

		public FilterExpressionSyntax Expression { get; }

		public override IEnumerable<FilterSyntaxNode> Children => new FilterSyntaxNode[] { Expression };
	}

	/// <summary><c>key: value</c>.</summary>
	internal sealed class KeyConditionSyntax : FilterExpressionSyntax
	{
		public KeyConditionSyntax(TextSpan span, FilterKey key, TextSpan keySpan, FilterValueSyntax value)
			: base(span)
		{
			Key     = key;
			KeySpan = keySpan;
			Value   = value;
		}

		public FilterKey         Key     { get; }
		public TextSpan          KeySpan { get; }
		public FilterValueSyntax Value   { get; }

		public override IEnumerable<FilterSyntaxNode> Children => new FilterSyntaxNode[] { Value };
	}

	/// <summary>A reference standing as a condition of its own: <c>assembly.method</c>.</summary>
	internal sealed class ReferenceExpressionSyntax : FilterExpressionSyntax
	{
		public ReferenceExpressionSyntax(ReferenceSyntax reference)
			: base(reference.Span)
		{
			Reference = reference;
		}

		public ReferenceSyntax Reference { get; }

		public override IEnumerable<FilterSyntaxNode> Children => new FilterSyntaxNode[] { Reference };
	}

	/// <summary>
	/// A run of words, optionally followed by a compact signature, or a compact signature alone: their conjunction.
	/// <c>public static</c>, <c>public Save*(int) : Task</c>, <c>Save*</c>.
	/// </summary>
	internal sealed class WordRunSyntax : FilterExpressionSyntax
	{
		public WordRunSyntax(TextSpan span, ImmutableArray<WordSyntax> words, SignatureSyntax? signature)
			: base(span)
		{
			Words     = words;
			Signature = signature;
		}

		public ImmutableArray<WordSyntax> Words     { get; }
		public SignatureSyntax?           Signature { get; }

		/// <summary>How many conditions the run is: one per word, one for each part of the signature.</summary>
		public int ConditionCount =>
			Words.Length + (Signature is null ? 0 : 1 + (Signature.Parameters is null ? 0 : 1) + (Signature.Returns is null ? 0 : 1));

		public override IEnumerable<FilterSyntaxNode> Children
		{
			get
			{
				foreach (var word in Words)
					yield return word;

				if (Signature is not null)
					yield return Signature;
			}
		}
	}

	/// <summary>A word: <c>public</c>, <c>protected internal</c>, or a reserved one, which carries its error.</summary>
	internal sealed class WordSyntax : FilterSyntaxNode
	{
		public WordSyntax(TextSpan span, FilterWord word)
			: base(span)
		{
			Word = word;
		}

		public WordSyntax(FilterSyntaxError error)
			: base(error.Span)
		{
			_error = error;
		}

		readonly FilterSyntaxError? _error;

		public FilterWord Word { get; }

		public override FilterSyntaxError? Error => _error;

		public override IEnumerable<FilterSyntaxNode> Children => None;
	}

	/// <summary>A compact signature: <c>**.*Service.Save*(.., CancellationToken) : Task*</c>.</summary>
	internal sealed class SignatureSyntax : FilterSyntaxNode
	{
		public SignatureSyntax(TextSpan span, NamePatternSyntax name, ParameterListPatternSyntax? parameters, TypePatternSyntax? returns)
			: base(span)
		{
			Name       = name;
			Parameters = parameters;
			Returns    = returns;
		}

		public NamePatternSyntax    Name       { get; }
		public ParameterListPatternSyntax? Parameters { get; }
		public TypePatternSyntax?   Returns    { get; }

		public override IEnumerable<FilterSyntaxNode> Children
		{
			get
			{
				yield return Name;

				if (Parameters is not null)
					yield return Parameters;

				if (Returns is not null)
					yield return Returns;
			}
		}
	}

	// ---- values --------------------------------------------------------------------------------------------------

	/// <summary>What stands after a key: a pattern, <c>any</c> or a reference.</summary>
	internal abstract class FilterValueSyntax : FilterSyntaxNode
	{
		protected FilterValueSyntax(TextSpan span)
			: base(span)
		{
		}
	}

	/// <summary><c>any</c> as a whole value: removes every restriction on the property from above.</summary>
	internal sealed class AnyValueSyntax : FilterValueSyntax
	{
		public AnyValueSyntax(TextSpan span)
			: base(span)
		{
		}

		public override IEnumerable<FilterSyntaxNode> Children => None;
	}

	/// <summary>
	/// <c>aspect.p</c> or <c>assembly.p</c>. <see cref="Target"/> is the text after the dot as written; whether it names a
	/// property, and the one it stands in, is checked by the reader.
	/// </summary>
	internal sealed class ReferenceSyntax : FilterValueSyntax
	{
		public ReferenceSyntax(TextSpan span, FilterLevel level, TextSpan levelSpan, string target, TextSpan targetSpan)
			: base(span)
		{
			Level      = level;
			LevelSpan  = levelSpan;
			Target     = target;
			TargetSpan = targetSpan;
		}

		public FilterLevel Level      { get; }
		public TextSpan    LevelSpan  { get; }
		public string      Target     { get; }
		public TextSpan    TargetSpan { get; }

		public override IEnumerable<FilterSyntaxNode> Children => None;
	}

	/// <summary>The value of a key that takes a pattern but was given something else.</summary>
	internal sealed class ErrorValueSyntax : FilterValueSyntax
	{
		public ErrorValueSyntax(FilterSyntaxError error)
			: base(error.Span)
		{
			Error = error;
		}

		public override FilterSyntaxError Error { get; }

		public override IEnumerable<FilterSyntaxNode> Children => None;
	}

	// ---- name patterns -------------------------------------------------------------------------------------------

	/// <summary>
	/// A dotted name pattern: <c>global::Shop.**.*Service.Save*</c>, <c>List&lt;int&gt;.Add</c>. A segment is a run of
	/// parts (<c>Get*</c> is a name and a star) with optional type arguments, or <c>**</c>.
	/// </summary>
	internal sealed class NamePatternSyntax : FilterValueSyntax
	{
		public NamePatternSyntax(TextSpan span, TextSpan? global, ImmutableArray<NameSegmentSyntax> segments, FilterSyntaxError? error = null)
			: base(span)
		{
			Global   = global;
			Segments = segments;
			_error   = error;
		}

		readonly FilterSyntaxError? _error;

		/// <summary>The span of <c>global::</c>, where the name is rooted.</summary>
		public TextSpan?                         Global   { get; }
		public ImmutableArray<NameSegmentSyntax> Segments { get; }

		/// <summary>A <c>?</c> after the name, where no type stands.</summary>
		public override FilterSyntaxError? Error => _error;

		public override IEnumerable<FilterSyntaxNode> Children => Segments;
	}

	/// <summary>One segment of a dotted name: <c>**</c>, or parts with optional type arguments.</summary>
	internal sealed class NameSegmentSyntax : FilterSyntaxNode
	{
		public NameSegmentSyntax(TextSpan span, bool isGlobstar, ImmutableArray<NamePartSyntax> parts, TypeArgumentsSyntax? typeArguments)
			: base(span)
		{
			IsGlobstar    = isGlobstar;
			Parts         = parts;
			TypeArguments = typeArguments;
		}

		/// <summary><c>**</c>: zero or more whole segments.</summary>
		public bool                           IsGlobstar    { get; }
		public ImmutableArray<NamePartSyntax> Parts         { get; }

		/// <summary><c>&lt;…&gt;</c> after the name; <see langword="null"/> means any arity, 0 included.</summary>
		public TypeArgumentsSyntax?           TypeArguments { get; }

		public override IEnumerable<FilterSyntaxNode> Children
		{
			get
			{
				foreach (var part in Parts)
					yield return part;

				if (TypeArguments is not null)
					yield return TypeArguments;
			}
		}
	}

	internal enum NamePartKind
	{
		/// <summary>Literal text: a name, or <c>@name</c> (the text without <c>@</c>), or <c>"…"</c> (the text unescaped).</summary>
		Text,
		/// <summary><c>*</c>.</summary>
		Star,
		/// <summary><c>{a,b}</c>.</summary>
		Alternatives,
		/// <summary>A form the language refuses, carrying its error.</summary>
		Error,
	}

	/// <summary>A part of a name segment.</summary>
	internal sealed class NamePartSyntax : FilterSyntaxNode
	{
		NamePartSyntax(TextSpan span, NamePartKind kind, string? text, bool isQuoted, ImmutableArray<NamePatternSyntax> alternatives, FilterSyntaxError? error)
			: base(span)
		{
			Kind         = kind;
			Text         = text;
			IsQuoted     = isQuoted;
			Alternatives = alternatives.IsDefault ? ImmutableArray<NamePatternSyntax>.Empty : alternatives;
			_error       = error;
		}

		readonly FilterSyntaxError? _error;

		public static NamePartSyntax Name(TextSpan span, string text)
		{
			return new NamePartSyntax(span, NamePartKind.Text, text, false, default, null);
		}

		public static NamePartSyntax Quoted(TextSpan span, string text, FilterSyntaxError? error)
		{
			return new NamePartSyntax(span, NamePartKind.Text, text, true, default, error);
		}

		public static NamePartSyntax Star(TextSpan span)
		{
			return new NamePartSyntax(span, NamePartKind.Star, null, false, default, null);
		}

		public static NamePartSyntax Choice(TextSpan span, ImmutableArray<NamePatternSyntax> alternatives)
		{
			return new NamePartSyntax(span, NamePartKind.Alternatives, null, false, alternatives, null);
		}

		public static NamePartSyntax Refused(FilterSyntaxError error)
		{
			return new NamePartSyntax(error.Span, NamePartKind.Error, null, false, default, error);
		}

		public NamePartKind                      Kind         { get; }

		/// <summary>The literal text of a <see cref="NamePartKind.Text"/> part.</summary>
		public string?                           Text         { get; }

		/// <summary>Whether the text was quoted: exact text, never a keyword, no wildcards.</summary>
		public bool                              IsQuoted     { get; }
		public ImmutableArray<NamePatternSyntax> Alternatives { get; }

		public override FilterSyntaxError? Error => _error;

		public override IEnumerable<FilterSyntaxNode> Children => Alternatives;
	}

	/// <summary><c>&lt;T1, T2&gt;</c> after a name segment.</summary>
	internal sealed class TypeArgumentsSyntax : FilterSyntaxNode
	{
		public TypeArgumentsSyntax(TextSpan span, ImmutableArray<TypePatternSyntax> arguments, FilterSyntaxError? error = null)
			: base(span)
		{
			Arguments = arguments;
			_error    = error;
		}

		readonly FilterSyntaxError? _error;

		public ImmutableArray<TypePatternSyntax> Arguments { get; }

		/// <summary><c>&lt;&gt;</c> or <c>&lt;,&gt;</c>.</summary>
		public override FilterSyntaxError? Error => _error;

		public override IEnumerable<FilterSyntaxNode> Children => Arguments;
	}

	// ---- type patterns -------------------------------------------------------------------------------------------

	/// <summary>A type pattern: a core with array ranks and <c>?</c> after it, in C# order.</summary>
	internal sealed class TypePatternSyntax : FilterValueSyntax
	{
		public TypePatternSyntax(TextSpan span, TypeCoreSyntax core, ImmutableArray<TypeSuffixSyntax> suffixes)
			: base(span)
		{
			Core     = core;
			Suffixes = suffixes;
		}

		public TypeCoreSyntax                   Core     { get; }
		public ImmutableArray<TypeSuffixSyntax> Suffixes { get; }

		public override IEnumerable<FilterSyntaxNode> Children
		{
			get
			{
				yield return Core;

				foreach (var suffix in Suffixes)
					yield return suffix;
			}
		}
	}

	/// <summary>An array rank (<c>[]</c>, <c>[,]</c>) or <c>?</c>.</summary>
	internal sealed class TypeSuffixSyntax : FilterSyntaxNode
	{
		public TypeSuffixSyntax(TextSpan span, int rank)
			: base(span)
		{
			Rank = rank;
		}

		/// <summary>The rank of an array suffix; 0 for <c>?</c>.</summary>
		public int  Rank       { get; }
		public bool IsNullable => Rank == 0;

		public override IEnumerable<FilterSyntaxNode> Children => None;
	}

	internal abstract class TypeCoreSyntax : FilterSyntaxNode
	{
		protected TypeCoreSyntax(TextSpan span)
			: base(span)
		{
		}
	}

	/// <summary>A named type: a dotted name pattern.</summary>
	internal sealed class NamedTypeSyntax : TypeCoreSyntax
	{
		public NamedTypeSyntax(NamePatternSyntax name)
			: base(name.Span)
		{
			Name = name;
		}

		public NamePatternSyntax Name { get; }

		public override IEnumerable<FilterSyntaxNode> Children => new FilterSyntaxNode[] { Name };
	}

	/// <summary><c>(T1, T2, …)</c>: a tuple, element names ignored.</summary>
	internal sealed class TupleTypeSyntax : TypeCoreSyntax
	{
		public TupleTypeSyntax(TextSpan span, ImmutableArray<TypePatternSyntax> elements)
			: base(span)
		{
			Elements = elements;
		}

		public ImmutableArray<TypePatternSyntax> Elements { get; }

		public override IEnumerable<FilterSyntaxNode> Children => Elements;
	}

	/// <summary><c>{T1, T2}</c> in a type position: either type.</summary>
	internal sealed class TypeAlternativesSyntax : TypeCoreSyntax
	{
		public TypeAlternativesSyntax(TextSpan span, ImmutableArray<TypePatternSyntax> alternatives)
			: base(span)
		{
			Alternatives = alternatives;
		}

		public ImmutableArray<TypePatternSyntax> Alternatives { get; }

		public override IEnumerable<FilterSyntaxNode> Children => Alternatives;
	}

	/// <summary>
	/// <c>delegate* unmanaged[Stdcall, SuppressGCTransition]&lt;int, void&gt;</c>: a function pointer, one type whatever
	/// commas its calling conventions hold. The last type of the list is the return type.
	/// </summary>
	internal sealed class FunctionPointerTypeSyntax : TypeCoreSyntax
	{
		public FunctionPointerTypeSyntax(TextSpan span, string? callingConvention, ImmutableArray<string> unmanagedConventions, ImmutableArray<FunctionPointerParameterSyntax> parameters)
			: base(span)
		{
			CallingConvention    = callingConvention;
			UnmanagedConventions = unmanagedConventions;
			Parameters           = parameters;
		}

		/// <summary><c>managed</c>, <c>unmanaged</c>, or <see langword="null"/> where none is written.</summary>
		public string?                                         CallingConvention    { get; }

		/// <summary>The names in <c>unmanaged[…]</c>, as written.</summary>
		public ImmutableArray<string>                          UnmanagedConventions { get; }

		/// <summary>The parameter types and, last, the return type.</summary>
		public ImmutableArray<FunctionPointerParameterSyntax>  Parameters           { get; }

		public override IEnumerable<FilterSyntaxNode> Children => Parameters;
	}

	/// <summary>A parameter or the return of a function pointer: a type with an optional <c>ref</c>, <c>ref readonly</c>, <c>in</c> or <c>out</c>.</summary>
	internal sealed class FunctionPointerParameterSyntax : FilterSyntaxNode
	{
		public FunctionPointerParameterSyntax(TextSpan span, FilterParameterModifier? modifier, TypePatternSyntax type)
			: base(span)
		{
			Modifier = modifier;
			Type     = type;
		}

		public FilterParameterModifier? Modifier { get; }
		public TypePatternSyntax        Type     { get; }

		public override IEnumerable<FilterSyntaxNode> Children => new FilterSyntaxNode[] { Type };
	}

	/// <summary>The value of <c>returns:</c>: a type with an optional <c>ref</c> or <c>ref readonly</c>.</summary>
	internal sealed class ReturnsPatternSyntax : FilterValueSyntax
	{
		public ReturnsPatternSyntax(TextSpan span, FilterRefKind refKind, TypePatternSyntax type)
			: base(span)
		{
			RefKind = refKind;
			Type    = type;
		}

		public FilterRefKind     RefKind { get; }
		public TypePatternSyntax Type    { get; }

		public override IEnumerable<FilterSyntaxNode> Children => new FilterSyntaxNode[] { Type };
	}

	/// <summary><c>(int, .., CancellationToken)</c>.</summary>
	internal sealed class ParameterListPatternSyntax : FilterValueSyntax
	{
		public ParameterListPatternSyntax(TextSpan span, ImmutableArray<ParameterPatternSyntax> parameters)
			: base(span)
		{
			Parameters = parameters;
		}

		public ImmutableArray<ParameterPatternSyntax> Parameters { get; }

		public override IEnumerable<FilterSyntaxNode> Children => Parameters;
	}

	internal enum ParameterPatternKind
	{
		/// <summary>Modifiers and a type.</summary>
		Typed,
		/// <summary><c>_</c>: one parameter.</summary>
		AnyOne,
		/// <summary><c>..</c>: any run of parameters.</summary>
		AnyRun,
		/// <summary>A form the language refuses, carrying its error.</summary>
		Error,
	}

	/// <summary>A parameter pattern: <c>ref readonly int</c>, <c>this IEnumerable&lt;*&gt;</c>, <c>_</c>, <c>..</c>.</summary>
	internal sealed class ParameterPatternSyntax : FilterValueSyntax
	{
		public ParameterPatternSyntax(TextSpan span, ParameterPatternKind kind, ImmutableArray<FilterParameterModifier> modifiers, TypePatternSyntax? type, FilterSyntaxError? error = null)
			: base(span)
		{
			Kind      = kind;
			Modifiers = modifiers.IsDefault ? ImmutableArray<FilterParameterModifier>.Empty : modifiers;
			Type      = type;
			_error    = error;
		}

		readonly FilterSyntaxError? _error;

		public ParameterPatternKind                    Kind      { get; }
		public ImmutableArray<FilterParameterModifier> Modifiers { get; }
		public TypePatternSyntax?                      Type      { get; }

		public override FilterSyntaxError? Error => _error;

		public override IEnumerable<FilterSyntaxNode> Children => Type is null ? None : new FilterSyntaxNode[] { Type };
	}
}
