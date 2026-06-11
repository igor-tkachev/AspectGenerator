# Limitations

AspectGenerator is compile-time call-site rewriting, not runtime AOP.

## Supported

- static method calls;
- instance method calls;
- extension method calls;
- generic method calls;
- `Task` and `Task<T>` targets;
- `ref`, `out`, and `in` parameters.

## Unsupported

- constructors;
- properties and indexers;
- operators;
- delegates;
- reflection calls;
- local functions;
- calls already compiled in external assemblies;
- `ValueTask` and `ValueTask<T>` in the current implementation.

Unsupported cases either cannot be represented as ordinary interceptable invocation syntax or are not implemented by AspectGenerator yet.
