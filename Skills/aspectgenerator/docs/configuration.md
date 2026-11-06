[AspectGenerator skill](../SKILL.md)

# Configuration

> You are here if you change generator options for one project or a solution.

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

The options apply separately to each compilation, even when the source file or `Directory.Build.props` is shared across projects. API ownership is described in [library mode](library-mode.md). For source emission behavior see [generator hosts](generator-host.md).
