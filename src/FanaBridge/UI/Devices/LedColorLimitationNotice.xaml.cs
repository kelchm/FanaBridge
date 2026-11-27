using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FanaBridge.Core.Devices.Profiles;

namespace FanaBridge.UI.Devices
{
    /// <summary>
    /// Explains, in the SimHub LEDs tab, the ways this wheel's LEDs cannot show
    /// the colors the picker offers.
    /// <para>
    /// This is presentation only. The colors are matched in the encoder, because
    /// they also arrive from gradients, the brightness slider and imported LED
    /// profiles — none of which pass through this UI, so constraining the picker
    /// would mislead without fixing anything.
    /// </para>
    /// </summary>
    public partial class LedColorLimitationNotice : UserControl
    {
        // How often an open LEDs tab re-checks the capabilities. Polled rather
        // than subscribed: an event subscription would tie the control to one
        // plugin generation, which can be replaced underneath it (issue #37), and
        // a profile change or wheel swap only needs to show up within a moment.
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

        private readonly Func<WheelCapabilities> _resolveCaps;
        private readonly DispatcherTimer _poll;

        // The capabilities currently shown. Capabilities are immutable and
        // replaced on every change, so a reference compare detects one.
        private WheelCapabilities _shown;
        private bool _hasShown;

        /// <param name="resolveCaps">
        /// Queried when this control is built — i.e. when the user opens the LEDs
        /// tab — and again while it is shown, rather than once when the LED module
        /// was created. The module is created once, often before the wheel's
        /// identity has settled and before a user profile override has been
        /// applied, and the active profile can change while the tab is open.
        /// </param>
        public LedColorLimitationNotice(Func<WheelCapabilities> resolveCaps)
        {
            _resolveCaps = resolveCaps ?? throw new ArgumentNullException(nameof(resolveCaps));
            InitializeComponent();
            Refresh();

            // Only while loaded, so a closed tab leaves no timer running.
            _poll = new DispatcherTimer { Interval = PollInterval };
            _poll.Tick += (s, e) => Refresh();
            Loaded += (s, e) => { Refresh(); _poll.Start(); };
            Unloaded += (s, e) => _poll.Stop();
        }

        /// <summary>
        /// Re-reads the capabilities and shows their limitations, or hides the
        /// notice when there are none (including when there is no profile).
        /// </summary>
        internal void Refresh()
        {
            WheelCapabilities caps;
            try
            {
                caps = _resolveCaps();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("LedColorLimitationNotice: caps lookup failed: " + ex.Message);
                return;
            }

            if (_hasShown && ReferenceEquals(caps, _shown))
                return;
            _shown = caps;
            _hasShown = true;

            var limitations = LedColorLimitation.ForCapabilities(caps);
            listLimitations.ItemsSource = limitations.Count == 0 ? null : limitations;
            root.Visibility = limitations.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Test hook: whether the notice is showing.</summary>
        internal bool IsShowingForTest => root.Visibility == Visibility.Visible;
    }
}
