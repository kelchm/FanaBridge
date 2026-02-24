using System;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;

namespace FanaBridge
{
    public partial class ScreenSettingsPanel : UserControl
    {
        private JObject _settings;
        private bool _suppressEvents;

        /// <summary>Fired when the user changes a setting. The parent should persist.</summary>
        public event Action SettingsChanged;

        public ScreenSettingsPanel()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Binds the panel to a device-instance settings JObject.
        /// Call once after construction, before the panel is displayed.
        /// </summary>
        public void Bind(JObject settings)
        {
            _settings = settings ?? new JObject();
            _suppressEvents = true;

            string mode = (string)_settings["displayMode"] ?? "Gear";
            foreach (ComboBoxItem item in cmbDisplayMode.Items)
            {
                if ((string)item.Tag == mode)
                {
                    cmbDisplayMode.SelectedItem = item;
                    break;
                }
            }

            _suppressEvents = false;
        }

        private void CmbDisplayMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;

            var selected = cmbDisplayMode.SelectedItem as ComboBoxItem;
            if (selected != null)
            {
                _settings["displayMode"] = (string)selected.Tag;
                SettingsChanged?.Invoke();
            }
        }
    }
}
