using System;
using System.Collections.Generic;
using FanaBridge.Core.Display.Protocol;
using GameReaderCommon;

namespace FanaBridge.Display.Drivers
{
    /// <summary>
    /// Maps SimHub <see cref="GameData"/> telemetry to encoded <see cref="ItmValue"/> entries
    /// and display suffixes for the ITM display. This is the <b>device-agnostic</b> half of ITM:
    /// a parameter encodes from the telemetry frame the same way regardless of which display
    /// shows it, so a single flat <c>paramId → encoder</c> registry serves every device.
    ///
    /// The wire-side vocabulary — the page catalog (which params a page carries) and
    /// subscription-report parsing — lives in <see cref="ItmTelemetry"/> (Protocol, no SimHub).
    /// This class knows both sides (wire <c>paramId</c> + SimHub <c>GameData</c>), which is why
    /// it belongs in Display.Drivers (plugin), not Protocol (Core). It holds no wire framing and
    /// no state — the pure, per-frame translation step, the ITM analogue of <c>FanatecDisplayDriver</c>'s reads.
    /// </summary>
    public static class ItmTelemetryMapper
    {
        // ── Typed encoder builders ───────────────────────────────────────
        // Each returns an encoder: read + encode one parameter from a frame at a handle.
        private static Func<StatusDataBase, byte, ItmValue> U8(ushort id, Func<StatusDataBase, byte> sel)
            => (d, h) => ItmValue.UInt8(h, id, sel(d));

        private static Func<StatusDataBase, byte, ItmValue> I16(ushort id, Func<StatusDataBase, short> sel)
            => (d, h) => ItmValue.Int16(h, id, sel(d));

        private static Func<StatusDataBase, byte, ItmValue> I32(ushort id, Func<StatusDataBase, int> sel)
            => (d, h) => ItmValue.Int32(h, id, sel(d));

        private static Func<StatusDataBase, byte, ItmValue> F32(ushort id, Func<StatusDataBase, float> sel)
            => (d, h) => ItmValue.Float32(h, id, sel(d));

        private static Func<StatusDataBase, byte, ItmValue> Str(ushort id, Func<StatusDataBase, string> sel)
            => (d, h) => ItmValue.Ascii(h, id, sel(d));

        // Flat paramId -> encoder registry. Device-agnostic: any subscribed parameter, on any
        // display, encodes from the same frame the same way. The Protocol page catalog
        // (ItmTelemetry.ParamsFor) says which params a page carries; this says how to encode one.
        // A guard test asserts every catalog param has an entry here (see HasEncoder).
        private static readonly Dictionary<ushort, Func<StatusDataBase, byte, ItmValue>> Registry = BuildRegistry();

