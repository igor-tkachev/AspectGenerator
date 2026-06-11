# Aspect Generator

[![Build](https://img.shields.io/github/actions/workflow/status/igor-tkachev/AspectGenerator/dotnet.yml?branch=master&label=build&logo=github&style=flat-square)](https://github.com/igor-tkachev/AspectGenerator/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/v/AspectGenerator?logo=nuget&style=flat-square)](https://www.nuget.org/packages/AspectGenerator)
[![NuGet downloads](https://img.shields.io/nuget/dt/AspectGenerator?logo=nuget&style=flat-square)](https://www.nuget.org/packages/AspectGenerator)
[![License](https://img.shields.io/github/license/igor-tkachev/AspectGenerator?style=flat-square)](LICENSE.txt)

AspectGenerator is a source generator for compile-time call-site rewriting with C# interceptors. It lets you define attribute-based aspects that run around intercepted method calls without runtime proxies or IL weaving.

> [!NOTE]
> AspectGenerator itself targets `netstandard2.0`, but consuming projects must be built with the .NET 10 SDK/compiler because the generator uses the stable Roslyn interceptor API based on opaque `InterceptableLocation` data. Older SDKs and the legacy `InterceptsLocation(filePath, line, character)` preview API are not supported.

> [!IMPORTANT]
> This is not runtime AOP. Aspects are applied by rewriting call sites visible to the current compilation. Calls made through reflection, delegates, already-compiled external assemblies, or unsupported C# constructs are outside the interception model.

## Quick Start

Install the package:

```bash
dotnet add package AspectGenerator
```

Configure the consuming project:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);AspectGenerator</InterceptorsNamespaces>
</PropertyGroup>
```

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
    InterceptorsNamespace = "AspectGenerator")]
```

`AspectGeneratorInterceptorsNamespace` must also be listed in `InterceptorsNamespaces`, otherwise the compiler will not enable generated interceptors from that namespace.

## Generated API Ownership

AspectGenerator normally generates the authoring/runtime API into each consuming compilation:

- `GenerateApi=true`: generate `AspectAttribute`, `InterceptInfo`, `InterceptData<T>`, `InterceptResult`, and related types.
- `GenerateApi=false`: do not generate the API. Use this when the API is supplied by an aspect library.
- `PublicApi=false`: generated API is internal to the consuming assembly.
- `PublicApi=true`: generated API is public. Treat this mode as experimental until the public contract is stabilized.

Common modes:

| Mode | Configuration | Intended use |
| --- | --- | --- |
| Single project | `GenerateApi=true`, `PublicApi=false` | App defines and uses its own aspects. |
| Aspect library | Library: `GenerateApi=true`, `PublicApi=true`; consumer: `GenerateApi=false` | Shared aspect attributes live in a reusable assembly. |
| Cross-project app | Same interceptor namespace in each project via `AspectGeneratorInterceptorsNamespace` and `InterceptorsNamespaces` | Intercept calls compiled in multiple projects. |

## Supported Scenarios

| Scenario | Status | Notes |
| --- | --- | --- |
| Static methods | Supported | Ordinary member method calls only. |
| Instance methods | Supported | Call site must be visible to the current compilation. |
| Extension methods | Supported | Covered by unit tests. |
| Generic methods | Supported | Explicit `InterceptMethods` strings must match generated display names. |
| `Task` and `Task<T>` async methods | Supported | Async hooks are selected for `Task` targets. |
| `ref`, `out`, `in` parameters | Supported | Covered by unit tests. |
| Constructors | Unsupported | Platform limitation of C# interceptors. |
| Properties and indexers | Unsupported | Not ordinary method invocation syntax. |
| Operators | Unsupported | Not currently matched by the generator. |
| Delegates and reflection | Unsupported | No direct call site rewrite. |
| Local functions | Unsupported | Platform limitation. |
| Calls compiled in external assemblies | Unsupported | Rebuild the assembly containing the call site with AspectGenerator enabled. |
| `ValueTask` and `ValueTask<T>` | Not currently supported | Planned or explicitly documented as a limitation. |

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

## Explicit Method Interception

`InterceptMethods` is a low-level compatibility feature that matches methods by display string:

```csharp
[Aspect(
    OnAfterCall = nameof(OnAfterCall),
    InterceptMethods =
    [
        "System.String.Substring(int)",
        "string.Substring(int)"
    ])]
```

This form is brittle because it depends on compiler display strings, aliases, overloads, and generic spelling. Prefer method-level aspect attributes where possible.

## Documentation And Wiki

The README is the concise entry point. The wiki should contain expanded pages with the same current terminology:

- [Configuration](https://raw.githubusercontent.com/wiki/igor-tkachev/AspectGenerator/Configuration.md)
- [Aspect library mode](https://raw.githubusercontent.com/wiki/igor-tkachev/AspectGenerator/Aspect-library-mode.md)
- [Hook lifecycle](https://raw.githubusercontent.com/wiki/igor-tkachev/AspectGenerator/Hook-lifecycle.md)
- [`InterceptMethods`](https://raw.githubusercontent.com/wiki/igor-tkachev/AspectGenerator/InterceptMethods.md)
- [Diagnostics](https://raw.githubusercontent.com/wiki/igor-tkachev/AspectGenerator/Diagnostics.md)
- [Limitations](https://raw.githubusercontent.com/wiki/igor-tkachev/AspectGenerator/Limitations.md)

When updating docs, keep README and wiki synchronized:

- document the .NET 10 SDK/compiler requirement and use `net10.0` in examples;
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
