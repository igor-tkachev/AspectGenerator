using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;

namespace AspectGenerator
{
	static class AspectBuildReport
	{
		#pragma warning disable RS1035 // Build reports are an explicit source-generator output channel for this package.

		public static void Write(AspectSourceGenerator.AnalysisResult analysis)
		{
			// An editor builds after a real build (Hot Reload needs its interceptors), but its report would overwrite the
			// compiler's with the state of an edit in progress; only the compiler and unknown hosts write one.
			if (analysis.Options.DesignTimeBuild ||
				analysis.Options.Host == GeneratorHostKind.Editor ||
				string.IsNullOrWhiteSpace(analysis.Options.ReportFile))
				return;

			try
			{
				var reportFile = GetReportFilePath(analysis.Options);
				var directory  = Path.GetDirectoryName(reportFile);

				if (!string.IsNullOrWhiteSpace(directory))
					Directory.CreateDirectory(directory);

				File.WriteAllText(reportFile, Format(analysis), Encoding.UTF8);
			}
			catch
			{
				// Build reports are informational and must not affect compilation.
			}

			static string GetReportFilePath(GeneratorExecutionOptions options)
			{
				var reportFile = options.ReportFile!;

				if (Path.IsPathRooted(reportFile) ||
				    string.IsNullOrWhiteSpace(options.ProjectDirectory))
					return reportFile;

				return Path.GetFullPath(Path.Combine(options.ProjectDirectory!, reportFile));
			}
		}

		#pragma warning restore RS1035

