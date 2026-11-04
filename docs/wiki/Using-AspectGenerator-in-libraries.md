# Using AspectGenerator in libraries

AspectGenerator is a development dependency: `dotnet add package AspectGenerator` writes `PrivateAssets="all"`, so the generator runs only in the project that references it and does not reach that project's consumers. A call site is rewritten only by the generator in the project that compiles it, so a project needs the generator when its own code contains calls to intercept.

A library can pass the generator on to its consumers by writing `PrivateAssets="none"`. The package ships its MSBuild props and targets in `buildTransitive/`, so a transitive consumer gets `InterceptorsNamespaces` and the generator options with it, and a direct reference in the same project is harmless.

## Scenarios

| Scenario | Library csproj | App | Result |
| --- | --- | --- | --- |
| App with aspects | n/a | `PackageReference` with `PrivateAssets="all"` | Calls in the app are intercepted. |
| Library with internal aspects only | `PrivateAssets="all"` | Nothing | Calls inside the library are rewritten when it is built. The app does not run the generator. |
| Shared aspect library (public attributes) | `PrivateAssets="all"`, `AspectGeneratorPublicApi=true` | Library reference, plus its own AspectGenerator reference to intercept its calls | The app uses the library's API and generates no copy. Without its own reference the app builds, but aspects applied in the app are not applied. |
| Public aspects applied in consumers: opt-in flow | `PrivateAssets="none"`, `AspectGeneratorPublicApi=true` | Library reference only | The generator and its props reach the app, and the app's calls to the library's aspected methods are intercepted. Every consumer of the library runs the generator. |
| Public aspects applied in consumers: direct reference | `PrivateAssets="all"`, `AspectGeneratorPublicApi=true` | Library reference and its own AspectGenerator reference | Same result; the library does not force the generator on its consumers. |
| Multi-project solution | Reference the generator in each project that has calls to intercept (once, in `Directory.Build.props`) | Same | One generator run per project. Set `AspectGeneratorPublicApi=true` only in the project that defines the shared aspects. |
| Test project | n/a | Its own reference, only if the tests call aspected methods and expect the aspects to run | The app's private reference does not reach the test project. |
| Upgrading from 1.0.0 | `PrivateAssets="all"` to keep it private; `PrivateAssets="none"` to keep the old flow | Add a reference if the library now stays private | In 1.0.0 the reference flowed implicitly and could reach a consumer without its props. Now the flow is explicit and always comes with the props. |

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

Only `PrivateAssets="all"` keeps the generator private. Leaving `PrivateAssets` out behaves like `none` for consumers: in a packed library NuGet writes `exclude="Build,Analyzers"` into the dependency, but the analyzer and the `buildTransitive/` props still reach the consumers. Write `none` explicitly so the intent is visible.

The app needs no AspectGenerator settings. Leave `AspectGeneratorGenerateApi` unset: when a referenced assembly already declares a public `AspectAttribute`, the project uses that API instead of generating its own. Only one assembly in an app's reference graph may expose the public API, otherwise the types clash (CS0433). Setting `AspectGeneratorGenerateApi` to `true` or `false` chooses explicitly.

## Direct reference

```xml
<!-- Library: keeps the generator private -->
<ItemGroup>
  <PackageReference Include="AspectGenerator" Version="1.1.0" PrivateAssets="all" />
</ItemGroup>

<PropertyGroup>
  <AspectGeneratorPublicApi>true</AspectGeneratorPublicApi>
</PropertyGroup>

<!-- App: its own reference -->
<ItemGroup>
  <PackageReference Include="AspectGenerator" Version="1.1.0" PrivateAssets="all" />
  <PackageReference Include="MyCompany.Aspects" Version="..." />
</ItemGroup>
```

A reference that is both direct and transitive is safe: NuGet imports the package's `buildTransitive/` files once, and the app generates no duplicate API.

## When the generator runs without its props

If the analyzer reaches a project without the package's MSBuild props (for example an analyzer reference added by hand) and the generated interceptor namespace is not listed in `InterceptorsNamespaces`, the interceptors cannot compile. The generator then reports one warning, [`AG0005`](Diagnostics), next to the compiler's interceptor errors. Reference the package from that project, or let the library pass it on with `PrivateAssets="none"`. A project wired by hand with `InterceptorsNamespaces` set gets no AG0005.
