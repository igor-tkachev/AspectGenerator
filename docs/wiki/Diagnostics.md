# Diagnostics

AspectGenerator diagnostics are intended to report user mistakes before generated code fails to compile.

## Existing Diagnostics

- `AG0001`: `OnCall` aspect must be last when the aspect is applied directly.
- `AG0002`: `OnCall` aspect must be last when the aspect is inferred from method metadata.
- `AG0003`: invocation cannot be intercepted by the compiler.
- `AG0004`: generated interceptor namespace is not listed in `InterceptorsNamespaces`.

## Hook Contract Diagnostics

- `AG0101`: hook method not found.
- `AG0102`: hook method must be static.
- `AG0103`: invalid hook parameter type.
- `AG0104`: invalid hook return type.
- `AG0105`: `OnCall` signature mismatch.
- `AG0106`: `UseInterceptData=true` requires `ref InterceptData<T>`.
- `AG0107`: async hook requires a supported async target.
- `AG0201`: invalid aspect filter regex.
- `AG0202`: invalid target filter rule.
- `AG0204`: unknown target filter condition key.
- `AG0205`: invalid target filter parameter pattern.
- `AG0206`: invalid target filter dotted pattern.
- `AG0208`: `TargetFilter` is used on a method-level aspect attribute.

## Compile-Time Reporting Diagnostics

Compile-time reporting diagnostics are source-generator diagnostics. They explain matching and generation decisions and do not add runtime tracing or logging code.

Reporting is configured with MSBuild-only `AspectGeneratorVerbosity` for the current reporting level and matching `[assembly: AspectGeneratorOptions(...)]` properties for per-category thresholds. Supported values are `Off`, `Quiet`, `Minimal`, `Normal`, `Detailed`, and `Diagnostic`. Defaults are `AspectGeneratorVerbosity=Quiet`, `SummaryVerbosity=Quiet`, `InterceptorsVerbosity=Minimal`, `TargetsVerbosity=Normal`, and `FiltersVerbosity=Diagnostic`. All `AG0700`-`AG0799` report diagnostics are emitted as `Info`.

- `AG0700`: summary report.
- `AG0710`: interceptor was generated for a selected call.
- `AG0711`: interceptor generation skipped an unsupported target.
- `AG0712`: interceptor generation skipped a non-selected target.
- `AG0720`: aspect selected a target method.
- `AG0721`: aspect was excluded from a target method.
- `AG0722`: aspect was skipped for a target method.
- `AG0723`: aspect was skipped because its `ConditionalAttribute` symbol is not defined.
- `AG0730`: `TargetFilter` rule or group matched a target.
- `AG0731`: `TargetFilter` rule or group did not match a target.
- `AG0732`: final `TargetFilter` decision for a target.
