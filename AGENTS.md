# Agent Instructions

Before working on AspectGenerator hardening, diagnostics, code generation, or incremental generator refactors, read `docs/AspectGeneratorHardeningPlan.md`.

Current notes:

- Invocation discovery should stay on `SyntaxProvider` candidates. Do not reintroduce full syntax-tree scans through `CompilationProvider` + `DescendantNodes()`.
- Keep `InterceptMethods` diagnostics deferred unless the user explicitly asks to work on them.
- Preserve CRLF line endings for touched repository files.
- Do not add new NuGet packages, template engines, formatters, source-generation helper libraries, or build tools unless the maintainer explicitly asks for that dependency.
- Do not replace internal code generation with Scriban, Handlebars.Net, CSharpier, or other external templating/formatting tools as an automatic refactoring step.

## Target filter parser

The parser of the target filter language is written in DotGram (`Source/TargetFilter/TargetFilter.gram`) and generated ahead of the build. The generated C# in `Source/TargetFilter/Generated` is committed and compiled like any other source: the build and the package have no DotGram dependency, and nothing is generated at build time.

- After any change to `TargetFilter.gram` (or to a type it builds), run `Source/TargetFilter/regenerate.sh` and commit the changed files with it. Never edit `Generated` by hand; the next run overwrites it. The script works in Git Bash on Windows too.
- `Source/TargetFilter/dotgram.pin` names the DotGram commit and the exact .NET SDK the generation uses. The script builds `DotGram.Cli` at that commit into a scratch folder (`DOTGRAM_SCRATCH`, `DOTGRAM_REPO` to fetch from a local clone) and refuses to run under another SDK: select the pinned one first, for instance with an untracked `global.json` (the refusal message prints it). Move the pin only to a green commit of the DotGram repository, in a commit of its own with the regenerated folder.
- The `filter-parser` CI job runs the same script and fails when `git status` shows any difference in `Source/TargetFilter`. `.gitattributes` keeps `*.gram` and scripts at LF and the generated folder byte for byte (`-text`, last rule).
- `-p:DotGramLocal=true -p:DotGramRoot=<a DotGram checkout>` builds the parser from the grammar with that checkout as the generator and leaves the committed folder out (default `DotGramRoot` is `..\dotgram`, next to this repository). It is for trying a DotGram change; the committed folder is what ships.
