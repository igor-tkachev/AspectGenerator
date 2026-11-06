[AspectGenerator skill](../SKILL.md)

# Generator hosts and Hot Reload

> You are here if interception or diagnostics differ between builds, live editor analysis or Hot Reload.

Interceptor source emission is automatic:

```text
compiler (csc, VBCSCompiler)                       -> build: analyzes and emits interceptor source
known editor (Visual Studio, VS Code's C# server)  -> DesignTimeBuild=true without BuildingProject=true: live analysis,
                                                      no interceptor source; after a build (BuildingProject=true): build
any other host (dotnet watch, test harnesses,
  AdhocWorkspace/MSBuildWorkspace tools, unknown IDEs) -> build
```

The generator tells the hosts apart by the process (the entry assembly), not by the MSBuild properties alone. Every host that may emit and run the compilation builds; only a known editor skips the expensive work, and only before a build: after one, the editor's compilation feeds Hot Reload and Edit and Continue, whose delta would otherwise drop the aspects of the edited method. The editor reads `DesignTimeBuild` and `BuildingProject` from `obj/.../*.GeneratedMSBuildEditorConfig.editorconfig`, which every real build rewrites with a build's values, so after a build it builds until the next design-time build.

Design-time builds report a reduced set of diagnostics: target filter errors (`AG0201`, `AG0202`, `AG0204`-`AG0206`, `AG0208`), `AG0209`, and `AG0101`/`AG0102` for aspects declared in the project. `AG0001`, `AG0002`, `AG0003`, `AG0006`, `AG0007`, `AG0103`-`AG0108` and `AG0101`/`AG0102` for library aspects need a bound call and appear only after a real build.

There is no public `GenerateInterceptors` option.

`AG0004` and `AG0005` also concern generated interceptors and are build-only. `AG0300` markers come from a separate analyzer and are not affected by the generator's design-time gate.

If a design-time build runs between the real build and a Hot Reload edit (for example after a project file change or restore), the editor returns to design-time mode until the next build. Start the debug session again after such a change.

A known editor never writes the build report, including when it builds after a real build. See [troubleshooting](troubleshooting.md) for the report and its `Generator host` row, and [diagnostics](diagnostics.md) for fixes.
