using System.Windows.Controls;
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
        /// <summary>A bound Display settings panel — the per-device Display tab. The two
        /// editor catalogs are threaded alongside the host so the panel hands each view only
        /// the narrow contract it uses (the picker gets <paramref name="propertyCatalog"/>,
        /// the mapped-control dropdown gets <paramref name="roleCatalog"/>).</summary>
        Control CreateDisplayPanel(
            IDisplayPanelHost host,
            IDisplayPropertyCatalog propertyCatalog,
            IMappedRoleCatalog roleCatalog);

        /// <summary>A bound Tuning settings panel.</summary>
        Control CreateTuningPanel(JObject customSettings);
    }
}
