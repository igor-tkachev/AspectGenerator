# AspectGenerator Agent Skill

Use this file as the compact agent-oriented guide for using and reviewing AspectGenerator in consuming projects.

AspectGenerator provides AOP-like compile-time method aspects for C# by rewriting visible call sites with C# interceptors. It is not runtime AOP, does not use runtime proxies, and does not weave IL.

## Build-tool requirement

AspectGenerator requires projects that use it to be built with .NET SDK 9.0.100 or later (Roslyn 4.12; Visual Studio 2022 17.12 or later).

An older compiler, such as SDK 8.0.x or Visual Studio 2022 before 17.12, cannot load the generator: the build reports `CS9057` (the analyzer references a newer compiler version) and the aspects are silently not applied. When aspects seem to be ignored, check the build output for `CS9057` first.

This is a build-toolchain requirement, not necessarily the same thing as the project target framework requirement. The important part is the SDK/compiler used by `dotnet build`, MSBuild, or Visual Studio. AspectGenerator itself targets `netstandard2.0`; consuming projects need a Roslyn 4.12 or newer compiler (the .NET 9 SDK or later) because the generator uses the stable Roslyn interceptor API based on opaque `InterceptableLocation` data.

Do not describe this as "requires .NET 9" or "requires .NET 10". Say "requires the .NET 9 SDK or later to build"; a `net8.0` project built with that SDK works.

The legacy preview API `InterceptsLocation(filePath, line, character)` is not supported.

## Package reference rule

AspectGenerator runs in the compilation that references the NuGet package.

Every project whose call sites should be intercepted must reference `AspectGenerator` directly. A shared aspect library may define aspect attributes and hooks, but applications or libraries containing the actual call sites must also reference `AspectGenerator` directly.

Use this rule when reviewing multi-project solutions:

```text
aspect definitions live in one project
call sites live in another project
=> both projects usually need direct AspectGenerator package references
```

The package imports the required MSBuild wiring automatically for direct package consumers.

AspectGenerator is a development dependency (`developmentDependency` in the nuspec): `dotnet add package AspectGenerator` adds it with `PrivateAssets="all"`. When editing a project file by hand, write `<PackageReference Include="AspectGenerator" Version="1.1.1" PrivateAssets="all" />`. The private reference does not flow: a project that consumes a library does not get the generator, so every project with call sites to intercept must reference it directly, unless the library opts in with `PrivateAssets="none"` (props and targets ship in `buildTransitive/`; see the wiki page "Using AspectGenerator in libraries"). Warning `AG0005` means the analyzer runs without the package props. It is a build-time generator; the package ships no runtime assembly.

## Generated API ownership

AspectGenerator can generate the authoring/runtime API into the current compilation:

```text
AspectAttribute
AspectInstanceLifetime
InterceptInfo
InterceptInfo<T>
InterceptData<T>
InterceptType
InterceptResult
Void
AspectGeneratorOptionsAttribute
AspectDiagnosticSeverity
```

Common modes:

```text
single project:
  AspectGeneratorGenerateApi unset
  AspectGeneratorPublicApi=false

aspect library exposing reusable aspects:
  AspectGeneratorGenerateApi unset
  AspectGeneratorPublicApi=true

consumer using public API from an aspect library:
  AspectGeneratorGenerateApi unset (a referenced public API is used instead of generating a copy)
  AspectGenerator reference (direct, or passed on by the library with PrivateAssets="none") AspectGenerator package reference still required if this project has call sites to intercept
```

`AspectGeneratorOptionsAttribute` and `AspectDiagnosticSeverity` are per-project configuration API. They are not the shared runtime contract for aspect libraries.

## MSBuild configuration

Relevant MSBuild properties:

```xml
<PropertyGroup>
  <AspectGeneratorPublicApi>false</AspectGeneratorPublicApi>
  <AspectGeneratorDebuggerStepThrough>false</AspectGeneratorDebuggerStepThrough>
  <AspectGeneratorReportFile>$(BaseIntermediateOutputPath)\GeneratedFiles\AspectGenerator\AspectGeneratorBuildReport.md</AspectGeneratorReportFile>
  <AspectGeneratorAspectDiagnosticSeverity>Info</AspectGeneratorAspectDiagnosticSeverity>
  <AspectGeneratorInterceptorsNamespace>AspectGenerator</AspectGeneratorInterceptorsNamespace>
</PropertyGroup>
```

