using System;
using System.Collections.Generic;
using FanaBridge.Protocol;

namespace FanaBridge.Display.Rules
{
    /// <summary>Which surface an engine instance drives (each device wires one of each).</summary>
    public enum RuleSetKind
    {
        /// <summary>The ITM (pixel) display — page targets, manual navigation applies.</summary>
        Itm,
        /// <summary>The legacy 7-segment surface — screen targets only.</summary>
        Legacy,
    }

    /// <summary>
    /// Evaluates ONE prioritized rule list against per-tick inputs and emits a display
    /// intent. Pure, deterministic, and synchronous: injected clock, no threads, no I/O —
    /// the same input sequence always produces the same outputs (the test suite relies on
    /// this). One instance per rule set; a config swap builds a NEW engine, mirroring the
    /// profile store's snapshot-and-swap, so engine state is exactly per-config.
    ///
    /// The model, and why:
    /// - <b>Priority is list order</b> (index 0 wins); the base target is the implicit
    ///   lowest-priority "always" rule. Rules whose live activation isn't winning wait.
    /// - <b>Holds</b> decide an activation's lifetime: WhileActive tracks a level condition;
    ///   ForDuration runs a window from each (re)fire; Indefinite latches until dismissed
    ///   (manual navigation, a preempting rule finishing, eligibility loss, or — for level
    ///   conditions — the condition going false). A preempted Indefinite never resumes:
    ///   whatever replaced it superseded it. Preemption means losing the screen — an
    ///   Indefinite that fires while already outranked never had it, so it waits and
    ///   claims the screen when the incumbent finishes, like any other hold.
    /// - <b>Dwell floor</b>: once the emitted intent changes it holds for
    ///   <see cref="MinDwellMs"/>, except a strictly-higher-priority activation may preempt
    ///   after <see cref="PreemptFloorMs"/>. This exists to protect the firmware — rapid
    ///   PageSets are the historical cause of dropped switches — so trigger churn can never
    ///   flap the display. (Conservative single-rig constants; hardware validation may tune.)
    /// - <b>Manual override</b> mirrors the lifecycle's "adopt, never fight" upward: a
    ///   wheel-button page change (already adopted by the lifecycle) becomes the resting
    ///   target, every live activation is dismissed, and rules re-enter competition only
    ///   via a fresh fire — the driver just chose a page; rules must not fight that choice.
    ///   The resting target reverts to the base page when a game session starts.
    /// - Pages this display doesn't have (per the caller-supplied available set) make a rule
    ///   permanently <see cref="RuleStatus.Unavailable"/> — surfaced here, not silently at
    ///   the director, because the status shows in the UI's priority list.
    ///
    /// The engine knows content identities only (<see cref="ItmPage"/>, screen ids) — wire
    /// page numbers, device ids, and the lifecycle live in the layers around it.
    /// </summary>
    public class DisplayRuleEngine
    {
        /// <summary>Minimum residency of an emitted intent before it may change again.
        /// Dialed 1500 → 500 (kelchm 2026-07-18): the long dwell was invasive during
        /// testing and release-latency feel; 500 still bounds flapping.</summary>
        internal const int MinDwellMs = 500;

        /// <summary>Earlier change allowed when a strictly-higher-priority activation preempts.</summary>
        internal const int PreemptFloorMs = 250;

        /// <summary>Activity ring capacity — oldest entries drop beyond this.</summary>
        internal const int ActivityCapacity = 50;

        // Comparison tolerance lives on CarrierEvaluator.Epsilon (single source).

        // Per-rule runtime state. Everything the engine remembers between ticks lives here
        // (plus the selection/resting fields below) — nothing else is stateful.
        // Condition/hold/eligibility state lives on CarrierRuntime (E3 extraction); Spec is
        // a live adapter refreshed from DisplayRule before each evaluate.
        private sealed class RuleRuntime
        {
            public DisplayRule Rule;
            public int Index;
            public bool Usable;          // enabled with a complete shape (validator-guaranteed)
            public bool Unavailable;     // targets a page this display doesn't have — permanent
            public CarrierSpec Spec;     // v1 DisplayRule adapted onto the carrier machine
            public CarrierRuntime State; // condition + hold clocks + eligibility + latch
            /// <summary>Cached warn callback — allocated once at build, not per tick.</summary>
            public Action WarnMissing;
        }

