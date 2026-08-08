using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using FanaBridge.Devices.Profiles;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Stands in for the LED module on devices whose registered capabilities
    /// have no LEDs — never because the runtime is merely unavailable.
    /// </summary>
    internal sealed class NoLedModuleHost : IFanatecLedModuleHost
    {
        public Control EditControl => null;

        public bool Apply(JObject source, bool isDefault) => true;

        public JObject Capture(bool forTemplate, bool forDefaultSettings) => new JObject();

        public void LoadDefaults() { }

        public void Display() { }

        public void SetStatus(bool canDrive, bool connected) { }

        public void StopDriving() { }

        public void HotSwapIfNeeded(WheelCapabilities currentCaps) { }

        public IEnumerable<DynamicButtonAction> GetDynamicActions() =>
            Enumerable.Empty<DynamicButtonAction>();

        public void Dispose() { }
    }
}
