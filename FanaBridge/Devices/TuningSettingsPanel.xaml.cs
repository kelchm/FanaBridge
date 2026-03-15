using System;
using System.Windows;
using System.Windows.Controls;
using FanaBridge;
using FanaBridge.Core;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Devices
{
    public partial class TuningSettingsPanel : UserControl
    {
        private JObject _settings;
        private bool _suppressEvents;

        /// <summary>Fired when the user changes a setting. The parent should persist.</summary>
        public event Action SettingsChanged;

        public TuningSettingsPanel()
        {
            InitializeComponent();
            IsVisibleChanged += OnVisibleChanged;
        }

        /// <summary>
        /// Binds the panel to a device-instance settings JObject.
        /// Call once after construction, before the panel is displayed.
        /// </summary>
        public void Bind(JObject settings)
        {
            _settings = settings ?? new JObject();
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
            bool enabled = FanatecPlugin.Instance?.Settings?.EnableTuning == true;

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

                var device = FanatecPlugin.Instance?.Device;
                if (device != null && device.IsConnected)
                {
                    rawDump = device.ReadTuningStateRaw();
                    if (rawDump != null)
                    {
                        byte raw = rawDump[18]; // TUNING_READ_ENCODER_MODE_OFFSET
                        if (Enum.IsDefined(typeof(EncoderMode), raw))
                        {
                            modeTag = ((EncoderMode)raw).ToString();
                            if (_settings != null)
                                _settings["encoderMode"] = modeTag;
                        }
                    }
                }

                // Fall back to persisted setting
                if (modeTag == null && _settings != null)
                    modeTag = (string)_settings["encoderMode"];

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
            _settings["encoderMode"] = modeTag;
            SettingsChanged?.Invoke();

            // Send to hardware immediately
            try
            {
                var device = FanatecPlugin.Instance?.Device;
                if (device != null && device.IsConnected)
                {
                    EncoderMode mode;
                    if (Enum.TryParse(modeTag, true, out mode))
                    {
                        bool ok = device.SetEncoderMode(mode);
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
