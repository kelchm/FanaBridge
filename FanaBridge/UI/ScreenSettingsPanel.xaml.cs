using System;
using System.Windows;
using System.Windows.Controls;
using FanaBridge.Adapters;
using FanaBridge.Profiles;

namespace FanaBridge.UI
{
    public partial class ScreenSettingsPanel : UserControl
    {
        private DisplaySettings _settings;
        private bool _suppressEvents;

        /// <summary>Fired when the user changes a setting. The parent should persist.</summary>
        public event Action SettingsChanged;

        public ScreenSettingsPanel()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Binds the panel to a DisplaySettings instance.
        /// Call once after construction, before the panel is displayed.
        /// </summary>
        public void Bind(DisplaySettings settings, DisplayType displayType = DisplayType.Basic)
        {
            _settings = settings ?? new DisplaySettings();
            _suppressEvents = true;

            string mode = _settings.DisplayMode ?? DisplaySettings.DefaultMode;
            foreach (ComboBoxItem item in cmbDisplayMode.Items)
            {
                if ((string)item.Tag == mode)
                {
                    cmbDisplayMode.SelectedItem = item;
                    break;
                }
            }

            // Show ITM info banner for wheels with graphical displays
            borderItmInfo.Visibility = displayType == DisplayType.Itm
                ? Visibility.Visible
                : Visibility.Collapsed;

            _suppressEvents = false;
        }

        private void CmbDisplayMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;

            var selected = cmbDisplayMode.SelectedItem as ComboBoxItem;
            if (selected != null)
            {
                _settings.DisplayMode = (string)selected.Tag;
                SettingsChanged?.Invoke();
            }
        }

    }
}
