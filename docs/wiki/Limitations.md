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
- `async void` methods.

Unsupported cases either cannot be represented as ordinary interceptable invocation syntax or are not implemented by AspectGenerator yet.
