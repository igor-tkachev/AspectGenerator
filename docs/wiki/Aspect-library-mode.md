# Aspect Library Mode

AspectGenerator can either generate its API into each project or consume the API from a shared aspect library.

## Single Project Mode

Use this when one project defines and consumes its own aspects.

```xml
<AspectGeneratorPublicApi>false</AspectGeneratorPublicApi>
```

Leave `AspectGeneratorGenerateApi` unset: the project generates its API unless a referenced assembly already exposes a public one.

## Shared Aspect Library

Any project that exposes aspect attributes to other projects must set `AspectGeneratorPublicApi=true`.

In the library that defines shared aspects:

```xml
<ItemGroup>
  <PackageReference Include="AspectGenerator" Version="1.1.1" PrivateAssets="all" />
</ItemGroup>

<PropertyGroup>
  <AspectGeneratorPublicApi>true</AspectGeneratorPublicApi>
</PropertyGroup>
```

In consuming projects:

```xml
<ItemGroup>
  <PackageReference Include="AspectGenerator" Version="1.1.1" PrivateAssets="all" />
  <PackageReference Include="MyCompany.Aspects" Version="..." />
</ItemGroup>
```

The consumer leaves `AspectGeneratorGenerateApi` unset and uses the library's public API. Only one assembly in the app's reference graph may expose the public API, otherwise the types clash (CS0433).

Every project whose call sites should be intercepted needs the generator. The default private reference does not flow through a library: the package is a development dependency, so a consumer of your library (or of a shared aspect library) does not get AspectGenerator unless it references it itself or the library opts in with `PrivateAssets="none"`. See [Using AspectGenerator in libraries](Using-AspectGenerator-in-libraries). The package appends the generated interceptor namespace to `InterceptorsNamespaces` automatically for direct package consumers.
