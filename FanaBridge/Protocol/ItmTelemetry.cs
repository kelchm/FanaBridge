using System;
using System.Collections.Generic;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// ITM telemetry page. Each page is a fixed parameter layout the firmware
    /// renders; SPEED and GEAR appear on every page as persistent headers. Values
    /// match the Base/BME page numbering in the protocol reference, "ITM Page Layouts".
    /// The numbering is Base/BME-specific — a Bentley display has no Car Settings page
    /// and a different legacy page (5, not 6), so this enum does not map to a Bentley.
    /// </summary>
    public enum ItmPage : byte
    {
        LapInfo = 1,
        FuelErsDrs = 2,
        CarSettings = 3,
        LapTimes = 4,
        TyreTemps = 5,
        /// <summary>Legacy / default fallback — carries no telemetry parameters.</summary>
        Legacy = 6,
    }

    /// <summary>
    /// ITM parameter IDs (the firmware's telemetry vocabulary). Only the subset
    /// used by the built-in page layouts is defined here; see the protocol
    /// reference, "ITM Parameter IDs", for the full list.
    /// </summary>
    public static class ItmParam
    {
        // Vehicle telemetry (1–84)
        public const ushort Speed = 1;
        public const ushort Rpm = 2;
        public const ushort RpmMax = 3;
        public const ushort Gear = 4;
        public const ushort Fuel = 5;
        public const ushort FuelMax = 6;
        public const ushort FuelPerLap = 7;
        public const ushort ErsLevel = 9;
        public const ushort DrsZone = 14;
        public const ushort DrsActive = 15;
        public const ushort AbsSetting = 18;
        public const ushort TcSetting = 20;
        public const ushort BrakeBias = 25;
        public const ushort EngineMapping = 26;
        public const ushort OilTemp = 33;
        public const ushort TyreFlTemp = 42;
        public const ushort TyreFrTemp = 45;
        public const ushort TyreRlTemp = 48;
        public const ushort TyreRrTemp = 51;

        // Race / timing (501–536)
        public const ushort Position = 501;
        public const ushort Lap = 505;
        public const ushort LapTime = 509;
        public const ushort LastLapTime = 510;
        public const ushort BestLapTime = 511;
        public const ushort DeltaOwnBest = 516;
        public const ushort CarAhead = 519;
        public const ushort CarBehind = 520;

        /// <summary>Sentinel that unsubscribes a slot.</summary>
        public const ushort Unsubscribe = 0xFFFF;
    }

    /// <summary>
    /// One entry from a firmware ITM subscription report (col03-IN). The firmware
    /// dictates which parameter sits at which handle for the current page; the host
    /// echoes values back at the same handle. <see cref="IsUnsubscribe"/> marks a slot
    /// the firmware is dropping (paramId 0xFFFF).
    /// </summary>
    public readonly struct ItmSubscription
    {
        /// <summary>Host handle (firmware handle with the 0x80 slot-marker bit cleared).</summary>
        public byte Handle { get; }

        /// <summary>Subscribed parameter ID, or 0xFFFF for an unsubscribe.</summary>
        public ushort ParamId { get; }

        public bool IsUnsubscribe => ParamId == ItmParam.Unsubscribe;

        public ItmSubscription(byte firmwareHandle, ushort paramId)
        {
            Handle = (byte)(firmwareHandle & 0x7F);
            ParamId = paramId;
        }
    }

    /// <summary>
    /// Wire-side ITM protocol vocabulary: the per-page parameter <b>catalog</b> (which
    /// parameter IDs a page carries, in order) and firmware subscription-report parsing.
    /// This is pure wire — no SimHub <c>GameData</c>. The SimHub telemetry → value/suffix
    /// mapping lives in <c>ItmTelemetryMapper</c> (Adapters), which knows both sides.
    ///
    /// The catalog is the firmware's page structure. Today only the Base/BME layout is
    /// modelled (via <see cref="ItmPage"/>); per-device profiles (Bentley/GTSWX/Base) are a
    /// follow-up — this is the single place the layouts are declared.
    /// </summary>
    public static class ItmTelemetry
    {
        // ── Page catalog (paramId order per page, matching the official-software captures) ──
        // Handles are assigned sequentially (0..N-1) in this order.

        private static readonly IReadOnlyList<ushort> LapInfoParams = Array.AsReadOnly(new ushort[]
            { ItmParam.Speed, ItmParam.Gear, ItmParam.Lap, ItmParam.Position, ItmParam.LapTime, ItmParam.LastLapTime });

        private static readonly IReadOnlyList<ushort> FuelErsDrsParams = Array.AsReadOnly(new ushort[]
            { ItmParam.Speed, ItmParam.Gear, ItmParam.Fuel, ItmParam.ErsLevel, ItmParam.DrsZone, ItmParam.DrsActive, ItmParam.DeltaOwnBest });

        // Order (handles 2..6) matches the official-software capture: TC, ABS, EngineMap, OilTemp, BrakeBias.
        private static readonly IReadOnlyList<ushort> CarSettingsParams = Array.AsReadOnly(new ushort[]
            { ItmParam.Speed, ItmParam.Gear, ItmParam.TcSetting, ItmParam.AbsSetting, ItmParam.EngineMapping, ItmParam.OilTemp, ItmParam.BrakeBias });

        private static readonly IReadOnlyList<ushort> LapTimesParams = Array.AsReadOnly(new ushort[]
            { ItmParam.Speed, ItmParam.Gear, ItmParam.LastLapTime, ItmParam.BestLapTime, ItmParam.CarAhead, ItmParam.CarBehind });

        // Order (handles 2..5) matches the official-software capture: FL, RL, FR, RR.
        private static readonly IReadOnlyList<ushort> TyreTempsParams = Array.AsReadOnly(new ushort[]
            { ItmParam.Speed, ItmParam.Gear, ItmParam.TyreFlTemp, ItmParam.TyreRlTemp, ItmParam.TyreFrTemp, ItmParam.TyreRrTemp });

        private static readonly IReadOnlyList<ushort> NoParams = Array.AsReadOnly(new ushort[0]);

        /// <summary>
        /// The ordered parameter IDs that make up a page — for building the ParamDefs slot
        /// layout and the cold-start seed. Empty for <see cref="ItmPage.Legacy"/>.
        /// </summary>
        public static IReadOnlyList<ushort> ParamsFor(ItmPage page)
        {
            switch (page)
            {
                case ItmPage.LapInfo: return LapInfoParams;
                case ItmPage.FuelErsDrs: return FuelErsDrsParams;
                case ItmPage.CarSettings: return CarSettingsParams;
                case ItmPage.LapTimes: return LapTimesParams;
                case ItmPage.TyreTemps: return TyreTempsParams;
                default: return NoParams;   // Legacy / unknown: no parameters
            }
        }

        /// <summary>
        /// Parses a firmware ITM subscription report (col03-IN, <c>FF 05 01</c>) into its
        /// entries. Each entry is <c>[deviceId][fwHandle][paramId-LE][dataType]</c> — the leading
        /// byte is the display-device id (which display the entry is for), not a marker; the host
        /// handle is <c>fwHandle &amp; 0x7F</c> and <c>paramId == 0xFFFF</c> means unsubscribe.
        /// Returns an empty list if the report carries no recognizable entries.
        /// </summary>
        public static IReadOnlyList<ItmSubscription> ParseSubscriptionReport(byte[] report, int len)
        {
            var result = new List<ItmSubscription>();
            if (report == null) return result;
            if (len <= 0 || len > report.Length) len = report.Length;

            // Find the FF 05 01 header (tolerate a leading report-ID in the first bytes).
            int start = -1;
            for (int i = 0; i <= 2 && i + 3 <= len; i++)
                if (report[i] == 0xFF && report[i + 1] == 0x05 && report[i + 2] == 0x01)
                {
                    start = i + 3;
                    break;
                }
            if (start < 0) return result;

            // Entries: [deviceId][fwHandle][idLo][idHi][dataType], 5 bytes each. Byte 0 is the
            // display-device id. We only drive the PBME/GTSWX (device 3), so stop at the first
            // entry for any other display (base/Bentley multi-device support isn't wired up yet).
            for (int i = start; i + 5 <= len; i += 5)
            {
                if (report[i] != 0x03) break;   // device 3 = PBME/GTSWX (the only display we handle)
                byte fwHandle = report[i + 1];
                ushort pid = (ushort)(report[i + 2] | (report[i + 3] << 8));
                result.Add(new ItmSubscription(fwHandle, pid));
            }
            return result;
        }
    }
}
