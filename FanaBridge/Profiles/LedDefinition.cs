using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace FanaBridge.Profiles
{
    /// <summary>
    /// Structured input association for a single LED.
    /// An encoder LED can carry BOTH relative and absolute mappings
    /// simultaneously so the user can switch modes at runtime.
    /// </summary>
    public class InputMapping
    {
        /// <summary>
        /// Button input ID (e.g. "JoystickPlugin.FANATEC_Wheel.Button3").
        /// Populated only for momentary push-button LEDs.
        /// </summary>
        [JsonProperty("button", NullValueHandling = NullValueHandling.Ignore)]
        public string Button { get; set; }

        /// <summary>
        /// Relative (incremental) encoder inputs: [CW, CCW].
        /// Null/omitted if not captured or not applicable.
        /// </summary>
        [JsonProperty("relative", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Relative { get; set; }

        /// <summary>
        /// Absolute (positional) encoder inputs — one entry per detent
        /// in the order they were detected (typically 12 for Fanatec).
        /// Null/omitted if not captured or not applicable.
        /// </summary>
        [JsonProperty("absolute", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Absolute { get; set; }

        /// <summary>True when at least one input has been captured.</summary>
        [JsonIgnore]
        public bool HasAny =>
            Button != null ||
            (Relative != null && Relative.Count > 0) ||
            (Absolute != null && Absolute.Count > 0);
    }

    /// <summary>
    /// Describes a single physical LED on the device.
    /// The array order in the profile defines the SimHub logical index.
    /// </summary>
    public class LedDefinition
    {
        /// <summary>Hardware communication channel.</summary>
        [JsonProperty("channel")]
        [JsonConverter(typeof(LedChannelConverter))]
        public LedChannel Channel { get; set; }

        /// <summary>
        /// Index within the channel's protocol array.
        /// For <see cref="LedChannel.ButtonRgb"/>: slot in the subcmd 0x02 color array (0-11).
        /// For <see cref="LedChannel.ButtonAuxIntensity"/>: byte index in the 16-byte intensity payload.
        /// For <see cref="LedChannel.RevRgb"/>/<see cref="LedChannel.FlagRgb"/>: slot in subcmd 0x00/0x01.
        /// </summary>
        [JsonProperty("hwIndex")]
        public int HwIndex { get; set; }

        /// <summary>Semantic role — what kind of LED this is.</summary>
        [JsonProperty("role")]
        public LedRole Role { get; set; }

        /// <summary>Human-readable label for UI display.</summary>
        [JsonProperty("label")]
        public string Label { get; set; }

        /// <summary>
        /// Legacy single-input association (e.g. "enc_left").
        /// Kept for backward compatibility with hand-authored profiles.
        /// New profiles should use <see cref="InputMapping"/> instead.
        /// </summary>
        [JsonProperty("input", NullValueHandling = NullValueHandling.Ignore)]
        public string Input { get; set; }

        /// <summary>
        /// Structured input mapping — supports buttons, relative encoders,
        /// absolute encoders, or both encoder modes simultaneously.
        /// Null/omitted for LEDs with no associated inputs.
        /// </summary>
        [JsonProperty("inputMapping", NullValueHandling = NullValueHandling.Ignore)]
        public InputMapping InputMapping { get; set; }
    }

    /// <summary>
    /// JSON converter for <see cref="LedChannel"/> that accepts both v1 and v2
    /// channel names. V1 names (rev, flag, color, mono, legacyRev, revStripe)
    /// are silently mapped to their v2 equivalents during deserialization.
    /// </summary>
    internal class LedChannelConverter : JsonConverter<LedChannel>
    {
        public override LedChannel ReadJson(JsonReader reader, Type objectType, LedChannel existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            string value = reader.Value as string;
            if (value == null)
                return default;

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
                default: return default;
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
