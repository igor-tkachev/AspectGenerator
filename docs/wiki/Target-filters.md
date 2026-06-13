# Target Filters

Target filters apply aspects to target methods without decorating every method explicitly.

Filters are not runtime AOP. They select target methods, but AspectGenerator still rewrites only call sites visible to the current compilation.

## Filter Model

`TargetFilter` is an ordered string list. `Contains` and `Regex` modes are implemented. `Dsl` is reserved while the DSL syntax is being designed.

- filters apply to canonical target method signatures;
- an entry starting with `-` is an exclude filter;
- the last matching entry wins inside one filter set;
- no matching entry means the filter set does not apply;
- exclude filters do not suppress explicit method-level aspect attributes.

`Contains` matching uses `StringComparison.Ordinal`.

`Regex` matching uses `RegexOptions.CultureInvariant`, is case-sensitive by default, and uses a timeout to protect IDE and design-time builds.

Invalid regex patterns report `AG0201`.

## Assembly Filters

```csharp
[assembly: Log(
    TargetFilterKind = AspectFilterKind.Contains,
    TargetFilter =
    [
        "MyApp.Services.",
        "-HealthCheck"
    ])]
```

## Type Filters

```csharp
[Log(
    TargetFilterKind = AspectFilterKind.Contains,
    TargetFilter = ["Save"])]
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
    public string[]? TargetFilter { get; set; }
    public AspectFilterKind TargetFilterKind { get; set; } = AspectFilterKind.Dsl;

    public static void OnAfterCall(InterceptInfo info) {}
}
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
