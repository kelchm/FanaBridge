using System.Collections.Generic;
using FanaBridge.Protocol;

namespace FanaBridge.SegmentDisplay.Rendering
{
    /// <summary>
    /// Final pipeline stage: converts text into 7-segment byte values.
    /// Dots and commas fold into the preceding segment's decimal point bit.
    /// If <see cref="SegmentDisplayFrame.Segments"/> is already set (e.g., by
    /// <see cref="ScrollStage"/>), this stage is a no-op.
    /// </summary>
    public class SegmentEncodeStage : IRenderStage
    {
        public SegmentDisplayFrame Process(SegmentDisplayFrame input, RenderContext ctx)
        {
            // If segments were already set (e.g., by scroll stage), skip encoding
            if (input.Segments != null) return input;

            var encoded = Encode(input.Text ?? "");

            input.Segments = new byte[]
            {
                encoded.Count > 0 ? encoded[0] : SevenSegment.Blank,
                encoded.Count > 1 ? encoded[1] : SevenSegment.Blank,
                encoded.Count > 2 ? encoded[2] : SevenSegment.Blank,
            };

            return input;
        }

        /// <summary>
        /// Encodes text into a list of 7-segment byte values.
        /// Dots and commas fold into the preceding segment's decimal point bit.
        /// </summary>
        internal static List<byte> Encode(string text)
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
