# Configuration

AspectGenerator itself targets `netstandard2.0`, but consuming projects must be built with the .NET 10 SDK/compiler because the generator uses the stable Roslyn interceptor API based on opaque `InterceptableLocation` data.

## MSBuild Properties

```xml
<PropertyGroup>
  <AspectGeneratorGenerateApi>true</AspectGeneratorGenerateApi>
  <AspectGeneratorPublicApi>false</AspectGeneratorPublicApi>
  <AspectGeneratorDebuggerStepThrough>false</AspectGeneratorDebuggerStepThrough>
  <AspectGeneratorReportFile>$(BaseIntermediateOutputPath)\GeneratedFiles\AspectGenerator\AspectGeneratorBuildReport.md</AspectGeneratorReportFile>
  <AspectGeneratorMarkInterceptedCalls>false</AspectGeneratorMarkInterceptedCalls>
  <AspectGeneratorInterceptorsNamespace>AspectGenerator</AspectGeneratorInterceptorsNamespace>
</PropertyGroup>
```

Advanced override:

```xml
<PropertyGroup>
  <!-- Force interceptor source emission even when the default build-mode logic would skip it. -->
  <AspectGeneratorGenerateInterceptors>true</AspectGeneratorGenerateInterceptors>
</PropertyGroup>
```

## Assembly Attribute Override

```csharp
using AspectGenerator;

[assembly: AspectGeneratorOptions(
    GenerateApi = true,
    PublicApi = false,
    DebuggerStepThrough = false,
    MarkInterceptedCalls = false,
    InterceptorsNamespace = "AspectGenerator")]
```

Assembly attribute values override MSBuild properties.

`AspectGeneratorGenerateInterceptors` controls `Interceptors.g.cs` emission. It defaults to `false` for design-time builds and `true` otherwise. Diagnostics still run when interceptor source emission is disabled.

AspectGenerator writes an informational build report file during normal builds. `AspectGeneratorReportFile` controls the output path. The report is not printed to the console by default. Diagnostics are reserved for errors, warnings, and actionable misconfiguration.

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
