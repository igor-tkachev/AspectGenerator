# AspectGenerator Hardening Plan

This plan tracks the remaining review-driven hardening work. Keep it current when a step is completed or intentionally deferred.

## Current Status

Completed:

- repository hygiene and CI matrix;
- removal of tracked `bin` / `obj` generated output;
- README and wiki refresh for the .NET 10 SDK / stable interceptor API;
- generated source baselines under `Baselines`;
- safer attribute literal generation;
- initial hook diagnostics `AG0101` and `AG0102`;
- hook contract diagnostics `AG0103` through `AG0107`;
- shared analysis model consumed by diagnostics and interceptor emission;
- build-time interceptor emission gate through `AspectGeneratorGenerateInterceptors` / `DesignTimeBuild`;
- `ValueTask` and `ValueTask<T>` async target support.

Not completed:

- `AG0004` severity policy;
- AOP-like target filters;
- type-level aspects and suppressor model;
- generated standard aspects;
- aspect-specific parameter and return modifiers;
- `ref` / `out` / `in` hardening;
- typed argument passing;
- public API contract stabilization;
- lazy `MemberInfo` initialization;
- `InterceptMethods` diagnostics.

## Capability Roadmap

The next feature work should extend capabilities around the existing interceptor mechanism instead of starting a broad architectural rewrite.

Priority:

- P0: AOP-like target filters, type-level aspects, suppressors, and a generated `Counter` aspect.
- P1: generated `Log` aspect with dependency-free backends, aspect-specific modifiers, and external logging backends.
- P2: cache aspect, validation attributes, `ref` / `out` / `in` hardening, and better diagnostics.
- P3: typed argument passing, lazy `MemberInfo`, and public API stabilization.

## P0: AOP-Like Target Filters

Status: not implemented.

Problem: applying an aspect currently requires explicit method-level attributes or low-level `InterceptMethods` strings. A simple ordered regex filter model would allow applying aspects to matching target methods while keeping interception scoped to visible call sites in the current compilation.

Scope:

- filters are regex strings;
- filters apply to target method canonical signatures, not call-site text;
- filters are evaluated in order;
- a filter starting with `-` is an exclude filter;
- the last matching filter wins inside one filter set;
- no matching filter means the filter set does not apply;
- explicit method-level aspect attributes remain explicit local intent and are not suppressed by filter excludes;
- future suppressors such as `[NoLog]`, `[NoCounter]`, or `[AspectIgnore]` remain a separate feature.

Public / generated API:

- [ ] Add `string[]? Filter { get; set; }` to generated `AspectAttribute`.
- [ ] Add generated `AspectFilterAttribute` usable at assembly and type level.
- [ ] Use one combined attribute target if separate assembly/type attributes create C# usability issues:
  - `AttributeTargets.Assembly`;
  - `AttributeTargets.Class`;
  - `AttributeTargets.Struct`.

Filter sources:

- [ ] Support aspect declaration-level filters through `[Aspect(Filter = [...])]`.
- [ ] Support assembly-level filters through `[assembly: AspectFilter(typeof(SomeAspectAttribute), Filter = [...])]`.
- [ ] Support type-level filters through `[AspectFilter(typeof(SomeAspectAttribute), Filter = [...])]`.
- [ ] Evaluate each filter set independently.
- [ ] Include an aspect when any filter set evaluates to include.
- [ ] Keep negative filters local to their own filter set.

Canonical method filter signature:

```text
<accessibility>[ <modifier>...] <return-type> <containing-type>.<method-name>[<type-parameters>](<parameter-types>)
```

Formatting rules:

- [ ] Include accessibility: `public`, `protected`, `internal`, `private`, `protected internal`, `private protected`.
- [ ] Include stable modifiers: `static`, `abstract`, `virtual`, `override`, `sealed`, `extern`, and `unsafe` if available from Roslyn symbol information.
- [ ] Do not include `async`, `partial`, parameter names, nullable annotations, attributes, or generic constraints.
- [ ] Use fully qualified type names without C# aliases, for example `System.String`, `System.Int32`, `System.Void`.
- [ ] Format generic types as readable fully qualified generic syntax, for example `System.Threading.Tasks.Task<System.String>`.
- [ ] Format parameters as modifier plus type only, for example `ref System.Int32`, `out System.Int32`, `in MyApp.Model`, `params System.String[]`.
- [ ] Include `this` receiver for extension methods.

