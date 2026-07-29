using FanaBridge.Protocol;

namespace FanaBridge.Display
{
    /// <summary>
    /// Schema-neutral segment-display text helpers. Shared by the v2 validator and
    /// segment-plane render paths.
    /// </summary>
    public static class SegmentText
    {
        /// <summary>
        /// Whether <paramref name="text"/> renders on the 7-segment display: 1–3 display
        /// positions, each covered by <see cref="SevenSegment.CharToSegment"/>. Positions
        /// are counted the way the encoder folds ("-1.5" is three positions, the dot rides
        /// the '1' — see <see cref="SevenSegment.EncodeWithDots"/>). Space is a deliberate
        /// blank; any other character the segment table cannot draw (it would fall back to
        /// blank) fails, so a screen never silently shows empty positions.
        /// </summary>
        public static bool IsRenderableText(string text)
        {
            int positions;
            return TryCountRenderablePositions(text, out positions)
                && positions >= 1 && positions <= 3;
        }

        /// <summary>
        /// Whether <paramref name="text"/> is a valid multi-char message: every character
        /// is renderable (or a folding dot), any length ≥ 1 position.
        /// </summary>
        public static bool IsRenderableMessage(string text)
        {
            int positions;
            return TryCountRenderablePositions(text, out positions) && positions >= 1;
        }

        /// <summary>Counts display positions the way <see cref="SevenSegment.EncodeWithDots"/>
        /// folds dots (including blank|dot slots for leading / consecutive dots), returning
        /// false when any non-space character has no segment coverage.
        /// Single source of truth for folded width (fit vs scroll, IsRenderableText ≤ 3).</summary>
        private static bool TryCountRenderablePositions(string text, out int positions)
        {
            positions = 0;
            if (string.IsNullOrEmpty(text))
                return false;

            // Glyph coverage first — EncodeWithDots would blank unmappable chars silently.
            foreach (char ch in text)
            {
                if (ch == '.' || ch == ',')
                    continue;
                if (ch != ' ' && SevenSegment.CharToSegment(ch) == SevenSegment.Blank)
                    return false;
            }

            // Single source of truth for folded width (fit vs scroll, IsRenderableText ≤ 3).
            positions = SevenSegment.EncodeWithDots(text).Count;
            return true;
        }
    }
}
