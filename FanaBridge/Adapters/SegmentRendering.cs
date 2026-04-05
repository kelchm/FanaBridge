using System;
using System.Collections.Generic;
using FanaBridge.Protocol;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Static rendering pipeline utilities for the 3-digit 7-segment display.
    /// These transform formatted text into display-ready output:
    /// alignment → truncation → segment encoding.
    /// </summary>
    internal static class SegmentRendering
    {
        /// <summary>
        /// Aligns a formatted string for the 3-character display using segment-aware sizing.
        /// Gear = centered, Number/Decimal/Time = right-aligned, Text = left-aligned.
        /// Dots and commas fold into the preceding segment, so "1.2" is 2 segments not 3.
        /// </summary>
        internal static string AlignText(string text, DisplayFormat format)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int segmentCount = EncodeText(text).Count;
            if (segmentCount >= 3) return text;
            switch (format)
            {
                case DisplayFormat.Gear:
                    return segmentCount == 1 ? " " + text + " " :
                           segmentCount == 2 ? " " + text : text;
                case DisplayFormat.Number:
                case DisplayFormat.Decimal:
                case DisplayFormat.Time:
                    return new string(' ', 3 - segmentCount) + text;
                case DisplayFormat.Text:
                default:
                    return text;
            }
        }

        /// <summary>
        /// Truncates text to fit 3 display segments. Dots and commas fold into the
        /// preceding segment and do not count toward the limit.
        /// </summary>
        internal static string TruncateTo3(string text, OverflowStrategy overflow = OverflowStrategy.TruncateRight)
        {
            if (string.IsNullOrEmpty(text)) return "";

            // Build a map of segment boundaries: each entry is the start index
            // of a segment, including any trailing dot/comma that folds into it.
            var segments = new List<int>();
            for (int i = 0; i < text.Length; i++)
            {
                if ((text[i] == '.' || text[i] == ',') && segments.Count > 0)
                    continue; // folds into previous segment
                segments.Add(i);
            }
            if (segments.Count <= 3) return text;

            if (overflow == OverflowStrategy.TruncateLeft)
            {
                // Keep the last 3 segments
                return text.Substring(segments[segments.Count - 3]);
            }
            else
            {
                // Keep the first 3 segments — end is the start of segment 4
                return text.Substring(0, segments[3]);
            }
        }

        /// <summary>
        /// Resolves <see cref="OverflowStrategy.Auto"/> to a concrete strategy
        /// based on the display format.
        /// </summary>
        internal static OverflowStrategy ResolveOverflow(OverflowStrategy overflow, DisplayFormat format)
        {
            if (overflow != OverflowStrategy.Auto) return overflow;
            return format == DisplayFormat.Text ? OverflowStrategy.Scroll
                 : format == DisplayFormat.Time ? OverflowStrategy.TruncateLeft
                 : OverflowStrategy.TruncateRight;
        }

        /// <summary>
        /// Applies the overflow strategy to text that may exceed 3 segments.
        /// Returns the text unchanged for Scroll (the controller handles scrolling).
        /// </summary>
        internal static string ApplyOverflow(string text, OverflowStrategy overflow)
        {
            switch (overflow)
            {
                case OverflowStrategy.TruncateLeft:
                case OverflowStrategy.TruncateRight:
                    return TruncateTo3(text, overflow);
                default:
                    return text;
            }
        }

        /// <summary>
        /// Encodes a text string into a list of 7-segment byte values.
        /// Dots and commas fold into the preceding segment's decimal point bit.
        /// </summary>
        internal static List<byte> EncodeText(string text)
        {
            var encoded = new List<byte>();
            if (string.IsNullOrEmpty(text)) return encoded;
            foreach (char ch in text)
            {
                if ((ch == '.' || ch == ',') && encoded.Count > 0)
                    encoded[encoded.Count - 1] |= SevenSegment.Dot;
                else
                    encoded.Add(SevenSegment.CharToSegment(ch));
            }
            return encoded;
        }
    }
}
