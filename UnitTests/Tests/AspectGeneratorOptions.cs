using AspectGenerator;

[assembly: AspectGeneratorOptions(
	DebuggerStepThrough = true,
	InterceptorsNamespace = "MyAspectGenerator")]

[assembly: Aspects.Log(
	Filter =
	[
		@"^public static System.String AspectGenerator\.Tests\.UnitTests\.AssemblyFilterTarget\(\)$"
	])]
