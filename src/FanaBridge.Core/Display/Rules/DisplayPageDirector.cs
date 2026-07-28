using System;
using System.Collections.Generic;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Session;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Rules
{
    /// <summary>One director tick's output, consumed by the device's frame loop.</summary>
    public struct DirectorTickResult
    {
        public DirectorTickResult(ManualNavigation? manual, string legacyScreenId,
            byte? requestedWirePage)
            : this(manual, legacyScreenId, requestedWirePage,
                CurrentPageKnowledge.Unknown,
                adopted: false, reverted: false, adoptWarned: false)
        {
        }

        public DirectorTickResult(
            ManualNavigation? manual,
            string legacyScreenId,
            byte? requestedWirePage,
            CurrentPageKnowledge pageKnowledge,
            bool adopted,
            bool reverted,
            bool adoptWarned)
        {
            Manual = manual;
            LegacyScreenId = legacyScreenId;
            RequestedWirePage = requestedWirePage;
            PageKnowledge = pageKnowledge;
            AdoptedThisTick = adopted;
            RevertedThisTick = reverted;
            AdoptWarnedThisTick = adoptWarned;
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

        /// <summary>
        /// Honest current-page knowledge from sync bookkeeping (E7). Unknown until the
        /// first Synced observation; mid-switch reports the commanded page (optimistic
        /// twin, DECISIONS 7c).
        /// </summary>
        public CurrentPageKnowledge PageKnowledge { get; }

        /// <summary>
        /// True when this tick adopted an uncommanded page change (flag-off parity path,
        /// or reject-path adopt-with-warning after re-assert / exhausted retries). Surfaces
        /// so the v2 composition can update the manual row — the director does not know
        /// the row.
        /// </summary>
        public bool AdoptedThisTick { get; }

        /// <summary>
        /// True when this tick issued a reject-uncommanded push-back request (one issue
        /// per attempt while the fight is outstanding). Only meaningful when
        /// <see cref="DisplayPageDirector.RejectUncommandedChanges"/> is on.
        /// </summary>
        public bool RevertedThisTick { get; }

        /// <summary>
        /// True when this tick adopted after a wheel re-assert within the reject debounce
        /// window, or after the bounded re-issue cap was exhausted (warn-once companion
        /// of <see cref="AdoptedThisTick"/>).
        /// </summary>
        public bool AdoptWarnedThisTick { get; }
    }

    /// <summary>
    /// Translates the rule engine's intents into lifecycle page requests and detects manual
    /// (wheel-button) navigation for the engine's manual-override policy. Sits strictly
    /// between the two: the engine knows content identities (<see cref="ItmPage"/>, screen
    /// ids), the lifecycle knows wire page numbers — the director resolves between them via
    /// the <see cref="ItmPageTable"/> for its display device, both directions.
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
    ///   used only by the optional reject-uncommanded debounce (E7); the controller owns
    ///   all page-switch pacing.
    /// - <b>Reject-uncommanded (E7, dormant):</b> when
    ///   <see cref="RejectUncommandedChanges"/> is on (nothing live sets it yet), an
    ///   uncommanded generation-edge landing is push-backed to the last commanded page.
    ///   "Recurrence" means an UNRESOLVED fight only: confirmed revert landing clears
    ///   state so a later press is a fresh fight. While outstanding, re-assert of the
    ///   same wire page inside the inclusive <see cref="RejectDebounceMs"/> window
    ///   adopts with warn; ignored push-backs re-issue up to
    ///   <see cref="MaxRevertAttempts"/> then adopt-with-warn ("the wheel is not accepting
    ///   page changes"). Out-of-table wire pages use that same machine (identity null)
    ///   only under reject. Flag off (today's world) keeps adopt byte-identical to pre-E7:
    ///   cataloged uncommanded landings adopt; out-of-catalog wire landings stay
    ///   Manual=null with one immediate intent re-assert and warn-once (v9 pinned).
    ///   The v2 <c>AdoptedUnknownPage</c> shape is composition-side only (E8); the director
    ///   flag-off path deliberately keeps Manual=null for unknown wire pages until E8b
    ///   re-signs that semantic — do not emit ManualNavigation(null) on this path.
    /// </summary>
    public class DisplayPageDirector
    {
        /// <summary>
        /// Reject-uncommanded debounce window (E7 implementation constant, test-pinned).
        /// Inclusive: a re-assert at exactly this many ms after issue is still in-window.
        /// </summary>
        public const int RejectDebounceMs = 2000;

        /// <summary>
        /// Max push-back issues while a single fight is outstanding (first issue + retries).
        /// Exhaustion → adopt-with-warn "the wheel is not accepting page changes".
        /// </summary>
        public const int MaxRevertAttempts = 3;

        private readonly IItmPageControl _control;
        private readonly Action<string> _log;
        private readonly Func<long> _now;

        // The device's page table: identity ↔ wire both directions plus the legacy page's
        // wire number (0 = this display has none) — the one page-mapping source of truth.
        private readonly ItmPageTable _pages;

        // ── Landing/request bookkeeping ──────────────────────────────────
        private byte _lastRequestedWire;    // last issued request (0 = none outstanding)
        private byte _lastSyncedWire;       // page seen at the previous Synced observation
        private long _lastGeneration = long.MinValue;   // any first observation is an edge
        private bool _baselineSeen;         // a Synced page has been observed since cold
        private bool _wasCold;              // edge-detects the lifecycle forgetting its page
        private bool _wasUncataloged;       // previous tick was Synced on an unnamed page

        // ── Reject-uncommanded (E7; dormant while RejectUncommandedChanges is false) ─
        // Last page FanaBridge commanded OR catalog-adopted (future revert target).
        // 0 = nothing commanded yet — reject must NOT revert while Unknown / never-commanded.
        private byte _lastCommandedWire;
        // Outstanding fight: true only while a push-back has not yet landed (or been
        // surrendered). Cleared on confirmed revert landing — "recurrence" = unresolved only.
        private bool _revertOutstanding;
        private byte _revertFromWire;       // the uncommanded page we pushed back from (0 = uncataloged)
        private long _revertIssuedAtMs;     // clock of the latest push-back issue
        private int _revertAttemptCount;    // issues in this fight (1..MaxRevertAttempts)
        private bool _warnedAdoptAfterRevert;
        private bool _warnedWheelNotAccepting;

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
            _now = nowMs ?? (() => 0);
            _pages = ItmPageTable.ForDevice(itmDeviceId);
        }

        /// <summary>
        /// When true, uncommanded page changes on a sync-generation edge are push-backed
        /// to the last commanded page. Default false = adopt (today's world, byte-identical).
        /// Nothing live sets this yet (E8 wires <c>settings.rejectUncommandedChanges</c>).
        /// </summary>
        public bool RejectUncommandedChanges { get; set; }

        /// <summary>
        /// Runs one director tick against the engine's current intent. Call once per frame,
        /// after the lifecycle's own Tick (so landings are observed the frame they happen)
        /// and after the engine's Tick (so the intent is this frame's).
        /// </summary>
        public DirectorTickResult Tick(DirectorIntent intent)
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
                // Commanded page is intentionally retained across cold: a connect that
                // never announced still has nothing commanded (_lastCommandedWire == 0).
                ClearRevertState();
            }
            _wasCold = cold;

            ManualNavigation? manual = null;
            bool holdRequests = false;
            bool adopted = false;
            bool reverted = false;
            bool adoptWarned = false;
            byte? forcedRequest = null; // reject push-back (issued below, outside intent path)

            if (state == ItmLifecycleState.Synced && current.HasValue)
            {
                byte landed = current.Value;

                // Confirmed revert landing: fight resolved — clear so a later press is fresh.
                if (_revertOutstanding
                    && landed == _lastRequestedWire
                    && landed == _lastCommandedWire
                    && _lastCommandedWire != 0)
                {
                    ClearRevertState();
                }

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
                else if (generationAdvanced)
                {
                    // Unresolved fight still on the uncommanded page (even when
                    // landed == _lastSyncedWire — that used to silently drop retries).
                    bool unresolvedOnFrom =
                        _revertOutstanding && landed == _revertFromWire;

                    bool freshUncommanded =
                        landed != _lastRequestedWire && landed != _lastSyncedWire;

                    if (unresolvedOnFrom || freshUncommanded)
                    {
                        if (_pages.TryGetPage(landed, out var pageId))
                        {
                            // Cataloged uncommanded landing: adopt (flag-off) or reject machine.
                            ApplyUncommandedLanding(
                                landed, pageId,
                                ref manual, ref holdRequests,
                                ref adopted, ref reverted, ref adoptWarned,
                                ref forcedRequest);
                        }
                        else
                        {
                            // Wire page outside this device's catalog.
                            // Flag-off (v9 pinned): never Manual — no identity to report, so
                            // the request phase re-asserts the intent once for this landing.
                            // Reject mode only: identity-null state machine (E7-001).
                            // AdoptedUnknownPage is V2/E8 composition, not this path.
                            WarnUnknownWireOnce(landed);
                            if (RejectUncommandedChanges)
                            {
                                ApplyUncommandedLanding(
                                    landed, identity: null,
                                    ref manual, ref holdRequests,
                                    ref adopted, ref reverted, ref adoptWarned,
                                    ref forcedRequest);
                            }
                            else
                            {
                                _lastRequestedWire = 0;
                            }
                        }
                    }
                }
                // landed == _lastRequestedWire: our request confirmed (possibly via
                // recovery re-establishing it) — not manual, latch stays.
                // landed == _lastSyncedWire (and not unresolved fight): a repaint or
                // recovery of the page already showing — not manual.
                _lastSyncedWire = landed;
            }
            else if (state == ItmLifecycleState.Synced)   // && page unknown
            {
                // Synced with the page unknown: the controller adopted a parameter set
                // matching no catalog page — the wheel button reached a page the firmware
                // knows and we don't. Adopt, never fight (flag-off). Under reject: still
                // no identity and no commanded page at connect-before-announce → do not
                // invent a revert target.
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
                    ApplyUncommandedLanding(
                        wirePage: null, identity: null,
                        ref manual, ref holdRequests,
                        ref adopted, ref reverted, ref adoptWarned,
                        ref forcedRequest);
                    if (manual.HasValue || adopted)
                        WarnUncatalogedCurrentOnce();
                }
                // _lastSyncedWire keeps the last NAMED page: the wheel button returning
                // to it lands as a re-confirmation, not as manual navigation.
            }
            _wasUncataloged = state == ItmLifecycleState.Synced && !current.HasValue;

            // Resolve the intent to a desired wire page (0 = nothing to request).
            // Special commands write col01 directly — the director does not page-navigate.
            string legacyScreenId = null;
            byte desired = 0;
            if (intent.Kind == DirectorIntentKind.Special)
            {
                desired = 0;
            }
            else if (intent.Kind == DirectorIntentKind.SegmentScreen)
            {
                if (_pages.LegacyWire != 0)
                {
                    desired = _pages.LegacyWire;
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
            else if (intent.Page.HasValue && !_pages.TryGetWire(intent.Page.Value, out desired))
            {
                // Engine-side availability gating should prevent this (rules targeting a
                // missing page are Unavailable); the base page can still slip through.
                desired = 0;
                WarnUnresolvedPageOnce(intent.Page.Value);
            }

            byte? requested = null;

            // Reject push-back takes priority over the intent request this tick (we already
            // decided not to fight via holdRequests, but the push-back IS the fight).
            if (forcedRequest.HasValue
                && state != ItmLifecycleState.Idle && state != ItmLifecycleState.Disabled)
            {
                _control.RequestPage(forcedRequest.Value);
                _lastRequestedWire = forcedRequest.Value;
                _lastCommandedWire = forcedRequest.Value;
                requested = forcedRequest.Value;
                _log("Display director: rejecting uncommanded page change — requesting "
                    + forcedRequest.Value);
            }
            else if (desired != 0 && !holdRequests
                && state != ItmLifecycleState.Idle && state != ItmLifecycleState.Disabled
                && desired != _lastRequestedWire
                && (!current.HasValue || desired != current.Value))
            {
                // Idle/Disabled drop requests silently — issuing there would latch a
                // request the controller never saw. Everywhere else the controller either
                // acts (Synced) or queues (in-flight states) — its queueing is honored.
                _control.RequestPage(desired);
                _lastRequestedWire = desired;
                _lastCommandedWire = desired;
                requested = desired;
                _log("Display director: requesting page " + desired
                    + (intent.SourceRuleId != null ? " (rule " + intent.SourceRuleId + ")" : " (base)"));
            }

            var knowledge = BuildPageKnowledge(state, current);
            return new DirectorTickResult(
                manual, legacyScreenId, requested, knowledge,
                adopted, reverted, adoptWarned);
        }

        /// <summary>
        /// Handle an uncommanded generation-edge landing (or an unresolved fight reconfirm).
        /// Flag-off: adopt (byte-identical). Flag-on: push-back with clear-on-land, inclusive
        /// debounce reassert-adopt, and bounded re-issue then surrender.
        /// </summary>
        private void ApplyUncommandedLanding(
            byte? wirePage,
            ItmPage? identity,
            ref ManualNavigation? manual,
            ref bool holdRequests,
            ref bool adopted,
            ref bool reverted,
            ref bool adoptWarned,
            ref byte? forcedRequest)
        {
            if (!RejectUncommandedChanges)
            {
                // Today's world: adopt, never fight.
                Adopt(identity, wirePage, warned: false,
                    ref manual, ref holdRequests, ref adopted, ref adoptWarned);
                return;
            }

            // Reject path. Nothing commanded yet → cannot invent a push-back target.
            // Adopt so the runtime stays honest (same as flag-off for this edge case).
            if (_lastCommandedWire == 0)
            {
                Adopt(identity, wirePage, warned: false,
                    ref manual, ref holdRequests, ref adopted, ref adoptWarned);
                return;
            }

            long now = _now();
            byte fromWire = wirePage ?? (byte)0;

            // Unresolved fight still on the uncommanded page.
            if (_revertOutstanding && fromWire == _revertFromWire)
            {
                // Inclusive debounce: re-assert at exactly RejectDebounceMs still adopts.
                if (now - _revertIssuedAtMs <= RejectDebounceMs)
                {
                    Adopt(identity, wirePage, warned: true,
                        ref manual, ref holdRequests, ref adopted, ref adoptWarned);
                    if (!_warnedAdoptAfterRevert)
                    {
                        _warnedAdoptAfterRevert = true;
                        _log("Display director: wheel re-asserted uncommanded page within "
                            + RejectDebounceMs + " ms — adopting with warning");
                    }
                    return;
                }

                // Past the window, still outstanding (firmware ignored the push-back):
                // re-issue up to MaxRevertAttempts, then surrender loudly.
                if (_revertAttemptCount >= MaxRevertAttempts)
                {
                    Adopt(identity, wirePage, warned: true,
                        ref manual, ref holdRequests, ref adopted, ref adoptWarned);
                    if (!_warnedWheelNotAccepting)
                    {
                        _warnedWheelNotAccepting = true;
                        _log("Display director: the wheel is not accepting page changes");
                    }
                    return;
                }

                _revertAttemptCount++;
                IssueRevert(fromWire, now, ref holdRequests, ref reverted, ref forcedRequest);
                return;
            }

            // Fresh observed change (fight was cleared, or different page): one new fight.
            _revertAttemptCount = 1;
            IssueRevert(fromWire, now, ref holdRequests, ref reverted, ref forcedRequest);
        }

        private void IssueRevert(
            byte fromWire,
            long now,
            ref bool holdRequests,
            ref bool reverted,
            ref byte? forcedRequest)
        {
            forcedRequest = _lastCommandedWire;
            holdRequests = true;
            reverted = true;
            _revertOutstanding = true;
            _revertFromWire = fromWire;
            _revertIssuedAtMs = now;
            _lastRequestedWire = _lastCommandedWire;
            // Do not set Manual — the page is being rejected, not adopted.
        }

        private void Adopt(
            ItmPage? identity,
            byte? wirePage,
            bool warned,
            ref ManualNavigation? manual,
            ref bool holdRequests,
            ref bool adopted,
            ref bool adoptWarned)
        {
            manual = new ManualNavigation(identity);
            holdRequests = true;
            adopted = true;
            if (warned)
                adoptWarned = true;
            _lastRequestedWire = 0;
            // Cataloged adopt updates the future revert target (E7-002 / OPUS-12 target).
            if (identity.HasValue && wirePage.HasValue && wirePage.Value != 0)
                _lastCommandedWire = wirePage.Value;
            ClearRevertState();
        }

        private CurrentPageKnowledge BuildPageKnowledge(ItmLifecycleState state, byte? current)
        {
            if (!_baselineSeen)
                return CurrentPageKnowledge.Unknown;
            if (state != ItmLifecycleState.Synced)
            {
                // Optimistic twin (DECISIONS 7c): mid-switch reports the COMMANDED page
                // immediately — the twin shows commanded state, not a stale last-synced.
                if (_lastCommandedWire != 0)
                {
                    _pages.TryGetPage(_lastCommandedWire, out var commandedId);
                    return CurrentPageKnowledge.Known(_lastCommandedWire, commandedId);
                }
                return CurrentPageKnowledge.Unknown;
            }
            if (!current.HasValue)
                return CurrentPageKnowledge.KnownUncataloged;
            _pages.TryGetPage(current.Value, out var identity);
            return CurrentPageKnowledge.Known(current.Value, identity);
        }

        private void ClearRevertState()
        {
            _revertOutstanding = false;
            _revertFromWire = 0;
            _revertIssuedAtMs = 0;
            _revertAttemptCount = 0;
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