        private static Dictionary<ushort, Func<StatusDataBase, byte, ItmValue>> BuildRegistry()
            => new Dictionary<ushort, Func<StatusDataBase, byte, ItmValue>>
            {
                // SPEED + GEAR head every page. SpeedLocal honours the user's km/h vs mph choice,
                // matching the 7-seg driver.
                [ItmParam.Speed] = I16(ItmParam.Speed, d => ClampSpeed(d.SpeedLocal)),
                [ItmParam.Gear] = U8(ItmParam.Gear, d => EncodeGear(d.Gear)),

                // Lap Info
                [ItmParam.Lap] = U8(ItmParam.Lap, d => ClampByte(d.CurrentLap)),
                [ItmParam.Position] = U8(ItmParam.Position, d => ClampByte(d.Position)),
                [ItmParam.LapTime] = F32(ItmParam.LapTime, d => Seconds(d.CurrentLapTime)),
                [ItmParam.LastLapTime] = F32(ItmParam.LastLapTime, d => Seconds(d.LastLapTime)),

                // Fuel / ERS / DRS
                // Round each displayed float to its field's precision on the host: the firmware
                // renders a decimal field as whole.round(frac*10^N) with NO carry, so an
                // unrounded value just below a boundary misrenders (16.9692 -> "16.10"). The
                // official app pre-rounds the same way. Fuel = 1 dp; delta/gaps = 2 dp; time
                // fields truncate in firmware and are left unrounded.
                [ItmParam.Fuel] = F32(ItmParam.Fuel, d => (float)Math.Round(d.Fuel, 1)),
                [ItmParam.ErsLevel] = I32(ItmParam.ErsLevel, d => SafeRound(d.ERSPercent)),
                [ItmParam.DrsZone] = U8(ItmParam.DrsZone, d => (byte)(d.DRSAvailable != 0 ? 1 : 0)),
                [ItmParam.DrsActive] = U8(ItmParam.DrsActive, d => (byte)(d.DRSEnabled != 0 ? 1 : 0)),
                [ItmParam.DeltaOwnBest] = F32(ItmParam.DeltaOwnBest, d => (float)Math.Round(d.DeltaToSessionBest ?? 0.0, 2)),

                // Car Settings
                [ItmParam.TcSetting] = U8(ItmParam.TcSetting, d => ClampByte(d.TCLevel)),
                [ItmParam.AbsSetting] = U8(ItmParam.AbsSetting, d => ClampByte(d.ABSLevel)),
                // ENGINE_MAPPING is ASCII text on the wire — map 10 travels as "10", not 0x0A.
                // Sending the numeric byte wedges the firmware (verified via official capture).
                [ItmParam.EngineMapping] = Str(ItmParam.EngineMapping, d => EngineMapText(d.EngineMap)),
                [ItmParam.OilTemp] = U8(ItmParam.OilTemp, d => ClampByte(d.OilTemperature)),
                // BRAKE_BIAS is Int32 tenths of a percent (512 = 51.2%); see BrakeBiasTenths.
                [ItmParam.BrakeBias] = I32(ItmParam.BrakeBias, d => BrakeBiasTenths(d.BrakeBias)),

                // Lap Times
                [ItmParam.BestLapTime] = F32(ItmParam.BestLapTime, d => Seconds(d.BestLapTime)),
                // Car ahead is shown as a negative gap (you're behind them); car behind positive.
                [ItmParam.CarAhead] = F32(ItmParam.CarAhead, d => -(float)Math.Round(NearestGap(d.OpponentsAheadOnTrack), 2)),
                [ItmParam.CarBehind] = F32(ItmParam.CarBehind, d => (float)Math.Round(NearestGap(d.OpponentsBehindOnTrack), 2)),

                // Tyre Temps
                [ItmParam.TyreFlTemp] = U8(ItmParam.TyreFlTemp, d => ClampByte(d.TyreTemperatureFrontLeft)),
                [ItmParam.TyreRlTemp] = U8(ItmParam.TyreRlTemp, d => ClampByte(d.TyreTemperatureRearLeft)),
                [ItmParam.TyreFrTemp] = U8(ItmParam.TyreFrTemp, d => ClampByte(d.TyreTemperatureFrontRight)),
                [ItmParam.TyreRrTemp] = U8(ItmParam.TyreRrTemp, d => ClampByte(d.TyreTemperatureRearRight)),
            };

        /// <summary>
        /// Whether this parameter has a value encoder. Used by the catalog guard test to prove
        /// every param a page can carry (<see cref="ItmTelemetry.ParamsFor"/>) is encodable.
        /// </summary>
        public static bool HasEncoder(ushort paramId) => Registry.ContainsKey(paramId);

        // ── Suffixes ─────────────────────────────────────────────────────

        // Temperatures whose value carries a unit label. SimHub delivers both the converted
        // value AND its unit in the same frame (StatusDataBase.TemperatureUnit), so we read the
        // label from that snapshot — no out-of-band settings lookup, always consistent with the
        // value. (Fuel is NOT unit-labeled; it uses a "/capacity" total, with the unit only as a
        // no-capacity fallback.)
        private static readonly HashSet<ushort> TempParams = new HashSet<ushort>
        {
            ItmParam.OilTemp, ItmParam.TyreFlTemp, ItmParam.TyreFrTemp,
            ItmParam.TyreRlTemp, ItmParam.TyreRrTemp,
        };

