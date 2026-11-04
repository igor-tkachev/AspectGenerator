# Limitations

AspectGenerator is compile-time call-site rewriting, not runtime AOP.

Projects that use AspectGenerator must be built with .NET SDK 9.0.100 or later (Roslyn 4.12; Visual Studio 2022 17.12 or later). An older compiler, such as SDK 8.0.x or Visual Studio 2022 before 17.12, cannot load the generator: the build reports `CS9057` (the analyzer references a newer compiler version) and the aspects are silently not applied. This is a build-toolchain requirement, not a target-framework requirement. AspectGenerator itself targets `netstandard2.0`, and the consuming project's `TargetFramework` is a separate decision.

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

## Known Issues

These call shapes are selected but the generated interceptor does not compile yet. They are tracked for a later release:

- extension methods called in static form (`Extensions.Ext(value)`): the interceptor refers to an undefined `__this__` (`CS0103`); instance-form calls (`value.Ext()`) work;
- calls inside generic types or methods whose type arguments are the caller's own type parameters (`CS0246`);
- calls whose type arguments are anonymous types;
- `protected` static targets, which the file-local interceptor class cannot access (`CS0122`);
- `params ReadOnlySpan<T>` parameters (`CS8640`);
- `out` parameters (`CS0177`), including a record's generated `Deconstruct`, which target filters therefore never select;
- non-generic `async Task` targets whose interceptor does not return on every path (`CS0161`);
- calls with `dynamic` arguments (`CS1963`);
- members of C# 14 extension blocks (`extension(T) { ... }`).
