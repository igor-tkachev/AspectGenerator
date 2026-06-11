# Roadmap

The source-of-truth hardening checklist is stored in the repository as `docs/AspectGeneratorHardeningPlan.md`.

This page mirrors the high-level roadmap for wiki readers.

## P1: Incremental Generator Pipeline

Status: not implemented.

The generator still implements `IIncrementalGenerator`, but the main interception discovery path depends on `CompilationProvider` and scans all syntax trees with `DescendantNodes()` to find invocations.

Planned work:

- split the pipeline into independent incremental sources for aspect definitions, invocation candidates, and configuration;
- use `SyntaxProvider` for `InvocationExpressionSyntax` candidates instead of scanning all syntax trees manually;
- add early syntactic filtering before semantic model access;
- resolve semantic information only for candidate invocations;
- build and reuse an aspect metadata map;
- preserve same-compilation aspects, referenced aspect libraries, cross-project scenarios, and `InterceptMethods`;
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

## P2: `ValueTask`

Status: not supported.

`ValueTask` and `ValueTask<T>` are documented as unsupported.

Planned work:

- either keep explicit non-support with diagnostics/documentation;
- or add `ValueTask` / `ValueTask<T>` support with async hook tests.

## Deferred: `InterceptMethods` Diagnostics

Status: deferred until after hook contract diagnostics and generator pipeline hardening.

Do not include `InterceptMethods` diagnostics in the current implementation scope.

Planned work:

- unmatched method display string diagnostic;
- ambiguous match diagnostic;
- alias-sensitive match diagnostic;
- design a safer typed alternative separately.
