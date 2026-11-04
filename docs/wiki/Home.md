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

Projects that use AspectGenerator must be built with .NET SDK 10.0.200 or later (Roslyn 5.3; Visual Studio 2026 18.3 or later). An older compiler, such as SDK 10.0.1xx or an earlier Visual Studio 2026 update, cannot load the generator: the build reports `CS9057` (the analyzer references a newer compiler version) and the aspects are silently not applied. This is a build-toolchain requirement, not a target-framework requirement. AspectGenerator itself targets `netstandard2.0`, and the consuming project's `TargetFramework` is a separate decision.

If the repository uses a `global.json`, require at least 10.0.200 and let newer feature bands roll forward:

```json
{
  "sdk": {
    "version": "10.0.200",
    "rollForward": "latestFeature"
  }
}
```

The AspectGenerator NuGet package imports the required MSBuild wiring automatically for projects that reference the package directly. Every project whose call sites should be intercepted must reference AspectGenerator directly.

The package is a development dependency: `dotnet add package AspectGenerator` adds it with `PrivateAssets="all"`, and the package does not become a dependency of your own package. If you edit the project file by hand, write `<PackageReference Include="AspectGenerator" Version="1.1.0" PrivateAssets="all" />`.

AspectGenerator is a build-time generator, not a runtime reference: the package contains only the analyzer and the MSBuild props and targets that configure it, with no assembly your code references. The analyzer needs that MSBuild wiring (`InterceptorsNamespaces` and the generator options) to produce interceptors that compile, so it must run only in the project that references the package directly. Keeping the reference private stops it from flowing to the consumers of a library, where the analyzer would run without the wiring and silently drop aspects or break the interceptors.
