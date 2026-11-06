[AspectGenerator skill](../SKILL.md)

# Diagnostics and fixes

> You are here if a build or editor reports an AGxxxx diagnostic or you want intercepted-call markers.

| Diagnostic | Meaning | Action |
| --- | --- | --- |
| `AG0001` | OnCall aspect must be last when applied directly | Move the OnCall aspect last in the applied aspect chain. |
| `AG0002` | OnCall aspect must be last when inferred from method metadata | Move the OnCall aspect last in the effective aspect chain. |
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
| `AG0201` | invalid TargetFilter regex | Correct the regex reported in the message. |
| `AG0202` | invalid TargetFilter rule | Correct the rule using the target filter syntax. |
| `AG0204` | unknown TargetFilter condition key | Use a supported condition key, including attributes: rather than attribute:. |
| `AG0205` | invalid TargetFilter parameter pattern | Correct the parameter pattern identified in the message. |
| `AG0206` | invalid TargetFilter dotted pattern | Correct the dotted name pattern identified in the message. |
| `AG0208` | TargetFilter is used on a method-level aspect attribute | Move TargetFilter to an assembly/type application; use a plain aspect application on a method. |
| `AG0209` | invalid AspectGenerator diagnostic severity value | Choose Off, Hidden, Info, Warning or Error. |
| `AG0300` | optional intercepted-call marker | No fix needed; set AspectGeneratorAspectDiagnosticSeverity=Off to disable markers. |

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
