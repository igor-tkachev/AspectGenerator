[AspectGenerator skill](../SKILL.md)

# Target filters

> You are here if you select target methods using assembly-level, type-level or method-level aspects, or you read or write a `TargetFilter`.

A target filter selects the methods an aspect applies to; AspectGenerator then intercepts the calls to them that it sees in the compilation. A filter is written in `TargetFilter` on an applied aspect attribute (assembly, type or method) and in `DefaultTargetFilter` on the aspect's `[Aspect]` declaration. The full reference is the [Target filters wiki page](https://github.com/igor-tkachev/AspectGenerator/wiki/Target-filters).

```csharp
[assembly: Log(TargetFilter = """
    namespace: Shop.**
    - method: *Internal*
    """)]

[Log(TargetFilter = "method: Save* and assembly.method")]
public class OrderService { }
```

The aspect attribute exposes `public string? TargetFilter { get; set; }` or `public string[]? TargetFilter { get; set; }` and allows the targets it is applied to in `AttributeUsage`. `TargetFilter` is compile-time metadata and is not copied to the runtime aspect instance. `[Aspect(TargetFilter = ...)]` is unsupported; the author's default is `DefaultTargetFilter`.

## What is selected

- Only calls are intercepted (not constructors, property accessors or operators), and the target is the method the call binds to at compile time: a call through an interface targets the interface member, a call on a derived type that does not override targets the base method.
- A filter on a type covers the methods declared in that type, not those of nested or derived types.

## Lines and expressions

- A filter is a list of lines: an include (`expr`) or a subtraction (`- expr`). `#` starts a comment.
- A line continues on the next only when that line begins with `and` or `or`, or while a `(` is open. In a `string[]`, every item holds whole lines.
- `and`, `or`, `not` follow C# precedence. `not` stands before one condition only: `not public`, `not method: Get*`; write `not a and not b`, not `not (a or b)`.
- Keys and words are lowercase. A name equal to a keyword is written with `@` (`method: @any`). Anything before `:` that is not a key is a compact signature: `public **.*Service.Save*(.., CancellationToken) : Task*`.

```target-filter
namespace: Shop.Services.**
  and method: {Save*,Update*}
  and params: (.., CancellationToken)
- method: *Internal*
```

## Properties, keys and words

| Property | Written as |
| --- | --- |
| `location` | `namespace:` (rooted, `MyApp.**`), `type:` (trailing segments) |
| `method`, `returns`, `params`, `param` | `method:`, `returns:`, `params: (int, ..)`, `param:` |
| `attribute` | `attribute:`: `X` matches `X` or `XAttribute`, on the method or a containing type, as the compiler reports it |
| `inherits`, `implements` | `inherits:` (the containing type is, derives from or implements it), `implements:` (the method is, implements or overrides a matching member) |
| `accessibility` | `public`, `internal`, `protected`, `private`, `protected internal`, `private protected` (exact) |
| `static`, `async`, `virtuality`, `extern`, `extension`, `origin`, `generated` | `static`/`instance`, `async`, `virtual`/`override`/`abstract`/`sealed`, `extern`, `extension`, `project`, `generated` |

Patterns: `*` is any characters within one segment, `**` whole segments, `{A,B}` alternatives, `List<*>` a generic with one argument (`List` alone is any arity), `"text"` exact text. `?` means nullable. Types are structural: arrays carry their rank (`int[]` is not `int[,]`), tuples match by element list, `*` is any type. `global::` roots a name. In a parameter list `_` is one parameter and `..` any run, at most once.

## Properties, declarations and the cascade

- A line declares the properties it mentions. An include splits at a top-level `and` between different properties; a subtraction at a top-level `or`. A filter includes each property once: `method: Save*` and `method: Get*` on two lines is an error (`AG0225`); write `method: Save* or Get*`.
- Blocks, from the most specific: the aspect attribute on the method, on its type, `[assembly: ...]`, and the aspect's `DefaultTargetFilter`. For each property the most specific block that declares it decides; what it does not declare comes from above. A method is considered only when an assembly, type or method block covers it.
- A bare method-level `[Log]` declares only `location` and `method`, so the author's default still applies. A `TargetFilter` on a method is an ordinary block for that method.
- Keep or reuse a value from above with `aspect.p` and `assembly.p`: `method: Save* and assembly.method` narrows, `method: Save* or assembly.method` widens, `attribute: aspect.attribute` returns to the author's value. `p: any` removes every restriction on `p` (`attribute: any`, `origin: any`).
- `DefaultTargetFilter` is the weakest block; a user declaration of the same property replaces it. Authors write subtractions only: `- attribute: NoLog`.

```csharp
[Aspect(OnAfterCall = nameof(OnAfterCall), DefaultTargetFilter = "- attribute: NoLog")]
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
sealed class LogAttribute : Attribute
{
    public string? TargetFilter { get; set; }

    public static void OnAfterCall(InterceptInfo info) { }
}
```

## The assembly level stays in the project

`[assembly: Log]` and an assembly filter without a place (`public`, `async`, `returns: string`) select only methods declared in the project, never the methods of the .NET libraries and packages called. This is a deliberate safeguard. To reach other assemblies name them (`namespace: Newtonsoft.**`, `method: System.Console.WriteLine`) or write `origin: any`.

## Settings and libraries

- Arguments other than `TargetFilter` are settings. A method's setting comes from the most specific selecting block that writes it; otherwise it is inherited from above, and then the aspect's default. Two selecting attributes that write different values give `AG0235`; write the value on the more specific level.
- A library can apply an aspect on its own methods; its own filters, read from the library, then apply and the consumer cannot switch the aspect off. If the library's chain does not select the method, the consumer's assembly filter decides as for any referenced method. A filter read from a referenced assembly that cannot be read (1.1 syntax) gives `AG0236` and the aspect selects nothing; rebuild the library with AspectGenerator 1.2.
- Any filter with an error switches its aspect off for the compilation; every error is reported (see [diagnostics and fixes](diagnostics.md)).

## Generic methods and cost

A condition describes the call with its type arguments (`method: List<int>.Add` selects `list.Add(1)`); extension methods are judged in their static form. Assembly filters bind calls, which is the expensive part: a line that names the method with some literal text (`method: Save`, `method: *Async`) or roots a namespace that no referenced assembly declares limits binding; `public`, `async` or `returns: Task*` alone bind every call. Prefer a method-name anchor in assembly filters of large projects. With `AspectGeneratorTrace=true` the build report explains each decision.

## From 1.1

The 1.1 language is gone: `contains:`, `regex:`, `signature:`, `pattern:`, `fulltype:`, `fullmethod:`, `path:`, `attributes:`, `;`, `&`, `|`, `...` and backslash escapes are errors (`AG0225`) that show the 1.2 spelling. Rules are no longer ordered and later ones do not win; two lines with the same key are not OR (write `method: Save* or Get*`); `attributes:` is `attribute:`; `?` means nullable; `TargetFilter` on a method filters that method. The wiki page has the full migration table.

```target-filter error=AG0225
signature: public * MyApp.Services.*
```

Target filters select target methods; AspectGenerator still rewrites only call sites visible to the current compilation. For errors see [diagnostics and fixes](diagnostics.md).
