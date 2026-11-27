using AspectGenerator;

// CS0436: the generated options API declares AspectDiagnosticSeverity in every consumer, and the generator has an internal
// enum of the same name, visible here through InternalsVisibleTo; the generated one is the right one, as the compiler picks.
#pragma warning disable CS0436
[assembly: AspectGeneratorOptions(
	DebuggerStepThrough      = true,
	AspectDiagnosticSeverity = AspectDiagnosticSeverity.Info,
	InterceptorsNamespace    = "MyAspectGenerator")]
#pragma warning restore CS0436

[assembly: Aspects.Log(
	TargetFilter =
	[
		"""
		method: {global::AspectGenerator.Tests.UnitTests.AssemblyFilterTarget, global::AspectGenerator.Tests.UnitTests.InterceptedMethod, global::AspectGenerator.Tests.UnitTests.InterceptedGenericMethod<string>, global::System.String.Substring}
		- method: global::System.String.Substring and params: (_, _)
		"""
	])]

[assembly: AspectGenerator.Tests.OnCall(
	TargetFilter =
	[
		"public global::AspectGenerator.Tests.OnCallObject.OnCall(int): int"
	])]