        private readonly List<RuleRuntime> _rules = new List<RuleRuntime>();
        private readonly RuleSetKind _kind;
        private readonly RuleIntent _baseIntent;
        private readonly Func<long> _now;
        private readonly Action<string> _log;

        // The resting target: base until a manual navigation replaces it; reverts to base
        // on the next in-game rising edge (or a config swap, which is a new engine).
        private RuleIntent _restingIntent;
        private bool _prevInGame;

        // The emitted selection (dwell state): the winning rule the intent follows, or the
        // resting target. Held distinct from the logical winner so an expired winner's
        // target keeps showing until the dwell floor lets the intent move on.
        private bool _hasSelection;
        private string _selectionRuleId;      // null = resting target
        private int _selectionIndex;          // int.MaxValue for resting (lowest priority)
        private RuleTarget _selectionTarget;  // null for resting
        private long _selectionChangedAt;
        private long _cycleAnchor;            // flip-period origin (set at selection time)
        // The selection's resolved cycle pages, cached at selection time: CyclePages
        // parses raw names and allocates on every read, and CurrentIntent runs per tick.
        private IReadOnlyList<ItmPage?> _selectionCyclePages;

        private string _prevWinnerId;         // RuleExpired detection

        // Activity ring (bounded; oldest dropped). Single-threaded like the engine itself —
        // the caller snapshots GetActivityEvents() output for cross-thread hand-off.
        private readonly DisplayActivityEvent[] _ring = new DisplayActivityEvent[ActivityCapacity];
        private int _ringStart;
        private int _ringCount;
        private long _activityVersion;

        private DisplayRuleEngine(IReadOnlyList<DisplayRule> rules, RuleSetKind kind,
            RuleIntent baseIntent, ISet<ItmPage> availablePages, Func<long> nowMs, Action<string> log)
        {
            _kind = kind;
            _baseIntent = baseIntent;
            _restingIntent = baseIntent;
            _now = nowMs ?? DefaultClock();
            _log = log ?? (_ => { });
            _selectionChangedAt = long.MinValue / 2;   // establishing the first intent is not a change

            if (rules != null)
            {
                foreach (var rule in rules)
                {
                    if (rule == null)
                        continue;
                    var rt = new RuleRuntime
                    {
                        Rule = rule,
                        Index = _rules.Count,
                        Spec = CarrierSpec.FromDisplayRule(rule),
                        State = new CarrierRuntime(),
                    };
                    // Closure cached at build — the hot path must not allocate per tick.
                    var captured = rt;
                    rt.WarnMissing = () => WarnMissingOnce(captured);
                    // The validated snapshot guarantees effectively-enabled rules have
                    // complete shapes; the shape check is a cheap guard against an
                    // unvalidated hand-built list.
                    rt.Usable = rule.EffectivelyEnabled && rule.When != null && rule.Show != null
                        && rule.Hold != null;
                    if (rt.Usable && kind == RuleSetKind.Itm)
                        rt.Unavailable = TargetsUnavailablePage(rule.Show, availablePages);
                    if (rt.Unavailable)
                        _log("DisplayRules: rule '" + DisplayRuleFormatter.Label(rule)
                            + "' targets a page this display does not have — unavailable");
                    _rules.Add(rt);
                }
            }
        }

        /// <summary>Builds the engine for a device's ITM rule set. <paramref name="availablePages"/>
        /// is the device's page set (null = every page); rules targeting a page outside it are
        /// permanently unavailable (a Bentley has no Car Settings page, for example).</summary>
        public static DisplayRuleEngine ForItm(IReadOnlyList<DisplayRule> rules, ItmPage basePage,
            ISet<ItmPage> availablePages = null, Func<long> nowMs = null, Action<string> log = null)
        {
            if (availablePages != null && !availablePages.Contains(basePage))
                log?.Invoke("DisplayRules: base page " + ItmTelemetry.NameOf(basePage)
                    + " is not available on this display");
            return new DisplayRuleEngine(rules, RuleSetKind.Itm,
                new RuleIntent(TargetKind.Page, basePage, null, null), availablePages, nowMs, log);
        }

