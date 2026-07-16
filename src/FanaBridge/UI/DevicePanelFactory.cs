using System.Windows.Controls;
using FanaBridge.Adapters;
using Newtonsoft.Json.Linq;

namespace FanaBridge.UI
{
    /// <summary>UI-side implementation of the device settings-panel factory.</summary>
    internal sealed class DevicePanelFactory : IDevicePanelFactory
    {
        public Control CreateDisplayPanel(DisplayPanelContext context)
        {
            var panel = new DisplayTabPanel();
            panel.Bind(context);
            return panel;
        }

        public Control CreateTuningPanel(JObject customSettings)
        {
            var panel = new TuningSettingsPanel();
            panel.Bind(customSettings);
            return panel;
        }
    }
}
