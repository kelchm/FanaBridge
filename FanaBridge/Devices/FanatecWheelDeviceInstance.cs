using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge;
using FanaBridge.Core;
using FanatecManaged;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using SimHub.Plugins.Devices;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;

namespace FanaBridge.Devices
{
    /// <summary>
    /// DeviceInstance for a specific Fanatec wheel type.
    ///
    /// Owns a single <c>LedModuleSettings</c> that handles all LED types
    /// (Rev, Flag, Button/Encoder) through the unified <c>FanatecLedDriver</c>.
    /// All LEDs appear under a single "LEDs" tab in SimHub's LED Editor.
    ///
    /// Provides the native SimHub LED profile editor, settings persistence,
    /// brightness controls, and the full .shdevice export structure.
    ///
    /// Does NOT own hardware — delegates to the shared FanatecPlugin singleton
    /// for all SDK/HID access. Reports Connected only when the singleton's
    /// current wheel identity matches this instance's wheel type.
    /// </summary>
    public class FanatecWheelDeviceInstance : DeviceInstance
    {
        private readonly DeviceConfig _config;
        private JObject _customSettings = new JObject();

        // LED module (col03) — null when wheel has no LEDs.
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
        /// Lazily creates the LedModuleSettings for this device.
        /// A single module handles all LED types (Rev, Flag, Button/Encoder)
        /// through the unified <see cref="FanatecLedDriver"/>.
        /// </summary>
        private void EnsureLedModuleInitialized()
        {
            if (_ledModuleInitialized)
                return;
            _ledModuleInitialized = true;

            var caps = _config.Capabilities;
            int allLeds = caps.AllLedCount;

            if (allLeds == 0) return;

            var manager = new FanatecLedManager(caps);
            var options = new LedModuleOptions
            {
                DeviceName = caps.ShortName ?? caps.Name,
                LedCount = caps.RevFlagCount,
                ButtonsCount = caps.ButtonLedCount,
                EncodersCount = 0,  // all non-rev/flag LEDs are "buttons" in SimHub
                RawLedCount = allLeds,
                LedDriver = manager,
                EnableBrightnessSection = true,
                ShowConnectionStatus = true,
                VID = FanatecSdkManager.FANATEC_VENDOR_ID,
            };

            _ledModule = new LedModuleSettings<FanatecLedManager>(options);
            _ledModule.IsEmbedded = true;
            _ledModule.IsEnabled = true;
            _ledModule.IndividualLEDsMode = IndividualLEDsMode.Combined;

            SimHub.Logging.Current.Info(
                "FanatecWheelDeviceInstance[" + caps.Name + "]: LED module created (" +
                "rev=" + caps.RevLedCount + ", flag=" + caps.FlagLedCount +
                ", color=" + caps.ColorLedCount + ", mono=" + caps.MonoLedCount +
                ", total=" + allLeds + ")");
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

            // While the wizard is probing hardware, suspend all output so
            // SimHub's per-frame LED writes don't overwrite the test signals.
            if (plugin.WizardActive)
                return;

            // ── Display ──────────────────────────────────────────────────
            if (_config.Capabilities.Display != DisplayType.None)
            {
                if (_displayManager == null)
                {
                    _displayManager = new FanatecDisplayManager(plugin.Display, _customSettings);
                    SimHub.Logging.Current.Info(
                        "FanatecWheelDeviceInstance[" + _config.Capabilities.Name + "]: Created display manager");
                }

                _displayManager.Update(data);
            }

            // ── LEDs ─────────────────────────────────────────────────────
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

            // LED settings tab
            var ledEditControl = _ledModule?.EditControl;
            if (ledEditControl != null)
            {
                yield return new DeviceSettingControl(
                    ledEditControl,
                    0,
                    "LEDs",
                    DeviceSettingControlKind.None,
                    true);
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

            // Tuning settings tab (only for wheels with encoders)
            if (_config.Capabilities.HasEncoders)
            {
                var tuningPanel = new TuningSettingsPanel();
                tuningPanel.Bind(_customSettings);
                tuningPanel.SettingsChanged += () =>
                {
                    // Persist settings on change (handled by SimHub)
                };

                yield return new DeviceSettingControl(
                    tuningPanel,
                    2,
                    "Tuning",
                    DeviceSettingControlKind.None,
                    true);
            }
        }
    }
}