        /// <summary>
        /// The unit suffix a temperature's value should display (single char, e.g. "C"/"F"/"K"),
        /// or false if the parameter isn't a temperature. Read from the frame's
        /// <c>TemperatureUnit</c> so it stays consistent with the already-converted value.
        /// </summary>
        public static bool TryGetUnitSuffix(ushort paramId, GameData data, out string suffix)
        {
            if (TempParams.Contains(paramId)) { suffix = UnitLabel(data?.NewData?.TemperatureUnit, "C"); return true; }
            suffix = null;
            return false;
        }

        /// <summary>The fuel unit as a single-char label (e.g. "L"/"G"), from the frame's
        /// <c>FuelUnit</c>. Used only as a fallback when no tank capacity is available.</summary>
        public static string FuelUnitLabel(GameData data) => UnitLabel(data?.NewData?.FuelUnit, "L");

        // A unit string's first letter, uppercased — a single char for the display's tight space,
        // robust to formats like "C" / "°C" / "Celsius" / "gal". Falls back when empty.
        private static string UnitLabel(string raw, string fallback)
        {
            if (!string.IsNullOrEmpty(raw))
                foreach (var c in raw)
                    if (char.IsLetter(c)) return char.ToUpperInvariant(c).ToString();
            return fallback;
        }

        /// <summary>Whether a parameter carries a "/total" suffix (lap, position, or fuel/capacity).</summary>
        public static bool IsTotalParam(ushort paramId)
            => paramId == ItmParam.Lap || paramId == ItmParam.Position || paramId == ItmParam.Fuel;

