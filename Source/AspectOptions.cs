using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AspectGenerator
{
	static class AspectOptionsResolver
	{
		public static Options GetMSBuildOptions(AnalyzerConfigOptionsProvider optionsProvider)
		{
			var options = optionsProvider.GlobalOptions;
			var aspectDiagnosticSeverity = GetAspectDiagnosticSeverityProperty(options, $"build_property.AspectGenerator{AspectOptionName.AspectDiagnosticSeverity}");

			return new Options
			{
				GenerateApi                          = GetBoolProperty(options, $"build_property.AspectGenerator{AspectOptionName.GenerateApi}"),
				DesignTimeBuild                      = GetBoolProperty(options,  "build_property.DesignTimeBuild"),
				PublicApi                            = GetBoolProperty(options, $"build_property.AspectGenerator{AspectOptionName.PublicApi}"),
				DebuggerStepThrough                  = GetBoolProperty(options, $"build_property.AspectGenerator{AspectOptionName.DebuggerStepThrough}"),
				ReportFile                           = options.TryGetValue($"build_property.AspectGenerator{AspectOptionName.ReportFile}",            out var reportFile) ? reportFile : null,
				AspectDiagnosticSeverity             = aspectDiagnosticSeverity.Value,
				InvalidAspectDiagnosticSeverityValue = aspectDiagnosticSeverity.InvalidValue,
				ProjectDirectory                     = options.TryGetValue("build_property.ProjectDir",                                               out var projectDir) ? projectDir : null,
				CompilerGeneratedFilesOutputPath     = options.TryGetValue("build_property.CompilerGeneratedFilesOutputPath",                         out var generatedFilesPath) ? generatedFilesPath : null,
				InterceptorsNamespace                = options.TryGetValue($"build_property.AspectGenerator{AspectOptionName.InterceptorsNamespace}", out var ns) ? ns : null,
				InterceptorsNamespaces               = options.TryGetValue("build_property.InterceptorsNamespaces",                                   out var namespaces) ? namespaces : null,
			};
		}

		public static GeneratorExecutionOptions Resolve(Compilation compilation, AnalyzerConfigOptionsProvider optionsProvider, IAspectDiagnosticSink diagnostics)
		{
			return Resolve(compilation, GetMSBuildOptions(optionsProvider), diagnostics);
		}

		public static GeneratorExecutionOptions Resolve(Compilation compilation, Options msBuildOptions, IAspectDiagnosticSink diagnostics)
		{
			var result = new Options
			{
				GenerateApi            = msBuildOptions.GenerateApi,
				DesignTimeBuild        = msBuildOptions.DesignTimeBuild,
				PublicApi              = msBuildOptions.PublicApi,
				DebuggerStepThrough    = msBuildOptions.DebuggerStepThrough,
				ReportFile             = msBuildOptions.ReportFile,
				AspectDiagnosticSeverity = msBuildOptions.AspectDiagnosticSeverity,
				InvalidAspectDiagnosticSeverityValue = msBuildOptions.InvalidAspectDiagnosticSeverityValue,
				ProjectDirectory       = msBuildOptions.ProjectDirectory,
				CompilerGeneratedFilesOutputPath = msBuildOptions.CompilerGeneratedFilesOutputPath,
				InterceptorsNamespace  = msBuildOptions.InterceptorsNamespace,
				InterceptorsNamespaces = msBuildOptions.InterceptorsNamespaces,
			};

			var attr = compilation.Assembly.GetAttributes().FirstOrDefault(a =>
				a.AttributeClass is { ContainingNamespace.Name: "AspectGenerator", Name: "AspectGeneratorOptionsAttribute" });

			if (attr is not null)
			{
				foreach (var arg in attr.NamedArguments)
				{
					switch (arg.Key)
					{
						case AspectOptionName.GenerateApi           when arg.Value.Value is bool   generateApi          : result.GenerateApi           = generateApi;           break;
						case AspectOptionName.PublicApi             when arg.Value.Value is bool   publicApi            : result.PublicApi             = publicApi;             break;
						case AspectOptionName.DebuggerStepThrough   when arg.Value.Value is bool   debuggerStepThrough  : result.DebuggerStepThrough   = debuggerStepThrough;   break;
						case AspectOptionName.AspectDiagnosticSeverity:
							if (TryConvertAspectDiagnosticSeverity(arg.Value.Value, out var aspectDiagnosticSeverity))
							{
								result.AspectDiagnosticSeverity = aspectDiagnosticSeverity;
								result.InvalidAspectDiagnosticSeverityValue = null;
							}
							break;
						case AspectOptionName.InterceptorsNamespace when arg.Value.Value is string interceptorsNamespace: result.InterceptorsNamespace = interceptorsNamespace; break;
					}
				}
			}

			if (result.InvalidAspectDiagnosticSeverityValue is not null)
				ReportInvalidAspectDiagnosticSeverity(diagnostics, result.InvalidAspectDiagnosticSeverityValue, Location.None);

			ApplyAssemblyOptionsFromSyntax(compilation, result, diagnostics);

			return CreateExecutionOptions(result);
		}

		static bool? GetBoolProperty(AnalyzerConfigOptions options, string name)
		{
			if (!options.TryGetValue(name, out var value))
				return null;

			return bool.TryParse(value, out var result) ? result : null;
		}

		static AspectDiagnosticSeverityOption GetAspectDiagnosticSeverityProperty(AnalyzerConfigOptions options, string name)
		{
			if (!options.TryGetValue(name, out var value))
				return default;

			if (string.IsNullOrWhiteSpace(value))
				return default;

			return TryParseAspectDiagnosticSeverity(value, out var result)
				? new AspectDiagnosticSeverityOption(result, null)
				: new AspectDiagnosticSeverityOption(null, value);
		}

		static GeneratorExecutionOptions CreateExecutionOptions(Options options)
		{
			var emitInterceptors = options.DesignTimeBuild is not true;

			return new GeneratorExecutionOptions(
				options.GenerateApi is not false,
				emitInterceptors,
				options.DesignTimeBuild is true,
				options.PublicApi is true,
				options.DebuggerStepThrough is true,
				options.ReportFile,
				options.AspectDiagnosticSeverity ?? AspectDiagnosticSeverity.Info,
				options.ProjectDirectory,
				options.CompilerGeneratedFilesOutputPath,
				options.InterceptorsNamespace,
				options.InterceptorsNamespaces);
		}

		static void ApplyAssemblyOptionsFromSyntax(Compilation compilation, Options options, IAspectDiagnosticSink diagnostics)
		{
			foreach (var tree in compilation.SyntaxTrees)
			{
				if (tree.GetRoot() is not CompilationUnitSyntax root)
					continue;

				foreach (var attribute in root.AttributeLists
					.Where(static list => list.Target?.Identifier.IsKind(SyntaxKind.AssemblyKeyword) == true)
					.SelectMany(static list => list.Attributes))
				{
					if (!AspectSymbols.IsAspectGeneratorOptionsAttributeName(attribute.Name.ToString()))
						continue;

					foreach (var argument in attribute.ArgumentList?.Arguments ?? default)
					{
						var name = argument.NameEquals?.Name.Identifier.ValueText;

						if (name is null)
							continue;

						switch (name)
						{
							case AspectOptionName.GenerateApi           when TryGetBoolLiteral(argument.Expression, out var generateApi)          : options.GenerateApi           = generateApi;           break;
							case AspectOptionName.PublicApi             when TryGetBoolLiteral(argument.Expression, out var publicApi)            : options.PublicApi             = publicApi;             break;
							case AspectOptionName.DebuggerStepThrough   when TryGetBoolLiteral(argument.Expression, out var debuggerStepThrough)  : options.DebuggerStepThrough   = debuggerStepThrough;   break;
							case AspectOptionName.AspectDiagnosticSeverity:
								if (TryGetAspectDiagnosticSeverity(argument.Expression, out var aspectDiagnosticSeverity))
									options.AspectDiagnosticSeverity = aspectDiagnosticSeverity;
								else
								{
									ReportInvalidAspectDiagnosticSeverity(diagnostics, argument.Expression.ToString(), argument.Expression.GetLocation());
									options.AspectDiagnosticSeverity = AspectDiagnosticSeverity.Info;
								}
								break;
							case AspectOptionName.InterceptorsNamespace when TryGetStringLiteral(argument.Expression, out var interceptorsNamespace): options.InterceptorsNamespace = interceptorsNamespace; break;
						}
					}
				}
			}
		}

		static void ReportInvalidAspectDiagnosticSeverity(IAspectDiagnosticSink diagnostics, string value, Location? location)
		{
			diagnostics.Report(
				AspectDiagnosticID.InvalidAspectDiagnosticSeverity,
				$"Invalid AspectGenerator diagnostic severity value '{value}'. Supported values are Off, Hidden, Info, Warning, and Error. Check AspectGeneratorAspectDiagnosticSeverity in the project file, Directory.Build.props, imported .props files, or the AspectGeneratorOptions assembly attribute.",
				location,
				DiagnosticSeverity.Warning);
		}

		static bool TryGetBoolLiteral(ExpressionSyntax expression, out bool value)
		{
			if (expression.IsKind(SyntaxKind.TrueLiteralExpression))
			{
				value = true;
				return true;
			}

			if (expression.IsKind(SyntaxKind.FalseLiteralExpression))
			{
				value = false;
				return true;
			}

			value = false;
			return false;
		}

		static bool TryGetStringLiteral(ExpressionSyntax expression, out string? value)
		{
			if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
			{
				value = literal.Token.ValueText;
				return true;
			}

			value = null;
			return false;
		}

		static bool TryConvertAspectDiagnosticSeverity(object? value, out AspectDiagnosticSeverity severity)
		{
			if (value is int intValue)
				return TryConvertAspectDiagnosticSeverity(intValue, out severity);

			if (value is short shortValue)
				return TryConvertAspectDiagnosticSeverity(shortValue, out severity);

			if (value is sbyte sbyteValue)
				return TryConvertAspectDiagnosticSeverity(sbyteValue, out severity);

			if (value is byte byteValue)
				return TryConvertAspectDiagnosticSeverity(byteValue, out severity);

			if (value is string stringValue)
				return TryParseAspectDiagnosticSeverity(stringValue, out severity);

			severity = default;
			return false;
		}

		static bool TryConvertAspectDiagnosticSeverity(int value, out AspectDiagnosticSeverity severity)
		{
			if (value is >= -1 and <= 3)
			{
				severity = (AspectDiagnosticSeverity)value;
				return true;
			}

			severity = default;
			return false;
		}

		static bool TryParseAspectDiagnosticSeverity(string value, out AspectDiagnosticSeverity severity)
		{
			if (System.Enum.TryParse(value, ignoreCase: true, out severity) &&
				severity is >= AspectDiagnosticSeverity.Off and <= AspectDiagnosticSeverity.Error)
				return true;

			if (int.TryParse(value, out var intValue))
				return TryConvertAspectDiagnosticSeverity(intValue, out severity);

			severity = default;
			return false;
		}

		static bool TryGetAspectDiagnosticSeverity(ExpressionSyntax expression, out AspectDiagnosticSeverity severity)
		{
			if (expression is MemberAccessExpressionSyntax memberAccess)
				return TryParseAspectDiagnosticSeverity(memberAccess.Name.Identifier.ValueText, out severity);

			if (expression is QualifiedNameSyntax qualifiedName)
				return TryParseAspectDiagnosticSeverity(qualifiedName.Right.Identifier.ValueText, out severity);

			if (expression is IdentifierNameSyntax identifier)
				return TryParseAspectDiagnosticSeverity(identifier.Identifier.ValueText, out severity);

			if (expression is LiteralExpressionSyntax literal)
			{
				if (literal.IsKind(SyntaxKind.NumericLiteralExpression) &&
					literal.Token.Value is int intValue)
					return TryConvertAspectDiagnosticSeverity(intValue, out severity);

				if (literal.IsKind(SyntaxKind.StringLiteralExpression))
					return TryParseAspectDiagnosticSeverity(literal.Token.ValueText, out severity);
			}

			if (expression is PrefixUnaryExpressionSyntax { OperatorToken.RawKind: (int)SyntaxKind.MinusToken } prefix &&
				prefix.Operand is LiteralExpressionSyntax { Token.Value: int operand })
				return TryConvertAspectDiagnosticSeverity(-operand, out severity);

			severity = default;
			return false;
		}

		readonly struct AspectDiagnosticSeverityOption
		{
			public AspectDiagnosticSeverityOption(AspectDiagnosticSeverity? value, string? invalidValue)
			{
				Value        = value;
				InvalidValue = invalidValue;
			}

			public AspectDiagnosticSeverity? Value        { get; }
			public string?                   InvalidValue { get; }
		}
	}
}
