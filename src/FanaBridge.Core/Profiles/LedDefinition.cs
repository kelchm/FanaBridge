using Newtonsoft.Json;

namespace FanaBridge.Profiles
{
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
        /// For <see cref="LedChannel.ButtonAuxIntensity"/>: byte index in the intensity payload.
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
}
