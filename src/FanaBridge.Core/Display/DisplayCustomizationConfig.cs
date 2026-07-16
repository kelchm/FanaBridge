using System.Collections.Generic;
using Newtonsoft.Json;
using FanaBridge.Protocol;

namespace FanaBridge.Display
{
    /// <summary>
    /// The per-wheel display customization document: prioritized rules for the ITM and
    /// legacy surfaces, the legacy screen library, and per-parameter field mapping
    /// overrides. There is no global layer — every document belongs to one wheel.
    ///
    /// Immutable after load by convention (the WheelProfileStore pattern): the UI edits a
    /// copy and atomically swaps the reference; the rule engine is built from a validated
    /// snapshot and rebuilt on swap. Serialization goes through
    /// <see cref="DisplayConfigSerializer"/>, which normalizes on load
    /// (<see cref="DisplayConfigValidator"/>) so a published config always satisfies the
    /// engine's invariants.
    /// </summary>
    public class DisplayCustomizationConfig
    {
        /// <summary>Current schema version for new documents.</summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>Document format version — newer versions load leniently (unknown
        /// fields ignored, unknown enum values degrade per rule) with a warning.</summary>
        [JsonProperty("schemaVersion", Order = -2)]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>Profile hook, unused in v1 — profiles are planned (per-wheel, e.g.
        /// per game/car-class) and the document reserves their identity slot so v1 files
        /// stay forward-compatible.</summary>
        [JsonProperty("profileId")]
        public string ProfileId { get; set; }

        /// <summary>Rules and base page for the ITM (pixel) display.</summary>
        [JsonProperty("itm")]
        public ItmRuleSet Itm { get; set; } = new ItmRuleSet();

        /// <summary>Rules, screens, and base screen for the legacy 7-segment surface.</summary>
        [JsonProperty("legacy")]
        public LegacyRuleSet Legacy { get; set; } = new LegacyRuleSet();

        /// <summary>Per-parameter source/format overrides, keyed by ITM param id
        /// (<see cref="ItmParam"/>). A parameter absent here keeps its built-in default —
        /// which is why an empty document reproduces stock behavior exactly.</summary>
        [JsonProperty("fieldMappings")]
        public Dictionary<ushort, FieldMapping> FieldMappings { get; set; }
            = new Dictionary<ushort, FieldMapping>();
    }

    /// <summary>The ITM surface's rule list and base page.</summary>
    public class ItmRuleSet
    {
        /// <summary>Prioritized rules — priority is list order, index 0 wins.</summary>
        [JsonProperty("rules")]
        public List<DisplayRule> Rules { get; set; } = new List<DisplayRule>();

        /// <summary>Serialized base page, preserved verbatim (see
        /// <see cref="RuleCondition.KindRaw"/>); omitted when default. Read <see cref="BasePage"/>.</summary>
        [JsonProperty("basePage")]
        public string BasePageRaw { get; set; }

        /// <summary>The "always" fallback page shown when no rule is active
        /// (defaults to <see cref="ItmPage.LapInfo"/> when omitted or unrecognized).</summary>
        [JsonIgnore]
        public ItmPage BasePage
        {
            get => EnumText.ParseNullable<ItmPage>(BasePageRaw) ?? ItmPage.LapInfo;
            set => BasePageRaw = EnumText.Write(value);
        }
    }

    /// <summary>The legacy 7-segment surface's rule list, screen library, and base screen.</summary>
    public class LegacyRuleSet
    {
        /// <summary>Prioritized rules — priority is list order, index 0 wins. Legacy rules
        /// target legacy screens only.</summary>
        [JsonProperty("rules")]
        public List<DisplayRule> Rules { get; set; } = new List<DisplayRule>();

        /// <summary>The screen shown when no rule is active, or null for a blank display.</summary>
        [JsonProperty("baseScreenId")]
        public string BaseScreenId { get; set; }

        /// <summary>The screen library rules pick targets from.</summary>
        [JsonProperty("screens")]
        public List<LegacyScreen> Screens { get; set; } = new List<LegacyScreen>();
    }

    /// <summary>
    /// Overrides what feeds one ITM parameter slot. The firmware's slots are fixed —
    /// customization is only which value feeds a param id and how it is formatted.
    /// </summary>
    public class FieldMapping
    {
        /// <summary>Where the value comes from (built-in field or SimHub property).</summary>
        [JsonProperty("source")]
        public PropertySpec Source { get; set; }

        /// <summary>Opaque format key, or null for the parameter's default. The format
        /// layer is a later piece; this stays an uninterpreted string until it exists.</summary>
        [JsonProperty("format")]
        public string Format { get; set; }
    }
}
