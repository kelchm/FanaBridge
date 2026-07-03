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

            // ITM displays are firmware-driven (pages chosen on the wheel), so they get
            // the info banner plus the ITM Options section. Basic 7-segment displays get
            // only the mode selector.
            bool isItm = displayType == DisplayType.Itm;

            // "None" (legacy page off) is an ITM-only choice; hide it on basic wheels.
            cmbItemNone.Visibility = isItm ? Visibility.Visible : Visibility.Collapsed;
            SelectByTag(cmbDisplayMode, _settings.DisplayMode ?? DisplaySettings.DefaultMode);
            chkEnableItm.IsChecked = _settings.ItmEnabled;
            chkShowLapTotal.IsChecked = _settings.ItmShowLapTotal;
            chkShowPositionTotal.IsChecked = _settings.ItmShowPositionTotal;

            borderItmInfo.Visibility = isItm ? Visibility.Visible : Visibility.Collapsed;
            sectionItmOptions.Visibility = isItm ? Visibility.Visible : Visibility.Collapsed;
            // The mode selector drives the simple 7-seg gear/speed — the only display on
            // basic wheels, and the optional legacy page on an ITM OLED. Shown in both
            // cases, but titled "Legacy Display Mode" on ITM wheels.
            sectionDisplayMode.Title = isItm ? "Legacy Display Mode" : "Display Mode";
            sectionDisplayMode.Visibility = Visibility.Visible;

            _suppressEvents = false;
        }

        private static void SelectByTag(ComboBox combo, string tag)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if ((string)item.Tag == tag)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private void CmbDisplayMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;

            if (cmbDisplayMode.SelectedItem is ComboBoxItem selected)
            {
                _settings.DisplayMode = (string)selected.Tag;
                SettingsChanged?.Invoke();
            }
        }

        private void ItmOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _settings == null) return;

            _settings.ItmEnabled = chkEnableItm.IsChecked == true;
            _settings.ItmShowLapTotal = chkShowLapTotal.IsChecked == true;
            _settings.ItmShowPositionTotal = chkShowPositionTotal.IsChecked == true;
            SettingsChanged?.Invoke();
        }
    }
}
