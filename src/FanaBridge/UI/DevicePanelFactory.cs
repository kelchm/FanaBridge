using System;
using System.Windows.Controls;
using FanaBridge.Adapters;
using FanaBridge.Profiles;

namespace FanaBridge.UI
{
    /// <summary>UI-side implementation of the device settings-panel factory.</summary>
    internal sealed class DevicePanelFactory : IDevicePanelFactory
    {
        public Control CreateScreenPanel(DisplaySettings settings, DisplayType display, byte itmDeviceId, Action settingsChanged)
        {
            var panel = new ScreenSettingsPanel();
            panel.Bind(settings, display, itmDeviceId);
            if (settingsChanged != null)
                panel.SettingsChanged += () => settingsChanged();
            return panel;
        }

        public Control CreateTuningPanel(FanatecDeviceSettings settings)
        {
            var panel = new TuningSettingsPanel();
            panel.Bind(settings);
            return panel;
        }
    }
}
