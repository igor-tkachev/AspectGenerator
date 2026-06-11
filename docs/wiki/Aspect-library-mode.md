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
<AspectGeneratorGenerateApi>true</AspectGeneratorGenerateApi>
<AspectGeneratorPublicApi>true</AspectGeneratorPublicApi>
```

In consuming projects:

```xml
<AspectGeneratorGenerateApi>false</AspectGeneratorGenerateApi>
```

All projects that need generated interceptors must set a generated interceptor namespace and include it in `InterceptorsNamespaces`.
