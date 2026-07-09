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
}
