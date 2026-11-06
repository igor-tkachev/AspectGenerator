---
name: aspectgenerator
description: Configure and use AspectGenerator compile-time C# method aspects including hooks and lifetimes, target filters, shared aspect libraries, diagnostics and build troubleshooting when a project uses the AspectGenerator NuGet package.
---

# AspectGenerator

AspectGenerator provides AOP-like compile-time method aspects for C# by rewriting visible call sites with C# interceptors. It is not runtime AOP, does not use runtime proxies, and does not weave IL.

Build consuming projects with .NET SDK 9.0.100 or later (Roslyn 4.12; Visual Studio 2022 17.12 or later). An older compiler reports `CS9057` and does not load the generator, so aspects are not applied. This is a build-toolchain requirement; a `net8.0` project built with that SDK works.

Use `<PackageReference Include="AspectGenerator" Version="1.1.1" PrivateAssets="all" />` for a private build-time dependency. Each project compiling call sites to intercept needs the generator, normally through a direct reference. A library can explicitly pass it and its MSBuild wiring to consumers with `PrivateAssets="none"`. The package has no runtime assembly.

The skill is available at `<global-packages>/aspectgenerator/<version>/skills/aspectgenerator/` or as a copy in `.agents/skills/aspectgenerator/` or `.claude/skills/aspectgenerator/`. Its guides use relative links and work in either location.

## Guide index

- [Writing an aspect](docs/writing-an-aspect.md) — static hooks, lifecycle, conditional aspects and instance lifetimes.
- [Target filters](docs/target-filters.md) — assembly/type applications, ordered rules and canonical signatures.
- [Diagnostics and fixes](docs/diagnostics.md) — AGxxxx diagnostics and optional AG0300 markers.
- [Library mode and PrivateAssets](docs/library-mode.md) — package references and generated API ownership across projects.
- [Configuration](docs/configuration.md) — MSBuild properties and assembly-level overrides.
- [Generator hosts and Hot Reload](docs/generator-host.md) — build versus design-time behavior and editor diagnostics.
- [Troubleshooting](docs/troubleshooting.md) — missing interception, supported targets, build reports, Debug trace and review checks.
