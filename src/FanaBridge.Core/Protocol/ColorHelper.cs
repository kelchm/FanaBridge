using System;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// RGB color helper with RGB565 conversion and predefined colors.
    /// </summary>
    public static class ColorHelper
    {
        /// <summary>
        /// Converts 24-bit RGB to 16-bit BGR565 (Fanatec hardware byte order).
        /// Blue in high 5 bits, green in middle 6, red in low 5.
        /// </summary>
        public static ushort RgbToRgb565(byte r, byte g, byte b)
        {
            ushort r5 = (ushort)((r >> 3) & 0x1F);
            ushort g6 = (ushort)((g >> 2) & 0x3F);
            ushort b5 = (ushort)((b >> 3) & 0x1F);
            return (ushort)((b5 << 11) | (g6 << 5) | r5);
        }

        /// <summary>
        /// Converts 24-bit RGB to 16-bit BGR555 packed in the RGB565 layout.
        /// Green is quantized to 5 bits (same as R/B) and placed in the lower
        /// 5 bits of the 6-bit green field, leaving bit 10 always zero.
        /// Required for hardware (e.g. Button Module Rally) that only reads
        /// 5 green bits and ignores or misinterprets the MSB.
        /// </summary>
        public static ushort RgbToRgb555(byte r, byte g, byte b)
        {
            ushort r5 = (ushort)((r >> 3) & 0x1F);
            ushort g5 = (ushort)((g >> 3) & 0x1F);
            ushort b5 = (ushort)((b >> 3) & 0x1F);
            return (ushort)((b5 << 11) | (g5 << 5) | r5);
        }

        /// <summary>
        /// Converts a System.Drawing.Color to RGB565.
        /// </summary>
        public static ushort ToRgb565(System.Drawing.Color color)
        {
            return RgbToRgb565(color.R, color.G, color.B);
        }

        /// <summary>
        /// Converts a System.Drawing.Color to RGB565, pre-multiplying the alpha
        /// channel into the RGB values. This encodes brightness/fading into the
        /// color itself (5-6-5 bit resolution) rather than relying on the Fanatec
        /// 3-bit intensity channel (only 8 levels).
        /// </summary>
        public static ushort ToRgb565Premultiplied(System.Drawing.Color color)
        {
            double a = color.A / 255.0;
            byte r = (byte)Math.Round(color.R * a);
            byte g = (byte)Math.Round(color.G * a);
            byte b = (byte)Math.Round(color.B * a);
            return RgbToRgb565(r, g, b);
        }

        /// <summary>
        /// Converts a System.Drawing.Color to RGB555, pre-multiplying alpha.
        /// See <see cref="RgbToRgb555"/> for when this is needed.
        /// </summary>
        public static ushort ToRgb555Premultiplied(System.Drawing.Color color)
        {
            double a = color.A / 255.0;
            byte r = (byte)Math.Round(color.R * a);
            byte g = (byte)Math.Round(color.G * a);
            byte b = (byte)Math.Round(color.B * a);
            return RgbToRgb555(r, g, b);
        }

        /// <summary>
        /// Converts a Color to a Fanatec 3-bit intensity value (0-7).
        /// Uses HSV Value (max channel) with premultiplied alpha, then
        /// scales to the hardware range.
        /// Intended for monochrome LEDs (e.g. encoder indicators) where
        /// SimHub provides full Color but the hardware only has brightness.
        /// </summary>
        public static byte ColorToIntensity(System.Drawing.Color color)
        {
            double a = color.A / 255.0;
            double value = Math.Max(color.R, Math.Max(color.G, color.B)) * a;
            int level = (int)Math.Round(value / 255.0 * 7.0);
            return (byte)Math.Min(Math.Max(level, 0), 7);
        }

        /// <summary>
        /// Converts 24-bit RGB to the Fanatec col01 RGB333 encoding (9 bits in 2 bytes).
        /// Each channel is quantized to 3 bits (0-7). Returns a ushort where the high
        /// byte is data_hi and the low byte is data_lo, matching the col01 report layout:
        ///   data_hi: [G1 G0 R2 R1 R0 B2 B1 B0]  (GG_RRR_BBB)
        ///   data_lo: [0  0  0  0  0  0  0  G2 ]  (.......G)
        /// </summary>
        public static ushort RgbToRgb333(byte r, byte g, byte b)
        {
            int r3 = (r >> 5) & 0x07;
            int g3 = (g >> 5) & 0x07;
            int b3 = (b >> 5) & 0x07;
            byte dataHi = (byte)(((g3 & 0x03) << 6) | (r3 << 3) | b3);
            byte dataLo = (byte)((g3 >> 2) & 0x01);
            return (ushort)((dataHi << 8) | dataLo);
        }

        /// <summary>
        /// Converts a System.Drawing.Color to RGB333, pre-multiplying alpha.
        /// See <see cref="RgbToRgb333"/> for the encoding format.
        /// </summary>
        public static ushort ToRgb333Premultiplied(System.Drawing.Color color)
        {
            double a = color.A / 255.0;
            byte r = (byte)Math.Round(color.R * a);
            byte g = (byte)Math.Round(color.G * a);
            byte b = (byte)Math.Round(color.B * a);
            return RgbToRgb333(r, g, b);
        }

        /// <summary>
        /// Converts 24-bit RGB to the col01 RGB333 encoding, snapped to the eight
        /// fully saturated values (each channel fully on or fully off) that official
        /// software emits.
        /// <para>
        /// The col01 <c>0x08</c> command carries either a per-LED on/off pattern or a
        /// single RGB333 color, and both share one wire format. RGB333 <c>(r=0, g=4,
        /// b=0)</c> — a mid-dark green — encodes to the same two bytes as the "LED 0
        /// on, rest off" pattern, and the Fanatec driver stack reacts to that pattern
        /// by switching the rim into on/off mode. Later colors are then read as
        /// patterns, so the strip lights in its fixed pattern color for the values
        /// whose low byte has bit 0 set and stays dark for the rest, until pure red
        /// or pure blue returns it to color mode. Snapping keeps output inside the
        /// set the hardware is known to be driven with and cannot produce that
        /// encoding.
        /// </para>
        /// </summary>
        /// <remarks>
        /// Each channel is judged <em>relative to the brightest channel</em>, not against
        /// a fixed level. Thresholding channels absolutely would make the hue depend on
        /// brightness — amber dimmed past the point where green falls below the cut but
        /// red does not would turn red, then black. Scaling all channels by the same
        /// factor cannot change which side of a relative threshold they sit on, so the
        /// chosen color survives dimming. Deciding whether the strip is lit at all is a
        /// separate question, handled by <see cref="ToRgb333PalettePremultiplied"/>.
        /// </remarks>
        public static ushort RgbToRgb333Palette(byte r, byte g, byte b)
        {
            var (litR, litG, litB) = LitChannels(r, g, b);
            return RgbToRgb333(
                litR ? (byte)0xFF : (byte)0x00,
                litG ? (byte)0xFF : (byte)0x00,
                litB ? (byte)0xFF : (byte)0x00);
        }

        /// <summary>
        /// Which color channels count as lit, judged relative to the brightest one.
        /// Shared by every col01 path that has one bit per channel to work with.
        /// </summary>
        private static (bool R, bool G, bool B) LitChannels(byte r, byte g, byte b)
        {
            int max = Math.Max(r, Math.Max(g, b));
            if (max == 0) return (false, false, false);
            return (IsLit(r, max), IsLit(g, max), IsLit(b, max));
        }

        // channel >= max/2, written to avoid integer truncation: at max=1 a truncated
        // threshold of 0 would count every channel — including the zero ones — as lit
        // and turn a near-black red into white.
        private static bool IsLit(byte channel, int max)
        {
            return channel > 0 && channel * 2 >= max;
        }

        /// <summary>
        /// Converts a System.Drawing.Color to the snapped RGB333 palette encoding,
        /// pre-multiplying alpha. See <see cref="RgbToRgb333Palette"/>.
        /// <para>
        /// The rim has no separate intensity channel, so brightness cannot dim the
        /// strip — it can only decide whether the strip is lit. That cut-off uses the
        /// same rule <c>legacyRevOnOff</c> applies to individual LEDs, so both col01
        /// channels agree on when hardware counts as on.
        /// </para>
        /// </summary>
        public static ushort ToRgb333PalettePremultiplied(System.Drawing.Color color)
        {
            if (ColorToIntensity(color) == 0) return 0;

            // Hue comes from the original channels, not the premultiplied ones.
            // Alpha scales all three equally, so it cannot change which side of a
            // relative threshold they fall on — but rounding each channel to a byte
            // first can, and did: ARGB(255,20,9,0) and ARGB(240,20,9,0) resolved to
            // red while ARGB(248,20,9,0) resolved to yellow. Alpha decides only
            // whether the strip is lit.
            return RgbToRgb333Palette(color.R, color.G, color.B);
        }

        /// <summary>
        /// Converts a Color to per-channel RGB booleans. A channel is lit when it
        /// is at least half the brightest channel's value. Alpha determines whether
        /// the LED is lit at all, but does not change which channels are selected.
        /// Used for col01 subcmd 0x0A/0x0B per-LED RGB encoding (7 colors plus off).
        /// </summary>
        public static (bool R, bool G, bool B) ColorToRgbBools(System.Drawing.Color color)
        {
            // Same rule as the RGB333 palette: a channel is lit relative to the
            // brightest one, and alpha decides only whether the LED is lit at all.
            // Thresholding each channel at "greater than zero" instead would turn
            // RGB(255,1,0) — visually pure red — into yellow, and light an LED for
            // any trace value a gradient happens to pass through.
            if (ColorToIntensity(color) == 0) return (false, false, false);
            return LitChannels(color.R, color.G, color.B);
        }

        /// <summary>
        /// Converts BGR565 to an HTML hex string (e.g. "#FF0000").
        /// Approximate — expands 5/6/5 bits back to 8-bit channels.
        /// </summary>
        public static string Rgb565ToHex(ushort bgr565)
        {
            byte b = (byte)(((bgr565 >> 11) & 0x1F) << 3);
            byte g = (byte)(((bgr565 >> 5) & 0x3F) << 2);
            byte r = (byte)((bgr565 & 0x1F) << 3);
            return string.Format("#{0:X2}{1:X2}{2:X2}", r, g, b);
        }

        /// <summary>
        /// Converts a 24-bit hex integer (0xRRGGBB) to RGB565.
        /// </summary>
        public static ushort HexToRgb565(uint hexColor)
        {
            byte r = (byte)((hexColor >> 16) & 0xFF);
            byte g = (byte)((hexColor >> 8) & 0xFF);
            byte b = (byte)(hexColor & 0xFF);
            return RgbToRgb565(r, g, b);
        }

        /// <summary>Predefined common colors in RGB565.</summary>
        public static class Colors
        {
            public static readonly ushort Red     = RgbToRgb565(255, 0, 0);
            public static readonly ushort Green   = RgbToRgb565(0, 255, 0);
            public static readonly ushort Blue    = RgbToRgb565(0, 0, 255);
            public static readonly ushort White   = RgbToRgb565(255, 255, 255);
            public static readonly ushort Black   = RgbToRgb565(0, 0, 0);
            public static readonly ushort Yellow  = RgbToRgb565(255, 255, 0);
            public static readonly ushort Magenta = RgbToRgb565(255, 0, 255);
            public static readonly ushort Cyan    = RgbToRgb565(0, 255, 255);
            public static readonly ushort Purple  = RgbToRgb565(128, 0, 255);
            public static readonly ushort Orange  = RgbToRgb565(255, 165, 0);
        }
    }
}
