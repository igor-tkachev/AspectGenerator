# Changelog

## Unreleased

- Documented the minimum build toolchain: .NET SDK 10.0.200 or later (Roslyn 5.3; Visual Studio 2026 18.3 or later). An older compiler reports `CS9057` and does not load the generator, so aspects are not applied. The documentation no longer suggests pinning SDK 10.0.100.

## 1.0.1

- Added compile-time method aspects using C# interceptors.
- Added .NET 10 SDK/compiler-based stable interceptor support using opaque `InterceptableLocation` data.
- Added target filters for assembly-level and type-level aspect application.
- Added conditional aspect support through `ConditionalAttribute`.
- Added build report generation.
- Added aspect lifetime configuration.
- Added optional intercepted-call marker diagnostics (`AG0300`).
- Added agent-oriented `SKILL.md`.
