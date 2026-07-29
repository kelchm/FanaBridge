using System.Collections.Generic;
using FanaBridge.Display.Catalog;

namespace FanaBridge.Display.Composition
{
    /// <summary>
    /// Per-param capability envelope for field-ladder gating (§14). Built from the catalog
    /// (E0); null tri-state members mean untested — warn, do not gate. A param
    /// <b>absent</b> from the map is a different case (not on this wheel) — see
    /// FrameComposer missing-capability split.
    /// </summary>
    public sealed class FieldCapability
    {
        public ushort ParamId { get; set; }

        /// <summary>Whether the field accepts a suffix write. Null = untested.</summary>
        public bool? SuffixSupported { get; set; }

        /// <summary>Max suffix character width when known. Null = untested / unlimited.</summary>
        public int? SuffixWidth { get; set; }

        /// <summary>Whether the value region accepts numeric content. Null = untested.</summary>
        public bool? ValueNumeric { get; set; }

        /// <summary>Whether the value region accepts ASCII/text content. Null = untested.</summary>
        public bool? ValueAscii { get; set; }

        /// <summary>
        /// Whether overrides are allowed on this param (catalog <c>overridable</c>).
        /// Null = untested; false = envelope lock (e.g. gear on PBME) — child inert,
        /// degrade-visible. Capability facts live in catalog DATA, never per-field code law.
        /// </summary>
        public bool? Overridable { get; set; }

        /// <summary>
        /// Catalog page ids that host this param (derived reach from placements). Empty when
        /// unknown — presence then keys only on primary host when supplied.
        /// </summary>
        public IReadOnlyList<string> HostCatalogPageIds { get; set; }

        /// <summary>
        /// Primary-host catalog page id for multi-host membership / presence helpers.
        /// Destination identity for the composed record uses the shared
        /// <see cref="FrameComposerOptions.PrimaryHostByParam"/> map (same source as E4),
        /// not this field alone.
        /// </summary>
        public string PrimaryHostCatalogPageId { get; set; }

        /// <summary>
        /// Index catalog placements into a param → capability map. Definitions supply
        /// capability booleans; placements supply host lists and primaryHost markers.
        /// Duplicate param appearances merge host lists; primaryHost prefers the first
        /// true marker; capability booleans take the first non-null observation.
        /// </summary>
        public static IReadOnlyDictionary<ushort, FieldCapability> FromCatalog(WheelCatalog catalog)
        {
            var map = new Dictionary<ushort, FieldCapability>();
            if (catalog?.Itm?.Pages == null)
                return map;

            var defs = CatalogFields.IndexByLogicalId(catalog);

            foreach (var page in catalog.Itm.Pages)
            {
                if (page?.Placements == null || string.IsNullOrEmpty(page.Id))
                    continue;
                foreach (var placement in page.Placements)
                {
                    if (placement == null || string.IsNullOrEmpty(placement.Field))
                        continue;
                    if (!defs.TryGetValue(placement.Field, out var def) || def == null)
                        continue;

                    if (!map.TryGetValue(def.ParamId, out var cap))
                    {
                        cap = new FieldCapability
                        {
                            ParamId = def.ParamId,
                            HostCatalogPageIds = new List<string>(),
                        };
                        map[def.ParamId] = cap;
                    }

                    var hosts = (List<string>)cap.HostCatalogPageIds;
                    if (!hosts.Contains(page.Id))
                        hosts.Add(page.Id);

                    if (placement.PrimaryHost == true
                        && string.IsNullOrEmpty(cap.PrimaryHostCatalogPageId))
                        cap.PrimaryHostCatalogPageId = page.Id;

                    if (def.Overridable != null && cap.Overridable == null)
                        cap.Overridable = def.Overridable;

                    if (def.Suffix != null)
                    {
                        if (cap.SuffixSupported == null)
                            cap.SuffixSupported = def.Suffix.Supported;
                        if (cap.SuffixWidth == null)
                            cap.SuffixWidth = def.Suffix.Width;
                    }
                    if (def.Value != null)
                    {
                        if (cap.ValueNumeric == null)
                            cap.ValueNumeric = def.Value.Numeric;
                        if (cap.ValueAscii == null)
                            cap.ValueAscii = def.Value.Ascii;
                    }
                }
            }

            return map;
        }

        /// <summary>
        /// Build the single primary-host map both E4 (<c>SeatArbiterOptions.PrimaryHostByParam</c>)
        /// and E5 (<see cref="FrameComposerOptions.PrimaryHostByParam"/>) consume — one source,
        /// no second map.
        /// </summary>
        public static IReadOnlyDictionary<ushort, string> PrimaryHostMapFromCapabilities(
            IReadOnlyDictionary<ushort, FieldCapability> capabilities)
        {
            var map = new Dictionary<ushort, string>();
            if (capabilities == null)
                return map;
            foreach (var kv in capabilities)
            {
                if (!string.IsNullOrEmpty(kv.Value?.PrimaryHostCatalogPageId))
                    map[kv.Key] = kv.Value.PrimaryHostCatalogPageId;
            }
            return map;
        }
    }

    /// <summary>Why a field override was treated as inert / soft-degraded this tick.</summary>
    public enum FieldDegradeReason
    {
        None = 0,
        /// <summary>Suffix write on a field with <c>suffix.supported == false</c>.</summary>
        SuffixNotSupported,
        /// <summary>
        /// Authored suffix exceeds catalog width — <b>runtime clamp only</b> (child still wins).
        /// Authored text is preserved on <see cref="DegradedFieldChild.AuthoredText"/>.
        /// </summary>
        SuffixWidthOverflow,
        /// <summary>Text/ASCII content into a value region with <c>value.ascii == false</c>.</summary>
        TextInNumericValue,
        /// <summary>Override disabled or load-degraded (not a capability miss).</summary>
        Inert,
        /// <summary>Unrecognized <c>writes</c> — cannot paint.</summary>
        UnknownWrites,
        /// <summary>
        /// Catalog <c>overridable: false</c> — DATA-driven lock (standing law: never a
        /// per-field code exclusion).
        /// </summary>
        ParamLocked,
        /// <summary>
        /// Content kind the field plane cannot render (outside
        /// {text, message, property-with-source}).
        /// </summary>
        UnrenderableContent,
        /// <summary>
        /// Param absent from the capability map (not on this wheel) — whole ladder inert.
        /// </summary>
        ParamNotOnWheel,
    }
}
