using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;

namespace AspectGenerator
{
	readonly record struct ReportInterceptorInfo(
		IMethodSymbol                      Method,
		ImmutableArray<AnalyzedInvocation> Invocations,
		string                             Name,
		ImmutableArray<AttributeInfo>      Attributes);

	static class AspectBuildReport
	{
		#pragma warning disable RS1035 // Build reports are an explicit source-generator output channel for this package.

		public static void Write(AspectSourceGenerator.AnalysisResult analysis)
		{
			if (analysis.Options.DesignTimeBuild ||
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
			var interceptors          = GetInterceptorInfos(analysis.AspectedMethods);
			var sb                    = new StringBuilder();

			sb
				.AppendLine("# AspectGenerator Build Report")
				.AppendLine()
				.AppendLine("## Summary")
				.AppendLine()
				.AppendLine("| Item | Value |")
				.AppendLine("| --- | --- |");

			AppendTableRow(sb, "Project", analysis.Compilation.AssemblyName ?? analysis.Compilation.Assembly.Identity.Name);
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

			sb
				.AppendLine()
				.AppendLine("## Target Methods")
				.AppendLine();

			if (interceptors.Length == 0)
			{
				sb.AppendLine("No target methods were selected.");
			}
			else
			{
				sb
					.AppendLine("| Target method | Generated interceptor | Aspect(s) | Call sites |")
					.AppendLine("| --- | --- | --- | ---: |");

				foreach (var interceptor in interceptors)
				{
					AppendTableRow(
						sb,
						$"`{FormatMarkdownCode(interceptor.Method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))}`",
						$"`{interceptor.Name}`",
						FormatAspectList(interceptor.Attributes),
						interceptor.Invocations.Length.ToString(CultureInfo.InvariantCulture));
				}
			}

			sb
				.AppendLine()
				.AppendLine("## Intercepted Call Sites")
				.AppendLine();

			if (interceptors.Length == 0)
			{
				sb.AppendLine("No call sites were selected.");
			}
			else
			{
				sb
					.AppendLine("| # | Source | Replaced call | Target method | Aspect(s) | Generated interceptor |")
					.AppendLine("| ---: | --- | --- | --- | --- | --- |");

				var index = 0;

				foreach (var interceptor in interceptors)
				{
					foreach (var invocation in interceptor.Invocations.OrderBy(static i => GetLocationSortKey(i.Inv.GetLocation()), StringComparer.Ordinal))
					{
						AppendTableRow(
							sb,
							(++index).ToString(CultureInfo.InvariantCulture),
							FormatMarkdownLocation(invocation.Inv.GetLocation()),
							$"`{FormatMarkdownCode(invocation.Inv.ToString())}`",
							$"`{FormatMarkdownCode(invocation.Method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))}`",
							FormatAspectList(invocation.Attributes),
							$"`{interceptor.Name}`");
					}
				}
			}

			return sb.ToString();
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

		static string FormatAspectList(IEnumerable<AttributeInfo> attributes)
		{
			return string.Join("<br />", attributes.Select(attribute =>
			{
				var declaredLifetime = GetDeclaredAspectLifetime(attribute);

				return $"`{FormatMarkdownCode(attribute.AttributeClass.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))}`" +
					$"<br />DeclaredLifetime: {declaredLifetime}" +
					$"<br />EffectiveLifetime: {declaredLifetime switch { "Instance" => "Instance", _ => "Static" }}";
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

		static string GetDeclaredAspectLifetime(AttributeInfo attribute)
		{
			if (attribute.AspectDefinitionData is {} aspectDefinition)
			{
				foreach (var option in aspectDefinition.NamedArguments)
				{
					if (option.Key == "Lifetime")
						return GetDeclaredAspectLifetime(option.Value.Value);
				}
			}

			if (attribute is { AspectDefinitionSyntax: {} syntax, AspectDefinitionSemanticModel: {} semanticModel })
			{
				foreach (var arg in syntax.ArgumentList?.Arguments ?? default)
				{
					if (arg.NameEquals?.Name.Identifier.ValueText != "Lifetime")
						continue;

					return GetDeclaredAspectLifetime(
						AspectSymbols.GetAttributeArgumentValue(arg.Expression, semanticModel) ??
						arg.Expression.ToString().Split('.').LastOrDefault());
				}
			}

			return "Auto";
		}

		static string GetDeclaredAspectLifetime(object? value)
		{
			return value switch
			{
				1                => "Static",
				2                => "Instance",
				"Static"         => "Static",
				"Instance"       => "Instance",
				"Auto"           => "Auto",
				string text when text.EndsWith(".Static",   StringComparison.Ordinal) => "Static",
				string text when text.EndsWith(".Instance", StringComparison.Ordinal) => "Instance",
				string text when text.EndsWith(".Auto",     StringComparison.Ordinal) => "Auto",
				_                => "Auto"
			};
		}

		static ImmutableArray<ReportInterceptorInfo> GetInterceptorInfos(ImmutableArray<AnalyzedInvocation> aspectedMethods)
		{
			var builder = ImmutableArray.CreateBuilder<ReportInterceptorInfo>();
			var names   = new HashSet<string>();

			foreach (var group in aspectedMethods.GroupBy(static m => m.Method, SymbolEqualityComparer.Default).OrderBy(static m => m.Key!.Name))
			{
				var method = (IMethodSymbol)group.Key!;
				var name   = method.Name + "_Interceptor";

				if (!names.Add(name))
				{
					for (var index = 1;; index++)
					{
						name = method.Name + "_Interceptor_" + index;

						if (names.Add(name))
							break;
					}
				}

				builder.Add(new ReportInterceptorInfo(method, [..group], name, [..group.First().Attributes]));
			}

			return builder.ToImmutable();
		}
	}
}
