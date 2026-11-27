[AspectGenerator skill](../SKILL.md)

# Diagnostics and fixes

> You are here if a build or editor reports an AGxxxx diagnostic or you want intercepted-call markers.

| Diagnostic | Meaning | Action |
| --- | --- | --- |
| `AG0001` | OnCall aspect must be last when applied directly, also when a type-level or assembly-level target filter applies it (reported on the attribute) | Move the OnCall aspect last in the applied aspect chain. |
| `AG0002` | OnCall aspect must be last when a referenced library applied it to the method (reported at the call) | Move the OnCall aspect last in the effective aspect chain. |
| `AG0003` | invocation cannot be intercepted by the compiler | Read the reported reason and check supported call shapes in troubleshooting; leave unsupported calls unintercepted. |
| `AG0004` | generated interceptor namespace is not listed in InterceptorsNamespaces | Restore the package MSBuild imports or list the configured namespace in InterceptorsNamespaces for a custom setup. |
| `AG0005` | generator runs without the package MSBuild props (reference the package, or PrivateAssets="none" in the library) | Reference the package directly or flow it from the library with PrivateAssets="none" so the props are imported. |
| `AG0006` | call selected only by a target filter is not intercepted (off by default; dotnet_diagnostic.AG0006.severity = warning lists them on the console, suggestion in the IDE only) | Read the reason; enable warning severity temporarily to list skipped calls on the console. |
| `AG0007` | summary per reason of the calls AG0006 would list, with their number (information, no location: severity = warning in a .globalconfig to see it on the console) | Use a .globalconfig with is_global = true and warning severity for console summaries; use AG0006 for locations. |
| `AG0101` | hook method not found | Use nameof for an existing accessible hook on the aspect type or a base type. |
| `AG0102` | hook method must be static | Make the hook static. |
| `AG0103` | invalid hook parameter type | Use a supported `InterceptInfo` / `InterceptInfo<T>` signature, optionally preceded by the typed aspect; check UseInterceptData. |
| `AG0104` | invalid hook return type | Return the type named by the diagnostic; async hooks use Task or ValueTask when the target framework provides it. |
| `AG0105` | OnCall signature mismatch | Match the target return and parameter types/ref kinds; include the receiver parameter for an instance target. |
| `AG0106` | UseInterceptData=true requires `ref InterceptData<T>` | Pass the carrier as `ref InterceptData<T>`. |
| `AG0107` | async hook requires supported async target | Use synchronous hooks or a supported Task / ValueTask target; async void is unsupported. |
| `AG0108` | OnAfterCall/OnCatch/OnFinally/OnUsing on an iterator run before its body (warning when applied, information when filter-selected) | Guard the method that enumerates the sequence or materialize the result; hooks around sequence creation cannot cover its body. |
| `AG0209` | invalid AspectGenerator diagnostic severity value | Choose Off, Hidden, Info, Warning or Error. |
| `AG0220` | target filter syntax error (also `not (…)`, `Task<>`) | Fix the text at the reported characters; the message names the expected input. |
| `AG0221` | unknown or reserved key or word, or a wrong kind of value | Use a key or word from the target filters guide; a name equal to a keyword is written with `@`. |
| `AG0224` | invalid target filter pattern (`**` inside a segment, `..` twice, `?` outside a type, empty `{}`) | Correct the pattern; `?` means nullable and `*` is the wildcard. |
| `AG0225` | 1.1 filter syntax, or a property included twice in one filter | Use the 1.2 spelling the message shows; write `method: Save* or Get*` instead of two `method:` lines. |
| `AG0227` | target filter nested too deeply, or a pattern with too many wildcards or alternatives | Simplify the filter or split it. |
| `AG0231` | a `string[]` item of a filter begins with `and` or `or` | Put the operator on the line it continues, in the same item. |
| `AG0232` | warning: an aspect attribute on a method is not applied to it (build only; fails the build under TreatWarningsAsErrors) | Read the explanation in the build report; change the filters or remove the attribute. |
| `AG0233` | invalid `aspect.p` / `assembly.p` reference | Name the property being declared, a level above the block, outside a compound line. |
| `AG0234` | invalid use of `any` | Write `p: any` as a whole value, once, as an include. |
| `AG0235` | two selecting attributes write different values of one setting (build only; the method still gets the aspect) | Write the value on the more specific level, usually the class. |
| `AG0236` | a filter read from a referenced assembly cannot be read (the aspect selects nothing; the message names the assembly) | Rebuild that assembly with this version of AspectGenerator. |
| `AG0300` | optional intercepted-call marker | No fix needed; set AspectGeneratorAspectDiagnosticSeverity=Off to disable markers. |

The ids of the 1.1 filter language (`AG0201`, `AG0202`, `AG0204`-`AG0206`, `AG0208`, `AG0210`, `AG0211`) are retired and never reused; a 1.1 filter is reported as `AG0225` with the 1.2 spelling.

A missing hook in a referenced assembly can also mean an inaccessible hook: metadata does not expose internal members without `InternalsVisibleTo`. Invalid hooks whose generated calls cannot compile are left out for that target, while other aspects remain; a synchronous hook returning a value still runs as a statement despite `AG0104`. An unresolved return type is left to the compiler's own error rather than adding `AG0104`.

See [writing an aspect](writing-an-aspect.md), [target filters](target-filters.md), [library mode](library-mode.md) and [troubleshooting](troubleshooting.md) for the relevant task. The [generator host guide](generator-host.md) explains which diagnostics appear during live analysis.

## Optional intercepted-call markers

`AG0300` diagnostics mark call sites where AspectGenerator applies aspects. They are controlled by `AspectGeneratorAspectDiagnosticSeverity`:

```text
Off      disables optional markers
Hidden   reports hidden markers
Info     default; reports informational markers
Warning  reports warning markers
Error    reports error markers
```

`AG0300` is enabled by default with `Info` severity. It does not indicate a problem. Set `AspectGeneratorAspectDiagnosticSeverity` to `Off` to disable these markers. Use `AG0300` for temporary IDE/source inspection and the build report for complete aspect application analysis.
