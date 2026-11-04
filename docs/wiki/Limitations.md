# Limitations

AspectGenerator is compile-time call-site rewriting, not runtime AOP.

Projects that use AspectGenerator must be built with .NET SDK 9.0.100 or later (Roslyn 4.12; Visual Studio 2022 17.12 or later). An older compiler, such as SDK 8.0.x or Visual Studio 2022 before 17.12, cannot load the generator: the build reports `CS9057` (the analyzer references a newer compiler version) and the aspects are silently not applied. This is a build-toolchain requirement, not a target-framework requirement. AspectGenerator itself targets `netstandard2.0`, and the consuming project's `TargetFramework` is a separate decision.

## Supported

- static method calls;
- instance method calls;
- extension method calls;
- generic method calls, including calls inside generic methods and types whose type arguments are the caller's type parameters;
- target filters;
- `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>` targets;
- `ref`, `out`, and `in` parameters;
- ref struct parameters (`Span<T>`, `params ReadOnlySpan<T>`) and `dynamic` parameters;
- instance methods of structs.

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

The source generator runs in the compilation of the project that references the generator package. A transitive reference to an aspect library is not enough to run AspectGenerator in the consuming project, unless the library passes the generator on with `PrivateAssets="none"`; otherwise projects with intercepted call sites must reference AspectGenerator directly.

## Calls That Are Reported and Left As They Are

A selected call that cannot get a compilable interceptor is not intercepted. The generator reports `AG0003` at the call, with the reason, and the call runs as written. The report is a warning when the aspect is applied to the target method itself, and information when only a target filter selected the method:

- calls inside expression trees (lambdas converted to `Expression<T>`, query clauses over `IQueryable`): the expression keeps the original method for the query provider;
- `base.M()` calls, which an interceptor could only make as a virtual call;
- local functions;
- members of C# 14 extension blocks (`extension(T) { ... }`);
- calls removed by `[Conditional]` and partial methods without an implementation;
- calls whose type arguments are the target's own type parameters (a generic method calling itself, a generic type calling its own members), which the compiler does not accept for a generic interceptor;
- `private` or `protected` targets, which the generated file-local interceptor class cannot access;
- methods that return by reference or return a ref struct;
- `__arglist` methods and by-reference `dynamic` parameters;
- `PassArguments` with a ref struct argument, which cannot be boxed;
- generic instance methods with ref struct or pointer parameters;
- `OnCall` hooks on calls whose type arguments cannot be named in generated code (see below).

Calls whose type arguments cannot be named in the generated file — a type parameter of the calling method or type, an anonymous type, a private nested type — get a generic interceptor over the target's own type parameters, with the same constraints; the compiler passes the call's type arguments to it.