		static string Format(AspectSourceGenerator.AnalysisResult analysis)
		{
			var selectedCallSiteCount = analysis.AspectedMethods.Length;
			var targetMethodCount     = analysis.AspectedMethods.Select(static m => m.Method).Distinct(SymbolEqualityComparer.Default).Count();
			var interceptorState      = analysis.Options.EmitInterceptors ? "enabled" : "disabled";
			var generatedApiState     = analysis.Options.GenerateApi ? "enabled" : "disabled";
			var interceptorsNamespace = GetInterceptorsNamespace(analysis.Options);
			var interceptors          = InterceptorNamingService.Create(analysis.AspectedMethods);
			var sb                    = new StringBuilder();
			var generatedSourceFiles  = 1;

			if (analysis.Options.GenerateApi)
				generatedSourceFiles++;

			if (analysis.Options.EmitInterceptors && analysis.AspectedMethods.Length > 0)
				generatedSourceFiles++;

			analysis.Trace.SetCounter("Generated source files", generatedSourceFiles);
			analysis.Trace.SetCounter("Diagnostics reported", analysis.Diagnostics.Length);

			sb
				.AppendLine("# AspectGenerator Build Report")
				.AppendLine()
				.AppendLine("## Summary")
				.AppendLine()
				.AppendLine("| Item | Value |")
				.AppendLine("| --- | --- |");

			AppendTableRow(sb, "Project", analysis.Compilation.AssemblyName ?? analysis.Compilation.Assembly.Identity.Name);
			AppendTableRow(sb, "Generator host", GeneratorHost.Describe(analysis.Options.Host));
			AppendTableRow(sb, analysis.Options.EmitInterceptors ? "Generated call sites" : "Selected call sites", selectedCallSiteCount.ToString(CultureInfo.InvariantCulture));
			AppendTableRow(sb, "Target methods", targetMethodCount.ToString(CultureInfo.InvariantCulture));
			AppendTableRow(sb, "Interceptor generation", interceptorState);
			AppendTableRow(sb, "Generated API", generatedApiState);
			AppendTableRow(sb, "Interceptors namespace", $"`{interceptorsNamespace}`");

			if (!analysis.Options.EmitInterceptors)
			{
				sb
					.AppendLine()
					.AppendLine("> No interceptor source was emitted.");
			}

			sb
				.AppendLine()
				.AppendLine("## Generated Sources")
				.AppendLine()
				.AppendLine("| Source | Details |")
				.AppendLine("| --- | --- |");

			AppendGeneratedSourceRow(sb, analysis.Options, "AspectGeneratorOptionsAttribute.g.cs", "Generated options API.");

			if (analysis.Options.GenerateApi)
				AppendGeneratedSourceRow(sb, analysis.Options, "AspectAttribute.g.cs", "Generated aspect authoring and runtime API.");

			if (analysis.Options.EmitInterceptors && analysis.AspectedMethods.Length > 0)
				AppendGeneratedSourceRow(sb, analysis.Options, "Interceptors.g.cs", "Generated interceptor methods for marked calls.");

			var trace = analysis.Trace.IsEnabled ? analysis.Trace.ToMarkdown() : "";

			// The summary and the sources above are always complete; the two lists share what the size limit leaves, the
			// target methods up to half of it. The headings and the notes on omitted rows fit in the reserve.
			var remaining = analysis.Options.ReportSizeLimit <= 0
				? long.MaxValue
				: analysis.Options.ReportSizeLimit - Encoding.UTF8.GetByteCount(sb.ToString()) - Encoding.UTF8.GetByteCount(trace) - ListReserve;

			sb
				.AppendLine()
				.AppendLine("## Target Methods")
				.AppendLine();

			if (interceptors.Count == 0)
			{
				sb.AppendLine("No target methods were selected.");
			}
			else
			{
				sb
					.AppendLine("| Target method | Generated interceptor | Aspect(s) | Call sites |")
					.AppendLine("| --- | --- | --- | ---: |");

				var listed = AppendLimitedRows(
					sb,
					interceptors.Select(static interceptor => FormatTableRow(
						$"`{FormatMarkdownCode(interceptor.Method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))}`",
						$"`{interceptor.Name}`",
						FormatAspectList(interceptor.Attributes),
						interceptor.Invocations.Count.ToString(CultureInfo.InvariantCulture))),
					remaining == long.MaxValue ? long.MaxValue : remaining / 2,
					ref remaining);

				AppendOmittedNote(sb, analysis.Options, interceptors.Count - listed, interceptors.Count, "target methods");
			}

			sb
				.AppendLine()
				.AppendLine("## Intercepted Call Sites")
				.AppendLine();

			if (interceptors.Count == 0)
			{
				sb.AppendLine("No call sites were selected.");
			}
			else
			{
				sb
					.AppendLine("| # | Source | Replaced call | Target method | Aspect(s) | Generated interceptor |")
					.AppendLine("| ---: | --- | --- | --- | --- | --- |");

				var index  = 0;
				var listed = AppendLimitedRows(
					sb,
					interceptors.SelectMany(static interceptor => interceptor.Invocations
						.OrderBy(static i => GetLocationSortKey(i.Inv.GetLocation()), StringComparer.Ordinal)
						.Select(invocation => (Interceptor: interceptor, Invocation: invocation)))
						.Select(item => FormatTableRow(
							(++index).ToString(CultureInfo.InvariantCulture),
							FormatMarkdownLocation(item.Invocation.Inv.GetLocation()),
							$"`{FormatMarkdownCode(item.Invocation.Inv.ToString())}`",
							$"`{FormatMarkdownCode(item.Invocation.Method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))}`",
							FormatAspectList(item.Invocation.Attributes),
							$"`{item.Interceptor.Name}`")),
					remaining,
					ref remaining);

				var callSiteCount = interceptors.Sum(static interceptor => interceptor.Invocations.Count);

				AppendOmittedNote(sb, analysis.Options, callSiteCount - listed, callSiteCount, "call sites");
			}

			if (trace.Length > 0)
				sb.AppendLine().Append(trace);

			return sb.ToString();
		}

		/// <summary>
		/// Bytes kept free under the size limit for the list headings and the notes on omitted rows.
		/// </summary>
		const int ListReserve = 2048;

		/// <summary>
		/// Appends rows while their UTF-8 size fits in <paramref name="budget"/> and in <paramref name="remaining"/>, which it
		/// reduces by what it appends; the rows after the first one that does not fit are not formatted. Returns the number
		/// of rows appended.
		/// </summary>
		static int AppendLimitedRows(StringBuilder sb, IEnumerable<string> rows, long budget, ref long remaining)
		{
			var listed = 0;

			foreach (var row in rows)
			{
				var size = Encoding.UTF8.GetByteCount(row);

				if (size > budget || size > remaining)
					break;

				sb.Append(row);
				budget    -= size;
				remaining -= size;
				listed++;
			}

			return listed;
		}

