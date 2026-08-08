using System;
using System.Windows;
using System.Windows.Controls;
using FanaBridge;
using FanaBridge.Plugin.Settings;
using FanaBridge.Tuning;

namespace FanaBridge.Plugin.UI
{
    public partial class TuningSettingsPanel : UserControl
    {
        private FanatecDeviceSettings _settings;
        private bool _suppressEvents;

        public TuningSettingsPanel()
        {
            InitializeComponent();
            IsVisibleChanged += OnVisibleChanged;
        }

        /// <summary>
        /// Binds the panel to the device's settings owner.
        /// Call once after construction, before the panel is displayed.
        /// </summary>
        internal void Bind(FanatecDeviceSettings settings)
        {
            _settings = settings;
            UpdateEnabledState();
        }

        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue)
                UpdateEnabledState();
        }

        /// <summary>
        /// Toggles between the disabled hint and the live controls based
        /// on the EnableTuning feature flag, then syncs with the device.
        /// </summary>
        private void UpdateEnabledState()
        {
            var plugin = FanatecPlugin.Instance;
            bool enabled = plugin?.Settings?.EnableTuning == true;

            // Without a plugin the flag reads false whether or not the user set
            // it, so the stock hint would send someone to a settings page that
            // is not there to tell them to turn on something already on.
            txtDisabledHint.Text = plugin == null
                ? "FanaBridge is not running, so these settings cannot be read from or " +
                  "written to the wheel. Enable the plugin to use them."
                : "Tuning features are disabled. Enable them in the FanaBridge plugin " +
                  "settings under Experimental Features.";

            panelDisabled.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            panelEnabled.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

            if (enabled)
                SyncFromDevice();
        }

        /// <summary>
        /// Reads the current encoder mode from the hardware and updates the
        /// combo box to match.  Falls back to the persisted setting if the
        /// device cannot be read.
        /// </summary>
        private void SyncFromDevice()
        {
            _suppressEvents = true;
            try
            {
                string modeTag = null;
                byte[] rawDump = null;

                var tuning = FanatecPlugin.Instance?.Tuning;
                if (tuning != null && tuning.IsConnected)
                {
                    rawDump = tuning.ReadTuningStateRaw();
                    if (rawDump != null)
                    {
                        byte raw = rawDump[18]; // TUNING_READ_ENCODER_MODE_OFFSET
                        if (Enum.IsDefined(typeof(EncoderMode), raw))
                        {
                            modeTag = ((EncoderMode)raw).ToString();
                            _settings?.UpdateEncoderMode(modeTag);
                        }
                    }
                }

                // Fall back to persisted setting
                // Fall back to what was stored, so the panel still shows the
                // user's choice when the wheel cannot be read.
                if (modeTag == null)
                    modeTag = _settings?.Current.EncoderMode;

                modeTag = modeTag ?? "Encoder";

                foreach (ComboBoxItem item in cmbEncoderMode.Items)
                {
                    if ((string)item.Tag == modeTag)
                    {
                        cmbEncoderMode.SelectedItem = item;
                        break;
                    }
                }

                // Update debug dump
                if (rawDump != null)
                    txtTuningDump.Text = FormatHexDump(rawDump);
                else
                    txtTuningDump.Text = "(read failed — device not connected?)";
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private static string FormatHexDump(byte[] data)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < data.Length; i += 16)
            {
                sb.AppendFormat("{0:X4}  ", i);
                int end = Math.Min(i + 16, data.Length);
                for (int j = i; j < end; j++)
                {
                    sb.AppendFormat("{0:X2} ", data[j]);
                    if (j == i + 7) sb.Append(' ');
                }
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            SyncFromDevice();
        }

        private void CmbEncoderMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;

            var selected = cmbEncoderMode.SelectedItem as ComboBoxItem;
            if (selected == null) return;

            string modeTag = (string)selected.Tag;
            _settings.UpdateEncoderMode(modeTag);

            // Send to hardware immediately
            try
            {
                var tuning = FanatecPlugin.Instance?.Tuning;
                if (tuning != null && tuning.IsConnected)
                {
                    EncoderMode mode;
                    if (Enum.TryParse(modeTag, true, out mode))
                    {
                        bool ok = tuning.SetEncoderMode(mode);
                        SimHub.Logging.Current.Info(
                            "TuningSettingsPanel: Encoder mode → " + mode + " (" + (ok ? "OK" : "FAILED") + ")");
                    }
                }
                else
                {
                    SimHub.Logging.Current.Warn("TuningSettingsPanel: Cannot set encoder mode — device not connected");
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("TuningSettingsPanel: Encoder mode error: " + ex.Message);
            }
        }
    }
}
