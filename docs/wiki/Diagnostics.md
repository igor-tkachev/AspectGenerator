# Diagnostics

AspectGenerator diagnostics are intended to report user mistakes before generated code fails to compile.

## Existing Diagnostics

- `AG0001`: `OnCall` aspect must be last when the aspect is applied directly.
- `AG0002`: `OnCall` aspect must be last when the aspect is inferred from method metadata.
- `AG0003` (warning): a call to a method that carries the aspect itself is not intercepted, because the compiler cannot intercept it or because generated code cannot express it. The message gives the reason; see [Limitations](Limitations).
- `AG0004`: generated interceptor namespace is not listed in `InterceptorsNamespaces`.
- `AG0006` (information): the same, for a call that only a target filter selected. A broad filter is expected to skip what it cannot rewrite, so this is not a warning; it has its own id so that it can be raised or silenced separately in `.editorconfig` (`dotnet_diagnostic.AG0006.severity`).
- `AG0005`: the analyzer runs without the package's MSBuild props (the `AspectGeneratorPropsImported` marker is not visible), and the generated interceptor namespace is not listed in `InterceptorsNamespaces`; typically the generator reached the project transitively or as a manual analyzer reference. Reference the package from the project, or have the library flow it on with `PrivateAssets="none"`; see [Using AspectGenerator in libraries](Using-AspectGenerator-in-libraries).

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

## Design-Time Builds

The IDE runs the generator on every edit in a design-time build (`DesignTimeBuild=true`). A design-time build emits no interceptors, so the generator does not select target methods or bind calls there; it compiles the target filters only. This keeps the editor responsive on large projects, but splits the diagnostics in two groups.

Reported live, in design-time builds and in builds:

- `AG0101` and `AG0102` for aspects declared in the project;
- `AG0201`, `AG0202`, `AG0204`, `AG0205`, `AG0206` for assembly-level and type-level target filters, including an aspect's `DefaultTargetFilter` at each type-level application;
- `AG0208`;
- `AG0209`.

Reported by builds only, because they need a selected target method or a bound call:

- `AG0001` and `AG0002`;
- `AG0103` through `AG0107`;
- `AG0101` and `AG0102` for aspects declared in referenced assemblies, which are checked against each intercepted target;
- `AG0003` through `AG0006`, which are about the generated interceptors and the calls they skip.

The build-only diagnostics appear when the project is built, in the build output and the IDE's error list from that build. `AG0300` markers come from a separate analyzer and are not affected.

## Build Report

AspectGenerator writes a human-readable build report during normal builds. The report is intended for developers, reviewers, maintainers, and AI coding agents that need to inspect how aspects were applied in the current compilation.

The report includes selected target methods, intercepted call sites, applied aspect attributes, generated interceptor method names, generated source references, source locations where available, selected generator options, and aspect lifetime information.

The report is not a compiler diagnostic stream and is not printed to the console by default. Diagnostics are reserved for errors, warnings, optional call-site markers, and actionable misconfiguration.

The report is intentionally optimized for inspection rather than golden-file comparison. It may include human-friendly details such as source links, line and column locations, generated source references, local paths, and Markdown formatting.

Default report path:

```text
obj/GeneratedFiles/AspectGenerator/AspectGeneratorBuildReport.md
```

The report is not printed to the console. Users who need it can inspect the report file directly. Design-time builds do not write the report.

## Build Report Trace

`AspectGeneratorTrace` adds internal generator diagnostics to the existing build report file:

```xml
<PropertyGroup>
  <AspectGeneratorTrace>true</AspectGeneratorTrace>
  <AspectGeneratorTraceLevel>Normal</AspectGeneratorTraceLevel>
</PropertyGroup>
```

The trace section contains generator-visible options, `AGTRACE`-prefixed events, counters, timings, and fast-path decisions such as skipped interceptor generation or skipped namespace validation. Supported trace levels are `Normal` and `Verbose`. Trace is disabled by default, does not create a second file, and does not emit compiler diagnostics.

Trace implementation is compiled into Debug-built AspectGenerator packages only. Release-built packages accept `AspectGeneratorTrace=true` but do not write an `AspectGenerator trace` section.

For local AGTRACE package debugging, pack AspectGenerator in Debug configuration:

```shell
# From the root of an AspectGenerator clone; add the output folder as a local NuGet package source.
dotnet pack Source/AspectGenerator.csproj -c Debug -o local-nuget -p:PackageVersion=2.0.0-dev.trace.1
```

For local package debugging, build one project and one target framework:

```bat
dotnet build MyProject.csproj -f net10.0 -p:AspectGeneratorTrace=true /clp:PerformanceSummary /bl
```

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

Use the build report for complete aspect application analysis. Use `AG0300` marker mode only as a temporary IDE/source-code inspection aid.
