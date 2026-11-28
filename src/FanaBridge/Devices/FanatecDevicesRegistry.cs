using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Core.Devices;
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

        public IEnumerable<DeviceDescriptor> GetDevices() =>
            GetDevices(() => FanatecPlugin.Instance);

        /// <summary>Test seam: registration against a given plugin generation.</summary>
        internal IEnumerable<DeviceDescriptor> GetDevices(Func<FanatecPlugin> resolvePlugin)
        {
            SimHub.Logging.Current.Info("FanatecDevicesRegistry: GetDevices() called");

            // At most one device per call, even if the attachment changes mid-loop.
            bool detected = false;

            foreach (var config in BuildConfigs())
            {
                // Capture for closure
                var capturedConfig = config;

                var descriptor = new DeviceDescriptor
                {
                    Name = config.Capabilities.ShortName ?? config.Capabilities.Name,
                    Brand = "Fanatec",
                    DeviceTypeID = config.DeviceTypeId,
                    ParentDeviceTypeID = config.ParentDeviceTypeId,
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
                if (!detected)
                    detected = ApplyDetection(descriptor, capturedConfig, resolvePlugin);
                yield return descriptor;
            }
        }

        /// <summary>
        /// Lists the device under the Add-device picker's "Devices found on your
        /// system" only while its wheel or hub + module is attached. SimHub asks
        /// for the devices each time the picker opens, so only the device attached
        /// then gets the base's USB ID; <c>CompatibilityTest</c> drops it if the
        /// attachment changes before the picker looks. Returns whether it did.
        /// </summary>
        internal static bool ApplyDetection(
            DeviceDescriptor descriptor, DeviceConfig config, Func<FanatecPlugin> resolvePlugin)
        {
            int productId;
            try
            {
                var snap = resolvePlugin()?.Wheelbase?.Snapshot;
                if (!FanatecWheelDeviceInstance.IsAttached(snap, config))
                    return false;
                productId = snap.ProductId;
            }
            catch (Exception) { return false; }

            if (productId <= 0)
                return false;

            descriptor.DetectionDescriptor = new USBRequest(
                FanatecWheelbase.FANATEC_VENDOR_ID, productId, true);

            // Runs off the UI thread; a throw would cost other devices their
            // place in the found list.
            descriptor.CompatibilityTest = () =>
            {
                try { return FanatecWheelDeviceInstance.IsAttached(resolvePlugin(), config); }
                catch (Exception) { return false; }
            };
            return true;
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

            // One device per attachment. The store canonicalizes match codes on
            // load, so every profile for an attachment yields the same
            // DeviceTypeID; the group's first profile just supplies the codes.
            // (Case-insensitive anyway, for codes the decode tables don't know.)
            var groups = new Dictionary<string, WheelProfile>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<WheelProfile>();

            foreach (var profile in WheelProfileStore.GetAll())
            {
                if (profile.IsBare)
                {
                    Info("Skipping bare profile '" + profile.Id + "' (" + profile.Source + ")");
                    continue;
                }

                string deviceTypeId = new DeviceConfig { Profile = profile }.DeviceTypeId;
                if (groups.TryGetValue(deviceTypeId, out var first))
                {
                    Info("Profile '" + profile.Id + "' (" + profile.Source + ") shares device " +
                        deviceTypeId + " with '" + first.Id + "' (" + first.Source + ")");
                    continue;
                }

                groups[deviceTypeId] = profile;
                ordered.Add(profile);
            }

            var overrides = PersistedPluginSettings.ReadProfileOverrides();
            var configs = new List<DeviceConfig>();

            foreach (var first in ordered)
            {
                // The resolver never picks a bare profile automatically while the
                // group has one that can drive something, so a bare result here is
                // an explicit override — the user's choice, registered as such.
                var profile = ResolveProfileFor(first, overrides, Info, Warn);
                var config = new DeviceConfig
                {
                    Profile = profile,
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
        /// The profile backing the device for <paramref name="first"/>'s attachment:
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
            WheelProfile first, IReadOnlyDictionary<string, string> overrides,
            Action<string> info, Action<string> warn)
        {
            string wheelCode = first.Match?.WheelType;
            string moduleCode = first.Match?.ModuleType;
            string matchKey = WheelProfileStore.MakeMatchKey(wheelCode, moduleCode);
            if (string.IsNullOrEmpty(matchKey))
                return first;

            overrides.TryGetValue(matchKey, out var overrideKey);
            var profile = WheelProfileStore.Resolve(wheelCode, moduleCode, overrideKey, out var overridden)
                ?? first;

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
