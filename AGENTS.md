# Agent Instructions

Before working on AspectGenerator hardening, diagnostics, code generation, or incremental generator refactors, read `docs/AspectGeneratorHardeningPlan.md`.

Current priority:

- Do not treat the large-project performance review item as resolved until invocation discovery no longer scans all syntax trees through `CompilationProvider` + `DescendantNodes()`.
- Keep `InterceptMethods` diagnostics deferred unless the user explicitly asks to work on them.
- Preserve CRLF line endings for touched repository files.
