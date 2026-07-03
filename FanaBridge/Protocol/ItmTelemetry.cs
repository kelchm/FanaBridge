using System;
using System.Collections.Generic;
using GameReaderCommon;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// ITM telemetry page. Each page is a fixed parameter layout the firmware
    /// renders; SPEED and GEAR appear on every page as persistent headers. Values
    /// match the Base/BME page numbering in the protocol reference, "ITM Page Layouts".
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
    /// Maps SimHub <see cref="GameData"/> telemetry to the encoded
    /// <see cref="ItmValue"/> entries for a given <see cref="ItmPage"/>, ready to
    /// hand to <see cref="ItmEncoder.SendValues"/>.
    ///
    /// This is the pure, per-frame translation step — the ITM analogue of
    /// <c>FanatecDisplayDriver</c>'s telemetry reads. It holds no state and does
    /// no I/O; page selection, keepalive scheduling, and the enable/ParamDefs
    /// sequence belong to the (future) ITM display driver above it.
    ///
    /// Handles are assigned sequentially (0..N-1) in page-field order. That mirrors
    /// a straightforward ParamDefs slot layout; if the on-hardware slot/handle
    /// mapping (protocol reference, "Slot &amp; Handle Mapping") proves to need
    /// specific handle ranges per page, this is the single place to change it.
    /// </summary>
    public static class ItmTelemetry
    {
        // A single page field: its parameter ID plus how to read+encode it from
        // a telemetry frame for a given handle.
        private sealed class Field
        {
            public readonly ushort ParamId;
            private readonly Func<StatusDataBase, byte, ItmValue> _encode;

            public Field(ushort paramId, Func<StatusDataBase, byte, ItmValue> encode)
            {
                ParamId = paramId;
                _encode = encode;
            }

            public ItmValue Encode(StatusDataBase data, byte handle) => _encode(data, handle);
        }

        // ── Typed field builders ─────────────────────────────────────────
        private static Field U8(ushort id, Func<StatusDataBase, byte> sel)
            => new Field(id, (d, h) => ItmValue.UInt8(h, id, sel(d)));

        private static Field I16(ushort id, Func<StatusDataBase, short> sel)
            => new Field(id, (d, h) => ItmValue.Int16(h, id, sel(d)));

        private static Field I32(ushort id, Func<StatusDataBase, int> sel)
            => new Field(id, (d, h) => ItmValue.Int32(h, id, sel(d)));

        private static Field F32(ushort id, Func<StatusDataBase, float> sel)
            => new Field(id, (d, h) => ItmValue.Float32(h, id, sel(d)));

        private static Field Str(ushort id, Func<StatusDataBase, string> sel)
            => new Field(id, (d, h) => ItmValue.Ascii(h, id, sel(d)));

        // SPEED + GEAR head every page (protocol reference, "ITM Page Layouts").
        // SpeedLocal honours the user's km/h vs mph choice, matching the 7-seg driver.
        private static readonly Field SpeedField = I16(ItmParam.Speed, d => ClampSpeed(d.SpeedLocal));
        private static readonly Field GearField = U8(ItmParam.Gear, d => EncodeGear(d.Gear));
        // Shared: appears on both the Lap Info and Lap Times pages.
        private static readonly Field LastLapTimeField = F32(ItmParam.LastLapTime, d => Seconds(d.LastLapTime));

        // ── Page layouts ─────────────────────────────────────────────────
        private static readonly Field[] LapInfo =
        {
            SpeedField,
            GearField,
            U8(ItmParam.Lap, d => ClampByte(d.CurrentLap)),
            U8(ItmParam.Position, d => ClampByte(d.Position)),
            F32(ItmParam.LapTime, d => Seconds(d.CurrentLapTime)),
            LastLapTimeField,
        };

        private static readonly Field[] FuelErsDrs =
        {
            SpeedField,
            GearField,
            F32(ItmParam.Fuel, d => (float)d.Fuel),
            I32(ItmParam.ErsLevel, d => SafeRound(d.ERSPercent)),
            U8(ItmParam.DrsZone, d => (byte)(d.DRSAvailable != 0 ? 1 : 0)),
            U8(ItmParam.DrsActive, d => (byte)(d.DRSEnabled != 0 ? 1 : 0)),
            F32(ItmParam.DeltaOwnBest, d => (float)(d.DeltaToSessionBest ?? 0.0)),
        };

        // Field order (handles 2..6) matches the official-software capture:
        // TC, ABS, EngineMap, OilTemp, BrakeBias.
        private static readonly Field[] CarSettings =
        {
            SpeedField,
            GearField,
            U8(ItmParam.TcSetting, d => ClampByte(d.TCLevel)),
            U8(ItmParam.AbsSetting, d => ClampByte(d.ABSLevel)),
            // ENGINE_MAPPING is ASCII text on the wire — map 10 travels as "10", not 0x0A.
            // Sending the numeric byte wedges the firmware (verified via official capture).
            Str(ItmParam.EngineMapping, d => EngineMapText(d.EngineMap)),
            U8(ItmParam.OilTemp, d => ClampByte(d.OilTemperature)),
            // BRAKE_BIAS is Int32 tenths of a percent (512 = 51.2%); see BrakeBiasTenths.
            I32(ItmParam.BrakeBias, d => BrakeBiasTenths(d.BrakeBias)),
        };

        private static readonly Field[] LapTimes =
        {
            SpeedField,
            GearField,
            LastLapTimeField,
            F32(ItmParam.BestLapTime, d => Seconds(d.BestLapTime)),
            // Car ahead is shown as a negative gap (you're behind them); car behind positive.
            F32(ItmParam.CarAhead, d => -NearestGap(d.OpponentsAheadOnTrack)),
            F32(ItmParam.CarBehind, d => NearestGap(d.OpponentsBehindOnTrack)),
        };

        // Field order (handles 2..5) matches the official-software capture:
        // FL, RL, FR, RR (left column, then right column).
        private static readonly Field[] TyreTemps =
        {
            SpeedField,
            GearField,
            U8(ItmParam.TyreFlTemp, d => ClampByte(d.TyreTemperatureFrontLeft)),
            U8(ItmParam.TyreRlTemp, d => ClampByte(d.TyreTemperatureRearLeft)),
            U8(ItmParam.TyreFrTemp, d => ClampByte(d.TyreTemperatureFrontRight)),
            U8(ItmParam.TyreRrTemp, d => ClampByte(d.TyreTemperatureRearRight)),
        };

        private static readonly Field[] Empty = new Field[0];

        // Flat paramId -> encoder registry, built from every page's fields. Lets the
        // firmware-driven path encode a value for any subscribed parameter ID without
        // caring which page it belongs to.
        private static readonly Dictionary<ushort, Field> ByParam = BuildRegistry();

        // Parameters that wedge the PBME firmware when sent — never put them on the wire.
        // The firmware may still subscribe them (so the field shows dashes), but sending a
        // value locks up the display.
        //
        // Currently empty: ENGINE_MAPPING used to live here because sending it as a numeric
        // byte froze the Car Settings page. A USBPcap of the official software revealed it is
        // ASCII text (map "10" = bytes '1','0'), so it is now sent via Str(...) instead of
        // being suppressed. Keep the set for any future param that proves unsendable.
        private static readonly HashSet<ushort> Unsupported = new HashSet<ushort>();

        private static Dictionary<ushort, Field> BuildRegistry()
        {
            var map = new Dictionary<ushort, Field>();
            foreach (var page in new[] { LapInfo, FuelErsDrs, CarSettings, LapTimes, TyreTemps })
                foreach (var f in page)
                    if (!map.ContainsKey(f.ParamId))
                        map[f.ParamId] = f;
            return map;
        }

        private static Field[] FieldsFor(ItmPage page)
        {
            switch (page)
            {
                case ItmPage.LapInfo: return LapInfo;
                case ItmPage.FuelErsDrs: return FuelErsDrs;
                case ItmPage.CarSettings: return CarSettings;
                case ItmPage.LapTimes: return LapTimes;
                case ItmPage.TyreTemps: return TyreTemps;
                default: return Empty;   // Legacy / unknown: no parameters
            }
        }

        // ── Public API ───────────────────────────────────────────────────

        /// <summary>
        /// The ordered parameter IDs that make up a page. Use to build the
        /// ParamDefs slot layout. Empty for <see cref="ItmPage.Legacy"/>.
        /// </summary>
        public static IReadOnlyList<ushort> ParamsFor(ItmPage page)
        {
            var fields = FieldsFor(page);
            var ids = new ushort[fields.Length];
            for (int i = 0; i < fields.Length; i++)
                ids[i] = fields[i].ParamId;
            return ids;
        }

        // ASCII unit suffixes the display appends to a parameter's value, sent via
        // ParamDefs. Matches the official software's suffixes from the capture
        // ('C' on temperatures, 'L' on fuel). Temperatures assume Celsius.
        private static readonly Dictionary<ushort, string> UnitSuffixes = new Dictionary<ushort, string>
        {
            { ItmParam.OilTemp, "C" },
            { ItmParam.TyreFlTemp, "C" },
            { ItmParam.TyreFrTemp, "C" },
            { ItmParam.TyreRlTemp, "C" },
            { ItmParam.TyreRrTemp, "C" },
            { ItmParam.Fuel, "L" },
        };

        /// <summary>
        /// The ASCII unit suffix a parameter's value should display (e.g. "C" for a
        /// temperature), or false if it has none. Used to build ParamDefs entries.
        /// </summary>
        public static bool TryGetUnitSuffix(ushort paramId, out string suffix)
            => UnitSuffixes.TryGetValue(paramId, out suffix);

        /// <summary>Whether a parameter can carry a dynamic "/total" suffix (lap, position).</summary>
        public static bool IsTotalParam(ushort paramId)
            => paramId == ItmParam.Lap || paramId == ItmParam.Position;

        /// <summary>
        /// The dynamic "/total" suffix for a parameter that has one — lap of total laps
        /// ("/34") and position of field size ("/20") — computed from the current
        /// telemetry. The firmware cannot know these, so the host supplies them.
        ///
        /// Returns false (no suffix) when the parameter has no total, there is no
        /// telemetry frame, or the game does not report a plausible total — i.e. the
        /// total must be present and at least the current value. This drops misleading
        /// "/0" / "/2" suffixes from games (e.g. Forza Horizon) that don't expose a race
        /// structure, while real races still show the total.
        /// </summary>
        public static bool TryGetTotalSuffix(ushort paramId, GameData data, out string suffix)
        {
            suffix = null;
            var s = data?.NewData;
            if (s == null) return false;

            switch (paramId)
            {
                case ItmParam.Lap:
                    if (s.TotalLaps > 0 && s.TotalLaps >= s.CurrentLap)
                        suffix = "/" + s.TotalLaps;
                    return suffix != null;
                case ItmParam.Position:
                    int field = s.OpponentsCount;   // SimHub's opponents list already includes the player
                    if (field > 1 && field >= s.Position)
                        suffix = "/" + field;
                    return suffix != null;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Encodes a single subscribed parameter's current value at <paramref name="handle"/>,
        /// for the firmware-driven path. Returns false when there is no telemetry frame or
        /// the parameter has no known encoder.
        /// </summary>
        public static bool TryEncodeParam(ushort paramId, byte handle, GameData data, out ItmValue value)
        {
            value = default;
            if (Unsupported.Contains(paramId))
                return false;   // never send — wedges the firmware
            var status = data?.NewData;
            if (status == null || !ByParam.TryGetValue(paramId, out var field))
                return false;
            value = field.Encode(status, handle);
            return true;
        }

        /// <summary>
        /// Parses a firmware ITM subscription report (col03-IN, <c>FF 05 01</c>) into its
        /// entries. Each entry is <c>[03][fwHandle][paramId-LE][unit]</c>; the host handle
        /// is <c>fwHandle &amp; 0x7F</c> and <c>paramId == 0xFFFF</c> means unsubscribe.
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

            // Entries: [03][fwHandle][idLo][idHi][unit], 5 bytes each, until a non-0x03 marker.
            for (int i = start; i + 5 <= len; i += 5)
            {
                if (report[i] != 0x03) break;
                byte fwHandle = report[i + 1];
                ushort pid = (ushort)(report[i + 2] | (report[i + 3] << 8));
                result.Add(new ItmSubscription(fwHandle, pid));
            }
            return result;
        }

        /// <summary>
        /// Encodes the current telemetry for <paramref name="page"/> into the value
        /// entries to send via <see cref="ItmEncoder.SendValues"/>. Handles are
        /// assigned <paramref name="handleBase"/>..+N-1 in field order. Returns an
        /// empty list when there is no telemetry frame or the page carries no parameters.
        /// </summary>
        public static IReadOnlyList<ItmValue> BuildValues(ItmPage page, GameData data, byte handleBase = 0)
        {
            var status = data?.NewData;
            var fields = FieldsFor(page);
            if (status == null || fields.Length == 0)
                return Array.Empty<ItmValue>();

            var values = new ItmValue[fields.Length];
            for (int i = 0; i < fields.Length; i++)
                values[i] = fields[i].Encode(status, (byte)(handleBase + i));
            return values;
        }

        // ── Encoding helpers ─────────────────────────────────────────────

        private static float Seconds(TimeSpan t) => (float)t.TotalSeconds;

        // The nearest on-track gap (seconds) among a set of opponents, or 0 if none /
        // unknown. SimHub gives no scalar gap-to-car-ahead/behind, so take the smallest
        // |RelativeGapToPlayer| from the ahead/behind list (robust to list ordering).
        private static float NearestGap(System.Collections.Generic.IEnumerable<Opponent> opponents)
        {
            if (opponents == null) return 0f;
            double best = double.MaxValue;
            foreach (var o in opponents)
            {
                double? g = o?.RelativeGapToPlayer;
                if (g.HasValue)
                {
                    double a = Math.Abs(g.Value);
                    if (a < best) best = a;
                }
            }
            return best == double.MaxValue ? 0f : (float)best;
        }

        // Round a possibly non-finite value to int, mapping NaN/Infinity to 0. A NaN
        // would otherwise slip through range comparisons (all false) into an undefined
        // cast — the same firmware-safety concern BrakeBiasTenths guards against.
        private static int SafeRound(double value)
            => double.IsNaN(value) || double.IsInfinity(value) ? 0 : (int)Math.Round(value);

        // Speed is non-negative; clamp to [0, Int16 max] (the SPEED param is Int16).
        private static short ClampSpeed(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            double v = Math.Round(value);
            if (v < 0) return 0;
            if (v > short.MaxValue) return short.MaxValue;
            return (short)v;
        }

        private static byte ClampByte(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            double v = Math.Round(value);
            if (v < 0) return 0;
            if (v > 255) return 255;
            return (byte)v;
        }

        private static byte ClampByte(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return (byte)value;
        }

        // BRAKE_BIAS is an Int32 in tenths of a percent (confirmed by capture: 51.2% =>
        // 512). SimHub reports a percentage, so send round(percent * 10), clamped to
        // [0, 1000] (0–100.0%).
        private static int BrakeBiasTenths(double percent)
        {
            if (double.IsNaN(percent) || double.IsInfinity(percent)) return 0;
            int v = (int)Math.Round(percent * 10.0);
            if (v < 0) return 0;
            if (v > 1000) return 1000;
            return v;
        }

        // ENGINE_MAPPING is rendered by the firmware as text (e.g. "1", "10"). SimHub reports
        // the map as an int; send its decimal string. Clamp to [0, 99] so the payload stays
        // within two ASCII bytes (the range the official software was observed to use).
        private static string EngineMapText(int map)
        {
            if (map < 0) map = 0;
            if (map > 99) map = 99;
            return map.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Reverse sentinel: -1 as a Uint8, which the firmware renders as "r".</summary>
        private const byte GearReverse = 0xFF;

        /// <summary>
        /// Parses SimHub's gear string to the ITM Uint8 gear value: "N"/empty = 0,
        /// "1".."9" = that number, "R" = <see cref="GearReverse"/>. Forward gears are
        /// literal, confirmed against an official-software capture (N=0, 2=2, 3=3).
        /// </summary>
        private static byte EncodeGear(string gear)
        {
            if (string.IsNullOrEmpty(gear))
                return 0;

            gear = gear.Trim().ToUpperInvariant();
            if (gear == "R" || gear == "REVERSE") return GearReverse;
            if (gear == "N" || gear == "NEUTRAL") return 0;

            return int.TryParse(gear, out int g) && g >= 0 && g <= 254 ? (byte)g : (byte)0;
        }
    }
}
