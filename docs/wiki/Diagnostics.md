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
- `AG0209`: invalid `AspectGeneratorAspectDiagnosticSeverity` or `AspectGeneratorOptions.AspectDiagnosticSeverity` value.
- `AG0300`: marks a call where AspectGenerator applies aspects. Controlled by `AspectGeneratorAspectDiagnosticSeverity`; default severity is `Info`.

## Build Report

Informational compile-time output is written to the AspectGenerator build report, not to compiler diagnostics. The report uses Markdown-friendly text with summary, generated source files, target methods, intercepted call sites, source locations, applied aspects, and generated interceptor names.

Default report path:

```text
obj/GeneratedFiles/AspectGenerator/AspectGeneratorBuildReport.md
```

The report is not printed to the console. Users who need it can inspect the report file directly. Design-time builds do not write the report.

## Intercepted Call Markers

AspectGenerator can mark calls where aspects are applied by reporting optional `AG0300` diagnostics. The default severity is `Info`:

```xml
<PropertyGroup>
  <AspectGeneratorAspectDiagnosticSeverity>Info</AspectGeneratorAspectDiagnosticSeverity>
</PropertyGroup>
```

Supported values are `Off`, `Hidden`, `Info`, `Warning`, and `Error`. Set `Off` to disable optional markers. Use `Warning` for audit-style builds, or `Error` when intercepted calls must be explicitly inspected before a commit.

`AG0300` shows where AspectGenerator applies aspects. Each marked call receives one diagnostic listing the applied aspect attributes and the generated interceptor method name.

The diagnostic is informational by default and does not indicate a problem. The package adds `AG0300` to `WarningsNotAsErrors`, so projects using `TreatWarningsAsErrors` do not fail when the marker severity is configured as `Warning`.

Use the build report for complete and baseline-friendly information. Use `AG0300` marker mode only as a temporary IDE/source-code inspection aid.
