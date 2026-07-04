using System.Collections.Generic;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// One page in an ITM device's page set: its on-wire page number, a human-readable name
    /// (for UI page pickers), and the parameters it carries. Pure wire/reference data — no SimHub.
    /// </summary>
    public sealed class ItmPageInfo
    {
        /// <summary>On-wire page number (the value sent in a <c>FF 05 04</c> PageSet).</summary>
        public byte Number { get; }

        /// <summary>Human-readable page name, e.g. "Lap Info" / "Tyre Temps".</summary>
        public string Name { get; }

        /// <summary>Parameter IDs this page carries, in order (empty for the legacy page).</summary>
        public IReadOnlyList<ushort> Params { get; }

        /// <summary>True for the legacy/fallback page — no telemetry parameters.</summary>
        public bool IsLegacy => Params.Count == 0;

        public ItmPageInfo(byte number, string name, IReadOnlyList<ushort> parameters)
        {
            Number = number;
            Name = name;
            Params = parameters;
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
        // dedicated set below.
        private static readonly IReadOnlyList<ItmPageInfo> Standard = new[]
        {
            new ItmPageInfo(1, "Lap Info",         ItmTelemetry.ParamsFor(ItmPage.LapInfo)),
            new ItmPageInfo(2, "Fuel / ERS / DRS", ItmTelemetry.ParamsFor(ItmPage.FuelErsDrs)),
            new ItmPageInfo(3, "Car Settings",     ItmTelemetry.ParamsFor(ItmPage.CarSettings)),
            new ItmPageInfo(4, "Lap Times",        ItmTelemetry.ParamsFor(ItmPage.LapTimes)),
            new ItmPageInfo(5, "Tyre Temps",       ItmTelemetry.ParamsFor(ItmPage.TyreTemps)),
            new ItmPageInfo(6, "Legacy",           ItmTelemetry.ParamsFor(ItmPage.Legacy)),
        };

        // Bentley GT3 (device 4): no Car Settings; the remaining pages renumber to a contiguous 1–5.
        private static readonly IReadOnlyList<ItmPageInfo> Bentley = new[]
        {
            new ItmPageInfo(1, "Lap Info",         ItmTelemetry.ParamsFor(ItmPage.LapInfo)),
            new ItmPageInfo(2, "Fuel / ERS / DRS", ItmTelemetry.ParamsFor(ItmPage.FuelErsDrs)),
            new ItmPageInfo(3, "Lap Times",        ItmTelemetry.ParamsFor(ItmPage.LapTimes)),
            new ItmPageInfo(4, "Tyre Temps",       ItmTelemetry.ParamsFor(ItmPage.TyreTemps)),
            new ItmPageInfo(5, "Legacy",           ItmTelemetry.ParamsFor(ItmPage.Legacy)),
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
