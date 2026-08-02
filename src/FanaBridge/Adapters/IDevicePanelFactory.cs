using System;
using System.Windows.Controls;
using FanaBridge.Profiles;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Constructs the per-device WPF settings panels. Implemented on the UI side
    /// and resolved through <see cref="FanatecPlugin.PanelFactory"/>, so Adapters
    /// never references FanaBridge.UI — UI stays the top layer with nothing
    /// below it depending on it. (SimHub instantiates DeviceInstances via the
    /// registry's parameterless factory, so this rides the same PluginResolver
    /// seam the generation guard uses rather than constructor injection.)
    /// </summary>
    internal interface IDevicePanelFactory
    {
        /// <summary>A bound Screen settings panel; <paramref name="settingsChanged"/> fires on any user change.</summary>
        Control CreateScreenPanel(DisplaySettings settings, DisplayType display, byte itmDeviceId, Action settingsChanged);

        /// <summary>
        /// A bound Tuning settings panel. Writes into <paramref name="customSettings"/>
        /// must hold <paramref name="settingsGate"/> — the device instance
        /// enumerates the same object during saves on another thread.
        /// </summary>
        Control CreateTuningPanel(JObject customSettings, object settingsGate);
    }
}