Diagnostics:

- [ ] Add `AG0201` for invalid aspect filter regex.
- [ ] Use `RegexOptions.CultureInvariant`.
- [ ] Do not use `IgnoreCase` by default.
- [ ] Use a regex timeout to protect IDE and design-time builds.
- [ ] Postpone "filter matched no target methods" diagnostics.

Implementation constraints:

- [ ] Do not reintroduce full `Compilation.SyntaxTrees` / `DescendantNodes()` scanning.
- [ ] Evaluate filters against invocation target methods using the existing `SyntaxProvider` candidate path.
- [ ] Start with normal methods only.
- [ ] Preserve existing behavior for explicit method-level aspects, external aspect attributes, `InterceptMethods`, diagnostics, `GenerateInterceptors`, `DesignTimeBuild`, and `InterceptorsNamespaces`.

Tests:

- [ ] Add canonical signature formatter tests:
  - public instance method;
  - static method;
  - void return;
  - `Task<T>` return;
  - generic method;
  - generic containing type;
  - `ref`, `out`, and `in`;
  - extension method receiver;
  - array parameter;
  - nullable annotations ignored;
  - `virtual` / `override`.
- [ ] Add generator-driver test for aspect declaration-level positive filter.
- [ ] Add generator-driver test for aspect declaration-level negative filter.
- [ ] Add generator-driver test for last matching filter wins.
- [ ] Add generator-driver test for assembly-level `AspectFilterAttribute`.
- [ ] Add generator-driver test for type-level `AspectFilterAttribute`.
- [ ] Add generator-driver test proving explicit method-level aspect still applies when no filter matches.
- [ ] Add generator-driver test for invalid regex diagnostic `AG0201`.
- [ ] Add generator-driver tests for `AspectGeneratorGenerateInterceptors=false` and `DesignTimeBuild=true`.

Documentation:

- [ ] Document ordered regex include / exclude filters.
- [ ] Document canonical method signature format.
- [ ] Document `-` prefix as exclude rule.
- [ ] Document last matching filter wins.
- [ ] Document that filters select target methods, while AspectGenerator still rewrites only visible call sites in the current compilation.

## P0: Type-Level Aspects And Suppressors

Status: not implemented.

Problem: aspects are currently method-level. A type-level aspect should apply to eligible methods declared by the type, with method-level composition and suppression rules.

Target shape:

```csharp
[Log]
public class Service
{
	public void A() {}

	[AspectIgnore]
	public void HealthCheck() {}
}
```

Checklist:

- [ ] Define eligible method set for type-level aspects.
- [ ] Apply type-level aspects to call sites for methods declared in the target type.
- [ ] Add a general suppressor attribute, for example `[AspectIgnore]` and `[AspectIgnore(typeof(LogAttribute))]`.
- [ ] Support aspect-specific suppressors generated by standard aspects, for example `[NoLog]` and `[NoCounter]`.
- [ ] Define method-level composition rules:
  - method-level aspect extends type-level aspect;
  - method-level suppressor disables matching type-level aspect;
  - ordering remains deterministic.
- [ ] Add tests for type-level aspect application, method-level suppression, inherited / nested type behavior, and multiple aspects.

## P0: Generated `Counter` Aspect

Status: not implemented.

Problem: standard aspects need a first built-in scenario that validates generation, type-level aspects, and suppressors without external dependencies.

Checklist:

- [ ] Add opt-in generation, for example `[assembly: CounterAspectOptions(Generate = true)]`.
- [ ] Generate `[Counter]` and `[NoCounter]`.
- [ ] Track per-method counters.
- [ ] Provide reset/read API suitable for tests and debugging.
- [ ] Use `Counter` tests to validate type-level aspects and method-level suppressors.
- [ ] Defer call-site counters until the per-method model is stable.

## P1: Generated `Log` Aspect

Status: not implemented.

Problem: logging is a likely standard aspect, but it should be built after type-level aspects and the modifier model to avoid special-case behavior.

Checklist:

- [ ] Add opt-in generation, for example `[assembly: LogAspectOptions(Generate = true, Backend = LogBackend.Trace)]`.
- [ ] Start with dependency-free backends only:
  - `Trace`;
  - `Debug`;
  - `Console`.
