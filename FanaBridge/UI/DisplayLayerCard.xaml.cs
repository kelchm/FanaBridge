using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FanaBridge.Adapters;
using FanaBridge.Protocol;

namespace FanaBridge.UI
{
    /// <summary>
    /// Rich list item card for a DisplayLayer. Shows an inline 7-segment
    /// mini-preview, layer name, summary, and mode pill.
    /// </summary>
    public partial class DisplayLayerCard : UserControl
    {
        private static readonly SolidColorBrush PillConstant = Frozen(Color.FromRgb(0x2A, 0x6A, 0x3A));
        private static readonly SolidColorBrush PillOnChange = Frozen(Color.FromRgb(0x6A, 0x5A, 0x20));
        private static readonly SolidColorBrush PillWhileTrue = Frozen(Color.FromRgb(0x20, 0x4A, 0x6A));
        private static readonly SolidColorBrush PillExpression = Frozen(Color.FromRgb(0x5A, 0x2A, 0x6A));
        private static readonly SolidColorBrush TextConstant = Frozen(Color.FromRgb(0x66, 0xDD, 0x88));
        private static readonly SolidColorBrush TextOnChange = Frozen(Color.FromRgb(0xDD, 0xCC, 0x66));
        private static readonly SolidColorBrush TextWhileTrue = Frozen(Color.FromRgb(0x66, 0xBB, 0xDD));
        private static readonly SolidColorBrush TextExpression = Frozen(Color.FromRgb(0xCC, 0x88, 0xDD));

        private DisplayLayer _layer;

        public DisplayLayerCard()
        {
            InitializeComponent();
        }

        /// <summary>The layer this card represents.</summary>
        public DisplayLayer Layer
        {
            get => _layer;
            set { _layer = value; Refresh(); }
        }

        /// <summary>Sets the displayed priority number (1-based).</summary>
        public void SetPriority(int index)
        {
            txtPriority.Text = index.ToString();
        }

        /// <summary>Refreshes all visual state from the bound layer.</summary>
        public void Refresh()
        {
            if (_layer == null) return;

            txtName.Text = _layer.Name ?? "";

            // Mode pill (includes timing for OnChange)
            switch (_layer.Mode)
            {
                case DisplayLayerMode.Constant:
                    pillMode.Background = PillConstant;
                    txtModePill.Text = "ALWAYS";
                    txtModePill.Foreground = TextConstant;
                    break;
                case DisplayLayerMode.OnChange:
                    pillMode.Background = PillOnChange;
                    txtModePill.Text = "ON CHANGE \u2022 " + _layer.TimingLabel;
                    txtModePill.Foreground = TextOnChange;
                    break;
                case DisplayLayerMode.WhileTrue:
                    pillMode.Background = PillWhileTrue;
                    txtModePill.Text = "WHILE TRUE";
                    txtModePill.Foreground = TextWhileTrue;
                    break;
                case DisplayLayerMode.Expression:
                    pillMode.Background = PillExpression;
                    txtModePill.Text = "EXPRESSION";
                    txtModePill.Foreground = TextExpression;
                    break;
            }

            // Summary line
            txtSummary.Text = GetSummary();

            // Enabled/disabled state
            cardBorder.Opacity = _layer.IsEnabled ? 1.0 : 0.5;
        }

        private string GetSummary()
        {
            if (_layer.Source == DisplaySource.FixedText)
                return _layer.FixedText ?? "";

            if (_layer.Source == DisplaySource.Expression || _layer.Mode == DisplayLayerMode.Expression)
                return "";

            string prop = _layer.Source == DisplaySource.Property && !string.IsNullOrEmpty(_layer.PropertyName)
                ? _layer.PropertyName
                : _layer.Mode == DisplayLayerMode.Constant
                    ? _layer.PropertyName
                    : _layer.WatchProperty;

            if (string.IsNullOrEmpty(prop)) return "";

            int lastDot = prop.LastIndexOf('.');
            return lastDot >= 0 ? prop.Substring(lastDot + 1) : prop;
        }

        /// <summary>
        /// Updates the mini 7-segment preview from a text string.
        /// </summary>
        public void SetPreviewText(string text)
        {
            var encoded = FanatecDisplayManager.EncodeText(text ?? "");
            miniDigit0.SetValue(encoded.Count > 0 ? encoded[0] : SevenSegment.Blank);
            miniDigit1.SetValue(encoded.Count > 1 ? encoded[1] : SevenSegment.Blank);
            miniDigit2.SetValue(encoded.Count > 2 ? encoded[2] : SevenSegment.Blank);
        }

        private static readonly SolidColorBrush DotWinning = Frozen(Color.FromRgb(0x66, 0xDD, 0x88));   // green
        private static readonly SolidColorBrush DotActive = Frozen(Color.FromRgb(0xDD, 0xCC, 0x66));    // amber
        private static readonly SolidColorBrush DotInactive = Frozen(Color.FromRgb(0x55, 0x55, 0x55));  // gray
        private static readonly SolidColorBrush DotDisabled = Frozen(Color.FromRgb(0xDD, 0x66, 0x66));  // red

        /// <summary>
        /// Updates the status dot color and tooltip.
        /// </summary>
        public void SetStatus(bool isEnabled, bool isWinning, bool isActive)
        {
            if (!isEnabled)
            {
                dotStatus.Fill = DotDisabled;
                dotStatus.ToolTip = "Disabled";
            }
            else if (isWinning)
            {
                dotStatus.Fill = DotWinning;
                dotStatus.ToolTip = "Active — currently displayed";
            }
            else if (isActive)
            {
                dotStatus.Fill = DotActive;
                dotStatus.ToolTip = "Active — overridden by higher priority";
            }
            else
            {
                dotStatus.Fill = DotInactive;
                dotStatus.ToolTip = "Inactive";
            }
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
