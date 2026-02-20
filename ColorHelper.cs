namespace FanatecLedControl;

/// <summary>
/// Utility class for RGB color conversions and predefined colors
/// </summary>
public static class ColorHelper
{
    /// <summary>
    /// Converts 24-bit RGB to 16-bit RGB565 format (big-endian)
    /// </summary>
    public static ushort RgbToRgb565(byte r, byte g, byte b)
    {
        // RGB565: RRRRR GGG GGG BBBBB
        ushort r5 = (ushort)((r >> 3) & 0x1F);
        ushort g6 = (ushort)((g >> 2) & 0x3F);
        ushort b5 = (ushort)((b >> 3) & 0x1F);

        return (ushort)((r5 << 11) | (g6 << 5) | b5);
    }

    /// <summary>
    /// Alias for RgbToRgb565 - converts RGB to RGB565 format
    /// </summary>
    public static ushort ToRgb565(byte r, byte g, byte b) => RgbToRgb565(r, g, b);

    /// <summary>
    /// Converts 24-bit RGB hex value (0xRRGGBB) to RGB565
    /// </summary>
    public static ushort HexToRgb565(uint hexColor)
    {
        byte r = (byte)((hexColor >> 16) & 0xFF);
        byte g = (byte)((hexColor >> 8) & 0xFF);
        byte b = (byte)(hexColor & 0xFF);
        return RgbToRgb565(r, g, b);
    }

    // Predefined colors
    public static class Colors
    {
        public static ushort Red => RgbToRgb565(255, 0, 0);
        public static ushort Green => RgbToRgb565(0, 255, 0);
        public static ushort Blue => RgbToRgb565(0, 0, 255);
        public static ushort White => RgbToRgb565(255, 255, 255);
        public static ushort Black => RgbToRgb565(0, 0, 0);
        public static ushort Yellow => RgbToRgb565(255, 255, 0);
        public static ushort Magenta => RgbToRgb565(255, 0, 255);
        public static ushort Cyan => RgbToRgb565(0, 255, 255);
        public static ushort Purple => RgbToRgb565(128, 0, 255);
        public static ushort Orange => RgbToRgb565(255, 165, 0);
    }
}