        /// <summary>Builds the engine for a device's legacy rule set. A null
        /// <paramref name="baseScreenId"/> means a blank display when no rule is active.</summary>
        public static DisplayRuleEngine ForLegacy(IReadOnlyList<DisplayRule> rules,
            string baseScreenId = null, Func<long> nowMs = null, Action<string> log = null)
        {
            return new DisplayRuleEngine(rules, RuleSetKind.Legacy,
                new RuleIntent(TargetKind.SegmentScreen, null, baseScreenId, null), null, nowMs, log);
        }

        private static Func<long> DefaultClock()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            return () => sw.ElapsedMilliseconds;
        }

        /// <summary>Which surface this engine drives.</summary>
        public RuleSetKind Kind => _kind;

        /// <summary>See <see cref="RuleEngineResult.ActivityVersion"/>.</summary>
        public long ActivityVersion => _activityVersion;

        /// <summary>The activity ring, oldest first. Returns a fresh snapshot each call; the
        /// engine is single-threaded, so cross-thread hand-off is the caller's concern.</summary>
        public IReadOnlyList<DisplayActivityEvent> GetActivityEvents()
        {
            var events = new DisplayActivityEvent[_ringCount];
            for (int i = 0; i < _ringCount; i++)
                events[i] = _ring[(_ringStart + i) % ActivityCapacity];
            return events;
        }

        /// <summary>
        /// Evaluates one tick. Call once per frame; the emitted intent is what the display
        /// should show now (the page director diffs it against the lifecycle's reality).
        /// </summary>
        public RuleEngineResult Tick(RuleEngineInput input)
        {
            long now = _now();
            bool inGame = input.InGame;

            // Game start (in-game rising edge): the resting target reverts to base — a
            // manual page choice belongs to the session it was made in. When the emitted
            // selection is resting on that manual page, the revert changes the emitted
            // intent, so it stamps the dwell clock like any other change — a rule firing
            // a tick later must still honor the floor.
            if (inGame && !_prevInGame)
            {
                if (_hasSelection && _selectionRuleId == null && !_restingIntent.Equals(_baseIntent))
                    _selectionChangedAt = now;
                _restingIntent = _baseIntent;
            }
            _prevInGame = inGame;

            // Manual navigation (ITM surface only): the lifecycle already adopted the page —
            // the display HAS moved. Adopt it here too, immediately and without dwell: the
            // manual page becomes the resting target, and every activation alive right now
            // is superseded (dismissed). Rules re-enter only via a fresh fire — a level
            // condition that was already true does not re-claim the screen.
            if (input.Manual.HasValue && _kind == RuleSetKind.Itm)
            {
                // A null page is navigation to a page outside the device's catalog: the
                // resting target then carries no page at all — the director requests
                // nothing for it, so the driver's choice is never fought.
                ItmPage? page = input.Manual.Value.Page;
                _restingIntent = new RuleIntent(TargetKind.Page, page, null, null);
                foreach (var rt in _rules)
                    rt.State.ClearActivation();
                _prevWinnerId = null;   // dismissed by the driver's own choice, not an expiry
                AddEvent(now, ActivityKind.ManualNavigation,
                    "Manual page change — " + (page == null
                        ? "a page not in this device's catalog" : ItmTelemetry.NameOf(page.Value)),
                    null);
                SetSelection(null, now, logReturn: false);
            }

            // Evaluate every rule's condition and update its activation (carrier evaluator).
            // Refresh Spec from the live DisplayRule so post-construction mutations of
            // When/Hold/Eligible are observed (pre-extraction public API semantics).
            var tickIn = new CarrierTickInput
            {
                NowMs = now,
                InGame = inGame,
                Properties = input.Properties,
                TriggeredActions = input.TriggeredActions,
            };
            foreach (var rt in _rules)
            {
                if (!rt.Usable || rt.Unavailable)
                    continue;

                rt.Spec.RefreshFromDisplayRule(rt.Rule);
                bool fresh = CarrierEvaluator.Evaluate(rt.Spec, rt.State, tickIn, rt.WarnMissing);
                if (fresh)
                    AddEvent(now, ActivityKind.RuleFired,
                        DisplayRuleFormatter.Label(rt.Rule), rt.Rule.Id);
            }

            // Winner: lowest index with a live, non-superseded activation.
            RuleRuntime winner = null;
            foreach (var rt in _rules)
            {
                if (rt.State.Active && !rt.State.Superseded)
                {
                    winner = rt;
                    break;
                }
            }

            // Mark preemption: an Indefinite that HELD the screen and was displaced by a
            // different winner is superseded — when that preemptor finishes it does not
            // resume. One that fired while already outranked never had the screen: it
            // waits like any other hold and takes over when the incumbent finishes.
            // (Preempted ForDuration/WhileActive activations DO resume; only Indefinite
            // latches, so only Indefinite needs the supersede rule.)
            if (winner != null && _prevWinnerId != null && winner.Rule.Id != _prevWinnerId)
            {
                var displaced = FindById(_prevWinnerId);
                if (displaced != null && displaced.State.Active
                    && displaced.Rule.Hold.Kind == HoldKind.UntilDismissed)
                    displaced.State.MarkSuperseded();
            }

            // A superseded Indefinite whose preemptors are all gone is dismissed now, not
            // resumed — it would otherwise be the winner again, and it was superseded.
            // Active cleared only; Superseded bit retained (HEAD parity / diagnostics).
            int winnerIdx = winner != null ? winner.Index : int.MaxValue;
            foreach (var rt in _rules)
                if (rt.State.Active && rt.State.Superseded && rt.Index < winnerIdx)
                    rt.State.Active = false;

            // The previous winner's activation ended (hold expiry or dismissal) — log it.
            // A mere preemption (previous winner still active, just outranked) is not an expiry.
            if (_prevWinnerId != null && (winner == null || winner.Rule.Id != _prevWinnerId))
            {
                var prev = FindById(_prevWinnerId);
                if (prev != null && !prev.State.Active)
                    AddEvent(now, ActivityKind.RuleExpired,
                        DisplayRuleFormatter.Label(prev.Rule) + " — expired", prev.Rule.Id);
            }
            _prevWinnerId = winner != null ? winner.Rule.Id : null;

            // Dwell floor: the emitted selection follows the winner, but never flaps. A
            // change is allowed after MinDwellMs, or after PreemptFloorMs when the newcomer
            // strictly outranks the current selection (the resting target ranks below every
            // rule). Until then the previous selection keeps the screen — including a
            // just-expired rule's target, which is exactly the anti-flap.
            if (!_hasSelection)
            {
                // Establishing the very first selection is not an intent change — the dwell
                // clock must not start here, or a rule firing right after a config swap
                // would be pointlessly blocked.
                SetSelection(winner, now, logReturn: false);
                _selectionChangedAt = long.MinValue / 2;
            }
            else if (!IsSelected(winner))
            {
                long held = now - _selectionChangedAt;
                int desiredIdx = winner != null ? winner.Index : int.MaxValue;
                if (held >= MinDwellMs || (desiredIdx < _selectionIndex && held >= PreemptFloorMs))
                    SetSelection(winner, now, logReturn: true);
            }

            return new RuleEngineResult(CurrentIntent(now), BuildStates(winner, now), _activityVersion);
        }

