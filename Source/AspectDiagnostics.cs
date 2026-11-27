using System.Collections.Generic;

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

		/// <summary>The AG0232 message: an aspect attribute written on a method that its target filters do not select.</summary>
		public static string GetAspectNotAppliedToMethodMessage(string aspect, string method)
		{
			return $"[{aspect}] on '{method}' is not applied to it: the target filters of the aspect do not select the method.";
		}

		/// <summary>
		/// The AG0235 message: the attributes that select a method write different values of one setting. The method keeps the
		/// aspect; the setting takes the aspect's default.
		/// </summary>
		/// <param name="values">Each value with the attributes that write it.</param>
		public static string GetAspectSettingsConflictMessage(string aspect, string method, string setting, IEnumerable<string> values)
		{
			return
				$"The attributes that select '{method}' for [{aspect}] write different values of {setting}: {string.Join("; ", values)}. " +
				$"Write the value on a more specific attribute; until then {setting} takes the aspect's default.";
		}

		/// <summary>
		/// The AG0236 message: a filter read from a referenced assembly cannot be read, so the aspect selects nothing in the
		/// compilation.
		/// </summary>
		public static string GetUnreadableReferencedFilterMessage(string filter, string assembly, string aspect, string error)
		{
			return
				$"The target filter of {filter} read from the referenced assembly '{assembly}' cannot be read: {error} " +
				$"[{aspect}] selects nothing in this compilation; rebuild '{assembly}' with this version of AspectGenerator.";
		}

		/// <summary>The page every diagnostic of the generator links to.</summary>
		public const string HelpLink = "https://github.com/igor-tkachev/AspectGenerator/wiki/Diagnostics";

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
			HelpLink);
		}
	}
}
