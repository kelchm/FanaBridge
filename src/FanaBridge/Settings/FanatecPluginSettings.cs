using System;
using System.Collections.Generic;

namespace FanaBridge.Settings
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
        /// whichever Fanatec wheelbase is connected over HID.
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
        /// <remarks>
        /// Keys compare case-insensitively, as the registration-time reader
        /// (<see cref="PersistedPluginSettings"/>) does, so a hand-edited key in
        /// another case resolves the same way at registration and at runtime.
        /// </remarks>
        public Dictionary<string, string> ProfileOverrides
        {
            get => _profileOverrides;
            set => _profileOverrides = CaseInsensitive(value);
        }

        private volatile Dictionary<string, string> _profileOverrides = CaseInsensitive(null);

        private static Dictionary<string, string> CaseInsensitive(IDictionary<string, string> source)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (source != null)
                foreach (var kv in source)
                    if (kv.Key != null)
                        result[kv.Key] = kv.Value;
            return result;
        }

        // ---- Feature flags ----

        /// <summary>
        /// Enable tuning features (encoder mode, etc.).  These write directly
        /// to device firmware settings via USB HID and are disabled by default.
        /// </summary>
        public bool EnableTuning { get; set; } = false;

        /// <summary>
        /// Experimental Control Mapper integration: feeds FanaBridge's FF 08 wheel
        /// identity into SimHub's Control Mapper so it can tell Fanatec rims apart —
        /// including wheels/bases its built-in support can't recognize (Podium DD, newer
        /// wheels). On by default; takes effect live (no restart) and only differentiates
        /// rims while Control Mapper's own "Recognize Individual Wheels" toggle is on
        /// (surfaced in the settings UI when it isn't). Gap-filler: SimHub wins for any
        /// wheel it already recognizes, so existing mappings are never disturbed, and it's
        /// a no-op unless the user has opted into per-rim mapping at the SimHub level. See
        /// <see cref="ControlMapper.ControlMapperBridge"/>.
        /// </summary>
        public bool EnableControlMapperIntegration { get; set; } = true;

        // ---- Updates ----

        /// <summary>
        /// Check GitHub for a newer FanaBridge release automatically: once at
        /// startup, then every 24 h while SimHub runs (one API request per
        /// check). Takes effect live. The manual "Check for updates" link in
        /// the settings UI works regardless.
        /// </summary>
        public bool EnableUpdateCheck { get; set; } = true;
    }
}