`AspectGeneratorInterceptorsNamespace` controls the namespace used for generated interceptors. The package appends this value to `InterceptorsNamespaces` automatically. Do not use `InterceptorsPreviewNamespaces`.

Interceptor source emission is automatic:

```text
compiler (csc, VBCSCompiler)                       -> build: analyzes and emits interceptor source
known editor (Visual Studio, VS Code's C# server)  -> DesignTimeBuild=true without BuildingProject=true: live analysis,
                                                      no interceptor source; after a build (BuildingProject=true): build
any other host (dotnet watch, test harnesses,
  AdhocWorkspace/MSBuildWorkspace tools, unknown IDEs) -> build
```

The generator tells the hosts apart by the process (the entry assembly), not by the MSBuild properties alone. Every host that may emit and run the compilation builds; only a known editor skips the expensive work, and only before a build: after one, the editor's compilation feeds Hot Reload and Edit and Continue, whose delta would otherwise drop the aspects of the edited method. The editor reads `DesignTimeBuild` and `BuildingProject` from `obj/.../*.GeneratedMSBuildEditorConfig.editorconfig`, which every real build rewrites with a build's values, so after a build it builds until the next design-time build.

Design-time builds report a reduced set of diagnostics: target filter errors (`AG0201`-`AG0206`, `AG0208`), `AG0209`, and `AG0101`/`AG0102` for aspects declared in the project. `AG0001`, `AG0002`, `AG0003`, `AG0006`, `AG0007`, `AG0103`-`AG0108` and `AG0101`/`AG0102` for library aspects need a bound call and appear only after a real build.

There is no public `GenerateInterceptors` option.

## Assembly-level configuration

The same settings can be set in source with the generated assembly attribute:

```csharp
using AspectGenerator;

[assembly: AspectGeneratorOptions(
    GenerateApi = true,
    PublicApi = false,
    DebuggerStepThrough = false,
    AspectDiagnosticSeverity = AspectDiagnosticSeverity.Info,
    InterceptorsNamespace = "AspectGenerator")]
```

Assembly-level options override MSBuild properties for that compilation.

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

Trace implementation is compiled into Debug-built AspectGenerator packages only. Release-built packages accept `AspectGeneratorTrace=true` but do not write an `AspectGenerator trace` section.

For AGTRACE diagnostics from a local package, pack AspectGenerator in Debug configuration:

```shell
# From the root of an AspectGenerator clone; add the output folder as a local NuGet package source.
dotnet pack Source/AspectGenerator.csproj -c Debug -o local-nuget -p:PackageVersion=2.0.0-dev.trace.1
```

Then build a single consuming project and target framework:

```bat
dotnet build MyProject.csproj -f net10.0 -p:AspectGeneratorTrace=true /clp:PerformanceSummary /bl
```

## Optional intercepted-call markers

`AG0300` diagnostics mark call sites where AspectGenerator applies aspects. They are controlled by `AspectGeneratorAspectDiagnosticSeverity`:

```text
Off      disables optional markers
Hidden   reports hidden markers
Info     default; reports informational markers
Warning  reports warning markers
Error    reports error markers
```

`AG0300` is enabled by default with `Info` severity. It does not indicate a problem. Set `AspectGeneratorAspectDiagnosticSeverity` to `Off` to disable these markers. Use `AG0300` for temporary IDE/source inspection and the build report for complete aspect application analysis.

## Defining aspects

Define an aspect by creating an attribute class decorated with `[Aspect]`:

