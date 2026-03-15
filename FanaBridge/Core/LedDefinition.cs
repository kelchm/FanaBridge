using System.Collections.Generic;
using Newtonsoft.Json;

namespace FanaBridge
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
        public LedChannel Channel { get; set; }

        /// <summary>
        /// Index within the channel's protocol array.
        /// For <see cref="LedChannel.Color"/>: slot in the subcmd 0x02 color array (0-11).
        /// For <see cref="LedChannel.Mono"/>: byte index in the 16-byte intensity payload.
        /// For <see cref="LedChannel.Rev"/>/<see cref="LedChannel.Flag"/>: slot in subcmd 0x00/0x01.
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