- [ ] Generate `[Log]`, `[NoLog]`, `[LogIgnore]`, and `[LogSensitive]`.
- [ ] Log enter / exit.
- [ ] Log exceptions.
- [ ] Support optional parameter logging.
- [ ] Support optional return value logging.
- [ ] Mask sensitive values and omit ignored values.

## P1: Aspect Modifiers

Status: not implemented.

Problem: aspect-specific parameter and return modifiers are needed by `LogIgnore`, `LogSensitive`, future `CacheKey`, and similar features. These should not be hardcoded per aspect.

Checklist:

- [ ] Design a generic modifier model, for example `AspectModifierForAttribute`.
- [ ] Support parameter modifiers.
- [ ] Support return value modifiers.
- [ ] Generate aspect-specific modifier attributes from standard aspects.
- [ ] Add diagnostics for modifiers used with incompatible aspects or invalid targets.

## P1: External Logging Backends

Status: deferred until generated `Log` is stable.

Problem: external logging backends are useful but should not introduce additional NuGet packages per backend.

Checklist:

- [ ] Support `Microsoft.Extensions.Logging`.
- [ ] Support NLog.
- [ ] Support Serilog.
- [ ] Support custom static method backend.
- [ ] Emit code that assumes the user project already references the selected backend package.
- [ ] Add diagnostics for backend type not found, logger member not found, and unsupported logger member type.

## P2: Cache Aspect

Status: not implemented.

Problem: caching is useful but can easily grow into distributed cache and expiration policy scope. Start with a small local cache model.

Checklist:

- [ ] Add opt-in generation, for example `[assembly: CacheAspectOptions(Generate = true)]`.
- [ ] Generate `[Cache]`, `[NoCache]`, `[ClearCache]`, `[CacheKey]`, and `[NoCacheKey]`.
- [ ] Use static `ConcurrentDictionary` per method as the initial storage model.
- [ ] Support primitive, string, enum, `DateTime`, and `Guid` key parts.
- [ ] Add diagnostics for unsupported key types.
- [ ] Defer distributed cache, expiration policies, and async invalidation.

## P2: Validation Attributes

Status: not implemented.

Problem: validation attributes are separate from aspect modifiers. They should validate parameters and return values rather than configure another aspect.

Checklist:

- [ ] Design standalone validation mechanism.
- [ ] Support `[NotNull]`.
- [ ] Support `[NotEmpty]`.
- [ ] Support `[Range]`.
- [ ] Consider regex validation later.
- [ ] Support return value validation, for example `[return: NotNull]`.

## P1: Incremental Generator Pipeline

Status: partially implemented.

Problem: the generator implements `IIncrementalGenerator`. The previous interception discovery path used `CompilationProvider` and scanned all syntax trees with `DescendantNodes()` to find invocations. That full syntax-tree scan has been removed; invocation candidates now come from `SyntaxProvider`. Diagnostics and interceptor emission now consume a shared analysis result, and `Interceptors.g.cs` emission is disabled by default during design-time builds.

Checklist:

- [x] Split the pipeline into independent incremental sources:
  - aspect definitions;
  - invocation candidates;
  - MSBuild / assembly options.
- [x] Use `SyntaxProvider` for `InvocationExpressionSyntax` candidates.
- [x] Add early syntactic filtering before semantic model access.
- [x] Resolve semantic information only for candidate invocations.
- [x] Build and reuse an aspect metadata map.
- [x] Preserve support for:
  - aspect attributes from the current compilation;
  - aspect attributes from referenced assemblies;
  - cross-project scenarios;
  - `InterceptMethods`.
- [ ] Add a synthetic performance regression test or benchmark project with many invocations.
- [ ] Re-run existing cross-project tests after each refactor step.

## P1: Generated Interceptor Type Shape

Status: completed.

Problem: generated code still emits a fixed `static partial class Interceptors` in the configured namespace, which can collide with user code or other generated code.

Checklist:

- [x] Use a file-local generated interceptor type to avoid collisions with user code.
- [x] Preserve configurable interceptor namespace.
- [x] Keep generated source deterministic.
- [x] Update generated source baselines after choosing the final shape.

## P2: `AG0004` Severity Policy

Status: not implemented.

Problem: `AG0004` is currently a warning when the generated interceptor namespace is not listed in `InterceptorsNamespaces`. In practice, that configuration usually means interception is broken.

