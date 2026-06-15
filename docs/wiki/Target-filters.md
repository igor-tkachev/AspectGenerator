# Target Filters

Target filters apply aspects to target methods without decorating every method explicitly.

Filters are not runtime AOP. They select target methods, but AspectGenerator still rewrites only call sites visible to the current compilation.

## Filter Model

`TargetFilter` is an ordered rule list. Each rule can use a matcher prefix. Unprefixed rules and `pattern:` use the native AspectGenerator target pattern syntax. `contains:` and `regex:` match the canonical target method signature.

- filters apply to canonical target method signatures;
- a rule starting with `-` is an exclude filter;
- a line starting with `#` is a comment;
- the last matching entry wins inside one filter set;
- no matching entry means the filter set does not apply;
- exclude filters do not suppress explicit method-level aspect attributes.

The property name is always `TargetFilter`; only the property type can differ. The aspect attribute can expose it as `string?` or `string[]?`. Every string is split into lines, empty lines are ignored, and each non-comment line is one rule. For `string[]`, rules from all items are concatenated in order.

Rule syntax:

```text
[-] [pattern:|regex:|contains:] rule-body
```

No matcher prefix is equivalent to `pattern:`.

`Pattern` matching is case-sensitive and matches structured method metadata: accessibility, modifiers, containing namespace/type, method name, return type, and parameter list.

`Contains` matching uses `StringComparison.Ordinal` over the canonical signature.

`Regex` matching uses `RegexOptions.CultureInvariant`, is case-sensitive by default, and uses a timeout to protect IDE and design-time builds.

Invalid regex patterns report `AG0201`. Invalid native pattern rules report `AG0202`, `AG0204`, `AG0205`, or `AG0206` depending on the error.

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

Condition-rule form combines conditions with `;`:

```text
namespace:MyApp.Services.**; type:*Service; method:Save*
method:*Async; returns:System.Threading.Tasks.Task*
params:..., *CancellationToken
param:out System.Int32
signature:public * MyApp.Services.*
```

Condition keys:

- `namespace`: containing namespace only;
- `type`: simple containing type name;
- `fulltype`: namespace plus containing type;
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
        # Include services and exclude health checks.
        contains: MyApp.Services.
        -contains: HealthCheck
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