        // ── Property-missing warning (one per rule, ever) ────────────────

        private void WarnMissingOnce(RuleRuntime rt)
        {
            // WarnedMissing lives on CarrierRuntime; CarrierEvaluator gates the call.
            _log("DisplayRules: rule '" + DisplayRuleFormatter.Label(rt.Rule) + "' — property '"
                + rt.Rule.When.Source.Name + "' unavailable; condition idle until it appears");
        }

        // ── Selection / intent ───────────────────────────────────────────

        private bool IsSelected(RuleRuntime rt)
            => rt == null
                ? _selectionRuleId == null
                : string.Equals(_selectionRuleId, rt.Rule.Id, StringComparison.Ordinal);

        private void SetSelection(RuleRuntime sel, long now, bool logReturn)
        {
            bool wasRule = _hasSelection && _selectionRuleId != null;
            _hasSelection = true;
            _selectionRuleId = sel != null ? sel.Rule.Id : null;
            _selectionIndex = sel != null ? sel.Index : int.MaxValue;
            _selectionTarget = sel != null ? sel.Rule.Show : null;
            _selectionChangedAt = now;
            bool cycleFamily = sel != null && sel.Rule.Show.Kind == TargetKind.Cycle;
            _selectionCyclePages = cycleFamily ? sel.Rule.Show.CyclePages : null;
            if (cycleFamily)
                _cycleAnchor = now;   // the flip period starts at win time
            if (sel == null && wasRule && logReturn)
                AddEvent(now, ActivityKind.ReturnedToBase,
                    "Returned to " + DescribeResting(), null);
        }

