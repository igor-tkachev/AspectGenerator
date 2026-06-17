# Target Filters

Target filters apply aspects to target methods without decorating every method explicitly.

Filters are not runtime AOP. They select target methods, but AspectGenerator still rewrites only call sites visible to the current compilation.

## Filter Model

`TargetFilter` is evaluated per target method. Each rule or native condition group returns include, exclude, or no decision. Rules/groups are evaluated in order, and the last effective decision wins.

Each rule can use a matcher prefix. Unprefixed rules and `pattern:` use the native AspectGenerator target pattern syntax. `contains:` and `regex:` match the canonical target method signature.

- `contains:` and `regex:` lines are standalone ordered rules;
- `pattern:` lines are standalone native pattern rules;
- unprefixed native condition lines are grouped into condition groups;
- a line starting with `-` starts an exclude rule/group;
- a line starting with `&` continues the current native condition group with explicit `AND`;
- a line starting with `|` starts an alternative native condition group with the same include/exclude action as the immediately previous native condition group;
- a line starting with `#` is a comment;
- the last effective matching entry wins inside one filter set;
- no matching entry means the filter set does not apply;
- exclude filters do not suppress explicit method-level aspect attributes.

The property name is always `TargetFilter`; only the property type can differ. The aspect attribute can expose it as `string?` or `string[]?`. Every string is split into lines, empty lines are ignored, and each non-comment line is one rule. For `string[]`, rules from all items are concatenated in order.

Standalone rule syntax:

```text
[-] [pattern:|regex:|contains:] rule-body
```

No matcher prefix is equivalent to `pattern:` for compact method patterns. For condition lines, no prefix participates in native condition grouping.

`Pattern` matching is case-sensitive and matches structured method metadata: accessibility, modifiers, containing namespace/type, method name, return type, and parameter list.

`Contains` matching uses `StringComparison.Ordinal` over the canonical signature.

`Regex` matching uses `RegexOptions.CultureInvariant`, is case-sensitive by default, and uses a timeout to protect IDE and design-time builds.

Invalid regex patterns report `AG0201`. Invalid native pattern rules report `AG0202`, `AG0204`, `AG0205`, or `AG0206` depending on the error.

Approximate grammar:

```text
filter          ::= line*
line            ::= blank | comment | rule
comment         ::= "#" text
rule            ::= prefix? matcher-rule
prefix          ::= "-" | "&" | "|"
matcher-rule    ::= prefixed-rule | native-rule
prefixed-rule   ::= ("pattern" | "contains" | "regex") ":" text
native-rule     ::= condition-rule | method-pattern
condition-rule  ::= condition-item (";" condition-item)*
condition-item  ::= condition-key ":" condition-expr | modifier-token+
condition-expr  ::= condition-term ("|" condition-term)*
condition-term  ::= condition-atom ("&" condition-atom)*
method-pattern  ::= modifier-token* method-name-pattern parameter-list? return-pattern?
return-pattern  ::= ":" type-pattern
```

This grammar is intentionally approximate. Pattern values also have escaping, wildcard, generic nesting, dotted-segment, and parameter-list parsing rules.

A simple unknown condition key reports `AG0204` and is not interpreted as a compact method pattern:

```text
foo: bar
```

Compact method patterns with return type remain valid when the text before `:` is not a simple condition key:

```text
public Save* : System.Void
MyApp.Service.Save* : System.String
```

## Native Pattern Syntax

The default matcher is `pattern:`, so these two rules are equivalent:

```text
public **.*Service.Save*(..., *CancellationToken) : System.Threading.Tasks.Task*
pattern: public **.*Service.Save*(..., *CancellationToken) : System.Threading.Tasks.Task*
```

Method-pattern form:

```text
[accessibility/modifiers...] [namespace/type path.]method[(parameters)] [: return-type]
```

Examples:

```text
Save
Save()
Save(...)
public Save*
public static MyApp.Services.*Service.Save*
protected internal MyApp.**.*Service.Save*(..., *CancellationToken) : System.Threading.Tasks.Task*
MyApp.Data.Repository<*>.Get<*>(_)
*.TryGet(System.String, out System.Int32) : System.Boolean
```

Parameter rules:

- no parameter list ignores parameters;
- `()` requires zero parameters;
- `(...)` matches any parameter list;
- `_` matches exactly one arbitrary parameter;
- `...` inside a parameter list matches zero or more parameters and can appear once;
- typed parameters can use `ref`, `out`, `in`, or `params`.

Condition-rule form combines conditions with `;` or grouped lines:

```text
namespace:MyApp.Services.**; type:*Service; method:Save*
method:*Async; returns:System.Threading.Tasks.Task*
params:..., *CancellationToken
param:out System.Int32
signature:public * MyApp.Services.*
```

Different condition keys in one group are `AND`. Repeated same keys are `OR` by default:

```text
namespace: MyApp.Services
namespace: MyApp.Jobs
method: Save*
method: Update*
params: ..., *CancellationToken
```

This means:

```text
(namespace is MyApp.Services OR MyApp.Jobs)
AND (method is Save* OR Update*)
AND params match ..., *CancellationToken
```

Use leading `&` to force `AND` for repeated keys:

```text
method: Save*
& method: *Async
```

When same-key `AND` and implicit same-key `OR` are mixed, the implicit `OR` joins the latest same-key bucket:

```text
method: Save*
& method: *Async
method: Update*
```

This means:

```text
method matches Save*
AND method matches (*Async OR Update*)
```

For complex same-key logic, prefer inline expressions:

```text
method: Save* & *Async | Update* & *Async
```

Within one condition value, `|` means `OR`, and `&` means `AND`. `&` has higher precedence than `|`:

