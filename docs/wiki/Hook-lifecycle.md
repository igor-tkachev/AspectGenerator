# Hook Lifecycle

`AspectAttribute` maps lifecycle hooks to static method names. Prefer `nameof(...)` so hook names stay in sync with refactors.

```csharp
[Aspect(
    OnInit = nameof(OnInit),
    OnUsing = nameof(OnUsing),
    OnUsingAsync = nameof(OnUsingAsync),
    OnBeforeCall = nameof(OnBeforeCall),
    OnBeforeCallAsync = nameof(OnBeforeCallAsync),
    OnCall = nameof(OnCall),
    OnAfterCall = nameof(OnAfterCall),
    OnAfterCallAsync = nameof(OnAfterCallAsync),
    OnCatch = nameof(OnCatch),
    OnCatchAsync = nameof(OnCatchAsync),
    OnFinally = nameof(OnFinally),
    OnFinallyAsync = nameof(OnFinallyAsync))]
```

Hooks must be static and accessible from generated code. `UseInterceptData=true` expects hooks to receive `ref InterceptData<T>`. `OnCall` replaces the target method call and must be the last aspect in the aspect chain.

Invalid hook names or signatures should be reported by `AG010x` diagnostics.
