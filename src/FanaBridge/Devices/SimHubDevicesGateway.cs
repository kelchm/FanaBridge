using System;
using System.Linq;
using SimHub.Plugins;
using SimHub.Plugins.Devices;

namespace FanaBridge.Plugin.Devices
{
    /// <summary>
    /// Read-and-check access to SimHub's Devices plugin for the settings
    /// page's add-device prompt. FanaBridge registers device descriptors via
    /// <see cref="FanatecDevicesRegistry"/>, but whether the user has actually
    /// ADDED a device is state owned by SimHub's DevicesPlugin: an un-added
    /// device has no DeviceInstance at all, so added-ness is invisible to
    /// FanaBridge's own device instances and must be queried here.
    /// </summary>
    public static class SimHubDevicesGateway
    {
        /// <summary>SimHub's Devices plugin, or null if not (yet) loaded.</summary>
        public static DevicesPlugin Resolve(PluginManager pluginManager)
        {
            return pluginManager?.GetPlugin<DevicesPlugin>();
        }

        /// <summary>
        /// Whether SimHub registered a descriptor with this DeviceTypeID.
        /// Descriptors are collected once at startup, so a profile created
        /// this session has a config but no descriptor until SimHub restarts —
        /// offering an add for it would silently do nothing.
        /// </summary>
        public static bool HasDescriptor(DevicesPlugin devices, string deviceTypeId)
        {
            var descriptors = devices?.DevicesPluginSettings?.DeviceDescriptors;
            if (descriptors == null || deviceTypeId == null)
                return false;

            // Ordinal: SimHub resolves DeviceTypeIDs with ==, never ignore-case.
            return descriptors.Any(d =>
                string.Equals(d?.DeviceTypeID, deviceTypeId, StringComparison.Ordinal));
        }

        /// <summary>
        /// Whether the user has added a device with this DeviceTypeID.
        /// Flattens composite devices the same way SimHub's own device list
        /// does. Note this is an exact-id check: SimHub additionally caps
        /// instances across a descriptor family (shared ParentDeviceTypeID),
        /// and answers such an add with its own explanatory dialog — that
        /// rare edge is left to SimHub rather than pre-checked here.
        /// </summary>
        public static bool IsDeviceAdded(DevicesPlugin devices, string deviceTypeId)
        {
            if (devices?.DevicesPluginSettings == null || deviceTypeId == null)
                return false;

            return devices.GetDevices().Any(d =>
                string.Equals(d?.DeviceDescriptor?.DeviceTypeID, deviceTypeId, StringComparison.Ordinal));
        }
    }
}
