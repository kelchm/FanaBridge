using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge;
using FanaBridge.Customization;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
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
    public class FanatecWheelDeviceInstance : DeviceInstance,
        IDisplayPanelHost, IDisplayPropertyCatalog, IMappedRoleCatalog
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

        // Late accessor over _displaySettings, handed to the runtime's per-frame Tick so the
        // settings read happens INSIDE the runtime AFTER its volatile _displayConfig acquire
        // (see SetSettings). A pre-evaluated argument at the Tick call site would read the
        // plain _displaySettings BEFORE the acquire and reintroduce the torn base-page pair.
        // One cached delegate — it reads the current field on each invocation.
        private Func<DisplaySettings> _displaySettingsAccessor;

        // The device-scoped ITM display session: the col03 ITM driver, its wire-driven
        // digital twin, the display-customization rule stack + volatile config snapshot,
        // page-policy sequencing, the ITM status line, and the ONE UI-facing envelope all
        // live here now. This device shell keeps LEDs, identity, connection, the settings
        // bag, and the legacy col01 driver, and delegates the ITM session to the runtime
        // (one Tick per connected ITM frame plus the lifecycle edges). Built once in the
        // ctor — cheap until Tick constructs a driver.
        private readonly DeviceDisplayRuntime _displayRuntime;

        // True once the legacy page has been blanked after switching to mode "None",
        // so it is cleared once on the transition rather than every frame.
        private bool _legacyBlanked;
        // One-shot guard for the legacy col01 drive: this instance-side drive used to sit
        // inside the runtime's ITM try/catch (sharing its _itmErrorLogged latch) and must
        // keep the same contract now that it runs on the instance — a firmware/transport
        // hiccup logs once and MUST NOT skip the LED update further down this DataUpdate.
        // Reset on the same edges the runtime resets its own latch: disconnect and rebind.
        private bool _legacyErrorLogged;
        // Tracks the settings page's display test so the handback edge (test just
        // ended) can blank the residue and reset the driver's value latches.
        private bool _displayTestWasActive;

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

        // Test seam for the issue-#37 generation matrix: resolves the current
        // plugin generation everywhere this class would read the singleton.
        // Production keeps the default; tests substitute plugin generations to
        // drive the null→A, A→A, A→B, and connected→scanning transitions.
        internal Func<FanatecPlugin> PluginResolver = () => FanatecPlugin.Instance;

        /// <summary>Test hook: the generation the cached drivers were built against.</summary>
        internal FanatecPlugin BoundPluginForTest => _boundPlugin;

        /// <summary>
        /// The ITM lifecycle status line for the Device Status panel, or null when this
        /// instance isn't driving an ITM display. Forwards to the display runtime's
        /// published envelope (FanatecPlugin.ItmStatus reads this off the registered
        /// instance), so it is safe to read from any thread and always consistent with
        /// what the Display tab shows.
        /// </summary>
        internal string ItmStatusDescription => _displayRuntime.ItmStatusDescription;

        /// <summary>Test seam: forwards to the runtime's rule-part of the display
        /// envelope, or null while no customization is active.</summary>
        internal DisplayRuleSnapshot DisplayRuleSnapshot => _displayRuntime.RuleSnapshot;

        /// <summary>Test seam: forwards to the runtime's values-part of the display
        /// envelope (what the ITM display is showing), or null while not driving ITM.</summary>
        internal DisplayValuesSnapshot DisplayValuesSnapshot => _displayRuntime.ValuesSnapshot;

        /// <summary>Test hook (parity gate): the rule stack, null when nothing is built.</summary>
        internal DisplayRuleStack DisplayStackForTest => _displayRuntime.Stack;

        /// <summary>Test hook: the ITM driver, null until an ITM display is driven.</summary>
        internal ItmDisplayDriver ItmDisplayForTest => _displayRuntime.ItmDriver;

        /// <summary>Test/R2b seam: the display runtime this device shell delegates the
        /// ITM session to.</summary>
        internal DeviceDisplayRuntime DisplayRuntimeForTest => _displayRuntime;

        // Test seam: injected clock for the ITM driver, so wiring tests (notably the
        // byte-parity gate) run fully deterministic scripted sessions instead of pacing
        // a driver-owned stopwatch with real sleeps. Production keeps the default
        // (null = the driver starts its own stopwatch). The runtime reads it late through
        // a Func<long> accessor supplied at construction, so a test's post-construction
        // assignment is honored.
        internal Func<long> ItmClockForTest;

        public FanatecWheelDeviceInstance(DeviceConfig config)
        {
            _config = config;
            _displaySettingsAccessor = () => _displaySettings;
            _displayRuntime = new DeviceDisplayRuntime(_config,
                itmClock: () => ItmClockForTest,
                log: msg => SimHub.Logging.Current.Info(msg));
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
            var plugin = PluginResolver();
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

            // The legacy col01 display driver holds its encoder for life — recreate it
            // lazily (DataUpdate builds it on demand from the live plugin).
            _displayManager = null;
            // The ITM session's driver-adjacent objects (driver, twin, rule stack, status
            // line, published envelope) are all invalidated by the generation swap — the
            // runtime drops them and nulls the envelope the instant the driver is
            // invalidated so the UI can never read a disposed generation's parts (issue #37;
            // the twin is dropped WITHOUT a detach — the old tap is already gone).
            _displayRuntime.OnGenerationRebind();
            _legacyBlanked = false;
            // The col01 driver is dropped/rebuilt against the new generation below — re-arm
            // its one-shot failure latch too, matching the runtime's _itmErrorLogged reset.
            _legacyErrorLogged = false;

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
            _displayRuntime.ClearConfig();   // no displayCustomization key = no customization

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
            var plugin = PluginResolver();
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

            // Display customization: serialize the CURRENT snapshot (not the raw
            // payload) — the EnumText model keeps values a future version wrote
            // intact through the load/save round-trip.
            var displayConfig = _displayRuntime.CurrentConfig;
            if (displayConfig != null)
                result["displayCustomization"] =
                    JObject.Parse(DisplayConfigSerializer.Save(displayConfig));

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

            // The display settings must be rebuilt BEFORE the customization snapshot is
            // handed to the runtime below. Write order here: plain-write _displaySettings,
            // THEN the runtime's volatile config release. The frame path honors the matching
            // read order INSIDE the runtime — it volatile-acquires the config first and only
            // then reads _displaySettings through _displaySettingsAccessor (never a value
            // pre-evaluated at the Tick call site). That acquire-before-settings pairing
            // guarantees a frame that sees the new config also sees the settings that arrived
            // with it. The rule stack captures ItmDefaultPage at build time — a torn pair
            // would latch a stale base page until the next rebuild.
            _displaySettings = new DisplaySettings
            {
                DisplayMode = (string)_customSettings["displayMode"] ?? DisplaySettings.DefaultMode,
                ItmEnabled = (bool?)_customSettings["itmEnabled"] ?? DisplaySettings.DefaultItmEnabled,
                ItmShowLapTotal = (bool?)_customSettings["itmShowLapTotal"] ?? DisplaySettings.DefaultShowLapTotal,
                ItmShowPositionTotal = (bool?)_customSettings["itmShowPositionTotal"] ?? DisplaySettings.DefaultShowPositionTotal,
                ItmDefaultPage = (byte?)_customSettings["itmDefaultPage"] ?? DisplaySettings.DefaultItmDefaultPage,
            };

            // Display customization document (whitelisted nested key; absent = none).
            // Parsed leniently on this thread — Load never throws — and handed to the
            // runtime as an immutable snapshot (its volatile release); the frame path
            // notices the reference change and rebuilds the rule stack. Released AFTER the
            // plain _displaySettings write above, and paired with the runtime's
            // acquire-before-settings read order, so a frame that sees the new config also
            // sees the settings that arrived with it (the runtime's volatile is the fence).
            // Settings can arrive before plugin Init: the snapshot just waits until the
            // frame path is alive to consume it.
            var displayCustomization = obj["displayCustomization"];
            _displayRuntime.SetConfig(displayCustomization == null
                ? null
                : DisplayConfigSerializer.Load(displayCustomization.ToString(),
                    msg => SimHub.Logging.Current.Warn("FanaBridge: " + msg)));

            _displayManager?.UpdateSettings(_displaySettings);
        }

        public override void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // Generation guard: if the plugin was replaced since our drivers were
            // built, drop them so they rebuild against the current hardware core
            // (see _boundPlugin). Must run before anything below touches them.
            var currentPlugin = PluginResolver();
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
                // The ITM session went cold: stop the driver, cold-start the twin, drop
                // the rule stack + status line, and republish the (now null) envelope —
                // the per-frame Tick below is unreachable while disconnected.
                _displayRuntime.OnDisconnected();
                // Reset the legacy col01 one-shot latches so a reconnect can re-blank the
                // legacy page when the mode is "None" and can log a fresh col01 failure
                // (mirrors the runtime clearing its own _itmErrorLogged on disconnect).
                _legacyBlanked = false;
                _legacyErrorLogged = false;
            }

            _wasConnected = isConnected;

            if (!isConnected)
                return;

            EnsureLedModuleInitialized();

            var plugin = PluginResolver();
            var device = plugin?.Transport;
            if (device == null || !device.IsConnected)
                return;

            // While the wizard is probing hardware, suspend all output so
            // SimHub's per-frame LED writes don't overwrite the test signals.
            if (plugin.WizardActive)
                return;

            // ── Display ──────────────────────────────────────────────────
            // While the settings page's display test owns the 7-segment display,
            // skip the legacy col01 drive so the test text isn't overwritten each
            // frame (LEDs and the col03 ITM display are unaffected). On handback,
            // Clear() blanks the test residue AND resets the driver's value
            // latches, so the live gear/speed repaints immediately instead of
            // waiting for the next value change.
            bool displayTest = plugin.DisplayTestActive;
            if (!displayTest && _displayTestWasActive)
                _displayManager?.Clear();
            _displayTestWasActive = displayTest;

            // Resolve THIS descriptor's caps override-aware — the same rule the
            // LED pipeline already follows via ResolveCapsFor (the single guard).
            // Registration caps froze the built-in profile's display type and ITM
            // device id, so a user override changing either was honored by LEDs
            // but silently ignored by the display drivers — restart or not,
            // because the registry's dedupe keeps the built-in for registration.
            var displayCaps = plugin.ResolveCapsFor(_config);
            var displayType = displayCaps.Display;

            // Switched away from ITM (e.g. override to a basic-display profile): the
            // runtime stops the session (a no-op when it holds no driver) so the next Itm
            // selection re-runs bring-up, and republishes the now-null envelope.
            if (displayType != DisplayType.Itm)
                _displayRuntime.OnDisplayTypeLeftItm(plugin);

            if (displayType == DisplayType.Itm)
            {
                // The runtime owns the whole ITM frame (driver/twin build + hot-swap, the
                // settings apply, wheel-change cold restart, subscription drain, driver +
                // twin tick, co-driver probe, status snapshot, rules, and the envelope
                // publish) under its own try/catch. It reads the settings live through the
                // accessor (AFTER its volatile config acquire — see SetSettings). It returns
                // false if the ITM update threw — in which case we skip the legacy col01
                // drive below exactly as the old shared try/catch did, keeping the LED update
                // (further down) unaffected.
                bool itmOk = _displayRuntime.Tick(
                    plugin, displayCaps, pluginManager, data, _displaySettingsAccessor);

                if (itmOk)
                {
                    // Optionally also drive the legacy 7-segment gear/speed over col01. On an
                    // ITM OLED (e.g. PBME) the firmware renders this on its legacy page. This
                    // adds col01 traffic interleaved with col03 ITM, which can destabilise the
                    // firmware under load, so it is opt-in — the "Legacy Display Mode" dropdown,
                    // where "None" leaves it off.
                    //
                    // Guarded exactly as this drive was before the ITM body moved into the
                    // runtime: it shared the runtime's ITM try/catch then, so a col01
                    // firmware/transport hiccup must still log once and MUST NOT skip the LED
                    // update further down this same DataUpdate. The one-shot _legacyErrorLogged
                    // latch mirrors the runtime's _itmErrorLogged (reset on disconnect/rebind).
                    try
                    {
                        if (_displaySettings.DisplayMode != DisplaySettings.ModeNone)
                        {
                            if (_displayManager == null)
                                _displayManager = new FanatecDisplayDriver(plugin.Display, _displaySettings);
                            if (!displayTest)
                                _displayManager.Update(data);
                            _legacyBlanked = false;
                        }
                        else if (_displayManager != null && !_legacyBlanked && !displayTest)
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
                        if (!_legacyErrorLogged)
                        {
                            _legacyErrorLogged = true;
                            SimHub.Logging.Current.Error(
                                "FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                                "]: ITM display update failed (LEDs unaffected): " + ex);
                        }
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

                if (!displayTest)
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

        // ── Display customization (rules) ────────────────────────────────

        /// <summary>
        /// Publishes a UI-built customization document — the Display tab's ONLY write path
        /// into the config, forwarded to the display runtime (which normalizes through the
        /// settings load path and publishes; the frame path rebuilds the rule stack, and
        /// SimHub persists via <see cref="GetSettings"/> on its own schedule). Tests call
        /// this on the instance and the IDisplayPanelHost member routes here.
        /// </summary>
        internal void ApplyDisplayConfig(DisplayCustomizationConfig config)
            => _displayRuntime.ApplyDisplayConfig(config);

        // ── IDisplayPanelHost (the Display tab's typed window into this instance) ──
        // Explicit implementation: nothing here belongs on the class's public surface
        // — the panel receives the interface from GetSettingsControls and the members
        // simply expose the same state/paths the frame code already maintains.

        DisplaySettings IDisplayPanelHost.DisplaySettings => _displaySettings;

        // The Display tab picks its ITM-vs-basic layout and which page table to populate
        // from DisplayType/ItmDeviceId, so both must report the caps the RUNTIME actually
        // drives: override-resolved via the current plugin (ResolveCapsFor), exactly as the
        // DataUpdate loop resolves them — not the frozen registration caps. Returning
        // registration caps let a profile override retarget the display for the driver
        // while the tab kept rendering the built-in layout / wrong page table. Resolved
        // live on each read, so it stays correct across a generation rebind (PluginResolver
        // returns the current plugin); degrades to the registration caps only when no
        // plugin is live yet, matching ResolveCapsFor's own fallback.
        private WheelCapabilities ResolvedDisplayCaps =>
            PluginResolver()?.ResolveCapsFor(_config) ?? _config.Capabilities ?? WheelCapabilities.None;

        // Whether this device should surface a Display tab. Reads the RESOLVED caps
        // (not the frozen registration caps) so a profile override that retargets the
        // display — a base whose override gains an ITM display, or an ITM wheel
        // overridden onto a display-less profile — is honored: the tab appears or
        // disappears with the display the runtime actually drives.
        internal bool ShouldOfferDisplayTab => ResolvedDisplayCaps.Display != DisplayType.None;

        DisplayType IDisplayPanelHost.DisplayType => ResolvedDisplayCaps.Display;

        byte IDisplayPanelHost.ItmDeviceId => ResolvedDisplayCaps.ItmDeviceId;

        DisplayCustomizationConfig IDisplayPanelHost.GetDisplayConfig() => _displayRuntime.CurrentConfig;

        void IDisplayPanelHost.ApplyDisplayConfig(DisplayCustomizationConfig config)
            => ApplyDisplayConfig(config);

        DisplayPanelSnapshot IDisplayPanelHost.Snapshot => _displayRuntime.Snapshot;

        void IDisplayPanelHost.NotifySettingsChanged()
        {
            // Sync the panel-edited DisplaySettings back to the JObject SimHub
            // persists — the same flow the old Screen panel's callback rode.
            _customSettings["displayMode"] = _displaySettings.DisplayMode;
            _customSettings["itmEnabled"] = _displaySettings.ItmEnabled;
            _customSettings["itmShowLapTotal"] = _displaySettings.ItmShowLapTotal;
            _customSettings["itmShowPositionTotal"] = _displaySettings.ItmShowPositionTotal;
            _customSettings["itmDefaultPage"] = _displaySettings.ItmDefaultPage;
            _displayManager?.UpdateSettings(_displaySettings);
            // ITM driver reads _displaySettings live each frame.
        }

        // ── IDisplayPropertyCatalog / IMappedRoleCatalog (on-demand editor catalogs) ──
        // Narrow contracts the Triggers editor pulls when a picker/dropdown opens — never
        // per frame. Kept on the instance (not the runtime) because both need the live
        // PluginManager + variant, which only the instance owns.

        IReadOnlyList<string> IDisplayPropertyCatalog.GetAllPropertyNames()
        {
            // On demand only (picker open) — the list can hold thousands of names, so it
            // is never fetched per frame. Defensive: no plugin manager (Init not reached)
            // or a SimHub-side throw yields an empty list, never an exception at the panel.
            try
            {
                var pm = PluginResolver()?.PluginManager;
                var names = pm?.GetAllPropertiesNames();
                return names != null ? new List<string>(names) : (IReadOnlyList<string>)Array.Empty<string>();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Debug(
                    "FanaBridge: GetAllPropertyNames failed: " + ex.GetBaseException().Message);
                return Array.Empty<string>();
            }
        }

        MappedRoles IMappedRoleCatalog.GetMappedRoles()
        {
            // On demand (mapped-control dropdown open). Read-only: the reader never writes
            // to Control Mapper. The rim's own variant is the key FanaBridge already owns
            // (the same string its variant provider emits); the reader nulls it when RIW is
            // off so the single-base row matches instead. Any failure degrades to the
            // sanctioned role catalog, then to empty — never a throw.
            try
            {
                var pm = PluginResolver()?.PluginManager;
                if (pm == null)
                    return MappedRoles.None;
                string variant = FanaBridgeVariantProvider.ComputeCurrentVariant();
                // No DirectInput InterfacePath here: resolving THIS device's path needs a
                // device enumeration (and the live/inert collection can't even be told
                // apart at that layer), so it is disproportionate plumbing for R1. Passing
                // null keeps the RIW-on and single-base RIW-off cases exact; when RIW is
                // off and more than one Fanatec base is mapped, the resolver reports an
                // honest aggregate rather than claiming another base's roles are mapped on
                // this wheel. Full interface-path disambiguation lands in R2.
                return new ControlMapperRoleReader().Read(pm, variant, interfacePath: null);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Debug(
                    "FanaBridge: GetMappedRoles failed: " + ex.GetBaseException().Message);
                return MappedRoles.None;
            }
        }

        public override void End()
        {
            SimHub.Logging.Current.Info(
                "FanatecWheelDeviceInstance[" + _config.Capabilities.Name + "]: End called");

            PluginResolver()?.UnregisterDeviceInstance(this);
            _displayManager?.Clear();
            // The ITM session ends: the runtime stops the driver, detaches + drops the
            // twin, and republishes so the envelope composes a null values part (DataUpdate
            // won't run again for this instance).
            _displayRuntime.OnEnd(PluginResolver());
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

            // Display/Tuning tabs are built through the plugin's panel factory so
            // this Adapters class never references FanaBridge.UI. No current
            // plugin generation → tabs omitted, consistent with the LED tab's
            // degradation via EnsureLedModuleInitialized.
            var panels = PluginResolver()?.PanelFactory;

            // Display settings tab (only for wheels with a display). The instance IS
            // the panel's host — the IDisplayPanelHost members above are its window.
            if (panels != null && ShouldOfferDisplayTab)
            {
                yield return new DeviceSettingControl(
                    panels.CreateDisplayPanel(this, this, this),
                    1,
                    "Display",
                    DeviceSettingControlKind.None,
                    true);
            }

            // Tuning settings tab (only for wheels with encoders)
            if (panels != null && _config.Capabilities.HasEncoders)
            {
                yield return new DeviceSettingControl(
                    panels.CreateTuningPanel(_customSettings),
                    2,
                    "Tuning",
                    DeviceSettingControlKind.None,
                    true);
            }
        }
    }
}
