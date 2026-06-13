# Target Filters

Target filters apply aspects to target methods without decorating every method explicitly.

Filters are not runtime AOP. They select target methods, but AspectGenerator still rewrites only call sites visible to the current compilation.

## Filter Model

Filters are ordered regex strings:

- filters apply to canonical target method signatures;
- a filter starting with `-` is an exclude filter;
- the last matching filter wins inside one filter set;
- no matching filter means the filter set does not apply;
- exclude filters do not suppress explicit method-level aspect attributes.

Regex matching uses `RegexOptions.CultureInvariant`, is case-sensitive by default, and uses a timeout to protect IDE and design-time builds.

Invalid regex patterns report `AG0201`.

## Aspect Declaration Filters

```csharp
[Aspect(
    OnAfterCall = nameof(OnAfterCall),
    Filter =
    [
        @"^public .* MyApp\.Services\..*Service\.",
        @"-\.HealthCheck\(\)$"
    ])]
sealed class LogAttribute : Attribute
{
    public static void OnAfterCall(InterceptInfo info) {}
}
```

## Assembly Filters

```csharp
[assembly: Log(
    Filter =
    [
        @"^public .* MyApp\.Services\..*Service\.",
        @"-\.HealthCheck\(\)$"
    ])]
```

## Type Filters

```csharp
[Log(Filter = [@".*\.Save.*\("])]
sealed class UserService
{
    public void SaveUser() {}
    public void LoadUser() {}
}
```

For assembly or type filters, the aspect attribute itself is applied to the assembly or type. The attribute must allow that target and expose a `Filter` property:

```csharp
[Aspect(OnAfterCall = nameof(OnAfterCall))]
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
sealed class LogAttribute : Attribute
{
    public string[]? Filter { get; set; }

    public static void OnAfterCall(InterceptInfo info) {}
}
```

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
public T MyApp.Data.Repository<T>.Get<T>(System.Int32)
```

Formatting rules:

- accessibility is included;
- stable modifiers such as `static`, `abstract`, `virtual`, `override`, `sealed`, and `extern` are included;
- `async`, `partial`, parameter names, nullable annotations, attributes, and generic constraints are omitted;
- type names are fully qualified and do not use C# aliases;
- parameter entries include only parameter modifiers and parameter types;
- extension method receivers use `this`.
