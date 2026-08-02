using System;
using System.Windows.Controls;
using FanaBridge.Adapters;
using FanaBridge.Profiles;
using Newtonsoft.Json.Linq;

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

        public Control CreateTuningPanel(JObject customSettings, object settingsGate)
        {
            var panel = new TuningSettingsPanel();
            panel.Bind(customSettings, settingsGate);
            return panel;
        }
    }
}
