using System;
using System.Collections.Generic;
using FanaBridge.Protocol;

namespace FanaBridge.Display
{
    /// <summary>One director tick's output, consumed by the device's frame loop.</summary>
    public struct DirectorTickResult
    {
        public DirectorTickResult(ManualNavigation? manual, string legacyScreenId,
            byte? requestedWirePage)
        {
            Manual = manual;
            LegacyScreenId = legacyScreenId;
            RequestedWirePage = requestedWirePage;
        }

        /// <summary>A wheel-button page change detected this tick — feed it into the ITM
        /// engine's NEXT tick (<see cref="RuleEngineInput.Manual"/>). The one-frame latency
        /// is deliberate and harmless at frame cadence: the lifecycle already adopted the
        /// page, so nothing is waiting on the engine's reaction.</summary>
        public ManualNavigation? Manual { get; }

        /// <summary>The legacy screen the current intent wants shown (set every tick the
        /// intent targets one and this display has a legacy page). The director gets the
        /// display onto the legacy page; the 3-char text write itself is a later phase —
        /// until then callers may log it.</summary>
        public string LegacyScreenId { get; }

        /// <summary>The wire page passed to <see cref="IItmPageControl.RequestPage"/> this
        /// tick, or null when nothing was requested (diagnostics and tests).</summary>
        public byte? RequestedWirePage { get; }
    }

    /// <summary>
    /// Translates the rule engine's intents into lifecycle page requests and detects manual
    /// (wheel-button) navigation for the engine's manual-override policy. Sits strictly
    /// between the two: the engine knows content identities (<see cref="ItmPage"/>, screen
    /// ids), the lifecycle knows wire page numbers — the director resolves between them via
    /// <see cref="ItmDeviceCatalog.PagesFor"/> for its display device, both directions.
    ///
    /// The model, and why:
    /// - <b>One request per intent change, never per frame.</b> A request is issued only
    ///   when the intent's resolved wire page differs from both the last request and the
    ///   lifecycle's confirmed page; issuing latches it. The controller owns execution
    ///   (quiet window, PageSet spacing, push confirmation) and queues requests that arrive
    ///   mid-procedure, so the director may ask in any non-dormant state.
    /// - <b>Landings are judged on sync-generation edges.</b> A Synced observation with an
    ///   advanced generation on a page the director neither requested nor was already
    ///   watching is manual navigation — reported to the engine, never fought (requests are
    ///   held for that one tick; the engine adopts the page on its next tick). The first
    ///   Synced observation after construction or a cold start (the lifecycle forgets its
    ///   page — <see cref="IItmPageControl.CurrentWirePage"/> goes null outside Synced) is
    ///   a baseline and is never manual; a landing on the page we asked for, or a
    ///   re-confirmation of the page already showing (recovery, repaint), is not manual
    ///   either. Synced with a null page is the controller's "adopted an uncataloged
    ///   parameter set" — manual navigation without a page identity: the engine rests on
    ///   "wherever the wheel is" so the unnamed page is never fought.
    /// - <b>A non-manual landing somewhere else voids the request.</b> A cold start that
    ///   settles on the bring-up default while the engine still wants its page re-issues
    ///   the intent — once per landing (each landing is a new sync generation), so a
    ///   persistent disagreement can never turn into a request storm.
    /// - <b>Never bypasses the lifecycle</b>: no frames, no timing — the injected clock is
    ///   accepted for construction parity with the engine but currently unused (the
    ///   controller owns all pacing).
    /// </summary>
    public class DisplayPageDirector
    {
        private readonly IItmPageControl _control;
        private readonly Action<string> _log;

        // The device's page table, resolved once: identity → wire and wire → identity,
        // plus the legacy page's wire number (0 = this display has none).
        private readonly Dictionary<ItmPage, byte> _wireByPage = new Dictionary<ItmPage, byte>();
        private readonly Dictionary<byte, ItmPage> _pageByWire = new Dictionary<byte, ItmPage>();
        private readonly byte _legacyWire;

