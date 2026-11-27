# Target Filters

A target filter selects the methods an aspect applies to. AspectGenerator then intercepts the calls to those methods that it sees in the compilation. A filter is written in `TargetFilter` on an aspect attribute placed on an assembly, a type or a method, and in `DefaultTargetFilter` on the aspect's `[Aspect]` declaration.

```csharp
[assembly: Log(TargetFilter = """
    namespace: Shop.**
    - method: *Internal*
    """)]

[Log(TargetFilter = "method: Save* and assembly.method")]
public class OrderService { }
```

Filters are not runtime AOP: they select target methods, and AspectGenerator still rewrites only the call sites visible to the current compilation. The language of the filter changed in 1.2; see [Migrating from 1.1](#migrating-from-11) for what a 1.1 filter becomes.

This page is the reference. For a first filter it is enough to know the three shapes:

```target-filter
public
```

```target-filter
namespace: MyApp.Services.** and method: Save*
```

```target-filter
- attribute: NoLog
```

The first selects public methods of the project, the second the `Save*` methods of that namespace and below, the third every method of the project that carries no `[NoLog]`.

## Declaring the aspect

The aspect attribute is applied to the assembly, a type or a method. It must allow those targets in its `AttributeUsage` and expose a `TargetFilter` property, as `string?` or as `string[]?`; the property name is always `TargetFilter`. The examples on this page use this aspect:

```csharp
[Aspect(OnAfterCall = nameof(OnAfterCall))]
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
sealed class LogAttribute : Attribute
{
    /// <summary>
    /// Compile-time selection metadata; this value is not copied to the runtime aspect instance.
    /// </summary>
    public string? TargetFilter { get; set; }

    public string? Level { get; set; }

    public static void OnAfterCall(InterceptInfo info) { }
}
```

Use `string[]?` when separate constants or generated attribute arguments are more convenient:

```csharp
public string[]? TargetFilter { get; set; }
```

`TargetFilter` is compile-time selection metadata and is not copied to the runtime aspect instance; its constructor or property initializer default remains in effect there. In AOP terminology it plays the role of a pointcut-like method selector; the term `pointcut` is explanatory only and is not part of the public API.

`TargetFilter` is a property of the applied attribute. `[Aspect(TargetFilter = ...)]` is intentionally unsupported, to keep aspect definition settings separate from aspect application; the aspect author's default is `DefaultTargetFilter` (see [Default target filters](#default-target-filters)).

### What can be selected

- Only **calls** (`M(...)`, `x.M(...)`) are intercepted; constructors, property accessors and operators are not.
- The target is the method a call **binds to at compile time**. `derived.BeginTransaction()` on a type that does not override it targets the base method; `handler.Handle(cmd)` through an interface targets the interface member. There is no run-time discovery of implementations.
- A filter on a type covers the methods **declared in that type**, not those of nested or derived types.
- A method declared only by another source generator is not visible to AspectGenerator; a `partial` method declared by the user and implemented by a generator is.
- The methods the compiler generates for records (`ToString`, `Equals`, `GetHashCode`, `PrintMembers`, `Deconstruct`) can be selected too (the word `generated`). Only explicit calls are intercepted: positional deconstruction (`var (a, b) = person`) calls `Deconstruct` without invocation syntax and is never intercepted.

## Filter Model

### Lines

A filter is a list of **lines**. Each line is either an **include** (`expr`) or a **subtraction** (`- expr`).

- `#` starts a comment that runs to the end of the line (outside `"…"`).
- A line continues on the next line only if that line begins with `and` or `or`, or while a `(` is open. Inside parentheses a line break may stand between any two tokens.
- A `string[]` value is read item by item; every item holds whole lines, and an item that begins with `and` or `or` is an error. All items of one attribute belong to the same filter.
- `TargetFilter = ""` is the same as no `TargetFilter`.

```target-filter
# service methods, except the internal ones
namespace: Shop.Services.**
  and method: {Save*,Update*}
- method: *Internal*
```

### Expressions

```ebnf
line     = [ "-" ] , expr ;
expr     = and-expr , { "or" , and-expr } ;
and-expr = unary , { "and" , unary } ;
unary    = "not" , atom | "(" , expr , ")" | atom ;
atom     = key ":" ( value | "any" | level "." property ) | word | signature | level "." property ;
```

- `and`, `or`, `not` have C#'s precedence (`not` > `and` > `or`).
- `not` stands only before a single condition: `not public`, `not method: Get*`. `not (a or b)` is an error; write `not a and not b`. A run of words or a compact signature is several conditions, so `not public static` and `not public Save*()` are errors too. Write what is meant: `not public and not static` (neither), or `not public or not static` (not both).

```target-filter
not public and not static
```

```target-filter error=AG0220
not (public or static)
```

```target-filter error=AG0220
not public static
```

### Tokens and white space

- **Keywords** are lowercase and case-sensitive: `and`, `or`, `not`, `any`, `aspect`, `assembly`, the keys (`namespace`, `type`, `method`, `returns`, `params`, `param`, `attribute`, `inherits`, `implements`, and the property names of the next section), the words, the reserved words, and the parameter modifiers `ref`, `out`, `in`, `this`, `params`. A name equal to a keyword is written with `@`: `type: *.@static`, `method: @any`. C# type aliases (`int`, `string`, …) are not keywords; they are rooted type names.
- **Key or signature.** A lowercase identifier directly followed by `:` is a key; an unknown one is an error (`methd: Save*`). Anything else before `:` is a compact signature (`Save: Task`, `Save(int): Task`). A method whose name is lowercase is written `@save: Task` or `save(): Task`. An identifier that equals a key except for case (`Method:`, `NAMESPACE:`) is an error with the lowercase spelling, because keys were case-insensitive in 1.1; a method really named `Method` is written `@Method: Save*` or `Method(): Save*`.
- **White space** (spaces, tabs) may stand between any two tokens (`namespace : Shop.**`, `Save (int)`), but never inside a pattern at its top level: `publik Save` is two conditions, not a name with a space.
- **Line breaks** stand only between the tokens of an expression: before `and`/`or` continuing a line, or anywhere inside an expression's `(…)`. They are not allowed inside a pattern — in `<…>`, a parameter list `(…)`, `{…}` or a compact signature.
- **Quoted text** `"…"` stays on one line; `"` and `\` are its only escapes, any other `\` is an error.

```target-filter error=AG0221
methd: Save*
```

```target-filter
@save: Task
```

```target-filter
namespace : Shop.** and Save (int)
```

### Properties

Every condition is about one **property** of a method. The keys and words map to properties as follows:

| Property | Written as |
| --- | --- |
| `location` | `namespace:`, `type:` |
| `method` | `method:` |
| `returns` | `returns:` |
| `params` | `params:` |
| `param` | `param:` |
| `attribute` | `attribute:` |
| `inherits` | `inherits:` |
| `implements` | `implements:` |
| `accessibility` | `public`, `internal`, `protected`, `private`, `protected internal`, `private protected` |
| `static` | `static`, `instance` (`instance` = `not static`) |
| `async` | `async` |
| `virtuality` | `virtual`, `override`, `abstract`, `sealed` |
| `extern` | `extern` |
| `extension` | `extension` |
| `origin` | `project` |
| `generated` | `generated` |

A line **declares** the properties it mentions. Within one filter:

- An include splits at a top-level `and` between conditions on **different** properties: `method: Save* and public` declares `method: Save*` and `public`. A subtraction splits at a top-level `or`: `- method: X or public` is `- method: X` and `- public`.
- Conditions on one property stay together: `namespace: Shop and type: Service` is one `location` declaration. More generally, a line splits only where its parts share no property: `method: Save* and (method: *A or public)` stays one line about `method` and `accessibility`.
- A line that does not split and mentions several properties (`method: Save* or public`, `- method: *Internal* and public`) is a **compound line**; it belongs to all of its properties at once.
- A filter may include each property **once**, and a compound line counts as an include of each of its properties. `method: Save*` and `method: Get*` as two lines is an error (write `method: Save* or Get*`), and so is `method: Save* or public` beside `method: Get*`. Subtractions may repeat.
- The value of a property in one filter is its include (if any) and not any of its subtractions.

The split is syntactic: lines declare properties, not alternatives. `method: Save* and public` followed by `method: Get*` is an error, not `(Save* and public) or Get*`.

```target-filter
method: Save* or Get*
```

```target-filter error=AG0225
method: Save*
method: Get*
```

```target-filter
method: Save* and public
- method: *Internal*
- method: Dispose or static
```

### Levels and the cascade

Each aspect attribute is a **block**. For a method, the blocks that can speak about it are, from most to least specific:

| Level | Block | Speaks about | Implicitly declares |
| --- | --- | --- | --- |
| method | `[Log(...)]` on the method | that method | `location` = its type and `method` = this method, unless it declares them |
| type | `[Log(...)]` on the method's own type (each attribute, on each `partial` declaration, is a block) | methods declared in that type | `location` = this type, unless it declares `location` |
| assembly | each `[assembly: Log(...)]` | every method called in the compilation | `project`, unless it declares `origin` (`project`, `origin: any`) or includes a `location` or `method` condition |
| aspect | `DefaultTargetFilter` of the aspect | every method | nothing |

**The assembly level stays inside the project by default — a deliberate safeguard.** `[assembly: Log]`, `[assembly: Log(TargetFilter = "public")]` or `"async"` select only methods declared in your project, never the thousands of public methods of the .NET libraries and packages you call (`string.Join`, `Console.WriteLine`), which would slow the build and log far more than intended. To reach methods of other assemblies, name them: a `namespace:`, `type:` or `method:` condition (`method: System.Console.WriteLine`, `namespace: Newtonsoft.**`), or `origin: any` for everything. Methods a library marked with the aspect itself are not affected by this default (see [Aspects applied inside libraries](#aspects-applied-inside-libraries)).

```csharp
// Project methods only.
[assembly: Log(TargetFilter = "public")]

// One method of a library, named.
[assembly: Log(TargetFilter = "method: System.Console.WriteLine")]

// Everything that is called, the libraries included. Use with care: see the cost section below.
[assembly: Log(TargetFilter = "origin: any and method: Save*")]
```

For each property, **the most specific block that declares it decides**; what it does not declare comes from the levels above. A property nobody declares does not restrict. A method gets the aspect when every declaration that decides is satisfied: each deciding include is true and each deciding subtraction is false.

- A compound line applies only while no more specific block declares any of its properties; declaring one of them below removes the whole line, and it does not come back further down.
- A method is considered only if at least one assembly, type or method block covers it. `DefaultTargetFilter` alone selects nothing, and `[Log]` on `Outer` does not reach methods of `Outer.Inner`.
- Several blocks on one level (two `[assembly: Log]`, attributes on two `partial` declarations) are alternatives: the method gets the aspect if any combination of one block per level selects it.
- Several attributes of one aspect placed **on the method itself** are not alternatives but separate applications: each forms its own combination with the levels above, and each one that selects the method adds an instance of the aspect with its own settings (ordered by `Order`). Settings conflicts (AG0235) arise only between assembly or type blocks.

**Example.** The aspect's default is `- attribute: NoLog`; the assembly has `namespace: Shop.**` and `- method: *Internal*`.

```csharp
[Aspect(OnAfterCall = nameof(OnAfterCall), DefaultTargetFilter = "- attribute: NoLog")]
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
sealed class LogAttribute : Attribute
{
    public string? TargetFilter { get; set; }

    public static void OnAfterCall(InterceptInfo info) { }
}

[assembly: Log(TargetFilter = """
    namespace: Shop.**
    - method: *Internal*
    """)]
```

| On `OrderService` | `SaveOrder` | `[NoLog] SavePassword` | `SaveInternal` | `[Log] RunInternal` | `[Log] [NoLog] Ping` | `Load` |
| --- | :-: | :-: | :-: | :-: | :-: | :-: |
| `method: Save*` | ✔ | — | ✔ | ✔ | — | — |
| `method: Save* and assembly.method` | ✔ | — | — | ✔ | — | — |
| `method: Save* or assembly.method` | ✔ | — | ✔ | ✔ | — | ✔ |
| `method: any` | ✔ | — | ✔ | ✔ | — | ✔ |
| `[Log]` without a filter | ✔ | — | — | ✔ | — | ✔ |

`[Log] RunInternal` is selected in every row because its own block declares `method` = "this method". The `[NoLog]` methods are never selected because nobody below the aspect declares `attribute`.

### Keeping a value from above: `aspect.p` and `assembly.p`

A declaration replaces everything said above about its property. To keep the value from above, name it:

| Write | Means |
| --- | --- |
| `method: Save* and assembly.method` | narrow the assembly's `method` |
| `method: Save* or assembly.method` | widen it |
| `attribute: aspect.attribute` | back to the aspect author's value |
| `attribute: Audited and aspect.attribute` | add a condition, keep the author's |
| `- assembly.method` | subtract the assembly's value |

```csharp
[Log(TargetFilter = "method: Save* and assembly.method")]
public class Narrowing { }

[Log(TargetFilter = "method: Save* or assembly.method")]
public class Widening { }

[Log(TargetFilter = "attribute: Audited and aspect.attribute")]
public class AddingToTheDefault { }

[Log(TargetFilter = "- assembly.method")]
public class SubtractingTheAssemblysValue { }
```

- `X.p` is the value property `p` would have for this method if there were no attributes below level `X`. If nothing at or above `X` restricts `p`, it is "no restriction": `method: Save* or assembly.method` then selects every method.
- Allowed levels: in a type or method filter, `aspect` and `assembly`; in an assembly filter, `aspect`; in `DefaultTargetFilter`, none. A method filter's `assembly.p` skips the type level.
- A reference names the property being declared only: `method: assembly.method` is valid, `method: assembly.returns` is not, and a reference cannot stand in a compound line (`assembly.method or public` is an error). Settings (`Level`, …) cannot be referenced.
- `- assembly.method` (a subtraction) is not the same as a second include `not assembly.method`; the one-line form is `method: Save* and not assembly.method`.
- `aspect.` and `assembly.` at the start of a pattern are references; a name that begins that way is written `@assembly.Save*`.

```csharp
// An assembly filter may refer to the aspect's value only.
[assembly: Log(TargetFilter = "attribute: Audited and aspect.attribute")]

// A type filter may also refer to the assembly's.
[Log(TargetFilter = "method: assembly.method")]
public class SameAsAssembly { }
```

```target-filter level=type error=AG0233
method: assembly.returns
```

```target-filter level=type error=AG0233
assembly.method or public
```

### Removing a restriction: `any`

`p: any` removes every restriction on `p` from above, the aspect author's included: `method: any`, `attribute: any`, `accessibility: any`. Properties written with words use their property name as the key (`accessibility: any`, `static: any`, `async: any`, `virtuality: any`, `extern: any`, `extension: any`, `generated: any`, `location: any`, `origin: any`); these property names are keys only for `any`. A reference uses the property name too: the property of `namespace:` and `type:` is `location`, so it is `assembly.location` (`assembly.namespace` is an error). `location: any` resets both `namespace:` and `type:`; `origin: any` lets an assembly filter reach referenced methods without naming them.

- `any` is a whole value only (`method: Save* or any` is an error) and the only include of that property in the filter.
- `- p: any` is an error.
- A method or attribute named `any` is written `@any`.

```target-filter
attribute: any
```

```target-filter
origin: any and method: Save*
```

```target-filter error=AG0234
method: Save* or any
```

```target-filter error=AG0234
- method: any
```

### Filters on methods

A `TargetFilter` on a method-level attribute is an ordinary method block. `[Log(TargetFilter = "public")] private void Run()` does not get the aspect. A bare `[Log]` on a method declares only `location` and `method`, so the author's default still applies: with `DefaultTargetFilter = "- attribute: NoLog"`, `[Log] [NoLog] void Ping()` is not logged; `[Log(TargetFilter = "attribute: any")] [NoLog] void Ping()` is.

```csharp
public class Pinger
{
    [Log(TargetFilter = "public")]
    private void Run() { }

    [Log]
    [NoLog]
    void Ping() { }

    [Log(TargetFilter = "attribute: any")]
    [NoLog]
    void Ping2() { }
}
```

An aspect attribute on a method that does not select it is reported by the build, `AG0232` (see [Diagnostics](Diagnostics)), and the build report explains the decision (see [Explaining a selection](#explaining-a-selection)).

### Default target filters

`DefaultTargetFilter` belongs to the aspect definition and is set on `[Aspect]`. It is the weakest block. A user declaration of the same property on any level replaces it; the other properties it declares still apply. Authors should write **subtractions only** there: an include in the default restricts every application that does not declare that property itself.

```csharp
[Aspect(
    OnBeforeCall = nameof(OnBeforeCall),
    DefaultTargetFilter = """
        - attribute: MyCompany.Diagnostics.NoLogAttribute
        """)]
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
sealed class LogAttribute : Attribute
{
    public string? TargetFilter { get; set; }

    public static void OnBeforeCall(InterceptInfo info) { }
}
```

A consumer who really wants the excluded methods writes `attribute: any`; one who adds a condition and keeps the author's writes `attribute: Audited and aspect.attribute`. Prefer fully qualified names in `attribute:` for aspects shared through libraries, to avoid ambiguity.

### Settings

Attribute arguments other than `TargetFilter` are **settings** (`Level`, `Format`, constructor arguments).

- A method's setting comes from the most specific block of its combination that **writes** that argument; an argument not written is inherited; written nowhere, it takes the aspect's default. A bare `[Log]` on a class keeps the assembly's `Level`. All constructor arguments together count as one setting; each named property is its own.
- When two combinations both select a method and write different values for one setting, it is an error (`AG0235`) naming both attributes. Write the value on a more specific level (usually the class). The method still gets the aspect (the error does not switch the aspect off, unlike an error in a filter's text); the conflicting setting takes the aspect's default until the conflict is fixed.

```csharp
[assembly: Log(Level = "Warning", TargetFilter = "namespace: Shop.**")]

// Keeps Level = "Warning" from the assembly.
[Log]
public class OrderService { }

// Writes its own Level.
[Log(Level = "Debug")]
public class DiagnosticsService { }
```

### Aspects applied inside libraries

A library can place an aspect on its own methods. For such a method, the library's own assembly-level and type-level attributes of that aspect apply, read from the library assembly, as in the library's own build (`project` in them means the library's own assembly). If that selects the method, the library's settings apply and the consumer's filters do not change it. If it does not, the consumer's assembly filters decide as for any referenced method. A consumer cannot switch off an aspect a library applied. A settings conflict inside the library's own attributes is reported in the consumer's build on the library's attribute, without a source location.

A filter read from a referenced assembly (a library's `TargetFilter`, or the `DefaultTargetFilter` of an aspect it defines) that cannot be read — for example 1.1 syntax in a library built with AspectGenerator 1.1 — is an error like any other filter error (`AG0236`): the aspect selects nothing in the consumer's compilation, and the error names the assembly. The library has to be rebuilt with AspectGenerator 1.2.

How a library-marked method behaves, step by step. In `Lib`:

```csharp
[assembly: Log(TargetFilter = "static")]

namespace Lib
{
    public class Writer
    {
        [Log]
        public void Write() { }
    }
}
```

1. `[Log] public void Write()` is a method-level block in `Lib`'s own chain: the aspect's default, `Lib`'s assembly and type attributes of `Log`, and the method's attribute.
2. If that chain selects `Write`, every call to it in the consumer gets `Log` with `Lib`'s settings, whatever the consumer's filters say and regardless of the project-only default.
3. If it does not (here `Lib`'s assembly filter is `static` and `Write` is an instance method), the consumer's assembly filter decides like for any referenced method: `public` alone does not reach it, `method: Lib.**.Write` or `namespace: Lib.**` does.

```csharp
// In the consumer: reaches Lib's Write because it names it.
[assembly: Log(TargetFilter = "method: Lib.**.Write")]
```

### A filter with an error

If any filter of an aspect has an error, that aspect selects nothing in the compilation, and every error the reader finds is reported. An unclosed `(` or `"` runs to the end of its `string[]` item, so further errors in that item are reported once it is closed. See [Diagnostics](Diagnostics) for the ids.

## Pattern Syntax

### Patterns

| Pattern | Matches |
| --- | --- |
| `*` | zero or more characters within one name segment (never across `.` or into `<…>`) |
| `**` | zero or more whole segments |
| `{Get,Find}*` | either alternative (names, or type patterns in a type position) |
| `List<*>` | `List` with one type argument; `List` without `<…>` is any arity, 0 included |
| `"<Main>$"` | exact text, no wildcards (escapes `\"` and `\\` only) |
| `@any`, `@and` | a name equal to a keyword |
| `global::MyApp.Save` | rooted: the whole name |

- There is no `?` wildcard. `?` after a type means nullable; elsewhere it is an error.
- `Task<>` and `Dictionary<,>` are errors; write `Task<*>`, `Dictionary<*,*>`.
- There are no backslash escapes.

```target-filter
method: {Get,Find}* and returns: List<*>
```

```target-filter
method: "<Main>$"
```

```target-filter error=AG0220
returns: Task<>
```

### Anchoring

| Key | Anchoring |
| --- | --- |
| `namespace:` | rooted: `MyApp.**` is `MyApp` and below; `""` is the global namespace |
| `type:`, `attribute:`, type positions | a trailing run of whole segments of the full name: `*Service` and `Data.*Repository` match in any namespace; `global::` roots |
| `method:`, compact signature | trailing segments of `Namespace.Type.Method`; `global::` roots |

A single segment in a type position matches the last segment (`Task<*>` matches `System.Threading.Tasks.Task<…>`). C# aliases (`int`, `string`) are rooted names, also as the type part of a method path (`method: int.Parse` is `System.Int32.Parse`). `dynamic` matches `dynamic`, not `object`.

`global::` stands only at the start of a pattern, or at the start of an alternative of a `{…}` that is the whole pattern (`{global::A.Save,B.Save}`); elsewhere it is an error. Likewise `**` may stand in an alternative only when the `{…}` is the whole pattern (`{**.Save,Other.Save}`, `namespace: {Shop.**,Billing}`), not inside a name.

```target-filter
namespace: {Shop.**,Billing}
```

```target-filter
method: {global::A.Save,B.Save}
```

```target-filter
type: *Service and returns: Task<*>
```

### Types

- A type pattern is structural. `*` matches any type; a name pattern (`System.String`, `*Service`, and also `**` or `*.*`) matches a type that has a name, and does not match an array, a tuple or a pointer.
- Arrays carry their rank: `int[]` ≠ `int[,]`; jagged arrays in C# order (`int[][,]`).
- Nullable reference annotations are ignored, on arrays too, but a `?` after a rank still closes the array to its left, as C# reads it: `string[]?[,]` is a two-dimensional array of `string[]` (the same type as `string[,][]`), not `string[][,]`. `X?` over a value type is `Nullable<X>` (`int?` does not match `int`). `*?` matches any reference type and any `Nullable<…>`; `System.Nullable<*>` matches nullable value types only.
- Tuples match their element list, names ignored.
- `returns: T` matches any ref kind; `returns: ref T` and `returns: ref readonly T` match exactly.
- A function pointer is written as in C#: `delegate*<int, void>`, `delegate* managed<…>`, `delegate* unmanaged<…>`, `delegate* unmanaged[Cdecl, SuppressGCTransition]<…>`; its parameter types may carry `ref`, `ref readonly`, `in`, `out`; the last type is the return type. Calling-convention names are exact (no wildcards); commas inside `[…]` do not split a parameter list. `*` matches any function pointer.
- A type-parameter name in a pattern (`Get<TKey>`) is an ordinary name; write `*`. A type parameter left in a call (inside generic code) matches only `*` — not `*?` and not quoted text.
- Pointer types have no pattern form yet: `*` or the quoted canonical text (`"System.Int32*"`) match them.

```target-filter
returns: int[,] or (int, string) or int?
```

```target-filter
returns: ref readonly *
```

```target-filter
param: delegate* unmanaged[Cdecl]<int, void>
```

```target-filter
method: Get<*> and returns: "System.Int32*"
```

### Keys

| Key | Value | Fact of the target method |
| --- | --- | --- |
| `namespace:` | dotted name | containing namespace |
| `type:` | type pattern | containing type (full name, outer types included) |
| `method:` | dotted name; the last segment is the method, optionally with `<…>` | `Type.Method` path |
| `returns:` | type pattern, optional `ref` / `ref readonly` | return type |
| `params:` | `(` list `)`: `()`, `(..)`, `(int, ..)`, `(.., CancellationToken)`, `(_, out bool)`, `(this string)` | whole parameter list |
| `param:` | parameter pattern | some parameter |
| `attribute:` | type pattern; as in C#, `X` matches the attribute class `X` or `XAttribute` | an attribute on the method or a containing type, as the compiler reports it (below) |
| `inherits:` | type pattern | the containing type is, derives from or implements it |
| `implements:` | method pattern | the method is, implements or overrides a matching member (below) |

`implements:` takes a method pattern and is true of a method that is a matching member itself (any method, virtual or not), implements a matching interface member as its own declaring type maps the interface, or overrides a matching member. An override counts for the interface members its overridden chain implements, and a constructed call's type arguments apply; a base method that only a derived type uses to implement an interface does not count.

```target-filter
inherits: *.IDisposable
```

```target-filter
implements: IHandler<*>.Handle
```

```target-filter
params: (.., CancellationToken) and returns: Task*
```

```target-filter
params: (_, out bool) or params: (this string)
```

```target-filter
attribute: Obsolete
```

`attribute:` sees the attributes the compiler reports for the method and its containing types — no more, no less. For a method in your project these are the attributes written in source. For a method in a referenced assembly they are the attributes stored in that assembly, which differ: the compiler adds some for language features (`AsyncStateMachine`, `Nullable`, `CompilerGenerated`, `Extension`, `DebuggerStepThrough` on `async` methods, …) and does not store others as attributes (`DllImport`, `MethodImpl`, `StructLayout`, …). So `attribute: AsyncStateMachine` matches `async` methods of libraries but not of your project; for such facts use the words (`async`, `extern`, `extension`, `generated`). An attribute on a base method that an override inherits does not count.

In parameter lists `_` is one parameter and `..` any run (at most once). A parameter has three independent modifiers: its kind (`ref`, `ref readonly`, `in`, `out`, or none), `this` and `params`. A written kind must match exactly (`in int` does not match `ref int`); an unwritten one accepts any kind. `this` and `params`, when written, are required; when not written, either is accepted. `param: _` means "has at least one parameter"; `..` stands only inside a `params:` list.

### Words

| Word | Meaning (the same for methods in source and in referenced assemblies) |
| --- | --- |
| `public`, `internal`, `protected`, `private`, `protected internal`, `private protected` | declared accessibility, exactly; `protected` ≠ `protected internal`. For a referenced method, as its reference assembly declares it (the .NET reference pack declares some runtime `protected internal` members as `protected`) |
| `static`, `instance` | — |
| `virtual`, `override`, `abstract`, `sealed`, `extern` | as C# declares them: an override is `override`, not `virtual`; `sealed` is a `sealed override`; an `abstract override` is both `abstract` and `override`; `extern` includes runtime-implemented (`InternalCall`) methods; an interface member without a body is `abstract`, one with a default body is `virtual`. So `- abstract` also leaves calls through interfaces alone |
| `extension` | an extension method, in either call form |
| `async` | the call's return type (with the call's type arguments; a type parameter by its constraints) is `Task`, `ValueTask`, their generic forms or another type with an accessible instance `GetAwaiter()` that follows the awaiter pattern (an `internal` one counts only inside its own assembly); **not** the `async` modifier: `async void` methods and `IAsyncEnumerable<T>` iterators are not `async`, which referenced assemblies do not record |
| `project` | declared in the current compilation |
| `generated` | declared by the compiler (record members such as `ToString`, `Equals`, `Deconstruct`) or marked `[CompilerGenerated]` |

A run of words is their conjunction: `public static`.

```target-filter
public static async
```

```target-filter
protected internal
```

### Compact signature

`public **.*Service.Save*(.., CancellationToken) : Task*` is shorthand for its words with `method:`, `params:` and `returns:`, and it splits by property like any `and` line. The return type follows `:`.

```target-filter
public **.*Service.Save*(.., CancellationToken) : Task*
```

```target-filter
Save(int): Task
```

### Reserved

The keys `within:`, `assembly:` and `overrides:`, the words `iterator`, `unsafe`, `partial`, `readonly` and `exposed`, and `/…/` literals are reserved for later versions and are an error now (`implements:` already covers overrides). `signature:` is a 1.1 key: it is reported as 1.1 syntax with the v2 spelling, and stays reserved.

```target-filter error=AG0221
within: Shop
```

```target-filter error=AG0225
signature: public * MyApp.Services.*
```

## Generic Methods

A condition describes the call as written, with its type arguments: `method: List<int>.Add` selects `list.Add(1)` on a `List<int>`. Where a pattern names a concrete type and the method's definition has a type parameter there, each call decides; otherwise the definition decides once for all calls. Inside generic code, a type argument that is the caller's own type parameter matches only `*`. Extension methods are judged in their static form, constructed from the call: `numbers.Any()` and `Enumerable.Any<int>(numbers)` are the same target. The result never depends on other filters or on referenced assemblies.

```target-filter
method: List<int>.Add
```

```target-filter
method: System.Linq.Enumerable.Any<int>
```

## Which calls are bound (cost)

To decide, a call must be bound, which is the expensive part. A line of an assembly filter limits binding when it names the method with a last segment that contains any literal text (`method: Save`, `method: Append*`, `method: *Async`, `method: *Internal*`, `method: *Repository.Save`) — the call's name is matched against it before binding — or roots a namespace (`namespace: MyApp.**`, `global::MyApp.**`) that no referenced assembly declares. Lines without such an anchor — `public`, `async`, `returns: Task*`, `not method: Save`, references, `any` — bind every call, except that an assembly filter which neither names a place nor declares `origin` stays within the project's own methods. An assembly filter costs as much as its most expensive alternative; the explain output names it. If a referenced assembly declares or applies aspects, every call is bound.

For a large project prefer an assembly filter anchored on a method name (`method: *Async`, `namespace: MyApp.**`) and use the build report to inspect the selected methods and intercepted call sites.

## Explaining a selection

For an aspect attribute on a method that is not applied, the build report lists, per combination of blocks, each property, the block that decided it (file and line) and its value, the expansion of any `aspect.`/`assembly.` reference, and where each setting came from. With `AspectGeneratorTrace=true` the same table is written for every method a filter considered; the report size limit (`AspectGeneratorReportSizeLimit`) applies to these tables. See [Diagnostics](Diagnostics#build-report).

## Highlighting

The parser of the filter language is generated with [DotGram](https://github.com/dotgram/dotgram) from a grammar kept in the repository; it is compiled into the generator and adds no package dependency. Highlighting of filter text in `TargetFilter` and `DefaultTargetFilter` values will come with the DotGram extension for Visual Studio 17.14 and later; it has not shipped yet, and until it does filter text is an ordinary string in every editor.

## Conditional Aspect Attributes

Aspect attribute classes can be decorated with `System.Diagnostics.ConditionalAttribute`. When an aspect attribute class has `[Conditional("SYMBOL")]`, applying that aspect is ignored unless `SYMBOL` is defined for the consuming syntax tree/project.

Multiple `[Conditional]` attributes are treated as `OR`:

```csharp
using System.Diagnostics;
using AspectGenerator;

[Conditional("DEBUG")]
[Conditional("TRACE")]
[Aspect(OnAfterCall = nameof(OnAfterCall))]
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
sealed class LogAttribute : Attribute
{
    public string? TargetFilter { get; set; }

    public static void OnAfterCall(InterceptInfo info) { }
}
```

The conditional check applies consistently to direct method aspect usage, type-level `TargetFilter`, and assembly-level `TargetFilter`.

Conditional aspect attributes are evaluated against project/tree preprocessor symbols. Project-level `DEBUG`, `TRACE`, and MSBuild-defined symbols are supported. Local source-file `#define` / `#undef` directive state is not guaranteed to be evaluated with full location sensitivity.

## Migrating from 1.1

Version 1.2 reads target filters in a new language. A filter written in 1.1 is reported as an error with the 1.2 spelling (`AG0225`) where it can be recognised, and its aspect selects nothing until the filter is rewritten. This is what each 1.1 construct becomes:

| 1.1 | 1.2 |
| --- | --- |
| `contains:`, `regex:`, `signature:`, `pattern:`, `fulltype:`, `fullmethod:`, `path:`, `attributes:`, `;`, `&`, `\|`, `...`, backslash escapes | removed; AG0225 shows the v2 spelling |
| two lines with the same key meant OR | a property is included once: `method: Save* or Get*` |
| `a` / `& b` / `c` on one key: a line that repeats the key joins the latest bucket of that key, so it meant `a and (b or c)` | write it as one declaration: `method: Save* and (method: *Async or method: Update*)` |
| rules ordered, the last match decides | no order; each property is decided by the most specific attribute |
| an aspect attribute on a method was never filtered | filtered like any attribute: `[Log] [NoLog]` is not logged under a `- attribute: NoLog` default |
| `TargetFilter` on a method was ignored (AG0208) | it filters that method |
| an assembly filter and a type filter of one aspect selected the union (`method: Save` on the assembly, `method: Load` on the type: both) | a property the type declares replaces the assembly's for that type's methods (only `Load`); keep both with `method: Load or assembly.method` |
| text 1.1 read without an error could be meaningless (`;` alone, `pub:lic`, unbalanced `<`) | it is an error (AG0220–AG0225) and the aspect selects nothing |
| a filter of subtractions only (`-attributes: NoLog`) selected nothing | subtractions start from the level's default: `- attribute: NoLog` on the assembly selects every project method without `[NoLog]` |
| an assembly filter without a place (`public`, `returns: string`, `param: int`) reached referenced methods too | it selects only methods of the project; name a place or write `origin: any` to go further |
| a name pattern matched the full name (`Task` meant only `Task`) | names match trailing whole segments (`Task` matches `System.Threading.Tasks.Task`); `global::` roots |
| `private` also matched `private protected`; `protected`, `internal` also matched `protected internal` | accessibility words are exact; `protected and internal` matches nothing — write `protected internal` |
| `Load<T>` named the arity and the type parameter | `Load` matches any arity; a type-parameter name is an ordinary name — write `Load<*>` |
| a name pattern in a type position matched arrays and tuples by their text (`*.*`, `**`) | type patterns are structural: write `*` for any type, `*[]`, `(int, string)` |
| array ranks were ignored (1.1.2) | `int[]` ≠ `int[,]` |
| tuples, generic attributes (`GenericMarker<int>`) and `attribute: System.Obsolete` (without `Attribute`) did not match | they match: tuple element lists, attribute type arguments, `X` or `XAttribute` as in C# |
| `extern` did not cover runtime-implemented methods (`Math.Sqrt`) | `extern` includes `InternalCall` methods |
| `param: a & b` meant one parameter matching both patterns | `param: a and param: b` may be met by two different parameters; one parameter matching two type patterns has no spelling |
| `DefaultTargetFilter` rules could be overridden by later rules | it is the weakest level; a user declaration of the same property replaces it |
| settings came from the first including attribute (the assembly's) | from the most specific attribute that writes them; conflicts are errors |
| library type and assembly filters were ignored for aspects the library placed | they apply |
| `?` matched one character | `?` means nullable |
| `int?` matched `int` | `int?` is `Nullable<int>` |
| generic methods declared in the project were judged by their definition, sometimes by the call depending on other filters | a condition describes the call with its type arguments, always; the result never depends on other filters |

A 1.1 filter and its rewrite:

```text
# 1.1
namespace: MyApp.Services
namespace: MyApp.Jobs
& type: *Service | *Job
& method: Save* | Update*
- method: HealthCheck
```

```target-filter
namespace: {MyApp.Services,MyApp.Jobs}
  and type: {*Service,*Job}
  and method: {Save*,Update*}
- method: HealthCheck
```
