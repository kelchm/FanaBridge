using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge;
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

        // The LED editor and everything persisted about this device. Both are
        // built during construction and live as long as the instance does —
        // they depend only on the device's registered capabilities, never on
        // whether the plugin is running. SimHub creates and saves devices
        // regardless of that, and a device that could not describe its own
        // settings used to have them erased.
        private readonly IFanatecLedModuleHost _ledHost;
        private readonly FanatecDeviceSettings _settings;
        private readonly IDevicePanelFactory _panels;

        // Whether SimHub has taken ownership of this device. Until it has, a
        // failure means the instance is abandoned without End() ever running,
        // so it has to clean up after itself (see GuardBeforePublication).
        private bool _published;

        // Display manager — null when the wheel has no display.
        private FanatecDisplayDriver _displayManager;

        // The live view of the display settings. Never replaced: the display
        // and ITM drivers read it every frame, and an open settings panel edits
        // it directly, so swapping it would leave both looking at an object
        // nothing updates any more.
        private readonly DisplaySettings _displaySettings = new DisplaySettings();

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

        /// <summary>Test hook: this device's settings owner.</summary>
        internal FanatecDeviceSettings SettingsForTest => _settings;

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

        public FanatecWheelDeviceInstance(DeviceConfig config)
            : this(config, null, null)
        {
        }

        /// <summary>
        /// Builds the device, its LED editor and its settings owner.
        /// </summary>
        /// <remarks>
        /// The editor is built here, from the device's registered capabilities,
        /// rather than when a plugin first appears. SimHub constructs and saves
        /// devices whether or not FanaBridge is enabled, and a device without an
        /// editor could not describe its own LED settings — so saving it wrote a
        /// document with none, over a file that had them.
        ///
        /// Nothing here touches hardware or the plugin singleton.
        /// </remarks>
        internal FanatecWheelDeviceInstance(
            DeviceConfig config,
            IDevicePanelFactory panels,
            IFanatecLedModuleHost ledHost)
        {
            _config = config;
            _panels = panels;

            _ledHost = ledHost ?? CreateLedHost(config);
            _settings = new FanatecDeviceSettings(config, _ledHost);
            _settings.Changed += OnSettingsChanged;

            // Start from defaults so the device is coherent even if SimHub never
            // delivers settings (a freshly added device).
            ApplySnapshotToDisplaySettings();
        }

        private IFanatecLedModuleHost CreateLedHost(DeviceConfig config)
        {
            if (config.Capabilities.AllLedCount == 0)
                return new NoLedModuleHost();

            return new FanatecLedModuleHost(
                config, () => PluginResolver(), ResolveCurrentCapabilities);
        }

        /// <summary>
        /// Mirrors the committed settings into the object the display and ITM
        /// drivers read, and pushes them to the display driver.
        /// </summary>
        private void OnSettingsChanged(object sender, EventArgs e)
        {
            ApplySnapshotToDisplaySettings();
            _displayManager?.UpdateSettings(_displaySettings);
        }

        private void ApplySnapshotToDisplaySettings()
        {
            var current = _settings.Current;
            _displaySettings.DisplayMode = current.DisplayMode;
            _displaySettings.ItmEnabled = current.ItmEnabled;
            _displaySettings.ItmShowLapTotal = current.ItmShowLapTotal;
            _displaySettings.ItmShowPositionTotal = current.ItmShowPositionTotal;
            _displaySettings.ItmDefaultPage = current.ItmDefaultPage;
        }

        // ── LED module setup ─────────────────────────────────────────────

        /// <summary>
        /// Resolves this descriptor's capabilities as of now, rather than as of
        /// whenever the LED module happened to be created.
        /// </summary>
        internal WheelCapabilities ResolveCurrentCapabilities()
        {
            var plugin = PluginResolver() ?? _boundPlugin;
            return plugin == null ? WheelCapabilities.None : plugin.ResolveCapsFor(_config);
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

            _ledHost.RebindToCurrentGeneration();

            // Display/ITM drivers hold their encoder for life — recreate them
            // lazily (DataUpdate builds them on demand from the live plugin).
            _displayManager = null;
            _itmDisplay = null;
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

            GuardBeforePublication(_settings.LoadDefaults);
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

        /// <summary>
        /// Produces this device's persisted settings document.
        /// </summary>
        /// <remarks>
        /// SimHub rewrites the file from this call, wholesale, with no merge
        /// against what is on disk — so anything missing here is erased. This
        /// only observes: it never repairs, reapplies or resets state on the way
        /// out. When a complete document cannot be produced it throws, and
        /// SimHub leaves the existing file (and its index entry) alone.
        /// </remarks>
        public override JToken GetSettings(bool forTemplate, bool forDefaultSettings) =>
            _settings.Capture(forTemplate, forDefaultSettings);

        public override void SetSettings(JToken settings, bool isDefault) =>
            GuardBeforePublication(() => _settings.Apply(settings, isDefault));

        public override void Init(PluginManager pluginManager)
        {
            GuardBeforePublication(() => base.Init(pluginManager));

            // Past this point SimHub owns the instance and will call End() on it,
            // so failures no longer need to clean up here.
            _published = true;
        }

        /// <summary>
        /// Cleans up if the device fails before SimHub takes ownership of it.
        /// </summary>
        /// <remarks>
        /// SimHub builds a device, gives it its settings, initializes it, and only
        /// then adds it to its list — and it does not call End() on one that threw
        /// on the way. Since the LED host is built with the device, an instance
        /// abandoned there would keep the manager's subscription to a static event
        /// alive for the rest of the session, once per failed attempt.
        ///
        /// After publication the opposite applies: the device stays in SimHub's
        /// list, so its host must survive and simply refuse to save.
        /// </remarks>
        private void GuardBeforePublication(Action action)
        {
            if (_published)
            {
                action();
                return;
            }

            try
            {
                action();
            }
            catch
            {
                _ledHost.Dispose();
                throw;
            }
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
                // Reset one-shot latches so a reconnect starts clean: errors can log again
                // and the legacy page can re-blank when the mode is "None".
                _itmErrorLogged = false;
                _legacyBlanked = false;
            }

            _wasConnected = isConnected;

            if (!isConnected)
                return;

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
                    _itmDisplay.DefaultPage = _displaySettings.ItmDefaultPage;

                    // A wheel/hub/module change (identity layer, FF 08) resets the display to
                    // a cold state that is invisible on the ITM channel — restart the ITM
                    // lifecycle from bring-up. Polled via the monotonic counter.
                    int wheelChanges = plugin.Wheelbase?.WheelChangeCount ?? 0;
                    if (wheelChanges != _itmWheelChangeCount)
                    {
                        _itmWheelChangeCount = wheelChanges;
                        _itmDisplay.OnWheelChanged();
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
            // Use the per-descriptor resolution, not the global caps — a
            // non-matching device resolves to its own registration profile
            // and so never hot-swaps to the connected wheel's profile.
            var currentCaps = plugin.ResolveCapsFor(_config);
            if (currentCaps?.Profile != null)
                _ledHost.HotSwapIfNeeded(currentCaps);

            // Settings the module could not fully take leave it partially
            // populated, so driving LEDs from it would show something the user
            // never chose. Output resumes once a later load or a reset makes it
            // trustworthy again -- the same condition that unblocks saving.
            if (!_settings.IsFaulted)
                _ledHost.Display();
        }

        public override void End()
        {
            SimHub.Logging.Current.Info(
                "FanatecWheelDeviceInstance[" + _config.Capabilities.Name + "]: End called");

            // Each step is independent: an earlier failure must not skip the
            // LED host's cleanup, which is the only thing that removes its
            // subscription to a static event SimHub never unhooks for us.
            try { PluginResolver()?.UnregisterDeviceInstance(this); }
            catch (Exception ex) { LogCleanupFailure("unregistering the device", ex); }

            try { _displayManager?.Clear(); }
            catch (Exception ex) { LogCleanupFailure("clearing the display", ex); }

            try { _itmDisplay?.Stop(); }
            catch (Exception ex) { LogCleanupFailure("stopping the ITM display", ex); }

            _settings.Changed -= OnSettingsChanged;
            _ledHost.Dispose();
        }

        private void LogCleanupFailure(string what, Exception ex)
        {
            SimHub.Logging.Current.Warn(
                "FanatecWheelDeviceInstance[" + _config.Capabilities.Name + "]: " +
                what + " failed during shutdown: " + ex.Message);
        }

        public override IEnumerable<DynamicButtonAction> GetDynamicButtonActions()
        {
            return _ledHost.GetDynamicActions();
        }

        /// <summary>
        /// The device's settings tabs.
        /// </summary>
        /// <remarks>
        /// None of these need a running plugin. Editing what a device stores is
        /// independent of whether its hardware is reachable, and hiding the tabs
        /// while FanaBridge was disabled left users unable to see settings that
        /// were nonetheless being saved.
        /// </remarks>
        public override IEnumerable<DeviceSettingControl> GetSettingsControls()
        {
            var ledEditControl = _ledHost.EditControl;
            if (ledEditControl != null)
            {
                yield return new DeviceSettingControl(
                    ledEditControl,
                    0,
                    "LEDs",
                    DeviceSettingControlKind.None,
                    true);
            }

            if (_panels == null)
                yield break;

            // Screen settings tab (only for wheels with a display)
            if (_config.Capabilities.Display != DisplayType.None)
            {
                var screenPanel = _panels.CreateScreenPanel(
                    _displaySettings, _config.Capabilities.Display, _config.Capabilities.ItmDeviceId,
                    settingsChanged: () => _settings.UpdateDisplay(_displaySettings));

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
                yield return new DeviceSettingControl(
                    _panels.CreateTuningPanel(_settings),
                    2,
                    "Tuning",
                    DeviceSettingControlKind.None,
                    true);
            }
        }
    }
}