        // ── Landing/request bookkeeping ──────────────────────────────────
        private byte _lastRequestedWire;    // last issued request (0 = none outstanding)
        private byte _lastSyncedWire;       // page seen at the previous Synced observation
        private long _lastGeneration = long.MinValue;   // any first observation is an edge
        private bool _baselineSeen;         // a Synced page has been observed since cold
        private bool _wasCold;              // edge-detects the lifecycle forgetting its page
        private bool _wasUncataloged;       // previous tick was Synced on an unnamed page

        // ── Warn-once latches (per director lifetime, like the engine's) ─
        private bool _warnedNoLegacyPage;
        private bool _warnedUncatalogedCurrent;
        private HashSet<byte> _unknownWireWarned;
        private HashSet<ItmPage> _unresolvedPageWarned;

        public DisplayPageDirector(IItmPageControl control, byte itmDeviceId,
            Func<long> nowMs = null, Action<string> log = null)
        {
            _control = control ?? throw new ArgumentNullException(nameof(control));
            _log = log ?? (_ => { });

            foreach (var info in ItmDeviceCatalog.PagesFor(itmDeviceId))
            {
                _wireByPage[info.Page] = info.Number;
                _pageByWire[info.Number] = info.Page;
                if (info.IsLegacy)
                    _legacyWire = info.Number;
            }
        }

