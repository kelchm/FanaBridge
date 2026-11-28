using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using FanaBridge;
using FanaBridge.Core.Devices;
using FanaBridge.Core.Devices.Profiles;
using FanaBridge.Core.Display.Session;
using FanaBridge.Display;
using FanaBridge.Display.Drivers;
using FanaBridge.Leds;
using FanaBridge.Settings;
using FanaBridge.UI.Devices;
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
    /// for all HID access. Reports Connected only when the singleton's
    /// current wheel identity matches this instance's wheel type.
    /// </summary>
    public class FanatecWheelDeviceInstance : DeviceInstance, INotifyPropertyChanged
    {
        private readonly DeviceConfig _config;

        // Built at construction from registered capabilities, never runtime
        // state — the lifecycle doc explains why that is load-bearing.
        private readonly IFanatecLedModuleHost _ledHost;
        private readonly FanatecDeviceSettings _settings;
        private readonly IDevicePanelFactory _panels;

        // Whether SimHub has taken ownership of this device. Until it has, a
        // failure means the instance is abandoned without End() ever running,
        // so it has to clean up after itself (see GuardBeforePublication).
        private bool _published;

        // Display manager — null when the wheel has no display.
        private FanatecDisplayDriver _displayManager;

        // Never replaced: drivers read it every frame and an open panel edits
        // it directly, so a swap would orphan both.
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
        // True once the 7-segment display (the legacy page on ITM wheels) has been
        // blanked and handed off, on the switch to mode "None" or when the
        // attachment has no display left to drive. From then on nothing here
        // writes to it, teardown included, until FanaBridge drives it again or
        // the settings page's display test takes it back.
        private bool _segmentDisplayReleased;
        // When the last hand-off blank was declined, so a transport that keeps
        // refusing it is retried about once a second rather than every frame.
        private long? _handOffDeclinedAtMs;
        private const long HandOffRetryMs = 1000;

        /// <summary>Test seam: the clock that paces hand-off blank retries, in milliseconds.</summary>
        internal Func<long> NowMs = DefaultClock();

        private static Func<long> DefaultClock()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            return () => sw.ElapsedMilliseconds;
        }
        // Tracks the settings page's display test so the handback edge (test just
        // ended) can blank the residue and reset the driver's value latches.
        private bool _displayTestWasActive;

        // Track connection state transitions for cleanup on disconnect.
        private bool _wasConnected;

        // Serializes hardware output against teardown: a frame that captured a
        // generation being finalized must not draw after BlankOutput ran.
        private readonly object _outputGate = new object();

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

        /// <summary>
        /// Test hook: the live settings object the display and ITM drivers read
        /// every frame — what loaded settings must reach, over and above the
        /// persisted document.
        /// </summary>
        internal DisplaySettings DisplaySettingsForTest => _displaySettings;

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
        /// Builds the device, its LED editor and its settings owner — from
        /// registered capabilities only; nothing here touches hardware or the
        /// plugin singleton (see the lifecycle doc, "Building it up front").
        /// </summary>
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

            // Everything SimHub raises has to reach subscribers of the hiding
            // event, or a device that hides one property would go quiet on all
            // the others. Self-referential, so it cannot keep this alive.
            base.PropertyChanged += ForwardBaseNotification;

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

            _ledHost.StopDriving();

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

        /// <summary>
        /// Whether this device is switched on <em>and</em> has something able
        /// to drive it. HIDES the base property, deliberately: WPF resolves on
        /// the runtime type (pane greys), while SimHub persists the user's real
        /// choice through the base member. Overriding would write "off" into
        /// their settings whenever the plugin was disabled. Pinned by
        /// EnabledHidingProbeTests; rationale in
        /// docs/device-settings-lifecycle.md ("Presenting availability").
        /// </summary>
        public new bool Enabled
        {
            get => base.Enabled && PluginResolver() != null;
            set
            {
                // Only WPF's toggle binding reaches this setter. A click the UI
                // refuses to show must not rewrite the stored choice.
                if (PluginResolver() != null)
                    base.Enabled = value;

                AnnounceEnabled(force: true);   // declined or no-op clicks spring back
            }
        }

        // Hides the base event as the property does: the base raises through a
        // compiler-generated member no derived class can call, and re-listing
        // the interface points WPF's subscription here. Pinned by
        // EnabledNotificationProbeTests.
        public new event PropertyChangedEventHandler PropertyChanged;

        // 1/0 once evaluated, -1 while unknown. Swapped atomically so exactly
        // one caller sees each transition: the UI thread writes it through the
        // setter while the update thread polls it every frame.
        private int _announcedEnabled = -1;

        // Named rather than a lambda so End() can unsubscribe it again.
        private void ForwardBaseNotification(object sender, PropertyChangedEventArgs e) =>
            PropertyChanged?.Invoke(this, e);

        /// <summary>
        /// Tells the UI to re-read <see cref="Enabled"/> when the answer has
        /// moved (or unconditionally, when the user just acted on it).
        /// </summary>
        private void AnnounceEnabled(bool force = false)
        {
            int now = Enabled ? 1 : 0;
            if (System.Threading.Interlocked.Exchange(ref _announcedEnabled, now) == now && !force)
                return;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        }

        public override DeviceState GetDeviceState() =>
            GetDeviceStateFor(PluginResolver());

        /// <summary>
        /// The device state as seen by one specific plugin generation.
        /// DataUpdate makes the same check against the generation and the
        /// attachment it captured at the top of the frame, so state and
        /// drivers can never come from different ones.
        /// </summary>
        private DeviceState GetDeviceStateFor(FanatecPlugin plugin)
        {
            // Never Disabled: SimHub reserves that for the user's own switch and
            // forces an enabled device claiming it back to Scanning every frame,
            // so it never settles and floods the log (see the lifecycle doc,
            // "Presenting availability").
            return IsAttached(plugin, _config) ? DeviceState.Connected : DeviceState.Scanning;
        }

        /// <summary>
        /// The attachment check shared by device state and the Add-device picker.
        /// </summary>
        internal static bool IsAttached(FanatecPlugin plugin, DeviceConfig config) =>
            IsAttached(plugin, plugin?.Wheelbase?.Attachment, config);

        /// <summary>
        /// The attachment check against one snapshot, so a frame can decide
        /// whether it is attached and what to drive from the same attachment.
        /// </summary>
        private static bool IsAttached(FanatecPlugin plugin, AttachmentSnapshot attachment, DeviceConfig config)
        {
            // ARCHITECTURE: reads the wheelbase's attachment snapshot; when the
            // peripheral model lands, bind to a peripheral snapshot instead.
            var wheelbase = plugin?.Wheelbase;
            if (wheelbase == null || attachment == null || !wheelbase.IsConnected)
                return false;

            // Identity still settling: don't drive a half-(re)connected wheel.
            if (!attachment.IdentityStable)
                return false;

            return config.MatchesAttachment(
                attachment.WheelDetected, attachment.WheelCode, attachment.ModuleCode);
        }

        /// <summary>
        /// Produces the persisted document. Complete or throw — SimHub rewrites
        /// the file wholesale, and a throw leaves the existing file alone.
        /// </summary>
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

            // Nothing else tells the UI when plugin availability moves.
            AnnounceEnabled();

            // SimHub calls DataUpdate regardless of the device's own switch —
            // honouring it is our job. ShouldBeRunning() is the SDK's gate and
            // reads the base Enabled (the user's real choice).
            bool switchedOn = ShouldBeRunning();

            // One attachment snapshot for the whole frame: whether this device is
            // attached and what it drives come from the same attachment, so a
            // wheel change can't pair this device's state with another wheel's
            // capabilities.
            var attachment = currentPlugin?.Wheelbase?.Attachment;

            bool isConnected = switchedOn && IsAttached(currentPlugin, attachment, _config);

            // The LEDs tab badge: hidden while nothing can drive the device;
            // connected state pushed because the module only refreshes its own
            // copy while driving output — exactly when it can't notice a loss.
            _ledHost.SetStatus(
                canDrive: switchedOn && currentPlugin != null,
                connected: isConnected);

            // Edge recorded before acting, so a throwing teardown can't re-arm it.
            bool lostConnection = _wasConnected && !isConnected;
            _wasConnected = isConnected;

            if (lostConnection)
            {
                SimHub.Logging.Current.Info(
                    "FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                    "]: " + (switchedOn ? "Lost connection" : "Switched off"));

                try
                {
                    StopDrivingHardware();
                }
                catch (Exception ex)
                {
                    LogCleanupFailure("stopping output on disconnect", ex);
                }
            }

            if (!isConnected)
                return;

            lock (_outputGate)
            {
                // Unpublished since the top of the frame: whoever unpublished it
                // blanks the wheel, and this frame must not relight it.
                if (!ReferenceEquals(PluginResolver(), currentPlugin))
                    return;

                // The attachment moved since the top of the frame (a wheel change,
                // a refresh, a settling edge): what this frame decided describes the
                // previous one, which may be another wheel. Skip it; the next frame
                // starts from the new attachment.
                if (AttachmentMoved(currentPlugin, attachment))
                    return;

                DriveHardware(currentPlugin, attachment, ref data);
            }
        }

        /// <summary>
        /// Whether the wheelbase has published a different attachment snapshot
        /// than the one the frame took.
        /// </summary>
        private static bool AttachmentMoved(FanatecPlugin plugin, AttachmentSnapshot attachment) =>
            !ReferenceEquals(plugin.Wheelbase?.Attachment, attachment);

        /// <summary>
        /// One frame of display/ITM/LED output, entirely from the frame's
        /// captured generation and attachment. Callers hold <see cref="_outputGate"/>.
        /// </summary>
        private void DriveHardware(FanatecPlugin plugin, AttachmentSnapshot attachment, ref GameData data)
        {
            var device = plugin?.Transport;
            if (device == null || !device.IsConnected)
                return;

            // While the wizard is probing hardware, suspend all output so
            // SimHub's per-frame LED writes don't overwrite the test signals.
            if (plugin.WizardActive)
                return;

            // Resolve THIS descriptor's caps override-aware, from the frame's
            // attachment — one answer for the display and the LEDs below.
            // Registration caps froze the built-in profile's display type and ITM
            // device id, so a user override changing either was honored by LEDs
            // but silently ignored by the display drivers — restart or not,
            // because the registry's dedupe keeps the built-in for registration.
            // An attachment left with no profile resolves to no display and no
            // LEDs, which stops this device's output.
            var caps = FanatecPlugin.ResolveCapsFor(_config, attachment);
            var displayType = caps.Display;

            // ── Display ──────────────────────────────────────────────────
            // While the settings page's display test owns the 7-segment display,
            // skip the legacy col01 drive so the test text isn't overwritten each
            // frame (LEDs and the col03 ITM display are unaffected). On handback,
            // Clear() blanks the test residue AND resets the driver's value
            // latches, so the live gear/speed repaints immediately instead of
            // waiting for the next value change.
            bool displayTest = plugin.DisplayTestActive;
            if (displayTest)
            {
                // The test writes the display itself, so it takes back a display
                // that was handed off: once it ends, the hand-off below blanks the
                // test's text and releases the display again.
                _segmentDisplayReleased = false;
                _handOffDeclinedAtMs = null;
            }
            if (!displayTest && _displayTestWasActive
                && _displaySettings.DisplayMode != DisplaySettings.ModeNone
                && displayType != DisplayType.None)
            {
                // In mode "None", or with no display to drive, the blank-once path
                // clears the test residue and releases ownership, with retry —
                // clearing here would double the write and could release ownership
                // on a declined clear.
                _displayManager?.Clear();
            }
            _displayTestWasActive = displayTest;

            // Switched away from ITM (e.g. override to a basic-display profile, or
            // the attachment's last profile deleted): stop the session so the next
            // Itm selection re-runs bring-up. Nothing is sent, so the screen keeps
            // its last frame, as it does when the device is switched off.
            if (displayType != DisplayType.Itm && _itmDisplay != null)
            {
                _itmDisplay.Stop();
                _itmDisplay = null;
                _itmStatusSnapshot = null;
                _itmWasRunning = false;
            }

            if (displayType == DisplayType.Itm)
            {
                // Override retargeted the ITM display id — the driver's deviceId
                // is ctor-fixed, so hot-swap it like the LED pipeline does.
                if (_itmDisplay != null && _itmDeviceId != caps.ItmDeviceId)
                {
                    _itmDisplay.Stop();
                    _itmDisplay = null;
                    _itmStatusSnapshot = null;
                    SimHub.Logging.Current.Info(
                        "FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                        "]: ITM display id changed (" + _itmDeviceId + " → " +
                        caps.ItmDeviceId + ") — rebuilding ITM driver");
                }
                if (_itmDisplay == null)
                {
                    // Drop any status snapshot cached from a previous controller before the
                    // new driver is visible, so the Device Status row never pairs it with the
                    // old controller's description (a plugin-generation rebind or a
                    // display-id change rebuilds the driver here).
                    _itmStatusSnapshot = null;
                    _itmDeviceId = caps.ItmDeviceId;
                    _itmDisplay = new ItmDisplayDriver(plugin.Itm,
                        log: msg => SimHub.Logging.Current.Info("FanaBridge: " + msg),
                        deviceId: _itmDeviceId);
                    // Baseline the wheel-change counter at creation — the driver is starting
                    // cold anyway, so changes before this point are already accounted for.
                    _itmWheelChangeCount = plugin.Wheelbase?.WheelChangeCount ?? 0;
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
                    UpdateSegmentDisplay(plugin, data, displayTest);
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
                // Same drive (and mode-"None" gate) as the ITM legacy page: "None"
                // stops all display writes so another application can own the screen
                // while FanaBridge keeps driving the LEDs.
                UpdateSegmentDisplay(plugin, data, displayTest);
            }
            else
            {
                // No display to drive any more (the attachment's profile is gone):
                // blank what this device last showed and hand the display off,
                // exactly as switching to mode "None" does.
                HandOffSegmentDisplay(plugin, displayTest);
            }

            // ── LEDs ─────────────────────────────────────────────────────
            // Hot-swap the driver if the active profile changed (e.g. user
            // picked a different override in the settings dropdown, or deleted
            // the attachment's last profile, which swaps to a driver with no
            // LEDs). Use the per-descriptor resolution, not the global caps — a
            // non-matching device resolves to its own registration profile
            // and so never hot-swaps to the connected wheel's profile.
            //
            // Checked again first: SimHub builds the LED driver from whatever is
            // published when it asks, so an attachment that moved during the
            // display work above is left to the next frame.
            if (AttachmentMoved(plugin, attachment))
                return;
            _ledHost.HotSwapIfNeeded(caps);

            // Settings the module could not fully take leave it partially
            // populated, so driving LEDs from it would show something the user
            // never chose. Output resumes once a later load or a reset makes it
            // trustworthy again -- the same condition that unblocks saving.
            if (!_settings.IsFaulted)
                _ledHost.Display();
        }

        /// <summary>
        /// Stops driving the wheel and darkens it. Blanks unconditionally: a
        /// write at a gone transport quietly fails, and unconditional blanking
        /// is what keeps teardown correct from either racer (see the lifecycle
        /// doc, "Teardown ordering").
        /// </summary>
        private void StopDrivingHardware()
        {
            _ledHost.StopDriving();

            // Skip the display blank in mode "None" or once the display has been
            // handed off — the display isn't ours, and on an identity transition the
            // transport may still be live.
            if (OwnsSegmentDisplay)
                _displayManager?.Clear();
            _itmDisplay?.Stop();
            _itmWasRunning = false;
            _itmStatusSnapshot = null;   // don't show a stale ITM row while disconnected
            // Reset one-shot latches so a reconnect starts clean: errors can log again,
            // and a hand-off blank still due is tried straight away. A display already
            // handed off stays released.
            _itmErrorLogged = false;
            _handOffDeclinedAtMs = null;
        }

        // Whether teardown may blank the 7-segment display: not in mode "None",
        // and not once it has been handed off to the firmware or another application.
        private bool OwnsSegmentDisplay =>
            _displaySettings.DisplayMode != DisplaySettings.ModeNone && !_segmentDisplayReleased;

        /// <summary>
        /// Darkens this device on the plugin's way out, while its transport is
        /// still alive. See docs/device-settings-lifecycle.md for the teardown
        /// ordering this participates in.
        /// </summary>
        internal void BlankOutput()
        {
            // The gate waits out a frame already drawing, so the blank always
            // lands after the last lit write.
            lock (_outputGate)
            {
                // Skip devices that were not being driven: nothing is lit, and
                // teardown should not pay their drivers' bounded refresh wait.
                if (!_wasConnected)
                    return;

                _wasConnected = false;

                try
                {
                    StopDrivingHardware();
                }
                catch (Exception ex)
                {
                    LogCleanupFailure("blanking output", ex);
                }
            }
        }

        /// <summary>
        /// Drives the 7-segment gear/speed display (the legacy page on ITM wheels),
        /// honouring mode "None": any other mode runs the display driver each frame;
        /// "None" blanks the display once on the transition — retried about once a
        /// second until the write is accepted — and then never writes again, leaving
        /// the display free for the firmware or another application.
        /// </summary>
        private void UpdateSegmentDisplay(FanatecPlugin plugin, GameData data, bool displayTest)
        {
            if (_displaySettings.DisplayMode != DisplaySettings.ModeNone)
            {
                // No encoder means this generation cannot reach a display at all;
                // a driver built around one could only throw.
                if (_displayManager == null && plugin.Display != null)
                {
                    _displayManager = new FanatecDisplayDriver(plugin.Display, _displaySettings);
                    SimHub.Logging.Current.Info(
                        "FanatecWheelDeviceInstance[" + _config.Capabilities.Name + "]: Created display manager");
                }
                if (!displayTest)
                    _displayManager?.Update(data);
                _segmentDisplayReleased = false;
                _handOffDeclinedAtMs = null;
            }
            else
            {
                HandOffSegmentDisplay(plugin, displayTest);
            }
        }

        /// <summary>
        /// Blanks the 7-segment display once and releases it, so FanaBridge stops
        /// writing it and shutdown cleanup leaves it alone. Used on the switch to
        /// mode "None" and when the attachment no longer has a display to drive.
        /// </summary>
        private void HandOffSegmentDisplay(FanatecPlugin plugin, bool displayTest)
        {
            if (_displayManager == null || _segmentDisplayReleased || displayTest)
                return;

            // A declined blank is retried, so a transient transport failure doesn't
            // leave the display frozen on its last value, but paced so a transport
            // that keeps refusing isn't written every frame.
            long now = NowMs();
            if (_handOffDeclinedAtMs.HasValue && now - _handOffDeclinedAtMs.Value < HandOffRetryMs)
                return;

            if (!_displayManager.Clear())
            {
                _handOffDeclinedAtMs = now;
                return;
            }

            // The accepted blank hands the display off: release ownership so
            // shutdown cleanup won't blank another application's content later.
            _segmentDisplayReleased = true;
            _handOffDeclinedAtMs = null;
            plugin.Display?.Release();
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

            // Mode "None", or a display already handed off, means the display isn't
            // ours — the exit blank would stomp whatever the firmware or another
            // application has on it.
            if (OwnsSegmentDisplay)
            {
                try { _displayManager?.Clear(); }
                catch (Exception ex) { LogCleanupFailure("clearing the display", ex); }
            }

            try { _itmDisplay?.Stop(); }
            catch (Exception ex) { LogCleanupFailure("stopping the ITM display", ex); }

            _settings.Changed -= OnSettingsChanged;

            // Let go of the UI's subscription. Forwarding is a self-reference so
            // it cannot keep this object alive, but a device SimHub has finished
            // with should not still be talking to bindings.
            base.PropertyChanged -= ForwardBaseNotification;
            PropertyChanged = null;

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
        /// The device's settings tabs. Built without a plugin so the device
        /// can always show what it stores; SimHub deliberately greys them while
        /// nothing can drive it (via the hiding Enabled) — do not "fix" that.
        /// </summary>
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
