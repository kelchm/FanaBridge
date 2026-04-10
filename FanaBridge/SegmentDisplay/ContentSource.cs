using Newtonsoft.Json;

namespace FanaBridge.SegmentDisplay
{
    /// <summary>
    /// Base class for polymorphic content sources.
    /// Determines what a layer displays. JSON discriminated by <see cref="Type"/>.
    /// </summary>
    [JsonConverter(typeof(ContentSourceConverter))]
    public abstract class ContentSource
    {
        /// <summary>JSON discriminator string.</summary>
        [JsonProperty("Type", Order = -10)]
        public abstract string Type { get; }
    }

    /// <summary>Content resolved from a SimHub property value.</summary>
    public class PropertyContent : ContentSource
    {
        public override string Type { get { return "Property"; } }

        /// <summary>SimHub property path (e.g., "DataCorePlugin.GameData.Gear").</summary>
        [JsonProperty("PropertyName")]
        public string PropertyName { get; set; }

        /// <summary>How the value is formatted for display.</summary>
        [JsonProperty("Format")]
        public SegmentFormat Format { get; set; }

        /// <summary>
        /// TimeSpan format string used when <see cref="Format"/> is
        /// <see cref="SegmentFormat.Time"/>. Default "ss\.f".
        /// </summary>
        [JsonProperty("TimeFormat", NullValueHandling = NullValueHandling.Ignore)]
        public string TimeFormat { get; set; }
    }

    /// <summary>Static text content.</summary>
    public class FixedTextContent : ContentSource
    {
        public override string Type { get { return "FixedText"; } }

        /// <summary>The text to display.</summary>
        [JsonProperty("Text")]
        public string Text { get; set; }
    }

    /// <summary>Content resolved from an NCalc expression.</summary>
    public class ExpressionContent : ContentSource
    {
        public override string Type { get { return "Expression"; } }

        /// <summary>NCalc expression to evaluate.</summary>
        [JsonProperty("Expression")]
        public string Expression { get; set; }

        /// <summary>How the result is formatted for display.</summary>
        [JsonProperty("Format")]
        public SegmentFormat Format { get; set; }
    }

    /// <summary>Cycles through multiple content sources at a fixed interval.</summary>
    public class SequenceContent : ContentSource
    {
        public override string Type { get { return "Sequence"; } }

        /// <summary>Ordered content sources to cycle through.</summary>
        [JsonProperty("Items")]
        public ContentSource[] Items { get; set; }

        /// <summary>Milliseconds between each item in the cycle.</summary>
        [JsonProperty("IntervalMs")]
        public int IntervalMs { get; set; } = 500;
    }

    /// <summary>Special hardware command that bypasses the text render pipeline.</summary>
    public class DeviceCommandContent : ContentSource
    {
        public override string Type { get { return "DeviceCommand"; } }

        /// <summary>The hardware command to send.</summary>
        [JsonProperty("Command")]
        public DeviceCommand Command { get; set; }
    }
}
