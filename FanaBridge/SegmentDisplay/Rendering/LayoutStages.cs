using System;
using System.Collections.Generic;

namespace FanaBridge.SegmentDisplay.Rendering
{
    /// <summary>
    /// Aligns text within the 3-segment display. Segment-aware sizing:
    /// dots and commas fold into the preceding segment and don't count.
    /// </summary>
    public class AlignStage : IRenderStage
    {
        private readonly AlignmentType _alignment;
        private readonly SegmentFormat _format;

        public AlignStage(AlignmentType alignment, SegmentFormat format)
        {
            _alignment = alignment;
            _format = format;
        }

        public DisplayFrame Process(DisplayFrame input, RenderContext ctx)
        {
            if (string.IsNullOrEmpty(input.Text)) return input;

            int segCount = CountSegments(input.Text);
            if (segCount >= 3) return input;

            var resolved = _alignment;
            if (resolved == AlignmentType.Auto)
            {
                switch (_format)
                {
                    case SegmentFormat.Gear:    resolved = AlignmentType.Center; break;
                    case SegmentFormat.Number:
                    case SegmentFormat.Decimal:
                    case SegmentFormat.Time:    resolved = AlignmentType.Right; break;
                    default:                    resolved = AlignmentType.Left; break;
                }
            }

            int pad = 3 - segCount;
            switch (resolved)
            {
                case AlignmentType.Right:
                    input.Text = new string(' ', pad) + input.Text;
                    break;
                case AlignmentType.Center:
                    int left = pad / 2;
                    int right = pad - left;
                    input.Text = new string(' ', left) + input.Text + new string(' ', right);
                    break;
                case AlignmentType.Left:
                default:
                    // Left-aligned: no padding needed (display blanks trailing segments)
                    break;
            }

            return input;
        }

        internal static int CountSegments(string text)
        {
            int count = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if ((text[i] == '.' || text[i] == ',') && count > 0)
                    continue; // folds into preceding segment
                count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Truncates text to fit 3 display segments. Segment-aware: dots and commas
    /// fold into the preceding segment and don't count toward the limit.
    /// </summary>
    public class TruncateStage : IRenderStage
    {
        private readonly OverflowType _direction;

        public TruncateStage(OverflowType direction)
        {
            _direction = direction;
        }

        public DisplayFrame Process(DisplayFrame input, RenderContext ctx)
        {
            if (string.IsNullOrEmpty(input.Text)) return input;

            var segments = BuildSegmentMap(input.Text);
            if (segments.Count <= 3) return input;

            if (_direction == OverflowType.TruncateLeft)
                input.Text = input.Text.Substring(segments[segments.Count - 3]);
            else
                input.Text = input.Text.Substring(0, segments.Count > 3 ? segments[3] : input.Text.Length);

            return input;
        }

        internal static List<int> BuildSegmentMap(string text)
        {
            var segments = new List<int>();
            for (int i = 0; i < text.Length; i++)
            {
                if ((text[i] == '.' || text[i] == ',') && segments.Count > 0)
                    continue;
                segments.Add(i);
            }
            return segments;
        }
    }

    /// <summary>
    /// Scrolls text that exceeds 3 segments across the display. Stateful —
    /// tracks scroll position based on <see cref="RenderContext.ElapsedMs"/>.
    /// </summary>
    public class ScrollStage : IRenderStage
    {
        private readonly int _speedMs;

        // Cached state
        private string _lastText;
        private List<byte> _frames;

        public ScrollStage(int speedMs)
        {
            _speedMs = Math.Max(50, speedMs);
        }

        public DisplayFrame Process(DisplayFrame input, RenderContext ctx)
        {
            if (string.IsNullOrEmpty(input.Text)) return input;

            var encoded = SegmentEncodeStage.Encode(input.Text);
            if (encoded.Count <= 3) return input; // fits without scrolling

            // Rebuild scroll frames if text changed
            if (input.Text != _lastText)
            {
                _lastText = input.Text;
                _frames = new List<byte> { 0, 0, 0 }; // leading blanks
                _frames.AddRange(encoded);
                _frames.Add(0);
                _frames.Add(0);
                _frames.Add(0); // trailing blanks
            }

            // Compute position from elapsed time
            int totalPositions = _frames.Count - 2; // -2 because we need 3 consecutive
            if (totalPositions <= 0) return input;

            int pos = (int)(ctx.ElapsedMs / _speedMs) % totalPositions;

            // Output the 3 bytes at the current scroll position
            input.Segments = new byte[]
            {
                _frames[pos],
                _frames[pos + 1],
                _frames[pos + 2],
            };
            // Text is consumed — segments are set directly
            return input;
        }
    }
}
