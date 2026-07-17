using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FanaBridge.UI.Display.Shared
{
    /// <summary>
    /// The hold-countdown indicator shared by the Overview priority list and the Triggers
    /// editor rows: the remaining-seconds text ("4s") an on-screen rule shows while a timed
    /// hold runs. Extracted from the two identical inline seconds TextBlocks so the row
    /// language stays single-sourced. A null/empty text collapses the control, matching the
    /// original blocks' hide-when-no-countdown behaviour.
    ///
    /// The design wraps this text in a small conic progress ring on the on-screen row; that
    /// ring is a later phase. <see cref="Update"/> already takes the fill fraction so both
    /// call sites speak the final API today, but Commit 1a is behaviour-preserving — the
    /// control renders the text-only look both sites ship, and the ring lights up when the
    /// convergence phase enables it.
    /// </summary>
    public class CountdownRing : Grid
    {
        private readonly TextBlock _text;

        public CountdownRing()
        {
            _text = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = DisplayPalette.GreenAccent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Children.Add(_text);
        }

        /// <summary>Set the countdown text (e.g. "4s"); a null/empty value collapses the
        /// control. <paramref name="fraction"/> is the ring's future fill [0..1] — accepted
        /// now for API stability, not yet drawn (Commit 1a is text-only).</summary>
        public void Update(double fraction, string secondsText)
        {
            _text.Text = secondsText ?? "";
            Visibility = string.IsNullOrEmpty(secondsText)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }
}
