using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FanaBridge.Adapters;
using FanaBridge.Protocol;

namespace FanaBridge.UI
{
    /// <summary>
    /// Rich list item card for a DisplayLayer. Shows an inline 7-segment
    /// mini-preview, layer name, and mode/timing pills.
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

        public event System.Action EnableChanged;

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

            chkEnabled.IsChecked = _layer.IsEnabled;
            txtName.Text = _layer.Name ?? "";

            // Mode pill
            switch (_layer.Mode)
            {
                case DisplayLayerMode.Constant:
                    pillMode.Background = PillConstant;
                    txtModePill.Text = "ALWAYS";
                    txtModePill.Foreground = TextConstant;
                    break;
                case DisplayLayerMode.OnChange:
                    pillMode.Background = PillOnChange;
                    txtModePill.Text = "ON CHANGE";
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

            // Timing pill
            if (_layer.Mode == DisplayLayerMode.OnChange)
            {
                pillTiming.Visibility = Visibility.Visible;
                txtTimingPill.Text = _layer.TimingLabel;
            }
            else
            {
                pillTiming.Visibility = Visibility.Collapsed;
            }

            // Opacity for disabled layers
            cardBorder.Opacity = _layer.IsEnabled ? 1.0 : 0.5;
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

        private void ChkEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_layer == null) return;
            _layer.IsEnabled = chkEnabled.IsChecked == true;
            cardBorder.Opacity = _layer.IsEnabled ? 1.0 : 0.5;
            EnableChanged?.Invoke();
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
