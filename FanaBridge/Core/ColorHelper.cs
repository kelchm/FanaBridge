using System;

namespace FanaBridge
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
