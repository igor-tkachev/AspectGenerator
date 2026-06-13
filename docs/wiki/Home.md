# AspectGenerator Wiki

> [!IMPORTANT]
> These pages document the .NET 10 / stable interceptor version of AspectGenerator. Preview-era instructions for `net8.0`, `InterceptorsPreviewNamespaces`, `AG_*` defines, or path-based `InterceptsLocation(filePath, line, character)` are obsolete.

AspectGenerator is a source generator for compile-time call-site rewriting with C# interceptors. It is not runtime AOP: only call sites compiled with the generator can be rewritten.

## Pages

- [Configuration](Configuration)
- [Aspect library mode](Aspect-library-mode)
- [Hook lifecycle](Hook-lifecycle)
- [Target filters](Target-filters)
- [InterceptMethods](InterceptMethods)
- [Diagnostics](Diagnostics)
- [Limitations](Limitations)
- [Roadmap](Roadmap)

## Minimum Setup

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);AspectGenerator</InterceptorsNamespaces>
</PropertyGroup>
```

The generated interceptor namespace must be listed in `InterceptorsNamespaces`.