        /// <summary>
        /// The "/total" suffix for a parameter that has one — lap of total laps ("/34"),
        /// position of field size ("/20"), fuel of tank capacity ("/23") — computed from the
        /// current telemetry. The firmware cannot know these, so the host supplies them.
        ///
        /// Returns false (no suffix) when the parameter has no total, there is no telemetry
        /// frame, or the game does not report a plausible total — i.e. the total must be present
        /// and at least the current value. This drops misleading "/0" / "/2" suffixes from games
        /// (e.g. Forza Horizon) that don't expose a race structure, while real races still show
        /// the total.
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
                case ItmParam.Fuel:
                    // Fuel of tank capacity (e.g. "/90"), in the user's SimHub unit. Suppress
                    // when no capacity is reported so we never show a bare "/0".
                    if (s.MaxFuel > 0 && s.MaxFuel >= s.Fuel)
                        suffix = "/" + (int)Math.Round(s.MaxFuel);
                    return suffix != null;
                default:
                    return false;
            }
        }

        // ── Value encoding ───────────────────────────────────────────────

        /// <summary>
        /// Encodes a single subscribed parameter's current value at <paramref name="handle"/>,
        /// for the firmware-driven path. Returns false when there is no telemetry frame or the
        /// parameter has no known encoder.
        /// </summary>
        public static bool TryEncodeParam(ushort paramId, byte handle, GameData data, out ItmValue value)
            => TryEncodeParam(paramId, handle, data, 0, out value);

        /// <summary>
        /// Encodes a single subscribed parameter honouring the firmware's declared slot type
        /// (<paramref name="dataType"/>, from the subscription push; 0 = unknown). The type
        /// matters for GEAR, whose wire form differs per display: a PBME declares u8 (0x12)
        /// and ignores ASCII, while a Formula V3 takes ASCII chars ('n', '1'..'9', 'r') — both
        /// hardware/capture-verified against the official software. Other parameters encode
        /// the same regardless.
        /// </summary>
        public static bool TryEncodeParam(ushort paramId, byte handle, GameData data, byte dataType, out ItmValue value)
        {
            value = default;
            var status = data?.NewData;
            if (status == null)
                return false;

            if (paramId == ItmParam.Gear && ItmTelemetry.IsTextType(dataType))
            {
                value = ItmValue.Ascii(handle, ItmParam.Gear, GearText(status.Gear));
                return true;
            }

            if (!Registry.TryGetValue(paramId, out var encode))
                return false;
            value = encode(status, handle);
            return true;
        }

        /// <summary>
        /// Encodes the current telemetry for <paramref name="page"/> into value entries.
        /// Handles are assigned <paramref name="handleBase"/>..+N-1 in the page's catalog order
        /// (<see cref="ItmTelemetry.ParamsFor"/>). Returns an empty list when there is no
        /// telemetry frame or the page carries no parameters.
        /// </summary>
        public static IReadOnlyList<ItmValue> BuildValues(ItmPage page, GameData data, byte handleBase = 0)
        {
            var status = data?.NewData;
            var ids = ItmTelemetry.ParamsFor(page);
            if (status == null || ids.Count == 0)
                return Array.Empty<ItmValue>();

            var values = new ItmValue[ids.Count];
            for (int i = 0; i < ids.Count; i++)
                values[i] = Registry[ids[i]](status, (byte)(handleBase + i));
            return values;
        }

        // ── Encoding helpers ─────────────────────────────────────────────

        private static float Seconds(TimeSpan t) => (float)t.TotalSeconds;

        // The nearest on-track gap (seconds) among a set of opponents, or 0 if none / unknown.
        // SimHub gives no scalar gap-to-car-ahead/behind, so take the smallest
        // |RelativeGapToPlayer| from the ahead/behind list (robust to list ordering).
        private static float NearestGap(IEnumerable<Opponent> opponents)
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

        // Round a possibly non-finite value to int, mapping NaN/Infinity to 0. A NaN would
        // otherwise slip through range comparisons (all false) into an undefined cast — the same
        // firmware-safety concern BrakeBiasTenths guards against.
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

        // BRAKE_BIAS is an Int32 in tenths of a percent (confirmed by capture: 51.2% => 512).
        // SimHub reports a percentage, so send round(percent * 10), clamped to [0, 1000]
        // (0–100.0%).
        private static int BrakeBiasTenths(double percent)
        {
            if (double.IsNaN(percent) || double.IsInfinity(percent)) return 0;
            int v = (int)Math.Round(percent * 10.0);
            if (v < 0) return 0;
            if (v > 1000) return 1000;
            return v;
        }

        // ENGINE_MAPPING is rendered by the firmware as text (e.g. "1", "10"). SimHub reports the
        // map as an int; send its decimal string. Clamp to [0, 99] so the payload stays within
        // two ASCII bytes (the range the official software was observed to use).
        private static string EngineMapText(int map)
        {
            if (map < 0) map = 0;
            if (map > 99) map = 99;
            return map.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Reverse sentinel: -1 as a Uint8, which the firmware renders as "r"
        /// (hardware-verified on a PBME under clean single-writer conditions).
        /// </summary>
        private const byte GearReverse = 0xFF;

        /// <summary>
        /// Parses SimHub's gear string to the ITM Uint8 gear value: "N"/empty = 0, "1".."9" =
        /// that number, "R" = <see cref="GearReverse"/>. Forward gears are literal, confirmed
        /// against official-software PBME captures (N=0, gears 1..4 literal). Used when the
        /// firmware declares GEAR as a numeric slot (or the type is unknown); text-declared
        /// displays take <see cref="GearText"/> instead.
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

        /// <summary>
        /// Parses SimHub's gear string to the ITM ASCII gear form used by text-declared
        /// displays (e.g. Formula V3): lowercase "n" for neutral, lowercase "r" for reverse,
        /// forward gears as their decimal digits — exactly the characters the official
        /// software puts on the wire (capture-verified: 'n', '5'..'1', 'r').
        /// </summary>
        private static string GearText(string gear)
        {
            if (string.IsNullOrEmpty(gear))
                return "n";

            gear = gear.Trim().ToUpperInvariant();
            if (gear == "R" || gear == "REVERSE") return "r";
            if (gear == "N" || gear == "NEUTRAL") return "n";

            return int.TryParse(gear, out int g) && g >= 1 && g <= 99
                ? g.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "n";
        }
    }
}
