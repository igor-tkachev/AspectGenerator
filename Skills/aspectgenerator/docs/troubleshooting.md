[AspectGenerator skill](../SKILL.md)

# Troubleshooting and build inspection

> You are here if aspects seem ignored, a call is missing, or you need to inspect a build or review a change.

## Build-tool requirement

AspectGenerator requires projects that use it to be built with .NET SDK 9.0.100 or later (Roslyn 4.12), which Visual Studio 2022 has shipped since version 17.12 (November 2024).

An older compiler, such as SDK 8.0.x or Visual Studio 2022 before 17.12, cannot load the generator: the build reports `CS9057` (the analyzer references a newer compiler version) and the aspects are silently not applied. When aspects seem to be ignored, check the build output for `CS9057` first.

This is a build-toolchain requirement, not necessarily the same thing as the project target framework requirement. The important part is the SDK/compiler used by `dotnet build`, MSBuild, or Visual Studio. AspectGenerator itself targets `netstandard2.0`; consuming projects need a Roslyn 4.12 or newer compiler (the .NET 9 SDK or later) because the generator uses the stable Roslyn interceptor API based on opaque `InterceptableLocation` data.

Do not describe this as "requires .NET 9" or "requires .NET 10". Say "requires the .NET 9 SDK or later to build"; a `net8.0` project built with that SDK works.

The legacy preview API `InterceptsLocation(filePath, line, character)` is not supported.

## Supported and unsupported scenarios

Supported:

```text
static methods
instance methods
extension methods
generic methods
async Task / Task<T> / ValueTask / ValueTask<T>
ref / out / in parameters
target filters
conditional aspects
```

Unsupported or outside the interception model:

```text
constructors
properties and indexers
operators
local functions
delegates
reflection
calls compiled in external assemblies
async void
unsupported C# invocation constructs
```

For external assemblies, rebuild the assembly containing the call site with AspectGenerator enabled.

## Build report

AspectGenerator writes a human-readable build report during normal builds. The report is intended for developers, reviewers, maintainers, and AI coding agents.

Default path:

```text
obj/GeneratedFiles/AspectGenerator/AspectGeneratorBuildReport.md
```

The report includes:

```text
summary of generator options
selected target methods
intercepted call sites
applied aspect attributes
declared/effective aspect lifetimes where available
generated interceptor method names
generated source file references
source locations where available
```

The report is not a compiler diagnostic stream and is not printed to the console by default. It is an inspection artifact. Agents should inspect it when asked to understand what AspectGenerator did in a build.

The report is intentionally human-friendly and agent-friendly, not a minimal deterministic baseline format. Absolute file links, line/column locations, generated source references, Markdown tables, and detailed call-site rows are useful and should not be removed merely because they are not golden-file friendly.

The report is limited to 1,024 KB by default. The summary is always complete; when the lists of target methods and call sites do not fit, the report says under each list how many rows it left out. Set `AspectGeneratorReportSizeLimit` (kilobytes) to `0` to list everything, for example `-p:AspectGeneratorReportSizeLimit=0` on the command line.

Design-time builds do not write the report, and a known editor never does, also after a build. The report's `Generator host` row names the process that wrote it; an IDE the generator does not know shows there as `unknown (<process>)` after editing in it — report that row, with the IDE's name and version, to have the IDE recognized.

### Trace mode

Use opt-in trace mode when investigating generator performance or no-op behavior:

```xml
<PropertyGroup>
  <AspectGeneratorTrace>true</AspectGeneratorTrace>
  <AspectGeneratorTraceLevel>Normal</AspectGeneratorTraceLevel>
</PropertyGroup>
```

Trace mode appends an `AspectGenerator trace` section to `AspectGeneratorBuildReport.md`. It records visible options, `AGTRACE`-prefixed events, counters, timings, and fast-path decisions. It does not emit compiler diagnostics and does not generate runtime tracing/logging code.

Trace works in the published package; no Debug build of AspectGenerator is needed.

Build a single consuming project and target framework:

```bat
dotnet build MyProject.csproj -f net10.0 -p:AspectGeneratorTrace=true /clp:PerformanceSummary /bl
```

Supported trace levels are `Normal` and `Verbose`; unknown values fall back to `Normal`.

## Investigation workflow

When asked to analyze a project using AspectGenerator:

```text
1. Verify the project is built with .NET SDK 9.0.100 or later and the build output has no `CS9057`.
2. Find every project containing call sites that should be intercepted.
3. Verify each such project references AspectGenerator directly or receives it through an explicit PrivateAssets="none" library reference.
4. Check AspectGeneratorGenerateApi / PublicApi ownership mode.
5. Check AspectGeneratorInterceptorsNamespace and InterceptorsNamespaces wiring.
6. Build normally, not only design-time.
7. Inspect AspectGeneratorBuildReport.md.
8. Use generated Interceptors.g.cs only after reading the report.
9. Treat AG0300 as optional marker diagnostics, not as the complete report.
```

When reviewing a change:

```text
look for unexpected target methods
look for missing call sites
look for duplicate aspects
look for wrong aspect ordering
look for OnCall not being last
look for Static vs Instance lifetime mistakes
look for unsupported constructors/properties/operators/delegates/reflection
look for projects that use aspect attributes but have neither a direct nor an opted-in transitive generator reference
```

## Current terminology

Use current terminology:

```text
InterceptorsNamespaces, not InterceptorsPreviewNamespaces
AspectGeneratorInterceptorsNamespace, not AG_InterceptorsNamespace
InterceptableLocation, not file/line/column InterceptsLocation
call-site rewriting, not runtime proxying
build report, not runtime log
build-toolchain requirement, not just target-framework requirement
```

Do not reintroduce public `GenerateInterceptors` documentation.
