[AspectGenerator skill](../SKILL.md)

# Writing an aspect

> You are here if you define an aspect attribute or change its hooks, lifetime or conditional behavior.

Define an aspect by creating an attribute class decorated with `[Aspect]`:

```csharp
using AspectGenerator;

[Aspect(OnBeforeCall = nameof(OnBeforeCall))]
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
sealed class LogAttribute : Attribute
{
	public static void OnBeforeCall(InterceptInfo info)
	{
		Console.WriteLine($"Calling {info.MemberInfo.Name}");
	}
}
```

Hook names are strings. Prefer `nameof(...)`.

Invalid hook names or signatures should be reported by `AG010x` diagnostics. If a case fails only through generated-code compiler errors, treat it as an AspectGenerator bug.

## Hook model

Current supported hook method style is static hook methods.

Common hook signatures:

```csharp
public static void OnBeforeCall(InterceptInfo info) {}
public static void OnAfterCall(InterceptInfo info) {}
public static void OnCatch(InterceptInfo info) {}
public static void OnFinally(InterceptInfo info) {}
```

Typed aspect instance parameter is supported for static lifecycle hooks:

```csharp
public static void OnBeforeCall(LogAttribute aspect, InterceptInfo info) {}
```

`UseInterceptData=true` expects hooks to receive `ref InterceptData<T>` instead of `InterceptInfo` / `InterceptInfo<T>`:

```csharp
public static void OnBeforeCall(ref InterceptData<int> data) {}
```

`OnCall` replaces the target method call. It must match the target signature and must be the last aspect in the aspect chain.

Async hooks such as `OnBeforeCallAsync`, `OnAfterCallAsync`, `OnCatchAsync`, and `OnFinallyAsync` require supported async targets: `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>`. `async void` is unsupported.

## Aspect instance lifetime

`AspectAttribute.Lifetime` controls applied aspect attribute instance construction:

```csharp
[Aspect(OnBeforeCall = nameof(OnBeforeCall), Lifetime = AspectInstanceLifetime.Static)]
```

Values:

```text
Auto      generator-selected behavior; currently follows the static fast path unless explicit Instance is requested
Static    reuse one lazily initialized aspect instance per generated interceptor/aspect application
Instance  create a new applied aspect attribute instance for every intercepted call
```

Static lifetime stores aspect instances in per-interceptor generated static state holders. These holders are initialized lazily on first use of the corresponding interceptor, so unrelated interceptor aspect instances are not constructed.

Static lifetime aspect instances are shared across threads; hooks that mutate the attribute must be thread-safe. An exception during construction surfaces as `TypeInitializationException` from the generated static state holder. Treat static instances as immutable configuration objects or implement synchronization for shared mutable state.

Use this guidance:

```text
Static for most aspects and performance-sensitive systems
Static + InterceptInfo.Tag for cheap per-call state
Instance only when typed per-call state stored in the aspect object is worth the allocation
```

Aspect constructors and property initializers should be cheap and side-effect free. Static lifetime runs them at most once per generated interceptor/aspect application; Instance lifetime runs them for every intercepted call.

## Conditional aspects

Aspect attribute classes may use `System.Diagnostics.ConditionalAttribute`:

```csharp
[Conditional("DEBUG")]
[Aspect(OnAfterCall = nameof(OnAfterCall))]
sealed class LogAttribute : Attribute
{
	public static void OnAfterCall(InterceptInfo info) {}
}
```

Multiple `[Conditional]` attributes are OR. Conditional aspect attributes apply consistently to direct method aspects, type-level `TargetFilter`, and assembly-level `TargetFilter`.

Project/tree preprocessor symbols such as `DEBUG`, `TRACE`, and MSBuild-defined symbols are supported. Local source-file `#define` / `#undef` directive state is not guaranteed to be evaluated with full location sensitivity.

## Lifecycle details

Hooks are static and accessible from generated code. `OnInit` initializes the invocation state and returns `InterceptInfo<T>` (or `InterceptData<T>` with `UseInterceptData=true`). `OnUsing` returns `IDisposable`; `OnUsingAsync` returns `IAsyncDisposable` for async disposal. Their scopes enclose the intercepted call. `OnBeforeCall` runs before the target, `OnAfterCall` after success, `OnCatch` on exception and `OnFinally` in cleanup. Async counterparts are preferred for supported async targets; synchronous hooks serve as fallbacks.

`UseInterceptType=true` includes the lifecycle stage in the hook data. `PassArguments=true` populates `MethodArguments`. The applied attribute is available as `InterceptInfo.Aspect` or `InterceptData<T>.Aspect`, in addition to the optional typed hook parameter.

## Iterators

An iterator call only creates a sequence; its body runs when the caller enumerates it. All hooks run during the call, before that body. `OnCatch` cannot observe exceptions from enumeration, and `OnUsing` disposes before enumeration starts. Iterators use synchronous hooks; an async-only hook gets `AG0107`.

`AG0108` warns for `OnAfterCall`, `OnCatch`, `OnFinally` or `OnUsing` on an iterator (information when filter-selected). For virtual, abstract or interface targets the check also covers overrides and implementations in the current project. To guard the work, apply the aspect to the enumerating method or return a materialized collection. Deferred sequences constructed by other code have the same timing issue but are not diagnosed as iterators.

See [diagnostics and fixes](diagnostics.md) for hook errors and [target filters](target-filters.md) for selecting methods without individual attributes.
