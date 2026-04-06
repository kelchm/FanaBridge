using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using FanaBridge.UI;
using FanatecManaged;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using SimHub.Plugins;
using SimHub.Plugins.Devices;
using SimHub.Plugins.OutputPlugins.GraphicalDash.LedModules;

namespace FanaBridge.Adapters
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

        // LED module — null when wheel has no LEDs.
        private LedModuleSettings<FanatecLedManager> _ledModule;
        private FanatecLedManager _manager;

        private bool _ledModuleInitialized;

        // Display controller — null when the wheel has no display.
        private SegmentDisplayController _displayController;
        private DisplaySettings _displaySettings = DisplaySettings.CreateDefault();

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

            // Use the currently-active profile (which respects user overrides)
            // when it matches THIS device.  Otherwise fall back to the registry
            // config — CurrentCapabilities is global and would leak the connected
            // wheel's caps into unrelated device instances.
            var caps = _config.Capabilities;
            var plugin = FanatecPlugin.Instance;
            if (plugin != null)
            {
                var sdk = plugin.SdkManager;
                var current = plugin.CurrentCapabilities;
                if (current?.Profile != null && current != WheelCapabilities.None
                    && sdk != null
                    && _config.WheelType == sdk.SteeringWheelType
                    && _config.ModuleType == sdk.SubModuleType)
                {
                    caps = current;
                }
            }
            int allLeds = caps.AllLedCount;

            if (allLeds == 0) return;

            if (plugin == null) plugin = FanatecPlugin.Instance;
            _manager = new FanatecLedManager(caps, plugin.Leds, plugin.LegacyLeds, plugin.Device);
            var manager = _manager;
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

            SimHub.Logging.Current.Info(
                "FanatecWheelDeviceInstance[" + caps.Name + "]: LED module created (" +
                "revRgb=" + caps.RevRgbCount + ", flagRgb=" + caps.FlagRgbCount +
                ", buttonRgb=" + caps.ButtonRgbCount + ", buttonAuxIntensity=" + caps.ButtonAuxIntensityCount +
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
            };
            _displaySettings = DisplaySettings.CreateDefault();

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

            if (_ledModule != null)
            {
                try
                {
                    // Serialize the module object itself (brightness, IndividualLEDsMode, etc.)
                    // under "ledModuleSettings" — matches how LedModuleDevice does it.
                    result["ledModuleSettings"] = JToken.FromObject(_ledModule);

                    // Per-channel profile data (leds, buttons, raw, …)
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

            // Custom settings (wheel/module identity)
            if (_customSettings != null)
            {
                foreach (var prop in _customSettings.Properties())
                {
                    // Skip legacy displayMode — we now serialize screens
                    if (prop.Name == "displayMode") continue;
                    result[prop.Name] = prop.Value.DeepClone();
                }
            }

            // Serialize current display settings
            result["layers"] = JArray.FromObject(_displaySettings.Layers);
            result["scrollSpeedMs"] = _displaySettings.ScrollSpeedMs;

            return result;
        }

        public override void SetSettings(JToken settings, bool isDefault)
        {
            if (!(settings is JObject obj))
                return;

            EnsureLedModuleInitialized();

            // Extract custom settings
            _customSettings = new JObject();
            foreach (var key in new[] { "wheelType", "moduleType",
                                        "layers", "screens", "overlays",
                                        "scrollSpeedMs", "gearOverlayEnabled",
                                        "gearOverlayDurationMs", "displayMode" })
            {
                if (obj[key] != null)
                    _customSettings[key] = obj[key].DeepClone();
            }

            if (_ledModule != null)
            {
                try
                {
                    // Restore module-level state (brightness, IndividualLEDsMode, etc.)
                    // before passing channel profiles, matching LedModuleDevice.SetSettings.
                    var moduleToken = obj["ledModuleSettings"];
                    if (moduleToken != null)
                        Newtonsoft.Json.JsonConvert.PopulateObject(moduleToken.ToString(), _ledModule);

                    // Per-channel profile data (leds, buttons, raw, …)
                    var dict = new Dictionary<string, JToken>();
                    foreach (var prop in obj.Properties())
                        dict[prop.Name] = prop.Value;
                    _ledModule.SetSettings(dict, isDefault);
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn(
                        "FanatecWheelDeviceInstance: SetSettings(LED) failed: " + ex.Message);
                }
            }

            // Restore display settings — migrate from legacy formats if needed.
            // Wrapped in try/catch so a malformed entry doesn't prevent device loading.
            try
            {
                if (_customSettings["layers"] != null)
                {
                    _displaySettings = new DisplaySettings();
                    var layersArray = _customSettings["layers"] as JArray;
                    if (layersArray != null)
                    {
                        foreach (var token in layersArray)
                        {
                            try
                            {
                                var layer = token.ToObject<DisplayLayer>();
                                if (layer != null)
                                    _displaySettings.Layers.Add(layer);
                            }
                            catch (System.Exception ex)
                            {
                                SimHub.Logging.Current.Warn("FanatecWheelDeviceInstance: Skipping malformed layer: " + ex.Message);
                            }
                        }
                    }
                    var speedToken = _customSettings["scrollSpeedMs"];
                    if (speedToken != null)
                        _displaySettings.ScrollSpeedMs = speedToken.Value<int>();
                }
                else if (_customSettings["displayMode"] != null)
                {
                    _displaySettings = DisplaySettings.MigrateFromLegacy(
                        (string)_customSettings["displayMode"]);
                    SimHub.Logging.Current.Info(
                        "FanatecWheelDeviceInstance: Migrated legacy displayMode to layer-based settings");
                }
                else
                {
                    _displaySettings = DisplaySettings.CreateDefault();
                }
            }
            catch (System.Exception ex)
            {
                SimHub.Logging.Current.Warn("FanatecWheelDeviceInstance: Failed to restore display settings, using defaults: " + ex.Message);
                _displaySettings = DisplaySettings.CreateDefault();
            }
            _displayController?.UpdateSettings(_displaySettings);
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

                _displayController?.Clear();
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

            // ── Display (ITM falls back to basic 7-seg until ITM support is implemented) ──
            if (_config.Capabilities.Display != DisplayType.None)
            {
                if (_displayController == null)
                {
                    _displayController = new SegmentDisplayController(plugin.SegmentEncoder, _displaySettings);
                    SimHub.Logging.Current.Info(
                        "FanatecWheelDeviceInstance[" + _config.Capabilities.Name + "]: Created display manager");
                }

                _displayController.Update(pluginManager, data);
            }

            // ── LEDs ─────────────────────────────────────────────────────
            // Hot-swap the driver if the active profile changed (e.g. user
            // picked a different override in the settings dropdown).
            if (_manager != null)
            {
                var currentCaps = plugin.CurrentCapabilities;
                if (currentCaps?.Profile != null)
                    _manager.HotSwapIfNeeded(currentCaps);
            }

            _ledModule?.Display();
        }

        public override void End()
        {
            SimHub.Logging.Current.Info(
                "FanatecWheelDeviceInstance[" + _config.Capabilities.Name + "]: End called");

            _displayController?.Clear();
            _ledModule?.FinalizeModule();
        }

        public override IEnumerable<DynamicButtonAction> GetDynamicButtonActions()
        {
            EnsureLedModuleInitialized();

            var actions = new List<DynamicButtonAction>();

            // LED actions
            var ledActions = _ledModule?.GetDynamicActions();
            if (ledActions != null)
                actions.AddRange(ledActions);

            // Display screen cycling actions
            if (_config.Capabilities.Display != DisplayType.None)
            {
                var next = new DynamicButtonAction("Next Screen",
                    (pm, inputName) => _displayController?.NextScreen());
                actions.Add(next);

                var prev = new DynamicButtonAction("Previous Screen",
                    (pm, inputName) => _displayController?.PreviousScreen());
                actions.Add(prev);
            }

            return actions;
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

            // Screen settings tab (only for wheels with a display).
            // _displayController may be null here if DataUpdate() hasn't run yet;
            // ScreenSettingsPanel.Bind accepts null and falls back to preview-only mode.
            if (_config.Capabilities.Display != DisplayType.None)
            {
                var screenPanel = new ScreenSettingsPanel();
                screenPanel.Bind(_displaySettings, _config.Capabilities.Display, _displayController);
                screenPanel.SettingsChanged += () =>
                {
                    // Sync back to JObject for persistence
                    _customSettings["layers"] = JArray.FromObject(_displaySettings.Layers);
                    _customSettings["scrollSpeedMs"] = _displaySettings.ScrollSpeedMs;
                    _displayController?.UpdateSettings(_displaySettings);
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
