using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AspectGenerator.Tests.TargetFilters
{
	/// <summary>Which reader filters go through.</summary>
	[TestClass]
	public class TargetFilterFrontTests
	{
		const string LegacyFront = "LegacyTargetFilterFront";

		[TestMethod]
		public void LegacySplitterIsNotCalledByTheGeneratorTest()
		{
			// The hand-written splitter is kept for the differential test only: no method of the generator outside it
			// names any of its members, so filters cannot reach it.
			using var stream = File.OpenRead(typeof(AspectFilters).Assembly.Location);
			using var pe     = new PEReader(stream);

			var metadata = pe.GetMetadataReader();
			var legacy   = metadata.TypeDefinitions.Single(h => metadata.GetString(metadata.GetTypeDefinition(h).Name) == LegacyFront);
			var inside   = new HashSet<TypeDefinitionHandle>(TypesWithin(metadata, legacy));
			var members  = new HashSet<int>();

			foreach (var type in inside)
			{
				var definition = metadata.GetTypeDefinition(type);

				members.Add(MetadataTokens.GetToken(type));

				foreach (var method in definition.GetMethods())
					members.Add(MetadataTokens.GetToken(method));

				foreach (var field in definition.GetFields())
					members.Add(MetadataTokens.GetToken(field));
			}

			var callers = new List<string>();
			var within  = 0;

			foreach (var typeHandle in metadata.TypeDefinitions)
			{
				var type = metadata.GetTypeDefinition(typeHandle);

				if (inside.Contains(typeHandle))
				{
					// The scan finds what it looks for: the splitter's own methods name its members.
					foreach (var methodHandle in type.GetMethods())
						if (metadata.GetMethodDefinition(methodHandle).RelativeVirtualAddress is var rva and not 0 && Tokens(pe.GetMethodBody(rva).GetILBytes() ?? []).Any(members.Contains))
							within++;

					continue;
				}

				foreach (var methodHandle in type.GetMethods())
				{
					var method = metadata.GetMethodDefinition(methodHandle);

					if (method.RelativeVirtualAddress == 0)
						continue;

					var body = pe.GetMethodBody(method.RelativeVirtualAddress);

					if (Tokens(body.GetILBytes() ?? []).Any(members.Contains))
						callers.Add($"{metadata.GetString(type.Name)}.{metadata.GetString(method.Name)}");
				}
			}

			Assert.IsTrue(members.Count > 10, "The legacy front was not found in the generator assembly.");
			Assert.IsTrue(within > 5, "The scan finds no reference even inside the legacy front.");
			Assert.AreEqual(0, callers.Count, "Generator methods that reach the legacy splitter: " + string.Join(", ", callers));
		}

		[TestMethod]
		public void LegacySplitterIsReachableFromTheTestsTest()
		{
			// What the differential test compares against does read filters.
			var set = AspectFilters.LegacyTargetFilterFront.GetFilters(["method: Save"]);

			Assert.AreEqual(1, set.Rules.Count);
		}

		static IEnumerable<TypeDefinitionHandle> TypesWithin(MetadataReader metadata, TypeDefinitionHandle type)
		{
			yield return type;

			foreach (var nested in metadata.GetTypeDefinition(type).GetNestedTypes())
				foreach (var inner in TypesWithin(metadata, nested))
					yield return inner;
		}

		static readonly Dictionary<short, OperandType> OperandTypes = typeof(OpCodes)
			.GetFields(BindingFlags.Public | BindingFlags.Static)
			.Select(static f => (OpCode)f.GetValue(null)!)
			.ToDictionary(static o => o.Value, static o => o.OperandType);

		/// <summary>The metadata tokens an IL body names.</summary>
		static IEnumerable<int> Tokens(byte[] il)
		{
			for (var i = 0; i < il.Length;)
			{
				short value = il[i++];

				if (value == 0xFE)
					value = unchecked((short)(0xFE00 | il[i++]));

				switch (OperandTypes[value])
				{
					case OperandType.InlineNone:
						break;
					case OperandType.ShortInlineBrTarget:
					case OperandType.ShortInlineI:
					case OperandType.ShortInlineVar:
						i += 1;
						break;
					case OperandType.InlineVar:
						i += 2;
						break;
					case OperandType.InlineI8:
					case OperandType.InlineR:
						i += 8;
						break;
					case OperandType.InlineSwitch:
						i += 4 + BitConverter.ToInt32(il, i) * 4;
						break;
					case OperandType.InlineField:
					case OperandType.InlineMethod:
					case OperandType.InlineTok:
					case OperandType.InlineType:
						yield return BitConverter.ToInt32(il, i);
						i += 4;
						break;
					default:
						i += 4;
						break;
				}
			}
		}
	}
}
