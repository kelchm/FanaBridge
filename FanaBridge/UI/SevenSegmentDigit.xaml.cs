using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FanaBridge.UI
{
    /// <summary>
    /// Renders a single 7-segment digit plus decimal point.
    /// Call <see cref="SetValue"/> with a segment byte (matching SevenSegment.cs encoding).
    /// </summary>
    public partial class SevenSegmentDigit : UserControl
    {
        private static readonly SolidColorBrush OnBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        private static readonly SolidColorBrush OffBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));

        private Shape[] _segments;
        private byte _lastValue = 0xFF; // force initial paint

        static SevenSegmentDigit()
        {
            OnBrush.Freeze();
            OffBrush.Freeze();
        }

        public SevenSegmentDigit()
        {
            InitializeComponent();
            _segments = new Shape[] { segA, segB, segC, segD, segE, segF, segG, segDP };
            SetValue(0x00);
        }

        /// <summary>
        /// Sets the segment state from a byte (bit 0=a, 1=b, ... 6=g, 7=dp).
        /// </summary>
        public void SetValue(byte value)
        {
            if (value == _lastValue) return;
            _lastValue = value;

            for (int i = 0; i < 8; i++)
            {
                _segments[i].Fill = ((value & (1 << i)) != 0) ? OnBrush : OffBrush;
            }
        }
    }
}