		static void AppendOmittedNote(StringBuilder sb, GeneratorExecutionOptions options, int omitted, int total, string what)
		{
			if (omitted <= 0)
				return;

			var limit = (options.ReportSizeLimit / 1024).ToString("N0", CultureInfo.InvariantCulture);

			sb
				.AppendLine()
				.Append("> ")
				.Append(omitted.ToString("N0", CultureInfo.InvariantCulture))
				.Append(" of ")
				.Append(total.ToString("N0", CultureInfo.InvariantCulture))
				.Append(' ')
				.Append(what)
				.Append(" are not listed: the report is limited to ")
				.Append(limit)
				.Append(" KB. Set `AspectGeneratorReportSizeLimit` to `0` to list them all, or to a larger size in kilobytes.")
				.AppendLine();
		}

		static void AppendGeneratedSourceRow(StringBuilder sb, GeneratorExecutionOptions options, string hintName, string details)
		{
			var source = GetGeneratedSourcePath(options, hintName) is {} path
				? FormatMarkdownLink(hintName, path)
				: $"`{hintName}`";

			AppendTableRow(sb, source, details);

			static string? GetGeneratedSourcePath(GeneratorExecutionOptions options, string hintName)
			{
				if (string.IsNullOrWhiteSpace(options.CompilerGeneratedFilesOutputPath))
					return null;

				var path = options.CompilerGeneratedFilesOutputPath!;

				if (!Path.IsPathRooted(path) &&
					!string.IsNullOrWhiteSpace(options.ProjectDirectory))
					path = Path.Combine(options.ProjectDirectory!, path);

				return Path.GetFullPath(Path.Combine(path, "AspectGenerator", "AspectGenerator.AspectSourceGenerator", hintName));
			}
		}

		static void AppendTableRow(StringBuilder sb, params string?[] values)
		{
			sb.Append(FormatTableRow(values));
		}

		static string FormatTableRow(params string?[] values)
		{
			return new StringBuilder()
				.Append("| ")
				.Append(string.Join(" | ",
					values.Select(static value => (value ?? "")
						.Replace("\\", "\\\\")
						.Replace("|",  "\\|")
						.Replace("\r", "")
						.Replace("\n", "<br />"))))
				.AppendLine(" |")
				.ToString();
		}

		static string FormatAspectList(IEnumerable<AttributeInfo> attributes)
		{
			return string.Join("<br />", attributes.Select(attribute =>
			{
				var lifetime = AspectLifetimeResolver.Resolve(attribute);

				return $"`{FormatMarkdownCode(attribute.AttributeClass.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))}`" +
					$"<br />DeclaredLifetime: {lifetime.DeclaredLifetime}" +
					$"<br />EffectiveLifetime: {lifetime.EffectiveLifetime}";
			}));
		}

		static string FormatMarkdownCode(string value)
		{
			return value.Replace("`", "\\`");
		}

		static string FormatMarkdownLocation(Location location)
		{
			var span = location.GetLineSpan();

			if (!span.IsValid)
				return "`unknown`";

			var path   = span.Path;
			var line   = span.StartLinePosition.Line + 1;
			var column = span.StartLinePosition.Character + 1;
			var label  = $"{Path.GetFileName(path)}:{line.ToString(CultureInfo.InvariantCulture)}:{column.ToString(CultureInfo.InvariantCulture)}";

			return FormatMarkdownLink(label, path);
		}

		static string FormatMarkdownLink(string label, string path)
		{
			return $"[{EscapeMarkdownLinkText(label)}]({new Uri(Path.GetFullPath(path)).AbsoluteUri})";
		}

		static string EscapeMarkdownLinkText(string text)
		{
			return text.Replace("[", "\\[").Replace("]", "\\]");
		}

		static string GetLocationSortKey(Location location)
		{
			var span = location.GetLineSpan();

			if (!span.IsValid)
				return "";

			return string.Concat(
				span.Path,
				":",
				span.StartLinePosition.Line.ToString("D10", CultureInfo.InvariantCulture),
				":",
				span.StartLinePosition.Character.ToString("D10", CultureInfo.InvariantCulture));
		}

		static string GetInterceptorsNamespace(GeneratorExecutionOptions options)
		{
			return string.IsNullOrWhiteSpace(options.InterceptorsNamespace) ? "AspectGenerator" : options.InterceptorsNamespace!;
		}

	}
}
