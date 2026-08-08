namespace FanaBridge.Plugin.Display
{
    /// <summary>
    /// Type-safe display configuration.
    /// Serialized to/from the device instance's JObject settings.
    /// </summary>
    public class DisplaySettings
    {
        public const string DefaultMode = "Gear";

        /// <summary>
        /// Sentinel <see cref="DisplayMode"/> meaning "display off": FanaBridge never
        /// writes to the 7-segment display (beyond a one-shot blank on the transition),
        /// leaving it free for the firmware or another application to drive. On ITM
        /// wheels this turns off the optional legacy gear/speed page.
        /// </summary>
        public const string ModeNone = "None";

        /// <summary>
        /// Display mode: "None", "Gear", "Speed", "GearAndSpeed", or "GearUpshiftBrackets".
        /// On basic 7-segment wheels this drives the wheel's only display; on ITM wheels it
        /// selects the optional legacy gear/speed page's mode. ITM telemetry pages
        /// themselves are firmware-driven (chosen with the wheel button).
        /// </summary>
        public string DisplayMode { get; set; } = DefaultMode;

        // ── ITM options ───────────────────────────────────────────────────

        public const bool DefaultItmEnabled = true;
        /// <summary>
        /// Whether the ITM telemetry display is enabled. Unchecking sends the firmware
        /// "ITM off" command (as the Fanatec software does); rechecking re-enables it.
        /// </summary>
        public bool ItmEnabled { get; set; } = DefaultItmEnabled;

        // Some games don't report a usable total laps or field size, producing misleading
        // "/0" or "/2" suffixes. These let the user turn the totals off per total.

        public const bool DefaultShowLapTotal = true;
        /// <summary>Show the "/total laps" suffix on the ITM lap field.</summary>
        public bool ItmShowLapTotal { get; set; } = DefaultShowLapTotal;

        public const bool DefaultShowPositionTotal = true;
        /// <summary>Show the "/field size" suffix on the ITM position field.</summary>
        public bool ItmShowPositionTotal { get; set; } = DefaultShowPositionTotal;

        public const byte DefaultItmDefaultPage = 1;   // Lap Info
        /// <summary>
        /// The ITM page (wire page number) forced when the display starts. The wheel's display
        /// button navigates from there. Defaults to page 1 (Lap Info). Valid page numbers depend
        /// on the display device — see <c>ItmDeviceCatalog.PagesFor</c>.
        /// </summary>
        public byte ItmDefaultPage { get; set; } = DefaultItmDefaultPage;
    }
}
