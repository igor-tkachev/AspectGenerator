using System;
using System.Collections.Generic;

namespace AspectGenerator.FilterV2
{
	/// <summary>The keys of the target filter language, version 2, as they are written before <c>:</c>.</summary>
	internal enum FilterKey
	{
		Namespace,
		Type,
		Method,
		Returns,
		Params,
		Param,
		Attribute,
		Inherits,
		Implements,
		Overrides,

		// The properties written with words take their property name as a key, for `any` and references only.
		Accessibility,
		Static,
		Async,
		Virtuality,
		Extern,
		Extension,
		Location,
		Origin,
		Generated,
	}

	/// <summary>
	/// The properties of a method a condition can be about. Every key and every word is about exactly one of them;
	/// <c>namespace:</c> and <c>type:</c> are both about <see cref="Location"/>.
	/// </summary>
	internal enum FilterProperty
	{
		Location,
		Method,
		Returns,
		Params,
		Param,
		Attribute,
		Inherits,
		Implements,
		Overrides,
		Accessibility,
		Static,
		Async,
		Virtuality,
		Extern,
		Extension,
		Origin,
		Generated,
	}

	/// <summary>The words of the language: each is one condition on one property.</summary>
	internal enum FilterWord
	{
		Public,
		Internal,
		Protected,
		Private,
		ProtectedInternal,
		PrivateProtected,
		Static,
		Instance,
		Async,
		Virtual,
		Override,
		Abstract,
		Sealed,
		Extern,
		Extension,
		Project,
		Generated,
	}

	/// <summary>The levels a reference (<c>aspect.p</c>, <c>assembly.p</c>) can name.</summary>
	internal enum FilterLevel
	{
		Aspect,
		Assembly,
	}

	/// <summary>The modifiers a parameter pattern can carry.</summary>
	internal enum FilterParameterModifier
	{
		Ref,
		RefReadonly,
		Out,
		In,
		Params,
		This,
	}

	/// <summary>How a <c>returns:</c> pattern matches the ref kind of the return.</summary>
	internal enum FilterRefKind
	{
		/// <summary>No modifier: any ref kind.</summary>
		Any,
		Ref,
		RefReadonly,
	}

	/// <summary>The vocabulary of the language and the choices it makes in one place.</summary>
	internal static class FilterV2Language
	{
		/// <summary>
		/// Whether <c>overrides:</c> is a key of its own. When it is not, <c>implements:</c> also matches a method that
		/// overrides a matching member, and <c>overrides:</c> is reported as an unknown key that names
		/// <c>implements:</c>. The word stays reserved either way, so that the choice can change without breaking a
		/// filter that names it with <c>@</c>.
		/// </summary>
		public const bool OverridesIsKey = false;

		static readonly Dictionary<string, FilterKey> _keys = new(StringComparer.Ordinal)
		{
			["namespace"]     = FilterKey.Namespace,
			["type"]          = FilterKey.Type,
			["method"]        = FilterKey.Method,
			["returns"]       = FilterKey.Returns,
			["params"]        = FilterKey.Params,
			["param"]         = FilterKey.Param,
			["attribute"]     = FilterKey.Attribute,
			["inherits"]      = FilterKey.Inherits,
			["implements"]    = FilterKey.Implements,
			["overrides"]     = FilterKey.Overrides,
			["accessibility"] = FilterKey.Accessibility,
			["static"]        = FilterKey.Static,
			["async"]         = FilterKey.Async,
			["virtuality"]    = FilterKey.Virtuality,
			["extern"]        = FilterKey.Extern,
			["extension"]     = FilterKey.Extension,
			["location"]      = FilterKey.Location,
			["origin"]        = FilterKey.Origin,
			["generated"]     = FilterKey.Generated,
		};

		static readonly Dictionary<string, FilterWord> _words = new(StringComparer.Ordinal)
		{
			["public"]    = FilterWord.Public,
			["internal"]  = FilterWord.Internal,
			["protected"] = FilterWord.Protected,
			["private"]   = FilterWord.Private,
			["static"]    = FilterWord.Static,
			["instance"]  = FilterWord.Instance,
			["async"]     = FilterWord.Async,
			["virtual"]   = FilterWord.Virtual,
			["override"]  = FilterWord.Override,
			["abstract"]  = FilterWord.Abstract,
			["sealed"]    = FilterWord.Sealed,
			["extern"]    = FilterWord.Extern,
			["extension"] = FilterWord.Extension,
			["project"]   = FilterWord.Project,
			["generated"] = FilterWord.Generated,
		};

