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

		/// <summary>
		/// The AG0007 message: how many calls selected only by a target filter were left as they are for one reason. The
		/// calls themselves are AG0006, which is off by default: every diagnostic with a source location makes the compiler
		/// look for [SuppressMessage] on the declarations around it, binding the enclosing method again each time, and a
		/// broad filter can skip thousands of calls.
		/// </summary>
		public static string GetFilterSelectedCallsNotInterceptedMessage(int count, string reason)
		{
			var calls = count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

			return count == 1
				? $"1 call selected by a target filter was not intercepted ({reason}). Enable {AspectDiagnosticID.FilterSelectedCallNotIntercepted} to list it."
				: $"{calls} calls selected by a target filter were not intercepted ({reason}). Enable {AspectDiagnosticID.FilterSelectedCallNotIntercepted} to list them.";
		}

		static DiagnosticDescriptor CreateInterceptedCallMarkerDescriptor(DiagnosticSeverity severity)
		{
			return new DiagnosticDescriptor(
			AspectDiagnosticID.InterceptedCallMarker,
			"Call is marked for interception",
			"Call is marked for interception by {0}; generated method: {1}",
			"AspectGenerator",
			severity,
			true,
			"Shows where AspectGenerator applies aspects and names the generated interceptor method.",
			"https://github.com/igor-tkachev/AspectGenerator/wiki/Diagnostics");
		}
	}
}
