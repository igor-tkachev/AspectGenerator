# Changelog

## Unreleased

- Documented the minimum build toolchain: .NET SDK 10.0.200 or later (Roslyn 5.3; Visual Studio 2026 18.3 or later). An older compiler reports `CS9057` and does not load the generator, so aspects are not applied. The documentation no longer suggests pinning SDK 10.0.100.
- Selected target methods are found from their declarations before call sites are bound, and call sites are rejected by name, generic arity and argument count before binding. Semantic models are reused per syntax tree. Together this makes the generator several times faster on large projects.
- Fixed calls that were not intercepted under declaration-first selection: calls through a PascalCase property or field, static calls through a derived type or a `using` alias, and assembly-level native `TargetFilter` patterns that select methods of referenced assemblies (for example `public static System.Math.Max(System.Int32, System.Int32)`).
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
