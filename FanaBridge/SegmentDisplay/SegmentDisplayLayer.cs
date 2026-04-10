using FanaBridge.Shared.Conditions;
using Newtonsoft.Json;

namespace FanaBridge.SegmentDisplay
{
    /// <summary>
    /// A single layer in the segment display stack. Combines role, activation
    /// condition, content source, rendering parameters, and visibility flags.
    /// </summary>
    public class SegmentDisplayLayer
    {
        // ── Identity ────────────────────────────────────────────────

        /// <summary>Human-readable name (e.g., "Gear", "Pit Limiter").</summary>
        [JsonProperty("Name")]
        public string Name { get; set; }

        /// <summary>
        /// Catalog template key. Null for user-created custom layers.
        /// Used to identify predefined layers for upgrade/reset.
        /// </summary>
        [JsonProperty("CatalogKey", NullValueHandling = NullValueHandling.Ignore)]
        public string CatalogKey { get; set; }

        // ── Stack behavior ──────────────────────────────────────────

        /// <summary>Whether this is a Screen (base page) or Overlay (condition-triggered interrupt).</summary>
        [JsonProperty("Role")]
        public LayerRole Role { get; set; }

        // ── Activation ──────────────────────────────────────────────

        /// <summary>When this layer is active. Polymorphic — see ActivationCondition subtypes.</summary>
        [JsonProperty("Condition")]
        public ActivationCondition Condition { get; set; }

        // ── Content ─────────────────────────────────────────────────

        /// <summary>What this layer displays. Polymorphic — see ContentSource subtypes.</summary>
        [JsonProperty("Content")]
        public ContentSource Content { get; set; }

        // ── Rendering ───────────────────────────────────────────────

        /// <summary>Text alignment within the 3-digit display.</summary>
        [JsonProperty("Alignment")]
        public AlignmentType Alignment { get; set; }

        /// <summary>How text longer than 3 characters is handled.</summary>
        [JsonProperty("Overflow")]
        public OverflowType Overflow { get; set; }

        /// <summary>Scroll speed in milliseconds per step (when overflow is Scroll).</summary>
        [JsonProperty("ScrollSpeedMs")]
        public int ScrollSpeedMs { get; set; } = 250;

        /// <summary>Visual effects applied to the layer output. Null or empty for none.</summary>
        [JsonProperty("Effects", NullValueHandling = NullValueHandling.Ignore)]
        public SegmentEffect[] Effects { get; set; }

        // ── Visibility ──────────────────────────────────────────────

        /// <summary>Show this layer when a game session is active.</summary>
        [JsonProperty("ShowWhenRunning")]
        public bool ShowWhenRunning { get; set; } = true;

        /// <summary>Show this layer when no game is running.</summary>
        [JsonProperty("ShowWhenIdle")]
        public bool ShowWhenIdle { get; set; }

        // ── Computed ────────────────────────────────────────────────

        /// <summary>True if the layer is enabled in at least one game state.</summary>
        [JsonIgnore]
        public bool IsEnabled { get { return ShowWhenRunning || ShowWhenIdle; } }

        /// <summary>True if this is a user-created layer (not from the catalog).</summary>
        [JsonIgnore]
        public bool IsCustom { get { return string.IsNullOrEmpty(CatalogKey); } }
    }
}
