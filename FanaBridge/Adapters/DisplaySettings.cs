namespace FanaBridge.Adapters
{
    /// <summary>
    /// Type-safe display configuration.
    /// Serialized to/from the device instance's JObject settings.
    /// </summary>
    public class DisplaySettings
    {
        public const string DefaultMode = "Gear";

        /// <summary>
        /// Display mode: "Gear", "Speed", "GearAndSpeed", or "GearUpshiftBrackets".
        /// </summary>
        public string DisplayMode { get; set; } = DefaultMode;
    }
}
