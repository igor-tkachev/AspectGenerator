# Diagnostics

AspectGenerator diagnostics are intended to report user mistakes before generated code fails to compile.

## Existing Diagnostics

- `AG0001`: `OnCall` aspect must be last when the aspect is applied directly.
- `AG0002`: `OnCall` aspect must be last when the aspect is inferred from method metadata.
- `AG0003` (warning): a call to a method that carries the aspect itself is not intercepted, because the compiler cannot intercept it or because generated code cannot express it. The message gives the reason; see [Limitations](Limitations).
- `AG0004`: generated interceptor namespace is not listed in `InterceptorsNamespaces`.
- `AG0006` (information, **off by default**): the same, for a call that only a target filter selected, reported at the call. A broad filter is expected to skip what it cannot rewrite, so this is not a warning. It is off because a broad filter can skip thousands of calls, and the compiler pays several milliseconds for every diagnostic reported at a source location (it binds the enclosing method again to look for `[SuppressMessage]`); on a large project that added tens of seconds to a build. Turn it on in `.editorconfig` to get the list with locations: `dotnet_diagnostic.AG0006.severity = suggestion` shows it in the IDE and in a binary log (`-bl`), but information and suggestion diagnostics are not printed by a command-line build; use `warning` to see the list on the console.
- `AG0007` (information): one summary per reason for the calls `AG0006` would list, with their number, for example `1,697 calls selected by a target filter were not intercepted (calls inside expression trees are not intercepted: the expression must keep the original method). Enable AG0006 to list them.` It has no source location; the summaries are ordered by reason. Being information, it is not printed by a command-line build either; `dotnet_diagnostic.AG0007.severity = warning` makes it visible there. Because it has no location, a file section of `.editorconfig` (`[*.cs]`) does not apply to it: set its severity in a global config (a `.globalconfig`, or any analyzer config with `is_global = true`), or use `NoWarn` / `WarningsAsErrors`.
- `AG0005`: the analyzer runs without the package's MSBuild props (the `AspectGeneratorPropsImported` marker is not visible), and the generated interceptor namespace is not listed in `InterceptorsNamespaces`; typically the generator reached the project transitively or as a manual analyzer reference. Reference the package from the project, or have the library flow it on with `PrivateAssets="none"`; see [Using AspectGenerator in libraries](Using-AspectGenerator-in-libraries).

## Hook Contract Diagnostics

- `AG0101`: hook method not found on the aspect type or its base types. For an aspect of a referenced assembly the message says the hook may also be inaccessible: metadata does not show the internal members of another assembly without `InternalsVisibleTo`. When a hook the interceptor would call fails in a way its generated call cannot compile, that aspect is left out of the target's interceptor and the other aspects still apply (relevant when the severity is lowered); a synchronous hook that returns a value fails `AG0104` but is called as a statement, so it stays.
- `AG0102`: hook method must be static.
- `AG0103`: invalid hook parameter type.
- `AG0104`: invalid hook return type. The message names the return type the hook needs; for an async hook on a target framework without `ValueTask`, it says that only `Task` is available there. A return type the compiler cannot resolve is left to the compiler's own error (`CS0246`).
- `AG0105`: `OnCall` signature mismatch.
- `AG0106`: `UseInterceptData=true` requires `ref InterceptData<T>`.
- `AG0107`: async hook requires a supported async target.
- `AG0108`: the aspect has `OnAfterCall`, `OnCatch`, `OnFinally` or `OnUsing` hooks on an iterator method, whose body runs only while the returned sequence is enumerated, after those hooks; see [Hook lifecycle](Hook-lifecycle#iterators). A warning at the aspect applied to the method, information at the call when only a target filter selected it.
- `AG0201`: invalid aspect filter regex.
- `AG0202`: invalid target filter rule.
- `AG0204`: unknown target filter condition key.
- `AG0205`: invalid target filter parameter pattern.
- `AG0206`: invalid target filter dotted pattern.
- `AG0208`: `TargetFilter` is used on a method-level aspect attribute, including one applied to a local function.
- `AG0209`: invalid `AspectGeneratorAspectDiagnosticSeverity` or `AspectGeneratorOptions.AspectDiagnosticSeverity` value.
- `AG0300`: marks a call where AspectGenerator applies aspects. Controlled by `AspectGeneratorAspectDiagnosticSeverity`; default severity is `Info`.

## Design-Time Builds

The IDE runs the generator on every edit in a design-time build. A design-time build emits no interceptors, so the generator does not select target methods or bind calls there; it compiles the target filters only. This keeps the editor responsive on large projects, but splits the diagnostics in two groups.

Reported live, in design-time builds and in builds:

- `AG0101` and `AG0102` for aspects declared in the project;
- `AG0201`, `AG0202`, `AG0204`, `AG0205`, `AG0206` for assembly-level and type-level target filters, including an aspect's `DefaultTargetFilter` at each type-level application;
- `AG0208`;
- `AG0209`.

Reported by builds only, because they need a selected target method or a bound call:

- `AG0001` and `AG0002`;
- `AG0103` through `AG0108`;
- `AG0101` and `AG0102` for aspects declared in referenced assemblies, which are checked against each intercepted target;
- `AG0003`, `AG0006` and `AG0007`, for calls that are left as they are;
- `AG0004` and `AG0005`, which are about the generated interceptors (these were never reported in design-time builds).

Which mode applies is decided by the process that runs the generator, before the MSBuild properties:

- the compiler (`csc`, or the compiler server `VBCSCompiler`) always builds, so a `DesignTimeBuild=true` left set by the project does not switch a real build to design time;
- a known editor — Visual Studio 2022 and 2026, the C# language server of VS Code — follows the properties: `DesignTimeBuild=true` without `BuildingProject=true` is the live analysis; anything else is a build;
- every other host builds: `dotnet watch`, generator test harnesses such as `Microsoft.CodeAnalysis.Testing`, tools on `AdhocWorkspace` or `MSBuildWorkspace`, and editors the generator does not know. Any of them may emit and run the compilation, and a compilation built at design time silently has no aspects. An unknown editor pays the full analysis on every generator run. To have it recognized, open an issue with the `Generator host` row of `obj/GeneratedFiles/AspectGenerator/AspectGeneratorBuildReport.md` as it reads after editing in that IDE (an unknown host writes the report; the row names its process, e.g. `unknown (dotnet-watch)`), the IDE's name and version, and whether it supports Hot Reload.

The editor reads the properties from `obj/.../<Project>.GeneratedMSBuildEditorConfig.editorconfig`, which every real build rewrites with `BuildingProject=true` and an empty `DesignTimeBuild`. From then until the next design-time build the editor builds too — on purpose, see Hot Reload below — and the build-only diagnostics appear live in that period.

### Hot Reload and Edit and Continue

Visual Studio and `dotnet watch` compile a Hot Reload delta from their own workspace compilation, generator output included. If that compilation was produced at design time, the edited method is re-emitted without interceptors and its aspects stop applying, with no error shown. That is why the editor builds after a real build (Hot Reload always starts with one), and why `dotnet watch` and every other unknown host build always. If a design-time build runs between the build and the edit (a project file change, a restore), the editor is back at design time until the next build; start the debug session again after such a change.

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

The report is not printed to the console. Users who need it can inspect the report file directly. Design-time builds do not write the report, and a known editor never does, also when it builds after a real build (its report would describe an edit in progress); the `Generator host` row of the summary names the process that wrote it.

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
