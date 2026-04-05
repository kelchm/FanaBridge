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
        internal static string TruncateTo3(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int chars = 0, cutoff = text.Length;
            for (int i = 0; i < text.Length; i++)
            {
                if ((text[i] == '.' || text[i] == ',') && chars > 0) continue;
                chars++;
                if (chars > 3) { cutoff = i; break; }
            }
            return text.Substring(0, cutoff);
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
