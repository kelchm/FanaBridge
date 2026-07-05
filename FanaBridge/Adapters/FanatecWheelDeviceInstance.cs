using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using FanaBridge.UI;
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
    /// for all HID access. Reports Connected only when the singleton's
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

        // LED settings/defaults that arrived while the module didn't exist yet
        // (SimHub can deliver SetSettings/LoadDefaultSettings before FanatecPlugin
        // finishes Init). Applied by EnsureLedModuleInitialized right after the
        // module is built, so an early payload is deferred instead of dropped.
        private JObject _pendingLedSettings;
        private bool _pendingLedSettingsIsDefault;
        private bool _pendingLedDefaults;

        // Display manager — null when the wheel has no display.
        private FanatecDisplayDriver _displayManager;
        private DisplaySettings _displaySettings = new DisplaySettings();

        // ITM display driver — null until a wheel with an ITM display is driven.
        private ItmDisplayDriver _itmDisplay;
        private bool _itmWasRunning;
        private bool _itmErrorLogged;
        // True once the legacy page has been blanked after switching to mode "None",
        // so it is cleared once on the transition rather than every frame.
        private bool _legacyBlanked;

        // Track connection state transitions for cleanup on disconnect.
        private bool _wasConnected;

        // Registered once with the plugin so it can read this device's display name
        // for the Control Mapper integration. Lazy (in DataUpdate) so it doesn't
        // depend on construction-vs-plugin-Init ordering.
        private bool _registeredWithPlugin;

        // The plugin generation the cached drivers were built against. SimHub
        // keeps DeviceInstances alive across in-process plugin manager restarts,
        // but FanatecPlugin can still be replaced (e.g. disabled then re-enabled)
        // — after which the drivers above hold encoders bound to a disposed
        // transport (issue #37). When Instance no longer matches, the drivers
        // are dropped/rebuilt against the current generation.
        private FanatecPlugin _boundPlugin;

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

            // Without the shared encoders we can't build a module at all. Leave
            // the initialized flag unset so the next call retries — latching it
            // here would permanently kill this instance's LED module just
            // because it raced ahead of plugin Init.
            var plugin = FanatecPlugin.Instance;
            if (plugin == null) return;

            _ledModuleInitialized = true;
            _boundPlugin = plugin;

            // Resolve the caps for THIS descriptor (live caps only when the
            // connected wheel matches us; otherwise our registration caps).
            var caps = plugin.ResolveCapsFor(_config);
            int allLeds = caps.AllLedCount;

            if (allLeds == 0) return;

            _manager = new FanatecLedManager(_config, plugin.Leds, plugin.LegacyLeds);
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
                VID = FanatecWheelbase.FANATEC_VENDOR_ID,
            };

            _ledModule = new LedModuleSettings<FanatecLedManager>(options);
            _ledModule.IsEmbedded = true;
            _ledModule.IsEnabled = true;

            SimHub.Logging.Current.Info(
                "FanatecWheelDeviceInstance[" + caps.Name + "]: LED module created (" +
                "revRgb=" + caps.RevRgbCount + ", flagRgb=" + caps.FlagRgbCount +
                ", buttonRgb=" + caps.ButtonRgbCount + ", buttonAuxIntensity=" + caps.ButtonAuxIntensityCount +
                ", total=" + allLeds + ")");

            // Apply anything SimHub delivered before the module existed.
            if (_pendingLedSettings != null)
            {
                ApplyLedSettings(_pendingLedSettings, _pendingLedSettingsIsDefault);
                _pendingLedSettings = null;
            }
            else if (_pendingLedDefaults)
            {
                _ledModule.LoadDefaults();
            }
            _pendingLedDefaults = false;
        }

        /// <summary>
        /// Applies a saved settings payload to the LED module (module-level
        /// state such as brightness first, then per-channel profile data).
        /// </summary>
        private void ApplyLedSettings(JObject obj, bool isDefault)
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

        /// <summary>
        /// Drops every driver bound to a previous plugin generation so it is
        /// rebuilt against the current one. The LED module itself survives
        /// (its settings and UI bindings must persist); only its driver is
        /// closed, which makes the base class re-request one via
        /// <c>FanatecLedManager.GetDriver()</c> — that path resolves the live
        /// encoders from <see cref="FanatecPlugin.Instance"/>.
        /// </summary>
        private void RebindToCurrentGeneration()
        {
            SimHub.Logging.Current.Info(
                "FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                "]: Plugin generation changed — rebinding drivers to the current hardware core");

            _manager?.Close();

            // Display/ITM drivers hold their encoder for life — recreate them
            // lazily (DataUpdate builds them on demand from the live plugin).
            _displayManager = null;
            _itmDisplay = null;
            _itmWasRunning = false;
            _itmErrorLogged = false;
            _legacyBlanked = false;

            // Re-register with the new plugin's instance list (used by the
            // Control Mapper integration to read display names).
            _registeredWithPlugin = false;
        }

        // ── DeviceInstance overrides ─────────────────────────────────────

        public override void LoadDefaultSettings()
        {
            SimHub.Logging.Current.Info(
                "FanatecWheelDeviceInstance[" + _config.Capabilities.Name + "]: LoadDefaultSettings");

            EnsureLedModuleInitialized();

            _customSettings = new JObject
            {
                ["wheelType"] = _config.WheelCode ?? "",
                ["moduleType"] = _config.ModuleCode ?? "",
                ["displayMode"] = DisplaySettings.DefaultMode,
                ["itmEnabled"] = DisplaySettings.DefaultItmEnabled,
                ["itmShowLapTotal"] = DisplaySettings.DefaultShowLapTotal,
                ["itmShowPositionTotal"] = DisplaySettings.DefaultShowPositionTotal,
                ["itmDefaultPage"] = DisplaySettings.DefaultItmDefaultPage,
            };
            _displaySettings = new DisplaySettings();

            if (_ledModule != null)
            {
                _ledModule.LoadDefaults();
            }
            else
            {
                // Module not buildable yet — remember that defaults were requested
                // so EnsureLedModuleInitialized applies them on creation.
                _pendingLedDefaults = true;
                _pendingLedSettings = null;
            }
        }

        public override DeviceState GetDeviceState()
        {
            var plugin = FanatecPlugin.Instance;
            if (plugin == null)
                return DeviceState.Disabled;

            // ARCHITECTURE: this reaches directly into wheelbase identity fields.
            // When the peripheral model lands, bind to a peripheral snapshot (class
            // + code + capabilities) instead, so a DeviceInstance can represent
            // pedals/shifter (hosted or standalone), not just a base attachment.
            var wheelbase = plugin.Wheelbase;
            if (wheelbase == null || !wheelbase.IsConnected)
                return DeviceState.Scanning;

            // While the attachment identity is settling (mid-transition), treat the
            // device as not-yet-connected so no LED/display output is driven at a
            // half-(re)connected wheel.
            if (!wheelbase.IdentityStable)
                return DeviceState.Scanning;

            if (!_config.MatchesAttachment(
                    wheelbase.WheelDetected, wheelbase.WheelCode, wheelbase.ModuleCode))
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
            foreach (var key in new[] { "wheelType", "moduleType", "displayMode", "itmEnabled",
                                        "itmShowLapTotal", "itmShowPositionTotal", "itmDefaultPage" })
            {
                if (obj[key] != null)
                    _customSettings[key] = obj[key].DeepClone();
            }

            if (_ledModule != null)
            {
                ApplyLedSettings(obj, isDefault);
                _pendingLedSettings = null;
                _pendingLedDefaults = false;
            }
            else
            {
                // Module not buildable yet (plugin still initializing) — keep the
                // payload so EnsureLedModuleInitialized can apply it on creation.
                _pendingLedSettings = (JObject)obj.DeepClone();
                _pendingLedSettingsIsDefault = isDefault;
                _pendingLedDefaults = false;
            }

            _displaySettings = new DisplaySettings
            {
                DisplayMode = (string)_customSettings["displayMode"] ?? DisplaySettings.DefaultMode,
                ItmEnabled = (bool?)_customSettings["itmEnabled"] ?? DisplaySettings.DefaultItmEnabled,
                ItmShowLapTotal = (bool?)_customSettings["itmShowLapTotal"] ?? DisplaySettings.DefaultShowLapTotal,
                ItmShowPositionTotal = (bool?)_customSettings["itmShowPositionTotal"] ?? DisplaySettings.DefaultShowPositionTotal,
                ItmDefaultPage = (byte?)_customSettings["itmDefaultPage"] ?? DisplaySettings.DefaultItmDefaultPage,
            };

            _displayManager?.UpdateSettings(_displaySettings);
        }

        public override void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // Generation guard: if the plugin was replaced since our drivers were
            // built, drop them so they rebuild against the current hardware core
            // (see _boundPlugin). Must run before anything below touches them.
            var currentPlugin = FanatecPlugin.Instance;
            if (currentPlugin != null && _boundPlugin != null
                && !ReferenceEquals(_boundPlugin, currentPlugin))
            {
                RebindToCurrentGeneration();
            }
            if (currentPlugin != null)
                _boundPlugin = currentPlugin;

            if (!_registeredWithPlugin && currentPlugin != null)
            {
                currentPlugin.RegisterDeviceInstance(this);
                _registeredWithPlugin = true;
            }

            bool isConnected = GetDeviceState() == DeviceState.Connected;

            // Detect Connected → Scanning transition
            if (_wasConnected && !isConnected)
            {
                SimHub.Logging.Current.Info(
                    "FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                    "]: Lost connection");

                _displayManager?.Clear();
                _itmDisplay?.Stop();
                _itmWasRunning = false;
                // Reset one-shot latches so a reconnect starts clean: errors can log again
                // and the legacy page can re-blank when the mode is "None".
                _itmErrorLogged = false;
                _legacyBlanked = false;
            }

            _wasConnected = isConnected;

            if (!isConnected)
                return;

            EnsureLedModuleInitialized();

            var plugin = FanatecPlugin.Instance;
            var device = plugin?.Transport;
            if (device == null || !device.IsConnected)
                return;

            // While the wizard is probing hardware, suspend all output so
            // SimHub's per-frame LED writes don't overwrite the test signals.
            if (plugin.WizardActive)
                return;

            // ── Display ──────────────────────────────────────────────────
            var displayType = _config.Capabilities.Display;
            if (displayType == DisplayType.Itm)
            {
                if (_itmDisplay == null)
                {
                    _itmDisplay = new ItmDisplayDriver(plugin.Itm,
                        log: msg => SimHub.Logging.Current.Info("FanaBridge: " + msg),
                        deviceId: _config.Capabilities.ItmDeviceId);
                    SimHub.Logging.Current.Info(
                        "FanatecWheelDeviceInstance[" + _config.Capabilities.Name + "]: Created ITM display driver");
                }

                // Guard the ITM/legacy display work: an exception here (firmware quirk,
                // transport hiccup) must not skip the LED update further down this same
                // DataUpdate call. Log the first failure, then stay quiet to avoid spam.
                try
                {
                    _itmDisplay.Enabled = _displaySettings.ItmEnabled;
                    if (_displaySettings.ItmEnabled)
                        _itmDisplay.Start();   // idempotent — re-arms bring-up after a disconnect
                    _itmDisplay.ShowLapTotal = _displaySettings.ItmShowLapTotal;
                    _itmDisplay.ShowPositionTotal = _displaySettings.ItmShowPositionTotal;
                    _itmDisplay.DefaultPage = _displaySettings.ItmDefaultPage;

                    // Feed the firmware's pushed ITM subscription reports (col03-IN) to the
                    // driver so it follows the page the wheel button selects.
                    plugin.Wheelbase?.DrainItmReports(_itmDisplay.OnSubscriptionReport);

                    _itmDisplay.Update(data);

                    // Log the bring-up completing once, so hardware verification can
                    // confirm from the SimHub log that ITM went live.
                    if (_itmDisplay.IsRunning && !_itmWasRunning)
                        SimHub.Logging.Current.Info(
                            "FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                            "]: ITM enabled — following firmware subscriptions");
                    _itmWasRunning = _itmDisplay.IsRunning;

                    // Optionally also drive the legacy 7-segment gear/speed over col01. On an
                    // ITM OLED (e.g. PBME) the firmware renders this on its legacy page. This
                    // adds col01 traffic interleaved with col03 ITM, which can destabilise the
                    // firmware under load, so it is opt-in — the "Legacy Display Mode" dropdown,
                    // where "None" leaves it off.
                    if (_displaySettings.DisplayMode != DisplaySettings.ModeNone)
                    {
                        if (_displayManager == null)
                            _displayManager = new FanatecDisplayDriver(plugin.Display, _displaySettings);
                        _displayManager.Update(data);
                        _legacyBlanked = false;
                    }
                    else if (_displayManager != null && !_legacyBlanked)
                    {
                        // Switched to None — blank the legacy page once. Only latch
                        // when the blanking write was accepted, so a transient
                        // transport failure gets retried instead of leaving the
                        // page frozen on its last value.
                        _legacyBlanked = _displayManager.Clear();
                    }
                }
                catch (Exception ex)
                {
                    if (!_itmErrorLogged)
                    {
                        _itmErrorLogged = true;
                        SimHub.Logging.Current.Error(
                            "FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                            "]: ITM display update failed (LEDs unaffected): " + ex);
                    }
                }
            }
            else if (displayType != DisplayType.None)
            {
                if (_displayManager == null)
                {
                    _displayManager = new FanatecDisplayDriver(plugin.Display, _displaySettings);
                    SimHub.Logging.Current.Info(
                        "FanatecWheelDeviceInstance[" + _config.Capabilities.Name + "]: Created display manager");
                }

                _displayManager.Update(data);
            }

            // ── LEDs ─────────────────────────────────────────────────────
            // Hot-swap the driver if the active profile changed (e.g. user
            // picked a different override in the settings dropdown).
            if (_manager != null)
            {
                // Use the per-descriptor resolution, not the global caps — a
                // non-matching device resolves to its own registration profile
                // and so never hot-swaps to the connected wheel's profile.
                var currentCaps = plugin.ResolveCapsFor(_config);
                if (currentCaps?.Profile != null)
                    _manager.HotSwapIfNeeded(currentCaps);
            }

            _ledModule?.Display();
        }

        public override void End()
        {
            SimHub.Logging.Current.Info(
                "FanatecWheelDeviceInstance[" + _config.Capabilities.Name + "]: End called");

            FanatecPlugin.Instance?.UnregisterDeviceInstance(this);
            _displayManager?.Clear();
            _itmDisplay?.Stop();
            _ledModule?.FinalizeModule();
        }

        public override IEnumerable<DynamicButtonAction> GetDynamicButtonActions()
        {
            EnsureLedModuleInitialized();
            return _ledModule?.GetDynamicActions() ?? Enumerable.Empty<DynamicButtonAction>();
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
                screenPanel.Bind(_displaySettings, _config.Capabilities.Display, _config.Capabilities.ItmDeviceId);
                screenPanel.SettingsChanged += () =>
                {
                    // Sync back to JObject for persistence.
                    _customSettings["displayMode"] = _displaySettings.DisplayMode;
                    _customSettings["itmEnabled"] = _displaySettings.ItmEnabled;
                    _customSettings["itmShowLapTotal"] = _displaySettings.ItmShowLapTotal;
                    _customSettings["itmShowPositionTotal"] = _displaySettings.ItmShowPositionTotal;
                    _customSettings["itmDefaultPage"] = _displaySettings.ItmDefaultPage;
                    _displayManager?.UpdateSettings(_displaySettings);
                    // ITM driver reads _displaySettings live each frame.
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
