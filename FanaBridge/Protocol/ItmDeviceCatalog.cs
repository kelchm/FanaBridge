using System.Collections.Generic;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// One slot in an ITM device's page set: the on-wire page number, which page content
    /// (<see cref="ItmPage"/>) sits there, and that page's params and display name. Reference
    /// data — no SimHub. The name and params are derived from the identity, defined once.
    /// </summary>
    public sealed class ItmPageInfo
    {
        /// <summary>On-wire page number on this device (the value sent in a <c>FF 05 04</c> PageSet).</summary>
        public byte Number { get; }

        /// <summary>Which page content sits at this slot — the identity, not the wire number.</summary>
        public ItmPage Page { get; }

        /// <summary>The page's display name (reference data, from <see cref="ItmTelemetry.NameOf"/>).</summary>
        public string Name { get; }

        /// <summary>Parameter IDs this page carries, in order (empty for the legacy page).</summary>
        public IReadOnlyList<ushort> Params { get; }

        /// <summary>True for the legacy/fallback page — no telemetry parameters.</summary>
        public bool IsLegacy => Page == ItmPage.Legacy;

        public ItmPageInfo(byte number, ItmPage page)
        {
            Number = number;
            Page = page;
            Name = ItmTelemetry.NameOf(page);
            Params = ItmTelemetry.ParamsFor(page);
        }
    }

    /// <summary>
    /// The pages each ITM display offers, keyed by wire device id — what a UI reads to populate a
    /// "default page" picker and what the driver seeds from on bring-up. A device's page set is a
    /// property of its firmware; today only device 4 (the Bentley GT3) differs from the standard
    /// six-page set. Page <b>content</b> reuses the shared param lists (<see cref="ItmTelemetry.ParamsFor"/>).
    ///
    /// The page set currently tracks the device id 1:1. If two wheels ever share a device id but
    /// lay their pages out differently, that is the seam to add a per-wheel discriminator (a profile
    /// field) — until then, deriving the pages from the device id is the simplest honest model.
    /// </summary>
    public static class ItmDeviceCatalog
    {
        // Standard six-page set — base display (1), the wheel OLED (3), and any device without a
        // dedicated set below. Each slot is (wire page number, page content).
        private static readonly IReadOnlyList<ItmPageInfo> Standard = new[]
        {
            new ItmPageInfo(1, ItmPage.LapInfo),
            new ItmPageInfo(2, ItmPage.FuelErsDrs),
            new ItmPageInfo(3, ItmPage.CarSettings),
            new ItmPageInfo(4, ItmPage.LapTimes),
            new ItmPageInfo(5, ItmPage.TyreTemps),
            new ItmPageInfo(6, ItmPage.Legacy),
        };

        // Bentley GT3 (device 4): no Car Settings; the remaining pages renumber to a contiguous 1–5.
        private static readonly IReadOnlyList<ItmPageInfo> Bentley = new[]
        {
            new ItmPageInfo(1, ItmPage.LapInfo),
            new ItmPageInfo(2, ItmPage.FuelErsDrs),
            new ItmPageInfo(3, ItmPage.LapTimes),
            new ItmPageInfo(4, ItmPage.TyreTemps),
            new ItmPageInfo(5, ItmPage.Legacy),
        };

        /// <summary>
        /// The pages the given wire <paramref name="deviceId"/> offers, in order. Unknown ids fall
        /// back to the standard set. The list is shared/immutable — do not mutate.
        /// </summary>
        public static IReadOnlyList<ItmPageInfo> PagesFor(byte deviceId)
        {
            switch (deviceId)
            {
                case 4:  return Bentley;   // Bentley GT3
                default: return Standard;
            }
        }
    }
}
