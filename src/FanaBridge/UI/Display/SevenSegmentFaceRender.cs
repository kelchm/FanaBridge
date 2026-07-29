using FanaBridge.Protocol;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Pure bit helpers for the shared 3-character seven-segment face.
    /// </summary>
    internal static class SevenSegmentFaceRender
    {
        /// <summary>Whether bit <paramref name="segment"/> (0–6) is lit on a segment byte.</summary>
        public static bool IsSegmentLit(byte segs, int segment)
            => segment >= 0 && segment < 7 && (segs & (1 << segment)) != 0;

        /// <summary>Whether the decimal-point bit (bit 7) is lit.</summary>
        public static bool IsDotLit(byte segs)
            => (segs & SevenSegment.Dot) != 0;

        public static byte[] BlankFrame()
            => new byte[] { SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank };
    }
}
