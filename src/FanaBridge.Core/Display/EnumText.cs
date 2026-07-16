using System;

namespace FanaBridge.Display
{
    /// <summary>
    /// Enum ↔ camelCase-string translation for the display config document. The document
    /// stores enum-valued fields as raw strings (the <c>*Raw</c> properties) with typed
    /// accessors that parse on read, rather than typed enums with a converter, for one
    /// load-bearing reason: a value written by a future version must survive a load/save
    /// round-trip through this build byte-for-byte. Parsing into a typed enum at
    /// deserialization time would discard the original text — degrading the rule
    /// permanently instead of only for the builds that don't know the value.
    /// Unrecognized text parses to the enum's <c>Unknown</c> member (or null for nullable
    /// reads); <see cref="DisplayConfigValidator"/> turns that into a per-rule
    /// degradation with a warning, never a throw.
    /// </summary>
    internal static class EnumText
    {
        /// <summary>Parses <paramref name="text"/> (case-insensitive), or
        /// <paramref name="fallback"/> when it is missing or unrecognized.</summary>
        public static TEnum Parse<TEnum>(string text, TEnum fallback) where TEnum : struct
            => TryParse(text, out TEnum value) ? value : fallback;

        /// <summary>Parses <paramref name="text"/> (case-insensitive), or null when it is
        /// missing or unrecognized.</summary>
        public static TEnum? ParseNullable<TEnum>(string text) where TEnum : struct
            => TryParse(text, out TEnum value) ? value : (TEnum?)null;

        private static bool TryParse<TEnum>(string text, out TEnum value) where TEnum : struct
        {
            value = default(TEnum);
            if (string.IsNullOrWhiteSpace(text)
                || !Enum.TryParse(text, ignoreCase: true, out TEnum parsed))
                return false;
            // Enum.TryParse accepts bare numerals even for undefined values; only a
            // defined member counts as recognized.
            if (!Enum.IsDefined(typeof(TEnum), parsed))
                return false;
            value = parsed;
            return true;
        }

        /// <summary>The camelCase document spelling of <paramref name="value"/>.</summary>
        public static string Write<TEnum>(TEnum value) where TEnum : struct
        {
            string name = value.ToString();
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }
    }
}
