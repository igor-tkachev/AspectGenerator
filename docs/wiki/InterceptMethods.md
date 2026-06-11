# InterceptMethods

`InterceptMethods` is a low-level compatibility feature for intercepting methods by compiler display string instead of applying an aspect attribute to the target method.

```csharp
[Aspect(
    OnAfterCall = nameof(OnAfterCall),
    InterceptMethods =
    [
        "System.String.Substring(int)",
        "string.Substring(int)"
    ])]
```

This API is brittle because matching depends on aliases, overloads, generic spelling, and display-string formatting. Prefer method-level aspect attributes where possible.

Future versions should provide diagnostics for unmatched or ambiguous strings and may add a safer typed API for common scenarios.
