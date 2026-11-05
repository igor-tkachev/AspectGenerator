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

## Iterators

An interceptor wraps the call, and the call of an iterator method (`yield return` in an `IEnumerable<T>`/`IEnumerator<T>` method, or an `async` `IAsyncEnumerable<T>` method) only creates the sequence: the iterator body runs later, while the caller enumerates it. An iterator returns no task, so its interceptor calls the synchronous hooks (an async-only hook is `AG0107`). On an iterator every hook runs during the call, before any of the body: `OnBeforeCall`, then `OnAfterCall` and `OnFinally` as soon as the sequence object is returned. An exception thrown by the body is thrown from `MoveNext` in the caller's loop and never reaches `OnCatch`, and `OnUsing` disposes its scope before the body starts.

`AG0108` reports this for aspects with `OnAfterCall`, `OnCatch`, `OnFinally` or `OnUsing` on an iterator: a warning at the aspect applied to the method, information at the call when only a target filter selected it, once per aspect application. For a virtual, abstract or interface method the check covers what the call can run: the method's own body and every override or implementation in the same project (overrides in other assemblies cannot be seen); the warning names the iterator when it is an override. To guard or time the work itself, apply the aspect to the method that enumerates the sequence, or have the target return a materialized collection. A method that returns a sequence built by other code (for example a LINQ query) is not an iterator; the same deferral applies to that query, but the generator cannot see it and does not report it.
