using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AspectGenerator
{
	sealed class GeneratorTraceReport
	{
		static readonly GeneratorTraceReport _disabled = new(false, AspectTraceLevel.Normal);

		readonly Dictionary<string,string?> _options = new(StringComparer.Ordinal);
		readonly Dictionary<string,long>    _counters = new(StringComparer.Ordinal);
		readonly List<TracePhase>           _phases = new();
		readonly List<string>               _decisions = new();
		readonly List<string>               _events = new();
		readonly List<TargetFilterTrace>    _targetFilters = new();
		readonly long                       _start = Stopwatch.GetTimestamp();
		bool                                _finalized;

		GeneratorTraceReport(bool isEnabled, AspectTraceLevel level)
		{
			IsEnabled = isEnabled;
			Level     = level;
		}

		public static GeneratorTraceReport Create(GeneratorExecutionOptions options)
		{
			return options.Trace
				? new GeneratorTraceReport(true, options.TraceLevel)
				: _disabled;
		}

		public bool             IsEnabled { get; }
		public AspectTraceLevel Level     { get; }

		public void SetOption(string name, string? value)
		{
			if (!IsEnabled)
				return;

			_options[name] = value;
		}

		public void SetCounter(string name, long value)
		{
			if (!IsEnabled)
				return;

			_counters[name] = value;
		}

		public void IncrementCounter(string name, long delta = 1)
		{
			if (!IsEnabled)
				return;

			_counters.TryGetValue(name, out var value);
			var newValue = value + delta;
			_counters[name] = newValue;
		}

		public TracePhaseScope TimePhase(string name)
		{
			return IsEnabled ? new TracePhaseScope(this, name) : default;
		}

		public void SkipPhase(string name)
		{
			if (!IsEnabled)
				return;

			_phases.Add(new TracePhase(name, null));
			AddEvent("phase", $"{ToTraceKey(name)}: skipped");
		}

		public void AddDecision(string text)
		{
			if (!IsEnabled)
				return;

			_decisions.Add(text);
			AddEvent("decision", text);
		}

		public void AddCompilation(Compilation compilation, GeneratorExecutionOptions options)
		{
			if (!IsEnabled)
				return;

			SetCounter("Syntax trees", compilation.SyntaxTrees.Count());
			SetCounter("References", compilation.References.Count());

			SetOption("Assembly", compilation.AssemblyName ?? compilation.Assembly.Identity.Name);
			SetOption("TargetFramework", options.TargetFramework);
			SetOption("Configuration", options.Configuration);
			SetOption("DesignTimeBuild", options.DesignTimeBuild.ToString());
			SetOption("Host", GeneratorHost.Describe(options.Host));
			SetOption("Language version", compilation.SyntaxTrees.FirstOrDefault()?.Options is CSharpParseOptions parseOptions
				? parseOptions.LanguageVersion.ToString()
				: "");
			AddEvent("start", $"assembly={compilation.AssemblyName ?? compilation.Assembly.Identity.Name}, targetFramework={options.TargetFramework ?? ""}");
		}

		public void AddOptions(GeneratorExecutionOptions options)
		{
			if (!IsEnabled)
				return;

			SetOption("AspectGeneratorGenerateApi", options.GenerateApi.ToString());
			SetOption("AspectGeneratorPublicApi", options.PublicApi.ToString());
			SetOption("AspectGeneratorDebuggerStepThrough", options.DebuggerStepThrough.ToString());
			SetOption("AspectGeneratorAspectDiagnosticSeverity", options.AspectDiagnosticSeverity.ToString());
			SetOption("AspectGeneratorInterceptorsNamespace", options.InterceptorsNamespace);
			SetOption("InterceptorsNamespaces", options.InterceptorsNamespaces);
			SetOption("AspectGeneratorReportFile", options.ReportFile);
			// In kilobytes, as the property takes it.
			SetOption("AspectGeneratorReportSizeLimit", (options.ReportSizeLimit / 1024).ToString(CultureInfo.InvariantCulture));
			SetOption("AspectGeneratorTrace", options.Trace.ToString());
			SetOption("AspectGeneratorTraceLevel", options.TraceLevel.ToString());
			AddEvent("options", $"generateApi={options.GenerateApi.ToString()}, trace={options.Trace.ToString()}, diagnosticSeverity={options.AspectDiagnosticSeverity}");

			if (!string.IsNullOrWhiteSpace(options.InvalidTraceLevelValue))
				AddWarning($"unknown AspectGeneratorTraceLevel '{options.InvalidTraceLevelValue}'; using Normal");
		}

		public void AddTargetFilter(
			int    index,
			string classification,
			bool   declarationFilter,
			bool   callsitePrefilter,
			bool   semanticFilter,
			bool   callsiteSemanticFilter,
			bool   mayTargetExternalMethods)
		{
			if (!IsEnabled)
				return;

			_targetFilters.Add(new TargetFilterTrace(
				index,
				classification,
				declarationFilter,
				callsitePrefilter,
				semanticFilter,
				callsiteSemanticFilter,
				mayTargetExternalMethods));
			AddEvent(
				"target-filter",
				$"index={index.ToString(CultureInfo.InvariantCulture)}, classification={classification}, declarationFilter={declarationFilter.ToString()}, callsitePrefilter={callsitePrefilter.ToString()}, semanticFilter={semanticFilter.ToString()}, callsiteSemanticFilter={callsiteSemanticFilter.ToString()}, mayTargetExternal={mayTargetExternalMethods.ToString()}");
		}

		public void AddResult(string text)
		{
			if (!IsEnabled)
				return;

			AddEvent("result", text);
		}

		public void AddWarning(string text)
		{
			if (!IsEnabled)
				return;

			AddEvent("warning", text);
		}

		public string ToMarkdown()
		{
			if (!IsEnabled)
				return "";

			AddFinalResults();

			var sb = new StringBuilder();

			sb
				.AppendLine("## AspectGenerator trace")
				.AppendLine()
				.AppendLine("### Compilation and options")
				.AppendLine()
				.AppendLine("| Property | Value |")
				.AppendLine("| --- | --- |");

			foreach (var item in _options.OrderBy(static item => item.Key, StringComparer.Ordinal))
				AppendTableRow(sb, item.Key, item.Value ?? "");

			sb
				.AppendLine()
				.AppendLine("### Counters")
				.AppendLine()
				.AppendLine("| Counter | Value |")
				.AppendLine("| --- | ---: |");

			foreach (var item in _counters.OrderBy(static item => item.Key, StringComparer.Ordinal))
				AppendTableRow(sb, item.Key, item.Value.ToString(CultureInfo.InvariantCulture));

			sb
				.AppendLine()
				.AppendLine("### Timings")
				.AppendLine()
				.AppendLine("| Phase | Elapsed |")
				.AppendLine("| --- | ---: |");

			foreach (var phase in _phases)
				AppendTableRow(sb, phase.Name, phase.Elapsed is null ? "skipped" : FormatElapsed(phase.Elapsed.Value));

			sb
				.AppendLine()
				.AppendLine("### Decisions")
				.AppendLine();

			if (_decisions.Count == 0)
				sb.AppendLine("- No trace decisions recorded.");
			else
				foreach (var decision in _decisions)
					sb.Append("- ").AppendLine(decision);

			sb
				.AppendLine()
				.AppendLine("### Target filter classification")
				.AppendLine()
				.AppendLine("| # | Classification | Declaration filter | Call-site prefilter | Target semantic model | Call-site semantic model | May target external methods |")
				.AppendLine("| ---: | --- | --- | --- | --- | --- | --- |");

			if (_targetFilters.Count == 0)
				AppendTableRow(sb, "", "No target filters recorded.", "", "", "", "", "");
			else
				foreach (var filter in _targetFilters)
					AppendTableRow(
						sb,
						filter.Index.ToString(CultureInfo.InvariantCulture),
						filter.Classification,
						filter.DeclarationFilter.ToString(),
						filter.CallsitePrefilter.ToString(),
						filter.SemanticFilter.ToString(),
						filter.CallsiteSemanticFilter.ToString(),
						filter.MayTargetExternalMethods.ToString());

			sb
				.AppendLine()
				.AppendLine("### AGTRACE events")
				.AppendLine();

			foreach (var item in _events)
				sb.Append("- `").Append(FormatMarkdownCode(item)).AppendLine("`");

			return sb.ToString();
		}

		void AddElapsed(string name, TimeSpan elapsed)
		{
			if (!IsEnabled)
				return;

			_phases.Add(new TracePhase(name, elapsed));
			AddEvent("phase", $"{ToTraceKey(name)}: end elapsedMs={elapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}");
		}

		void AddFinalResults()
		{
			if (!IsEnabled)
				return;

			if (_finalized)
				return;

			_finalized = true;

			foreach (var counter in _counters.OrderBy(static counter => counter.Key, StringComparer.Ordinal))
				AddEvent("counter", $"{ToTraceKey(counter.Key)}={counter.Value.ToString(CultureInfo.InvariantCulture)}");

			if (_counters.TryGetValue("Generated interceptor methods", out var generatedInterceptors))
				AddResult($"generated-interceptors={generatedInterceptors.ToString(CultureInfo.InvariantCulture)}");

			var elapsedTicks = Stopwatch.GetTimestamp() - _start;
			var elapsed = TimeSpan.FromSeconds((double)elapsedTicks / Stopwatch.Frequency);
			AddResult($"totalElapsedMs={elapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}");

			var slowestPhase = _phases
				.Where(static phase => phase.Elapsed is not null)
				.OrderByDescending(static phase => phase.Elapsed!.Value)
				.FirstOrDefault();

			if (slowestPhase.Name is not null)
				AddResult($"slowestPhase={ToTraceKey(slowestPhase.Name)}");
		}

		void AddEvent(string category, string text)
		{
			_events.Add($"AGTRACE {category}: {text}");
		}

		static string FormatElapsed(TimeSpan elapsed)
		{
			return elapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) + " ms";
		}

		static string ToTraceKey(string value)
		{
			return value switch
			{
				"Applied aspect attributes"  => "applied-aspects",
				"Aspect definitions"         => "aspect-definitions",
				"Candidate call syntax nodes" => "invocation-candidates",
				"Invocation candidates before prefilter" => "invocation-candidates-before-prefilter",
				"Invocation candidates after prefilter"  => "invocation-candidates-after-prefilter",
				"Resolved invocation symbols" => "symbol-resolution-attempts",
				"Symbol resolution attempts"  => "symbol-resolution-attempts",
				"Symbol resolution successes" => "symbol-resolution-successes",
				"Symbol resolution failures"  => "symbol-resolution-failures",
				"Target method declarations"  => "target-method-declarations",
				"Target methods after declaration filter" => "target-methods-after-declaration-filter",
				"Target methods after semantic filter"    => "target-methods-after-semantic-filter",
				"Intercepted call sites"      => "intercepted-call-sites",
				"Generated interceptor methods" => "generated-interceptors",
				_ => value.Trim().ToLowerInvariant().Replace(' ', '-')
			};
		}

		static void AppendTableRow(StringBuilder sb, params string?[] values)
		{
			sb
				.Append("| ")
				.Append(string.Join(" | ",
					values.Select(static value => (value ?? "")
						.Replace("\\", "\\\\")
						.Replace("|",  "\\|")
						.Replace("\r", "")
						.Replace("\n", "<br />"))))
				.AppendLine(" |");
		}

		static string FormatMarkdownCode(string value)
		{
			return value.Replace("`", "\\`");
		}

		readonly struct TracePhase
		{
			public TracePhase(string name, TimeSpan? elapsed)
			{
				Name    = name;
				Elapsed = elapsed;
			}

			public string?   Name    { get; }
			public TimeSpan? Elapsed { get; }
		}

		readonly struct TargetFilterTrace
		{
			public TargetFilterTrace(
				int    index,
				string classification,
				bool   declarationFilter,
				bool   callsitePrefilter,
				bool   semanticFilter,
				bool   callsiteSemanticFilter,
				bool   mayTargetExternalMethods)
			{
				Index                    = index;
				Classification           = classification;
				DeclarationFilter        = declarationFilter;
				CallsitePrefilter        = callsitePrefilter;
				SemanticFilter           = semanticFilter;
				CallsiteSemanticFilter   = callsiteSemanticFilter;
				MayTargetExternalMethods = mayTargetExternalMethods;
			}

			public int    Index                    { get; }
			public string Classification           { get; }
			public bool   DeclarationFilter        { get; }
			public bool   CallsitePrefilter        { get; }
			public bool   SemanticFilter           { get; }
			public bool   CallsiteSemanticFilter   { get; }
			public bool   MayTargetExternalMethods { get; }
		}

		public readonly struct TracePhaseScope : IDisposable
		{
			readonly GeneratorTraceReport? _trace;
			readonly string?               _name;
			readonly long                  _start;

			public TracePhaseScope(GeneratorTraceReport trace, string name)
			{
				_trace = trace;
				_name  = name;
				_start = Stopwatch.GetTimestamp();
			}

			public void Dispose()
			{
				if (_trace is null || _name is null)
					return;

				var elapsedTicks = Stopwatch.GetTimestamp() - _start;
				var elapsed = TimeSpan.FromSeconds((double)elapsedTicks / Stopwatch.Frequency);

				_trace.AddElapsed(_name, elapsed);
			}
		}
	}
}