```csharp
using AspectGenerator;

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

Hook names are strings. Prefer `nameof(...)`.

Invalid hook names or signatures should be reported by `AG010x` diagnostics. If a case fails only through generated-code compiler errors, treat it as an AspectGenerator bug.

## Hook model

Current supported hook method style is static hook methods.

Common hook signatures:

```csharp
public static void OnBeforeCall(InterceptInfo info) {}
public static void OnAfterCall(InterceptInfo info) {}
public static void OnCatch(InterceptInfo info) {}
public static void OnFinally(InterceptInfo info) {}
```

Typed aspect instance parameter is supported for static lifecycle hooks:

```csharp
public static void OnBeforeCall(LogAttribute aspect, InterceptInfo info) {}
```

`UseInterceptData=true` expects hooks to receive `ref InterceptData<T>` instead of `InterceptInfo` / `InterceptInfo<T>`:

```csharp
public static void OnBeforeCall(ref InterceptData<int> data) {}
```

`OnCall` replaces the target method call. It must match the target signature and must be the last aspect in the aspect chain.

Async hooks such as `OnBeforeCallAsync`, `OnAfterCallAsync`, `OnCatchAsync`, and `OnFinallyAsync` require supported async targets: `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>`. `async void` is unsupported.

## Aspect instance lifetime

`AspectAttribute.Lifetime` controls applied aspect attribute instance construction:

```csharp
[Aspect(OnBeforeCall = nameof(OnBeforeCall), Lifetime = AspectInstanceLifetime.Static)]
```

Values:

```text
Auto      generator-selected behavior; currently follows the static fast path unless explicit Instance is requested
Static    reuse one lazily initialized aspect instance per generated interceptor/aspect application
Instance  create a new applied aspect attribute instance for every intercepted call
```

Static lifetime stores aspect instances in per-interceptor generated static state holders. These holders are initialized lazily on first use of the corresponding interceptor, so unrelated interceptor aspect instances are not constructed.

Static aspect instances are shared. Treat them as immutable configuration objects or implement synchronization for shared mutable state.

Use this guidance:

```text
Static for most aspects and performance-sensitive systems
Static + InterceptInfo.Tag for cheap per-call state
Instance only when typed per-call state stored in the aspect object is worth the allocation
```

Aspect constructors and property initializers should be cheap and side-effect free. Static lifetime runs them at most once per generated interceptor/aspect application; Instance lifetime runs them for every intercepted call.

## TargetFilter

`TargetFilter` applies an aspect to matching target methods at assembly or type level. It belongs to the applied aspect attribute, not to `[Aspect]`.

`DefaultTargetFilter` belongs to `[Aspect]` on the aspect definition. It lets aspect authors prepend default rules to assembly-level or type-level applications of that aspect. V1 uses simple concatenation:

```text
EffectiveTargetFilter = DefaultTargetFilter + TargetFilter
```

Later matching rules win. A consumer `TargetFilter` can override a default exclusion unless it repeats that exclusion later.

Aspect attributes that support filters should expose:

```csharp
public string? TargetFilter { get; set; }
```

or:

```csharp
public string[]? TargetFilter { get; set; }
```

Each string is split into non-empty lines. Lines starting with `#` are comments. `string[]` values concatenate all rules.

Rule types:

```text
unprefixed  native AspectGenerator target pattern syntax
pattern:     native AspectGenerator target pattern syntax
contains:    ordinal substring match against canonical method signature
regex:       regex match against canonical method signature
-attributes: exclude targets with a matching method or containing type attribute
-rule        exclude rule; last matching rule wins inside one filter set
```

Example:

```csharp
[assembly: Log(
    TargetFilter = """
        # Include service save methods and exclude health checks.
        public **.*Service.Save*(..., *CancellationToken)
        -contains: HealthCheck
        """)]
```

Native condition rules support method metadata conditions. Different keys in one group are AND. Repeated same keys are OR by default. Leading `&` forces AND. Leading `|` starts an alternative condition group. Inline condition expressions support `&` and `|`, with `&` higher precedence. Escape literal operators as `\&` and `\|`.

Canonical method signature format:

```text
<accessibility>[ <modifier>...] <return-type> <containing-type>.<method-name>[<type-parameters>](<parameter-types>)
```

Types are fully qualified without C# aliases. Nullable annotations and parameter names are omitted. Extension method receivers are formatted with `this`.

The `attributes:` condition checks method attributes and containing type attributes, including nested containing types. It supports short attribute names, short names without the `Attribute` suffix, and fully qualified names. `attribute:` is intentionally not supported.

