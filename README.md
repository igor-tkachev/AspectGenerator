# Aspect Generator

[![Build](https://img.shields.io/github/actions/workflow/status/igor-tkachev/AspectGenerator/dotnet.yml?branch=master&label=build&logo=github&style=flat-square)](https://github.com/igor-tkachev/AspectGenerator/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/v/AspectGenerator?logo=nuget&style=flat-square)](https://www.nuget.org/packages/AspectGenerator)
[![NuGet downloads](https://img.shields.io/nuget/dt/AspectGenerator?logo=nuget&style=flat-square)](https://www.nuget.org/packages/AspectGenerator)
[![License](https://img.shields.io/github/license/igor-tkachev/AspectGenerator?style=flat-square)](LICENSE.txt)

Compile-time method aspects for C#: your code runs before, after and around calls to the methods you mark. No runtime proxies, no IL weaving: a source generator rewrites the call sites with C# interceptors.

Define an aspect:

```csharp
using System.Diagnostics;
using AspectGenerator;

[Aspect(
    OnBeforeCall = nameof(OnBeforeCall),
    OnAfterCall  = nameof(OnAfterCall),
    OnCatch      = nameof(OnCatch))]
[AttributeUsage(AttributeTargets.Method)]
sealed class LogAttribute : Attribute
{
    public static void OnBeforeCall(InterceptInfo info)
    {
        Console.WriteLine($"-> {info.MemberInfo.Name}");
        info.Tag = Stopwatch.StartNew();
    }

    public static void OnAfterCall(InterceptInfo info)
    {
        var time = (Stopwatch)info.Tag!;
        Console.WriteLine($"<- {info.MemberInfo.Name} took {time.ElapsedMilliseconds} ms");
    }

    public static void OnCatch(InterceptInfo info)
    {
        Console.WriteLine($"!! {info.MemberInfo.Name} threw: {info.Exception!.Message}");
    }
}
```

Mark a method and call it:

```csharp
var orders = new OrderService();

orders.Save(42);

try { orders.Save(-1); } catch (ArgumentException) { }

class OrderService
{
    [Log]
    public void Save(int id)
    {
        if (id < 0)
            throw new ArgumentException("bad id");

        Thread.Sleep(20); // some work
        Console.WriteLine($"   saved order {id}");
    }
}
```

Output:

```console
-> Save
   saved order 42
<- Save took 23 ms
-> Save
!! Save threw: bad id
```

The generator finds both calls to `Save` and redirects them to a generated method. Simplified, it looks like this:

```csharp
[InterceptsLocation(1, "fawaHq6u...")] // orders.Save(42)
[InterceptsLocation(1, "fawaHq6u...")] // orders.Save(-1)
public static void Save_Interceptor(this OrderService __this__, int id)
{
    var info = new InterceptInfo<Void> { MemberInfo = ..., Aspect = ... };

    try
    {
        LogAttribute.OnBeforeCall(info);

        if (info.InterceptResult != InterceptResult.Return)
        {
            __this__.Save(id);
            LogAttribute.OnAfterCall(info);
        }
    }
    catch (Exception ex)
    {
        info.Exception       = ex;
        info.InterceptResult = InterceptResult.ReThrow;

        LogAttribute.OnCatch(info);

        if (info.InterceptResult == InterceptResult.ReThrow)
            throw;
    }
}
```

The method itself is not changed. The aspect is plain code in your project, and the generated source can be read and debugged like any other.

## What you can do with it

- Trace calls with OpenTelemetry: start an activity per call, set its status on exit ([example](https://github.com/igor-tkachev/AspectGenerator/tree/master/Examples/OpenTelemetryAspect)).
- Run a method in a database transaction: commit on success, roll back on exception ([example](https://github.com/igor-tkachev/AspectGenerator/tree/master/Examples/TransactionAspect)).
- Swallow or replace exceptions, or replace the call entirely (`OnCatch` with `InterceptResult.IgnoreThrow`, `OnCall`); async hooks for `Task` and `ValueTask` methods (see [Hooks](#hooks)).
- Apply an aspect to many methods at once with a [target filter](#target-filters), for example every `public **.*Service.Save*(...)` in the assembly.
- Ship aspects in a library ([aspect library](https://github.com/igor-tkachev/AspectGenerator/tree/master/Examples/AspectLibrary), [multi-project](https://github.com/igor-tkachev/AspectGenerator/tree/master/Examples/MultiProject)).

A transaction aspect, shortened from the example (`DataConnection` is from [linq2db](https://github.com/linq2db/linq2db)):

```csharp
[Aspect(
    PassArguments = true,
    OnBeforeCall  = nameof(OnBeforeCall),
    OnFinally     = nameof(OnFinally))]
[AttributeUsage(AttributeTargets.Method)]
sealed class TransactionAttribute : Attribute
{
    public static void OnBeforeCall(InterceptInfo info)
    {
        if (info.MethodArguments!.OfType<DataConnection>().FirstOrDefault() is { Transaction: null } db)
        {
            db.BeginTransaction();
            info.Tag = db;
        }
    }

    public static void OnFinally(InterceptInfo info)
    {
        if (info.Tag is DataConnection db)
        {
            if (info.Exception is null)
                db.CommitTransaction();
            else
                db.RollbackTransaction();
        }
    }
}

[Transaction]
public static void Transfer(DataConnection db, int from, int to, decimal amount)
{
    // Both updates are committed together, or neither is.
}
```

## Install

```bash
dotnet add package AspectGenerator
```

It is a development dependency: the reference is added with `PrivateAssets="all"`, and nothing is deployed with your application. See [Installation details](#installation-details).

## Requirements

Build with .NET SDK 9.0.100 or later: Visual Studio 2022 17.12 or later, Rider 2024.3 or later, or the `dotnet` CLI. This is about the compiler, not the target framework: `net8.0` works, and so does `netstandard2.0` with `<LangVersion>latest</LangVersion>`. An older compiler cannot load the generator; the build reports `CS9057` and the aspects are not applied.

This is not runtime AOP. Only calls compiled in the project that references AspectGenerator are rewritten. Calls through reflection, delegates or already-compiled external assemblies are not intercepted. See [Requirements in detail](#requirements-in-detail) and [Supported Scenarios](#supported-scenarios).

## Why AspectGenerator?

| Approach | Strengths | Trade-offs |
| --- | --- | --- |
| AspectGenerator | Compile-time call-site rewriting, no runtime proxy dispatch, generated code, better fit for AOT/trimming-oriented applications | Requires the .NET 9 SDK or later (Roslyn 4.12) to build, intercepts only visible call sites, method-call syntax only |
| Runtime proxies | Mature and familiar; no compiler interceptor requirement | Calls must go through the proxy, runtime dispatch overhead, AOT/trimming concerns |
| IL weaving | Can cover broader code patterns | Rewrites assemblies, harder debugging and deployment model |

## Installation details

The package is a development dependency, so `dotnet add package` adds it with `PrivateAssets="all"`. If you edit the project file by hand, write the same thing:

```xml
<PackageReference Include="AspectGenerator" Version="1.1.2" PrivateAssets="all" />
```

AspectGenerator is a build-time generator, not a runtime reference. The package ships only the analyzer and the MSBuild props and targets that configure it; it has no assembly your code references and nothing is deployed with your application. The package imports the required MSBuild wiring automatically for projects that reference it directly. Because the analyzer needs that wiring (`InterceptorsNamespaces` and the generator options) to produce interceptors that compile, it must run only in the project that references the package directly. Marking the package private keeps it from flowing to the consumers of your library. A library that wants its consumers to get the generator opts in with `PrivateAssets="none"`; the wiring flows with it (see [Using AspectGenerator in libraries](#using-aspectgenerator-in-libraries)).

## Requirements in detail

Projects that use AspectGenerator must be built with .NET SDK 9.0.100 or later (Roslyn 4.12), which Visual Studio 2022 has shipped since version 17.12 (November 2024).

This is a build-toolchain requirement, not a target-framework requirement: it is about the build tools used by `dotnet build`, MSBuild, or Visual Studio. AspectGenerator itself targets `netstandard2.0`, and your project target framework is a separate decision. The project must be compiled with a Roslyn 4.12 or newer compiler (the .NET 9 SDK or later) because the generator uses the stable Roslyn interceptor API based on opaque `InterceptableLocation` data and the `InterceptorsNamespaces` property. Projects that target `net8.0` are supported when they are built with such an SDK. Older SDKs and the legacy `InterceptsLocation(filePath, line, character)` preview API are not supported.

The generated code uses nullable annotations and newer C# features, so a project whose default C# version is older, such as `netstandard2.0`, sets `<LangVersion>latest</LangVersion>`.

This does not mean the newest Visual Studio. Any Visual Studio 2022 from 17.12 on builds it, including the 17.14 Long-Term Servicing Channel release; so do Visual Studio 2026, JetBrains Rider 2024.3 or later, and the `dotnet` CLI with the .NET 9 or 10 SDK. An existing Visual Studio 2022 installation needs only an update, not a new major version, and the SDK installs side by side with older ones.

An older compiler, such as SDK 8.0.x or Visual Studio 2022 before 17.12, cannot load the generator: the build reports `CS9057` (the analyzer references a newer compiler version) and the aspects are silently not applied.

If the repository uses a `global.json`, require at least 9.0.100 and let newer feature bands roll forward:

```json
{
  "sdk": {
    "version": "9.0.100",
    "rollForward": "latestFeature"
  }
}
```

Aspects are applied by rewriting call sites visible to the current compilation. AspectGenerator finds interceptable call sites to the target methods and emits interceptor methods for those locations. Calls made through reflection, delegates, already-compiled external assemblies, or unsupported C# constructs are outside the interception model; [Supported Scenarios](#supported-scenarios) lists them.

## Configuration

AspectGenerator can be configured with MSBuild properties:

```xml
<PropertyGroup>
  <AspectGeneratorPublicApi>false</AspectGeneratorPublicApi>
  <AspectGeneratorDebuggerStepThrough>false</AspectGeneratorDebuggerStepThrough>
  <AspectGeneratorReportFile>$(BaseIntermediateOutputPath)\GeneratedFiles\AspectGenerator\AspectGeneratorBuildReport.md</AspectGeneratorReportFile>
  <AspectGeneratorReportSizeLimit>1024</AspectGeneratorReportSizeLimit>
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

AspectGenerator analyzes code during the editor's live analysis so IDE diagnostics and optional call-site markers can work; interceptor source is emitted by builds. The generator tells the two apart by the process that runs it: the compiler always builds, and so does every other host that may run the result (`dotnet watch`, test harnesses, workspace tools); only Visual Studio and the C# language server of VS Code follow `DesignTimeBuild`, and after a build they emit interceptors too, so that Hot Reload keeps the aspects of an edited method. This behavior is automatic and not user-configurable.

AspectGenerator writes an informational build report file during normal builds. The report is not printed to the console by default. Diagnostics are reserved for errors, warnings, and actionable misconfiguration.

For AI coding agents and automation-oriented reviews, see [AspectGenerator skill](https://github.com/igor-tkachev/AspectGenerator/blob/master/Skills/aspectgenerator/SKILL.md). It summarizes the required build toolchain, project configuration, aspect API, hook model, target filters, diagnostics, build report usage, and common review checks.

## Using AspectGenerator in libraries

The generator rewrites only the calls compiled in the project that references it, and the default reference is private, so by default it does not reach the consumers of a library. A library that ships public aspects for its consumers can pass the generator on with `PrivateAssets="none"` (the props and targets ship in `buildTransitive/`, so `InterceptorsNamespaces` and the options flow with it); otherwise each consumer references AspectGenerator itself. The [wiki page](https://github.com/igor-tkachev/AspectGenerator/wiki/Using-AspectGenerator-in-libraries) has a table of the scenarios (app, library with internal aspects, shared aspect library, opt-in flow, multi-project solutions, test projects, upgrading from 1.0.0).

```xml
<!-- Library whose public aspects should apply in its consumers -->
<ItemGroup>
  <PackageReference Include="AspectGenerator" Version="1.1.2" PrivateAssets="none" />
</ItemGroup>
<PropertyGroup>
  <AspectGeneratorPublicApi>true</AspectGeneratorPublicApi>
</PropertyGroup>
```

If the analyzer runs in a project without the package's props, the build reports warning `AG0005` that names the fix.

## Generated API Ownership

AspectGenerator normally generates the authoring/runtime API into each consuming compilation. This runtime API includes `AspectAttribute`, `InterceptInfo`, `InterceptInfo<T>`, `InterceptData<T>`, `InterceptResult`, and related types.

- `GenerateApi=true`: generate `AspectAttribute`, `InterceptInfo`, `InterceptData<T>`, `InterceptResult`, and related types.
- `GenerateApi=false`: do not generate the API. Use this when the API is supplied by an aspect library.
- `GenerateApi` unset (the default): generate the API, unless a referenced assembly already declares a public `AspectAttribute`; then that API is used.
- `PublicApi=false`: generated API is internal to the consuming assembly.
- `PublicApi=true`: generated API is public. Treat this mode as experimental until the public contract is stabilized.

Any project that exposes aspect attributes to other projects must set `AspectGeneratorPublicApi=true`. Consumer projects leave `AspectGeneratorGenerateApi` unset: they use the public runtime API of a referenced aspect library, or generate their own internal one when there is none. Only one assembly in an app's reference graph may expose the public API (otherwise CS0433).

Common modes:

| Mode | Configuration | Intended use |
| --- | --- | --- |
| Single project | `GenerateApi` unset, `PublicApi=false` | App defines and uses its own aspects. |
| Aspect library | Library: `PublicApi=true`; consumer: `GenerateApi` unset | Shared aspect attributes live in a reusable assembly. |
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

`TargetFilter` applies an aspect to matching target methods. `DefaultTargetFilter` on `[Aspect]` lets the aspect author prepend default rules to every assembly-level or type-level application of that aspect. Unprefixed rules and `pattern:` use the native AspectGenerator target pattern syntax; `contains:` and `regex:` match the canonical method signature.

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

`DefaultTargetFilter` belongs to the aspect definition, not to the applied aspect attribute. V1 uses simple concatenation: `DefaultTargetFilter` lines first, applied `TargetFilter` lines second. Later matching rules still win, so a consumer `TargetFilter` can override a default exclusion unless it repeats that exclusion later.

The native `attributes:` condition matches method attributes and containing type attributes, including nested containing types. It accepts short names, short names without the `Attribute` suffix, and fully qualified names. Prefer fully qualified names such as `-attributes: MyCompany.Diagnostics.NoLogAttribute` for shared aspect libraries.

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

AspectGenerator writes a human-readable build report during normal builds. This report can be inspected by developers, reviewers, maintainers, or AI coding agents to understand which target methods and call sites were selected, which aspects were applied, and which interceptor methods were generated. This is informational build output, not runtime tracing or logging code.

The report uses Markdown-friendly text with summary, generated source files, target methods, intercepted call sites, source locations, applied aspects, generated interceptor names, and aspect lifetime information. It is optimized for inspection and may include local paths, source links, line and column locations, and generated source references. It is not intended to be a deterministic golden-file baseline format in V1.

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

The report is limited to 1,024 KB by default (`AspectGeneratorReportSizeLimit`, in kilobytes): a broad target filter on a large project selects tens of thousands of calls, and a report that lists each of them reaches tens of megabytes. The limit applies to the lists of target methods and call sites: the summary, the generated sources and, when it is on, the trace are always written in full, so with a limit smaller than they are the report is larger than the limit and the lists are empty. When the lists of target methods and call sites do not fit, the report lists their first rows (the target methods take at most half of the space left) and says under each list how many rows it left out. Set `AspectGeneratorReportSizeLimit` to `0` to list everything, or to a larger size; a value that is not a whole number of kilobytes uses the default.

```xml
<PropertyGroup>
  <AspectGeneratorReportSizeLimit>0</AspectGeneratorReportSizeLimit>
</PropertyGroup>
```

Diagnostics are reserved for errors, warnings, and actionable misconfiguration. The build report is informational, stored as a build artifact, and is not emitted as compiler diagnostics.

### Build Report Trace

For source-generator performance and behavior diagnostics, enable opt-in trace output:

```xml
<PropertyGroup>
  <AspectGeneratorTrace>true</AspectGeneratorTrace>
  <AspectGeneratorTraceLevel>Normal</AspectGeneratorTraceLevel>
</PropertyGroup>
```

Trace output is appended to `AspectGeneratorBuildReport.md` as an `AspectGenerator trace` section. It includes generator-visible options, `AGTRACE`-prefixed events, counters, phase timings, and fast-path decisions. Supported trace levels are `Normal` and `Verbose`; unknown values fall back to `Normal`.

Trace is disabled by default and does not emit compiler diagnostics or runtime tracing/logging code. Trace works in the published (Release-built) package; there is no need to build AspectGenerator from source for it. A build with trace off does not collect it.

To investigate a build, build one consuming project and one target framework, and combine the report with normal MSBuild tools:

```bat
dotnet build MyProject.csproj -f net10.0 -p:AspectGeneratorTrace=true /clp:PerformanceSummary /bl
```

## Build-time performance

AspectGenerator avoids runtime proxy dispatch, but it performs compile-time analysis of candidate invocation expressions in the current compilation. Build-time cost depends on project size, number of invocation expressions, number of applied aspects, and `TargetFilter` complexity.

For large projects, prefer narrow `TargetFilter` rules for assembly-level aspects and use the build report to inspect selected target methods and intercepted call sites.

## Debugging generated interceptors

Generated interceptors are generated C# source and can be inspected together with the build report. Keep `AspectGeneratorDebuggerStepThrough=false` when debugging generated interceptor behavior. Set it to `true` when you want the debugger to step over generated AspectGenerator code where applicable.

## Intercepted Call Markers

AspectGenerator can mark calls where aspects are applied by reporting optional `AG0300` diagnostics. The default severity is `Info`:

```xml
<PropertyGroup>
  <AspectGeneratorAspectDiagnosticSeverity>Info</AspectGeneratorAspectDiagnosticSeverity>
</PropertyGroup>
```

Supported values are `Off`, `Hidden`, `Info`, `Warning`, and `Error`. Set `Off` to disable optional markers, use `Warning` for audit-style builds, or `Error` when intercepted calls must be explicitly inspected before a commit.

`AG0300` shows where AspectGenerator applies aspects. Each marked call receives one diagnostic listing the applied aspect attributes and the generated interceptor method name. The diagnostic is informational by default and does not indicate a problem. Use the build report for complete aspect application analysis.

The package adds `AG0300` to `WarningsNotAsErrors`, so projects using `TreatWarningsAsErrors` do not fail when the marker severity is configured as `Warning`.

## Releasing

A release is a tag: pushing `vX.Y.Z`, where `X.Y.Z` is the `<Version>` in `Source/AspectGenerator.csproj`, runs `.github/workflows/publish.yml`, which builds, tests, packs and publishes the package to nuget.org. A manual run of the workflow does everything except the publish.

Publishing uses nuget.org trusted publishing, so the repository holds no API key. It needs a trusted publishing policy on nuget.org (the owner of the `AspectGenerator` package, repository `igor-tkachev/AspectGenerator`, workflow file `publish.yml`, environment `release`), a `release` environment in the repository settings and the `NUGET_USER` secret (the nuget.org user name that owns the policy).

## Documentation And Wiki

The README is the concise entry point. The wiki should contain expanded pages with the same current terminology:

- [AspectGenerator skill](https://github.com/igor-tkachev/AspectGenerator/blob/master/Skills/aspectgenerator/SKILL.md) is the compact agent-oriented guide for configuration, usage, diagnostics, build reports, and review workflows.
- [CHANGELOG.md](CHANGELOG.md) contains release notes.
- [Configuration](https://github.com/igor-tkachev/AspectGenerator/wiki/Configuration)
- [Aspect library mode](https://github.com/igor-tkachev/AspectGenerator/wiki/Aspect-library-mode)
- [Using AspectGenerator in libraries](https://github.com/igor-tkachev/AspectGenerator/wiki/Using-AspectGenerator-in-libraries)
- [Hook lifecycle](https://github.com/igor-tkachev/AspectGenerator/wiki/Hook-lifecycle)
- [Target filters](https://github.com/igor-tkachev/AspectGenerator/wiki/Target-filters)
- [Diagnostics](https://github.com/igor-tkachev/AspectGenerator/wiki/Diagnostics)
- [Limitations](https://github.com/igor-tkachev/AspectGenerator/wiki/Limitations)

For diagnostic codes and troubleshooting, see [Diagnostics](https://github.com/igor-tkachev/AspectGenerator/wiki/Diagnostics).

When updating docs, keep README and wiki synchronized:

- document the .NET 9 SDK (Roslyn 4.12) minimum build requirement and avoid implying that `net9.0` or `net10.0` is required as the target framework;
- use `InterceptorsNamespaces`, not `InterceptorsPreviewNamespaces`;
- use `AspectGeneratorGenerateApi`, `AspectGeneratorPublicApi`, `AspectGeneratorDebuggerStepThrough`, and `AspectGeneratorInterceptorsNamespace`;
- describe `InterceptableLocation` as opaque compiler data;
- keep unsupported scenarios consistent between README and wiki.

Any remaining preview-era wiki page should be updated or marked with an “Obsolete preview documentation” warning.

## Development

`AspectGenerator.slnx` is the primary solution file for repository builds. `AspectGenerator.sln` is retained only for tool compatibility when present.

Run the main checks locally:

```bash
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet pack Source/AspectGenerator.csproj --no-build
```

Use `dotnet clean` for normal cleanup. `Clean-BuildArtifacts.ps1` is a repository helper for deleting `.vs`, `bin`, and `obj` folders when a deeper local cleanup is needed.

Build artifacts under `bin/` and `obj/` must not be committed. If generated source snapshots are needed, store them in explicit repository baseline folders such as `Baselines/GeneratedFiles`, not under `obj/GeneratedFiles`.

[Skills/aspectgenerator/SKILL.md](https://github.com/igor-tkachev/AspectGenerator/blob/master/Skills/aspectgenerator/SKILL.md) is the packaged agent skill entry point, with task guides under `docs/`. The package installs it at `skills/aspectgenerator/`; consumers can use it from their NuGet global-packages folder or copy it into `.agents/skills/aspectgenerator/` or `.claude/skills/aspectgenerator/`. [Skills/README.md](https://github.com/igor-tkachev/AspectGenerator/blob/master/Skills/README.md) describes maintenance and validation. `AGENTS.md` contains repository maintenance instructions for coding agents working in this repo.
