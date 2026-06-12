# Roadmap

The source-of-truth hardening checklist is stored in the repository as `docs/AspectGeneratorHardeningPlan.md`.

This page mirrors the high-level roadmap for wiki readers.

## P1: Incremental Generator Pipeline

Status: partially implemented.

The previous full syntax-tree scan has been removed. Invocation candidates now come from `SyntaxProvider`; the remaining work is a performance regression test or benchmark.

Planned work:

- add a synthetic performance regression test or benchmark project with many invocations.

## P1: Generated Interceptor Type Shape

Status: not implemented.

Generated code still emits a fixed `static partial class Interceptors` in the configured namespace. This can collide with user code or other generated code.

Planned work:

- evaluate `file static class AspectGeneratorInterceptors_<stable hash>`;
- preserve configurable interceptor namespace;
- update generated source baselines after choosing the final shape.

## P2: `AG0004` Severity Policy

Status: not implemented.

`AG0004` is currently a warning when the generated interceptor namespace is not listed in `InterceptorsNamespaces`.

Planned work:

- decide whether this should become an error;
- document the severity policy;
- add a focused diagnostics test for the chosen behavior.

## P2: Public API Contract

Status: deferred.

`PublicApi=true` remains experimental. Generated `InterceptInfo`, `InterceptInfo<T>`, and `InterceptData<T>` still expose mutable fields.

Planned work:

- keep the existing fields for compatibility in the generated/internal path;
- decide whether public API should use properties, a separate runtime package, or an explicit versioned contract;
- document migration guidance before any breaking change.

## P2: `MemberInfo` Initialization

Status: deferred.

Generated interceptors still initialize `MemberInfo` through expression-tree helper methods.

Planned work:

- keep the current expression-tree approach as the baseline;
- generate lazy `MemberInfo` initialization only when a hook requires `MemberInfo`;
- measure whether the optimization is worth the added codegen complexity.

## Completed: `ValueTask`

Status: implemented.

`ValueTask` and `ValueTask<T>` targets are supported for async hooks. `async void` remains unsupported.

Completed work:

- support `ValueTask` and `ValueTask<T>` target methods;
- allow async hooks to return `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>`;
- cover exception flow, finally flow, and return value mutation with tests.

## Deferred: `InterceptMethods` Diagnostics

Status: deferred until after hook contract diagnostics and generator pipeline hardening.

Do not include `InterceptMethods` diagnostics in the current implementation scope.

Planned work:

- unmatched method display string diagnostic;
- ambiguous match diagnostic;
- alias-sensitive match diagnostic;
- design a safer typed alternative separately.
