# Changelog

## 1.1.0 — 2026-11-04

- Changed: the package is now a development dependency (`<DevelopmentDependency>true</DevelopmentDependency>`). `dotnet add package AspectGenerator` adds it with `PrivateAssets="all"`, and it no longer appears as a dependency of a package that references it. Before, a normal `PackageReference` flowed transitively to the consumers of a library; there the analyzer ran without the package's `build/*.props` (`InterceptorsNamespaces` and the generator options), which silently dropped aspects or broke interceptors. A library author who relied on the package flowing to consumers either writes `PrivateAssets="none"` on the reference (the generator then flows, with its props) or references AspectGenerator directly in every project where it is used. Existing references without `PrivateAssets="all"` should add it, or write `PrivateAssets="none"` to keep the flow.
- The props and targets are shipped in `buildTransitive/` as well as `build/` (which forwards to it), so a generator that flows transitively brings `InterceptorsNamespaces` and the options with it, and a direct reference does not apply them twice.
- `AspectGeneratorGenerateApi` no longer defaults to `true` in the props: when it is unset and a referenced assembly already declares a public `AspectAttribute`, the project uses that API instead of generating a second copy. Set it explicitly to restore the previous behavior.
- New warning `AG0005`: the generator intercepts calls in a project whose build props were not imported (a transitive analyzer without props, or a manual analyzer reference). It replaces the confusing `AG0004` and interceptor compile errors with one message that names the fix.
- Documentation: new wiki page "Using AspectGenerator in libraries" with a table of the library and application scenarios.
- The package contains only the analyzer (`analyzers/dotnet/cs`) and the build props and targets; it has no `lib` assembly.

- Documented the minimum build toolchain: .NET SDK 9.0.100 or later (Roslyn 4.12; Visual Studio 2022 17.12 or later). The generator is built against Roslyn 4.12, so any newer compiler loads it; an older one (SDK 8.0.x, Visual Studio before 17.12) reports `CS9057` and does not load it, so aspects are not applied. Consumers that target `net8.0` work when built with such an SDK.
- Selected target methods are found from their declarations before call sites are bound, and call sites are rejected by name, generic arity and argument count before binding. Semantic models are reused per syntax tree. Together this makes the generator several times faster on large projects.
- Fixed calls that were not intercepted under declaration-first selection: instance-form extension method calls (`"a".Ext()`, `items.Count()` with inferred type arguments, `text?.Ext()`), calls through a PascalCase property or field, static calls through a derived type or a `using` alias, and assembly-level native `TargetFilter` patterns that select methods of referenced assemblies (for example `public static System.Math.Max(System.Int32, System.Int32)`), and compiler-generated record methods (`ToString`, `Equals`, `GetHashCode`) selected by assembly-level or type-level filters.
- `AG0300` markers now use the generator's selection plan, so they are reported exactly for the intercepted calls, and the marker analyzer no longer binds every invocation.
- Generated interceptors use fully qualified (`global::`) type names. Aspect attributes declared in the global namespace or nested in a class no longer produce code that does not compile.
- Calls with explicit type arguments (`M<T>()`) and conditional calls (`a?.M()`) are intercepted; the generated code no longer contains `#error` for them.
- Documentation: removed machine-specific paths, added the trace properties to the repository build props, and marked the file-local interceptor type as implemented in the roadmap.

## 1.0.1

- Added compile-time method aspects using C# interceptors.
- Added .NET 10 SDK/compiler-based stable interceptor support using opaque `InterceptableLocation` data.
- Added target filters for assembly-level and type-level aspect application.
- Added conditional aspect support through `ConditionalAttribute`.
- Added build report generation.
- Added aspect lifetime configuration.
- Added optional intercepted-call marker diagnostics (`AG0300`).
- Added agent-oriented `SKILL.md`.
