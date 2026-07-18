using System;
using System.Collections.Generic;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Legacy
{
    /// <summary>
    /// Pure content → text / segment formatting for legacy virtual pages. No transport,
    /// no clock, no mutation: callers inject GameData-shaped values (and, for Property,
    /// an <see cref="IPropertyReader"/>). The four absorbed <see cref="LegacyDisplayDriver"/>
    /// modes (Speed / Gear / GearAndSpeed / GearBrackets) reproduce that driver's
    /// read/render semantics exactly (SpeedLocal, clamps, ParseGear, bracket rule); the
    /// GearAndSpeed 2 s overlay is selected by the injected timestamps rather than
    /// DateTime.UtcNow.
    /// </summary>
    public static class LegacyValueFormatter
    {
        /// <summary>Duration of the post-shift gear overlay in GearAndSpeed mode —
        /// matches <c>LegacyDisplayDriver</c>'s 2 s window.</summary>
        public const int GearOverlayMs = 2000;

        // ── Per-kind formatters → display string ─────────────────────────

        /// <summary>SpeedLocal rounded and clamped 0–999, zero-padded to 3 digits so
        /// <see cref="Render"/> matches <c>DisplayEncoder.DisplaySpeed</c>.</summary>
        public static string FormatSpeed(double speedLocal)
            => Clamp999(Round(speedLocal)).ToString("D3");

        /// <summary>Parsed gear as a single centered glyph (" R ", " N ", " 3 ").</summary>
        public static string FormatGear(string gear)
            => CenterGear(ParseGear(gear));

        /// <summary>
        /// Gear for <see cref="GearOverlayMs"/> after <paramref name="gearChangedAtMs"/>,
        /// else speed — clock-injected stand-in for the driver's DateTime overlay.
        /// </summary>
        public static string FormatGearAndSpeed(
            string gear, double speedLocal, long gearChangedAtMs, long nowMs)
        {
            if (nowMs - gearChangedAtMs < GearOverlayMs)
                return FormatGear(gear);
            return FormatSpeed(speedLocal);
        }

        /// <summary>
        /// Gear glyph, optionally wrapped in brackets when both RPMs &gt; 0 and the
        /// redline-reached flag is set (same gate as GearUpshiftBrackets).
        /// Bracketed form is recognized by <see cref="Render"/>.
        /// </summary>
        public static string FormatGearBrackets(
            string gear, double rpms, double redLineReached)
        {
            int g = ParseGear(gear);
            bool brackets = rpms > 0 && redLineReached > 0;
            string glyph = GearToString(g);
            return brackets ? "[" + glyph + "]" : CenterGear(g);
        }

        /// <summary>Rpms/10, clamped 0–999, zero-padded to 3 digits.</summary>
        public static string FormatRpm(double rpms)
            => Clamp999(Round(rpms / 10.0)).ToString("D3");

        /// <summary>Position clamped 0–999, zero-padded to 3 digits.</summary>
        public static string FormatPosition(double position)
            => Clamp999(Round(position)).ToString("D3");

        /// <summary>Fuel rounded and clamped 0–999, zero-padded to 3 digits.</summary>
        public static string FormatFuel(double fuel)
            => Clamp999(Round(fuel)).ToString("D3");

        /// <summary>PropertySpec read as a number via <paramref name="reader"/>, clamped
        /// 0–999. Returns null when the property is missing/unreadable (caller skips).</summary>
        public static string FormatProperty(IPropertyReader reader, PropertySpec source)
        {
            if (reader == null || source == null)
                return null;
            double value;
            if (!reader.TryGetNumber(source, out value))
                return null;
            return Clamp999(Round(value)).ToString("D3");
        }

        /// <summary>Static text kinds — returns <paramref name="text"/> unchanged
        /// (validator already guaranteed renderability).</summary>
        public static string FormatText(string text) => text ?? "";

        // ── Text → 3 segment bytes ───────────────────────────────────────

        /// <summary>
        /// Renders <paramref name="text"/> to a 3-byte segment frame via
        /// <see cref="SevenSegment.EncodeWithDots"/>, taking the first three positions
        /// and blank-padding the rest. Bracketed gear forms ("[3]", "[R]") use the
        /// dedicated bracket segment codes so they match <c>DisplayEncoder.DisplayGear</c>.
        /// </summary>
        public static byte[] Render(string text)
        {
            var frame = new byte[] { SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank };
            if (string.IsNullOrEmpty(text))
                return frame;

            // Bracketed gear: "[X]" / "[10]" — DisplayGear uses BracketLeft/Right, not
            // CharToSegment('['), so special-case the form FormatGearBrackets emits.
            if (text.Length >= 3 && text[0] == '[' && text[text.Length - 1] == ']')
            {
                string inner = text.Substring(1, text.Length - 2);
                frame[0] = SevenSegment.BracketLeft;
                frame[1] = GearToSegment(ParseGear(inner));
                frame[2] = SevenSegment.BracketRight;
                return frame;
            }

            List<byte> encoded = SevenSegment.EncodeWithDots(text);
            int n = encoded.Count < 3 ? encoded.Count : 3;
            for (int i = 0; i < n; i++)
                frame[i] = encoded[i];
            return frame;
        }

        // ── Shared helpers (driver-identical) ────────────────────────────

        /// <summary>SimHub gear string → int: "R"=-1, "N"=0, "1"-"9"=1-9; empty/garbage → 0.</summary>
        public static int ParseGear(string gear)
        {
            if (string.IsNullOrEmpty(gear)) return 0;

            gear = gear.Trim().ToUpperInvariant();

            if (gear == "R" || gear == "REVERSE") return -1;
            if (gear == "N" || gear == "NEUTRAL") return 0;

            int result;
            if (int.TryParse(gear, out result))
                return result;

            return 0;
        }

        private static string CenterGear(int gear)
            => " " + GearToString(gear) + " ";

        private static string GearToString(int gear)
        {
            if (gear == -1) return "R";
            if (gear == 0) return "N";
            // DisplayGear only knows -1..9; out-of-range still stringifies for text, but
            // Render/GearToSegment map them to N — match the driver's ShowGear path by
            // only emitting single-digit forms the encoder understands.
            if (gear >= 1 && gear <= 9) return gear.ToString();
            return "N";
        }

        private static byte GearToSegment(int gear)
        {
            switch (gear)
            {
                case -1: return SevenSegment.R;
                case 0: return SevenSegment.N;
                case 1: return SevenSegment.Digit1;
                case 2: return SevenSegment.Digit2;
                case 3: return SevenSegment.Digit3;
                case 4: return SevenSegment.Digit4;
                case 5: return SevenSegment.Digit5;
                case 6: return SevenSegment.Digit6;
                case 7: return SevenSegment.Digit7;
                case 8: return SevenSegment.Digit8;
                case 9: return SevenSegment.Digit9;
                default: return SevenSegment.N;
            }
        }

        private static int Round(double value) => (int)Math.Round(value);

        private static int Clamp999(int value)
        {
            if (value < 0) return 0;
            if (value > 999) return 999;
            return value;
        }
    }
}
