[AspectGenerator skill](../SKILL.md)

# Target filters

> You are here if you select target methods using assembly-level or type-level aspects.

`TargetFilter` applies an aspect to matching target methods at assembly or type level. It belongs to the applied aspect attribute, not to `[Aspect]`.

`TargetFilter` is compile-time selection metadata and is not copied to the runtime aspect instance; its constructor or property initializer default remains in effect there.

`DefaultTargetFilter` belongs to `[Aspect]` on the aspect definition. It lets aspect authors prepend default rules to assembly-level or type-level applications of that aspect. V1 uses simple concatenation:

```text
EffectiveTargetFilter = DefaultTargetFilter + TargetFilter
```

Later matching rules win. A consumer `TargetFilter` can override a default exclusion unless it repeats that exclusion later.

Aspect attributes that support filters should expose:

```csharp
public string? TargetFilter { get; set; }
```

or:

```csharp
public string[]? TargetFilter { get; set; }
```

Each string is split into non-empty lines. Lines starting with `#` are comments. `string[]` values concatenate all rules.

Rule types:

```text
unprefixed  native AspectGenerator target pattern syntax
pattern:     native AspectGenerator target pattern syntax
contains:    ordinal substring match against canonical method signature
regex:       regex match against canonical method signature
-attributes: exclude targets with a matching method or containing type attribute
-rule        exclude rule; last matching rule wins inside one filter set
```

Example:

```csharp
[assembly: Log(
    TargetFilter = """
        # Include service save methods and exclude health checks.
        public **.*Service.Save*(..., *CancellationToken)
        -contains: HealthCheck
        """)]
```

Native condition rules support method metadata conditions. Different keys in one group are AND. Repeated same keys are OR by default. Leading `&` forces AND. Leading `|` starts an alternative condition group. Inline condition expressions support `&` and `|`, with `&` higher precedence. Escape literal operators as `\&` and `\|`.

Canonical method signature format:

```text
<accessibility>[ <modifier>...] <return-type> <containing-type>.<method-name>[<type-parameters>](<parameter-types>)
```

Types are fully qualified without C# aliases. Nullable annotations and parameter names are omitted. Extension method receivers are formatted with `this`.

The `attributes:` condition checks method attributes and containing type attributes, including nested containing types. It supports short attribute names, short names without the `Attribute` suffix, and fully qualified names. `attribute:` is intentionally not supported.

Target filters select target methods. AspectGenerator still rewrites only call sites visible to the current compilation.

No matching rule means a filter set does not apply. Each filter set is evaluated independently; inclusion by any set applies the aspect. Exclusions remain local to their set and do not suppress explicit method-level aspects. Aspect attributes used at assembly/type level need corresponding `AttributeUsage` targets.

`contains:` is ordinal and `regex:` is case-sensitive by default with culture-invariant matching and timeout protection. Native patterns match structured method metadata rather than call-site text. For errors see [diagnostics and fixes](diagnostics.md).
