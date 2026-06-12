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
- build-time interceptor emission gate through `AspectGeneratorGenerateInterceptors` / `DesignTimeBuild`.

Not completed:

- `AG0004` severity policy;
- public API contract stabilization;
- lazy `MemberInfo` initialization;
- `ValueTask` decision;
- `InterceptMethods` diagnostics.

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

## P2: Public API Contract

Status: deferred.

Problem: `PublicApi=true` remains experimental. Generated `InterceptInfo`, `InterceptInfo<T>`, and `InterceptData<T>` still expose mutable fields.

Checklist:

- [ ] Keep existing fields for compatibility in the generated/internal path.
- [ ] Decide whether public API should use properties, a separate runtime package, or an explicit versioned contract.
- [ ] Document migration guidance before any breaking change.

## P2: `MemberInfo` Initialization

Status: deferred.

Problem: generated interceptors still initialize `MemberInfo` through expression-tree helper methods even when hooks may not need it.

Checklist:

- [ ] Keep the current expression-tree approach as the baseline.
- [ ] Generate lazy `MemberInfo` initialization only when a hook requires `MemberInfo`.
- [ ] Measure whether the optimization is worth the added codegen complexity.

## P2: `ValueTask`

Status: not supported.

Checklist:

- [ ] Either keep explicit non-support with diagnostics and documentation.
- [ ] Or add `ValueTask` / `ValueTask<T>` support with async hook tests.

## Deferred: `InterceptMethods` Diagnostics

Status: deferred until after hook contract diagnostics and generator pipeline hardening.

Do not implement this in the current scope unless explicitly requested.

Checklist:

- [ ] Add unmatched method display string diagnostic.
- [ ] Add ambiguous match diagnostic.
- [ ] Add alias-sensitive match diagnostic.
- [ ] Design a safer typed alternative separately.
