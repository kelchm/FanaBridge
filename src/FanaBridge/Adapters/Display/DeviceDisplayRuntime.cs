using System;
using FanaBridge;
using FanaBridge.Display.Rules;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using GameReaderCommon;
using SimHub.Plugins;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// The device-scoped ITM display session, extracted from
    /// <see cref="FanatecWheelDeviceInstance"/> so the device shell no longer owns the
    /// whole display god-object. This runtime owns the col03 ITM driver, its wire-driven
    /// digital twin, the display-customization rule stack and its volatile config
    /// snapshot, the ITM status line, page-policy sequencing (the SOLE caller of
    /// <see cref="ItmDisplayDriver.SetPagePolicy"/> /
    /// <see cref="ItmDisplayDriver.RestoreBuiltInPagePolicy"/>), and the ONE UI-facing
    /// <see cref="DisplayPanelSnapshot"/> envelope.
    ///
    /// The device instance keeps the device shell — LEDs, identity, connection, the
    /// PluginResolver/settings bag, and the legacy col01 7-segment driver — and delegates
    /// the ITM session here: one <see cref="Tick"/> per connected ITM frame plus the
    /// lifecycle edges (<see cref="OnDisplayTypeLeftItm"/>, <see cref="OnDisconnected"/>,
    /// <see cref="OnGenerationRebind"/>, <see cref="OnEnd"/>). All ITM mutation stays on
    /// the DataUpdate thread; the only cross-thread reads are the two volatile channels
    /// (<see cref="Snapshot"/> and the config).
    ///
    /// Behavior is byte-for-byte the same as when this lived on the instance: the
    /// empty-config fast path constructs none of the rule runtime, and every teardown
    /// edge republishes the envelope in-frame so a stale part never outlives its session.
    /// </summary>
    internal sealed class DeviceDisplayRuntime
    {
        private readonly DeviceConfig _config;
        // Late-bound clock accessor: the device instance's ItmClockForTest is assigned by
        // tests AFTER construction, so the runtime reads it through this indirection each
        // time it builds a driver/twin instead of capturing a value at ctor time.
        private readonly Func<Func<long>> _itmClock;
        // Info-level logger (wraps SimHub.Logging.Current.Info). Warn/Error levels and the
        // "FanaBridge:"-prefixed sub-object log lambdas call SimHub.Logging.Current directly
        // below to preserve the exact levels/format the instance emitted.
        private readonly Action<string> _log;

        // ── ITM session state (moved verbatim from the device instance) ──
        // The ITM col03 driver — null until a wheel with an ITM display is driven.
        private ItmDisplayDriver _itmDisplay;
        // The wire-driven digital twin for the live Overview mirror: a virtual panel that
        // consumes the exact col03 frames the driver+lifecycle send (via the plugin's
        // outbound tap) plus the same firmware pushes, and publishes the screen-state
        // snapshot the UI reads. Built and torn down on the SAME lifetime edges as the
        // driver (it models the same device), attached to the shared ITM tap for the
        // session's whole ITM tenure and detached (identity-guarded) at teardown.
        private VirtualItmDisplay _itmTwin;
        // The display id the ITM driver was built against; an override changing it
        // hot-swaps the driver (deviceId is a ctor-fixed value).
        private byte _itmDeviceId;
        private bool _itmWasRunning;
        private bool _itmErrorLogged;
        // Wheel-change edge detection (polled — no event subscription that could outlive a
        // plugin generation, see issue #37).
        private int _itmWheelChangeCount;

        // Display customization: the parsed per-device config snapshot (volatile — written
        // by SetConfig/ApplyDisplayConfig, read on the frame path; a reference swap is the
        // rebuild signal), the rule stack built from it, and the stack's latest snapshot
        // (a PART of the published envelope below — DataUpdate-thread only). All three stay
        // null on the empty-config fast path.
        private volatile DisplayCustomizationConfig _displayConfig;
        private DisplayRuleStack _displayStack;
        private DisplayRuleSnapshot _displayRuleSnapshot;

        // ITM status line for the Device Status panel / diagnostics. Composed on the
        // DataUpdate thread — a PART of the published envelope, never read cross-thread
        // itself. Refreshed on state/sync changes plus a coarse 1 s tick.
        private string _itmStatus;
        private ItmLifecycleState _itmSnapState;
        private int _itmSnapGen;
        private int _itmSnapTick;

        // The ONE UI-facing volatile channel: the envelope over the three display parts
        // (ITM status line, rule-stack snapshot, values snapshot). Recomposed by
        // MaybePublishPanelSnapshot only when a part actually changed; the teardown edges
        // are enumerated on DisplayPanelSnapshot.
        private volatile DisplayPanelSnapshot _panelSnapshot;

        public DeviceDisplayRuntime(DeviceConfig config, Func<Func<long>> itmClock, Action<string> log)
        {
            _config = config;
            _itmClock = itmClock ?? (() => null);
            _log = log ?? (_ => { });
        }

        // ── UI / status reads (volatile-envelope backed, thread-safe) ────

        /// <summary>The one UI-facing envelope, or null while there is nothing to show.</summary>
        internal DisplayPanelSnapshot Snapshot => _panelSnapshot;

        /// <summary>The ITM lifecycle status line, or null while not driving ITM. Routed
        /// through the published envelope, so it is safe to read from any thread.</summary>
        internal string ItmStatusDescription => _panelSnapshot?.ItmStatus;

        /// <summary>Test seam: the rule part of the display envelope, or null while no
        /// customization is active.</summary>
        internal DisplayRuleSnapshot RuleSnapshot => _displayRuleSnapshot;

        /// <summary>Test seam: the values part of the display envelope (what the ITM
        /// display is showing, from the wire-driven twin), or null while not driving ITM.</summary>
        internal DisplayValuesSnapshot ValuesSnapshot => _itmTwin?.Snapshot;

        /// <summary>Test seam: the rule stack, null when nothing is built.</summary>
        internal DisplayRuleStack Stack => _displayStack;

        /// <summary>Test seam: the ITM driver, null until an ITM display is driven.</summary>
        internal ItmDisplayDriver ItmDriver => _itmDisplay;

        // ── Config (volatile release / acquire, the rebuild signal) ──────

        /// <summary>The current customization config snapshot, or null when none.</summary>
        internal DisplayCustomizationConfig CurrentConfig => _displayConfig;

        /// <summary>Publishes a parsed config snapshot (volatile release). The frame path
        /// notices the reference swap and rebuilds the rule stack.</summary>
        internal void SetConfig(DisplayCustomizationConfig parsed) => _displayConfig = parsed;

        /// <summary>Drops any config (no displayCustomization key = no customization).</summary>
        internal void ClearConfig() => _displayConfig = null;

        /// <summary>
        /// Publishes a UI-built customization document — the Display tab's ONLY write path
        /// into the config. The document is run through the settings load path
        /// (serialize → parse → <see cref="DisplayConfigValidator"/> normalization), so a
        /// UI-built config obeys exactly the invariants a loaded one does. A null or empty
        /// document publishes null, preserving the empty-config parity fast path. Nothing
        /// else is synced here: the frame path notices the reference swap and rebuilds the
        /// rule stack, and SimHub persists via GetSettings on its own schedule.
        /// </summary>
        internal void ApplyDisplayConfig(DisplayCustomizationConfig config)
        {
            var normalized = config == null
                ? null
                : DisplayConfigSerializer.Load(DisplayConfigSerializer.Save(config),
                    msg => SimHub.Logging.Current.Warn("FanaBridge: " + msg));
            _displayConfig = normalized != null && !normalized.IsEmpty ? normalized : null;
        }

        // ── Per-frame step ───────────────────────────────────────────────

        /// <summary>
        /// One connected ITM frame: builds/hot-swaps the driver + twin, applies the ITM
        /// settings, restarts cold on a wheel change, drains firmware subscriptions, ticks
        /// the driver and twin, runs the co-driver probe / status snapshot, ticks the
        /// display rules, and publishes the envelope. Runs the whole ITM body under its own
        /// try/catch (mirroring the old inline guard): a firmware/transport hiccup logs once
        /// and returns false so the device instance skips its legacy col01 drive this frame
        /// exactly as before (the legacy drive used to share this try). Returns true on a
        /// clean frame.
        ///
        /// Settings are read through a late accessor rather than a pre-evaluated argument so
        /// the volatile <c>_displayConfig</c> acquire is sequenced BEFORE the settings read:
        /// the writer (SetSettings) plain-writes the settings then volatile-releases the
        /// config, so a frame that acquires a newly-published config is guaranteed to also
        /// see the settings written before it — the rule stack latches ItmDefaultPage at
        /// build time, and reading settings before the acquire would let a torn pair latch a
        /// stale base page. Evaluating the accessor at the call site (before this acquire)
        /// would break that ordering.
        /// </summary>
        internal bool Tick(FanatecPlugin plugin, WheelCapabilities displayCaps,
            PluginManager pluginManager, GameData data, Func<DisplaySettings> settings)
        {
            // Override retargeted the ITM display id — the driver's deviceId is ctor-fixed,
            // so hot-swap it like the LED pipeline does.
            if (_itmDisplay != null && _itmDeviceId != displayCaps.ItmDeviceId)
            {
                _itmDisplay.Stop();
                _itmDisplay = null;
                // The twin is keyed to the display id (its constructor fixes it) — drop it
                // with the driver so the rebuild below constructs one for the new id and
                // re-attaches. Detach the old one from the shared tap.
                plugin.DetachItmObserver(_itmTwin);
                _itmTwin = null;
                _log("FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                    "]: ITM display id changed (" + _itmDeviceId + " → " +
                    displayCaps.ItmDeviceId + ") — rebuilding ITM driver");
            }
            if (_itmDisplay == null)
            {
                _itmDeviceId = displayCaps.ItmDeviceId;
                _itmDisplay = new ItmDisplayDriver(plugin.Itm,
                    nowMs: _itmClock(),
                    log: msg => SimHub.Logging.Current.Info("FanaBridge: " + msg),
                    deviceId: _itmDeviceId);
                // The wire-driven twin for the same display device, on the same clock as
                // the driver so their snapshot throttles stay in step. Attached to the
                // shared ITM outbound tap the instant it exists — bootstrap by construction:
                // the tap is live from encoder creation, so the twin observes the whole
                // session from the first bring-up frame with no mid-stream attach.
                _itmTwin = new VirtualItmDisplay(deviceId: _itmDeviceId, nowMs: _itmClock());
                plugin.AttachItmObserver(_itmTwin);
                // A display-id hot-swap rebuilds the driver UNDER a still-live rule stack
                // (UpdateDisplayRules below replaces the stack after the driver's first
                // Update this same frame). Keep page policy continuously external across the
                // rebuild so that first Update's cold bring-up targets the stack's base — not
                // the dormant ItmDefaultPage setting — and the lifecycle's game-start revert
                // stays suppressed for the stack's uninterrupted tenure. Edge-triggered: this
                // rebuild IS the edge. Every other path through this block (first build,
                // reconnect, generation rebind) has already dropped the stack, so the fresh
                // driver correctly starts under built-in policy.
                //
                // Re-RESOLVE the base against the NEW device's page table rather than carrying
                // the old stack's BaseWirePage: a wire page number is valid only with the
                // device id/table that produced it (invariant — never carry a raw wire across
                // the device-id boundary). On a cross-catalog change the same wire is a
                // different page (Tyre Temps is wire 5 on device 3 but wire 4 on device 4,
                // where wire 5 is Legacy), so the old wire would cold-start the new driver on
                // the wrong page. ResolveBase maps the configured base identity onto this
                // device's wire (and falls to the default wire's identity when this device
                // lacks the configured base). The rebuilt stack re-takes policy later this
                // frame; this keeps the boundary coherent until it does.
                if (_displayStack != null)
                {
                    var resolved = ItmPageTable.ForDevice(_itmDeviceId)
                        .ResolveBase(_displayStack.ConfiguredBase, _displayStack.DefaultWirePage);
                    _itmDisplay.SetPagePolicy(resolved.Wire);
                }
                // Baseline the wheel-change counter at creation — the driver is starting
                // cold anyway, so changes before this point are already accounted for.
                _itmWheelChangeCount = plugin.Wheelbase?.WheelChangeCount ?? 0;
                // Drop any status line cached from a disposed generation's controller, so
                // the Device Status row never shows the old controller's description (a
                // plugin-generation rebind or a display-id change rebuilds the driver here;
                // the envelope republishes at the end of this frame).
                _itmStatus = null;
                _log("FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                    "]: Created ITM display driver");
            }

            // Guard the ITM display work: an exception here (firmware quirk, transport
            // hiccup) must not skip the LED update further down the device instance's
            // DataUpdate call. Log the first failure, then stay quiet to avoid spam.
            bool ok = true;
            try
            {
                // Acquire the volatile config FIRST, then read the settings snapshot: this
                // pairing is the fence that keeps a frame which sees a newly-published config
                // from latching a stale ItmDefaultPage (see the method summary). Both the
                // acquired config and the single settings read flow down into
                // UpdateDisplayRules so the ordering holds all the way to the stack build.
                var config = _displayConfig;
                var settingsSnap = settings();

                _itmDisplay.Enabled = settingsSnap.ItmEnabled;
                if (settingsSnap.ItmEnabled)
                    _itmDisplay.Start();   // idempotent — re-arms bring-up after a disconnect
                _itmDisplay.ShowLapTotal = settingsSnap.ItmShowLapTotal;
                _itmDisplay.ShowPositionTotal = settingsSnap.ItmShowPositionTotal;
                // The built-in page policy's base page: the user's default-page setting,
                // read live each frame like the toggles above (the driver edge-detects a
                // change and switches the display live). WHO owns page policy is a separate,
                // edge-triggered contract on the driver: UpdateDisplayRules below hands
                // policy to the rule stack on build (SetPagePolicy — this setting goes
                // dormant) and back on teardown (RestoreBuiltInPagePolicy); disconnect /
                // driver-teardown edges restore it through ItmDisplayDriver.Stop.
                _itmDisplay.DefaultPage = settingsSnap.ItmDefaultPage;

                // A wheel/hub/module change (identity layer, FF 08) resets the display to a
                // cold state that is invisible on the ITM channel — restart the ITM
                // lifecycle from bring-up. Polled via the monotonic counter.
                int wheelChanges = plugin.Wheelbase?.WheelChangeCount ?? 0;
                if (wheelChanges != _itmWheelChangeCount)
                {
                    _itmWheelChangeCount = wheelChanges;
                    _itmDisplay.OnWheelChanged();
                    // The twin gets the same cold-start signal the lifecycle does: the new
                    // wheel is a different device, so every observed frame so far described
                    // one that no longer exists. It re-grounds from the fresh bring-up
                    // frames.
                    _itmTwin?.OnColdStart();
                    // Rule/engine state belongs to the wheel session that produced it —
                    // rebuild the stack cold alongside the display. Page policy deliberately
                    // stays external across this null: the cold entry above targeted the
                    // stack's base, and UpdateDisplayRules rebuilds from the same config this
                    // frame and re-takes policy (or, if customization vanished too, restores
                    // the built-in).
                    _displayStack = null;
                    // A hot-swap fully cold-restarts the lifecycle — re-arm the one-shot
                    // "ITM enabled" log so the re-sync on the new wheel gets a fresh
                    // confirmation line.
                    _itmWasRunning = false;
                }

                // Feed the firmware's pushed ITM subscription reports (col03-IN) to the
                // driver so it follows the page the wheel button selects — and to the twin,
                // which needs the SAME pushes to build its handle table and infer
                // wheel-button page changes (the OUT wire carries no PageSet for those).
                plugin.Wheelbase?.DrainItmReports(FeedItmSubscriptionReport);

                _itmDisplay.Update(data);
                // Stamp the host lifecycle state onto the twin's snapshot (the wire does not
                // carry it) and expire its grace/throttle windows. Runs after the driver's
                // Update so the twin has already observed this frame's OUT frames through the
                // tap; nothing here sends.
                _itmTwin?.Tick(_itmDisplay.Lifecycle.State);

                // Log the FIRST bring-up completing per connection, so hardware verification
                // can confirm from the SimHub log that ITM went live. Sticky until
                // disconnect: IsRunning legitimately flaps through page switches, game exits,
                // and recoveries (the controller logs those itself), and re-firing here would
                // read as a reconnect loop.
                if (_itmDisplay.IsRunning && !_itmWasRunning)
                {
                    _itmWasRunning = true;
                    _log("FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                        "]: ITM enabled — following firmware subscriptions");
                }

                // While the display is failing (recovery ladder / unavailable), run the
                // TTL-cached co-driver probe so its detection edge lands in the session log
                // next to the failure it may explain — not only while the settings tab
                // happens to be open.
                var itmState = _itmDisplay.Lifecycle.State;
                if (itmState == ItmLifecycleState.Recovery || itmState == ItmLifecycleState.Unavailable)
                    plugin.ProbeItmCoDriver();

                PublishItmStatusSnapshot(itmState);

                // Display customization rules (after the driver's Update so the director
                // observes the lifecycle post-Tick). No-op — and builds nothing — unless a
                // non-empty config is loaded (the parity gate). The already-acquired config
                // and settings snapshot are handed down so the acquire-before-settings order
                // holds through the stack build.
                UpdateDisplayRules(config, pluginManager, data, settingsSnap);
            }
            catch (Exception ex)
            {
                ok = false;
                if (!_itmErrorLogged)
                {
                    _itmErrorLogged = true;
                    SimHub.Logging.Current.Error(
                        "FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                        "]: ITM display update failed (LEDs unaffected): " + ex);
                }
            }

            // Publish the display envelope for the UI — after ALL ITM work (and outside the
            // try/catch, so a mid-frame failure still publishes a consistent view of
            // whatever the parts say now). Change-gated: composes nothing when no part moved
            // this frame. This subsumes the old standalone per-frame publish.
            MaybePublishPanelSnapshot();
            return ok;
        }

        // ── Lifecycle edges (each republishes the envelope in-frame) ─────

        /// <summary>
        /// Display type switched away from ITM (e.g. an override to a basic-display
        /// profile): stop the session so the next Itm selection re-runs bring-up. A no-op
        /// when no driver is held. Republishes the (now null) envelope — the frame path's
        /// Tick won't run on the non-ITM path, so nothing else would clear it.
        /// </summary>
        internal void OnDisplayTypeLeftItm(FanatecPlugin plugin)
        {
            if (_itmDisplay == null)
                return;
            _itmDisplay.Stop();
            _itmDisplay = null;
            // The twin models the ITM panel — it goes with the driver. Detach it from the
            // shared tap (identity-guarded) so a later ITM device doesn't inherit a dead
            // observer, and drop the reference.
            plugin.DetachItmObserver(_itmTwin);
            _itmTwin = null;
            _itmWasRunning = false;
            _displayStack = null;         // rules only drive an ITM display
            _displayRuleSnapshot = null;  // and their published snapshot goes with them
            MaybePublishPanelSnapshot();
        }

        /// <summary>
        /// Connection lost: the display went cold. Stop the driver, cold-start the twin (its
        /// published screen goes blank with the driver — it stays attached to the tap and
        /// re-grounds from the reconnect's fresh bring-up frames), drop the rule stack, reset
        /// the one-shot latches, and republish so the envelope goes null (the per-frame Tick
        /// is unreachable while disconnected).
        /// </summary>
        internal void OnDisconnected()
        {
            _itmDisplay?.Stop();
            _itmTwin?.OnColdStart();
            _itmWasRunning = false;
            _itmStatus = null;           // don't show a stale ITM row while disconnected
            _displayStack = null;        // rules restart clean with the reconnect
            _displayRuleSnapshot = null;
            _itmErrorLogged = false;     // errors can log again after a reconnect
            MaybePublishPanelSnapshot();
        }

        /// <summary>
        /// Plugin generation rebind (issue #37): the cached driver holds an encoder bound to
        /// a disposed transport. Drop every driver-adjacent object so the frame path rebuilds
        /// them against the current generation, and null the status line AND the published
        /// envelope the instant the driver is invalidated (not just at the rebuild site) so
        /// the UI can never read a disposed generation's parts. The twin is dropped WITHOUT a
        /// detach — the old tap died with the transport (never keep a reference into a
        /// disposed generation); Tick re-attaches a fresh twin this same frame.
        /// </summary>
        internal void OnGenerationRebind()
        {
            _itmDisplay = null;
            _itmTwin = null;
            _displayStack = null;
            _displayRuleSnapshot = null;
            _itmStatus = null;
            _panelSnapshot = null;
            _itmWasRunning = false;
            _itmErrorLogged = false;
        }

        /// <summary>
        /// Device End: the session is over. Stop the driver, detach the twin (identity-
        /// guarded) and drop it, and republish so the envelope composes a null values part
        /// (DataUpdate won't run again for this device).
        /// </summary>
        internal void OnEnd(FanatecPlugin plugin)
        {
            _itmDisplay?.Stop();
            plugin?.DetachItmObserver(_itmTwin);
            _itmTwin = null;
            MaybePublishPanelSnapshot();
        }

        // ── Status snapshot / envelope composition ───────────────────────

        private void PublishItmStatusSnapshot(ItmLifecycleState state)
        {
            int gen = _itmDisplay.Lifecycle.SyncGeneration;
            int tick = Environment.TickCount;
            // Wrap-safe elapsed check: Environment.TickCount rolls to int.MinValue every
            // ~24.9 days (and net48 has no TickCount64), so measure the delta as an unsigned
            // difference — correct across the wrap, and it never throws even under a
            // checked-arithmetic build.
            if (_itmStatus != null && state == _itmSnapState && gen == _itmSnapGen
                && unchecked((uint)(tick - _itmSnapTick)) < 1000)
                return;
            _itmSnapState = state;
            _itmSnapGen = gen;
            _itmSnapTick = tick;
            _itmStatus = _itmDisplay.Lifecycle.Describe();
        }

        /// <summary>
        /// Recomposes the published envelope when a part's reference (or the status string)
        /// changed since the last composition — zero allocation otherwise. Called once per
        /// connected ITM frame after all display work, and at every teardown edge that can't
        /// reach that call (the disconnect/leave-ITM/End edges run it directly; a generation
        /// rebind nulls the envelope outright). All parts null composes null, so "nothing to
        /// show" and "never showed anything" are the same observable state.
        /// </summary>
        private void MaybePublishPanelSnapshot()
        {
            // The status part is gated on the driver exactly like the old per-channel
            // accessor was, so a stale line can never describe a dropped driver.
            string status = _itmDisplay == null ? null : _itmStatus;
            var rules = _displayRuleSnapshot;
            var values = _itmTwin?.Snapshot;

            var current = _panelSnapshot;
            if (current == null)
            {
                if (status == null && rules == null && values == null)
                    return;
            }
            else if (ReferenceEquals(current.Rules, rules)
                && ReferenceEquals(current.Values, values)
                && string.Equals(current.ItmStatus, status, StringComparison.Ordinal))
            {
                return;
            }

            _panelSnapshot = status == null && rules == null && values == null
                ? null
                : new DisplayPanelSnapshot(status, rules, values, DateTime.UtcNow);
        }

        // Hands one firmware subscription report to both consumers of the push stream: the
        // driver (whose lifecycle adopts the entries and paces the sends) and the twin
        // (whose handle table and page inference are built from the same reports). A named
        // method keeps the per-frame drain callback a single cached delegate.
        private void FeedItmSubscriptionReport(byte[] report)
        {
            _itmDisplay.OnSubscriptionReport(report);
            _itmTwin?.OnSubscriptionReport(report);
        }

        /// <summary>
        /// Frame step for the display-rules runtime. The empty-config fast path is the
        /// feature's safety guarantee: with no customization document (or an explicitly
        /// empty one) nothing is constructed and nothing runs — the ITM frame path stays
        /// byte-identical to a build without the feature. With a config, the stack is
        /// (re)built whenever its config or driver reference changes (settings swap,
        /// generation rebind, display-id change) and ticked once per frame; rules only drive
        /// pages while the user has ITM enabled.
        ///
        /// The config is the value the caller already volatile-acquired at the top of the
        /// frame's config-consuming path (Tick), and <paramref name="settings"/> was read
        /// AFTER that acquire — so the acquire-before-settings ordering that keeps a torn
        /// pair from latching a stale ItmDefaultPage is preserved here rather than
        /// re-acquiring the config independently.
        /// </summary>
        private void UpdateDisplayRules(DisplayCustomizationConfig config,
            PluginManager pluginManager, GameData data, DisplaySettings settings)
        {
            if (config == null || config.IsEmpty || !settings.ItmEnabled)
            {
                // Customization removed (or ITM turned off): drop the runtime and its
                // published snapshot so nothing stale lingers, and hand page policy back to
                // the built-in owner — the driver returns to the default-page setting live
                // and re-enables its game-start revert. The policy check covers the corner
                // where the stack was already dropped this same frame (a wheel change) while
                // the driver still holds the external policy; the dead stack's published rule
                // snapshot drops there too. Rule state restarts clean when customization
                // comes back — engines are per-config.
                if (_displayStack != null || _itmDisplay.HasExternalPagePolicy)
                {
                    _displayStack = null;
                    _displayRuleSnapshot = null;
                    _itmDisplay.RestoreBuiltInPagePolicy();
                }
                return;
            }

            if (_displayStack == null || !ReferenceEquals(_displayStack.Config, config)
                || !ReferenceEquals(_displayStack.Driver, _itmDisplay))
            {
                _displayStack = new DisplayRuleStack(config, _itmDisplay, _itmDeviceId,
                    settings.ItmDefaultPage,
                    msg => SimHub.Logging.Current.Info("FanaBridge: " + msg));
                // The stack takes page policy the moment it exists (and re-takes it on every
                // rebuild, which is also how a base-page change within a live stack lands):
                // edge-triggered — the driver rests on the stack's base from its next Update
                // and suppresses the lifecycle's game-start revert for the stack's whole
                // tenure.
                _itmDisplay.SetPagePolicy(_displayStack.BaseWirePage);
                _log("FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                    "]: Display rules active (" + (config.Itm?.Rules?.Count ?? 0) + " ITM, " +
                    (config.Legacy?.Rules?.Count ?? 0) + " legacy)");
            }

            var snapshot = _displayStack.Tick(pluginManager, data);
            if (snapshot != null)
                _displayRuleSnapshot = snapshot;
        }
    }
}
