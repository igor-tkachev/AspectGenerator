﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AspectGenerator
{
	[Generator(LanguageNames.CSharp)]
	public class AspectSourceGenerator : IIncrementalGenerator
	{
		const string AspectAttributeText =
			"""
			// <auto-generated/>
			#pragma warning disable
			#nullable enable

			using System;

			#if AG_GENERATE_API || !AG_NOT_GENERATE_API

			namespace AspectGenerator
			{
				/// <summary>
				/// <para>Defines an aspect.</para>
				/// <para>Create a new attribute decorated with this attribute to define an aspect.</para>
				/// </summary>
				[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
			#if AG_PUBLIC_API
				public
			#endif
				sealed class AspectAttribute : Attribute
				{
					public string?   OnInit            { get; set; }
					public string?   OnUsing           { get; set; }
					public string?   OnUsingAsync      { get; set; }
					public string?   OnBeforeCall      { get; set; }
					public string?   OnBeforeCallAsync { get; set; }
					public string?   OnCall            { get; set; }
					public string?   OnAfterCall       { get; set; }
					public string?   OnAfterCallAsync  { get; set; }
					public string?   OnCatch           { get; set; }
					public string?   OnCatchAsync      { get; set; }
					public string?   OnFinally         { get; set; }
					public string?   OnFinallyAsync    { get; set; }
					public string[]? InterceptMethods  { get; set; }
					public bool      UseInterceptType  { get; set; }
					public bool      PassArguments     { get; set; }
					public bool      UseInterceptData  { get; set; }
				}

			#if AG_PUBLIC_API
				public
			#endif
				enum InterceptType
				{
					OnInit,
					OnUsing,
					OnBeforeCall,
					OnAfterCall,
					OnCatch,
					OnFinally
				}

			#if AG_PUBLIC_API
				public
			#endif
				enum InterceptResult
				{
					Continue,
					Return,
					ReThrow     = Continue,
					IgnoreThrow = Return
				}

			#if AG_PUBLIC_API
				public
			#endif
				struct Void
				{
				}

			#if AG_PUBLIC_API
				public
			#endif
				partial class InterceptInfo
				{
					public object?         Tag;
					public InterceptType   InterceptType;
					public InterceptResult InterceptResult;
					public Exception?      Exception;

					public InterceptInfo?                                        PreviousInfo;
					public System.Reflection.MemberInfo                          MemberInfo;
					public object?[]?                                            MethodArguments;
					public Type                                                  AspectType;
					public System.Collections.Generic.Dictionary<string,object?> AspectArguments;
				}

			#if AG_PUBLIC_API
				public
			#endif
				partial class InterceptInfo<T> : InterceptInfo
				{
					public T ReturnValue;
				}

			#if AG_PUBLIC_API
				public
			#endif
				partial struct InterceptData<T>
				{
					public object?         Tag;
					public InterceptType   InterceptType;
					public InterceptResult InterceptResult;
					public Exception?      Exception;

					public InterceptInfo<T>?                                     PreviousInfo;
					public System.Reflection.MemberInfo                          MemberInfo;
					public object?[]?                                            MethodArguments;
					public Type                                                  AspectType;
					public System.Collections.Generic.Dictionary<string,object?> AspectArguments;

					public T ReturnValue;
				}
			}

			#endif

			#if AG_GENERATE_InterceptsLocationAttribute || !AG_NOT_GENERATE_InterceptsLocationAttribute

			namespace System.Runtime.CompilerServices
			{
				[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
				sealed class InterceptsLocationAttribute(string filePath, int line, int character) : Attribute
				{
				}
			}

			#endif

			""";

		class Options
		{
			public string? InterceptorsNamespace;
		}

		record AttributeInfo(AttributeData? AttributeData, INamedTypeSymbol AttributeClass)
		{
		}

		public void Initialize(IncrementalGeneratorInitializationContext context)
		{
#if DEBUG && TRUE1
			if (!System.Diagnostics.Debugger.IsAttached)
			{
				System.Diagnostics.Debugger.Launch();
			}
#endif

			context.RegisterPostInitializationOutput(ctx => ctx.AddSource("AspectAttribute.g.cs", SourceText.From(AspectAttributeText, Encoding.UTF8)));

			var attributes = context.SyntaxProvider
				.ForAttributeWithMetadataName("AspectGenerator.AspectAttribute",
					predicate: static (node, _) => node is ClassDeclarationSyntax,
					transform: static (attr, _) => attr)
				.Collect();

			var options = context.AnalyzerConfigOptionsProvider.Select((c, _) =>
				new Options
				{
					InterceptorsNamespace = c.GlobalOptions.TryGetValue("build_property.AspectGenerator_InterceptorsNamespace", out var ns) ? ns : null,
				});

			context.RegisterImplementationSourceOutput(context.CompilationProvider.Combine(options.Combine(attributes)), Implement);
		}

		static void Implement(SourceProductionContext spc, (Compilation Left, (Options Left, ImmutableArray<GeneratorAttributeSyntaxContext> Right) Right) data)
		{
			var compilation = data.Left;
			var attrs       = data.Right.Right;

			var aspectAttributes = attrs.Select(a => a.TargetSymbol).ToImmutableHashSet(SymbolEqualityComparer.Default);
			var aspectedMethods  = new List<(InvocationExpressionSyntax inv,IMethodSymbol method,List<AttributeInfo> attributes)>();
			var methodDic        = new Dictionary<IMethodSymbol,List<AttributeInfo>>(SymbolEqualityComparer.Default);
			var interceptedDic   = new Dictionary<string,List<AttributeInfo>>();

			foreach (var a in attrs)
			{
				if (a is { Attributes: [{ NamedArguments: [_, ..] args }, ..], TargetSymbol: INamedTypeSymbol ts })
				{
					foreach (var arg in args)
					{
						if (arg is { Key: "InterceptMethods", Value.Values: [_, ..] methods })
						{
							foreach (var m in methods)
							{
								if (m.Value is string s)
								{
									var ai = new AttributeInfo(null, ts);

									if (interceptedDic.ContainsKey(s))
										interceptedDic[s].Add(ai);
									else
										interceptedDic[s] = [ai];
								}
							}
						}
					}
				}
			}

			foreach (var tree in compilation.SyntaxTrees)
			{
				var semantic = compilation.GetSemanticModel(tree);

				// Scan the semantic tree...
				//
				foreach (var node in tree.GetRoot(spc.CancellationToken).DescendantNodes())
				{
					// .. to find an invocation...
					//
					if (node.IsKind(SyntaxKind.InvocationExpression))
					{
						var info = semantic.GetSymbolInfo(node, spc.CancellationToken);

						// .. of a method...
						//
						if (info.Symbol is IMethodSymbol method)
						{
							if (!methodDic.TryGetValue(method, out var attributes))
							{
								attributes = new();

								var methodAttributes = method.GetAttributes();

								// .. decorated with any Aspect attribute...
								//
								if (methodAttributes.Length > 0)
								{
									foreach (var ma in methodAttributes)
									{
										// .. if attribute is defined in the compiling assembly...
										//
										if (aspectAttributes.Contains(ma.AttributeClass!))
										{
											attributes.Add(new AttributeInfo(ma, ma.AttributeClass!));
										}
										// .. or somewhere else.
										else if (ma.AttributeClass?.GetAttributes().Any(aa => aa is { AttributeClass : { ContainingNamespace.Name : "AspectGenerator", Name : "AspectAttribute" }}) is true)
										{
											attributes.Add(new AttributeInfo(ma, ma.AttributeClass!));
										}
									}
								}

								if (interceptedDic.TryGetValue(method.ToString(), out var mm))
									attributes.AddRange(mm);

								methodDic[method] = attributes.Distinct().ToList();
							}

							if (attributes.Count > 0)
								aspectedMethods.Add(((InvocationExpressionSyntax)node, method, attributes));
						}
					}

					if (spc.CancellationToken.IsCancellationRequested)
						break;
				}

				if (spc.CancellationToken.IsCancellationRequested)
					break;
			}

			if (aspectedMethods.Count == 0 || spc.CancellationToken.IsCancellationRequested)
				return;

			GenerateSource(spc, data.Right.Left, aspectedMethods);
		}

		static void GenerateSource(SourceProductionContext spc, Options options, List<(InvocationExpressionSyntax inv,IMethodSymbol method,List<AttributeInfo> attributes)> aspectedMethods)
		{
			// Generate source. One file for all the interceptors.
			// Interceptors.g.cs
			//
			var sb = new StringBuilder(
				$$"""
				// <auto-generated/>
				#pragma warning disable
				#nullable enable

				using System;

				using SR  = System.Reflection;
				using SLE = System.Linq.Expressions;
				using SCG = System.Collections.Generic;

				namespace {{(string.IsNullOrWhiteSpace(options.InterceptorsNamespace) ? "AspectGenerator" : options.InterceptorsNamespace)}}
				{
					using AspectGenerator = AspectGenerator;

					static partial class Interceptors
					{
						static SR.MethodInfo GetMethodInfo(SLE.Expression expr)
						{
							return expr switch
							{
								SLE.MethodCallExpression mc => mc.Method,
								_                           => throw new InvalidOperationException()
							};
						}

						static SR.MethodInfo MethodOf<T>(SLE.Expression<Func<T>> func) => GetMethodInfo(func.Body);
						static SR.MethodInfo MethodOf   (SLE.Expression<Action>  func) => GetMethodInfo(func.Body);


				""");

			var nameCounter = 0;
			var nameSet     = new HashSet<string>();

			string GetInterceptorName(string methodName)
			{
				if (nameSet.Contains(methodName))
					return GetInterceptorName($"{methodName}_{++nameCounter}");

				nameSet.Add(methodName);
				return methodName;
			}

			foreach (var m in aspectedMethods.GroupBy(m => m.method, SymbolEqualityComparer.Default).OrderBy(m => m.Key!.Name))
			{
				var method          = (IMethodSymbol)m.Key!;
				var interceptorName = GetInterceptorName($"{method.Name}_Interceptor");
				var methods         = m.ToList();
				var attributes      = methods[0].attributes;

				if (attributes.Any(a => a.AttributeData?.NamedArguments.Any(na => na.Key == "Order") is true))
				{
					attributes =
						(
							from a in attributes
							let o = a.AttributeData?.NamedArguments.Select(na => (KeyValuePair<string,TypedConstant>?)na).FirstOrDefault(na => na!.Value.Key == "Order")
							let n = o is null ? int.MaxValue : o.Value.Value.Value switch
							{
								string s => int.TryParse(s, out var n) ? n : null,
								int    n => (int?)n,
								_        => null
							}
							orderby n
							select a
						)
						.ToList();
				}

				foreach (var p in method.Parameters)
				{
					switch (p.RefKind)
					{
						case RefKind.Ref :
						case RefKind.Out :
						case RefKind.In  :
							sb.AppendLine(
								$$"""
										static {{p.Type, -30}} {{interceptorName}}_Arg_{{p.Name, -13}} = default({{p.Type}});
								""");
							break;
					}
				}

				sb.AppendLine(
					$$"""
							static SR. MemberInfo                 {{interceptorName}}_MemberInfo        = MethodOf{{GetMethodOf(method, interceptorName)}};
					""");

				for (var i = 0; i < attributes.Count; i++)
				{
					var attr = attributes[i];

					sb
						.AppendLine(
							$$"""
									static SCG.Dictionary<string,object?> {{interceptorName}}_AspectArguments_{{i}} = new()
									{
							""")
						;

					if (attr.AttributeData is not null)
						foreach (var arg in attr.AttributeData.NamedArguments)
						{
							string value;

							if (arg.Value.Type is IArrayTypeSymbol arrayType)
							{
								value = arg.Value.Values.Length switch
								{
									0 => $"new {arrayType.ElementType}[0]",
									_ => $"new {arg.Value.Type} {{ {arg.Value.Values.Select(v => PrintValue(v.Value)).Aggregate((v1, v2) => $"{v1}, {v2}")} }}"
								};
							}
							else
								value = PrintValue(arg.Value.Value);

							sb
								.AppendLine(
									$$"""
												["{{arg.Key}}"] = {{value}},
									""")
								;

							static string PrintValue(object? val)
							{
								return val switch
								{
									null             => "null",
									string           => $"\"{val}\"",
									char             => $"'{val}'",
									double           => $"{val}d",
									float            => $"{val}f",
									long             => $"{val}l",
									INamedTypeSymbol => $"typeof({val})",
									_                => $"{val}"
								};
							}
						}

					sb
						.AppendLine(
							"""
									};
							""")
						;
				}

				sb.AppendLine(
					$$"""
							//
							/// <summary>
							/// Intercepts {{method}}.
							/// </summary>
							//
					""");

				foreach (var (inv, _, _) in methods)
				{

					sb.AppendLine(
						$"""
								// Intercepts {inv}.
						""");

					void AppendInterceptsLocation(FileLinePositionSpan span)
					{
						sb.AppendLine(
							$"""
									[System.Runtime.CompilerServices.InterceptsLocation(@"{span.Path}", line: {span.Span.Start.Line + 1}, character: {span.Span.Start.Character + 1})]
							""");
					}

					switch (inv.Expression)
					{
						case MemberAccessExpressionSyntax { Name       : var name } : AppendInterceptsLocation(name.GetLocation().GetLineSpan()); break;
						case IdentifierNameSyntax         { Identifier : var id   } : AppendInterceptsLocation(id.  GetLocation().GetLineSpan()); break;
						default :
							sb.AppendLine(
								$"""
										#error Unknown expression type {inv.Expression.GetType()} for : {inv.Expression}
								""");
							break;
					}

					sb.AppendLine(
						"""
								//
						""");
				}

				sb
					.Append(
						$"""
								[System.Runtime.CompilerServices.CompilerGenerated]
								//[System.Diagnostics.DebuggerStepThrough]
								public static{' '}
						""")
					;

				var methodModifierPosition = sb.Length;

				sb
					.AppendLine(
						$$"""
						{{PrintType(method.ReturnType)}} {{interceptorName}}({{PrintParameters(method)}})
								{
						""")
					;

				GenerateMethodBody(spc, sb, methods[0].inv, method, interceptorName, attributes, methodModifierPosition);

				sb
					.AppendLine(
						"""
								}
						""")
					.AppendLine();
			}

			TrimEnd(sb);

			sb
				.AppendLine(
					"""
						}
					}
					""");

			spc.AddSource("Interceptors.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
		}

		static void GenerateMethodBody(
			SourceProductionContext    spc,
			StringBuilder              sb,
			InvocationExpressionSyntax invocationExpression,
			IMethodSymbol              method,
			string                     interceptorName,
			List<AttributeInfo>        attributes,
			int                        methodModifierPosition)
		{
			var aspectAttrs   = attributes.Select(a => a.AttributeClass!.GetAttributes().First(aa => aa is { AttributeClass : { ContainingNamespace.Name : "AspectGenerator", Name : "AspectAttribute" }})).ToList();
			var isReturnsTask = method.ReturnType is { Name: "Task", ContainingNamespace: { Name : "Tasks", ContainingNamespace: { Name : "Threading", ContainingNamespace.Name : "System" }}};
			var taskType      = isReturnsTask && method.ReturnType is INamedTypeSymbol { IsGenericType : true, TypeArguments : [var argType] } ? argType : null;
			var generateAsync = isReturnsTask && aspectAttrs.Any(d => d.NamedArguments.Any(a => a.Key.EndsWith("Async")));
			var generateArgs  = aspectAttrs.Any(d => d.NamedArguments.Any(a => a.Key == "PassArguments"));

			// Generate arguments.
			//
			if (generateArgs)
			{
				sb.Append("\t\t\tvar __args__ = new object?[] { ");

				if (method.IsStatic == false)
					sb.Append("__this__, ");

				foreach (var p in method.Parameters)
					sb.Append(p.Name).Append(p.RefKind == RefKind.Out ? " = default, " : ", ");

				while (sb[^1] is ' ' or ',')
					sb.Length--;

				sb.Append(" };").AppendLine().AppendLine();
			}

			GenerateAttribute(0, "\t\t\t", false);

			void GenerateAttribute(int idx, string indent, bool isPrevData)
			{
				var attr   = attributes[idx].AttributeClass!;
				var aspect = aspectAttrs[idx];

				// Get aspect attribute parameters.
				//
				object? onInit           = null;
				object? onUsing          = null; object? onUsingAsync      = null;
				object? onBeforeCall     = null; object? onBeforeCallAsync = null;
				object? onCall           = null;
				object? onAfterCall      = null; object? onAfterCallAsync = null;
				object? onCatch          = null; object? onCatchAsync     = null;
				object? onFinally        = null; object? onFinallyAsync   = null;
				var     useInterceptType = false;
				var     passArguments    = false;
				var     useInterceptData = false;
				var     needInfo         = attributes.Count > 1 || generateAsync;

				foreach (var arg in aspect.NamedArguments)
					switch (arg.Key)
					{
						case "OnInit"            : onInit            = arg.Value.Value; needInfo = true; break;
						case "OnUsing"           : onUsing           = arg.Value.Value; needInfo = true; break;
						case "OnUsingAsync"      : onUsingAsync      = arg.Value.Value; needInfo = true; break;
						case "OnBeforeCall"      : onBeforeCall      = arg.Value.Value; needInfo = true; break;
						case "OnBeforeCallAsync" : onBeforeCallAsync = arg.Value.Value; needInfo = true; break;
						case "OnCall"            : onCall            = arg.Value.Value;                  break;
						case "OnAfterCall"       : onAfterCall       = arg.Value.Value; needInfo = true; break;
						case "OnAfterCallAsync"  : onAfterCallAsync  = arg.Value.Value; needInfo = true; break;
						case "OnCatch"           : onCatch           = arg.Value.Value; needInfo = true; break;
						case "OnCatchAsync"      : onCatchAsync      = arg.Value.Value; needInfo = true; break;
						case "OnFinally"         : onFinally         = arg.Value.Value; needInfo = true; break;
						case "OnFinallyAsync"    : onFinallyAsync    = arg.Value.Value; needInfo = true; break;
						case "UseInterceptType"  : useInterceptType  = arg.Value.Value is true; break;
						case "PassArguments"     : passArguments     = arg.Value.Value is true; break;
						case "UseInterceptData"  : useInterceptData  = arg.Value.Value is true; break;
					}

				if (generateAsync)
					sb.Insert(methodModifierPosition, "async ");

				var returnType = generateAsync ? taskType?.ToString() ?? "AspectGenerator.Void" : method.ReturnsVoid ? "AspectGenerator.Void" : $"{method.ReturnType}";

				// Generate AspectCallInfo.
				//
				if (needInfo)
				{
					sb
						.Append(indent).AppendLine($"// {(object?)attributes[idx].AttributeData ?? attributes[idx].AttributeClass}")
						.Append(indent).AppendLine("//")
						//.Append(indent).Append($"var __attr__{idx} = new {attributes[idx]}").Append(attributes[idx].NamedArguments.Length == 0 ? "()" : "").AppendLine(";")
						.Append(indent).AppendLine($"var __info__{idx} = new AspectGenerator.Intercept{(useInterceptData ? "Data" : "Info")}<{returnType}>")
						.Append(indent).AppendLine("{")
						//.Append(indent).AppendLine($"\tReturnValue     = {(idx > 0 ? $"__info__{idx - 1}.ReturnValue" : $"default({(method.ReturnsVoid ? "Void" : $"{method.ReturnType}")})")},")
						.Append(indent).AppendLine($"\tMemberInfo      = {interceptorName}_MemberInfo,")
						.Append(indent).AppendLine($"\tAspectType      = typeof({attr}),")
						.Append(indent).AppendLine($"\tAspectArguments = {interceptorName}_AspectArguments_{idx},")
						;

					if (passArguments)
						sb
							.Append(indent).AppendLine("\tMethodArguments = __args__,")
							;

					if (idx > 0 && !isPrevData)
						sb.Append(indent).AppendLine($"\tPreviousInfo    = __info__{idx - 1}");

					sb
						.Append(indent).AppendLine("};")
						.AppendLine()
						;
				}

				// Generate OnInit.
				//
				if (onInit is not null)
				{
					GenerateInterceptType("", "OnInit")
						.Append(indent).AppendLine($"__info__{idx} = {attr.ContainingNamespace}.{attr.Name}.{onInit}({PassInfo(idx)});")
						.AppendLine()
						;
				}

				// Generate OnUsing.
				//
				if (onUsing is not null)
				{
					var isAsync = generateAsync && onUsingAsync != null;
					GenerateInterceptType("", "OnUsing")
						.Append(indent).AppendLine($"{(isAsync ? "await " : "")}using ({attr.ContainingNamespace}.{attr.Name}.{(isAsync ? onUsingAsync : onUsing)}({PassInfo(idx)}))")
						.Append(indent).AppendLine("{")
						;
					indent += '\t';
				}

				// If OnCatch or OnFinally is defined, generate try/catch/finally block.
				//
				if (onCatch is not null || onFinally is not null)
				{
					sb.Append(indent).AppendLine("try");
					sb.Append(indent).AppendLine("{");
					indent += '\t';
				}

				// Generate OnBeforeCall.
				//
				if (generateAsync && onBeforeCallAsync is not null || onBeforeCall is not null)
				{
					GenerateInterceptType("", "OnBeforeCall");
					GenerateMethodCall(onBeforeCallAsync, onBeforeCall)
						.AppendLine()
						.Append(indent).AppendLine($"if (__info__{idx}.InterceptResult != AspectGenerator.InterceptResult.Return)")
						.Append(indent).AppendLine("{")
						;

					indent += '\t';
				}

				// Generate next attribute...
				//
				if (idx < attributes.Count - 1)
				{
					if (onCall is not null)
					{
						if (attributes[idx].AttributeData?.ApplicationSyntaxReference is {} asr)
						{
							spc.ReportDiagnostic(
								Diagnostic.Create(
									"AG0001",
									"AspectGenerator",
									$"Aspect '{attr}' specifies 'OnCall' interceptor and has to be the last one in aspect list.",
									DiagnosticSeverity.Error,
									DiagnosticSeverity.Error,
									true,
									0,
									location: Location.Create(asr.SyntaxTree, asr.Span)));
						}
						else
						{
							spc.ReportDiagnostic(
								Diagnostic.Create(
									"AG0002",
									"AspectGenerator",
									$"Method '{method}' is decorated with an aspect that specifies 'OnCall' interceptor. This aspect has to be the last one in aspect list.",
									DiagnosticSeverity.Error,
									DiagnosticSeverity.Error,
									true,
									0,
									location: Location.Create(invocationExpression.SyntaxTree, invocationExpression.Span)));
						}
					}

					sb.Append(indent).AppendLine("{");

					GenerateAttribute(idx + 1, indent + '\t', useInterceptData);

					TrimEnd(sb);

					sb
						.Append(indent).AppendLine("}")
						.AppendLine()
						;
				}
				// .. or call the target method.
				//
				else
				{
					sb
						.Append(indent)
						.Append(
							method.ReturnsVoid || isReturnsTask && method.ReturnType is INamedTypeSymbol { IsGenericType: false }
								? string.Empty
								: $"{(needInfo ? $"__info__{idx}.ReturnValue" : "var __return__")} = {(generateAsync ? "await " : "")}")
						.Append(
							onCall is null
								? method.IsExtensionMethod || method.IsStatic
									? method.OriginalDefinition.ContainingType
									: "__this__"
								: $"{attr.ContainingNamespace}.{attr.Name}")
						.Append('.')
						.Append(onCall ?? method.Name)
						.Append('(')
						;

					if (method.IsExtensionMethod || onCall is not null && method.IsStatic is false)
					{
						sb.Append("__this__");

						if (method.Parameters.Length > 0)
							sb.Append(", ");
					}

					foreach (var p in method.Parameters)
					{
						sb
							.Append(
								p.RefKind switch
								{
									RefKind.Ref => "ref ",
									RefKind.Out => "out ",
									RefKind.In  => "in ",
									_           => ""
								})
							.Append(p.Name)
							.Append(", ");
					}

					if (method.Parameters.Length > 0)
						sb.Length -= 2;

					sb
						.AppendLine(");")
						.AppendLine()
						;
				}

				// Generate OnAfterCall.
				//
				if (generateAsync && onAfterCallAsync is not null || onAfterCall is not null)
				{
					GenerateInterceptType("", "OnAfterCall");
					GenerateMethodCall(onAfterCallAsync, onAfterCall)
						.AppendLine();
				}

				// Generate OnBeforeCall end.
				//
				if (generateAsync && onBeforeCallAsync is not null || onBeforeCall is not null)
				{
					TrimEnd(sb);

					indent = indent[..^1];

					sb
						.Append(indent)
						.AppendLine("}")
						.AppendLine()
						;
				}

				// If OnCatch or OnFinally is defined, generate try/catch/finally block end.
				//
				if (generateAsync && onCatchAsync   is not null || onCatch   is not null ||
					generateAsync && onFinallyAsync is not null || onFinally is not null)
				{
					indent = indent[..^1];

					TrimEnd(sb);

					sb.AppendLine($"{indent}}}");

					// Generate OnCatch.
					//
					sb
						.Append(indent).AppendLine("catch (Exception __ex__)")
						.Append(indent).AppendLine("{")
						.Append(indent).AppendLine($"\t__info__{idx}.Exception{(onCatch is null ? null : "      ")} = __ex__;")
						;

					if (generateAsync && onCatchAsync is not null || onCatch is not null)
					{
						sb.Append(indent).AppendLine($"\t__info__{idx}.InterceptResult = AspectGenerator.InterceptResult.ReThrow;");

						GenerateInterceptType("\t", "OnCatch")
							.AppendLine()
							.Append('\t')
							;

						GenerateMethodCall(onCatchAsync, onCatch)
							.AppendLine()
							.Append(indent).AppendLine($"\tif (__info__{idx}.InterceptResult == AspectGenerator.InterceptResult.ReThrow)")
							.Append(indent).AppendLine("\t\tthrow;")
							;
					}
					else
					{
						sb.Append(indent).AppendLine("\tthrow;");
					}

					sb.Append(indent).AppendLine("}");

					// Generate OnFinally.
					//
					if (generateAsync && onFinallyAsync is not null || onFinally is not null)
					{
						sb
							.Append(indent).AppendLine("finally")
							.Append(indent).AppendLine("{");

						GenerateInterceptType("\t", "OnFinally")
							.Append('\t')
							;

						GenerateMethodCall(onFinallyAsync, onFinally)
							.Append(indent).AppendLine("}")
							;
					}

					sb.AppendLine();
				}

				// Generate OnUsing.
				//
				if (onUsing is not null)
				{
					indent = indent[..^1];

					TrimEnd(sb);

					sb
						.Append(indent)
						.AppendLine("}")
						.AppendLine()
						;
				}

				// Generate return.
				//
				if (idx > 0)
					sb.Append(indent).AppendLine($"__info__{idx - 1}.ReturnValue = __info__{idx}.ReturnValue;");
				else if (!(method.ReturnsVoid || isReturnsTask && method.ReturnType is INamedTypeSymbol { IsGenericType: false }))
					sb.AppendLine($"\t\t\treturn {(needInfo ? $"__info__{idx}.ReturnValue" : "__return__")};");

				TrimEnd(sb);

				string PassInfo(int idx)
				{
					return $"{(useInterceptData ? "ref " : "")}__info__{idx}";
				}

				StringBuilder GenerateMethodCall(object? onAsync, object? call)
				{
					if (generateAsync && onAsync is not null)
						return sb.Append(indent).AppendLine($"await {attr.ContainingNamespace}.{attr.Name}.{onAsync}({PassInfo(idx)});");
					return sb.Append(indent).AppendLine($"{attr.ContainingNamespace}.{attr.Name}.{call}({PassInfo(idx)});");
				}

				StringBuilder GenerateInterceptType(string additionalIndent, string interceptType)
				{
					if (useInterceptType)
					{
						sb
							.Append(additionalIndent)
							.Append(indent)
							.AppendLine($"__info__{idx}.InterceptType = AspectGenerator.InterceptType.{interceptType};");
					}

					return sb;
				}
			}
		}

		static void TrimEnd(StringBuilder sb)
		{
			while (sb[^1] is '\r' or '\n')
				sb.Length--;

			sb.AppendLine();
		}

		static string PrintType(ITypeSymbol type)
		{
			return $"{type}";
		}

		static string PrintParameters(IMethodSymbol method)
		{
			var parameters = method.Parameters;
			var sb         = new StringBuilder();

			if (method.IsStatic == false)
				sb.Append($"this {method.ReceiverType} __this__, ");

			foreach (var p in parameters)
				sb
					.Append(p)
					.Append(", ")
					;

			if (parameters.Length != 0 || method.IsStatic == false)
				sb.Length -= 2;

			return sb.ToString();
		}

		static string GetMethodOf(IMethodSymbol method, string interceptorName)
		{
			var sb = new StringBuilder();

			sb.Append("(() => ");

			sb
				.Append(method.IsExtensionMethod || method.IsStatic ? method.OriginalDefinition.ContainingType : $"default({method.ReceiverType})")
				.Append('.')
				.Append(method.Name)
				.Append('(')
				;

			if (method.IsExtensionMethod)
				sb.Append($"default({method.ReceiverType}), ");

			foreach (var p in method.Parameters)
				switch (p.RefKind)
				{
					case RefKind.Ref : sb.Append($"ref {interceptorName}_Arg_{p.Name}, "); break;
					case RefKind.In  : sb.Append($"in {interceptorName}_Arg_{p.Name}, ");  break;
					case RefKind.Out : sb.Append($"out {interceptorName}_Arg_{p.Name}, "); break;
					default          : sb.Append($"default({p.Type}), ");                  break;
				}

			while (sb[^1] is ',' or ' ')
				sb.Length--;

			sb.Append("))");

			return sb.ToString();
		}
	}
}