```text
method: Save* & *Async | Update* & *Async
param: *CancellationToken & *DbConnection
```

Use `\|` or `\&` when a literal operator character is needed in a native pattern value. Operators are not parsed specially for `contains:` and `regex:` rules.

Leading `|` starts an alternative group:

```text
namespace: MyApp.Services
& type: *Service
& method: Save*

| namespace: MyApp.Jobs
& type: *Job
& method: Run*
```

Leading `|` is valid only immediately after a native condition group. It is not a continuation mechanism for standalone `regex:`, `contains:`, `pattern:`, or compact method-pattern rules. These examples are invalid and report `AG0202`:

```text
namespace: MyApp.Services
regex:^public .*$
| method: Save*
```

```text
namespace: MyApp.Services
contains: Save
| method: Update*
```

```text
namespace: MyApp.Services
pattern: MyApp.**.Save*
| method: Update*
```

Leading `-` starts an exclude group. A following leading `|` keeps the exclude action:

```text
- method: HealthCheck
| method: Ping
```

`pattern:` is always standalone, even when its body looks like a condition rule:

```text
pattern: namespace: MyApp.Services; method: Save*
```

Condition keys:

- `namespace`: containing namespace only;
- `type`: simple containing type name;
- `path` / `fulltype`: namespace plus containing type;
- `method`: method name only;
- `fullmethod`: namespace plus containing type plus method name;
- `returns`: return type;
- `param`: any parameter;
- `params`: full parameter list;
- `signature`: canonical signature string.

Pattern wildcards:

- `*` matches zero or more characters inside one segment;
- `?` matches exactly one character inside one segment;
- `**` matches zero or more complete dotted segments and must be a complete segment;
- dots inside generic angle brackets do not split segments.

## Assembly Filters

```csharp
[assembly: Log(
    TargetFilter = """
        # Include service/job methods with CancellationToken.
        namespace: MyApp.Services
        namespace: MyApp.Jobs
        & type: *Service | *Job
        & method: Save* | Update* | Run*
        & params: ..., *CancellationToken

        # Exclude health checks.
        - method: HealthCheck
        | method: Ping

        # Expert/agent escape hatch.
        regex:^public .* MyApp\.Generated\..*\.Map.*\(.*\)$
        """)]
```

## Type Filters

```csharp
[Log(
    TargetFilter = "contains: Save")]
sealed class UserService
{
    public void SaveUser() {}
    public void LoadUser() {}
}
```

For assembly or type filters, the aspect attribute itself is applied to the assembly or type. The attribute must allow that target and expose a `TargetFilter` property:

```csharp
[Aspect(OnAfterCall = nameof(OnAfterCall))]
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
sealed class LogAttribute : Attribute
{
    public string? TargetFilter { get; set; }

    public static void OnAfterCall(InterceptInfo info) {}
}
```

Use `string[]?` when separate constants or generated attribute arguments are more convenient:

```csharp
public string[]? TargetFilter { get; set; }
```

In AOP terminology, `TargetFilter` plays the role of a pointcut-like method selector. The term `pointcut` is explanatory only and is not part of the public API.

Target filters are only supported on applied aspect attributes at assembly or type level. `[Aspect(TargetFilter = ...)]` is intentionally unsupported to keep aspect definition settings separate from aspect application.

## Precedence

For the same aspect attribute type:

- an explicit method-level aspect application wins and is not suppressed by assembly or type filters;
- type-level `TargetFilter` decisions override assembly-level `TargetFilter` decisions;
- later effective decisions win inside one filter set.

This allows a broad assembly-level include with local type-level excludes:

```csharp
[assembly: Log(TargetFilter = "namespace: MyApp.Services")]

[Log(TargetFilter = "- method: HealthCheck")]
class Service
{
    void Save() {}
    void HealthCheck() {}
}
```

`Save` is intercepted, while `HealthCheck` is not intercepted unless it has an explicit method-level `[Log]`.

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

    public static void OnAfterCall(InterceptInfo info) {}
}
```

The conditional check applies consistently to direct method aspect usage, type-level `TargetFilter`, and assembly-level `TargetFilter`.

Conditional aspect attributes are evaluated against project/tree preprocessor symbols. Project-level `DEBUG`, `TRACE`, and MSBuild-defined symbols are supported. Local source-file `#define` / `#undef` directive state is not guaranteed to be evaluated with full location sensitivity.

## Canonical Signature Format

```text
<accessibility>[ <modifier>...] <return-type> <containing-type>.<method-name>[<type-parameters>](<parameter-types>)
```

Examples:

```text
public System.String MyApp.Services.UserService.GetName(System.Int32)
public static System.Void MyApp.Services.Cache.Clear()
public virtual System.Threading.Tasks.Task<System.String> MyApp.Services.UserService.GetNameAsync(System.Int32,System.Threading.CancellationToken)
public System.Boolean MyApp.Services.UserService.TryGet(System.String,out System.Int32)
public System.Void MyApp.Services.UserService.Update(ref MyApp.Models.User)
public static System.String MyApp.Extensions.UserExtensions.Format(this MyApp.Models.User,System.Int32)
public MyApp.Models.User MyApp.Data.Repository<MyApp.Models.User>.Get<System.Int32>(System.Int32)
```

Formatting rules:

- accessibility is included;
- stable modifiers such as `static`, `abstract`, `virtual`, `override`, `sealed`, and `extern` are included;
- `async`, `partial`, parameter names, nullable annotations, attributes, and generic constraints are omitted;
- type names are fully qualified and do not use C# aliases;
- generic calls use constructed type arguments from the intercepted call site;
- parameter entries include only parameter modifiers and parameter types;
- extension method receivers use `this`.
