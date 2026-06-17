# Configuration

AspectGenerator itself targets `netstandard2.0`, but consuming projects must be built with the .NET 10 SDK/compiler because the generator uses the stable Roslyn interceptor API based on opaque `InterceptableLocation` data.

## MSBuild Properties

```xml
<PropertyGroup>
  <AspectGeneratorGenerateApi>true</AspectGeneratorGenerateApi>
  <AspectGeneratorGenerateInterceptors>true</AspectGeneratorGenerateInterceptors>
  <AspectGeneratorPublicApi>false</AspectGeneratorPublicApi>
  <AspectGeneratorDebuggerStepThrough>false</AspectGeneratorDebuggerStepThrough>
  <AspectGeneratorVerbosity>Quiet</AspectGeneratorVerbosity>
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
    SummaryVerbosity = AspectReportVerbosity.Quiet,
    InterceptorsVerbosity = AspectReportVerbosity.Minimal,
    TargetsVerbosity = AspectReportVerbosity.Normal,
    FiltersVerbosity = AspectReportVerbosity.Diagnostic,
    InterceptorsNamespace = "AspectGenerator")]
```

Assembly attribute values override MSBuild properties.

`AspectGeneratorGenerateInterceptors` controls `Interceptors.g.cs` emission. It defaults to `false` for design-time builds and `true` otherwise. Diagnostics still run when interceptor source emission is disabled.

Compile-time reporting diagnostics are controlled by the MSBuild-only `AspectGeneratorVerbosity` current reporting level and assembly-level per-category verbosity thresholds. Supported values are `Off`, `Quiet`, `Minimal`, `Normal`, `Detailed`, and `Diagnostic`. Defaults are `AspectGeneratorVerbosity=Quiet`, `SummaryVerbosity=Quiet`, `InterceptorsVerbosity=Minimal`, `TargetsVerbosity=Normal`, and `FiltersVerbosity=Diagnostic`.

`AspectGeneratorInterceptorsNamespace` controls the namespace used for generated interceptors. The AspectGenerator NuGet package appends this namespace to `InterceptorsNamespaces` automatically for projects that reference the package directly. Manual `InterceptorsNamespaces` configuration should only be needed for unusual or custom build setups.

Every project whose call sites should be intercepted must reference AspectGenerator directly. A shared aspect library may reference AspectGenerator to define and build aspect attributes, but applications that use those aspect attributes must also reference AspectGenerator directly if their call sites should be intercepted.

## Obsolete Preview Names

Do not use these preview-era settings for current versions:

- `InterceptorsPreviewNamespaces`
- `AG_InterceptorsNamespace`
- `AG_PUBLIC_API`
- `AG_NOT_GENERATE_API`
- `InterceptsLocation(filePath, line, character)`
