using System;
using System.Windows;
using System.Windows.Controls;
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
        private readonly Func<WheelCapabilities> _resolveCaps;

        /// <param name="resolveCaps">
        /// Queried when this control is built — i.e. when the user opens the LEDs
        /// tab — rather than when the LED module was created. The module is created
        /// once, often before the wheel's identity has settled and before a user
        /// profile override has been applied, so capabilities captured then can be
        /// the built-in registration set rather than what is actually driving the
        /// wheel.
        /// </param>
        public LedColorLimitationNotice(Func<WheelCapabilities> resolveCaps)
        {
            _resolveCaps = resolveCaps ?? throw new ArgumentNullException(nameof(resolveCaps));
            InitializeComponent();
            Refresh();
        }

        private void Refresh()
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

            var limitations = LedColorLimitation.ForCapabilities(caps);
            if (limitations.Count == 0) return;

            listLimitations.ItemsSource = limitations;
            root.Visibility = Visibility.Visible;
        }
    }
}
