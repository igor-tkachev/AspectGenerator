# Hook Lifecycle

`AspectAttribute` maps lifecycle hooks to method names. Prefer `nameof(...)` so hook names stay in sync with refactors.

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

Static hooks remain the fast path:

```csharp
public static void OnBeforeCall(InterceptInfo info) {}
```

When a hook needs typed aspect configuration, it can receive the applied aspect attribute instance:

```csharp
public static void OnBeforeCall(LogAttribute aspect, InterceptInfo info) {}
```

When a static hook needs a fresh typed aspect instance per call, set `AspectInstanceLifetime.Instance`:

```csharp
[Aspect(OnBeforeCall = nameof(OnBeforeCall), Lifetime = AspectInstanceLifetime.Instance)]
sealed class TimingAttribute : Attribute
{
    public long Started { get; set; }

    public static void OnBeforeCall(TimingAttribute aspect, InterceptInfo info)
    {
        aspect.Started = Stopwatch.GetTimestamp();
        info.Tag = aspect.Started;
    }
}
```

`AspectInstanceLifetime.Static` stores the constructed aspect attribute in a per-interceptor generated static state holder. The holder is initialized lazily on first use of that interceptor, so unrelated interceptor aspect instances are not constructed.

Static lifetime aspect instances are shared. Aspect authors should treat them as immutable configuration objects or implement their own synchronization for shared mutable state.

`AspectInstanceLifetime.Instance` creates a new applied aspect attribute instance for every intercepted call. Hooks still need to be static and accessible from generated code.

Use Static lifetime for most aspects and performance-sensitive systems. Use Static lifetime plus `InterceptInfo.Tag` for cheap per-call state. Use Instance lifetime only when typed per-call state in the aspect object is worth the allocation.

Aspect attribute constructors and property initializers should be cheap and side-effect free. Static lifetime runs them at most once per generated interceptor/aspect application; Instance lifetime runs them for every intercepted call.

`UseInterceptData=true` expects hooks to receive `ref InterceptData<T>`. `OnCall` replaces the target method call and must be the last aspect in the aspect chain.

Invalid hook names or signatures should be reported by `AG010x` diagnostics.
