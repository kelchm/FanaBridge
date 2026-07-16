using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge;
using FanaBridge.Display;
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
        // The display id the ITM driver was built against; an override changing
        // it hot-swaps the driver (deviceId is a ctor-fixed value).
        private byte _itmDeviceId;
        private bool _itmWasRunning;
        private bool _itmErrorLogged;
        // Wheel-change edge detection (polled — no event subscription that could
        // outlive a plugin generation, see issue #37). A wheel/hub/module change
        // resets the display cold with no trace on the ITM channel, so the ITM
        // lifecycle must restart from bring-up.
        private int _itmWheelChangeCount;
        // Display customization: the parsed per-device config snapshot (volatile —
        // written by SetSettings, read on the frame path; a reference swap is the
        // rebuild signal), the rule stack built from it, and the UI-facing snapshot.
        // All three stay null on the empty-config fast path — with no customization
        // the frame path must be byte-identical to a build without the feature.
        private volatile DisplayCustomizationConfig _displayConfig;
        private DisplayRuleStack _displayStack;
        private volatile DisplayRuleSnapshot _displayRuleSnapshot;
        // True once the legacy page has been blanked after switching to mode "None",
        // so it is cleared once on the transition rather than every frame.
        private bool _legacyBlanked;
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

        // ITM status snapshot for the Device Status panel / diagnostics. Composed on the
        // DataUpdate thread (the only thread that mutates the lifecycle) and read from the
        // UI's DispatcherTimer — a volatile string hand-off instead of cross-thread reads
        // of live state-machine fields. Refreshed on state/sync changes plus a coarse
        // 1 s tick (the Unavailable line carries a retry countdown).
        private volatile string _itmStatusSnapshot;
        private ItmLifecycleState _itmSnapState;
        private int _itmSnapGen;
        private int _itmSnapTick;

        private void PublishItmStatusSnapshot(ItmLifecycleState state)
        {
            int gen = _itmDisplay.Lifecycle.SyncGeneration;
            int tick = Environment.TickCount;
            // Wrap-safe elapsed check: Environment.TickCount rolls to int.MinValue every ~24.9
            // days (and net48 has no TickCount64), so measure the delta as an unsigned difference
            // — correct across the wrap, and it never throws even under a checked-arithmetic build.
            if (_itmStatusSnapshot != null && state == _itmSnapState && gen == _itmSnapGen
                && unchecked((uint)(tick - _itmSnapTick)) < 1000)
                return;
            _itmSnapState = state;
            _itmSnapGen = gen;
            _itmSnapTick = tick;
            _itmStatusSnapshot = _itmDisplay.Lifecycle.Describe();
        }

        /// <summary>
        /// The ITM lifecycle status line for the Device Status panel, or null when this
        /// instance isn't driving an ITM display. Safe to read from any thread.
        /// </summary>
        internal string ItmStatusDescription => _itmDisplay == null ? null : _itmStatusSnapshot;

        /// <summary>
        /// The latest display-rules snapshot (intent, per-rule status, recent activity),
        /// or null while no customization is active. Safe to read from any thread — the
        /// future Display tab polls it like <see cref="ItmStatusDescription"/>.
        /// </summary>
        internal DisplayRuleSnapshot DisplayRuleSnapshot => _displayRuleSnapshot;

        /// <summary>
        /// The latest display-values snapshot (what the ITM display is showing, rendered
        /// from the values the driver last sent), or null while this instance isn't
        /// driving an ITM display. Cleared at the same teardown edges as
        /// <see cref="ItmStatusDescription"/>: the driver drops it on Stop (disconnect,
        /// display-type switch, End) and the driver reference itself is dropped on a
        /// generation rebind or display-id change. Safe to read from any thread.
        /// </summary>
        internal DisplayValuesSnapshot DisplayValuesSnapshot => _itmDisplay?.ValuesSnapshot;

        /// <summary>Test hook (parity gate): the rule stack, null when nothing is built.</summary>
        internal DisplayRuleStack DisplayStackForTest => _displayStack;

        /// <summary>Test hook: the ITM driver, null until an ITM display is driven.</summary>
        internal ItmDisplayDriver ItmDisplayForTest => _itmDisplay;

        // Test seam: injected clock for the ITM driver, so wiring tests (notably the
        // byte-parity gate) run fully deterministic scripted sessions instead of pacing
        // a driver-owned stopwatch with real sleeps. Production keeps the default
        // (null = the driver starts its own stopwatch).
        internal Func<long> ItmClockForTest;

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

            // Display/ITM drivers hold their encoder for life — recreate them
            // lazily (DataUpdate builds them on demand from the live plugin).
            _displayManager = null;
            _itmDisplay = null;
            // The rule stack wraps the driver's lifecycle — same fate (issue #37:
            // never keep references into a disposed generation).
            _displayStack = null;
            _displayRuleSnapshot = null;
            // Clear the status cache the instant the driver is invalidated (not just at the
            // rebuild site) so the Device Status row can never read a disposed generation's
            // description in the window before the new driver publishes (issue #37 path).
            _itmStatusSnapshot = null;
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
            _displayConfig = null;   // no displayCustomization key = no customization

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
            var displayConfig = _displayConfig;
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
            // published below: the frame path reads the volatile _displayConfig first and
            // _displaySettings second, so this write order (plain write, then volatile
            // release) guarantees a frame that sees the new config also sees the settings
            // that arrived with it. The rule stack captures ItmDefaultPage at build time —
            // a torn pair would latch a stale base page until the next rebuild.
            _displaySettings = new DisplaySettings
            {
                DisplayMode = (string)_customSettings["displayMode"] ?? DisplaySettings.DefaultMode,
                ItmEnabled = (bool?)_customSettings["itmEnabled"] ?? DisplaySettings.DefaultItmEnabled,
                ItmShowLapTotal = (bool?)_customSettings["itmShowLapTotal"] ?? DisplaySettings.DefaultShowLapTotal,
                ItmShowPositionTotal = (bool?)_customSettings["itmShowPositionTotal"] ?? DisplaySettings.DefaultShowPositionTotal,
                ItmDefaultPage = (byte?)_customSettings["itmDefaultPage"] ?? DisplaySettings.DefaultItmDefaultPage,
            };

            // Display customization document (whitelisted nested key; absent = none).
            // Parsed leniently on this thread — Load never throws — and published as
            // an immutable snapshot; the frame path notices the reference change and
            // rebuilds the rule stack. Settings can arrive before plugin Init: the
            // snapshot just waits here until the frame path is alive to consume it.
            var displayCustomization = obj["displayCustomization"];
            _displayConfig = displayCustomization == null
                ? null
                : DisplayConfigSerializer.Load(displayCustomization.ToString(),
                    msg => SimHub.Logging.Current.Warn("FanaBridge: " + msg));

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
                _itmDisplay?.Stop();
                _itmWasRunning = false;
                _itmStatusSnapshot = null;   // don't show a stale ITM row while disconnected
                _displayStack = null;        // rules restart clean with the reconnect
                _displayRuleSnapshot = null;
                // Reset one-shot latches so a reconnect starts clean: errors can log again
                // and the legacy page can re-blank when the mode is "None".
                _itmErrorLogged = false;
                _legacyBlanked = false;
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

            // Switched away from ITM (e.g. override to a basic-display profile):
            // stop the session so the next Itm selection re-runs bring-up.
            if (displayType != DisplayType.Itm && _itmDisplay != null)
            {
                _itmDisplay.Stop();
                _itmDisplay = null;
                _itmWasRunning = false;
                _displayStack = null;         // rules only drive an ITM display
                _displayRuleSnapshot = null;  // and their published snapshot goes with them
                                              // (UpdateDisplayRules never runs again on this
                                              // path, so nothing else would clear it)
            }

            if (displayType == DisplayType.Itm)
            {
                // Override retargeted the ITM display id — the driver's deviceId
                // is ctor-fixed, so hot-swap it like the LED pipeline does.
                if (_itmDisplay != null && _itmDeviceId != displayCaps.ItmDeviceId)
                {
                    _itmDisplay.Stop();
                    _itmDisplay = null;
                    SimHub.Logging.Current.Info(
                        "FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                        "]: ITM display id changed (" + _itmDeviceId + " → " +
                        displayCaps.ItmDeviceId + ") — rebuilding ITM driver");
                }
                if (_itmDisplay == null)
                {
                    _itmDeviceId = displayCaps.ItmDeviceId;
                    _itmDisplay = new ItmDisplayDriver(plugin.Itm,
                        nowMs: ItmClockForTest,
                        log: msg => SimHub.Logging.Current.Info("FanaBridge: " + msg),
                        deviceId: _itmDeviceId);
                    // Baseline the wheel-change counter at creation — the driver is starting
                    // cold anyway, so changes before this point are already accounted for.
                    _itmWheelChangeCount = plugin.Wheelbase?.WheelChangeCount ?? 0;
                    // Drop any status snapshot cached from a disposed generation's controller,
                    // so the Device Status row never shows the old controller's description
                    // (a plugin-generation rebind or a display-id change rebuilds the driver here).
                    _itmStatusSnapshot = null;
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
                    // While display rules are active they own page policy: the driver's
                    // default page follows the stack's base page (the config's base page
                    // when set), and the lifecycle's own game-start revert is suppressed —
                    // the rule engine performs that revert itself (resting target → base
                    // on the in-game rising edge, routed through the page director), and
                    // a lifecycle-initiated switch would read upstream as phantom
                    // wheel-button navigation, dismissing rules the user never touched.
                    // _displayStack is last frame's (UpdateDisplayRules runs below);
                    // one frame of lag on build/teardown is harmless at frame cadence.
                    var rules = _displayStack;
                    _itmDisplay.DefaultPage = rules != null
                        ? rules.BaseWirePage : _displaySettings.ItmDefaultPage;
                    _itmDisplay.GameStartPageRevert = rules == null;

                    // A wheel/hub/module change (identity layer, FF 08) resets the display to
                    // a cold state that is invisible on the ITM channel — restart the ITM
                    // lifecycle from bring-up. Polled via the monotonic counter.
                    int wheelChanges = plugin.Wheelbase?.WheelChangeCount ?? 0;
                    if (wheelChanges != _itmWheelChangeCount)
                    {
                        _itmWheelChangeCount = wheelChanges;
                        _itmDisplay.OnWheelChanged();
                        // Rule/engine state belongs to the wheel session that produced
                        // it — rebuild the stack cold alongside the display.
                        _displayStack = null;
                        // A hot-swap fully cold-restarts the lifecycle — re-arm the one-shot
                        // "ITM enabled" log so the re-sync on the new wheel gets a fresh
                        // confirmation line (swaps are infrequent, so no reconnect-loop noise).
                        _itmWasRunning = false;
                    }

                    // Feed the firmware's pushed ITM subscription reports (col03-IN) to the
                    // driver so it follows the page the wheel button selects.
                    plugin.Wheelbase?.DrainItmReports(_itmDisplay.OnSubscriptionReport);

                    _itmDisplay.Update(data);

                    // Log the FIRST bring-up completing per connection, so hardware
                    // verification can confirm from the SimHub log that ITM went live.
                    // Sticky until disconnect: IsRunning legitimately flaps through page
                    // switches, game exits, and recoveries (the controller logs those
                    // itself), and re-firing here would read as a reconnect loop.
                    if (_itmDisplay.IsRunning && !_itmWasRunning)
                    {
                        _itmWasRunning = true;
                        SimHub.Logging.Current.Info(
                            "FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                            "]: ITM enabled — following firmware subscriptions");
                    }

                    // While the display is failing (recovery ladder / unavailable), run the
                    // TTL-cached co-driver probe so its detection edge lands in the session
                    // log next to the failure it may explain — not only while the settings
                    // tab happens to be open.
                    var itmState = _itmDisplay.Lifecycle.State;
                    if (itmState == ItmLifecycleState.Recovery || itmState == ItmLifecycleState.Unavailable)
                        plugin.ProbeItmCoDriver();

                    PublishItmStatusSnapshot(itmState);

                    // Display customization rules (after the driver's Update so the
                    // director observes the lifecycle post-Tick). No-op — and builds
                    // nothing — unless a non-empty config is loaded (the parity gate).
                    UpdateDisplayRules(pluginManager, data);

                    // Optionally also drive the legacy 7-segment gear/speed over col01. On an
                    // ITM OLED (e.g. PBME) the firmware renders this on its legacy page. This
                    // adds col01 traffic interleaved with col03 ITM, which can destabilise the
                    // firmware under load, so it is opt-in — the "Legacy Display Mode" dropdown,
                    // where "None" leaves it off.
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
        /// Frame step for the display-rules runtime. The empty-config fast path is the
        /// feature's safety guarantee: with no customization document (or an explicitly
        /// empty one) nothing is constructed and nothing runs — the ITM frame path
        /// above stays byte-identical to a build without the feature. With a config,
        /// the stack is (re)built whenever its config or driver reference changes
        /// (settings swap, generation rebind, display-id change) and ticked once per
        /// frame; rules only drive pages while the user has ITM enabled.
        /// </summary>
        private void UpdateDisplayRules(PluginManager pluginManager, GameData data)
        {
            var config = _displayConfig;
            if (config == null || config.IsEmpty || !_displaySettings.ItmEnabled)
            {
                // Customization removed (or ITM turned off): drop the runtime and its
                // published snapshot so nothing stale lingers. Rule state restarts
                // clean when customization comes back — engines are per-config.
                if (_displayStack != null)
                {
                    _displayStack = null;
                    _displayRuleSnapshot = null;
                }
                return;
            }

            if (_displayStack == null || !ReferenceEquals(_displayStack.Config, config)
                || !ReferenceEquals(_displayStack.Driver, _itmDisplay))
            {
                _displayStack = new DisplayRuleStack(config, _itmDisplay, _itmDeviceId,
                    _displaySettings.ItmDefaultPage,
                    msg => SimHub.Logging.Current.Info("FanaBridge: " + msg));
                SimHub.Logging.Current.Info(
                    "FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                    "]: Display rules active (" + (config.Itm?.Rules?.Count ?? 0) + " ITM, " +
                    (config.Legacy?.Rules?.Count ?? 0) + " legacy)");
            }

            var snapshot = _displayStack.Tick(pluginManager, data);
            if (snapshot != null)
                _displayRuleSnapshot = snapshot;
        }

        /// <summary>
        /// Publishes a UI-built customization document — the Display tab's ONLY write
        /// path into the config. The document is run through the settings load path
        /// (serialize → parse → <see cref="DisplayConfigValidator"/> normalization), so a
        /// UI-built config obeys exactly the invariants a loaded one does. A null or
        /// empty document publishes null, preserving the empty-config parity fast path.
        /// Nothing else is synced here: the frame path notices the reference swap and
        /// rebuilds the rule stack, and SimHub persists via <see cref="GetSettings"/> on
        /// its own schedule (the same flow a DisplaySettings change rides today).
        /// </summary>
        internal void ApplyDisplayConfig(DisplayCustomizationConfig config)
        {
            var normalized = config == null
                ? null
                : DisplayConfigSerializer.Load(DisplayConfigSerializer.Save(config),
                    msg => SimHub.Logging.Current.Warn("FanaBridge: " + msg));
            _displayConfig = normalized != null && !normalized.IsEmpty ? normalized : null;
        }

        public override void End()
        {
            SimHub.Logging.Current.Info(
                "FanatecWheelDeviceInstance[" + _config.Capabilities.Name + "]: End called");

            PluginResolver()?.UnregisterDeviceInstance(this);
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

            // Display/Tuning tabs are built through the plugin's panel factory so
            // this Adapters class never references FanaBridge.UI. No current
            // plugin generation → tabs omitted, consistent with the LED tab's
            // degradation via EnsureLedModuleInitialized.
            var panels = PluginResolver()?.PanelFactory;

            // Display settings tab (only for wheels with a display)
            if (panels != null && _config.Capabilities.Display != DisplayType.None)
            {
                var displayPanel = panels.CreateDisplayPanel(new DisplayPanelContext
                {
                    DisplaySettings = _displaySettings,
                    DisplayType = _config.Capabilities.Display,
                    ItmDeviceId = _config.Capabilities.ItmDeviceId,
                    GetConfig = () => _displayConfig,
                    ApplyConfig = ApplyDisplayConfig,
                    GetSnapshot = () => _displayRuleSnapshot,
                    GetValues = () => DisplayValuesSnapshot,
                    GetItmStatus = () => ItmStatusDescription,
                    SettingsChanged = () =>
                    {
                        // Sync back to JObject for persistence.
                        _customSettings["displayMode"] = _displaySettings.DisplayMode;
                        _customSettings["itmEnabled"] = _displaySettings.ItmEnabled;
                        _customSettings["itmShowLapTotal"] = _displaySettings.ItmShowLapTotal;
                        _customSettings["itmShowPositionTotal"] = _displaySettings.ItmShowPositionTotal;
                        _customSettings["itmDefaultPage"] = _displaySettings.ItmDefaultPage;
                        _displayManager?.UpdateSettings(_displaySettings);
                        // ITM driver reads _displaySettings live each frame.
                    },
                });

                yield return new DeviceSettingControl(
                    displayPanel,
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
