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
- ref struct parameters (`Span<T>`, `params ReadOnlySpan<T>`), pointer and function pointer parameters, and `dynamic` parameters;
- instance methods of structs, and extension methods with a `this ref`, `this in` or `this ref readonly` parameter.

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

A selected call that cannot get a compilable interceptor is not intercepted. The generator reports it at the call, with the reason, and the call runs as written: `AG0003`, a warning, when the aspect is applied to the target method itself, and `AG0006`, information, when only a target filter selected the method. Positional deconstruction (`var (a, b) = value`) has no invocation syntax and is never intercepted; an explicit `value.Deconstruct(out a, out b)` call is.

- calls inside expression trees (lambdas converted to `Expression<T>`, query clauses over `IQueryable`): the expression keeps the original method for the query provider;
- `base.M()` calls, which an interceptor could only make as a virtual call;
- local functions;
- members of C# 14 extension blocks (`extension(T) { ... }`);
- calls removed by `[Conditional]` and partial methods without an implementation;
- calls whose type arguments are the target's own type parameters (a generic method calling itself, a generic type calling its own members), which the compiler does not accept for a generic interceptor;
- `private` or `protected` targets, which the generated file-local interceptor class cannot access (an interceptor emitted into a partial declaration of the target's type could reach them; that is a possible later feature);
- methods that return by reference, a ref struct (or a type parameter that `allows ref struct`), or a pointer;
- static abstract and static virtual interface members called through a type parameter (`T.Create()`);
- interface, `object` and `ValueType` methods called on a type parameter receiver that may be a struct: the interceptor would box it, and what the call changes would be lost;
- methods with pointer parameters whose aspects have async hooks (an unsafe method cannot be async);
- `__arglist` methods and by-reference `dynamic` parameters;
- `PassArguments` with a ref struct argument (or one whose type `allows ref struct`), which cannot be boxed;
- generic instance methods with ref struct or pointer parameters;
- `OnCall` hooks on calls whose type arguments cannot be named in generated code (see below).

Calls whose type arguments cannot be named in the generated file — a type parameter of the calling method or type, an anonymous type, a private nested type — get a generic interceptor over the target's own type parameters, with the same constraints; the compiler passes the call's type arguments to it.
