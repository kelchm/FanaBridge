using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FanaBridge.Protocol;
using FanaBridge.SegmentDisplay;
using FanaBridge.SegmentDisplay.Rendering;
using FanaBridge.Shared.Conditions;

namespace FanaBridge.UI
{
    /// <summary>
    /// Rich list item card for a <see cref="SegmentDisplayLayer"/>.
    /// Shows an inline 7-segment mini-preview, layer name, summary, and mode pill.
    /// </summary>
    public partial class DisplayLayerCard : UserControl
    {
        // Role/condition pill colors
        private static readonly SolidColorBrush PillScreen = Frozen(Color.FromRgb(0x2A, 0x6A, 0x3A));
        private static readonly SolidColorBrush PillOnChange = Frozen(Color.FromRgb(0x6A, 0x5A, 0x20));
        private static readonly SolidColorBrush PillWhileTrue = Frozen(Color.FromRgb(0x20, 0x4A, 0x6A));
        private static readonly SolidColorBrush PillExpression = Frozen(Color.FromRgb(0x5A, 0x2A, 0x6A));
        private static readonly SolidColorBrush TextScreen = Frozen(Color.FromRgb(0x66, 0xDD, 0x88));
        private static readonly SolidColorBrush TextOnChange = Frozen(Color.FromRgb(0xDD, 0xCC, 0x66));
        private static readonly SolidColorBrush TextWhileTrue = Frozen(Color.FromRgb(0x66, 0xBB, 0xDD));
        private static readonly SolidColorBrush TextExpression = Frozen(Color.FromRgb(0xCC, 0x88, 0xDD));

        // Status dot colors
        private static readonly SolidColorBrush DotWinning = Frozen(Color.FromRgb(0x66, 0xDD, 0x88));
        private static readonly SolidColorBrush DotActive = Frozen(Color.FromRgb(0xDD, 0xCC, 0x66));
        private static readonly SolidColorBrush DotInactive = Frozen(Color.FromRgb(0x55, 0x55, 0x55));
        private static readonly SolidColorBrush DotDisabled = Frozen(Color.FromRgb(0xDD, 0x66, 0x66));

        private SegmentDisplayLayer _layer;

        public DisplayLayerCard()
        {
            InitializeComponent();
        }

        /// <summary>The layer this card represents.</summary>
        public SegmentDisplayLayer Layer
        {
            get { return _layer; }
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
            if (_layer == null)
            {
                txtName.Text = "";
                txtModePill.Text = "";
                txtSummary.Text = "";
                pillMode.Background = null;
                txtModePill.Foreground = null;
                miniDigit0.SetValue(SevenSegment.Blank);
                miniDigit1.SetValue(SevenSegment.Blank);
                miniDigit2.SetValue(SevenSegment.Blank);
                dotStatus.Fill = DotInactive;
                cardBorder.Opacity = 0.5;
                return;
            }

            txtName.Text = _layer.Name ?? "";

            // Mode pill based on Role + Condition type
            if (_layer.Role == LayerRole.Screen)
            {
                pillMode.Background = PillScreen;
                txtModePill.Text = "SCREEN";
                txtModePill.Foreground = TextScreen;
            }
            else if (_layer.Condition is OnValueChange ovc)
            {
                pillMode.Background = PillOnChange;
                string timing = ovc.HoldMs > 0
                    ? string.Format("{0:0.#}s", ovc.HoldMs / 1000.0) : "";
                txtModePill.Text = "ON CHANGE" + (timing.Length > 0 ? " \u2022 " + timing : "");
                txtModePill.Foreground = TextOnChange;
            }
            else if (_layer.Condition is WhilePropertyTrue)
            {
                pillMode.Background = PillWhileTrue;
                txtModePill.Text = "WHILE TRUE";
                txtModePill.Foreground = TextWhileTrue;
            }
            else if (_layer.Condition is WhileExpressionTrue)
            {
                pillMode.Background = PillExpression;
                txtModePill.Text = "EXPRESSION";
                txtModePill.Foreground = TextExpression;
            }
            else
            {
                pillMode.Background = PillScreen;
                txtModePill.Text = _layer.Role == LayerRole.Overlay ? "OVERLAY" : "SCREEN";
                txtModePill.Foreground = TextScreen;
            }

            // Scope pill
            if (_layer.ShowWhenRunning && !_layer.ShowWhenIdle)
            {
                pillScope.Visibility = Visibility.Visible;
                txtScopePill.Text = "IN GAME";
            }
            else if (!_layer.ShowWhenRunning && _layer.ShowWhenIdle)
            {
                pillScope.Visibility = Visibility.Visible;
                txtScopePill.Text = "IDLE";
            }
            else
            {
                pillScope.Visibility = Visibility.Collapsed;
            }

            // Summary line
            txtSummary.Text = GetSummary();

            // Enabled/disabled opacity
            cardBorder.Opacity = _layer.IsEnabled ? 1.0 : 0.5;
        }

        private string GetSummary()
        {
            if (_layer.Content is FixedTextContent ftc)
                return "\"" + (ftc.Text ?? "") + "\"";

            if (_layer.Content is PropertyContent pc)
            {
                string prop = pc.PropertyName ?? "";
                int lastDot = prop.LastIndexOf('.');
                return lastDot >= 0 ? prop.Substring(lastDot + 1) : prop;
            }

            if (_layer.Content is ExpressionContent)
                return "Expression";

            if (_layer.Content is SequenceContent sc)
                return "Sequence (" + (sc.Items?.Length ?? 0) + ")";

            if (_layer.Content is DeviceCommandContent)
                return "Command";

            return "";
        }

        /// <summary>Updates the mini 7-segment preview from a text string.</summary>
        public void SetPreviewText(string text)
        {
            var encoded = SegmentEncodeStage.Encode(text ?? "");
            miniDigit0.SetValue(encoded.Count > 0 ? encoded[0] : SevenSegment.Blank);
            miniDigit1.SetValue(encoded.Count > 1 ? encoded[1] : SevenSegment.Blank);
            miniDigit2.SetValue(encoded.Count > 2 ? encoded[2] : SevenSegment.Blank);
        }

        /// <summary>Updates the status dot color and tooltip.</summary>
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
                dotStatus.ToolTip = "Active \u2014 currently displayed";
            }
            else if (isActive)
            {
                dotStatus.Fill = DotActive;
                dotStatus.ToolTip = "Active \u2014 overridden by higher priority";
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
