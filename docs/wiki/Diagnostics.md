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
- `AG0300`: intercepted call-site marker. Disabled by default; enable with `AspectGeneratorMarkInterceptedCalls=true` for temporary IDE inspection.

## Build Report

Informational compile-time output is written to the AspectGenerator build report, not to compiler diagnostics. The report uses Markdown-friendly text with summary, generated source files, target methods, intercepted call sites, source locations, applied aspects, and generated interceptor names.

Default report path:

```text
obj/GeneratedFiles/AspectGenerator/AspectGeneratorBuildReport.md
```

The report is not printed to the console. Users who need it can inspect the report file directly. Design-time builds do not write the report.

## Intercepted Call Markers

AspectGenerator can optionally mark intercepted call sites with `AG0300` warning diagnostics.

This mode is disabled by default:

```xml
<PropertyGroup>
  <AspectGeneratorMarkInterceptedCalls>true</AspectGeneratorMarkInterceptedCalls>
</PropertyGroup>
```

When enabled, each actually intercepted call site receives one `AG0300` warning. Multiple aspects on the same call site produce one marker listing all applied aspect attribute names.

`AG0300` is informational and does not indicate a problem. The package adds `AG0300` to `WarningsNotAsErrors`, so projects using `TreatWarningsAsErrors` do not fail because of marker warnings.

Use the build report for complete and baseline-friendly information. Use `AG0300` marker mode only as a temporary IDE/source-code inspection aid.