        private string DescribeResting()
            => _restingIntent.Kind == TargetKind.Page
                ? (_restingIntent.Page == null
                    ? "the current page"   // resting on an uncataloged manual page
                    : DisplayRuleFormatter.PageName(_restingIntent.Page))
                : (_restingIntent.ScreenId != null
                    ? "screen '" + _restingIntent.ScreenId + "'" : "blank");

        private RuleIntent CurrentIntent(long now)
        {
            if (_selectionRuleId == null)
                return _restingIntent;
            var t = _selectionTarget;
            switch (t.Kind)
            {
                case TargetKind.SegmentScreen:
                    return new RuleIntent(TargetKind.SegmentScreen, null, t.ScreenId, _selectionRuleId);
                case TargetKind.Special:
                    return new RuleIntent(TargetKind.Special, null, null, _selectionRuleId, t.Command);
                case TargetKind.Cycle:
                    // The dwell floor does not apply to the internal flip — the flip is the
                    // rule's target, not an intent change.
                    // Math.Max guards a hand-built list; the validator clamps PeriodMs >= 1s.
                    var pages = _selectionCyclePages;
                    if (pages == null || pages.Count == 0)
                        return new RuleIntent(TargetKind.Page, null, null, _selectionRuleId);
                    long phase = (now - _cycleAnchor) / Math.Max(1, t.PeriodMs) % pages.Count;
                    return new RuleIntent(TargetKind.Page,
                        pages[(int)phase], null, _selectionRuleId);
                default:
                    return new RuleIntent(TargetKind.Page, t.Page, null, _selectionRuleId);
            }
        }

        private RuleLiveState[] BuildStates(RuleRuntime winner, long now)
        {
            var states = new RuleLiveState[_rules.Count];
            for (int i = 0; i < _rules.Count; i++)
            {
                var rt = _rules[i];
                RuleStatus status;
                int? remaining = null;
                if (!rt.Usable)
                    status = RuleStatus.Disabled;
                else if (rt.Unavailable)
                    status = RuleStatus.Unavailable;
                else if (!rt.State.EligibleNow)
                    status = RuleStatus.Ineligible;
                else if (rt == winner)
                {
                    status = RuleStatus.OnScreen;
                    if (rt.Rule.Hold.Kind == HoldKind.ForDuration)
                        remaining = (int)Math.Max(0, rt.State.ExpiresAt - now);
                }
                else if (rt.State.Active)
                    status = RuleStatus.Waiting;
                else
                    status = RuleStatus.Armed;
                states[i] = new RuleLiveState(rt.Rule.Id, status, remaining);
            }
            return states;
        }

        private RuleRuntime FindById(string id)
        {
            foreach (var rt in _rules)
                if (string.Equals(rt.Rule.Id, id, StringComparison.Ordinal))
                    return rt;
            return null;
        }

        // ── Availability / activity ring ─────────────────────────────────

        private static bool TargetsUnavailablePage(RuleTarget show, ISet<ItmPage> availablePages)
        {
            if (availablePages == null)
                return false;   // no set supplied = every page available
            switch (show.Kind)
            {
                case TargetKind.Page:
                    return show.Page == null || !availablePages.Contains(show.Page.Value);
                case TargetKind.Cycle:
                {
                    var pages = show.CyclePages;
                    if (pages == null || pages.Count == 0)
                        return true;
                    for (int i = 0; i < pages.Count; i++)
                    {
                        if (pages[i] == null || !availablePages.Contains(pages[i].Value))
                            return true;
                    }
                    return false;
                }
                default:
                    // Legacy screens and special commands are never page-gated.
                    return false;
            }
        }

        private void AddEvent(long at, ActivityKind kind, string text, string ruleId)
        {
            int idx;
            if (_ringCount < ActivityCapacity)
            {
                idx = (_ringStart + _ringCount) % ActivityCapacity;
                _ringCount++;
            }
            else
            {
                idx = _ringStart;
                _ringStart = (_ringStart + 1) % ActivityCapacity;
            }
            _ring[idx] = new DisplayActivityEvent(at, kind, text, ruleId);
            _activityVersion++;
        }
    }
}
