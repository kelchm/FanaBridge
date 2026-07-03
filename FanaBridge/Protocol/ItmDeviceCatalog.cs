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
    /// The ITM page set each display device offers — keyed by wire device id. This is what a UI
    /// reads to know which pages are valid/available (e.g. to populate a "default page" picker),
    /// and it is the single place the per-device layouts are declared.
    ///
    /// The page <b>content</b> reuses the Base/BME param lists (<see cref="ItmTelemetry.ParamsFor"/>);
    /// devices differ only in which pages they expose and their numbering:
    /// <list type="bullet">
    /// <item>Base display (1) and wheel OLED / PBME / GTSWX (3) share the standard six pages.</item>
    /// <item>Bentley (4) has no Car Settings page and renumbers the rest to a contiguous 1–5.</item>
    /// </list>
    /// (GTSWX shares wire id 3 with the PBME; its only real difference — a compact Lap Times page —
    /// is a parameter-level detail the firmware-driven path handles on its own, so it needs no
    /// separate page set here.)
    /// </summary>
    public static class ItmDeviceCatalog
    {
        // Standard six-page set: base display, wheel OLED (PBME / GTSWX).
        private static readonly IReadOnlyList<ItmPageInfo> Standard = new[]
        {
            new ItmPageInfo(1, "Lap Info",         ItmTelemetry.ParamsFor(ItmPage.LapInfo)),
            new ItmPageInfo(2, "Fuel / ERS / DRS", ItmTelemetry.ParamsFor(ItmPage.FuelErsDrs)),
            new ItmPageInfo(3, "Car Settings",     ItmTelemetry.ParamsFor(ItmPage.CarSettings)),
            new ItmPageInfo(4, "Lap Times",        ItmTelemetry.ParamsFor(ItmPage.LapTimes)),
            new ItmPageInfo(5, "Tyre Temps",       ItmTelemetry.ParamsFor(ItmPage.TyreTemps)),
            new ItmPageInfo(6, "Legacy",           ItmTelemetry.ParamsFor(ItmPage.Legacy)),
        };

        // Bentley: no Car Settings; the remaining pages renumber to a contiguous 1–5.
        private static readonly IReadOnlyList<ItmPageInfo> Bentley = new[]
        {
            new ItmPageInfo(1, "Lap Info",         ItmTelemetry.ParamsFor(ItmPage.LapInfo)),
            new ItmPageInfo(2, "Fuel / ERS / DRS", ItmTelemetry.ParamsFor(ItmPage.FuelErsDrs)),
            new ItmPageInfo(3, "Lap Times",        ItmTelemetry.ParamsFor(ItmPage.LapTimes)),
            new ItmPageInfo(4, "Tyre Temps",       ItmTelemetry.ParamsFor(ItmPage.TyreTemps)),
            new ItmPageInfo(5, "Legacy",           ItmTelemetry.ParamsFor(ItmPage.Legacy)),
        };

        /// <summary>
        /// The pages a display device offers, in order, for the given wire
        /// <paramref name="deviceId"/> (see <see cref="ItmDevice"/>). Unknown ids fall back to the
        /// standard set. The list is shared/immutable — do not mutate.
        /// </summary>
        public static IReadOnlyList<ItmPageInfo> PagesFor(byte deviceId)
        {
            switch (deviceId)
            {
                case (byte)ItmDevice.Bentley: return Bentley;
                case (byte)ItmDevice.Base:            // base display
                case (byte)ItmDevice.BmeOrGtswx:      // wheel OLED (PBME / GTSWX)
                default:
                    return Standard;
            }
        }
    }
}
