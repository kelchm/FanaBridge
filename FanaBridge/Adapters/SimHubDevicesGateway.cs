using System;
using System.Linq;
using SimHub.Plugins;
using SimHub.Plugins.Devices;

namespace FanaBridge.Adapters
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
        /// Flattens composite devices the same way SimHub's own device list does.
        /// </summary>
        public static bool IsDeviceAdded(DevicesPlugin devices, string deviceTypeId)
        {
            if (devices?.DevicesPluginSettings == null || deviceTypeId == null)
                return false;

            return devices.GetDevices().Any(d =>
                string.Equals(d?.DeviceDescriptor?.DeviceTypeID, deviceTypeId, StringComparison.Ordinal));
        }

        /// <summary>
        /// The already-added device that would make SimHub refuse to add
        /// <paramref name="deviceTypeId"/>, or null when the add can proceed.
        /// SimHub enforces MaximumInstances (1 for all FanaBridge descriptors)
        /// across a descriptor FAMILY, not just the exact id: an existing
        /// device blocks a candidate when their ids match or they are related
        /// through ParentDeviceTypeID — for FanaBridge that means two
        /// hub+module combos sharing the same module. Attempting the add
        /// anyway ends in SimHub's "You can only add 1 instances of …" dialog,
        /// so the prompt explains the conflict instead of offering the button.
        /// </summary>
        public static DeviceInstance FindBlockingDevice(
            DevicesPlugin devices, string deviceTypeId, string parentDeviceTypeId)
        {
            var added = devices?.DevicesPluginSettings?.Devices;
            if (added == null || deviceTypeId == null)
                return null;

            // Root devices only — SimHub's instance cap counts the same set.
            return added.ToList().FirstOrDefault(d =>
                d?.DeviceDescriptor != null
                && IsSimilarDescriptor(
                    d.DeviceDescriptor.DeviceTypeID, d.DeviceDescriptor.ParentDeviceTypeID,
                    deviceTypeId, parentDeviceTypeId));
        }

        /// <summary>
        /// Mirrors how SimHub decides which existing devices count against a
        /// candidate descriptor's MaximumInstances: same DeviceTypeID, or —
        /// when the existing device has a parent — a shared parent, or a
        /// parent/child relation in either direction.
        /// </summary>
        internal static bool IsSimilarDescriptor(
            string existingId, string existingParentId,
            string candidateId, string candidateParentId)
        {
            if (string.Equals(existingId, candidateId, StringComparison.Ordinal))
                return true;

            if (existingParentId == null)
                return false;

            return string.Equals(existingParentId, candidateParentId, StringComparison.Ordinal)
                || string.Equals(existingParentId, candidateId, StringComparison.Ordinal)
                || string.Equals(existingId, candidateParentId, StringComparison.Ordinal);
        }
    }
}
