using System;
using System.Collections.Generic;
using FanaBridge.Display.Arbitration;
using FanaBridge.Protocol;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Display.Schema2
{
    /// <summary>
    /// Spec §9b — the ONE shipped migration: pre-epic display settings → a native v2
    /// document. Bake-on-sight, marker-stamped, idempotent. Zero v1-type dependency:
    /// reads pre-epic scalars only and writes v2 POCOs.
    /// </summary>
    public static class PreEpicSettingsMigrator
    {
        /// <summary>Document ExtensionData key for the bake-on-sight marker.</summary>
        public const string MarkerKey = "migratedFrom";

        /// <summary>Marker value written on every successful bake.</summary>
        public const string MarkerValue = "preEpicSettings";

        /// <summary>
        /// When <paramref name="existingV2"/> is non-null, returns it unchanged (never
        /// overwrites an authored or previously baked v2 document). Otherwise bakes a
        /// fresh v2 document from pre-epic scalars and stamps <see cref="MarkerKey"/>.
        /// Never throws; unresolvable page ids omit rest and log.
        /// </summary>
        public static DisplayConfigV2 Apply(
            DisplayConfigV2 existingV2,
            string displayControl,
            byte itmDefaultPage,
            byte itmDeviceId = 0,
            Action<string> log = null)
        {
            if (existingV2 != null)
                return existingV2;

            return Bake(displayControl, itmDefaultPage, itmDeviceId, log);
        }

        /// <summary>
        /// Builds a new v2 document from pre-epic settings. Always stamps the marker
        /// (bake-on-sight — even when rest is omitted for an unresolvable page).
        /// </summary>
        public static DisplayConfigV2 Bake(
            string displayControl,
            byte itmDefaultPage,
            byte itmDeviceId = 0,
            Action<string> log = null)
        {
            var doc = new DisplayConfigV2();
            doc.Settings = doc.Settings ?? new SettingsBlock();
            doc.Settings.Mode = MapControlToMode(displayControl);

            if (TryResolveItmPage(itmDefaultPage, itmDeviceId, out string catalogPageId))
            {
                if (doc.Priority == null)
                    doc.Priority = new PriorityLadder();
                if (doc.Priority.Rest == null)
                    doc.Priority.Rest = new RestBlock();

                doc.Priority.Rest.InSessionPage = new PageRef
                {
                    Kind = PageRefKind.ItmPage,
                    CatalogPageId = catalogPageId,
                };
            }
            else
            {
                SafeLog(log,
                    "PreEpicSettingsMigrator: itmDefaultPage " + itmDefaultPage
                    + " is not in this device's catalog (deviceId " + itmDeviceId
                    + ") — rest.inSessionPage omitted");
            }

            StampMarker(doc);
            return doc;
        }

        /// <summary>True when the document carries the §9b bake-on-sight marker.</summary>
        public static bool HasMarker(DisplayConfigV2 doc)
        {
            if (doc?.ExtensionData == null)
                return false;
            if (!doc.ExtensionData.TryGetValue(MarkerKey, out JToken token) || token == null
                || token.Type == JTokenType.Null)
                return false;
            return string.Equals(token.Type == JTokenType.String
                    ? (string)token
                    : token.ToString(),
                MarkerValue, StringComparison.Ordinal);
        }

        /// <summary>
        /// Pre-epic <c>displayControl</c> tri-state → v2 <c>settings.mode</c>.
        /// Itm → on · Legacy → legacyOnly · Off → off · anything else → on.
        /// </summary>
        internal static SettingsMode MapControlToMode(string displayControl)
        {
            if (string.Equals(displayControl, "Legacy", StringComparison.OrdinalIgnoreCase))
                return SettingsMode.LegacyOnly;
            if (string.Equals(displayControl, "Off", StringComparison.OrdinalIgnoreCase))
                return SettingsMode.Off;
            // Itm, absent, and unknown all land on "on" (codec default for ITM-capable).
            return SettingsMode.On;
        }

        /// <summary>
        /// Resolves a pre-epic wire page number against <see cref="ItmDeviceCatalog"/>
        /// into a catalog page id (e.g. wire 1 → <c>lapInfo</c>). False when the wire
        /// is not on this device's set or has no catalog spelling.
        /// </summary>
        internal static bool TryResolveItmPage(
            byte itmDefaultPage, byte itmDeviceId, out string catalogPageId)
        {
            catalogPageId = null;
            var pages = ItmDeviceCatalog.PagesFor(itmDeviceId);
            if (pages == null)
                return false;

            for (int i = 0; i < pages.Count; i++)
            {
                var info = pages[i];
                if (info == null || info.Number != itmDefaultPage)
                    continue;

                catalogPageId = CatalogPageIdAdapter.FromItmPage(info.Page);
                return !string.IsNullOrEmpty(catalogPageId);
            }
            return false;
        }

        private static void StampMarker(DisplayConfigV2 doc)
        {
            if (doc.ExtensionData == null)
                doc.ExtensionData = new Dictionary<string, JToken>(StringComparer.Ordinal);
            doc.ExtensionData[MarkerKey] = MarkerValue;
        }

        private static void SafeLog(Action<string> log, string message)
        {
            if (log == null) return;
            try { log(message); }
            catch
            {
                // Logger failures must not surface from the migrator.
            }
        }
    }
}
