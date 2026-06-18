# Configuration

AspectGenerator itself targets `netstandard2.0`, but consuming projects must be built with the .NET 10 SDK/compiler because the generator uses the stable Roslyn interceptor API based on opaque `InterceptableLocation` data.

## MSBuild Properties

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

## Assembly Attribute Override

```csharp
using AspectGenerator;

[assembly: AspectGeneratorOptions(
    GenerateApi = true,
    PublicApi = false,
    DebuggerStepThrough = false,
    AspectDiagnosticSeverity = AspectDiagnosticSeverity.Info,
    InterceptorsNamespace = "AspectGenerator")]
```

Assembly attribute values override MSBuild properties.

`AspectGeneratorOptionsAttribute` and `AspectDiagnosticSeverity` are generated automatically for the current project and are used only to configure AspectGenerator. They are always internal and do not participate in aspect-library runtime API ownership.

`AspectGeneratorAspectDiagnosticSeverity` controls optional diagnostics such as `AG0300` intercepted-call markers. Supported values are `Off`, `Hidden`, `Info`, `Warning`, and `Error`; the default is `Info`.

AspectGenerator analyzes code during design-time builds so IDE diagnostics and optional call-site markers can work. Interceptor source is emitted only during normal builds. This behavior is automatic and not user-configurable.

AspectGenerator writes an informational build report file during normal builds. `AspectGeneratorReportFile` controls the output path. The report is not printed to the console by default. Diagnostics are reserved for errors, warnings, and actionable misconfiguration.

`AspectGeneratorInterceptorsNamespace` controls the namespace used for generated interceptors. The AspectGenerator NuGet package appends this namespace to `InterceptorsNamespaces` automatically for projects that reference the package directly. Manual `InterceptorsNamespaces` configuration should only be needed for unusual or custom build setups.

The package `.props` asset defines defaults early. The package `.targets` asset appends `InterceptorsNamespaces` late, after project-level overrides such as `AspectGeneratorInterceptorsNamespace` are evaluated.

Every project whose call sites should be intercepted must reference AspectGenerator directly. A shared aspect library may reference AspectGenerator to define and build aspect attributes, but applications that use those aspect attributes must also reference AspectGenerator directly if their call sites should be intercepted.

Runtime API ownership is separate from configuration API ownership. `AspectAttribute`, `InterceptInfo`, `InterceptInfo<T>`, `InterceptData<T>`, `InterceptResult`, and related runtime types are generated according to `AspectGeneratorGenerateApi` and `AspectGeneratorPublicApi`. Any project that exposes aspect attributes to other projects must set `AspectGeneratorPublicApi=true`.

## Obsolete Preview Names

Do not use these preview-era settings for current versions:

- `InterceptorsPreviewNamespaces`
- `AG_InterceptorsNamespace`
- `AG_PUBLIC_API`
- `AG_NOT_GENERATE_API`
- `InterceptsLocation(filePath, line, character)`
