using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace FanaBridge.Profiles
{
    /// <summary>
    /// A complete wheel profile — the single source of truth for a device's
    /// LED and display configuration.  Loaded from JSON files in the Profiles
    /// directory.  Can be shipped with the plugin (built-in) or created by
    /// users for unsupported wheels.
    /// </summary>
    public class WheelProfile
    {
        /// <summary>JSON Schema reference — always "wheel-profile.schema.json".</summary>
        [JsonProperty("$schema", Order = -3)]
        public string Schema { get; set; }

        /// <summary>
        /// Profile format version.  Must be 1 for the current schema.
        /// Increment when breaking format changes are made.
        /// </summary>
        [JsonProperty("schemaVersion", Order = -2)]
        public int SchemaVersion { get; set; }

        /// <summary>Unique profile identifier (e.g. "PSWBMW", "PHUB_PBMR").</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>Full product name.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>Short display name for SimHub UI.</summary>
        [JsonProperty("shortName")]
        public string ShortName { get; set; }

        /// <summary>Matching criteria for auto-detection via SDK.</summary>
        [JsonProperty("match")]
        public ProfileMatch Match { get; set; }

        /// <summary>Display type: "None", "Basic", or "Itm".</summary>
        [JsonProperty("display")]
        public string Display { get; set; }

        /// <summary>
        /// Pixel encoding for the Color LED channel.
        /// Defaults to "Rgb565" when omitted.  Set to "Rgb555" for hardware
        /// whose LED controller only reads 5 green bits (e.g. Button Module Rally).
        /// </summary>
        [JsonProperty("colorFormat", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string ColorFormatRaw { get; set; }

        /// <summary>
        /// Whether this profile has been tested on physical hardware.
        /// Defaults to true when omitted (existing profiles are verified).
        /// Unverified profiles show a warning banner in the UI.
        /// </summary>
        [JsonProperty("verified", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool? VerifiedRaw { get; set; }

        /// <summary>Parsed verified flag (defaults to true when omitted).</summary>
        [JsonIgnore]
        public bool Verified => VerifiedRaw ?? true;

        /// <summary>Parsed color format enum (defaults to Rgb565).</summary>
        [JsonIgnore]
        public ColorFormat ColorFormat
        {
            get
            {
                if (Enum.TryParse(ColorFormatRaw, true, out ColorFormat cf))
                    return cf;
                return ColorFormat.Rgb565;
            }
        }

        /// <summary>
        /// Ordered array of LED definitions.  Array index = SimHub logical index.
        /// The driver iterates this list each frame and dispatches to the
        /// appropriate hardware channel based on <see cref="LedDefinition.Channel"/>.
        /// </summary>
        [JsonProperty("leds")]
        public List<LedDefinition> Leds { get; set; } = new List<LedDefinition>();

        // ── Computed views ───────────────────────────────────────────────

        /// <summary>Parsed display type enum.</summary>
        [JsonIgnore]
        public DisplayType DisplayType
        {
            get
            {
                if (Enum.TryParse(Display, true, out DisplayType dt))
                    return dt;
                return FanaBridge.Profiles.DisplayType.None;
            }
        }

        /// <summary>Total LED count across all channels.</summary>
        [JsonIgnore]
        public int TotalLedCount => Leds.Count;

        /// <summary>Count of Rev LEDs (subcmd 0x00).</summary>
        [JsonIgnore]
        public int RevLedCount => Leds.Count(l => l.Channel == LedChannel.Rev);

        /// <summary>Count of Flag LEDs (subcmd 0x01).</summary>
        [JsonIgnore]
        public int FlagLedCount => Leds.Count(l => l.Channel == LedChannel.Flag);

        /// <summary>Count of RGB color LEDs (subcmd 0x02).</summary>
        [JsonIgnore]
        public int ColorLedCount => Leds.Count(l => l.Channel == LedChannel.Color);

        /// <summary>Count of monochrome intensity LEDs (subcmd 0x03).</summary>
        [JsonIgnore]
        public int MonoLedCount => Leds.Count(l => l.Channel == LedChannel.Mono);

        /// <summary>Count of legacy non-RGB rev LEDs (col01 bitmask).</summary>
        [JsonIgnore]
        public int LegacyRevLedCount => Leds.Count(l => l.Channel == LedChannel.LegacyRev);

        /// <summary>Count of RevStripe LEDs (col01 RGB333, typically 1).</summary>
        [JsonIgnore]
        public int RevStripeLedCount => Leds.Count(l => l.Channel == LedChannel.RevStripe);

        /// <summary>Count of "button" LEDs for SimHub (color + mono = everything except rev/flag).</summary>
        [JsonIgnore]
        public int ButtonLedCount => ColorLedCount + MonoLedCount;

        /// <summary>Rev + Flag count for SimHub's LedCount — includes all rev-like channels.</summary>
        [JsonIgnore]
        public int RevFlagCount => RevLedCount + FlagLedCount + LegacyRevLedCount + RevStripeLedCount;

        /// <summary>True if this device has any LEDs at all.</summary>
        [JsonIgnore]
        public bool HasLeds => Leds.Count > 0;

        // ── Runtime metadata (set by WheelProfileStore, not serialized) ──

        /// <summary>
        /// Whether this profile is built-in (embedded resource) or user-created.
        /// Set by <see cref="WheelProfileStore"/> during loading.
        /// </summary>
        [JsonIgnore]
        public ProfileSource Source { get; set; }

        /// <summary>
        /// Disk path for user profiles, or the embedded resource name for
        /// built-in profiles.  Useful for diagnostics and UI display.
        /// </summary>
        [JsonIgnore]
        public string SourcePath { get; set; }
    }
}
