# Configuration

AspectGenerator itself targets `netstandard2.0`, but consuming projects must be built with the .NET 10 SDK/compiler because the generator uses the stable Roslyn interceptor API based on opaque `InterceptableLocation` data.

## MSBuild Properties

```xml
<PropertyGroup>
  <AspectGeneratorGenerateApi>true</AspectGeneratorGenerateApi>
  <AspectGeneratorGenerateInterceptors>true</AspectGeneratorGenerateInterceptors>
  <AspectGeneratorPublicApi>false</AspectGeneratorPublicApi>
  <AspectGeneratorDebuggerStepThrough>false</AspectGeneratorDebuggerStepThrough>
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
    InterceptorsNamespace = "AspectGenerator")]
```

Assembly attribute values override MSBuild properties.

`AspectGeneratorGenerateInterceptors` controls `Interceptors.g.cs` emission. It defaults to `false` for design-time builds and `true` otherwise. Diagnostics still run when interceptor source emission is disabled.

## Obsolete Preview Names

Do not use these preview-era settings for current versions:

- `InterceptorsPreviewNamespaces`
- `AG_InterceptorsNamespace`
- `AG_PUBLIC_API`
- `AG_NOT_GENERATE_API`
- `InterceptsLocation(filePath, line, character)`
