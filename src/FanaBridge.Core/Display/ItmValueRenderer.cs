using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FanaBridge.Protocol;

namespace FanaBridge.Display
{
    /// <summary>
    /// Renders an encoded <see cref="ItmValue"/> (plus the ASCII suffix the driver sent
    /// with it, when any) to the display string the firmware puts on the OLED — so a
    /// host-side preview shows exactly what the hardware shows for the bytes that went
    /// on the wire. The per-parameter formats reproduce the official quick guide's page
    /// renders, quirks included: <c>15 /73</c> laps with a space before the slash-total,
    /// zero-padded <c>02 /20</c> position, oil temp <c>098C</c> zero-padded with NO
    /// space before its unit while tire temps get <c>075 C</c> WITH one, brake bias with
    /// a comma decimal (<c>56,4%</c>), signed gap/delta seconds with an <c>s</c> suffix,
    /// and <c>mm:ss.mmm</c> lap times. Where the guide shows no example, the nearest
    /// observed convention is used (noted per case below).
    /// </summary>
    public static class ItmValueRenderer
    {
        /// <summary>The filled dot the display shows for an active DRS flag.</summary>
        public const string DrsDotOn = "●";    // ●

        /// <summary>The hollow dot the display shows for an inactive DRS flag.</summary>
        public const string DrsDotOff = "○";   // ○

