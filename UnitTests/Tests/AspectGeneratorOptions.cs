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
		@"regex: ^public static System.String AspectGenerator\.Tests\.UnitTests\.AssemblyFilterTarget\(\)$",
		@"regex: ^public static System.String AspectGenerator\.Tests\.UnitTests\.InterceptedMethod\(System.String\)$",
		@"regex: ^public static System.String AspectGenerator\.Tests\.UnitTests\.InterceptedGenericMethod<System.String>\(System.String\)$",
		@"regex: ^public System.String System.String.Substring\(System.Int32\)$"
	])]

[assembly: AspectGenerator.Tests.OnCall(
	TargetFilter =
	[
		@"regex: ^public System.Int32 AspectGenerator\.Tests\.OnCallObject\.OnCall\(System.Int32\)$"
	])]
