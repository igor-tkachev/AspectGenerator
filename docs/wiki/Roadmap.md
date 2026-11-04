# Roadmap

The source-of-truth hardening checklist is stored in the repository as `docs/AspectGeneratorHardeningPlan.md`.

This page mirrors the high-level roadmap for wiki readers.

## P1: Incremental Generator Pipeline

Status: partially implemented.

The previous full syntax-tree scan has been removed. Invocation candidates now come from `SyntaxProvider`; the remaining work is a performance regression test or benchmark.

Planned work:

- add a synthetic performance regression test or benchmark project with many invocations.

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

## Completed: Generated Interceptor Type Shape

Status: implemented.

Interceptors are emitted into a file-local `file static class AspectGeneratorInterceptors` in the configured interceptor namespace, so the generated type cannot collide with user code or other generated code. The namespace stays configurable through `AspectGeneratorInterceptorsNamespace`, and generated source baselines cover the final shape.

## Completed: `MemberInfo` Initialization

Status: implemented.

Generated interceptors keep the expression-tree helper approach for target metadata, but the metadata is stored in per-interceptor static state holders. Each holder has its own explicit static constructor, so target metadata and static aspect instances are initialized lazily when that interceptor is first used.

Follow-up work:

- measure first-use cost on a project with many intercepted methods;
- consider an explicit option if users need to disable `InterceptInfo.MemberInfo` generation.

## Completed: `ValueTask`

Status: implemented.

`ValueTask` and `ValueTask<T>` targets are supported for async hooks. `async void` remains unsupported.

Completed work:

- support `ValueTask` and `ValueTask<T>` target methods;
- allow async hooks to return `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>`;
- cover exception flow, finally flow, and return value mutation with tests.