        /// <summary>
        /// The display string for one encoded parameter value. <paramref name="suffix"/>
        /// is the ASCII suffix the driver last sent for the slot (a "/total", a unit
        /// letter, or null/blank for none — a blank " " is the driver's active clear and
        /// renders as no suffix). <paramref name="dataType"/> is the firmware's declared
        /// slot type from the subscription push; it matters only for GEAR, whose wire
        /// form differs per display (ASCII text vs numeric u8).
        /// </summary>
        public static string Render(ushort paramId, ItmValue value, string suffix = null, byte dataType = 0)
        {
            suffix = NormalizeSuffix(suffix);
            switch (paramId)
            {
                case ItmParam.Speed:
                    return AsInt(value).ToString(CultureInfo.InvariantCulture);

                case ItmParam.Gear:
                    return RenderGear(value, dataType);

                // Laps: "15 /73" — unpadded value, space before the slash-total. (The
                // guide zero-pads only the position field.)
                case ItmParam.Lap:
                    return WithSpacedSuffix(AsInt(value).ToString(CultureInfo.InvariantCulture), suffix);

                // Position: "02 /20" — zero-padded to two digits.
                case ItmParam.Position:
                    return WithSpacedSuffix(AsInt(value).ToString("D2", CultureInfo.InvariantCulture), suffix);

                case ItmParam.LapTime:
                case ItmParam.LastLapTime:
                case ItmParam.BestLapTime:
                    return RenderLapTime(AsFloat(value));

                // Fuel: "49.0 /106L" — space before the slash-capacity. The field always
                // prints its single fraction digit (hardware-observed), so a whole level
                // renders "49.0" even where the guide's page render shows "49".
                case ItmParam.Fuel:
                    return WithSpacedSuffix(RenderFuel(AsFloat(value)), suffix);

                // ERS: "55%" — the display appends the percent itself (no suffix is sent).
                case ItmParam.ErsLevel:
                    return AsInt(value).ToString(CultureInfo.InvariantCulture) + "%";

                case ItmParam.DrsZone:
                case ItmParam.DrsActive:
                    return AsInt(value) != 0 ? DrsDotOn : DrsDotOff;

                // Delta + gaps: "-0.49s" / "0.92s" / "-6.42s" — signed, period decimal,
                // two decimals. The delta field's precision is hardware-observed at two
                // (the guide's page render shows a third digit the field never prints),
                // which is also why the driver pre-rounds every wire delta/gap to 2 dp.
                case ItmParam.DeltaOwnBest:
                case ItmParam.CarAhead:
                case ItmParam.CarBehind:
                    return AsFloat(value).ToString("0.00", CultureInfo.InvariantCulture) + "s";

                // TC / ABS: "08" / "12" — zero-padded to two digits.
                case ItmParam.TcSetting:
                case ItmParam.AbsSetting:
                    return AsInt(value).ToString("D2", CultureInfo.InvariantCulture);

                // Engine map travels as ASCII text and renders verbatim ("3", "10").
                case ItmParam.EngineMapping:
                    return AsAscii(value);

                // Oil temp: "098C" — zero-padded three digits, NO space before the unit.
                case ItmParam.OilTemp:
                    return AsInt(value).ToString("D3", CultureInfo.InvariantCulture) + (suffix ?? "");

                // Tire temps: "075 C" — zero-padded three digits, WITH a space.
                case ItmParam.TyreFlTemp:
                case ItmParam.TyreFrTemp:
                case ItmParam.TyreRlTemp:
                case ItmParam.TyreRrTemp:
                    return WithSpacedSuffix(AsInt(value).ToString("D3", CultureInfo.InvariantCulture), suffix);

                // Brake bias: Int32 tenths of a percent, comma decimal — 564 → "56,4%".
                case ItmParam.BrakeBias:
                {
                    int tenths = AsInt(value);
                    string sign = tenths < 0 ? "-" : "";
                    tenths = Math.Abs(tenths);
                    return sign + (tenths / 10) + "," + (tenths % 10) + "%";
                }

                // Outside the built-in page layouts: a best-effort numeric render (the
                // guide shows no such field).
                default:
                    return AsInt(value).ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// The dash placeholder a field shows before any value has been sent (and after a
        /// DisplayReset): laps/position/fuel show <c>--- / -</c>, times <c>--:--.-</c> —
        /// both hardware-observed; the rest use the nearest convention (plain dashes, a
        /// single dash for gear, a hollow dot for a DRS flag).
        /// </summary>
        public static string Placeholder(ushort paramId)
        {
            switch (paramId)
            {
                case ItmParam.Lap:
                case ItmParam.Position:
                case ItmParam.Fuel:
                    return "--- / -";
                case ItmParam.LapTime:
                case ItmParam.LastLapTime:
                case ItmParam.BestLapTime:
                    return "--:--.-";
                case ItmParam.Gear:
                    return "-";
                case ItmParam.DrsZone:
                case ItmParam.DrsActive:
                    return DrsDotOff;
                default:
                    return "---";
            }
        }

        // ── Per-family renderers ─────────────────────────────────────────

        // GEAR renders per the wire form the display declared: ASCII displays receive
        // 'n' / '1'..'9' / 'r' and show them; numeric displays receive a u8 where 0 is
        // neutral and 0xFF reverse. Both render as the display's uppercase N/R glyphs.
        private static string RenderGear(ItmValue value, byte dataType)
        {
            if (ItmTelemetry.IsTextType(dataType))
            {
                string text = AsAscii(value);
                if (text == "n") return "N";
                if (text == "r") return "R";
                return text;
            }
            int gear = AsInt(value) & 0xFF;
            if (gear == 0) return "N";
            if (gear == 0xFF) return "R";
            return gear.ToString(CultureInfo.InvariantCulture);
        }

        // Lap times render as mm:ss.mmm ("01:36.911"). Milliseconds are resolved by
        // TRUNCATING the float32 seconds — the time fields truncate rather than round
        // (hardware-observed; the same reason the driver leaves them unrounded), so a
        // wire value sitting just below a decimal millisecond drops it: float32(51.196)
        // = 51.19599915 renders "00:51.195".
        private static string RenderLapTime(float seconds)
        {
            if (seconds < 0f) seconds = 0f;
            long totalMs = (long)((double)seconds * 1000.0);
            long minutes = totalMs / 60_000;
            long rest = totalMs % 60_000;
            return minutes.ToString("D2", CultureInfo.InvariantCulture) + ":"
                + (rest / 1000).ToString("D2", CultureInfo.InvariantCulture) + "."
                + (rest % 1000).ToString("D3", CultureInfo.InvariantCulture);
        }

        // Fuel always renders the field's single decimal ("49.0", "49.3") — the display
        // prints the fraction digit unconditionally, whole levels included
        // (hardware-observed).
        private static string RenderFuel(float value)
            => value.ToString("0.0", CultureInfo.InvariantCulture);

        // ── Decoding helpers (little-endian Raw bits, per ItmValue.Size) ─

        // Sign-extends the raw bits by their wire width (u8 slots are non-negative on
        // every built-in field, so width-1 reads unsigned).
        private static int AsInt(ItmValue value)
        {
            switch (value.Size)
            {
                case 1: return (byte)value.Raw;
                case 2: return unchecked((short)value.Raw);
                default: return unchecked((int)value.Raw);
            }
        }

        private static float AsFloat(ItmValue value)
        {
            if (value.Size != 4)
                return AsInt(value);
            var u = new FloatUnion { U = value.Raw };
            float f = u.F;
            return float.IsNaN(f) || float.IsInfinity(f) ? 0f : f;
        }

        private static string AsAscii(ItmValue value)
        {
            int len = Math.Min((int)value.Size, 4);
            var sb = new StringBuilder(len);
            for (int i = 0; i < len; i++)
                sb.Append((char)((value.Raw >> (8 * i)) & 0xFF));
            return sb.ToString();
        }

        // Value + " " + suffix ("15 /73", "49.0 /106L", "075 C"); no trailing space when
        // there is no suffix.
        private static string WithSpacedSuffix(string value, string suffix)
            => suffix == null ? value : value + " " + suffix;

        // A null/empty/whitespace suffix (including the driver's blank " " clear)
        // renders as no suffix at all.
        private static string NormalizeSuffix(string suffix)
            => string.IsNullOrWhiteSpace(suffix) ? null : suffix;

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatUnion
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public uint U;
        }
    }
}
