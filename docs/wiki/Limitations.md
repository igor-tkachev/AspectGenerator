# Limitations

AspectGenerator is compile-time call-site rewriting, not runtime AOP.

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