Target filters select target methods. AspectGenerator still rewrites only call sites visible to the current compilation.

## Conditional aspects

Aspect attribute classes may use `System.Diagnostics.ConditionalAttribute`:

```csharp
[Conditional("DEBUG")]
[Aspect(OnAfterCall = nameof(OnAfterCall))]
sealed class LogAttribute : Attribute
{
    public static void OnAfterCall(InterceptInfo info) {}
}
```

Multiple `[Conditional]` attributes are OR. Conditional aspect attributes apply consistently to direct method aspects, type-level `TargetFilter`, and assembly-level `TargetFilter`.

Project/tree preprocessor symbols such as `DEBUG`, `TRACE`, and MSBuild-defined symbols are supported. Local source-file `#define` / `#undef` directive state is not guaranteed to be evaluated with full location sensitivity.

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

## Diagnostics quick reference

```text
AG0001  OnCall aspect must be last when applied directly
AG0002  OnCall aspect must be last when inferred from method metadata
AG0003  invocation cannot be intercepted by the compiler
AG0004  generated interceptor namespace is not listed in InterceptorsNamespaces
AG0005  generator runs without the package MSBuild props (reference the package, or PrivateAssets="none" in the library)
AG0006  call selected only by a target filter is not intercepted (off by default; dotnet_diagnostic.AG0006.severity = warning lists them on the console, suggestion in the IDE only)
AG0007  summary per reason of the calls AG0006 would list, with their number (information, no location: severity = warning in a .globalconfig to see it on the console)
AG0101  hook method not found
AG0102  hook method must be static
AG0103  invalid hook parameter type
AG0104  invalid hook return type
AG0105  OnCall signature mismatch
AG0106  UseInterceptData=true requires ref InterceptData<T>
AG0107  async hook requires supported async target
AG0108  OnAfterCall/OnCatch/OnFinally/OnUsing on an iterator run before its body (warning when applied, information when filter-selected)
AG0201  invalid TargetFilter regex
AG0202  invalid TargetFilter rule
AG0204  unknown TargetFilter condition key
AG0205  invalid TargetFilter parameter pattern
AG0206  invalid TargetFilter dotted pattern
AG0208  TargetFilter is used on a method-level aspect attribute
AG0209  invalid AspectGenerator diagnostic severity value
AG0300  optional intercepted-call marker
```

## Agent workflow

When asked to analyze a project using AspectGenerator:

```text
1. Verify the project is built with .NET SDK 9.0.100 or later and the build output has no `CS9057`.
2. Find every project containing call sites that should be intercepted.
3. Verify each such project references AspectGenerator directly.
4. Check AspectGeneratorGenerateApi / PublicApi ownership mode.
5. Check AspectGeneratorInterceptorsNamespace and InterceptorsNamespaces wiring.
6. Build normally, not only design-time.
7. Inspect AspectGeneratorBuildReport.md.
8. Use generated Interceptors.g.cs only after reading the report.
9. Treat AG0300 as optional marker diagnostics, not as the complete report.
10. Keep README, wiki, and this SKILL.md synchronized when behavior changes.
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
look for projects that use aspect attributes but forgot a direct package reference
```

## Documentation rules for agents

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

Do not remove build report details just because they are not suitable for deterministic golden-file comparison. The report is intentionally human- and agent-readable.

## Dependency guidance for agents

Do not add new NuGet packages, template engines, formatters, source-generation helper libraries, or build tools unless the maintainer explicitly asks for that dependency.

In particular, do not replace internal code generation with Scriban, Handlebars.Net, CSharpier, or other external templating/formatting tools as an automatic refactoring step.

When improving generated-code quality, prefer internal refactoring first:

```text
CodeWriter
GeneratedCodeSymbolPrinter
InterceptorNamingService
AspectLifetimeResolver
smaller emitter classes
targeted regression tests
```

If a new dependency seems useful, propose it as a separate design discussion with trade-offs, package impact, versioning risk, analyzer/generator loading risk, and build performance implications. Do not include it in unrelated cleanup or refactoring changes.
