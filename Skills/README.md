# Maintaining the AspectGenerator skill

This file is for repository maintainers and is not packed. `aspectgenerator/SKILL.md` is the short consumer entry point; `aspectgenerator/docs/*.md` contains task guides. All Markdown under `aspectgenerator/` ships in the main AspectGenerator package at `skills/aspectgenerator/`. No other package should ship the same skill name. There is no package-root copy of `SKILL.md`.

Consumers read the installed skill from `<global-packages>/aspectgenerator/<version>/skills/aspectgenerator/` or copy that folder into `.agents/skills/aspectgenerator/` or `.claude/skills/aspectgenerator/`. Do not create or write `.dotnet-package-skills.json`; it belongs to the beta `dotnet-package-skills` tool.

## Rules for skill content

- The skill works from both locations. Links between skill files are relative and stay inside `aspectgenerator/`. Do not link to package-root assets or repository paths. AspectGenerator has no `lib` runtime assembly or packaged runtime XML reference; its authoring API is generated into the consuming compilation.
- `SKILL.md` frontmatter has `name: aspectgenerator`. Its one-line `description` explains what the skill covers and when to use it, has at most 1024 characters, and contains neither `: ` nor ` #` sequences. Use the plain-scalar format checked by the package tests.
- Write for the reader who opens one guide for one task. Each guide starts with a one-line pointer to `SKILL.md` and a “You are here if” block. State rules plainly; avoid banners, capitals and “must read” instructions that send agents through every guide.
- Text written into user code, including comments and names, is ordinary code text, with no notes about the agent that wrote it.
- Keep guidance versioned with the implementation. Changes to a documented public API, generator capability or default update the corresponding guide in the same change. Keep the README and wiki synchronized as behavior changes.
- Do not put maintainer prompts, roadmaps, test prompts or internal notes under `aspectgenerator/`; every file there ships to consumers.
- Files are UTF-8 without BOM. Follow `.gitattributes` (`text=auto`); repository text files are stored with LF line endings.

## Validation

CI packs the main project before running the package-content test. That test validates the actual archive's skill layout, frontmatter, relative link destinations and portable paths. It also checks that this README and the former package-root entry point are absent.

Build and pack with warnings as errors and node reuse disabled:

```sh
export MSBUILDDISABLENODEREUSE=1
dotnet build AspectGenerator.slnx -c Release -warnaserror -nodeReuse:false
dotnet pack Source/AspectGenerator.csproj --no-build -c Release -warnaserror -nodeReuse:false
dotnet test UnitTests/Tests/AspectGenerator.Tests.csproj --no-build -c Release --filter NuGetPackageContainsMsBuildAssetsTest
```

For local AGTRACE diagnostics, pack a Debug generator and add the output folder as a local NuGet source:

```sh
export MSBUILDDISABLENODEREUSE=1
dotnet pack Source/AspectGenerator.csproj -c Debug -o local-nuget -p:PackageVersion=2.0.0-dev.trace.1 -warnaserror -nodeReuse:false
```

## Dependency guidance

Do not add new NuGet packages, template engines, formatters, source-generation helper libraries, or build tools unless the maintainer explicitly asks for that dependency.

In particular, do not replace internal code generation with Scriban, Handlebars.Net, CSharpier, or other external templating/formatting tools as an automatic refactoring step.

When improving generated-code quality, prefer internal refactoring first:

```text
CodeWriter
GeneratedCodeSymbolPrinter
InterceptorNamingService
AspectLifetimeResolver
smaller emitter classes
targeted regression tests
```

If a new dependency seems useful, propose it as a separate design discussion with trade-offs, package impact, versioning risk, analyzer/generator loading risk, and build performance implications. Do not include it in unrelated cleanup or refactoring changes.
