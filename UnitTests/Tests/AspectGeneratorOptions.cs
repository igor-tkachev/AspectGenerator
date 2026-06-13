using AspectGenerator;

[assembly: AspectGeneratorOptions(
	DebuggerStepThrough = true,
	InterceptorsNamespace = "MyAspectGenerator")]

[assembly: Aspects.Log(
	TargetFilterKind = AspectFilterKind.Regex,
	TargetFilter =
	[
		@"^public static System.String AspectGenerator\.Tests\.UnitTests\.AssemblyFilterTarget\(\)$",
		@"^public static System.String AspectGenerator\.Tests\.UnitTests\.InterceptedMethod\(System.String\)$",
		@"^public static System.String AspectGenerator\.Tests\.UnitTests\.InterceptedGenericMethod<System.String>\(System.String\)$",
		@"^public System.String System.String.Substring\(System.Int32\)$"
	])]

[assembly: AspectGenerator.Tests.OnCall(
	TargetFilterKind = AspectFilterKind.Regex,
	TargetFilter =
	[
		@"^public System.Int32 AspectGenerator\.Tests\.OnCallObject\.OnCall\(System.Int32\)$"
	])]
