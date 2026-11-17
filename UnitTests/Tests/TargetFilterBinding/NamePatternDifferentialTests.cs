using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using AspectGenerator.Cascade;
using AspectGenerator.TargetFilterBinding;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilterBinding
{
	/// <summary>
	/// The dotted pattern matcher against a second reading of the same patterns that shares nothing with it: every
	/// alternative written out, and a plain search over what is left. Random patterns and names, from a fixed seed.
	/// </summary>
	[TestClass]
	public class NamePatternDifferentialTests
	{
		public TestContext TestContext { get; set; } = null!;

		// ---- a pattern as the test builds it ---------------------------------------------------------------------

		abstract record Part;
		sealed record TextPart(string Text) : Part;
		sealed record StarPart : Part;
		sealed record ChoicePart(List<List<Segment>> Alternatives) : Part;

		/// <summary>A segment: <c>**</c> where <see cref="Parts"/> is null.</summary>
		sealed record Segment(List<Part>? Parts);

		static string Render(List<Segment> pattern)
		{
			return string.Join(".", pattern.Select(static s => s.Parts is null ? "**" : string.Concat(s.Parts.Select(static p => p switch
			{
				TextPart text     => text.Text,
				StarPart          => "*",
				ChoicePart choice => "{" + string.Join(",", choice.Alternatives.Select(Render)) + "}",
				_                 => throw new InvalidOperationException(),
			}))));
		}

		static List<Segment> RandomPattern(Random random, int depth)
		{
			var segments = new List<Segment>();
			var count    = random.Next(1, depth == 0 ? 4 : 3);

			for (var i = 0; i < count; i++)
			{
				// `**` stands as a whole segment of the pattern itself, not inside alternatives.
				if (depth == 0 && random.Next(6) == 0)
				{
					segments.Add(new Segment(null));
					continue;
				}

				var parts = new List<Part>();
				var size  = random.Next(1, 4);

				for (var p = 0; p < size; p++)
				{
					var kind = random.Next(depth < 2 ? 7 : 5);

					if (kind < 3)
						parts.Add(new TextPart(random.Next(3) switch { 0 => "a", 1 => "b", _ => "ab" }));
					else if (kind < 5 && parts.LastOrDefault() is not StarPart)
						parts.Add(new StarPart());
					else if (kind >= 5)
						parts.Add(new ChoicePart([..Enumerable.Range(0, random.Next(1, 4)).Select(_ => RandomPattern(random, depth + 1))]));
					else
						parts.Add(new TextPart("b"));
				}

				segments.Add(new Segment(parts));
			}

			return segments;
		}

		// ---- the second reading ----------------------------------------------------------------------------------

		/// <summary>Every way to read a pattern with its alternatives written out: segments of text and stars.</summary>
		static List<List<string?>> Expand(List<Segment> pattern)
		{
			var ways = new List<List<string?>> { new() };

			foreach (var segment in pattern)
			{
				if (segment.Parts is null)
				{
					foreach (var way in ways)
						way.Add(null);

					continue;
				}

				// The ways to read this segment: each a run of segments, the first of which goes on the segment before the
				// braces and the last of which goes on after them.
				var runs = new List<List<string>> { new() { "" } };

				foreach (var part in segment.Parts)
				{
					if (part is ChoicePart choice)
					{
						var next = new List<List<string>>();

						foreach (var run in runs)
						{
							foreach (var alternative in choice.Alternatives.SelectMany(Expand))
							{
								var joined = run.ToList();

								joined[^1] += alternative[0];
								joined.AddRange(alternative.Skip(1)!);
								next.Add(joined);
							}
						}

						runs = next;
					}
					else
					{
						foreach (var run in runs)
							run[^1] += part is TextPart text ? text.Text : "*";
					}
				}

				ways = [..ways.SelectMany(way => runs.Select(run => way.Concat(run).ToList()))];
			}

			return ways;
		}

		static bool MatchSegment(string pattern, int p, string name, int n)
		{
			if (p == pattern.Length)
				return n == name.Length;

			if (pattern[p] == '*')
				return Enumerable.Range(n, name.Length - n + 1).Any(next => MatchSegment(pattern, p + 1, name, next));

			return n < name.Length && pattern[p] == name[n] && MatchSegment(pattern, p + 1, name, n + 1);
		}

		static bool MatchWhole(List<string?> pattern, int p, string[] names, int n)
		{
			if (p == pattern.Count)
				return n == names.Length;

			if (pattern[p] is not { } segment)
				return Enumerable.Range(n, names.Length - n + 1).Any(next => MatchWhole(pattern, p + 1, names, next));

			return n < names.Length && MatchSegment(segment, 0, names[n], 0) && MatchWhole(pattern, p + 1, names, n + 1);
		}

		static bool Reference(List<Segment> pattern, string[] names, bool rooted)
		{
			foreach (var way in Expand(pattern))
			{
				if (rooted ? MatchWhole(way, 0, names, 0) : Enumerable.Range(0, names.Length + 1).Any(skip => MatchWhole(way, 0, names[skip..], 0)))
					return true;
			}

			return false;
		}

		[TestMethod]
		public void TheMatcherAgreesWithAPlainSearchTest()
		{
			var random   = new Random(20261116);
			var failures = new List<string>();
			var matched  = 0;
			var cases    = 0;

			for (var i = 0; i < 4000; i++)
			{
				var pattern = RandomPattern(random, 0);
				var text    = Render(pattern);
				var rooted  = random.Next(2) == 0;

				// A star alone is "any type" where a type stands; as a name it is one segment, which is what is tested here.
				var filter = $"namespace: {(rooted ? "global::" : "")}{text}";

				for (var n = 0; n < 6; n++)
				{
					var names = Enumerable.Range(0, random.Next(0, 5)).Select(_ => new string([..Enumerable.Range(0, random.Next(1, 5)).Select(_ => random.Next(2) == 0 ? 'a' : 'b')])).ToArray();
					var facts = new RecordFacts(string.Join(".", names.Append("C")), "M");

					// A namespace is rooted either way; the trailing reading is that of a method, whose path ends in type and method.
					var expected = Reference(pattern, names, rooted: true);
					var actual   = FilterProbe.Answer(filter, facts) == Tri.True;

					cases++;

					if (expected)
						matched++;

					if (expected != actual)
						failures.Add($"'{filter}' on [{string.Join(".", names)}]: {actual}, the plain search says {expected}");

					if (names.Length < 2)
						continue;

					var method         = new RecordFacts(string.Join(".", names[..^1]), names[^1]);
					var methodFilter   = $"method: {(rooted ? "global::" : "")}{text}";
					var expectedMethod = Reference(pattern, names, rooted);
					var actualMethod   = FilterProbe.Answer(methodFilter, method) == Tri.True;

					cases++;

					if (expectedMethod)
						matched++;

					if (expectedMethod != actualMethod)
						failures.Add($"'{methodFilter}' on [{string.Join(".", names)}]: {actualMethod}, the plain search says {expectedMethod}");
				}
			}

			TestContext.WriteLine($"{cases} cases, {matched} matches");

			Assert.AreEqual(0, failures.Count, $"{failures.Count} of {cases}:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(30))}");
			Assert.IsTrue(matched > cases / 20, $"Only {matched} of {cases} cases match.");
		}

		/// <summary>
		/// A pattern with many choices against a name that almost matches: the cost stays the product of the two lengths.
		/// Without remembering what a step found at a position this search does not end.
		/// </summary>
		[TestMethod]
		public void ManyChoicesStayCheapTest()
		{
			var name  = new string('a', 400);
			var facts = new RecordFacts("Shop." + name, name);
			var clock = Stopwatch.StartNew();

			foreach (var pattern in new[]
			{
				string.Concat(Enumerable.Repeat("*a", 40)) + "b",
				string.Concat(Enumerable.Repeat("{a,aa}", 40)) + "b",
				string.Concat(Enumerable.Repeat("{a*,*a}", 30)) + "b",
				"**." + string.Concat(Enumerable.Repeat("*a", 30)) + "b.**",
			})
			{
				Assert.AreEqual(Tri.False, FilterProbe.Answer("type: " + pattern, facts), pattern);
				Assert.AreEqual(Tri.False, FilterProbe.Answer("method: " + pattern, facts), pattern);
			}

			Assert.AreEqual(Tri.True, FilterProbe.Answer("type: " + string.Concat(Enumerable.Repeat("*a", 40)), facts));
			Assert.AreEqual(Tri.True, FilterProbe.Answer("method: **.**.**.**." + string.Concat(Enumerable.Repeat("{a,aa}*", 30)), facts));

			clock.Stop();

			TestContext.WriteLine($"{clock.ElapsedMilliseconds} ms");

			Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(20), $"{clock.ElapsedMilliseconds} ms");
		}

		/// <summary>
		/// The conditions of an ordinary filter are answered without allocating: they run for every call that is bound.
		/// </summary>
		[TestMethod]
		public void OrdinaryConditionsAllocateNothingTest()
		{
			var block = FilterBlockBuilder.Build(CascadeLevel.Aspect,
			[
				"""
				namespace: Shop.** and type: *Service<*>
				method: **.Save* or *Repository.{Get,Find}*Async or global::Shop.Services.OrderService<int>.Load
				params: (int, .., CancellationToken)
				param: ref readonly List<*>
				returns: Task<(int, string?)[]>
				attribute: {Audited, Obsolete}
				- attribute: NoLog
				inherits: IService<*>
				implements: IService<int>.Save*
				public and instance and not async and not generated and project
				"""
			]);

			Assert.AreEqual(0, block.Diagnostics.Length, string.Join("; ", block.Diagnostics));

			const string List  = "System.Collections.Generic.List<int>";
			const string Token = "System.Threading.CancellationToken";

			var facts = new RecordFacts("Shop.Services.OrderService<int>", "SaveAll", "System.Threading.Tasks.Task<(int, string)[]>", "int", "ref readonly " + List, Token)
			{
				Attributes         = [ShapeText.ParseNamed("Shop.AuditedAttribute")],
				InheritedTypes     = [ShapeText.ParseNamed("Shop.Services.OrderService<int>"), ShapeText.ParseNamed("Shop.IService<int>")],
				ImplementedMembers = [RecordFacts.MethodShape("Shop.IService<int>", "SaveAll")],
			};

			bool Ask(MatchPhase phase)
			{
				var target = phase == MatchPhase.Call ? FilterTarget.Call(facts) : FilterTarget.Definition(facts);
				var holds  = true;

				foreach (var declaration in block.Block.Declarations)
					holds &= declaration.IsSatisfied(target, null!);

				return holds && !target.DependsOnCall;
			}

			Assert.IsTrue(Ask(MatchPhase.Call));
			Assert.IsTrue(Ask(MatchPhase.Definition));

			const int Count = 20_000;

			var bytes = GC.GetAllocatedBytesForCurrentThread();

			for (var i = 0; i < Count; i++)
				Ask(MatchPhase.Call);

			bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;

			TestContext.WriteLine($"{bytes / Count} bytes a decision");

			// The target of a decision, and nothing per condition.
			Assert.IsTrue(bytes / Count <= 64, $"{bytes / Count} bytes a decision");
		}

		/// <summary>
		/// More choices in a row than the stack can follow: the matcher gives up and the pattern matches nothing, where
		/// following it would end the compiler.
		/// </summary>
		[TestMethod]
		public void MoreChoicesThanTheStackHoldsMatchNothingTest()
		{
			const int Count = 200_000;

			var name    = new string('a', Count);
			var facts   = new RecordFacts("Shop." + name, name);
			var pattern = string.Concat(Enumerable.Repeat("{a}", Count));

			Assert.AreEqual(Tri.False, FilterProbe.Answer("type: " + pattern, facts));
			Assert.AreEqual(Tri.True,  FilterProbe.Answer("type: " + string.Concat(Enumerable.Repeat("{a}", 100)), new RecordFacts("Shop." + new string('a', 100), "M")));

			// Segments by the thousand are followed without a choice.
			var dotted = string.Join(".", Enumerable.Repeat("a", 20_000));

			Assert.AreEqual(Tri.True,  FilterProbe.Answer("type: global::" + dotted + ".*", new RecordFacts(dotted + ".C", "M")));
			Assert.AreEqual(Tri.False, FilterProbe.Answer("type: global::" + dotted + ".b", new RecordFacts(dotted + ".C", "M")));
		}
	}
}
