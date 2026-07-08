using System;
using System.Collections.Generic;
using FanaBridge.Profiles;
using SimHub.Plugins.Devices;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Registers one DeviceDescriptor per loaded <see cref="WheelProfile"/> so each
    /// appears as a separate entry in SimHub's Devices view with its own
    /// settings (.shdevice) and connected/disconnected status.
    ///
    /// All descriptors share the Fanatec VID (0x0EB7). Detection is based on
    /// the FF 08 wheel identity, not USB PID, because all wheel rims share the
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

            foreach (var config in BuildConfigs(verbose: true))
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
                    // matching is done in GetDeviceState() against the wheelbase identity.
                    DetectionDescriptor = new USBRequest(0x0EB7, 0x0001, true),
                    Factory = () => new FanatecWheelDeviceInstance(capturedConfig),
                    MaximumInstances = 1,
                    IsGeneric = false,
                    IsOEM = false,
                    IsDeprecated = false,
                };
            }
        }

        /// <summary>
        /// Builds the deduplicated set of device configs that back the
        /// registered descriptors — one per device match key. When multiple
        /// profiles share the same match (e.g. built-in + user test variants),
        /// the built-in profile wins for the device descriptor (name, type ID).
        /// The actual LED/display capabilities used at runtime come from the
        /// currently-active profile (see FanatecLedManager.GetDriver).
        ///
        /// Shared by descriptor registration (<see cref="GetDevices"/>) and the
        /// settings page's add-device prompt so both resolve a detected wheel
        /// to the same DeviceTypeID. <paramref name="verbose"/> logs the
        /// skip/dedupe decisions; only registration does, so the per-refresh
        /// prompt path doesn't flood the log.
        /// </summary>
        public static IReadOnlyCollection<DeviceConfig> BuildConfigs(bool verbose = false)
        {
            // Ensure profiles are loaded from disk
            WheelProfileStore.EnsureLoaded();

            var configs = new Dictionary<string, DeviceConfig>(StringComparer.OrdinalIgnoreCase);

            foreach (var profile in WheelProfileStore.GetAll())
            {
                // Skip bare hub profiles (no LEDs, no display)
                if (!profile.HasLeds && profile.DisplayType == DisplayType.None)
                {
                    if (verbose)
                        SimHub.Logging.Current.Info(
                            "FanatecDevicesRegistry: Skipping bare profile '" + profile.Id + "'");
                    continue;
                }

                var config = new DeviceConfig
                {
                    Profile = profile,
                    Capabilities = new WheelCapabilities(profile),
                };

                if (configs.TryGetValue(config.DeviceTypeId, out var existing))
                {
                    // Built-in always wins — it defines the device's full capability
                    if (existing.Profile.Source == ProfileSource.BuiltIn)
                    {
                        if (verbose)
                            SimHub.Logging.Current.Info(
                                "FanatecDevicesRegistry: Profile '" + profile.Id +
                                "' (" + profile.Source + ") skipped — built-in '" +
                                existing.Profile.Id + "' defines device " + config.DeviceTypeId);
                        continue;
                    }

                    // New profile is built-in, existing is user — promote built-in
                    if (profile.Source == ProfileSource.BuiltIn)
                    {
                        if (verbose)
                            SimHub.Logging.Current.Info(
                                "FanatecDevicesRegistry: Built-in '" + profile.Id +
                                "' replaces user '" + existing.Profile.Id +
                                "' for device " + config.DeviceTypeId);
                    }
                    else
                    {
                        // Both are user profiles — keep the first one.
                        // The registry only determines the device descriptor
                        // (name, type ID). LED capability comes from the
                        // currently-active profile at runtime.
                        if (verbose)
                            SimHub.Logging.Current.Info(
                                "FanatecDevicesRegistry: Profile '" + profile.Id +
                                "' (" + profile.Source + ") skipped — '" +
                                existing.Profile.Id + "' already registered for " +
                                config.DeviceTypeId);
                        continue;
                    }
                }

                configs[config.DeviceTypeId] = config;
            }

            return configs.Values;
        }

        /// <summary>
        /// The registered device config matching the currently attached
        /// wheel/hub + module, or null when nothing registered matches (no
        /// profile, or only a bare profile that never gets a descriptor).
        /// Uses <see cref="DeviceConfig.MatchesAttachment"/> — the same exact
        /// wheel+module predicate as device-state reporting — rather than
        /// <c>WheelProfileStore.FindByWheelType</c>, whose bare-wheel fallback
        /// would suggest a device that could never report Connected for a
        /// hub with a module attached.
        /// </summary>
        public static DeviceConfig FindConfigForAttachment(
            bool wheelDetected, string wheelCode, string moduleCode)
        {
            if (!wheelDetected)
                return null;

            foreach (var config in BuildConfigs())
            {
                if (config.MatchesAttachment(wheelDetected, wheelCode, moduleCode))
                    return config;
            }

            return null;
        }
    }
}
