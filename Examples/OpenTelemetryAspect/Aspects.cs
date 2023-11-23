using System;
using System.Diagnostics;
using System.Threading.Tasks;
using AspectGenerator;

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
		OnUsing      = nameof(OnUsing),
		OnAsyncUsing = nameof(OnAsyncUsing),
		OnFinally    = nameof(OnFinally)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
	sealed class MetricsAttribute : Attribute
	{
		static readonly ActivitySource _activitySource = new("Sample.Aspect");

		public static Activity? OnUsing(InterceptInfo info)
		{
			var activity = _activitySource.StartActivity(info.MemberInfo.Name);

			info.Tag = activity;

			return activity;
		}

		class AsyncActivity(Activity activity) : IAsyncDisposable
		{
			public readonly Activity Activity = activity;

			public ValueTask DisposeAsync()
			{
				Activity.Dispose();
				return ValueTask.CompletedTask;
			}
		}

		public static IAsyncDisposable? OnAsyncUsing(InterceptInfo info)
		{
			var activity = _activitySource.StartActivity(info.MemberInfo.Name);

			if (activity == null)
				return null;

			var asyncActivity = new AsyncActivity(activity);

			info.Tag = asyncActivity;

			return asyncActivity;
		}

		public static void OnFinally(InterceptInfo info)
		{
			switch (info)
			{
				case { Tag: Activity activity, Exception: var ex } : SetStatus(activity,    ex); break;
				case { Tag: AsyncActivity aa,  Exception: var ex } : SetStatus(aa.Activity, ex); break;
			}

			static void SetStatus(Activity activity, Exception? ex) =>
				activity.SetStatus(ex is null ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
		}
	}

	/// <summary>
	/// IgnoreCatch aspect.
	/// </summary>
	[Aspect(
		OnCatch = nameof(OnCatch)
		)]
	[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
	sealed class IgnoreCatchAttribute : Attribute
	{
		public static void OnCatch(InterceptInfo info)
		{
			info.InterceptResult = InterceptResult.IgnoreThrow;
		}
	}
}
