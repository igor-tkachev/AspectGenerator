# Configuration

AspectGenerator itself targets `netstandard2.0`, but consuming projects must be built with the .NET 10 SDK/compiler because the generator uses the stable Roslyn interceptor API based on opaque `InterceptableLocation` data.

## Configuration Model

AspectGenerator configuration is evaluated per project, because the generator and analyzer run in the context of a single compilation. Each project that references AspectGenerator has its own configuration, its own generated configuration API, and its own generator execution.

This does not mean configuration must be duplicated manually. Projects can share the same configuration source, but that source is applied separately to each project.

## Configuration API

AspectGenerator always generates the configuration API into each project that references the package:

```csharp
namespace AspectGenerator
{
    enum AspectDiagnosticSeverity
    {
        Off = -1,
        Hidden = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
    }

    [AttributeUsage(AttributeTargets.Assembly, Inherited = false, AllowMultiple = false)]
    sealed class AspectGeneratorOptionsAttribute : Attribute
    {
        public bool GenerateApi { get; set; } = true;
        public bool PublicApi { get; set; }
        public bool DebuggerStepThrough { get; set; }
        public AspectDiagnosticSeverity AspectDiagnosticSeverity { get; set; } =
            AspectDiagnosticSeverity.Info;
        public string? InterceptorsNamespace { get; set; }
    }
}
```

This API is local to the current project. It is used only to configure AspectGenerator for the current compilation. It is not part of the public aspect-library contract and is not meant to be consumed from another assembly.

## MSBuild Configuration

The recommended way to configure multiple projects consistently is to use MSBuild properties, usually through `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <AspectGeneratorGenerateApi>true</AspectGeneratorGenerateApi>
    <AspectGeneratorPublicApi>false</AspectGeneratorPublicApi>
    <AspectGeneratorDebuggerStepThrough>false</AspectGeneratorDebuggerStepThrough>
    <AspectGeneratorReportFile>$(BaseIntermediateOutputPath)\GeneratedFiles\AspectGenerator\AspectGeneratorBuildReport.md</AspectGeneratorReportFile>
    <AspectGeneratorAspectDiagnosticSeverity>Info</AspectGeneratorAspectDiagnosticSeverity>
    <AspectGeneratorInterceptorsNamespace>AspectGenerator</AspectGeneratorInterceptorsNamespace>
  </PropertyGroup>
</Project>
```

These properties are evaluated separately for each project. A project can still override any value in its own `.csproj`.

## Assembly Attribute Configuration

A project can also be configured with an assembly-level attribute:

```csharp
using AspectGenerator;

[assembly: AspectGeneratorOptions(
    GenerateApi = true,
    PublicApi = false,
    DebuggerStepThrough = false,
    AspectDiagnosticSeverity = AspectDiagnosticSeverity.Info,
    InterceptorsNamespace = "AspectGenerator")]
```

If multiple projects should use the same assembly-level configuration, include the same source file into those projects:

```xml
<ItemGroup>
  <Compile Include="..\Build\AspectGeneratorOptions.cs"
           Link="AspectGeneratorOptions.cs" />
</ItemGroup>
```

The file is shared, but it is compiled separately in each project and binds to that project's local generated `AspectGeneratorOptionsAttribute`.

Do not rely on an `AspectGeneratorOptionsAttribute` type from another assembly to configure the current project. Configuration belongs to the current compilation.

## Configuration Precedence

Assembly-level options override MSBuild properties for the current project.

Recommended model:

```text
Directory.Build.props
  repository-wide defaults

.csproj
  project-specific overrides

[assembly: AspectGeneratorOptions(...)]
  source-level override for the current compilation
```

## Runtime API Ownership

The configuration API is separate from the runtime/authoring API used by aspects:

```text
AspectAttribute
InterceptInfo
InterceptInfo<T>
InterceptData<T>
InterceptType
InterceptResult
Void
```

Those types are controlled by `AspectGeneratorGenerateApi` and `AspectGeneratorPublicApi`.

Common modes:

| Mode | Configuration | Meaning |
| --- | --- | --- |
| Single-project usage | `GenerateApi=true`, `PublicApi=false` | The project defines and uses its own aspects. Generated runtime API is internal. |
| Aspect library | `GenerateApi=true`, `PublicApi=true` | The project exposes aspect attributes and runtime API to other projects. |
| Consumer of aspect library | `GenerateApi=false` | The project uses public runtime API from a referenced aspect library. |

Any project that exposes aspect attributes to other projects must set:

```xml
<PropertyGroup>
  <AspectGeneratorGenerateApi>true</AspectGeneratorGenerateApi>
  <AspectGeneratorPublicApi>true</AspectGeneratorPublicApi>
</PropertyGroup>
```

## Optional Diagnostic Severity

`AspectGeneratorAspectDiagnosticSeverity` controls optional diagnostics such as `AG0300` intercepted-call markers. Supported values are `Off`, `Hidden`, `Info`, `Warning`, and `Error`; the default is `Info`.

`Off` disables optional diagnostics. `Info` is intended for normal IDE inspection. `Warning` can be used for audit-style builds. `Error` can be used when intercepted calls must be explicitly inspected before changes are accepted.

## Build And Interceptor Settings

AspectGenerator analyzes code during design-time builds so IDE diagnostics and optional call-site markers can work. Interceptor source is emitted only during normal builds. This behavior is automatic and not user-configurable.

`AspectGeneratorReportFile` controls the path of the human-readable build report. The report is written during normal builds and skipped during design-time builds.

The report can be used by developers and AI coding agents to inspect selected target methods, intercepted call sites, applied aspects, generated interceptor methods, generated source references, source locations, and aspect lifetime information. It is not printed to the console by default. Diagnostics are reserved for errors, warnings, optional call-site markers, and actionable misconfiguration.

`AspectGeneratorInterceptorsNamespace` controls the namespace used for generated interceptors. The AspectGenerator NuGet package appends this namespace to `InterceptorsNamespaces` automatically for projects that reference the package directly. Manual `InterceptorsNamespaces` configuration should only be needed for unusual or custom build setups.

The package `.props` asset defines defaults early. The package `.targets` asset appends `InterceptorsNamespaces` late, after project-level overrides such as `AspectGeneratorInterceptorsNamespace` are evaluated.

Every project whose call sites should be intercepted must reference AspectGenerator directly. A shared aspect library may reference AspectGenerator to define and build aspect attributes, but applications that use those aspect attributes must also reference AspectGenerator directly if their call sites should be intercepted.

## Obsolete Preview Names

Do not use these preview-era settings for current versions:

- `InterceptorsPreviewNamespaces`
- `AG_InterceptorsNamespace`
- `AG_PUBLIC_API`
- `AG_NOT_GENERATE_API`
- `InterceptsLocation(filePath, line, character)`
