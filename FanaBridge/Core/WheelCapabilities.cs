namespace FanaBridge
{
    /// <summary>
    /// The type of display available on a wheel or button module.
    /// Describes the protocol/capability level, not the underlying
    /// technology (OLED, LCD, 7-seg LED are all possible for a given level).
    /// </summary>
    public enum DisplayType
    {
        /// <summary>No display.</summary>
        None,

        /// <summary>Simple 3-character display (7-seg LED, small OLED, etc.).</summary>
        Basic,

        /// <summary>Rich graphical display with ITM support (OLED or LCD).</summary>
        Itm,
    }

    /// <summary>
    /// Describes the hardware capabilities of a specific Fanatec steering wheel
    /// or hub + module configuration.  Kept intentionally minimal — only add
    /// properties when there is code that needs to branch on them.
    /// </summary>
    public class WheelCapabilities
    {
        /// <summary>Full product name (e.g. "Fanatec Podium Steering Wheel BMW M4 GT3").</summary>
        public string Name { get; set; }

        /// <summary>Short display name for the SimHub Devices UI (e.g. "Fanatec BMW M4 GT3").</summary>
        public string ShortName { get; set; }

        /// <summary>Number of individually-addressable RGB button LEDs.</summary>
        public int ButtonLedCount { get; set; }

        /// <summary>Number of individually-addressable RGB encoder LEDs.</summary>
        public int EncoderLedCount { get; set; }

        /// <summary>Total addressable LEDs (buttons + encoders). Used for the raw/individual LED module.</summary>
        public int TotalLedCount => ButtonLedCount + EncoderLedCount;

        /// <summary>The type of display available on this wheel/module.</summary>
        public DisplayType Display { get; set; }

        // ── Null object ──────────────────────────────────────────────────

        /// <summary>
        /// Represents "no wheel connected" or "not yet identified".
        /// All capabilities are zeroed — this is not a real device config.
        /// </summary>
        public static WheelCapabilities None => new WheelCapabilities
        {
            Name = null,
            ShortName = null,
            ButtonLedCount = 0,
            EncoderLedCount = 0,
            Display = DisplayType.None,
        };
    }
}
