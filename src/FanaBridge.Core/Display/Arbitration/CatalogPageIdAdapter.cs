using System;
using System.Collections.Generic;
using FanaBridge.Display.Catalog;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Arbitration
{
    /// <summary>
    /// Explicit <see cref="ItmPage"/> → catalog page id adapter (E7 / E8 assembly).
    /// CatalogPage.Id is the EnumText camelCase spelling (lapInfo, tyreTemps, …).
    /// Dormant until E8 wires director ManualNavigation into SeatManualInput.
    /// </summary>
    public static class CatalogPageIdAdapter
    {
        /// <summary>
        /// Map a director <see cref="ItmPage"/> to a catalog page id (EnumText camelCase).
        /// Returns null for unknown / unmapped values.
        /// </summary>
        public static string FromItmPage(ItmPage page)
        {
            switch (page)
            {
                case ItmPage.LapInfo: return "lapInfo";
                case ItmPage.FuelErsDrs: return "fuelErsDrs";
                case ItmPage.CarSettings: return "carSettings";
                case ItmPage.LapTimes: return "lapTimes";
                case ItmPage.TyreTemps: return "tyreTemps";
                case ItmPage.Legacy: return "legacy";
                default: return null;
            }
        }

        /// <summary>
        /// Destination id for a cataloged manual adopt: <c>itm:{catalogPageId}</c>.
        /// Null when the page has no catalog spelling.
        /// </summary>
        public static string ToDestinationId(ItmPage page)
        {
            string id = FromItmPage(page);
            return id == null ? null : DestinationIds.Itm(id);
        }

        /// <summary>
        /// Assert every <see cref="ItmPage"/> in the device table maps to a catalog page
        /// present in <paramref name="catalog"/>. Returns missing identities (empty = ok).
        /// </summary>
        public static IReadOnlyList<ItmPage> MissingFromCatalog(
            ItmPageTable table, WheelCatalog catalog)
        {
            var missing = new List<ItmPage>();
            if (table?.Pages == null)
                return missing;

            var catalogIds = new HashSet<string>(StringComparer.Ordinal);
            var pages = catalog?.Itm?.Pages;
            if (pages != null)
            {
                foreach (var p in pages)
                {
                    if (p?.Id != null)
                        catalogIds.Add(p.Id);
                }
            }

            foreach (var info in table.Pages)
            {
                string id = FromItmPage(info.Page);
                if (id == null || !catalogIds.Contains(id))
                    missing.Add(info.Page);
            }
            return missing;
        }
    }
}
