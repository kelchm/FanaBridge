using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using FanaBridge.Profiles;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Stands in for the LED module on devices that have no LEDs.
    /// </summary>
    /// <remarks>
    /// Used only when the device's registered capabilities have no LEDs at all
    /// — never because the runtime happens to be unavailable. Substituting it
    /// for a missing runtime is what produced settings documents with no LED
    /// data, which SimHub then wrote over the complete file.
    /// </remarks>
    internal sealed class NoLedModuleHost : IFanatecLedModuleHost
    {
        public bool HasModule => false;

        public Control EditControl => null;

        public bool Apply(JObject source, bool isDefault) => true;

        public JObject Capture(bool forTemplate, bool forDefaultSettings) => new JObject();

        public void LoadDefaults() { }

        public void Display() { }

        public void SetStatus(bool canDrive, bool connected) { }

        public void ClearOutput() { }

        public void RebindToCurrentGeneration() { }

        public void HotSwapIfNeeded(WheelCapabilities currentCaps) { }

        public IEnumerable<DynamicButtonAction> GetDynamicActions() =>
            Enumerable.Empty<DynamicButtonAction>();

        public void FinalizeModule() { }

        public void Dispose() { }
    }
}
