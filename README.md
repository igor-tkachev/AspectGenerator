# Aspect Generator

[![Build](https://img.shields.io/github/actions/workflow/status/igor-tkachev/AspectGenerator/dotnet.yml?branch=master&label=build&logo=github&style=flat-square)](https://github.com/igor-tkachev/AspectGenerator/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/v/AspectGenerator?logo=nuget&style=flat-square)](https://www.nuget.org/packages/AspectGenerator)
[![NuGet downloads](https://img.shields.io/nuget/dt/AspectGenerator?logo=nuget&style=flat-square)](https://www.nuget.org/packages/AspectGenerator)
[![License](https://img.shields.io/github/license/igor-tkachev/AspectGenerator?style=flat-square)](LICENSE.txt)

AspectGenerator provides AOP-like method aspects for C# using compile-time call-site rewriting with source generators and C# interceptors. It lets you run code around method calls without runtime proxies, dynamic dispatch wrappers, or IL weaving.

AspectGenerator is not traditional runtime AOP. Aspects are applied to call sites visible to the current compilation.

> [!IMPORTANT]
> **Projects that use AspectGenerator must be built with the .NET 10 SDK/compiler.**
>
> This is a build-toolchain requirement, not a target-framework requirement. AspectGenerator itself targets `netstandard2.0`, and your project target framework is a separate decision. The project must be compiled with the .NET 10 SDK/compiler because the generator uses the stable Roslyn interceptor API based on opaque `InterceptableLocation` data. Older SDKs and the legacy `InterceptsLocation(filePath, line, character)` preview API are not supported.

> [!IMPORTANT]
> This is not runtime AOP. Aspects are applied by rewriting call sites visible to the current compilation. Calls made through reflection, delegates, already-compiled external assemblies, or unsupported C# constructs are outside the interception model.

## Quick Start

Install the package:

```bash
dotnet add package AspectGenerator
```

Build the consuming project with the .NET 10 SDK/compiler. This requirement is about the build tools used by `dotnet build`, MSBuild, or Visual Studio, not about forcing a specific `TargetFramework`.

For example, use a `global.json` to pin the repository to a .NET 10 SDK:

```json
{
  "sdk": {
    "version": "10.0.100"
  }
}
```

The AspectGenerator NuGet package imports the required MSBuild wiring automatically for projects that reference the package directly.

Define an aspect:

```csharp
using AspectGenerator;

namespace Aspects;

[Aspect(OnBeforeCall = nameof(OnBeforeCall))]
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
sealed class LogAttribute : Attribute
{
    public static void OnBeforeCall(InterceptInfo info)
    {
        Console.WriteLine($"Calling {info.MemberInfo.Name}");
    }
}
```

Use it on a method:

```csharp
using Aspects;

class Service
{
    [Log]
    public static void DoWork()
    {
    }
}

Service.DoWork();
```

AspectGenerator finds interceptable call sites to `DoWork` in the current compilation and emits interceptor methods for those locations.

## Configuration

AspectGenerator can be configured with MSBuild properties:

```xml
<PropertyGroup>
  <AspectGeneratorGenerateApi>true</AspectGeneratorGenerateApi>
  <AspectGeneratorPublicApi>false</AspectGeneratorPublicApi>
  <AspectGeneratorDebuggerStepThrough>false</AspectGeneratorDebuggerStepThrough>
  <AspectGeneratorReportFile>$(BaseIntermediateOutputPath)\GeneratedFiles\AspectGenerator\AspectGeneratorBuildReport.md</AspectGeneratorReportFile>
  <AspectGeneratorAspectDiagnosticSeverity>Info</AspectGeneratorAspectDiagnosticSeverity>
  <AspectGeneratorInterceptorsNamespace>AspectGenerator</AspectGeneratorInterceptorsNamespace>
</PropertyGroup>
```

The same settings can be overridden with an assembly-level attribute:

```csharp
using AspectGenerator;

[assembly: AspectGeneratorOptions(
    GenerateApi = true,
    PublicApi = false,
    DebuggerStepThrough = false,
    AspectDiagnosticSeverity = AspectDiagnosticSeverity.Info,
    InterceptorsNamespace = "AspectGenerator")]
```

`AspectGeneratorOptionsAttribute` and `AspectDiagnosticSeverity` are generated automatically for the current project and are used only to configure AspectGenerator. They are always internal and do not participate in aspect-library runtime API ownership.

Configuration is evaluated per project because the generator and analyzer run in a single compilation. Use `Directory.Build.props` or a linked source file with `[assembly: AspectGeneratorOptions(...)]` to share configuration across projects; each project still applies that configuration to its own compilation. Runtime API ownership is separate and remains controlled by `AspectGeneratorGenerateApi` and `AspectGeneratorPublicApi`.

`AspectGeneratorInterceptorsNamespace` controls the namespace used for generated interceptors. The package appends this namespace to `InterceptorsNamespaces` automatically for direct package consumers. Manual `InterceptorsNamespaces` configuration should only be needed for unusual or custom build setups.

The package `.props` asset defines defaults early. The package `.targets` asset appends `InterceptorsNamespaces` late, after project-level overrides such as `AspectGeneratorInterceptorsNamespace` are evaluated.

AspectGenerator analyzes code during design-time builds so IDE diagnostics and optional call-site markers can work. Interceptor source is emitted only during normal builds. This behavior is automatic and not user-configurable.

AspectGenerator writes an informational build report file during normal builds. The report is not printed to the console by default. Diagnostics are reserved for errors, warnings, and actionable misconfiguration.

## Generated API Ownership

AspectGenerator normally generates the authoring/runtime API into each consuming compilation. This runtime API includes `AspectAttribute`, `InterceptInfo`, `InterceptInfo<T>`, `InterceptData<T>`, `InterceptResult`, and related types.

- `GenerateApi=true`: generate `AspectAttribute`, `InterceptInfo`, `InterceptData<T>`, `InterceptResult`, and related types.
- `GenerateApi=false`: do not generate the API. Use this when the API is supplied by an aspect library.
- `PublicApi=false`: generated API is internal to the consuming assembly.
- `PublicApi=true`: generated API is public. Treat this mode as experimental until the public contract is stabilized.

Any project that exposes aspect attributes to other projects must set `AspectGeneratorPublicApi=true`. Consumer projects can either generate their own internal runtime API for local aspects or set `AspectGeneratorGenerateApi=false` and use the public runtime API supplied by an aspect library.

Common modes:

| Mode | Configuration | Intended use |
| --- | --- | --- |
| Single project | `GenerateApi=true`, `PublicApi=false` | App defines and uses its own aspects. |
| Aspect library | Library: `GenerateApi=true`, `PublicApi=true`; consumer: `GenerateApi=false` | Shared aspect attributes live in a reusable assembly. |
| Cross-project app | Same interceptor namespace in each project via `AspectGeneratorInterceptorsNamespace` | Intercept calls compiled in multiple projects. |

Every project whose call sites should be intercepted must reference AspectGenerator directly. A shared aspect library may reference AspectGenerator to define and build aspect attributes, but applications that use those aspect attributes must also reference AspectGenerator directly if their call sites should be intercepted. The source generator runs in the compilation of the project that references the generator package.

## Supported Scenarios

| Scenario | Status | Notes |
| --- | --- | --- |
| Static methods | Supported | Ordinary member method calls only. |
| Instance methods | Supported | Call site must be visible to the current compilation. |
| Extension methods | Supported | Covered by unit tests. |
| Generic methods | Supported | Covered by unit tests. |
| Target filters | Supported | Native condition patterns, compact method patterns, contains, and regex filters apply aspects to matching target methods. |
| `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>` async methods | Supported | Async hooks are selected for supported async targets. |
| `ref`, `out`, `in` parameters | Supported | Covered by unit tests. |
| Constructors | Unsupported | Platform limitation of C# interceptors. |
| Properties and indexers | Unsupported | Not ordinary method invocation syntax. |
| Operators | Unsupported | Not currently matched by the generator. |
| Delegates and reflection | Unsupported | No direct call site rewrite. |
| Local functions | Unsupported | Platform limitation. |
| Calls compiled in external assemblies | Unsupported | Rebuild the assembly containing the call site with AspectGenerator enabled. |
| `async void` | Unsupported | Async hooks require an awaitable target return type. |

## Hooks

`AspectAttribute` maps lifecycle hooks to static method names:

```csharp
[Aspect(
    OnInit = nameof(OnInit),
    OnUsing = nameof(OnUsing),
    OnUsingAsync = nameof(OnUsingAsync),
    OnBeforeCall = nameof(OnBeforeCall),
    OnBeforeCallAsync = nameof(OnBeforeCallAsync),
    OnCall = nameof(OnCall),
    OnAfterCall = nameof(OnAfterCall),
    OnAfterCallAsync = nameof(OnAfterCallAsync),
    OnCatch = nameof(OnCatch),
    OnCatchAsync = nameof(OnCatchAsync),
    OnFinally = nameof(OnFinally),
    OnFinallyAsync = nameof(OnFinallyAsync))]
```

Hook names are strings, so prefer `nameof(...)`. Invalid names or signatures should be reported by AspectGenerator diagnostics; if a case still fails only through generated-code compiler errors, treat that as a bug.

## Target Filters

`TargetFilter` applies an aspect to matching target methods. Unprefixed rules and `pattern:` use the native AspectGenerator target pattern syntax; `contains:` and `regex:` match the canonical method signature.

```csharp
[assembly: Log(
    TargetFilter = """
        # Include service saves and exclude health checks.
        public **.*Service.Save*(..., *CancellationToken)
        -contains: HealthCheck
        """)]

[Log(
    TargetFilter = "contains: Save")]
sealed class UserService
{
    public void SaveUser() {}
}

[Aspect(OnAfterCall = nameof(OnAfterCall))]
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
sealed class LogAttribute : Attribute
{
    public string? TargetFilter { get; set; }

    public static void OnAfterCall(InterceptInfo info) {}
}
```

Filters are ordered rules. The property name is always `TargetFilter`; only the property type can differ. It can be declared as `string?` or `string[]?` on an aspect attribute. Every string is split into lines, empty lines are ignored, and lines starting with `#` are comments. `string[]` values are processed as a simple concatenation of all rules. A rule starting with `-` excludes the target for that filter set, and the last matching rule wins. `TargetFilter` plays the role of a pointcut-like method selector in AOP terminology; AspectGenerator still rewrites only call sites visible to the current compilation.

Target filters are only supported on applied aspect attributes at assembly or type level. `[Aspect(TargetFilter = ...)]` is intentionally unsupported to avoid mixing aspect definition settings with aspect application.

The canonical signature format is:

```text
<accessibility>[ <modifier>...] <return-type> <containing-type>.<method-name>[<type-parameters>](<parameter-types>)
```

Types are fully qualified without C# aliases, nullable annotations and parameter names are omitted, extension method receivers are formatted with `this`, and generic calls use constructed type arguments.

## Conditional Aspects

Aspect attribute classes can be decorated with `System.Diagnostics.ConditionalAttribute`. When an aspect attribute class has `[Conditional("SYMBOL")]`, applying that aspect is ignored unless `SYMBOL` is defined for the consuming syntax tree/project.

Multiple `[Conditional]` attributes are treated as `OR`:

```csharp
using System.Diagnostics;
using AspectGenerator;

[Conditional("DEBUG")]
[Conditional("TRACE")]
[Aspect(OnAfterCall = nameof(OnAfterCall))]
sealed class LogAttribute : Attribute
{
    public static void OnAfterCall(InterceptInfo info) {}
}
```

This applies consistently to direct method aspect usage, type-level `TargetFilter`, and assembly-level `TargetFilter`.

Conditional aspect attributes are evaluated against project/tree preprocessor symbols. Project-level `DEBUG`, `TRACE`, and MSBuild-defined symbols are supported. Local source-file `#define` / `#undef` directive state is not guaranteed to be evaluated with full location sensitivity.

## Build Report

AspectGenerator writes a compile-time build report file during normal builds. This is informational build output, not runtime tracing or logging code. The report uses Markdown-friendly text with summary, generated source files, target methods, intercepted call sites, source locations, applied aspects, and generated interceptor names.

Default path:

```text
obj/GeneratedFiles/AspectGenerator/AspectGeneratorBuildReport.md
```

The report is not printed to the console. Users who need it can inspect the report file directly.

To change the report path or file name, set `AspectGeneratorReportFile`:

```xml
<PropertyGroup>
  <AspectGeneratorReportFile>$(MSBuildProjectDirectory)\artifacts\aspect-report.txt</AspectGeneratorReportFile>
</PropertyGroup>
```

Diagnostics are reserved for errors and warnings. The build report is informational, stored as a build artifact, and is not emitted as compiler diagnostics.

## Intercepted Call Markers

AspectGenerator can mark calls where aspects are applied by reporting optional `AG0300` diagnostics. The default severity is `Info`:

```xml
<PropertyGroup>
  <AspectGeneratorAspectDiagnosticSeverity>Info</AspectGeneratorAspectDiagnosticSeverity>
</PropertyGroup>
```

Supported values are `Off`, `Hidden`, `Info`, `Warning`, and `Error`. Set `Off` to disable optional markers, use `Warning` for audit-style builds, or `Error` when intercepted calls must be explicitly inspected before a commit.

`AG0300` shows where AspectGenerator applies aspects. Each marked call receives one diagnostic listing the applied aspect attributes and the generated interceptor method name. The diagnostic is informational by default and does not indicate a problem. Use the build report for complete and baseline-friendly information.

The package adds `AG0300` to `WarningsNotAsErrors`, so projects using `TreatWarningsAsErrors` do not fail when the marker severity is configured as `Warning`.

## Documentation And Wiki

The README is the concise entry point. The wiki should contain expanded pages with the same current terminology:

- [Configuration](https://github.com/igor-tkachev/AspectGenerator/wiki/Configuration)
- [Aspect library mode](https://github.com/igor-tkachev/AspectGenerator/wiki/Aspect-library-mode)
- [Hook lifecycle](https://github.com/igor-tkachev/AspectGenerator/wiki/Hook-lifecycle)
- [Target filters](https://github.com/igor-tkachev/AspectGenerator/wiki/Target-filters)
- [Diagnostics](https://github.com/igor-tkachev/AspectGenerator/wiki/Diagnostics)
- [Limitations](https://github.com/igor-tkachev/AspectGenerator/wiki/Limitations)

When updating docs, keep README and wiki synchronized:

- document the .NET 10 SDK/compiler requirement and avoid implying that `net10.0` is required as the target framework;
- use `InterceptorsNamespaces`, not `InterceptorsPreviewNamespaces`;
- use `AspectGeneratorGenerateApi`, `AspectGeneratorPublicApi`, `AspectGeneratorDebuggerStepThrough`, and `AspectGeneratorInterceptorsNamespace`;
- describe `InterceptableLocation` as opaque compiler data;
- keep unsupported scenarios consistent between README and wiki.

Any remaining preview-era wiki page should be updated or marked with an “Obsolete preview documentation” warning.

## Development

Run the main checks locally:

```bash
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet pack Source/AspectGenerator.csproj --no-build
```

Build artifacts under `bin/` and `obj/` must not be committed. If generated source snapshots are needed, store them in an explicit baseline folder under `UnitTests/`, not under `obj/GeneratedFiles`.
