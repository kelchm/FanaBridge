using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Timer = System.Timers.Timer;

namespace FanaBridge
{
    public partial class SettingsControl : UserControl
    {
        public FanatecPlugin Plugin { get; }

        public SettingsControl()
        {
            InitializeComponent();
        }

        public SettingsControl(FanatecPlugin plugin) : this()
        {
            Plugin = plugin;
            DataContext = plugin.Settings;

            // Subscribe/unsubscribe symmetrically so tab switches don't lose the handler
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Plugin.StateChanged += OnPluginStateChanged;
            UpdateStatus();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopScroll();
            Plugin.StateChanged -= OnPluginStateChanged;
        }

        private void OnPluginStateChanged()
        {
            Dispatcher.BeginInvoke(new Action(UpdateStatus));
        }

        private void UpdateStatus()
        {
            if (Plugin == null) return;

            bool connected = Plugin.IsDeviceConnected;

            if (!connected)
            {
                txtStatus.Text = "Disconnected";
                txtWheelName.Text = "—";
                txtCapabilities.Text = "—";
                return;
            }

            var caps = Plugin.CurrentCapabilities;
            bool identified = caps.Name != null;

            if (!identified)
            {
                // Connected but wheel not (yet) identified
                txtStatus.Text = "Connected — " + Plugin.WheelName;
                txtWheelName.Text = "—";
                txtCapabilities.Text = "—";
                return;
            }

            txtStatus.Text = "Connected";
            txtWheelName.Text = Plugin.WheelName;
            txtCapabilities.Text = string.Format("{0} button LEDs, {1} encoder LEDs, Display: {2}",
                caps.ButtonLedCount,
                caps.EncoderLedCount,
                caps.Display);
        }

        private void BtnReconnect_Click(object sender, RoutedEventArgs e)
        {
            Plugin?.ForceReconnect();
            UpdateStatus(); // immediate since we're already on UI thread
        }

        // =====================================================================
        // DISPLAY TEST — scroll support
        // =====================================================================

        private const int SCROLL_SPEED_MIN = 50;
        private const int SCROLL_SPEED_MAX = 1000;
        private const int SCROLL_SPEED_DEFAULT = 250;

        private Timer _scrollTimer;
        private List<byte> _scrollFrames;
        private int _scrollPos;

        private void TxtScrollSpeed_LostFocus(object sender, RoutedEventArgs e)
        {
            txtScrollSpeed.Text = ClampScrollSpeed().ToString();
        }

        private int ClampScrollSpeed()
        {
            int ms;
            if (!int.TryParse(txtScrollSpeed.Text, out ms))
                return SCROLL_SPEED_DEFAULT;
            return Math.Max(SCROLL_SPEED_MIN, Math.Min(SCROLL_SPEED_MAX, ms));
        }

        private void BtnSendDisplay_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin == null || !Plugin.IsDeviceConnected) return;

            StopScroll();

            string text = txtDisplayTest.Text;
            if (string.IsNullOrEmpty(text)) text = "---";

            // Encode with dot-folding to see how many display positions we need
            var encoded = EncodeText(text);

            if (encoded.Count <= 3)
            {
                // Fits on the display — just send it
                SimHub.Logging.Current.Info($"SettingsControl: Sending display text \"{text}\"");
                Plugin.Device.DisplayText(text);
                return;
            }

            // Longer text — scroll it
            SimHub.Logging.Current.Info($"SettingsControl: Scrolling display text \"{text}\"");
            StartScroll(encoded);
        }

        private void BtnStopScroll_Click(object sender, RoutedEventArgs e)
        {
            StopScroll();
            if (Plugin != null && Plugin.IsDeviceConnected)
                Plugin.Device.ClearDisplay();
        }

        private void BtnClearDisplay_Click(object sender, RoutedEventArgs e)
        {
            StopScroll();
            if (Plugin == null || !Plugin.IsDeviceConnected) return;
            Plugin.Device.ClearDisplay();
        }

        /// <summary>
        /// Encode a string to 7-segment bytes, folding dots/commas onto the previous character.
        /// </summary>
        private static List<byte> EncodeText(string text)
        {
            var encoded = new List<byte>();
            foreach (char ch in text)
            {
                if ((ch == '.' || ch == ',') && encoded.Count > 0)
                    encoded[encoded.Count - 1] |= SevenSegment.Dot;
                else
                    encoded.Add(SevenSegment.CharToSegment(ch));
            }
            return encoded;
        }

        private void StartScroll(List<byte> encoded)
        {
            // Pad with 3 blanks on each side so the text slides in and out
            _scrollFrames = new List<byte>();
            _scrollFrames.Add(SevenSegment.Blank);
            _scrollFrames.Add(SevenSegment.Blank);
            _scrollFrames.Add(SevenSegment.Blank);
            _scrollFrames.AddRange(encoded);
            _scrollFrames.Add(SevenSegment.Blank);
            _scrollFrames.Add(SevenSegment.Blank);
            _scrollFrames.Add(SevenSegment.Blank);
            _scrollPos = 0;

            int delayMs = ClampScrollSpeed();
            txtScrollSpeed.Text = delayMs.ToString();

            _scrollTimer = new Timer(delayMs);
            _scrollTimer.AutoReset = true;
            _scrollTimer.Elapsed += ScrollTick;
            _scrollTimer.Start();

            btnStopScroll.Visibility = Visibility.Visible;
        }

        private void ScrollTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            var frames = _scrollFrames;
            if (Plugin == null || !Plugin.IsDeviceConnected || frames == null)
            {
                Dispatcher.BeginInvoke(new Action(StopScroll));
                return;
            }

            int pos = _scrollPos;
            if (pos > frames.Count - 3)
            {
                pos = 0;
                _scrollPos = 0;
            }

            Plugin.Device.SetDisplay(
                frames[pos],
                frames[pos + 1],
                frames[pos + 2]);

            _scrollPos = pos + 1;
        }

        private void StopScroll()
        {
            if (_scrollTimer != null)
            {
                _scrollTimer.Stop();
                _scrollTimer.Elapsed -= ScrollTick;
                _scrollTimer.Dispose();
                _scrollTimer = null;
            }
            _scrollFrames = null;
            _scrollPos = 0;

            if (btnStopScroll != null)
                btnStopScroll.Visibility = Visibility.Collapsed;
        }

    }
}