Checklist:

- [ ] Decide whether `AG0004` should become an error.
- [ ] Document the severity policy.
- [ ] Add a focused diagnostics test for the chosen behavior.

## P2: Better Diagnostics

Status: partially implemented.

Problem: hook contract diagnostics exist, but several silent no-op or hard-to-debug scenarios still need explicit diagnostics.

Checklist:

- [x] Add hook diagnostics `AG0101` through `AG0107`.
- [ ] Add unmatched `InterceptMethods` entry diagnostic.
- [ ] Add ambiguous `InterceptMethods` match diagnostic.
- [ ] Add alias-sensitive `InterceptMethods` match diagnostic.
- [ ] Add diagnostics for aspects applied to unsupported targets, such as constructors, properties, operators, and local functions.
- [ ] Add diagnostic for hook declared in aspect configuration but never used by generated code.
- [ ] Revisit `OnCall` not-last diagnostics and severity.
- [ ] Decide and document `AG0004` namespace-not-allowed severity.

## P2: `ref` / `out` / `in` Hardening

Status: partially implemented.

Problem: `ref`, `out`, and `in` parameters are supported, but this is a high-risk area for wrapper generation, `OnCall` compatibility, and `MethodArguments` behavior.

Checklist:

- [ ] Add focused test where a `ref` parameter is observed and updated correctly.
- [ ] Add focused test where an `out` parameter is assigned correctly.
- [ ] Add focused test where an `in` parameter is passed correctly.
- [ ] Add `OnCall` compatibility tests for `ref`, `out`, and `in`.
- [ ] Add `MethodArguments` behavior tests for `ref`, `out`, and `in`.
- [ ] Add diagnostics for unsupported hook signatures involving by-ref parameters.

## P2: Public API Contract

Status: deferred.

Problem: `PublicApi=true` remains experimental. Generated `InterceptInfo`, `InterceptInfo<T>`, and `InterceptData<T>` still expose mutable fields.

Checklist:

- [ ] Keep existing fields for compatibility in the generated/internal path.
- [ ] Decide whether public API should use properties, a separate runtime package, or an explicit versioned contract.
- [ ] Document migration guidance before any breaking change.

## P2: `MemberInfo` Initialization

Status: deferred.

Problem: generated interceptors initialize `MemberInfo` through expression-tree helper methods in static fields. This is not a per-call allocation, but it can add first-use cost because the generated interceptor type initializes static `MemberInfo` fields before the value may actually be needed by a hook.

Checklist:

- [ ] Keep the current expression-tree approach as the baseline.
- [ ] Measure first-use cost on a project with many intercepted methods.
- [ ] Design an explicit configuration option for `MemberInfo` generation, for example always generate, lazy generate, or disable when the user knows hooks do not read `InterceptInfo.MemberInfo`.
- [ ] Prefer an explicit option over trying to infer hook usage from parameter types.
- [ ] If implemented, generate lazy static `MemberInfo` initialization only for modes that require `MemberInfo`.
- [ ] Document the performance tradeoff and compatibility behavior.

## Completed: `ValueTask`

Status: implemented.

Checklist:

- [x] Support `ValueTask` / `ValueTask<T>` target methods.
- [x] Allow async hooks to return `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>`.
- [x] Keep `async void` unsupported with diagnostics.
- [x] Add tests for exception flow, finally flow, and return value mutation.

## P3: Typed Argument Passing

Status: deferred.

Problem: `MethodArguments` and `AspectArguments` use `object?[]` and dictionaries. This is flexible but allocates and loses type information.

Checklist:

- [ ] Keep current `object?[]` / dictionary model as the compatibility baseline.
- [ ] Investigate generated typed structs for method arguments.
- [ ] Avoid `object?[]` allocations when `PassArguments=false`.
- [ ] Consider typed parameter access for generated standard aspects.
- [ ] Measure performance before adding codegen complexity.

## Deferred: `InterceptMethods` Diagnostics

Status: deferred until after hook contract diagnostics and generator pipeline hardening.

Do not implement this in the current scope unless explicitly requested.

Checklist:

- [ ] Add unmatched method display string diagnostic.
- [ ] Add ambiguous match diagnostic.
- [ ] Add alias-sensitive match diagnostic.
- [ ] Design a safer typed alternative separately.
