# AspectGenerator Wiki

> [!IMPORTANT]
> These pages document the .NET 10 SDK/compiler and stable interceptor version of AspectGenerator. Preview-era instructions for `net8.0`, `InterceptorsPreviewNamespaces`, `AG_*` defines, or path-based `InterceptsLocation(filePath, line, character)` are obsolete.

AspectGenerator is a source generator for compile-time call-site rewriting with C# interceptors. It is not runtime AOP: only call sites compiled with the generator can be rewritten.

## Pages

- [Configuration](Configuration)
- [Aspect library mode](Aspect-library-mode)
- [Hook lifecycle](Hook-lifecycle)
- [Target filters](Target-filters)
- [Diagnostics](Diagnostics)
- [Limitations](Limitations)
- [Roadmap](Roadmap)

## Minimum Setup

Projects that use AspectGenerator must be built with the .NET 10 SDK/compiler. This is a build-toolchain requirement, not a target-framework requirement. AspectGenerator itself targets `netstandard2.0`, and the consuming project's `TargetFramework` is a separate decision.

For example, pin the repository SDK with `global.json`:

```json
{
  "sdk": {
    "version": "10.0.100"
  }
}
```

The AspectGenerator NuGet package imports the required MSBuild wiring automatically for projects that reference the package directly. Every project whose call sites should be intercepted must reference AspectGenerator directly.
