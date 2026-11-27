using System;
using System.Collections.Generic;
using System.Linq;

using AspectGenerator.Cascade;
using AspectGenerator.TargetFilterBinding;
using AspectGenerator.TargetFilterFacts;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AspectGenerator.Tests.FilterV2Oracle
{
	/// <summary>A method both sides decide: the oracle's facts of it, and the product's facts of its definition and of the call.</summary>
	sealed class OracleCase
	{
		public required string               Label      { get; init; }
		public required OMethod              Oracle     { get; init; }
		public required IMethodFacts         Definition { get; init; }
		public required IMethodFacts         Call       { get; init; }
	}

	/// <summary>
	/// Runs one filter text through the product the way an assembly attribute is decided: the block from
	/// <see cref="FilterBlockBuilder"/>, the cascade with an empty aspect default, the definition first and the call
	/// where the definition cannot answer for every call.
	/// </summary>
	sealed class ProductBlock
	{
		static readonly FilterBlock _emptyDefault = FilterBlockBuilder.Build(CascadeLevel.Aspect, []);

		readonly CascadeEvaluator<FilterTarget>?                  _evaluator;
		readonly CascadeEvaluator<FilterTarget>.CascadeContext?   _context;
		readonly Func<FilterTarget,CascadeDecision>?              _decide;

		public ProductBlock(string filter)
		{
			Block = FilterBlockBuilder.Build(CascadeLevel.Assembly, [filter], label: "assembly");

			if (Block.HasErrors)
				return;

			_evaluator = new CascadeEvaluator<FilterTarget>(_emptyDefault.Block, [Block.Block], [], static t => t.Facts.IsProject);
			_context   = _evaluator.Prepare([]);

			var evaluator = _evaluator;
			var context   = _context;

			_decide = t => evaluator.Decide(t, context, []);
		}

		public FilterBlock Block { get; }

		public bool HasErrors => Block.HasErrors;

		/// <summary>Whether the block selects the call; <see langword="null"/> when the filter has an error.</summary>
		public bool? Selects(OracleCase target)
		{
			if (_decide is null)
				return null;

			if (FilterTarget.TryDecideDefinition(target.Definition, _decide, out var decision))
				return decision.Selected;

			return FilterTarget.DecideCall(target.Call, _decide).Selected;
		}
	}

	/// <summary>The methods the oracle is compared on: the fixed targets, and the calls of a piece of generic code.</summary>
	static class OracleCases
	{
		static readonly Lazy<OracleCase[]> _fixture = new(BuildTargets);
		static readonly Lazy<OracleCase[]> _targets = new(() => [.._fixture.Value.Where((_, i) => OracleTargets.Entries[i].Symbol.MethodKind != MethodKind.ExplicitInterfaceImplementation)]);
		static readonly Lazy<OracleCase[]> _calls   = new(BuildCalls);

		/// <summary>Every target of <see cref="OracleTargets"/>, in its order, each decided as a call that binds to its symbol.</summary>
		public static OracleCase[] Fixture => _fixture.Value;

		/// <summary>
		/// The targets the oracle is compared on. An explicit interface implementation is never the target of a call, and
		/// its name (the interface member's full name) has no spelling in the language: it is left out.
		/// </summary>
		public static OracleCase[] Targets => _targets.Value;

		/// <summary>The calls of <see cref="GenericSource"/>, with their call sites.</summary>
		public static OracleCase[] Calls => _calls.Value;

		public static IEnumerable<OracleCase> All => Targets.Concat(Calls);

		static OracleCase[] BuildTargets()
		{
			var compilation = OracleTargets.Compilation;
			var context     = new FactContext(compilation);
			var shapes      = new TypeShapes();

			return [..OracleTargets.Entries.Select(e => Create($"#{e.Index} {e.Label} {OracleTargets.Signature(e.Symbol)}", e.Symbol, null, compilation, context, shapes))];
		}

		static OracleCase Create(string label, IMethodSymbol symbol, SyntaxNode? site, Compilation compilation, FactContext context, TypeShapes shapes)
		{
			return new OracleCase
			{
				Label      = label,
				Oracle     = OracleFacts.Read(symbol, compilation.Assembly),
				Definition = new SymbolMethodFacts(MethodFacts.CreateDefinition(symbol, FactPlan.All, context), context, shapes),
				Call       = new SymbolMethodFacts(MethodFacts.Create(symbol, FactPlan.All, context, site), context, shapes),
			};
		}

		public static CSharpCompilation GenericCompilation { get; } = Compile();

		static CSharpCompilation Compile()
		{
			var compilation = CSharpCompilation.Create(
				"OracleGenericCalls",
				[CSharpSyntaxTree.ParseText(GenericSource, new CSharpParseOptions(LanguageVersion.Preview), "Calls.cs")],
				OracleTargets.GetReferences().Where(static r => !(r.Display ?? "").Contains("AspectGenerator.", StringComparison.OrdinalIgnoreCase)),
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

			var errors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).ToArray();

			if (errors.Length > 0)
				throw new InvalidOperationException("The generic calls do not compile:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(static e => e.ToString())));

			return compilation;
		}

		static OracleCase[] BuildCalls()
		{
			var compilation = GenericCompilation;
			var context     = new FactContext(compilation);
			var shapes      = new TypeShapes();
			var result      = new List<OracleCase>();

			foreach (var tree in compilation.SyntaxTrees)
			{
				var model = compilation.GetSemanticModel(tree);

				foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
					if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method)
						result.Add(Create("call " + invocation, method, invocation, compilation, context, shapes));
			}

			return [..result];
		}

		/// <summary>The call written <paramref name="text"/>.</summary>
		public static OracleCase Call(string text)
		{
			return Calls.Single(c => c.Label == "call " + text);
		}

		/// <summary>Generic code and the calls it makes: constructed calls, calls with the caller's own type parameter, both forms of an extension method.</summary>
		public const string GenericSource =
			"""
			#nullable enable
			using System;
			using System.Collections.Generic;
			using System.Linq;
			using System.Threading.Tasks;

			namespace G
			{
				public class Box<T>
				{
					public void Put(T item) { }
					public U Take<U>(U value) { return value; }
					public T Get() { return default!; }
				}

				public static class Ext
				{
					public static bool Has<T>(this IEnumerable<T> source) { return true; }
				}

				public class Awaitable
				{
					public Awaiter GetAwaiter() { return new Awaiter(); }
				}

				public class Awaiter : System.Runtime.CompilerServices.INotifyCompletion
				{
					public bool IsCompleted { get { return true; } }
					public void GetResult() { }
					public void OnCompleted(Action continuation) { }
				}

				public static class Use
				{
					public static T Make<T>() { return default!; }

					public static void Run<TCaller>(List<int> li, List<string> ls, List<TCaller> lt, Box<int> bi, Box<TCaller> bt, int[] numbers, TCaller value)
					{
						li.Add(1);
						ls.Add("");
						lt.Add(value);
						bi.Put(1);
						bt.Put(value);
						bi.Take<string>("");
						bi.Take(value);
						numbers.Any();
						Enumerable.Any<int>(numbers);
						numbers.Has();
						Ext.Has<int>(numbers);
						lt.Has();
						Make<Task<int>>();
						Make<int>();
						Make<TCaller>();
						Make<Awaitable>();
						Task.FromResult(1);
						bi.Get();
						bt.Get();
					}
				}
			}
			""";
	}
}
