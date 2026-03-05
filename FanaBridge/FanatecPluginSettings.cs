using System.Collections.Generic;

namespace FanaBridge
{
    /// <summary>
    /// Plugin settings. Must be JSON-serializable (public fields/properties, no complex types).
    /// Persisted via SimHub's ReadCommonSettings / SaveCommonSettings.
    /// </summary>
    public class FanatecPluginSettings
    {
        // ---- Device ----

        /// <summary>
        /// Optional USB Product ID override. When 0, the plugin auto-detects
        /// whichever Fanatec wheelbase is connected via the SDK.
        /// Set to a specific PID (e.g. 0x0020) to force a particular device.
        /// </summary>
        public int ProductIdOverride { get; set; } = 0;

        // ---- Performance ----

        /// <summary>Maximum HID update rate in Hz (1-120)</summary>
        public int MaxUpdateRateHz { get; set; } = 60;

        // ---- Profile selection ----

        /// <summary>
        /// Per-wheel profile override.  Key = wheel match key (e.g. "PHUB_PBMR"),
        /// Value = profile ID to use instead of auto-resolve.
        /// Empty / missing key = auto (built-in takes priority, user overrides).
        /// </summary>
        public Dictionary<string, string> ProfileOverrides { get; set; }
            = new Dictionary<string, string>();

        // ---- Feature flags ----

        /// <summary>
        /// Enable tuning features (encoder mode, etc.).  These write directly
        /// to device firmware settings via USB HID and are disabled by default.
        /// </summary>
        public bool EnableTuning { get; set; } = false;
    }
}
