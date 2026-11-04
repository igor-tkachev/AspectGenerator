# Using AspectGenerator in libraries

AspectGenerator is a development dependency: `dotnet add package AspectGenerator` writes `PrivateAssets="all"`, so the generator runs only in the project that references it and does not reach the projects that consume that project. A call site is rewritten only by the generator in the project that compiles it, so a project needs the generator when its own code contains calls to intercept.

A library can opt in to passing the generator on with `PrivateAssets="none"`. The package ships its MSBuild props and targets in `buildTransitive/` (and `build/` forwards to it), so a transitive consumer gets `InterceptorsNamespaces` and the generator options as well, and a direct reference in the same project never applies them twice.

## Scenarios

| Scenario | Library csproj | What the app needs | What happens |
| --- | --- | --- | --- |
| App with aspects | n/a | `<PackageReference Include="AspectGenerator" Version="1.1.0" PrivateAssets="all" />` | Calls in the app are intercepted. |
| Library with internal aspects only | `PackageReference` with `PrivateAssets="all"` | Nothing | Calls inside the library are rewritten when the library is built, and the rewrite is part of its IL. The app does not run the generator. |
| Shared aspect library (public aspect attributes) | `PrivateAssets="all"` and `<AspectGeneratorPublicApi>true</AspectGeneratorPublicApi>` | A reference to the library, plus its own AspectGenerator reference (`PrivateAssets="all"`) to intercept calls in the app | The library exposes `AspectAttribute` and related types. The app does not generate a second copy of that API. Without its own reference the app compiles, but aspects applied in the app are not applied. |
| Public aspects that apply in consumers, opt-in flow | `PrivateAssets="none"` and `AspectGeneratorPublicApi=true` | A reference to the library only | The library package depends on AspectGenerator; the generator, `InterceptorsNamespaces` and the options reach the app transitively, and calls in the app to aspected methods of the library are intercepted. Every consumer of the library runs the generator. |
| Public aspects that apply in consumers, direct reference | `PrivateAssets="all"` and `AspectGeneratorPublicApi=true` | A reference to the library and its own AspectGenerator reference (`PrivateAssets="all"`) | Same result, and the library does not force the generator on its consumers. |
| Multi-project solution | Each project that contains calls to intercept references the generator; put the reference in `Directory.Build.props` to write it once | The same | The generator runs per project, with its own report and API ownership. Set `AspectGeneratorPublicApi=true` only in the project that defines the shared aspects. |
| Test project | n/a | Its own AspectGenerator reference if the tests should intercept their calls to aspected methods | The app's private reference does not reach the test project. Tests that only exercise the app's code need nothing, because the app's own call sites are already rewritten in its IL; tests that call aspected methods and expect the aspects to run need the reference. |
| Upgrading from 1.0.0 | `PrivateAssets="all"` to keep it private (what `dotnet add package` does now); `PrivateAssets="none"` to keep the old flow | Nothing, unless the library relied on the flow and now keeps it private: then add the reference | In 1.0.0 the reference flowed implicitly, and in a consumer the analyzer could run without its props. Now the flow is explicit and always comes with the props. |

## Opt-in flow

```xml
<!-- Library -->
<ItemGroup>
  <PackageReference Include="AspectGenerator" Version="1.1.0" PrivateAssets="none" />
</ItemGroup>

<PropertyGroup>
  <AspectGeneratorPublicApi>true</AspectGeneratorPublicApi>
</PropertyGroup>
```

Write `PrivateAssets="none"` explicitly. Leaving `PrivateAssets` out passes the generator through project references, but in a packed library NuGet then writes `exclude="Build,Analyzers"` into the dependency, so the generator does not reach the consumers of the package.

The app needs no AspectGenerator settings. `AspectGeneratorGenerateApi` is not set by default: when it is unset and a referenced assembly already declares a public `AspectAttribute`, the project uses that API instead of generating its own. Set `AspectGeneratorGenerateApi` to `true` or `false` to choose explicitly.

## Direct reference

```xml
<!-- Library: keeps the generator private -->
<PackageReference Include="AspectGenerator" Version="1.1.0" PrivateAssets="all" />
<PropertyGroup>
  <AspectGeneratorPublicApi>true</AspectGeneratorPublicApi>
</PropertyGroup>

<!-- App: its own reference -->
<PackageReference Include="AspectGenerator" Version="1.1.0" PrivateAssets="all" />
<PackageReference Include="MyCompany.Aspects" Version="..." />
```

A reference that is both direct and transitive is safe: the props are guarded against a second import, and the app generates no duplicate API.

## When the generator runs without its props

If the analyzer reaches a project without the package's MSBuild props (for example through a manual `Analyzer` item), the project has no `InterceptorsNamespaces` and the interceptors do not compile. When the generator would intercept calls in such a project, it reports one warning, [`AG0005`](Diagnostics), instead of leaving only the compiler errors. Reference the package from that project, or let the library flow it with `PrivateAssets="none"`.
