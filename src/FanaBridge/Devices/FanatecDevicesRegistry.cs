using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Core.Devices.Profiles;
using FanaBridge.Settings;
using FanaBridge.UI.Devices;
using SimHub.Plugins.Devices;

namespace FanaBridge.Devices
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
        /// <summary>
        /// Builds the settings panels for each device. Not resolved from the
        /// plugin: SimHub shows a device's settings whether or not FanaBridge is
        /// running. Overridable so tests can build instances without WPF.
        /// </summary>
        internal static readonly IDevicePanelFactory PanelFactory = new DevicePanelFactory();

        public IEnumerable<DeviceDescriptor> GetDevices()
        {
            SimHub.Logging.Current.Info("FanatecDevicesRegistry: GetDevices() called");

            foreach (var config in BuildConfigs())
            {
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
                    // The composition root for a device: it supplies the panel
                    // factory so the settings tabs do not depend on the plugin
                    // being enabled (SimHub shows them either way).
                    Factory = () => new FanatecWheelDeviceInstance(
                        capturedConfig, PanelFactory, null),
                    MaximumInstances = 1,
                    IsGeneric = false,
                    IsOEM = false,
                    IsDeprecated = false,
                };
            }
        }

        private static readonly object DecisionLogLock = new object();
        private static string _lastDecisionLog;

        /// <summary>
        /// Builds the device configs, one per DeviceTypeID. Profiles that can
        /// back a device are grouped by the DeviceTypeID they would register
        /// under; each group's profile is whatever
        /// <see cref="WheelProfileStore.Resolve"/> picks for that attachment
        /// with the persisted override — the same rule the running plugin
        /// applies — so the LED editor is sized from the profile that will
        /// actually drive the wheel. Runtime output additionally follows the
        /// active profile live via ResolveCapsFor; only editor sizing waits for
        /// a restart.
        ///
        /// Shared by descriptor registration (<see cref="GetDevices"/>) and the
        /// settings page's add-device prompt so both resolve a detected wheel
        /// to the same DeviceTypeID. Both call this repeatedly (SimHub asks for
        /// the devices every time its Add-device picker opens), so the
        /// per-device decisions are logged only when they change.
        /// </summary>
        public static IReadOnlyCollection<DeviceConfig> BuildConfigs()
        {
            // Ensure profiles are loaded from disk
            WheelProfileStore.EnsureLoaded();

            var decisions = new List<KeyValuePair<bool, string>>();
            void Info(string message) => decisions.Add(new KeyValuePair<bool, string>(false, message));
            void Warn(string message) => decisions.Add(new KeyValuePair<bool, string>(true, message));

            // The first profile of each group names the device. GetAll() is in
            // load order with built-ins first, so that is the built-in when
            // there is one, else the first user profile — the spelling a saved
            // device's case-sensitive DeviceTypeID was registered with.
            var identities = new Dictionary<string, WheelProfile>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<WheelProfile>();

            foreach (var profile in WheelProfileStore.GetAll())
            {
                if (IsBare(profile))
                {
                    Info("Skipping bare profile '" + profile.Id + "' (" + profile.Source + ")");
                    continue;
                }

                string deviceTypeId = new DeviceConfig { Profile = profile }.DeviceTypeId;
                if (identities.TryGetValue(deviceTypeId, out var identity))
                {
                    Info("Profile '" + profile.Id + "' (" + profile.Source + ") shares device " +
                        deviceTypeId + " with '" + identity.Id + "' (" + identity.Source + ")");
                    continue;
                }

                identities[deviceTypeId] = profile;
                ordered.Add(profile);
            }

            var overrides = PersistedPluginSettings.ReadProfileOverrides();
            var configs = new List<DeviceConfig>();

            foreach (var identity in ordered)
            {
                var profile = ResolveProfileFor(identity, overrides, Info, Warn, out bool fromOverride);

                // A bare profile reached WITHOUT an override — e.g. a bare built-in
                // hub profile named after the attachment beating a user LED profile
                // with a non-standard id — can't size a device. The group registered
                // under its identity profile before, so keep it that way: dropping
                // the descriptor would hide an already-added device from SimHub
                // along with its settings. An explicit override to a bare profile is
                // the user's choice and registers as such, as it always did.
                if (IsBare(profile) && !fromOverride)
                {
                    Warn("Device " + new DeviceConfig { Profile = identity }.DeviceTypeId +
                        " resolves to bare profile '" + profile.Id + "' (" + profile.Source +
                        "); registering it with '" + identity.Id + "' (" + identity.Source +
                        ") so an added device keeps loading. Choose its profile in FanaBridge's settings.");
                    profile = identity;
                }

                // An applied override has always named the device (its match
                // spelling became the DeviceTypeID), so it keeps doing so; otherwise
                // the group's identity profile does. Either way the ID is exactly
                // what earlier builds registered, which SimHub matches saved devices
                // against case-sensitively.
                var config = new DeviceConfig
                {
                    Profile = profile,
                    IdentityProfile = fromOverride || ReferenceEquals(profile, identity) ? null : identity,
                    Capabilities = new WheelCapabilities(profile),
                };

                Info("Registering " + config.Capabilities.Name + " (" + config.DeviceTypeId +
                    ") with profile '" + profile.Id + "' (" + profile.Source + ")");
                configs.Add(config);
            }

            LogIfChanged(decisions);
            return configs;
        }

        /// <summary>
        /// The profile backing the device named after <paramref name="identity"/>:
        /// <see cref="WheelProfileStore.Resolve"/> for its attachment, honouring
        /// the persisted override for its match key.
        /// </summary>
        /// <remarks>
        /// Resolving the override here (rather than only at runtime) is what lets
        /// a restart actually produce an LED editor matching an override that
        /// changes the LED layout, including one that adds LEDs to a display-only
        /// wheel. The resolver only honours an override matching the same
        /// wheel/module, since the device's identity — and the name of its
        /// settings file — is derived from those.
        /// </remarks>
        private static WheelProfile ResolveProfileFor(
            WheelProfile identity, IReadOnlyDictionary<string, string> overrides,
            Action<string> info, Action<string> warn, out bool fromOverride)
        {
            fromOverride = false;
            string wheelCode = identity.Match?.WheelType;
            string moduleCode = identity.Match?.ModuleType;
            string matchKey = WheelProfileStore.MakeMatchKey(wheelCode, moduleCode);
            if (string.IsNullOrEmpty(matchKey))
                return identity;

            overrides.TryGetValue(matchKey, out var overrideKey);
            var profile = WheelProfileStore.Resolve(wheelCode, moduleCode, overrideKey, out var overridden)
                ?? identity;
            fromOverride = overridden != null && ReferenceEquals(overridden, profile);

            if (string.IsNullOrEmpty(overrideKey))
                return profile;

            if (overridden == null)
            {
                info("Override '" + overrideKey + "' for " + matchKey +
                    " did not resolve — using " + profile.Id);
            }
            else if (!ReferenceEquals(overridden, profile))
            {
                var overriddenMatch = WheelProfileStore.MakeMatchKey(
                    overridden.Match?.WheelType, overridden.Match?.ModuleType);
                warn("Override '" + overrideKey + "' matches " + (overriddenMatch ?? "nothing") +
                    " but was stored for " + matchKey +
                    " — ignoring so the device keeps its identity.");
            }

            return profile;
        }

        /// <summary>A profile with no LEDs and no display, which never gets a device.</summary>
        private static bool IsBare(WheelProfile profile) =>
            !profile.HasLeds && profile.DisplayType == DisplayType.None;

        /// <summary>
        /// Logs this call's decisions only when they differ from the previous
        /// call's, so repeated registration requests don't flood the log.
        /// </summary>
        private static void LogIfChanged(List<KeyValuePair<bool, string>> decisions)
        {
            string summary = string.Join("\n", decisions.Select(d => d.Value));
            lock (DecisionLogLock)
            {
                if (string.Equals(summary, _lastDecisionLog, StringComparison.Ordinal))
                    return;
                _lastDecisionLog = summary;
            }

            foreach (var decision in decisions)
            {
                string message = "FanatecDevicesRegistry: " + decision.Value;
                if (decision.Key)
                    SimHub.Logging.Current.Warn(message);
                else
                    SimHub.Logging.Current.Info(message);
            }
        }

        /// <summary>
        /// The registered device config matching the currently attached
        /// wheel/hub + module, or null when nothing registered matches (no
        /// profile, or only a bare profile that never gets a descriptor).
        /// Uses <see cref="DeviceConfig.MatchesAttachment"/> — the same exact
        /// wheel+module predicate as device-state reporting — so it only ever
        /// suggests a device that can report Connected.
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
