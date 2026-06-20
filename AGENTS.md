# Agent Instructions

Before working on AspectGenerator hardening, diagnostics, code generation, or incremental generator refactors, read `docs/AspectGeneratorHardeningPlan.md`.

Current notes:

- Invocation discovery should stay on `SyntaxProvider` candidates. Do not reintroduce full syntax-tree scans through `CompilationProvider` + `DescendantNodes()`.
- Keep `InterceptMethods` diagnostics deferred unless the user explicitly asks to work on them.
- Preserve CRLF line endings for touched repository files.
- Do not add new NuGet packages, template engines, formatters, source-generation helper libraries, or build tools unless the maintainer explicitly asks for that dependency.
- Do not replace internal code generation with Scriban, Handlebars.Net, CSharpier, or other external templating/formatting tools as an automatic refactoring step.
