[AspectGenerator skill](../SKILL.md)

# Library mode and PrivateAssets

> You are here if you configure package references or share aspects between projects.

AspectGenerator runs in the compilation that references the NuGet package.

Every project whose call sites should be intercepted needs `AspectGenerator`, normally through a direct reference or through a library that explicitly uses `PrivateAssets="none"`. A shared aspect library may define aspect attributes and hooks, but applications or libraries containing the actual call sites also need the generator in their compilation.

Use this rule when reviewing multi-project solutions:

```text
aspect definitions live in one project
call sites live in another project
=> both projects usually need direct AspectGenerator package references
```

The package imports the required MSBuild wiring automatically for direct package consumers.

AspectGenerator is a development dependency (`developmentDependency` in the nuspec): `dotnet add package AspectGenerator` adds it with `PrivateAssets="all"`. When editing a project file by hand, write `<PackageReference Include="AspectGenerator" PrivateAssets="all" />` and set `Version` to the version of the AspectGenerator package this skill ships in. The private reference does not flow: a project that consumes a library does not get the generator, so every project with call sites to intercept must reference it directly, unless the library opts in with `PrivateAssets="none"` (props and targets ship in `buildTransitive/`; see the rules below). Warning `AG0005` means the analyzer runs without the package props. It is a build-time generator; the package ships no runtime assembly.

## Generated API ownership

AspectGenerator can generate the authoring/runtime API into the current compilation:

```text
AspectAttribute
AspectInstanceLifetime
InterceptInfo
InterceptInfo<T>
InterceptData<T>
InterceptType
InterceptResult
Void
AspectGeneratorOptionsAttribute
AspectDiagnosticSeverity
```

Common modes:

```text
single project:
  AspectGeneratorGenerateApi unset
  AspectGeneratorPublicApi=false

aspect library exposing reusable aspects:
  AspectGeneratorGenerateApi unset
  AspectGeneratorPublicApi=true

consumer using public API from an aspect library:
  AspectGeneratorGenerateApi unset (a referenced public API is used instead of generating a copy)
  AspectGenerator reference (direct, or passed on by the library with PrivateAssets="none")
  still required if this project has call sites to intercept
```

`AspectGeneratorOptionsAttribute` and `AspectDiagnosticSeverity` are per-project configuration API. They are not the shared runtime contract for aspect libraries.

## Sharing and transitive flow

Set `AspectGeneratorPublicApi=true` in a library exposing reusable aspect attributes. Leave `AspectGeneratorGenerateApi` unset in its consumers so a referenced public `AspectAttribute` is reused. Only one assembly in the reference graph may expose that public API, otherwise the types clash (`CS0433`). Explicit `true` generates a copy; explicit `false` suppresses generation.

Only `PrivateAssets="all"` keeps the generator private. Omitting `PrivateAssets` also flows to consumers; use `PrivateAssets="none"` explicitly to show that intent. Transitive flow brings props and targets from `buildTransitive/`; direct and transitive references together import them once.

A library with only internal aspects can keep its reference private: its own calls are rewritten when it builds. A test project needs its own generator reference only if it calls aspected methods and expects those calls to be intercepted. A shared aspect library's private reference does not reach either its application or test consumers.

For namespace or missing-props warnings see [diagnostics](diagnostics.md). For property and assembly option precedence see [configuration](configuration.md).
