using System;
using System.Diagnostics;

using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Aspects
{
	/// <summary>
	/// Initializes OpenTelemetry.
	/// </summary>
	static class OpenTelemetryFactory
	{
		public static TracerProvider? Create()
		{
			return Sdk.CreateTracerProviderBuilder()
				.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("MySample"))
				.AddSource("Sample.Aspect")
				.AddConsoleExporter()
				.Build();
		}
	}

	/// <summary>
	/// Metrics aspect.
	/// </summary>
	[Aspect(
		// Specify the name of the method used in the 'using' statement
		// that returns a IDisposable object.
		OnUsing = nameof(OnUsing)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
	sealed class MetricsAttribute : Attribute
	{
		static readonly ActivitySource _activitySource = new("Sample.Aspect");

		public static Activity? OnUsing(InterceptCallInfo info)
		{
			return _activitySource.StartActivity(info.MemberInfo.Name);
		}
	}
}
