using System;
using System.Collections.Generic;
using System.Threading;
using FanaBridge;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Composition;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Display.Session;
using FanaBridge.Display.Twin;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using GameReaderCommon;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Host;
using SimHub.Plugins;

namespace FanaBridge.Display.Runtime
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

        // v2 world — the sole runtime engine (volatile release / frame latch /
        // composition). One document decides the engine for this device (OQ-4).
        private volatile DisplayConfigV2 _configV2;
        private DisplayConfigV2 _frameConfigV2;
        private DisplayCompositionV2 _compositionV2;
        private ComposedResolutionRecord _composedResolution;
        // Rebuild identity for the catalog envelope (TryResolve returns fresh instances;
        // key by wheel code + device id, not reference).
        private string _compositionCatalogKey;
        private WheelCatalog _compositionCatalog;
        private string _compositionBuiltCatalogKey;
        // Page-control identity for rebuild: true when bound to the live ITM driver.
        private bool _compositionBoundToDriver;
        private ItmDisplayDriver _compositionBoundDriver;
        // itmField value buffer published by the mapper, read by composition conditions.
        private ItmFieldValueBuffer _itmFieldBuffer;
        private IPropertyReader _compositionProperties;
        // Game-identity edge for SeatArbiter / CarrierEvaluator (GameChanged).
        private string _lastGameId;
        // O12: last in-game flag published on the UI envelope.
        private bool _lastInGame;

        // Shared property source for the composition and ITM mapper field plans.
        // Null on the empty-config fast path. BeginFrame runs once per Tick BEFORE
        // the driver's Update so override resolution and conditions share
        // the same framed reads.
        private SimHubPropertySource _propertySource;

        // ITM status line for the Device Status panel / diagnostics. Composed on the
        // DataUpdate thread — a PART of the published envelope, never read cross-thread
        // itself. Refreshed on state/sync changes plus a coarse 1 s tick.
        private string _itmStatus;
        private ItmLifecycleState _itmSnapState;
        private int _itmSnapGen;
        private int _itmSnapTick;

        // The ONE UI-facing volatile channel over status, v2 resolution, and values.
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

        /// <summary>Test seam: the values part of the display envelope (what the ITM
        /// display is showing, from the wire-driven twin), or null while not driving ITM.</summary>
        internal DisplayValuesSnapshot ValuesSnapshot => _itmTwin?.Snapshot;

        /// <summary>Test seam: the v2 composition, null when no live v2 document.</summary>
        internal DisplayCompositionV2 Composition => _compositionV2;

        /// <summary>Test seam: last v2 composed-resolution record (also on the envelope).</summary>
        internal ComposedResolutionRecord ComposedResolution => _composedResolution;

        /// <summary>Test seam: the ITM driver, null until an ITM display is driven.</summary>
        internal ItmDisplayDriver ItmDriver => _itmDisplay;

        /// <summary>Test seam: the shared property source, null on the empty-config path.</summary>
        internal SimHubPropertySource PropertySource => _propertySource;

        // ── Config (volatile release / acquire, the rebuild signal) ──────

        /// <summary>The current v2 document snapshot, or null when none.</summary>
        internal DisplayConfigV2 CurrentConfigV2 => _configV2;

        /// <summary>v2 document acquired for the current frame.</summary>
        internal DisplayConfigV2 FrameConfigV2 => _frameConfigV2;

        /// <summary>
        /// True when the frame-latched v2 document owns the composition engine this frame
        /// (<see cref="SettingsMode.Off"/> is not live).
        /// </summary>
        internal static bool IsLiveCompositionV2(DisplayConfigV2 config)
            => config != null && config.Settings != null
                && config.Settings.Mode != SettingsMode.Off;

        /// <summary>Publishes a normalized v2 document (volatile release).</summary>
        internal void SetConfigV2(DisplayConfigV2 normalized) => _configV2 = normalized;

        /// <summary>
        /// Publishes a pre-normalized v2 document only into absence: CAS with
        /// expected <c>null</c> on the same <c>_configV2</c> slot as
        /// <see cref="TryApplyDisplayConfigV2"/>. Returns false when a document
        /// appeared since the caller last observed absence (caller discards).
        /// </summary>
        internal bool TrySetConfigV2IfAbsent(DisplayConfigV2 normalized)
        {
#pragma warning disable CS0420 // volatile field by ref — Interlocked supplies its own barriers
            var prior = Interlocked.CompareExchange(ref _configV2, normalized, null);
#pragma warning restore CS0420
            return prior == null;
        }

        /// <summary>Drops the v2 document (no <c>display</c> key / cleared).</summary>
        internal void ClearConfigV2() => _configV2 = null;

        /// <summary>
        /// O13: UI-built v2 document through the same normalize path as SetSettings load.
        /// Null clears the v2 document.
        /// </summary>
        internal void ApplyDisplayConfigV2(DisplayConfigV2 config, WheelCatalog catalog = null)
        {
            if (config == null)
            {
                _configV2 = null;
                return;
            }

            Action<string> warn = msg => SimHub.Logging.Current.Warn("FanaBridge: " + msg);
            // Round-trip through serializer so extension data + raw spellings stay honest.
            var loaded = DisplayConfigV2Serializer.Load(
                DisplayConfigV2Serializer.Save(config), warn);
            _configV2 = DisplayConfigV2Validator.Normalize(loaded, warn, catalog);
        }

        /// <summary>
        /// Compare-and-swap form of <see cref="ApplyDisplayConfigV2"/>: normalize
        /// <paramref name="config"/> then publish only when the live document is still
        /// <paramref name="expected"/>. Uses <see cref="Interlocked.CompareExchange{T}"/>
        /// against the same <c>_configV2</c> slot that <see cref="SetConfigV2"/> /
        /// <see cref="ClearConfigV2"/> / <see cref="ApplyDisplayConfigV2"/> write — no
        /// separate lock. Returns false on a lost race (caller surfaces conflict).
        /// </summary>
        internal bool TryApplyDisplayConfigV2(
            DisplayConfigV2 expected, DisplayConfigV2 config, WheelCatalog catalog = null)
        {
            DisplayConfigV2 next;
            if (config == null)
            {
                next = null;
            }
            else
            {
                Action<string> warn = msg => SimHub.Logging.Current.Warn("FanaBridge: " + msg);
                var loaded = DisplayConfigV2Serializer.Load(
                    DisplayConfigV2Serializer.Save(config), warn);
                next = DisplayConfigV2Validator.Normalize(loaded, warn, catalog);
            }

            // Atomic publish: succeeds only if _configV2 is still the expected identity.
            // Concurrent SetSettings / bake / ApplyDisplayConfigV2 plain-writes lose the CAS.
#pragma warning disable CS0420 // volatile field by ref — Interlocked supplies its own barriers
            var prior = Interlocked.CompareExchange(ref _configV2, next, expected);
#pragma warning restore CS0420
            return ReferenceEquals(prior, expected);
        }

        // Segment / special-screen sinks for the rule-driven col01 path — set by the
        // device instance from its sole LegacyDisplayDriver each frame before Tick /
        // TickLegacyRules. The composition never constructs a driver or encoder.
        private Func<byte, byte, byte, bool> _legacySegmentWriter;
        private Func<byte, bool> _specialScreenWriter;
        private Action _specialReleased;

        /// <summary>Threads the device instance's <c>LegacyDisplayDriver.TryShowSegments</c>
        /// into the rule stack. Pass null when no driver is live.</summary>
        internal void SetLegacySegmentWriter(Func<byte, byte, byte, bool> writer)
            => _legacySegmentWriter = writer;

        /// <summary>Threads special-screen send + release reclaim into the rule stack.
        /// Pass null writers when no driver is live.</summary>
        internal void SetSpecialScreenHooks(Func<byte, bool> show, Action released)
        {
            _specialScreenWriter = show;
            _specialReleased = released;
        }

        // ── Per-frame step ───────────────────────────────────────────────

        /// <summary>
        /// One connected ITM frame: builds/hot-swaps the driver + twin, applies the ITM
        /// settings, restarts cold on a wheel change, drains firmware subscriptions, ticks
        /// the driver and twin, runs the co-driver probe / status snapshot, ticks the
        /// display composition, and publishes the envelope. Runs the whole ITM body under its own
        /// try/catch (mirroring the old inline guard): a firmware/transport hiccup logs once
        /// and returns false so the device instance skips its legacy col01 drive this frame
        /// exactly as before (the legacy drive used to share this try). Returns true on a
        /// clean frame.
        ///
        /// Settings are read through a late accessor rather than a pre-evaluated argument so
        /// the volatile v2 config acquire is sequenced BEFORE the settings read:
        /// the writer (SetSettings) plain-writes the settings then volatile-releases the
        /// config, so a frame that acquires a newly-published config is guaranteed to also
        /// see the settings written before it.
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
                // H5: ColdEntry derives playlist entry destination from the live document
                // at command time (no stored playlist state). Non-playlist → null →
                // EffectiveDefaultPage (byte-identical).
                _itmDisplay.Lifecycle.ColdEntryPageProvider = ResolvePlaylistColdEntryWire;
                // The wire-driven twin for the same display device, on the same clock as
                // the driver so their snapshot throttles stay in step. Attached to the
                // shared ITM outbound tap the instant it exists — bootstrap by construction:
                // the tap is live from encoder creation, so the twin observes the whole
                // session from the first bring-up frame with no mid-stream attach.
                _itmTwin = new VirtualItmDisplay(deviceId: _itmDeviceId, nowMs: _itmClock());
                plugin.AttachItmObserver(_itmTwin);
                // A display-id hot-swap rebuilds the driver UNDER a still-live rule stack
                // Keep page policy continuous across an ITM display-id rebuild when a live
                // v2 composition owns it. Re-resolve rest identity against the NEW device
                // catalog (never carry a raw wire across the device-id boundary).
                if (_compositionV2 != null && TakesItmPagePolicyV2(_compositionV2.Config))
                {
                    var table = ItmPageTable.ForDevice(_itmDeviceId);
                    byte wire = 0;
                    if (_compositionV2.ConfiguredBase is ItmPage basePage
                        && table.TryGetWire(basePage, out byte w))
                        wire = w;
                    _itmDisplay.SetPagePolicy(wire);
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
                // Acquire the volatile config first, then read the settings snapshot.
                // FrameConfigV2 is the same acquire: DriveLegacyCol01 after
                // this Tick must arbitrate against them, never re-read the volatile.
                var configV2 = _configV2;
                _frameConfigV2 = configV2;
                var settingsSnap = settings();

                bool itmEnabled = configV2?.Settings?.Mode == SettingsMode.On;
                _itmDisplay.Enabled = itmEnabled;
                if (itmEnabled)
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
                    // rebuild the stack/composition cold alongside the display. Page policy
                    // deliberately stays external across this null: the cold entry above
                    // targeted the stack's base, and UpdateDisplayRules rebuilds from the
                    // same config this frame and re-takes policy (or, if customization
                    // vanished too, restores the built-in).
                    DropEngines();
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

                // ── Shared property source ─────────────────────────────────
                // BeginFrame MUST run before the driver's Update: the mapper resolves
                // field plans through this same SimHubPropertySource instance during
                // value encode. Composition reuses the source after the driver.
                // No live composition keeps the fast path (no source, no overrides).
                bool v2Live = IsLiveCompositionV2(configV2);
                if (v2Live)
                {
                    if (_propertySource == null)
                        _propertySource = new SimHubPropertySource(
                            msg => SimHub.Logging.Current.Info("FanaBridge: " + msg));
                    _propertySource.BeginFrame(pluginManager, data);
                    // Leave mapper plans as last composition tick installed them (lag-1).
                }
                else
                {
                    _propertySource = null;
                    _itmFieldBuffer = null;
                    _compositionProperties = null;
                    _itmDisplay.Mapper.ParamValueSink = null;
                    _itmDisplay.Mapper.ConfigureFromPlans(null, null);
                }

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

                // Display customization (after the driver's Update so the director observes
                // the lifecycle post-Tick). Constructor switch: one document decides the
                // engine (OQ-4). The already-acquired configs and settings snapshot are
                // handed down so the acquire-before-settings order holds through the build.
                UpdateDisplayRules(configV2, pluginManager, data, settingsSnap);
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
        /// profile): stop the session so the next Itm selection re-runs bring-up.
        /// When <paramref name="clearCompositionWithoutDriver"/> is true (DisplayType.None
        /// teardown), also clears composition diagnostics even if the ITM driver is already
        /// null (basic-v2 → None must not retain a stale envelope). When false and no driver
        /// is held (basic path every frame), composition is left alone so TickLegacyRules
        /// can keep driving it.
        /// </summary>
        internal void OnDisplayTypeLeftItm(
            FanatecPlugin plugin,
            bool clearCompositionWithoutDriver = false)
        {
            if (_itmDisplay == null)
            {
                if (!clearCompositionWithoutDriver)
                    return;
                DropEngines();
                _propertySource = null;
                _itmFieldBuffer = null;
                _compositionProperties = null;
                MaybePublishPanelSnapshot();
                return;
            }
            _itmDisplay.Stop();
            _itmDisplay = null;
            // The twin models the ITM panel — it goes with the driver. Detach it from the
            // shared tap (identity-guarded) so a later ITM device doesn't inherit a dead
            // observer, and drop the reference.
            plugin.DetachItmObserver(_itmTwin);
            _itmTwin = null;
            _itmWasRunning = false;
            DropEngines();
            _propertySource = null;
            _itmFieldBuffer = null;
            _compositionProperties = null;
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
            DropEngines();               // rules restart clean with the reconnect
            _propertySource = null;
            _itmFieldBuffer = null;
            _compositionProperties = null;
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
            DropEngines();
            _propertySource = null;
            _itmFieldBuffer = null;
            _compositionProperties = null;
            _itmStatus = null;
            _panelSnapshot = null;
            _itmWasRunning = false;
            _itmErrorLogged = false;
            _lastGameId = null;
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
            var composed = _composedResolution;
            var values = _itmTwin?.Snapshot;
            bool inGame = _lastInGame;
            // Normalize null → empty so the no-composition path stays reference-stable
            // against DisplayPanelSnapshot's empty fallback (recompose gate).
            var aggregates = _compositionV2?.LastAggregates;
            var manual = _compositionV2?.LastManual;

            var current = _panelSnapshot;
            if (current == null)
            {
                if (status == null && composed == null && values == null)
                    return;
            }
            else if (ReferenceEquals(current.ComposedResolution, composed)
                && ReferenceEquals(current.Values, values)
                && string.Equals(current.ItmStatus, status, StringComparison.Ordinal)
                && current.InGame == inGame
                && SameAggregates(current.Aggregates, aggregates)
                && ReferenceEquals(current.Manual, manual))
            {
                return;
            }

            _panelSnapshot = status == null && composed == null && values == null
                ? null
                : new DisplayPanelSnapshot(
                    status, values, DateTime.UtcNow, composed,
                    inGame, aggregates, manual);
        }

        /// <summary>
        /// Aggregates equality for the recompose gate: null and empty are the same
        /// (snapshot ctor maps null → empty array).
        /// </summary>
        private static bool SameAggregates(
            IReadOnlyList<AggregateMembership> published,
            IReadOnlyList<AggregateMembership> live)
        {
            if (ReferenceEquals(published, live))
                return true;
            bool pubEmpty = published == null || published.Count == 0;
            bool liveEmpty = live == null || live.Count == 0;
            return pubEmpty && liveEmpty;
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
        /// Frame step for the display runtime. A live
        /// v2 document builds <see cref="DisplayCompositionV2"/>. Empty / Off keeps
        /// the fast path.
        ///
        /// The config values are those the caller already volatile-acquired at the top of
        /// the frame's config-consuming path (Tick), and <paramref name="settings"/> was
        /// read AFTER that acquire — so the acquire-before-settings ordering holds.
        /// </summary>
        private void UpdateDisplayRules(DisplayConfigV2 configV2,
            PluginManager pluginManager, GameData data,
            DisplaySettings settings)
        {
            // v2 key wins: one document decides the engine for this device.
            if (IsLiveCompositionV2(configV2))
            {
                UpdateCompositionV2(configV2, pluginManager, data, itmPath: true);
                return;
            }

            // Leaving v2 (or never had it) restores built-in policy if needed.
            DropV2CompositionOnly();
            if (_itmDisplay != null && _itmDisplay.HasExternalPagePolicy)
                _itmDisplay.RestoreBuiltInPagePolicy();
        }

        /// <summary>
        /// Basic (non-ITM) frame step. v2 docs take the composition path with a no-op page
        /// control; empty documents build no engine.
        /// </summary>
        internal void TickLegacyRules(PluginManager pluginManager, GameData data,
            DisplaySettings settings)
        {
            // One acquire for the whole basic frame — DriveLegacyCol01 must arbitrate
            // against FrameConfigV2, not re-read the volatile after Apply.
            var configV2 = _configV2;
            _frameConfigV2 = configV2;

            if (IsLiveCompositionV2(configV2))
            {
                if (_propertySource == null)
                    _propertySource = new SimHubPropertySource(
                        msg => SimHub.Logging.Current.Info("FanaBridge: " + msg));
                _propertySource.BeginFrame(pluginManager, data);
                UpdateCompositionV2(configV2, pluginManager, data, itmPath: false);
                MaybePublishPanelSnapshot();
                return;
            }

            DropV2CompositionOnly();
            if (_propertySource != null)
            {
                _propertySource = null;
                MaybePublishPanelSnapshot();
            }
        }

        /// <summary>
        /// Builds / ticks <see cref="DisplayCompositionV2"/> for a live v2 document.
        /// <paramref name="itmPath"/> true = ITM driver present; false = basic/legacy-only.
        /// </summary>
        private void UpdateCompositionV2(DisplayConfigV2 configV2, PluginManager pluginManager,
            GameData data, bool itmPath)
        {
            // LegacyOnly or no ITM driver → no page control (never take ITM page policy).
            bool wantDriver = itmPath
                && _itmDisplay != null
                && configV2.Settings.Mode == SettingsMode.On;

            EnsureCatalogResolved();

            bool needRebuild = _compositionV2 == null
                || !ReferenceEquals(_compositionV2.Config, configV2)
                || _compositionBoundToDriver != wantDriver
                || (wantDriver && !ReferenceEquals(_compositionBoundDriver, _itmDisplay))
                || !string.Equals(_compositionBuiltCatalogKey, _compositionCatalogKey,
                    StringComparison.Ordinal);

            if (needRebuild)
            {
                Action<string> log = msg => SimHub.Logging.Current.Info("FanaBridge: " + msg);
                IItmPageControl pageControl = wantDriver
                    ? (IItmPageControl)new ItmLifecyclePageControl(_itmDisplay.Lifecycle)
                    : NoOpPageControl.Instance;
                byte itmDeviceId = wantDriver ? _itmDeviceId : (byte)0;

                // Fresh field buffer per rebuild; mapper publishes into it this tenure.
                _itmFieldBuffer = new ItmFieldValueBuffer();
                if (_itmDisplay != null)
                    _itmDisplay.Mapper.ParamValueSink = _itmFieldBuffer;
                // BeginFrame already ran above Update when the property source exists.
                if (_propertySource == null)
                    _propertySource = new SimHubPropertySource(log);
                _compositionProperties = new PropertyReaderWithItmFields(
                    _propertySource, _itmFieldBuffer);

                Func<ushort, bool> hasEncoder = null;
                if (_itmDisplay != null)
                {
                    var mapper = _itmDisplay.Mapper;
                    hasEncoder = id => mapper.HasEncoder(id);
                }

                // Late-bound clock (null → private stopwatch if the device has not injected one).
                Func<long> clock = _itmClock();
                if (clock == null)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    clock = () => sw.ElapsedMilliseconds;
                }

                _compositionV2 = new DisplayCompositionV2(
                    configV2,
                    _compositionCatalog,
                    pageControl,
                    itmDeviceId,
                    nowMs: clock,
                    log: log,
                    properties: _compositionProperties,
                    options: new DisplayCompositionV2Options
                    {
                        DeviceKey = _config.WheelCode ?? "",
                        HasEncoder = hasEncoder,
                    });

                // Mapper plan application at tick END (lag-1; G2 seam).
                if (_itmDisplay != null)
                {
                    var driver = _itmDisplay;
                    _compositionV2.ApplyFieldPlans = (plans, props) =>
                        driver.Mapper.ConfigureFromPlans(plans, props);
                }
                else
                {
                    _compositionV2.ApplyFieldPlans = null;
                }

                _compositionBoundToDriver = wantDriver;
                _compositionBoundDriver = wantDriver ? _itmDisplay : null;
                _compositionBuiltCatalogKey = _compositionCatalogKey;
                _log("FanatecWheelDeviceInstance[" + _config.Capabilities.Name +
                    "]: Display composition v2 active (mode="
                    + (configV2.Settings.ModeRaw ?? "on") + ")");
            }

            // Page policy: mode != LegacyOnly AND any ITM-page destination (RISK-5).
            if (itmPath && _itmDisplay != null)
            {
                if (configV2.Settings.Mode == SettingsMode.On
                    && TakesItmPagePolicyV2(configV2))
                    _itmDisplay.SetPagePolicy(_compositionV2.BaseWirePage);
                else if (_itmDisplay.HasExternalPagePolicy)
                    _itmDisplay.RestoreBuiltInPagePolicy();
            }

            BindCompositionWriters(_compositionV2);

            bool inGame = data != null && data.GameRunning && data.NewData != null;
            string gameId = data != null ? data.GameName : null;
            bool gameChanged = !string.Equals(gameId, _lastGameId, StringComparison.Ordinal);
            _lastGameId = gameId;
            _lastInGame = inGame;

            var content = BuildSegmentContent(data, inGame, _compositionProperties);
            _composedResolution = _compositionV2.Tick(new DisplayCompositionV2TickInput
            {
                InGame = inGame,
                GameChanged = gameChanged,
                GameId = gameId,
                Content = content,
            });
        }

        /// <summary>
        /// ColdEntry provider: derive playlist entry destination from the <b>live</b>
        /// document at command time — no stored playlist state. Volatile-acquires
        /// <c>_configV2</c>; playlist idle → <see cref="IdleCompile.ResolveAtEntry"/>
        /// (ONE selector with arbiter ticks) → page identity → wire on the current
        /// device table. Non-playlist / non-page entry / unresolvable → null so
        /// <see cref="ItmLifecycleController"/> falls back to EffectiveDefaultPage.
        /// Device-id hot-swap cannot carry a foreign wire.
        /// </summary>
        private byte? ResolvePlaylistColdEntryWire()
        {
            // Volatile acquire: only the current document decides cold entry.
            var config = _configV2;
            if (config == null)
                return null;

            var idle = config.Priority?.Rest?.Idle;
            if (idle == null || idle.Kind != IdleKind.Playlist || idle.DegradedAtLoad)
                return null;

            var map = BuildPlaylistMap(config);
            // Capability envelope when catalog is live; null = untested (filter does not drop).
            var sc = _compositionCatalog?.ScreenCommands;
            var compiled = IdleCompile.ResolveAtEntry(idle, sc, map);
            if (compiled.Kind != IdleCompileKind.Page)
                return null;

            return ResolveWireForDestination(compiled.PageDestinationId);
        }

        /// <summary>
        /// Id → entry map matching SeatArbiter / WheelScreenArbiter construction
        /// (case-insensitive; degraded entries omitted).
        /// </summary>
        private static IReadOnlyDictionary<string, PlaylistEntry> BuildPlaylistMap(
            DisplayConfigV2 config)
        {
            if (config?.Playlists == null || config.Playlists.Count == 0)
                return null;

            var map = new Dictionary<string, PlaylistEntry>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < config.Playlists.Count; i++)
            {
                var pl = config.Playlists[i];
                if (pl?.Id != null && !pl.DegradedAtLoad)
                    map[pl.Id] = pl;
            }
            return map.Count == 0 ? null : map;
        }

        private byte? ResolveWireForDestination(string destinationId)
        {
            if (string.IsNullOrEmpty(destinationId))
                return null;
            var table = ItmPageTable.ForDevice(_itmDeviceId);
            if (destinationId.StartsWith("itm:", StringComparison.Ordinal))
            {
                string catalogId = destinationId.Substring("itm:".Length);
                if (TryCatalogIdToItmPage(catalogId, out ItmPage page)
                    && table.TryGetWire(page, out byte wire)
                    && wire != 0)
                    return wire;
                return null;
            }
            if (destinationId.StartsWith("hosted:", StringComparison.Ordinal))
            {
                // Hosted faces park on Legacy wire when the device has one.
                return table.LegacyWire != 0 ? table.LegacyWire : (byte?)null;
            }
            return null;
        }

        /// <summary>Inverse of <see cref="CatalogPageIdAdapter.FromItmPage"/> (explicit pair).</summary>
        private static bool TryCatalogIdToItmPage(string catalogPageId, out ItmPage page)
        {
            page = default;
            if (string.IsNullOrEmpty(catalogPageId))
                return false;
            if (string.Equals(catalogPageId, "lapInfo", StringComparison.Ordinal))
            {
                page = ItmPage.LapInfo;
                return true;
            }
            if (string.Equals(catalogPageId, "fuelErsDrs", StringComparison.Ordinal))
            {
                page = ItmPage.FuelErsDrs;
                return true;
            }
            if (string.Equals(catalogPageId, "carSettings", StringComparison.Ordinal))
            {
                page = ItmPage.CarSettings;
                return true;
            }
            if (string.Equals(catalogPageId, "lapTimes", StringComparison.Ordinal))
            {
                page = ItmPage.LapTimes;
                return true;
            }
            if (string.Equals(catalogPageId, "tyreTemps", StringComparison.Ordinal))
            {
                page = ItmPage.TyreTemps;
                return true;
            }
            if (string.Equals(catalogPageId, "legacy", StringComparison.Ordinal))
            {
                page = ItmPage.Legacy;
                return true;
            }
            return false;
        }

        private void BindCompositionWriters(DisplayCompositionV2 composition)
        {
            composition.TryWriteLegacySegments = _legacySegmentWriter;
            composition.TryShowSpecialScreen = _specialScreenWriter;
            composition.OnSpecialReleased = _specialReleased;
        }

        private void EnsureCatalogResolved()
        {
            string key = (_config.WheelCode ?? "").Trim().ToLowerInvariant()
                + ":" + _itmDeviceId;
            if (string.Equals(_compositionCatalogKey, key, StringComparison.Ordinal)
                && _compositionCatalog != null)
                return;

            Action<string> log = msg => SimHub.Logging.Current.Info("FanaBridge: " + msg);
            WheelCatalog catalog;
            if (!CatalogLoader.TryResolve(_config.WheelCode, out catalog, log,
                    itmDeviceId: _itmDeviceId))
                catalog = null;
            _compositionCatalog = catalog;
            _compositionCatalogKey = key;
        }

        private static SegmentContentContext BuildSegmentContent(
            GameData data, bool inGame, IPropertyReader properties)
        {
            StatusDataBase d = inGame && data != null ? data.NewData : null;
            return new SegmentContentContext
            {
                InGame = inGame,
                SpeedLocal = d != null ? (double?)d.SpeedLocal : null,
                Gear = d != null ? d.Gear : null,
                Rpms = d != null ? (double?)d.Rpms : null,
                Position = d != null ? (double?)d.Position : null,
                Fuel = d != null ? (double?)d.Fuel : null,
                Properties = properties,
            };
        }

        /// <summary>Drop the composition engine and its published parts (lifecycle edges).</summary>
        private void DropEngines()
        {
            _compositionV2 = null;
            _composedResolution = null;
            _compositionBoundToDriver = false;
            _compositionBoundDriver = null;
            _compositionCatalogKey = null;
            _compositionBuiltCatalogKey = null;
            _compositionCatalog = null;
            if (_itmDisplay != null)
                _itmDisplay.Mapper.ParamValueSink = null;
        }

        private void DropV2CompositionOnly()
        {
            if (_compositionV2 == null && _composedResolution == null)
                return;
            bool heldPolicy = _compositionV2 != null
                && _itmDisplay != null
                && _itmDisplay.HasExternalPagePolicy
                && TakesItmPagePolicyV2(_compositionV2.Config);
            _compositionV2 = null;
            _composedResolution = null;
            _compositionBoundToDriver = false;
            _compositionBoundDriver = null;
            _compositionCatalogKey = null;
            _compositionBuiltCatalogKey = null;
            _compositionCatalog = null;
            _itmFieldBuffer = null;
            _compositionProperties = null;
            if (_itmDisplay != null)
            {
                _itmDisplay.Mapper.ParamValueSink = null;
                if (heldPolicy)
                    _itmDisplay.RestoreBuiltInPagePolicy();
            }
        }

        /// <summary>
        /// v2 page-policy takeover (RISK-5): settings.mode != LegacyOnly AND the document
        /// has any ITM-page destination (rest, seats, cycles, pageOrder, or itmPage entries).
        /// </summary>
        internal static bool TakesItmPagePolicyV2(DisplayConfigV2 config)
        {
            if (config?.Settings == null)
                return false;
            if (config.Settings.Mode == SettingsMode.LegacyOnly
                || config.Settings.Mode == SettingsMode.Off)
                return false;
            return HasAnyItmPageDestination(config);
        }

        /// <summary>True when the v2 document references any ITM catalog page destination.</summary>
        internal static bool HasAnyItmPageDestination(DisplayConfigV2 config)
        {
            if (config == null)
                return false;

            if (IsItmPageRef(config.Priority?.Rest?.InSessionPage)
                || IsItmPageRef(config.Priority?.Rest?.Idle?.Page))
                return true;

            var rows = config.Priority?.EffectiveRows;
            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (IsItmPageRef(rows[i]?.Target))
                        return true;
                }
            }

            if (config.Pages != null)
            {
                for (int i = 0; i < config.Pages.Count; i++)
                {
                    var p = config.Pages[i];
                    if (p != null && p.Kind == PageEntryKind.ItmPage && !p.Removed
                        && !string.IsNullOrEmpty(p.CatalogPageId))
                        return true;
                }
            }

            if (config.PageOrder != null)
            {
                for (int i = 0; i < config.PageOrder.Count; i++)
                {
                    if (IsItmPageRef(config.PageOrder[i]))
                        return true;
                }
            }

            if (config.Cycles != null)
            {
                for (int i = 0; i < config.Cycles.Count; i++)
                {
                    var members = config.Cycles[i]?.Members;
                    if (members == null)
                        continue;
                    for (int j = 0; j < members.Count; j++)
                    {
                        if (IsItmPageRef(members[j]))
                            return true;
                    }
                }
            }

            return false;
        }

        private static bool IsItmPageRef(PageRef pageRef)
            => pageRef != null
                && pageRef.Kind == PageRefKind.ItmPage
                && !string.IsNullOrEmpty(pageRef.CatalogPageId);

        /// <summary>No-op ITM page control for basic-wheel rule stacks (no lifecycle).</summary>
        private sealed class NoOpPageControl : IItmPageControl
        {
            public static readonly NoOpPageControl Instance = new NoOpPageControl();
            public ItmLifecycleState State => ItmLifecycleState.Idle;
            public byte? CurrentWirePage => null;
            public long SyncGeneration => 0;
            public void RequestPage(byte wirePage) { }
        }
    }
}
