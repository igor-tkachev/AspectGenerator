using Microsoft.CodeAnalysis;

namespace AspectGenerator
{
	static class AspectDiagnostics
	{
		public static class Id
		{
			public const string OnCallNotLastAspect                 = "AG0001";
			public const string OnCallNotLastMethod                 = "AG0002";
			public const string CannotIntercept                     = "AG0003";
			public const string NamespaceNotAllowed                 = "AG0004";
			public const string HookMethodNotFound                  = "AG0101";
			public const string HookMethodNotStatic                 = "AG0102";
			public const string HookInvalidParameters               = "AG0103";
			public const string HookInvalidReturnType               = "AG0104";
			public const string OnCallHookMismatch                  = "AG0105";
			public const string HookRequiresInterceptData           = "AG0106";
			public const string AsyncHookRequiresTask               = "AG0107";
			public const string InvalidAspectFilterRegex            = "AG0201";
			public const string InvalidAspectFilterRule             = "AG0202";
			public const string UnknownAspectFilterConditionKey     = "AG0204";
			public const string InvalidAspectFilterParameterPattern = "AG0205";
			public const string InvalidAspectFilterDottedPattern    = "AG0206";
			public const string MethodLevelTargetFilter             = "AG0208";
			public const string InterceptedCallMarker               = "AG0300";
		}

		#pragma warning disable RS2008 // AspectGenerator does not use analyzer release tracking files yet.
		public static readonly DiagnosticDescriptor InterceptedCallMarkerHiddenDescriptor = CreateInterceptedCallMarkerDescriptor(DiagnosticSeverity.Hidden);
		public static readonly DiagnosticDescriptor InterceptedCallMarkerInfoDescriptor   = CreateInterceptedCallMarkerDescriptor(DiagnosticSeverity.Info);
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
				_ => throw new System.InvalidOperationException("AG0300 is disabled."),
			};
		}

		static DiagnosticDescriptor CreateInterceptedCallMarkerDescriptor(DiagnosticSeverity severity)
		{
			return new DiagnosticDescriptor(
			Id.InterceptedCallMarker,
			"Call is intercepted by AspectGenerator",
			"Call is intercepted by {0}",
			"AspectGenerator",
			severity,
			true,
			"Marks a call site that is selected by AspectGenerator and is expected to be intercepted in a normal build.",
			"https://github.com/igor-tkachev/AspectGenerator/wiki/Diagnostics");
		}
	}
}
