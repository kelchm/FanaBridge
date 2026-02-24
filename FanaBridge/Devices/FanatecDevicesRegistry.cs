using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using FanatecManaged;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using SimHub.Plugins.Devices;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;

namespace FanaBridge
{
    /// <summary>
    /// Registers one DeviceDescriptor per known Fanatec wheel type so each
    /// appears as a separate entry in SimHub's Devices view with its own
    /// settings (.shdevice) and connected/disconnected status.
    ///
    /// All descriptors share the Fanatec VID (0x0EB7). Detection is based on
    /// the SDK wheel identity, not USB PID, because all wheel rims share the
    /// wheelbase PID. The DeviceInstances are thin wrappers over the shared
    /// FanatecPlugin singleton — they do not open their own HID connections.
    /// </summary>
    public class FanatecDevicesRegistry : IDeviceDescriptorsRegistry
    {
        public IEnumerable<DeviceDescriptor> GetDevices()
        {
            SimHub.Logging.Current.Info("FanatecDevicesRegistry: GetDevices() called");

            foreach (var config in WheelCapabilityRegistry.GetDeviceConfigurations())
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

    /// <summary>
    /// DeviceInstance for a specific Fanatec wheel type.
    ///
    /// Owns a <c>LedModuleSettings&lt;FanatecLedManager&gt;</c> which provides
    /// the native SimHub LED profile editor, settings persistence, brightness
    /// controls, and the full .shdevice export structure.
    ///
    /// Does NOT own hardware — delegates to the shared FanatecPlugin singleton
    /// for all SDK/HID access. Reports Connected only when the singleton's
    /// current wheel identity matches this instance's wheel type.
    /// </summary>
    public class FanatecWheelDeviceInstance : DeviceInstance
    {
        private readonly DeviceConfig _config;
        private JObject _customSettings = new JObject();

        // Native LED module — provides RGBLedsDrivers, settings, and UI.
        private LedModuleSettings<FanatecLedManager> _ledModule;
        private bool _ledModuleInitialized;

        // Display manager — null when the wheel has no display.
        private FanatecDisplayManager _displayManager;

        // Track connection state transitions for cleanup on disconnect.
        private bool _wasConnected;

        public FanatecWheelDeviceInstance(DeviceConfig config)
        {
            _config = config;
        }

        // ── LED module setup ─────────────────────────────────────────────

        /// <summary>
        /// Lazily creates the LedModuleSettings with a pre-configured
        /// FanatecLedManager for this wheel's capabilities.
        /// </summary>
        private void EnsureLedModuleInitialized()
        {
            if (_ledModuleInitialized)
                return;
            _ledModuleInitialized = true;

            var caps = _config.Capabilities;
            if (caps.TotalLedCount == 0)
                return;

            var manager = new FanatecLedManager(caps);
            var options = new LedModuleOptions
            {
                DeviceName = caps.ShortName ?? caps.Name,
                LedCount = 0,            // no RPM/telemetry LEDs
                // Combine button + encoder LEDs into ButtonsCount (matches native devices)
                ButtonsCount = caps.TotalLedCount,
                EncodersCount = 0,       // not using separate encoder section
                RawLedCount = caps.TotalLedCount,
                LedDriver = manager,
                EnableBrightnessSection = true,
                ShowConnectionStatus = true,
                VID = FanatecSdkManager.FANATEC_VENDOR_ID,
            };

            _ledModule = new LedModuleSettings<FanatecLedManager>(options);

            // Mark as embedded so the standalone device-enable checkbox is hidden —
            // enable/disable is handled by the parent DeviceInstance's Connected toggle.
            _ledModule.IsEmbedded = true;
            _ledModule.IsEnabled = true;  // Required for UI to be interactive
            _ledModule.IndividualLEDsMode = IndividualLEDsMode.Combined;  // Show raw profile by default

            SimHub.Logging.Current.Info(
                "FanatecWheelDeviceInstance[" + caps.Name + "]: LedModuleSettings created (" +
                "buttons=" + caps.ButtonLedCount + ", encoders=" + caps.EncoderLedCount +
                ", raw=" + caps.TotalLedCount + ")");
        }

        // ── DeviceInstance overrides ─────────────────────────────────────

        public override void LoadDefaultSettings()
        {
            SimHub.Logging.Current.Info(
                "FanatecWheelDeviceInstance[" + _config.Capabilities.Name + "]: LoadDefaultSettings");

            EnsureLedModuleInitialized();

            _customSettings = new JObject
            {
                ["wheelType"] = _config.WheelType.ToString(),
                ["moduleType"] = _config.ModuleType.ToString(),
                ["displayMode"] = "Gear",
            };

            if (_ledModule != null)
                _ledModule.LoadDefaults();
        }

        public override DeviceState GetDeviceState()
        {
            var plugin = FanatecPlugin.Instance;
            if (plugin == null)
                return DeviceState.Disabled;

            var sdk = plugin.SdkManager;
            if (sdk == null || !sdk.IsConnected)
                return DeviceState.Scanning;

            if (!sdk.WheelDetected || sdk.SteeringWheelType != _config.WheelType)
                return DeviceState.Scanning;

            // For hub+module configs, also match the module type
            if (_config.ModuleType != M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED
                && sdk.SubModuleType != _config.ModuleType)
                return DeviceState.Scanning;

            return DeviceState.Connected;
        }

        public override JToken GetSettings(bool forTemplate, bool forDefaultSettings)
        {
            var result = new JObject();

            // LED module settings (produces ledModuleSettings, leds, buttons, encoders, raw keys)
            if (_ledModule != null)
            {
                try
                {
                    var ledDict = _ledModule.GetSettings(forTemplate, forDefaultSettings);
                    if (ledDict != null)
                    {
                        foreach (var kvp in ledDict)
                        {
                            result[kvp.Key] = kvp.Value ?? JValue.CreateNull();
                        }
                    }
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn(
                        "FanatecWheelDeviceInstance: GetSettings(LED) failed: " + ex.Message);
                }
            }

            // Custom settings (display mode, wheel/module identity)
            if (_customSettings != null)
            {
                foreach (var prop in _customSettings.Properties())
                {
                    result[prop.Name] = prop.Value.DeepClone();
                }
            }

            return result;
        }

        public override void SetSettings(JToken settings, bool isDefault)
        {
            if (!(settings is JObject obj))
                return;

            EnsureLedModuleInitialized();

            // Extract custom settings
            _customSettings = new JObject();
            foreach (var key in new[] { "wheelType", "moduleType", "displayMode" })
            {
                if (obj[key] != null)
                    _customSettings[key] = obj[key].DeepClone();
            }

            // Pass LED-related settings to LedModuleSettings
            if (_ledModule != null)
            {
                try
                {
                    // LedModuleSettings.SetSettings expects Dictionary<string, JToken>
                    var dict = new Dictionary<string, JToken>();
                    foreach (var prop in obj.Properties())
                    {
                        dict[prop.Name] = prop.Value;
                    }
                    _ledModule.SetSettings(dict, isDefault);
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn(
                        "FanatecWheelDeviceInstance: SetSettings(LED) failed: " + ex.Message);
                }
            }

            _displayManager?.UpdateSettings(_customSettings);
        }

        public override void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            bool isConnected = GetDeviceState() == DeviceState.Connected;

            // Detect Connected → Scanning transition
            if (_wasConnected && !isConnected)
            {
                SimHub.Logging.Current.Info(
                    "FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                    "]: Lost connection");

                _displayManager?.Clear();
            }

            _wasConnected = isConnected;

            if (!isConnected)
                return;

            EnsureLedModuleInitialized();

            var plugin = FanatecPlugin.Instance;
            var device = plugin?.Device;
            if (device == null || !device.IsConnected)
                return;

            // ── Display ──────────────────────────────────────────────────
            if (_config.Capabilities.Display != DisplayType.None)
            {
                if (_displayManager == null)
                {
                    _displayManager = new FanatecDisplayManager(device, _customSettings);
                    SimHub.Logging.Current.Info(
                        "FanatecWheelDeviceInstance[" + _config.Capabilities.Name + "]: Created display manager");
                }

                _displayManager.Update(data);
            }

            // ── LEDs ─────────────────────────────────────────────────────
            // LedModuleSettings.Display() evaluates all owned RGBLedsDrivers,
            // builds LedDeviceState, and routes through the FanatecLedManager
            // → FanatecLedButtonsDriver → FanatecDevice.SetLedState().
            _ledModule?.Display();
        }

        public override void End()
        {
            SimHub.Logging.Current.Info(
                "FanatecWheelDeviceInstance[" + _config.Capabilities.Name + "]: End called");

            _displayManager?.Clear();

            // LedModuleSettings may implement IDisposable for cleanup
            try
            {
                (_ledModule as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(
                    "FanatecWheelDeviceInstance: LedModule dispose failed: " + ex.Message);
            }
        }

        public override IEnumerable<DynamicButtonAction> GetDynamicButtonActions()
        {
            return Enumerable.Empty<DynamicButtonAction>();
        }

        public override IEnumerable<DeviceSettingControl> GetSettingsControls()
        {
            EnsureLedModuleInitialized();

            // LED settings tab — native LedModuleSettings UI
            if (_ledModule != null)
            {
                var editControl = _ledModule.EditControl;
                if (editControl != null)
                {
                    yield return new DeviceSettingControl(
                        editControl,
                        0,
                        "LEDs",
                        DeviceSettingControlKind.None,
                        true);
                }
            }

            // Screen settings tab (only for wheels with a display)
            if (_config.Capabilities.Display != DisplayType.None)
            {
                var screenPanel = new ScreenSettingsPanel();
                screenPanel.Bind(_customSettings);
                screenPanel.SettingsChanged += () =>
                {
                    _displayManager?.UpdateSettings(_customSettings);
                };

                yield return new DeviceSettingControl(
                    screenPanel,
                    1,
                    "Screen",
                    DeviceSettingControlKind.None,
                    true);
            }
        }
    }
}