		static readonly Dictionary<string, FilterProperty> _properties = new(StringComparer.Ordinal)
		{
			["location"]      = FilterProperty.Location,
			["method"]        = FilterProperty.Method,
			["returns"]       = FilterProperty.Returns,
			["params"]        = FilterProperty.Params,
			["param"]         = FilterProperty.Param,
			["attribute"]     = FilterProperty.Attribute,
			["inherits"]      = FilterProperty.Inherits,
			["implements"]    = FilterProperty.Implements,
			["overrides"]     = FilterProperty.Overrides,
			["accessibility"] = FilterProperty.Accessibility,
			["static"]        = FilterProperty.Static,
			["async"]         = FilterProperty.Async,
			["virtuality"]    = FilterProperty.Virtuality,
			["extern"]        = FilterProperty.Extern,
			["extension"]     = FilterProperty.Extension,
			["origin"]        = FilterProperty.Origin,
			["generated"]     = FilterProperty.Generated,
		};

		public static FilterKey Key(string text)
		{
			return _keys[text];
		}

		public static FilterWord Word(string text)
		{
			return _words[text];
		}

		/// <summary>The property a reference names, by its property name; <see langword="false"/> for any other text.</summary>
		public static bool TryGetProperty(string name, out FilterProperty property)
		{
			if (name == "overrides" && !OverridesIsKey)
			{
				property = default;
				return false;
			}

			return _properties.TryGetValue(name, out property);
		}

		/// <summary>The property a condition with this key is about.</summary>
		public static FilterProperty Property(FilterKey key)
		{
			return key switch
			{
				FilterKey.Namespace     => FilterProperty.Location,
				FilterKey.Type          => FilterProperty.Location,
				FilterKey.Location      => FilterProperty.Location,
				FilterKey.Method        => FilterProperty.Method,
				FilterKey.Returns       => FilterProperty.Returns,
				FilterKey.Params        => FilterProperty.Params,
				FilterKey.Param         => FilterProperty.Param,
				FilterKey.Attribute     => FilterProperty.Attribute,
				FilterKey.Inherits      => FilterProperty.Inherits,
				FilterKey.Implements    => FilterProperty.Implements,
				FilterKey.Overrides     => FilterProperty.Overrides,
				FilterKey.Accessibility => FilterProperty.Accessibility,
				FilterKey.Static        => FilterProperty.Static,
				FilterKey.Async         => FilterProperty.Async,
				FilterKey.Virtuality    => FilterProperty.Virtuality,
				FilterKey.Extern        => FilterProperty.Extern,
				FilterKey.Extension     => FilterProperty.Extension,
				FilterKey.Origin        => FilterProperty.Origin,
				FilterKey.Generated     => FilterProperty.Generated,
				_                       => throw new ArgumentOutOfRangeException(nameof(key), key, null),
			};
		}

		/// <summary>The property a word is about.</summary>
		public static FilterProperty Property(FilterWord word)
		{
			return word switch
			{
				FilterWord.Public            => FilterProperty.Accessibility,
				FilterWord.Internal          => FilterProperty.Accessibility,
				FilterWord.Protected         => FilterProperty.Accessibility,
				FilterWord.Private           => FilterProperty.Accessibility,
				FilterWord.ProtectedInternal => FilterProperty.Accessibility,
				FilterWord.PrivateProtected  => FilterProperty.Accessibility,
				FilterWord.Static            => FilterProperty.Static,
				FilterWord.Instance          => FilterProperty.Static,
				FilterWord.Async             => FilterProperty.Async,
				FilterWord.Virtual           => FilterProperty.Virtuality,
				FilterWord.Override          => FilterProperty.Virtuality,
				FilterWord.Abstract          => FilterProperty.Virtuality,
				FilterWord.Sealed            => FilterProperty.Virtuality,
				FilterWord.Extern            => FilterProperty.Extern,
				FilterWord.Extension         => FilterProperty.Extension,
				FilterWord.Project           => FilterProperty.Origin,
				FilterWord.Generated         => FilterProperty.Generated,
				_                            => throw new ArgumentOutOfRangeException(nameof(word), word, null),
			};
		}

		/// <summary>The name of a property as a key and in a reference.</summary>
		public static string Name(FilterProperty property)
		{
			return property switch
			{
				FilterProperty.Location      => "location",
				FilterProperty.Method        => "method",
				FilterProperty.Returns       => "returns",
				FilterProperty.Params        => "params",
				FilterProperty.Param         => "param",
				FilterProperty.Attribute     => "attribute",
				FilterProperty.Inherits      => "inherits",
				FilterProperty.Implements    => "implements",
				FilterProperty.Overrides     => "overrides",
				FilterProperty.Accessibility => "accessibility",
				FilterProperty.Static        => "static",
				FilterProperty.Async         => "async",
				FilterProperty.Virtuality    => "virtuality",
				FilterProperty.Extern        => "extern",
				FilterProperty.Extension     => "extension",
				FilterProperty.Origin        => "origin",
				FilterProperty.Generated     => "generated",
				_                            => throw new ArgumentOutOfRangeException(nameof(property), property, null),
			};
		}

		/// <summary>Whether a key takes only <c>any</c> or a reference: the keys of the properties written with words.</summary>
		public static bool IsPropertyNameKey(FilterKey key)
		{
			return key >= FilterKey.Accessibility;
		}
	}
}
