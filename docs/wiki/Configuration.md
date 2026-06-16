# Configuration

AspectGenerator itself targets `netstandard2.0`, but consuming projects must be built with the .NET 10 SDK/compiler because the generator uses the stable Roslyn interceptor API based on opaque `InterceptableLocation` data.

## MSBuild Properties

```xml
<PropertyGroup>
  <AspectGeneratorGenerateApi>true</AspectGeneratorGenerateApi>
  <AspectGeneratorGenerateInterceptors>true</AspectGeneratorGenerateInterceptors>
  <AspectGeneratorPublicApi>false</AspectGeneratorPublicApi>
  <AspectGeneratorDebuggerStepThrough>false</AspectGeneratorDebuggerStepThrough>
  <AspectGeneratorSummarySeverity>Info</AspectGeneratorSummarySeverity>
  <AspectGeneratorInterceptorsSeverity>Hidden</AspectGeneratorInterceptorsSeverity>
  <AspectGeneratorTargetsSeverity>Hidden</AspectGeneratorTargetsSeverity>
  <AspectGeneratorFiltersSeverity>Off</AspectGeneratorFiltersSeverity>
  <AspectGeneratorInterceptorsNamespace>AspectGenerator</AspectGeneratorInterceptorsNamespace>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);AspectGenerator</InterceptorsNamespaces>
</PropertyGroup>
```

## Assembly Attribute Override

```csharp
using AspectGenerator;

[assembly: AspectGeneratorOptions(
    GenerateApi = true,
    PublicApi = false,
    DebuggerStepThrough = false,
    SummarySeverity = AspectReportSeverity.Info,
    InterceptorsSeverity = AspectReportSeverity.Hidden,
    TargetsSeverity = AspectReportSeverity.Hidden,
    FiltersSeverity = AspectReportSeverity.Off,
    InterceptorsNamespace = "AspectGenerator")]
```

Assembly attribute values override MSBuild properties.

`AspectGeneratorGenerateInterceptors` controls `Interceptors.g.cs` emission. It defaults to `false` for design-time builds and `true` otherwise. Diagnostics still run when interceptor source emission is disabled.

Compile-time reporting diagnostics are controlled per category. Supported severities are `Off`, `Hidden`, `Info`, and `Warning`. The defaults are `SummarySeverity=Info`, `InterceptorsSeverity=Hidden`, `TargetsSeverity=Hidden`, and `FiltersSeverity=Off`.

## Obsolete Preview Names

Do not use these preview-era settings for current versions:

- `InterceptorsPreviewNamespaces`
- `AG_InterceptorsNamespace`
- `AG_PUBLIC_API`
- `AG_NOT_GENERATE_API`
- `InterceptsLocation(filePath, line, character)`
