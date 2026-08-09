using System;
using Newtonsoft.Json;

namespace FanaBridge.Core.Devices.Profiles
{
    /// <summary>
    /// JSON converter for <see cref="LedChannel"/> that accepts both v1 and v2
    /// channel names. V1 names (rev, flag, color, mono) are mapped to their
    /// v2 equivalents during deserialization.
    /// </summary>
    internal class LedChannelConverter : JsonConverter<LedChannel>
    {
        public override LedChannel ReadJson(JsonReader reader, Type objectType, LedChannel existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            string value = reader.Value as string;
            if (value == null)
                throw new JsonSerializationException("LED channel must be a string.");

            // Try v2 names first (standard enum parse, case-insensitive)
            if (Enum.TryParse(value, true, out LedChannel channel))
                return channel;

            // Fall back to v1 name mapping (only names that shipped in v1)
            switch (value.ToLowerInvariant())
            {
                case "rev": return LedChannel.RevRgb;
                case "flag": return LedChannel.FlagRgb;
                case "color": return LedChannel.ButtonRgb;
                case "mono": return LedChannel.ButtonAuxIntensity;
                default:
                    throw new JsonSerializationException($"Unknown LED channel '{value}'.");
            }
        }

        public override void WriteJson(JsonWriter writer, LedChannel value, JsonSerializer serializer)
        {
            // Always write v2 names in camelCase
            string name = value.ToString();
            writer.WriteValue(char.ToLowerInvariant(name[0]) + name.Substring(1));
        }
    }
}
