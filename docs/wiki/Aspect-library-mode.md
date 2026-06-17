# Aspect Library Mode

AspectGenerator can either generate its API into each project or consume the API from a shared aspect library.

## Single Project Mode

Use this when one project defines and consumes its own aspects.

```xml
<AspectGeneratorGenerateApi>true</AspectGeneratorGenerateApi>
<AspectGeneratorPublicApi>false</AspectGeneratorPublicApi>
```

## Shared Aspect Library

In the library that defines shared aspects:

```xml
<ItemGroup>
  <PackageReference Include="AspectGenerator" Version="..." PrivateAssets="all" />
</ItemGroup>

<PropertyGroup>
  <AspectGeneratorGenerateApi>true</AspectGeneratorGenerateApi>
  <AspectGeneratorPublicApi>true</AspectGeneratorPublicApi>
</PropertyGroup>
```

In consuming projects:

```xml
<ItemGroup>
  <PackageReference Include="AspectGenerator" Version="..." PrivateAssets="all" />
  <PackageReference Include="MyCompany.Aspects" Version="..." />
</ItemGroup>

<PropertyGroup>
  <AspectGeneratorGenerateApi>false</AspectGeneratorGenerateApi>
</PropertyGroup>
```

Every project whose call sites should be intercepted must reference AspectGenerator directly. The package appends the generated interceptor namespace to `InterceptorsNamespaces` automatically for direct package consumers.
