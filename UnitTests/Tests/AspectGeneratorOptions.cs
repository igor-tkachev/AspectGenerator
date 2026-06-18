using AspectGenerator;

[assembly: AspectGeneratorOptions(
	DebuggerStepThrough = true,
	PublicApi = true,
	AspectDiagnosticSeverity = AspectDiagnosticSeverity.Off,
	InterceptorsNamespace = "MyAspectGenerator")]

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
