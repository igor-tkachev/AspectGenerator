# Limitations

AspectGenerator is compile-time call-site rewriting, not runtime AOP.

Projects that use AspectGenerator must be built with .NET SDK 10.0.200 or later (Roslyn 5.3; Visual Studio 2026 18.3 or later). An older compiler, such as SDK 10.0.1xx or an earlier Visual Studio 2026 update, cannot load the generator: the build reports `CS9057` (the analyzer references a newer compiler version) and the aspects are silently not applied. This is a build-toolchain requirement, not a target-framework requirement. AspectGenerator itself targets `netstandard2.0`, and the consuming project's `TargetFramework` is a separate decision.

## Supported

- static method calls;
- instance method calls;
- extension method calls;
- generic method calls;
- target filters;
- `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>` targets;
- `ref`, `out`, and `in` parameters.

## Unsupported

- constructors;
- properties and indexers;
- operators;
- delegates;
- reflection calls;
- local functions;
- calls already compiled in external assemblies;
- projects that only reference an aspect library transitively without a direct AspectGenerator package reference;
- `async void` methods.

Unsupported cases either cannot be represented as ordinary interceptable invocation syntax or are not implemented by AspectGenerator yet.

The source generator runs in the compilation of the project that references the generator package. A transitive reference to an aspect library is not enough to run AspectGenerator in the consuming project; projects with intercepted call sites must reference AspectGenerator directly.
