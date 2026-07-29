using System;
using System.Collections.Generic;
using System.Globalization;
using FanaBridge.Display.Rules;

namespace FanaBridge.Display.Catalog
{
    /// <summary>
    /// Standing law (§3a): offered formats and lockedness derive from the catalog
    /// envelope where a catalog resolves — never from per-field code exclusions.
    /// <para>
    /// With a catalog: <see cref="AnnouncedFormats"/> supplies the offered format set
    /// and definition <c>overridable:false</c> supplies the lock. Without a catalog:
    /// fall back to the <see cref="FieldFormats"/> family tables only (no lock).
    /// </para>
    /// Shared by the v1 Pages authoring surface while it lives (E9-exit) and any
    /// later consumer that needs the same envelope facts.
    /// </summary>
    public static class FieldEnvelope
    {
        /// <summary>
        /// Formats offered for authoring on <paramref name="paramId"/>. Catalog
        /// announce wins when present; without a catalog the format-family tables
        /// are the only fallback.
        /// </summary>
        public static IReadOnlyList<string> OfferedFormats(
            WheelCatalog catalog, ushort paramId)
        {
            if (catalog != null)
            {
                var byParam = catalog.AnnouncedFormats?.ByParam;
                if (byParam != null)
                {
                    string key = paramId.ToString(CultureInfo.InvariantCulture);
                    if (byParam.TryGetValue(key, out var list) && list != null && list.Count > 0)
                        return list;
                }
                // Catalog resolved but this param has no announce seed — empty offer
                // (do not invent options from code tables when envelope data exists).
                return Array.Empty<string>();
            }

            return FieldFormats.AllowedFor(paramId);
        }

        /// <summary>
        /// Whether <paramref name="format"/> is in the envelope-offered set for
        /// <paramref name="paramId"/> (case-sensitive camelCase vocabulary).
        /// </summary>
        public static bool IsFormatAllowed(
            WheelCatalog catalog, ushort paramId, string format)
        {
            if (string.IsNullOrEmpty(format))
                return false;
            var offered = OfferedFormats(catalog, paramId);
            for (int i = 0; i < offered.Count; i++)
            {
                if (string.Equals(offered[i], format, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Envelope lock: catalog definition <c>overridable: false</c>. Without a
        /// catalog there is no envelope lock (standing law — no code exclusion).
        /// </summary>
        public static bool IsLocked(WheelCatalog catalog, ushort paramId)
        {
            if (catalog == null)
                return false;
            var def = CatalogFields.FindDefinitionByParam(catalog, paramId);
            return def != null && def.Overridable == false;
        }
    }
}
