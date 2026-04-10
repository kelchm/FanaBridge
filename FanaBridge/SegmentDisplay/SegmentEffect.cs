using Newtonsoft.Json;

namespace FanaBridge.SegmentDisplay
{
    /// <summary>
    /// Base class for display effects that modify how content is rendered.
    /// JSON discriminated by <see cref="Type"/>.
    /// </summary>
    [JsonConverter(typeof(SegmentEffectConverter))]
    public abstract class SegmentEffect
    {
        /// <summary>JSON discriminator string.</summary>
        [JsonProperty("Type", Order = -10)]
        public abstract string Type { get; }

        /// <summary>Human-readable label for UI display (e.g., "Blink", "Flash 3x").</summary>
        [JsonIgnore]
        public abstract string Label { get; }
    }

    /// <summary>
    /// Toggles display visibility on a repeating on/off interval.
    /// </summary>
    public class BlinkEffect : SegmentEffect
    {
        public override string Type { get { return "Blink"; } }
        public override string Label { get { return "Blink"; } }

        /// <summary>Milliseconds the display is visible.</summary>
        [JsonProperty("OnMs")]
        public int OnMs { get; set; } = 500;

        /// <summary>Milliseconds the display is blanked.</summary>
        [JsonProperty("OffMs")]
        public int OffMs { get; set; } = 500;
    }

    /// <summary>
    /// Rapid blink N times from activation, then shows solid.
    /// Count=0 means continuous flashing (no transition to solid).
    /// </summary>
    public class FlashEffect : SegmentEffect
    {
        public override string Type { get { return "Flash"; } }

        public override string Label
        {
            get
            {
                if (Count == 0) return "Flash";
                return "Flash " + Count + "x";
            }
        }

        /// <summary>Number of flash cycles. 0 = continuous.</summary>
        [JsonProperty("Count")]
        public int Count { get; set; } = 3;

        /// <summary>Duration of each on/off cycle in milliseconds.</summary>
        [JsonProperty("RateMs")]
        public int RateMs { get; set; } = 150;
    }
}
