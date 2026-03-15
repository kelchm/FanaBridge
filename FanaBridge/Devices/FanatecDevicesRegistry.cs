using System;
using System.Collections.Generic;
using SimHub.Plugins.Devices;

namespace FanaBridge
{
    /// <summary>
    /// Registers one DeviceDescriptor per loaded <see cref="WheelProfile"/> so each
    /// appears as a separate entry in SimHub's Devices view with its own
    /// settings (.shdevice) and connected/disconnected status.
    ///
    /// All descriptors share the Fanatec VID (0x0EB7). Detection is based on
    /// the SDK wheel identity, not USB PID, because all wheel rims share the
    /// wheelbase PID. The DeviceInstances are thin wrappers over the shared
    /// FanatecPlugin singleton — they do not open their own HID connections.
    ///
    /// Profiles are loaded from JSON files by <see cref="WheelProfileStore"/>.
    /// </summary>
    public class FanatecDevicesRegistry : IDeviceDescriptorsRegistry
    {
        public IEnumerable<DeviceDescriptor> GetDevices()
        {
            SimHub.Logging.Current.Info("FanatecDevicesRegistry: GetDevices() called");

            // Ensure profiles are loaded from disk
            WheelProfileStore.EnsureLoaded();

            // De-duplicate by DeviceTypeId so that a user profile with the
            // same match criteria as a built-in profile doesn't produce two
            // descriptors with the same DeviceTypeID (which SimHub rejects).
            // GetAll() returns built-in first, then user, so last-wins gives
            // user profiles priority — matching the auto-resolution behaviour.
            var configs = new Dictionary<string, DeviceConfig>(StringComparer.OrdinalIgnoreCase);

            foreach (var profile in WheelProfileStore.GetAll())
            {
                // Skip bare hub profiles (no LEDs, no display)
                if (!profile.HasLeds && profile.DisplayType == DisplayType.None)
                {
                    SimHub.Logging.Current.Info(
                        "FanatecDevicesRegistry: Skipping bare profile '" + profile.Id + "'");
                    continue;
                }

                var config = new DeviceConfig
                {
                    Profile = profile,
                    Capabilities = new WheelCapabilities(profile),
                };

                if (configs.ContainsKey(config.DeviceTypeId))
                {
                    SimHub.Logging.Current.Info(
                        "FanatecDevicesRegistry: Profile '" + profile.Id +
                        "' (" + profile.Source + ") supersedes earlier entry for " +
                        config.DeviceTypeId);
                }

                configs[config.DeviceTypeId] = config;
            }

            foreach (var config in configs.Values)
            {
                SimHub.Logging.Current.Info(
                    "FanatecDevicesRegistry: Registering " + config.Capabilities.Name +
                    " (" + config.DeviceTypeId + ")");

                // Capture for closure
                var capturedConfig = config;

                yield return new DeviceDescriptor
                {
                    Name = config.Capabilities.ShortName ?? config.Capabilities.Name,
                    Brand = "Fanatec",
                    DeviceTypeID = config.DeviceTypeId,
                    ParentDeviceTypeID = config.ParentDeviceTypeId,
                    // All Fanatec wheelbases share VID 0x0EB7. We use an arbitrary
                    // PID (0x0001) just so SimHub sees a USB descriptor; the real
                    // matching is done in GetDeviceState() via the SDK.
                    DetectionDescriptor = new USBRequest(0x0EB7, 0x0001, true),
                    Factory = () => new FanatecWheelDeviceInstance(capturedConfig),
                    MaximumInstances = 1,
                    IsGeneric = false,
                    IsOEM = false,
                    IsDeprecated = false,
                };
            }
        }
    }
}
