using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Display.Catalog
{
    /// <summary>
    /// Per-wheel catalog document (shipped data, not user config): ITM pages/fields,
    /// segment capability, screen commands, and announced-format seeds. Tolerant shape —
    /// extension data everywhere, tri-state capability booleans as nullable bool.
    /// </summary>
    public class WheelCatalog
    {
        [JsonProperty("catalogVersion")]
        public int CatalogVersion { get; set; }

        [JsonProperty("wheelId")]
        public string WheelId { get; set; }

        [JsonProperty("displayName")]
        public string DisplayName { get; set; }

        /// <summary>File-level provisional badge. False/absent when verified.</summary>
        [JsonProperty("provisional")]
        public bool? Provisional { get; set; }

        [JsonProperty("itm")]
        public ItmCatalogSection Itm { get; set; }

        [JsonProperty("segment")]
        public SegmentCatalogSection Segment { get; set; }

        [JsonProperty("screenCommands")]
        public ScreenCommandsCapability ScreenCommands { get; set; }

        [JsonProperty("announcedFormats")]
        public AnnouncedFormats AnnouncedFormats { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>ITM half of a wheel catalog: legacy index, pages, transition timings.</summary>
    public class ItmCatalogSection
    {
        /// <summary>On-wire index of the Legacy page (6 on PBME, 5 on Bentley).</summary>
        [JsonProperty("legacyPageIndex")]
        public int? LegacyPageIndex { get; set; }

        [JsonProperty("pages")]
        public List<CatalogPage> Pages { get; set; } = new List<CatalogPage>();

        [JsonProperty("transitions")]
        public CatalogTransitions Transitions { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips (e.g. deviceId).</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>One ITM page in the catalog roster.</summary>
    public class CatalogPage
    {
        /// <summary>Catalog page identity (ItmPage EnumText spelling: lapInfo, tyreTemps, …).</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>On-wire page index on this wheel.</summary>
        [JsonProperty("index")]
        public int Index { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("provisional")]
        public bool? Provisional { get; set; }

        [JsonProperty("fields")]
        public List<CatalogField> Fields { get; set; } = new List<CatalogField>();

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>One field on a catalog page.</summary>
    public class CatalogField
    {
        [JsonProperty("fieldId")]
        public string FieldId { get; set; }

        /// <summary>Firmware param id (protocol constant).</summary>
        [JsonProperty("paramId")]
        public ushort ParamId { get; set; }

        [JsonProperty("shortCode")]
        public string ShortCode { get; set; }

        [JsonProperty("displayLabel")]
        public string DisplayLabel { get; set; }

        [JsonProperty("firmwareLabel")]
        public string FirmwareLabel { get; set; }

        [JsonProperty("region")]
        public FieldRegion Region { get; set; }

        /// <summary>Designated bring-up host for this param on this wheel. Exactly one
        /// per param per wheel is required; zero/multiple → flag degraded-visible.</summary>
        [JsonProperty("primaryHost")]
        public bool? PrimaryHost { get; set; }

        [JsonProperty("header")]
        public bool? Header { get; set; }

        [JsonProperty("overridable")]
        public bool? Overridable { get; set; }

        [JsonProperty("suffix")]
        public FieldSuffixCapability Suffix { get; set; }

        [JsonProperty("value")]
        public FieldValueCapability Value { get; set; }

        [JsonProperty("provisional")]
        public bool? Provisional { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>Layout position of a field on its page (row/column/shared + extras).</summary>
    public class FieldRegion
    {
        [JsonProperty("row")]
        public string Row { get; set; }

        [JsonProperty("column")]
        public string Column { get; set; }

        [JsonProperty("shared")]
        public bool? Shared { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>Suffix capability envelope. <see cref="Supported"/> is tri-state
    /// (true / false / null = untested).</summary>
    public class FieldSuffixCapability
    {
        [JsonProperty("supported")]
        public bool? Supported { get; set; }

        [JsonProperty("width")]
        public int? Width { get; set; }

        [JsonProperty("provisional")]
        public bool? Provisional { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>Value-region capability. Numeric/ascii are tri-state.</summary>
    public class FieldValueCapability
    {
        [JsonProperty("numeric")]
        public bool? Numeric { get; set; }

        [JsonProperty("ascii")]
        public bool? Ascii { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>ITM transition timing seeds (ms). Null = untested.</summary>
    public class CatalogTransitions
    {
        [JsonProperty("legacyEntryMs")]
        public int? LegacyEntryMs { get; set; }

        [JsonProperty("legacyExitMs")]
        public int? LegacyExitMs { get; set; }

        [JsonProperty("virtualRepaintMs")]
        public int? VirtualRepaintMs { get; set; }

        [JsonProperty("provisional")]
        public bool? Provisional { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>Segment-display capability half of the catalog.</summary>
    public class SegmentCatalogSection
    {
        [JsonProperty("present")]
        public bool? Present { get; set; }

        /// <summary>Character renderability table (glyph → renderable/awkward/no).</summary>
        [JsonProperty("charTable")]
        public Dictionary<string, string> CharTable { get; set; }

        /// <summary>Per-digit decimal support, or null when untested.</summary>
        [JsonProperty("decimalPerDigit")]
        public List<bool> DecimalPerDigit { get; set; }

        [JsonProperty("blink")]
        public bool? Blink { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips (digits, hostedOnLegacyPage, provisional, …).</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>
    /// Screen-command capability envelope. Each command is tri-state
    /// (true / false / null = untested). Key spelling <c>logoInverted</c> per
    /// SpecialCommands (shipped document spelling).
    /// </summary>
    public class ScreenCommandsCapability
    {
        [JsonProperty("logo")]
        public bool? Logo { get; set; }

        [JsonProperty("blank")]
        public bool? Blank { get; set; }

        [JsonProperty("white")]
        public bool? White { get; set; }

        [JsonProperty("logoInverted")]
        public bool? LogoInverted { get; set; }

        [JsonProperty("provisional")]
        public bool? Provisional { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>Static seed of the dynamic format envelope, keyed by param id string.</summary>
    public class AnnouncedFormats
    {
        [JsonProperty("byParam")]
        public Dictionary<string, List<string>> ByParam { get; set; }

        [JsonProperty("provisional")]
        public bool? Provisional { get; set; }

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }
}
