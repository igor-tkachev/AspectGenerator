using System;

namespace AspectGenerator
{
	public static class AspectDiagnosticID
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
		public const string InvalidAspectDiagnosticSeverity     = "AG0209";
		public const string InterceptedCallMarker               = "AG0300";
	}
}
