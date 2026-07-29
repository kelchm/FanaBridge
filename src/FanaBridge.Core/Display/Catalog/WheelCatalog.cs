using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Display.Catalog
{
    /// <summary>
    /// Per-wheel catalog document (shipped data, not user config): ITM pages/fields,
    /// segment capability, screen commands, and announced-format seeds. Tolerant shape —
    /// extension data everywhere, tri-state capability booleans as nullable bool.
    /// <para>
    /// catalogVersion 2: field definitions live once under <see cref="ItmCatalogSection.Fields"/>;
    /// pages carry <see cref="CatalogPage.Placements"/> that reference logical field ids.
    /// Reach is derived from placements — never a stored recurring flag.
    /// </para>
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

    /// <summary>
    /// ITM half of a wheel catalog: legacy index, field definitions, page placements,
    /// transition timings.
    /// </summary>
    public class ItmCatalogSection
    {
        /// <summary>On-wire index of the Legacy page (6 on PBME, 5 on Bentley).</summary>
        [JsonProperty("legacyPageIndex")]
        public int? LegacyPageIndex { get; set; }

        /// <summary>
        /// Field definitions keyed by stable logical id (<see cref="CatalogFieldDefinition.Id"/>).
        /// One entry per logical field on this wheel — identity, param binding, labels,
        /// capability envelope. Page-specific layout lives in placements.
        /// </summary>
        [JsonProperty("fields")]
        public List<CatalogFieldDefinition> Fields { get; set; } = new List<CatalogFieldDefinition>();

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

        /// <summary>
        /// Placements on this page in firmware param order. Each references a logical field
        /// id defined in <see cref="ItmCatalogSection.Fields"/>.
        /// </summary>
        [JsonProperty("placements")]
        public List<CatalogFieldPlacement> Placements { get; set; }
            = new List<CatalogFieldPlacement>();

        /// <summary>Members this build does not recognize, preserved verbatim for
        /// round-trips.</summary>
        [JsonExtensionData]
        public IDictionary<string, JToken> ExtensionData { get; set; }
    }

    /// <summary>
    /// One logical field definition for a wheel: identity, param binding, labels, and
    /// capability envelope. Not page-specific — placements carry region / primaryHost.
    /// </summary>
    public class CatalogFieldDefinition
    {
        /// <summary>Stable logical field id (camelCase token: speed, gear, lap, …).</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>Firmware param id (protocol constant).</summary>
        [JsonProperty("paramId")]
        public ushort ParamId { get; set; }

        [JsonProperty("shortCode")]
        public string ShortCode { get; set; }

        [JsonProperty("displayLabel")]
        public string DisplayLabel { get; set; }

        [JsonProperty("firmwareLabel")]
        public string FirmwareLabel { get; set; }

        /// <summary>
        /// Definition-level copy hint only — reach is derived from placements, never from
        /// this flag.
        /// </summary>
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

    /// <summary>
    /// One (page × field) placement: logical field reference, region, and page-specific
    /// markers (<c>primaryHost</c>).
    /// </summary>
    public class CatalogFieldPlacement
    {
        /// <summary>Logical field id — resolves against <see cref="ItmCatalogSection.Fields"/>.</summary>
        [JsonProperty("field")]
        public string Field { get; set; }

        [JsonProperty("region")]
        public FieldRegion Region { get; set; }

        /// <summary>Designated bring-up host for this param on this wheel. Exactly one
        /// per param per wheel is required; zero/multiple → flag degraded-visible.</summary>
        [JsonProperty("primaryHost")]
        public bool? PrimaryHost { get; set; }

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

    /// <summary>
    /// Catalog navigation helpers: definition lookup, placement walk, and pure reach
    /// derivation (placements → host page list). No dual-shape reader — catalogVersion 2 only.
    /// </summary>
    public static class CatalogFields
    {
        /// <summary>
        /// Build a logical-id → definition index for <paramref name="catalog"/>.
        /// First definition wins on duplicate ids.
        /// </summary>
        public static IReadOnlyDictionary<string, CatalogFieldDefinition> IndexByLogicalId(
            WheelCatalog catalog)
        {
            var map = new Dictionary<string, CatalogFieldDefinition>(StringComparer.Ordinal);
            var fields = catalog?.Itm?.Fields;
            if (fields == null)
                return map;
            for (int i = 0; i < fields.Count; i++)
            {
                var d = fields[i];
                if (d == null || string.IsNullOrEmpty(d.Id))
                    continue;
                if (!map.ContainsKey(d.Id))
                    map[d.Id] = d;
            }
            return map;
        }

        /// <summary>
        /// Build a paramId → definition index. First definition wins on duplicate params.
        /// </summary>
        public static IReadOnlyDictionary<ushort, CatalogFieldDefinition> IndexByParamId(
            WheelCatalog catalog)
        {
            var map = new Dictionary<ushort, CatalogFieldDefinition>();
            var fields = catalog?.Itm?.Fields;
            if (fields == null)
                return map;
            for (int i = 0; i < fields.Count; i++)
            {
                var d = fields[i];
                if (d == null)
                    continue;
                if (!map.ContainsKey(d.ParamId))
                    map[d.ParamId] = d;
            }
            return map;
        }

        /// <summary>Resolve a logical field id to its definition, or null.</summary>
        public static CatalogFieldDefinition FindDefinition(WheelCatalog catalog, string logicalId)
        {
            if (catalog?.Itm?.Fields == null || string.IsNullOrEmpty(logicalId))
                return null;
            for (int i = 0; i < catalog.Itm.Fields.Count; i++)
            {
                var d = catalog.Itm.Fields[i];
                if (d != null && string.Equals(d.Id, logicalId, StringComparison.Ordinal))
                    return d;
            }
            return null;
        }

        /// <summary>Resolve a param id to its definition, or null.</summary>
        public static CatalogFieldDefinition FindDefinitionByParam(
            WheelCatalog catalog, ushort paramId)
        {
            if (catalog?.Itm?.Fields == null)
                return null;
            for (int i = 0; i < catalog.Itm.Fields.Count; i++)
            {
                var d = catalog.Itm.Fields[i];
                if (d != null && d.ParamId == paramId)
                    return d;
            }
            return null;
        }

        /// <summary>
        /// Param ids placed on a catalog page, in placement order (firmware param order).
        /// Unresolvable logical ids are skipped.
        /// </summary>
        public static List<ushort> ParamsOnPage(WheelCatalog catalog, string catalogPageId)
        {
            var list = new List<ushort>();
            var pages = catalog?.Itm?.Pages;
            if (pages == null || string.IsNullOrEmpty(catalogPageId))
                return list;
            var defs = IndexByLogicalId(catalog);
            for (int i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                if (page == null
                    || !string.Equals(page.Id, catalogPageId, StringComparison.Ordinal))
                    continue;
                if (page.Placements == null)
                    break;
                for (int p = 0; p < page.Placements.Count; p++)
                {
                    var pl = page.Placements[p];
                    if (pl == null || string.IsNullOrEmpty(pl.Field))
                        continue;
                    if (defs.TryGetValue(pl.Field, out var def) && def != null)
                        list.Add(def.ParamId);
                }
                break;
            }
            return list;
        }

        /// <summary>
        /// Reach derivation: catalog page ids that place <paramref name="logicalId"/>.
        /// Pure derivation from placements — nothing in the user document declares reach.
        /// </summary>
        public static IReadOnlyList<string> HostPageIds(WheelCatalog catalog, string logicalId)
        {
            var hosts = new List<string>();
            if (catalog?.Itm?.Pages == null || string.IsNullOrEmpty(logicalId))
                return hosts;
            for (int i = 0; i < catalog.Itm.Pages.Count; i++)
            {
                var page = catalog.Itm.Pages[i];
                if (page?.Placements == null || string.IsNullOrEmpty(page.Id))
                    continue;
                for (int p = 0; p < page.Placements.Count; p++)
                {
                    var pl = page.Placements[p];
                    if (pl != null
                        && string.Equals(pl.Field, logicalId, StringComparison.Ordinal))
                    {
                        if (!hosts.Contains(page.Id))
                            hosts.Add(page.Id);
                        break;
                    }
                }
            }
            return hosts;
        }

        /// <summary>
        /// Reach counts for UI copy: placed host pages vs total ITM pages on the wheel.
        /// Returns false when the logical id has no placements (or catalog is empty).
        /// </summary>
        public static bool TryGetReach(
            WheelCatalog catalog, string logicalId, out int placed, out int total)
        {
            placed = 0;
            total = catalog?.Itm?.Pages?.Count ?? 0;
            if (string.IsNullOrEmpty(logicalId) || total == 0)
                return false;
            var hosts = HostPageIds(catalog, logicalId);
            placed = hosts.Count;
            return placed > 0;
        }

        /// <summary>
        /// Reach counts by param id (via definition table). False when the param is
        /// not defined / not placed.
        /// </summary>
        public static bool TryGetReachByParam(
            WheelCatalog catalog, ushort paramId, out int placed, out int total)
        {
            placed = 0;
            total = catalog?.Itm?.Pages?.Count ?? 0;
            var def = FindDefinitionByParam(catalog, paramId);
            if (def == null || string.IsNullOrEmpty(def.Id))
                return false;
            return TryGetReach(catalog, def.Id, out placed, out total);
        }

        /// <summary>
        /// Logical id bound to <paramref name="paramId"/> in this catalog, or null.
        /// </summary>
        public static string LogicalIdForParam(WheelCatalog catalog, ushort paramId)
            => FindDefinitionByParam(catalog, paramId)?.Id;
    }
}
