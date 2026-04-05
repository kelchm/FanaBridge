using System.Collections.Generic;
using System.Windows.Controls;
using FanaBridge.Adapters;
using FanaBridge.Protocol;

namespace FanaBridge.UI
{
    /// <summary>
    /// 3-digit 7-segment display preview for the settings panel.
    /// Accepts a text string and renders it using the same encoding
    /// that the real hardware uses. Automatically scrolls text longer
    /// than 3 display characters.
    /// </summary>
    public partial class SevenSegmentPreview : UserControl
    {
        private SevenSegmentDigit[] _digits;

        // Scroll state
        private List<byte> _scrollFrames;
        private int _scrollPos;
        private string _scrollSourceText;

        public SevenSegmentPreview()
        {
            InitializeComponent();
            _digits = new[] { digit0, digit1, digit2 };
        }

        /// <summary>
        /// Sets the display from a text string. If the text fits in 3 characters,
        /// displays it directly. If longer, call <see cref="ScrollTick"/> repeatedly
        /// to advance the scroll animation.
        /// </summary>
        public void SetText(string text)
        {
            var encoded = SegmentRendering.EncodeText(text ?? "");

            if (encoded.Count <= 3)
            {
                // Fits — show directly, reset scroll
                _scrollFrames = null;
                _scrollSourceText = null;

                byte s0 = encoded.Count > 0 ? encoded[0] : SevenSegment.Blank;
                byte s1 = encoded.Count > 1 ? encoded[1] : SevenSegment.Blank;
                byte s2 = encoded.Count > 2 ? encoded[2] : SevenSegment.Blank;
                _digits[0].SetValue(s0);
                _digits[1].SetValue(s1);
                _digits[2].SetValue(s2);
                return;
            }

            // Long text — set up scroll frames if text changed
            if (text != _scrollSourceText)
            {
                _scrollSourceText = text;
                _scrollFrames = new List<byte>();
                _scrollFrames.Add(SevenSegment.Blank);
                _scrollFrames.Add(SevenSegment.Blank);
                _scrollFrames.Add(SevenSegment.Blank);
                _scrollFrames.AddRange(encoded);
                _scrollFrames.Add(SevenSegment.Blank);
                _scrollFrames.Add(SevenSegment.Blank);
                _scrollFrames.Add(SevenSegment.Blank);
                _scrollPos = 0;
            }

            // Show current scroll frame
            ShowScrollFrame();
        }

        /// <summary>
        /// Advances the scroll by one position. Call this on a timer tick.
        /// Returns true if currently scrolling.
        /// </summary>
        public bool ScrollTick()
        {
            if (_scrollFrames == null)
                return false;

            _scrollPos++;
            if (_scrollPos > _scrollFrames.Count - 3)
                _scrollPos = 0;

            ShowScrollFrame();
            return true;
        }

        /// <summary>Whether the preview is currently in scrolling mode.</summary>
        public bool IsScrolling => _scrollFrames != null;

        /// <summary>
        /// Sets the display from raw segment bytes.
        /// </summary>
        public void SetSegments(byte seg0, byte seg1, byte seg2)
        {
            _scrollFrames = null;
            _scrollSourceText = null;
            _digits[0].SetValue(seg0);
            _digits[1].SetValue(seg1);
            _digits[2].SetValue(seg2);
        }

        private void ShowScrollFrame()
        {
            if (_scrollFrames == null) return;
            int p = _scrollPos;
            if (p > _scrollFrames.Count - 3) p = 0;
            _digits[0].SetValue(_scrollFrames[p]);
            _digits[1].SetValue(_scrollFrames[p + 1]);
            _digits[2].SetValue(_scrollFrames[p + 2]);
        }
    }
}
