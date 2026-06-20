using System;
using System.Linq;

namespace AspectGenerator
{
	static class AspectLifetimeResolver
	{
		public static AspectLifetimeInfo Resolve(AttributeInfo attribute)
		{
			var declaredLifetime = GetDeclaredLifetime(attribute);

			return new AspectLifetimeInfo(
				declaredLifetime,
				declaredLifetime == "Instance" ? "Instance" : "Static");
		}

		public static bool UsesInstanceLifetime(AttributeInfo attribute)
		{
			return Resolve(attribute).EffectiveLifetime == "Instance";
		}

		static string GetDeclaredLifetime(AttributeInfo attribute)
		{
			foreach (var option in AspectSourceGenerator.GetAspectOptions(attribute))
			{
				if (option.Key != "Lifetime")
					continue;

				return ParseLifetime(option.Value);
			}

			return "Auto";
		}

		static string ParseLifetime(object? value)
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
	}

	readonly record struct AspectLifetimeInfo(string DeclaredLifetime, string EffectiveLifetime);
}