        /// <summary>
        /// Runs one director tick against the engine's current intent. Call once per frame,
        /// after the lifecycle's own Tick (so landings are observed the frame they happen)
        /// and after the engine's Tick (so the intent is this frame's).
        /// </summary>
        public DirectorTickResult Tick(RuleIntent intent)
        {
            var state = _control.State;
            byte? current = _control.CurrentWirePage;
            long generation = _control.SyncGeneration;
            bool generationAdvanced = generation != _lastGeneration;
            _lastGeneration = generation;

            // Cold edge: the lifecycle forgot its page (stop, wheel change, cold bring-up).
            // The next Synced observation re-establishes the baseline, and any outstanding
            // request is void — the controller dropped its own queue on the same event.
            // Synced with a null page is NOT cold: the controller only reports that
            // combination after adopting a parameter set outside the catalog — the display
            // is alive on a page we cannot name, handled as its own case below.
            bool cold = !current.HasValue && state != ItmLifecycleState.Synced;
            if (cold && !_wasCold)
            {
                _baselineSeen = false;
                _lastSyncedWire = 0;
                _lastRequestedWire = 0;
            }
            _wasCold = cold;

            ManualNavigation? manual = null;
            bool holdRequests = false;

            if (state == ItmLifecycleState.Synced && current.HasValue)
            {
                byte landed = current.Value;
                if (!_baselineSeen)
                {
                    // First Synced observation since construction / cold start: baseline,
                    // never manual. If we had asked for a different page, the request did
                    // not survive the cold path — void it so the request phase below may
                    // re-issue the intent (once; see the class comment).
                    _baselineSeen = true;
                    if (_lastRequestedWire != 0 && landed != _lastRequestedWire)
                        _lastRequestedWire = 0;
                }
                else if (generationAdvanced && landed != _lastRequestedWire
                    && landed != _lastSyncedWire)
                {
                    // The display moved somewhere the director did not send it.
                    if (_pageByWire.TryGetValue(landed, out var identity))
                    {
                        // Wheel-button navigation (a landed legacy page reports as the
                        // legacy page identity). The engine adopts it on its next tick;
                        // requesting anything now would fight the driver's choice for the
                        // one frame before it does.
                        manual = new ManualNavigation(identity);
                        holdRequests = true;
                        _lastRequestedWire = 0;
                    }
                    else
                    {
                        // A wire page outside this device's catalog: never manual (there is
                        // no identity to report). No manual explanation means the request
                        // phase may re-assert the intent, once for this landing.
                        WarnUnknownWireOnce(landed);
                        _lastRequestedWire = 0;
                    }
                }
                // landed == _lastRequestedWire: our request confirmed (possibly via
                // recovery re-establishing it) — not manual, latch stays.
                // landed == _lastSyncedWire: a repaint or recovery of the page already
                // showing — not manual.
                _lastSyncedWire = landed;
            }
            else if (state == ItmLifecycleState.Synced)   // && page unknown
            {
                // Synced with the page unknown: the controller adopted a parameter set
                // matching no catalog page — the wheel button reached a page the firmware
                // knows and we don't. Adopt, never fight. Reported as manual navigation
                // WITHOUT a page identity: the engine parks its resting target on
                // "wherever the wheel is" (no page intent), so nothing is requested while
                // the display sits there, and rules re-enter via a fresh fire exactly as
                // after manual navigation to a cataloged page.
                if (!_baselineSeen)
                {
                    // Director joined (config swap) while the display sits on an unnamed
                    // page: baseline, never manual — the request phase may steer to the
                    // engine's target, as with any cataloged first observation.
                    _baselineSeen = true;
                    _lastRequestedWire = 0;
                    WarnUncatalogedCurrentOnce();
                }
                else if (generationAdvanced && !_wasUncataloged)
                {
                    manual = new ManualNavigation(null);
                    holdRequests = true;
                    _lastRequestedWire = 0;   // any outstanding request evidently lost
                    WarnUncatalogedCurrentOnce();
                }
                // _lastSyncedWire keeps the last NAMED page: the wheel button returning
                // to it lands as a re-confirmation, not as manual navigation.
            }
            _wasUncataloged = state == ItmLifecycleState.Synced && !current.HasValue;

            // Resolve the intent to a desired wire page (0 = nothing to request).
            string legacyScreenId = null;
            byte desired = 0;
            if (intent.Kind == TargetKind.LegacyScreen)
            {
                if (_legacyWire != 0)
                {
                    desired = _legacyWire;
                    legacyScreenId = intent.ScreenId;
                }
                else if (!_warnedNoLegacyPage)
                {
                    // No legacy page on this display: the screen cannot show. The director
                    // knows no base page of its own, so "treat as base" means leaving the
                    // display where it is.
                    _warnedNoLegacyPage = true;
                    _log("Display director: intent targets a legacy screen but this display"
                        + " has no legacy page — leaving the current page");
                }
            }
            else if (intent.Page.HasValue && !_wireByPage.TryGetValue(intent.Page.Value, out desired))
            {
                // Engine-side availability gating should prevent this (rules targeting a
                // missing page are Unavailable); the base page can still slip through.
                desired = 0;
                WarnUnresolvedPageOnce(intent.Page.Value);
            }

            byte? requested = null;
            if (desired != 0 && !holdRequests
                && state != ItmLifecycleState.Idle && state != ItmLifecycleState.Disabled
                && desired != _lastRequestedWire
                && (!current.HasValue || desired != current.Value))
            {
                // Idle/Disabled drop requests silently — issuing there would latch a
                // request the controller never saw. Everywhere else the controller either
                // acts (Synced) or queues (in-flight states) — its queueing is honored.
                _control.RequestPage(desired);
                _lastRequestedWire = desired;
                requested = desired;
                _log("Display director: requesting page " + desired
                    + (intent.SourceRuleId != null ? " (rule " + intent.SourceRuleId + ")" : " (base)"));
            }

            return new DirectorTickResult(manual, legacyScreenId, requested);
        }

        private void WarnUncatalogedCurrentOnce()
        {
            if (_warnedUncatalogedCurrent)
                return;
            _warnedUncatalogedCurrent = true;
            _log("Display director: display is on a page outside this device's page catalog"
                + " — adopting it (rules re-enter on a fresh fire)");
        }

        private void WarnUnknownWireOnce(byte wirePage)
        {
            if (_unknownWireWarned == null)
                _unknownWireWarned = new HashSet<byte>();
            if (_unknownWireWarned.Add(wirePage))
                _log("Display director: display landed on wire page " + wirePage
                    + ", which is not in this device's page catalog — treating as neutral");
        }

        private void WarnUnresolvedPageOnce(ItmPage page)
        {
            if (_unresolvedPageWarned == null)
                _unresolvedPageWarned = new HashSet<ItmPage>();
            if (_unresolvedPageWarned.Add(page))
                _log("Display director: intent targets " + ItmTelemetry.NameOf(page)
                    + ", which this display does not have — not requested");
        }
    }
}
