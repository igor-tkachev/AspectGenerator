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

			namespace AspectGenerator
			{
				[Aspect]
				[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
				sealed class AspectAttribute : Attribute
				{
					public string? OnInit        { get; set; }
					public string? OnUsing       { get; set; }
					public string? OnAsyncUsing  { get; set; }
					public string? OnBeforeCall  { get; set; }
					public string? OnAfterCall   { get; set; }
					public string? OnCatch       { get; set; }
					public string? OnFinally     { get; set; }
					public bool    PassArguments { get; set; }
				}

				enum InterceptType
				{
					OnInit,
					OnBeforeCall,
					OnAfterCall,
					OnCatch,
					OnFinally
				}

				enum InterceptResult
				{
					Continue,
					Return,
					ReThrow     = Continue,
					IgnoreThrow = Return
				}

				struct Void
				{
				}

				abstract class InterceptInfo
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

				class InterceptInfo<T> : InterceptInfo
				{
					public T ReturnValue;
				}
			}

			namespace System.Runtime.CompilerServices
			{
				[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
				sealed class InterceptsLocationAttribute(string filePath, int line, int character) : Attribute
				{
				}
			}

			""";

		class Options
		{
			public string? InterceptorsNamespace;
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

			if (attrs.Length == 0)
				return;

			var aspectAttributes = attrs.Select(a => a.TargetSymbol).ToImmutableHashSet(SymbolEqualityComparer.Default);
			var aspectedMethods  = new List<(InvocationExpressionSyntax inv,IMethodSymbol method,ImmutableArray<AttributeData> attributes)>();

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
							var methodAttributes = method.GetAttributes();

							// .. decorated with any Aspect attribute.
							//
							if (methodAttributes.Any(a => aspectAttributes.Contains(a.AttributeClass!)))
								aspectedMethods.Add(((InvocationExpressionSyntax)node, method, methodAttributes));
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

		static void GenerateSource(SourceProductionContext spc, Options options, List<(InvocationExpressionSyntax inv,IMethodSymbol method,ImmutableArray<AttributeData> attributes)> aspectedMethods)
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

				if (attributes.Any(a => a.NamedArguments.Any(na => na.Key == "Order")))
				{
					attributes =
						(
							from a in attributes
							let o = a.NamedArguments.Select(na => (KeyValuePair<string,TypedConstant>?)na).FirstOrDefault(na => na!.Value.Key == "Order")
							let n = o is null ? int.MaxValue : o.Value.Value.Value switch
							{
								string s => int.TryParse(s, out var n) ? n : null,
								int    n => (int?)n,
								_        => null
							}
							orderby n
							select a
						)
						.ToImmutableArray();
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

				for (var i = 0; i < attributes.Length; i++)
				{
					var attr = attributes[i];

					sb
						.AppendLine(
							$$"""
									static SCG.Dictionary<string,object?> {{interceptorName}}_AspectArguments_{{i}} = new()
									{
							""")
						;

					foreach (var arg in attr.NamedArguments)
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

				GenerateMethodBody(sb, method, interceptorName, attributes, methodModifierPosition);

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

		static void GenerateMethodBody(StringBuilder sb, IMethodSymbol method, string interceptorName, ImmutableArray<AttributeData> attributes, int methodModifierPosition)
		{
			var aspectAttrs   = attributes.Select(a => a.AttributeClass!.GetAttributes().First(aa => aa is { AttributeClass : { ContainingNamespace.Name : "AspectGenerator", Name : "AspectAttribute" }})!).ToList();
			var isReturnsTask = method.ReturnType is { Name: "Task", ContainingNamespace: { Name : "Tasks", ContainingNamespace: { Name : "Threading", ContainingNamespace.Name : "System" }}};
			var taskType      = isReturnsTask && method.ReturnType is INamedTypeSymbol { IsGenericType : true, TypeArguments : [var argType] } ? argType : null;
			var generateAsync = isReturnsTask && aspectAttrs.Any(d => d.NamedArguments.Any(a => a.Key == "OnAsyncUsing"));
			var generateArgs  = aspectAttrs.Any(d => d.NamedArguments.Any(a => a.Key == "PassArguments"));

			// Generate arguments.
			//
			if (generateArgs)
			{
				sb
					.Append("\t\t\tvar __args__ = new object?[] { ")
					;

				if (method.IsStatic == false)
					sb.Append("__this__, ");

				foreach (var p in method.Parameters)
				{
					if (p.RefKind == RefKind.Out)
						sb.Append(p.Name).Append(" = default, ");
					else
						sb.Append(p.Name).Append(", ");
				}

				while (sb[^1] is ' ' or ',')
					sb.Length--;

				sb.Append(" };").AppendLine().AppendLine();
			}

			GenerateAttribute(0, "\t\t\t");

			void GenerateAttribute(int idx, string indent)
			{
				var attr = attributes[idx].AttributeClass!;

				// Get aspect attribute parameters.
				//
				object? onInit        = null;
				object? onUsing       = null;
				object? onAsyncUsing  = null;
				object? onBeforeCall  = null;
				object? onAfterCall   = null;
				object? onCatch       = null;
				object? onFinally     = null;
				var     passArguments = false;

				foreach (var arg in aspectAttrs[idx].NamedArguments)
					switch (arg.Key)
					{
						case "OnInit"        : onInit        = arg.Value.Value; break;
						case "OnUsing"       : onUsing       = arg.Value.Value; break;
						case "OnAsyncUsing"  : onAsyncUsing  = arg.Value.Value; break;
						case "OnBeforeCall"  : onBeforeCall  = arg.Value.Value; break;
						case "OnAfterCall"   : onAfterCall   = arg.Value.Value; break;
						case "OnCatch"       : onCatch       = arg.Value.Value; break;
						case "OnFinally"     : onFinally     = arg.Value.Value; break;
						case "PassArguments" : passArguments = arg.Value.Value is true; break;
					}

				if (generateAsync)
				{
					sb.Insert(methodModifierPosition, "async ");
				}

				var returnType = generateAsync ? taskType?.ToString() ?? "AspectGenerator.Void" : method.ReturnsVoid ? "AspectGenerator.Void" : $"{method.ReturnType}";

				// Generate AspectCallInfo.
				//
				sb
					.Append(indent).AppendLine($"// {attributes[idx]}")
					.Append(indent).AppendLine("//")
					//.Append(indent).Append($"var __attr__{idx} = new {attributes[idx]}").Append(attributes[idx].NamedArguments.Length == 0 ? "()" : "").AppendLine(";")
					.Append(indent).AppendLine($"var __info__{idx} = new AspectGenerator.InterceptInfo<{returnType}>")
					.Append(indent).AppendLine("{")
					//.Append(indent).AppendLine($"\tReturnValue     = {(idx > 0 ? $"__info__{idx - 1}.ReturnValue" : $"default({(method.ReturnsVoid ? "Void" : $"{method.ReturnType}")})")},")
					.Append(indent).AppendLine($"\tMemberInfo      = {interceptorName}_MemberInfo,")
					.Append(indent).AppendLine($"\tAspectType      = typeof({attr}),")
					.Append(indent).AppendLine($"\tAspectArguments = {interceptorName}_AspectArguments_{idx},")
					;

				if (passArguments)
					sb
						.Append(indent).AppendLine($"\tMethodArguments = __args__,")
						;

				if (idx > 0)
					sb.Append(indent).AppendLine($"\tPreviousInfo    = __info__{idx - 1}");

				sb
					.Append(indent).AppendLine("};")
					.AppendLine()
					;

				// Generate OnInit.
				//
				if (onInit is not null)
					sb
						.Append(indent).AppendLine($"__info__{idx}.InterceptType = AspectGenerator.InterceptType.OnInit;")
						.Append(indent).AppendLine($"__info__{idx} = {attr.ContainingNamespace}.{attr.Name}.{onInit}(__info__{idx});")
						.AppendLine()
						;

				// Generate OnUsing.
				//
				if (onUsing is not null)
				{
					var isAsync = generateAsync && onAsyncUsing != null;
					sb
						.Append(indent).AppendLine($"{(isAsync ? "await " : "")}using ({attr.ContainingNamespace}.{attr.Name}.{(isAsync ? onAsyncUsing : onUsing)}(__info__{idx}))")
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
				if (onBeforeCall is not null)
				{
					sb
						.Append(indent).AppendLine($"__info__{idx}.InterceptType = AspectGenerator.InterceptType.OnBeforeCall;")
						.Append(indent).AppendLine($"{attr.ContainingNamespace}.{attr.Name}.{onBeforeCall}(__info__{idx});")
						.AppendLine()
						.Append(indent).AppendLine($"if (__info__{idx}.InterceptResult != AspectGenerator.InterceptResult.Return)")
						.Append(indent).AppendLine("{")
						;

					indent += '\t';
				}

				// Generate next attribute...
				//
				if (idx < attributes.Length - 1)
				{
					sb.Append(indent).AppendLine("{");

					GenerateAttribute(idx + 1, indent + '\t');

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
						.Append(method.ReturnsVoid ? string.Empty : $"__info__{idx}.ReturnValue = {(generateAsync ? "await " : "")}")
						.Append(method.IsExtensionMethod || method.IsStatic? method.OriginalDefinition.ContainingType : "__this__")
						.Append('.')
						.Append(method.Name)
						.Append('(')
						;

					if (method.IsExtensionMethod)
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
				if (onAfterCall is not null)
					sb
						.Append(indent).AppendLine($"__info__{idx}.InterceptType = AspectGenerator.InterceptType.OnAfterCall;")
						.Append(indent).AppendLine($"{attr.ContainingNamespace}.{attr.Name}.{onAfterCall}(__info__{idx});")
						.AppendLine()
						;

				// Generate OnBeforeCall end.
				//
				if (onBeforeCall is not null)
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
				if (onCatch is not null || onFinally is not null)
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

					if (onCatch is not null)
						sb
							.Append(indent).AppendLine($"\t__info__{idx}.InterceptResult = AspectGenerator.InterceptResult.ReThrow;")
							.Append(indent).AppendLine($"\t__info__{idx}.InterceptType   = AspectGenerator.InterceptType.OnCatch;")
							.AppendLine()
							.Append(indent).AppendLine($"\t{attr.ContainingNamespace}.{attr.Name}.{onCatch}(__info__{idx});")
							.AppendLine()
							.Append(indent).AppendLine($"\tif (__info__{idx}.InterceptResult == AspectGenerator.InterceptResult.ReThrow)")
							.Append(indent).AppendLine("\t\tthrow;")
							;
					else
						sb.Append(indent).AppendLine("\tthrow;");

					sb.Append(indent).AppendLine("}");

					// Generate OnFinally.
					//
					if (onFinally is not null)
					{
						sb
							.Append(indent).AppendLine("finally")
							.Append(indent).AppendLine("{")
							.Append(indent).AppendLine($"\t__info__{idx}.InterceptType = AspectGenerator.InterceptType.OnFinally;")
							.Append(indent).AppendLine($"\t{attr.ContainingNamespace}.{attr.Name}.{onFinally}(__info__{idx});")
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
				else if (!method.ReturnsVoid)
					sb.AppendLine($"\t\t\treturn __info__{idx}.ReturnValue;");

				TrimEnd(sb);
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
				sb
					.Append($"this {method.ReceiverType} __this__, ")
					;

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
