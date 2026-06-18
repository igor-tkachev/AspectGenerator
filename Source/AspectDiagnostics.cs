using Microsoft.CodeAnalysis;

namespace AspectGenerator
{
	static class AspectDiagnostics
	{
		#pragma warning disable RS2008 // AspectGenerator does not use analyzer release tracking files yet.

		public static readonly DiagnosticDescriptor InterceptedCallMarkerHiddenDescriptor  = CreateInterceptedCallMarkerDescriptor(DiagnosticSeverity.Hidden);
		public static readonly DiagnosticDescriptor InterceptedCallMarkerInfoDescriptor    = CreateInterceptedCallMarkerDescriptor(DiagnosticSeverity.Info);
		public static readonly DiagnosticDescriptor InterceptedCallMarkerWarningDescriptor = CreateInterceptedCallMarkerDescriptor(DiagnosticSeverity.Warning);
		public static readonly DiagnosticDescriptor InterceptedCallMarkerErrorDescriptor   = CreateInterceptedCallMarkerDescriptor(DiagnosticSeverity.Error);

		#pragma warning restore RS2008

		public static DiagnosticDescriptor GetInterceptedCallMarkerDescriptor(AspectDiagnosticSeverity severity)
		{
			return severity switch
			{
				AspectDiagnosticSeverity.Hidden  => InterceptedCallMarkerHiddenDescriptor,
				AspectDiagnosticSeverity.Info    => InterceptedCallMarkerInfoDescriptor,
				AspectDiagnosticSeverity.Warning => InterceptedCallMarkerWarningDescriptor,
				AspectDiagnosticSeverity.Error   => InterceptedCallMarkerErrorDescriptor,
				_ => throw new System.InvalidOperationException($"{AspectDiagnosticID.InterceptedCallMarker} is disabled."),
			};
		}

		static DiagnosticDescriptor CreateInterceptedCallMarkerDescriptor(DiagnosticSeverity severity)
		{
			return new DiagnosticDescriptor(
			AspectDiagnosticID.InterceptedCallMarker,
			"Call is marked for interception",
			"Call is marked for interception by {0}",
			"AspectGenerator",
			severity,
			true,
			"Shows where AspectGenerator applies aspects.",
			"https://github.com/igor-tkachev/AspectGenerator/wiki/Diagnostics");
		}
	}
}
