using System;
using System.Collections.Generic;

namespace FanaBridge.Protocol
{
    /// <summary>The ITM lifecycle states. See <see cref="ItmLifecycleController"/>.</summary>
    public enum ItmLifecycleState
    {
        /// <summary>Not started (no connection, or never started). Nothing on the wire.</summary>
        Idle,
        /// <summary>User turned ITM off. Gate-off sent once; dormant until re-enabled.</summary>
        Disabled,
        /// <summary>Cold entry: gate-on → enable → PageSet, each latched on transport accept.</summary>
        BringUp,
        /// <summary>Commands sent (bring-up or resume); waiting for the confirming subscription push.</summary>
        AwaitPush,
        /// <summary>Host page change in flight: values suspended, quiet window, PageSet, await push.</summary>
        Switching,
        /// <summary>Push confirmed; handles adopted from the firmware. Values may flow.</summary>
        Synced,
        /// <summary>Expected push missing — running the recovery ladder (values stay suspended).</summary>
        Recovery,
        /// <summary>Game exited: gate-off (screen dark). Resumes via PageSet-while-off → gate-on.</summary>
        TelemetryIdle,
        /// <summary>Recovery ladder exhausted. Exponential backoff, then retry the gate-cycle rung.</summary>
        Unavailable,
    }

    /// <summary>
    /// The ITM display lifecycle state machine. Hardware truth this encodes (all verified on
    /// hardware or in official-software captures — see docs/reference/protocol.md, "ITM Display"):
    ///
    /// - The firmware's subscription push is the protocol's <b>only acknowledgment</b>. Every
    ///   state that has asked for a page change knows what push it expects and by when; a missing
    ///   push is a first-class failure with a recovery ladder, not silence.
    /// - Value traffic concurrent with a page switch is the identified cause of dropped switches,
    ///   and a handle can be re-bound to a different parameter on any change — so values and
    ///   ParamDefs are <b>fully suspended</b> from the moment a switch begins until its push
    ///   confirms, and for the whole recovery ladder.
    /// - Push confirmation matches on the <b>parameter set</b>, never on handles; handles and
    ///   declared data types are adopted from the push. No page→handle map is ever cached.
    /// - A PageSet to the already-displayed page yields no push, so "no push" after one PageSet
    ///   is ambiguous; the ladder's flip-away-and-back forces a genuine change to convert
    ///   silence into signal.
    /// - The <c>FF 05 02</c> gate is the real screen control. Exit = gate-off (screen dark —
    ///   no stale values, no burn-in). Resume = PageSet-while-off then gate-on, which elicits
    ///   the page's push deterministically; a bare gate-on lands on the legacy page with no
    ///   subscriptions and must never be sent.
    /// - The runtime session does not survive a power cycle or wheel re-seat even though the
    ///   gate <i>setting</i> persists — every cold entry re-runs the full bring-up, and a
    ///   wheel-change event (from the identity layer) is treated as a cold start.
    /// - Unexpected pushes are adopted in every state (wheel-button page changes, late pushes
    ///   from a boot-cold base): the firmware is the source of truth and is never fought.
    ///
    /// This class is a pure state machine: injected clock, no HID reads, no threads. It consumes
    /// parsed pushes (<see cref="OnPush"/>), telemetry-liveness edges (<see cref="Tick"/>), user
    /// page requests and enable state, and wheel-change events; it emits commands through
    /// <see cref="ItmEncoder"/>, each latched on transport accept and retried until accepted.
    /// The value/ParamDefs pipeline lives in the driver above, gated by <see cref="ValuesAllowed"/>
    /// and repainted on <see cref="SyncGeneration"/> changes.
    /// </summary>
    public class ItmLifecycleController
    {
        private readonly ItmEncoder _encoder;
        private readonly byte _deviceId;
        private readonly Func<long> _now;
        private readonly Action<string> _log;

        // ── Tunables (ms). Defaults are conservative multiples of the measured
        //    behavior; single-rig timing constants, compensated by the ladder and
        //    loud escalation logging until field data arrives. ─────────────────
        /// <summary>Deadline for the expected push after a page-changing command completes.
        /// Pushes follow a genuine change in 20–70 ms; 250 ms is a comfortable budget.</summary>
        public int PushDeadlineMs { get; set; } = 250;

        /// <summary>How long a push accumulates across reports before it is judged. One tested
        /// setup consolidates a push into 1–2 reports; another sends one entry per report over
        /// ~15 ms — the matcher must never treat a single report as the complete set.</summary>
        public int AccumulateWindowMs { get; set; } = 50;

        /// <summary>Grace window after an unsubscribe-only push in Synced: subscriptions arriving
        /// within it are a page change (host- or button-driven); nothing arriving means the
        /// firmware dropped our subscriptions → Recovery.</summary>
        public int UnsubGraceMs { get; set; } = 100;

        /// <summary>Quiet time between suspending values and sending a host PageSet. Suspension
        /// is load-bearing: switches with values streaming alongside fail a substantial fraction
        /// of the time, and mid-switch values can land on re-bound handles.</summary>
        public int SwitchQuietMs { get; set; } = 50;

        /// <summary>Minimum spacing between PageSet commands (firmware reconfiguration time).</summary>
        public int PageSetSpacingMs { get; set; } = 100;

        /// <summary>Backoff steps for <see cref="ItmLifecycleState.Unavailable"/> retries.</summary>
        public IReadOnlyList<int> UnavailableBackoffMs { get; set; } =
            new int[] { 5_000, 30_000, 300_000 };

        /// <summary>The page targeted by cold entries (and resume, when no better page is known).</summary>
        public byte DefaultPage { get; set; } = 1;

        // ── Observable state ─────────────────────────────────────────────
        public ItmLifecycleState State { get; private set; } = ItmLifecycleState.Idle;

        /// <summary>The wire page number the display is known to be on (0 = unknown). Adopted
        /// from pushes by matching the subscribed parameter set against the device's page
        /// catalog — never assumed from a sent PageSet.</summary>
        public byte CurrentPage { get; private set; }

        /// <summary>Increments every time a push is adopted (sync, re-sync, wheel-button page
        /// change). The driver repaints — immediate values, double-tap, then ParamDefs — on
        /// every change: after any resync the display shows stale firmware-cached values until
        /// the first fresh send.</summary>
        public int SyncGeneration { get; private set; }

        /// <summary>Values (and ParamDefs) may only be sent while this is true: Synced, and no
        /// push currently accumulating (an in-flight push means the page is changing under us).</summary>
        public bool ValuesAllowed => State == ItmLifecycleState.Synced && _accumCloseAt == 0;

        /// <summary>The adopted handle map: host handle → subscription (param + declared type),
        /// sorted by handle. Built exclusively from firmware pushes.</summary>
        public IEnumerable<KeyValuePair<byte, ItmSubscription>> Subscriptions => _subs;

        /// <summary>Number of parameters the firmware currently has subscribed.</summary>
        public int SubscriptionCount => _subs.Count;

        // ── Internal state ───────────────────────────────────────────────
        private enum Cmd { GateOn, GateOff, Enable, PageSet }

        private struct Step
        {
            public Cmd Cmd;
            public byte Page;   // PageSet only
        }

        // The in-flight command procedure: each step is sent in order and latched only when
        // the transport accepts it (a declined write is retried next tick). PageSet steps
        // additionally respect PageSetSpacingMs.
        private readonly List<Step> _steps = new List<Step>();
        private int _stepIdx;
        private bool _armOnDrain;    // arm the push expectation when the queue drains
        private byte _armPage;       // the page whose push the armed expectation matches
        private bool _pushDuringProcedure;   // a push arrived while steps were still being sent

        private long _lastPageSetMs = -1_000_000_000;   // last accepted PageSet (spacing floor)

        // Push expectation (armed once the triggering command sequence is fully accepted).
        private byte _armedPage;     // page whose param set is expected (0 = not armed)
        private long _deadlineAt;    // 0 = no deadline

        // Push accumulation across fragmented reports.
        private long _accumCloseAt;  // 0 = no accumulation open

        // Unsubscribe-grace (Synced only).
        private long _graceUntil;    // 0 = none

        // Adopted handle map (from pushes only — cold entries clear it; there is no seeding).
        private readonly SortedDictionary<byte, ItmSubscription> _subs =
            new SortedDictionary<byte, ItmSubscription>();

        // Switching.
        private byte _targetPage;      // the page a procedure is driving toward
        private long _quietUntil;      // Switching: when the quiet window ends (0 = sent already)
        private byte _pendingRequest;  // host request queued behind an in-flight procedure

        // Recovery ladder.
        private enum Rung { None, PageSet1, PageSet2, FlipAway, FlipBack, GateCycle }
        private Rung _rung = Rung.None;
        private byte _flipPage;

        // Unavailable backoff.
        private int _backoffIdx;
        private long _retryAt;

        // Telemetry-liveness edge tracking + resume page.
        private bool _wasLive;
        private byte _resumePage;

        private bool _userEnabled = true;

        public ItmLifecycleController(ItmEncoder encoder, byte deviceId = ItmEncoder.DefaultDeviceId,
            Func<long> nowMs = null, Action<string> log = null)
        {
            _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
            _deviceId = deviceId;
            _now = nowMs ?? DefaultClock();
            _log = log ?? (_ => { });
        }

        private static Func<long> DefaultClock()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            return () => sw.ElapsedMilliseconds;
        }

        /// <summary>A short human-readable state line for logs and the Device Status panel.</summary>
        public string Describe()
        {
            switch (State)
            {
                case ItmLifecycleState.Synced:
                    return "Synced — page " + (CurrentPage == 0 ? "?" : CurrentPage.ToString())
                        + ", " + _subs.Count + " params";
                case ItmLifecycleState.Recovery:
                    return "Recovering (" + RungName(_rung) + ", target page " + _targetPage + ")";
                case ItmLifecycleState.Unavailable:
                    long wait = _retryAt - _now();
                    return "Unavailable — retry in " + Math.Max(0, (wait + 999) / 1000) + " s";
                case ItmLifecycleState.AwaitPush:
                    return "Waiting for page " + _targetPage + " confirmation";
                case ItmLifecycleState.Switching:
                    return "Switching to page " + _targetPage;
                default:
                    return State.ToString();
            }
        }

        /// <summary>The adopted handle map as a log-friendly line, e.g. <c>h0=p1:t34 h1=p4:t12</c>.</summary>
        public string DescribeMap()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _subs)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append('h').Append(kv.Key).Append("=p").Append(kv.Value.ParamId);
                if (kv.Value.DataType != 0)
                    sb.Append(":t").Append(kv.Value.DataType.ToString("X2"));
            }
            return sb.Length == 0 ? "(no subscriptions)" : sb.ToString();
        }

        private static string RungName(Rung r)
        {
            switch (r)
            {
                case Rung.PageSet1: return "re-PageSet 1/2";
                case Rung.PageSet2: return "re-PageSet 2/2";
                case Rung.FlipAway: return "flip-away";
                case Rung.FlipBack: return "flip-back";
                case Rung.GateCycle: return "gate cycle";
                default: return r.ToString();
            }
        }

        // ── Inputs ───────────────────────────────────────────────────────

        /// <summary>
        /// Begins the lifecycle with a full cold bring-up. Idempotent — a no-op unless Idle,
        /// so it is safe to call every frame while the device is connected.
        /// </summary>
        public void Start()
        {
            if (State != ItmLifecycleState.Idle || !_userEnabled)
                return;
            ColdEntry("start");
        }

        /// <summary>
        /// Stops the lifecycle (connection lost): back to Idle, all session state dropped,
        /// nothing sent. A later <see cref="Start"/> re-runs the cold bring-up.
        /// </summary>
        public void Stop()
        {
            State = ItmLifecycleState.Idle;
            ClearProcedure();
            ClearExpectation();
            _subs.Clear();
            CurrentPage = 0;
            _pendingRequest = 0;
            _rung = Rung.None;
            _backoffIdx = 0;
            _wasLive = false;
        }

        /// <summary>
        /// Applies the user's ITM on/off setting (safe to call every frame; edges are detected
        /// internally). Off sends a single gate-off — the same persistent state the vendor
        /// software's ITM switch sets — and goes dormant. On re-arms the cold bring-up.
        /// </summary>
        public void SetUserEnabled(bool enabled)
        {
            if (enabled == _userEnabled)
                return;
            _userEnabled = enabled;

            if (!enabled)
            {
                // Enforce "off" from any state, including Idle (user may have ITM disabled
                // from the start — the display must still be gated off once).
                ClearProcedure();
                ClearExpectation();
                _rung = Rung.None;
                State = ItmLifecycleState.Disabled;
                QueueGateOff();
                _log("ITM: disabled by user — gating off (setting persists until re-enabled here or in vendor software)");
            }
            else if (State == ItmLifecycleState.Disabled)
            {
                // Back to Idle; the next Start() runs the cold entry.
                State = ItmLifecycleState.Idle;
                ClearProcedure();
            }
        }

        /// <summary>
        /// Host page request (e.g. the default-page setting changed). In Synced this starts the
        /// switch procedure; while another procedure is in flight it is queued and applied after
        /// the next sync — unless a wheel-button change supersedes it (adopt, never fight).
        /// </summary>
        public void RequestPage(byte page)
        {
            if (page == 0)
                return;
            switch (State)
            {
                case ItmLifecycleState.Synced:
                    if (page == CurrentPage)
                        return;   // same-page PageSet pushes nothing — never ask for one
                    BeginSwitch(page);
                    break;
                case ItmLifecycleState.TelemetryIdle:
                    _resumePage = page;   // used by the resume PageSet-while-off
                    break;
                case ItmLifecycleState.BringUp:
                case ItmLifecycleState.AwaitPush:
                case ItmLifecycleState.Switching:
                case ItmLifecycleState.Recovery:
                case ItmLifecycleState.Unavailable:
                    _pendingRequest = page;
                    break;
                    // Idle/Disabled: cold entries read DefaultPage directly.
            }
        }

        /// <summary>
        /// A wheel/hub/module change reported by the identity layer (FF 08). A hot-swap resets
        /// the display to a cold state that is invisible on the ITM channel itself, so this is
        /// treated as a full cold start: the handle map is dropped and bring-up re-runs.
        /// </summary>
        public void OnWheelChanged()
        {
            switch (State)
            {
                case ItmLifecycleState.Idle:
                    return;
                case ItmLifecycleState.Disabled:
                    // The new wheel comes up with whatever gate setting persisted — re-assert off.
                    QueueGateOff();
                    return;
                default:
                    ColdEntry("wheel changed");
                    return;
            }
        }

        /// <summary>
        /// Applies one parsed firmware push report (col03-IN <c>FF 05 01</c> entries for this
        /// display). Entries are adopted into the handle map immediately, in every state; the
        /// report opens (or joins) an accumulation window that is judged as a whole, because a
        /// single page change can arrive fragmented across several reports.
        /// </summary>
        public void OnPush(IReadOnlyList<ItmSubscription> entries)
        {
            if (entries == null || entries.Count == 0)
                return;

            for (int i = 0; i < entries.Count; i++)
            {
                var s = entries[i];
                if (s.IsUnsubscribe)
                    _subs.Remove(s.Handle);
                else
                    _subs[s.Handle] = s;
            }

            if (_stepIdx < _steps.Count || _armOnDrain)
                _pushDuringProcedure = true;

            if (_accumCloseAt == 0)
                _accumCloseAt = _now() + AccumulateWindowMs;
        }

        /// <summary>
        /// Drives the machine one tick. Call once per frame while the device is connected,
        /// with whether game telemetry is currently live.
        /// </summary>
        public void Tick(bool telemetryLive)
        {
            long now = _now();

            // Telemetry live→dead edge: park in TelemetryIdle (gate-off, screen dark) from any
            // active state — a switch or recovery interrupted by a game exit is abandoned; the
            // resume shape re-establishes everything and is the strongest recovery there is.
            if (_wasLive && !telemetryLive && IsActiveState(State))
                EnterTelemetryIdle("game exited");
            _wasLive = telemetryLive;

            // Judge a completed push accumulation.
            if (_accumCloseAt != 0 && now >= _accumCloseAt)
            {
                _accumCloseAt = 0;
                JudgeAccumulation(now);
            }

            // Unsub-grace expiry: subscriptions were dropped and nothing followed.
            if (_graceUntil != 0 && now >= _graceUntil && _accumCloseAt == 0)
            {
                _graceUntil = 0;
                if (State == ItmLifecycleState.Synced)
                {
                    byte target = KnownTelemetryPageOrDefault(CurrentPage);
                    _log("ITM: subscriptions dropped (unsubscribe with nothing following) — recovering page " + target);
                    EnterRecovery(target, Rung.PageSet1);
                }
            }

            // TelemetryIdle → resume on telemetry arrival: PageSet-while-off, then gate-on.
            // (Never a bare gate-on — that lands on the legacy page with no subscriptions.)
            if (State == ItmLifecycleState.TelemetryIdle && telemetryLive)
            {
                byte page = KnownTelemetryPageOrDefault(_resumePage);
                _targetPage = page;
                State = ItmLifecycleState.AwaitPush;
                QueueStep(Cmd.PageSet, page);
                QueueStep(Cmd.GateOn);
                ArmOnDrain(page);
                _log("ITM: telemetry resumed — PageSet(" + page + ") while off, then gate-on");
            }

            // Switching: after the quiet window, send the PageSet.
            if (State == ItmLifecycleState.Switching && _quietUntil != 0 && now >= _quietUntil)
            {
                _quietUntil = 0;
                QueueStep(Cmd.PageSet, _targetPage);
                ArmOnDrain(_targetPage);
            }

            // Unavailable: backoff expiry retries the strongest rung (gate cycle with
            // PageSet-while-off — the most reliable recovery observed).
            if (State == ItmLifecycleState.Unavailable && now >= _retryAt)
            {
                _log("ITM: retrying after backoff — gate cycle for page " + _targetPage);
                EnterRecovery(_targetPage, Rung.GateCycle);
            }

            PumpSteps(now);

            // Expected-push deadline. Deferred while an accumulation is open — a push is
            // mid-flight and the judgment will resolve it.
            if (_deadlineAt != 0 && now >= _deadlineAt && _accumCloseAt == 0 && _stepIdx >= _steps.Count)
            {
                ClearExpectation();
                OnPushMissed();
            }
        }

        // ── Procedures ───────────────────────────────────────────────────

        // Cold entry (start, restart, wheel change, power cycle): gate + enable + PageSet, each
        // latched on transport accept; confirmation only ever comes from the push. The enable is
        // kept for official-software parity — it has never been observed to do anything, and no
        // state transition depends on it. The handle map is dropped: host state must never be
        // used to infer firmware state.
        private void ColdEntry(string why)
        {
            ClearProcedure();
            ClearExpectation();
            _subs.Clear();
            CurrentPage = 0;
            _rung = Rung.None;
            _graceUntil = 0;

            _targetPage = DefaultPage;
            State = ItmLifecycleState.BringUp;
            QueueStep(Cmd.GateOn);
            QueueStep(Cmd.Enable);
            QueueStep(Cmd.PageSet, DefaultPage);
            ArmOnDrain(DefaultPage);
            _log("ITM: bring-up (" + why + ") — gate + enable + PageSet(" + DefaultPage + ")");
        }

        private void BeginSwitch(byte page)
        {
            _targetPage = page;
            State = ItmLifecycleState.Switching;
            ClearExpectation();
            _quietUntil = _now() + SwitchQuietMs;   // values are suspended from this instant
        }

        private void EnterTelemetryIdle(string why)
        {
            ClearProcedure();
            ClearExpectation();
            _graceUntil = 0;
            _rung = Rung.None;
            _resumePage = KnownTelemetryPageOrDefault(CurrentPage);
            State = ItmLifecycleState.TelemetryIdle;
            QueueGateOff();
            _log("ITM: " + why + " — gating display off (dark; resume restores page " + _resumePage + ")");
        }

        private void EnterRecovery(byte target, Rung rung)
        {
            _targetPage = target;
            State = ItmLifecycleState.Recovery;
            _rung = rung;
            StartRung();
        }

        // The ladder, quiet throughout (the driver sends nothing outside Synced): re-PageSet ×2 →
        // flip-away-and-back (a genuine change MUST push, converting silence into a hard signal) →
        // gate cycle with PageSet-while-off (the most reliable recovery observed; also the only
        // move that un-wedges a display that stopped responding to PageSets) → Unavailable.
        // Every rung logs what it tried — the ladder doubles as field diagnostics.
        private void StartRung()
        {
            ClearProcedure();
            ClearExpectation();
            switch (_rung)
            {
                case Rung.PageSet1:
                case Rung.PageSet2:
                    _log("ITM recovery: " + RungName(_rung) + " — PageSet(" + _targetPage + ")");
                    QueueStep(Cmd.PageSet, _targetPage);
                    ArmOnDrain(_targetPage);
                    break;
                case Rung.FlipAway:
                    _flipPage = PickFlipPage(_targetPage);
                    _log("ITM recovery: flip-away-and-back — PageSet(" + _flipPage + ") then back to " + _targetPage);
                    QueueStep(Cmd.PageSet, _flipPage);
                    ArmOnDrain(_flipPage);
                    break;
                case Rung.FlipBack:
                    QueueStep(Cmd.PageSet, _targetPage);
                    ArmOnDrain(_targetPage);
                    break;
                case Rung.GateCycle:
                    _log("ITM recovery: gate cycle — gate-off, PageSet(" + _targetPage + ") while off, gate-on (display blanks briefly)");
                    QueueStep(Cmd.GateOff);
                    QueueStep(Cmd.PageSet, _targetPage);
                    QueueStep(Cmd.GateOn);
                    ArmOnDrain(_targetPage);
                    break;
            }
        }

        private void OnPushMissed()
        {
            switch (State)
            {
                case ItmLifecycleState.AwaitPush:
                case ItmLifecycleState.Switching:
                    _log("ITM: no push within " + PushDeadlineMs + " ms for page " + _targetPage +
                         " — entering recovery (a PageSet to the already-shown page correctly pushes nothing;" +
                         " the ladder disambiguates)");
                    EnterRecovery(_targetPage, Rung.PageSet1);
                    break;
                case ItmLifecycleState.Recovery:
                    AdvanceRung();
                    break;
            }
        }

        private void AdvanceRung()
        {
            switch (_rung)
            {
                case Rung.PageSet1:
                    _rung = Rung.PageSet2; StartRung(); break;
                case Rung.PageSet2:
                    _rung = Rung.FlipAway; StartRung(); break;
                case Rung.FlipAway:   // the flip page didn't push — a hard failure signal
                case Rung.FlipBack:
                    _rung = Rung.GateCycle; StartRung(); break;
                case Rung.GateCycle:
                    EnterUnavailable(); break;
            }
        }

        private void EnterUnavailable()
        {
            State = ItmLifecycleState.Unavailable;
            _rung = Rung.None;
            ClearProcedure();
            ClearExpectation();
            int backoff = UnavailableBackoffMs[Math.Min(_backoffIdx, UnavailableBackoffMs.Count - 1)];
            if (_backoffIdx < UnavailableBackoffMs.Count - 1)
                _backoffIdx++;
            _retryAt = _now() + backoff;
            _log("ITM: recovery ladder exhausted for page " + _targetPage + " — display unavailable, retrying in "
                 + (backoff / 1000) + " s. If this persists, another program may be driving the display.");
        }

        // ── Push judgment ────────────────────────────────────────────────

        // Judges the state of the handle map after an accumulation window closes. Matching is
        // on the parameter SET (handles are setup-specific and can re-bind); the map itself was
        // already adopted entry-by-entry as reports arrived.
        private void JudgeAccumulation(long now)
        {
            var set = SubscribedParamSet();

            if (set.Count == 0)
            {
                // Unsubscribe-only. In Synced this may be the front half of a page change
                // (host- or button-driven) or our subscriptions being dropped — grace decides.
                // After our own gate-off (TelemetryIdle/Disabled) it is the expected echo.
                if (State == ItmLifecycleState.Synced)
                    _graceUntil = now + UnsubGraceMs;
                return;
            }
            _graceUntil = 0;

            byte matchedPage = PageForParamSet(set);

            // A procedure's commands are still being sent (e.g. resume's gate-on not yet
            // accepted): adopt the data but defer transitions until the procedure completes —
            // going Synced with a gate-on unsent would strand a dark display.
            if (_stepIdx < _steps.Count)
            {
                CurrentPage = matchedPage;
                return;
            }

            // The armed expectation (or the in-flight target, for a push that lands before the
            // deadline is even armed) confirms the procedure.
            if (_armedPage != 0 && SetEqualsPage(set, _armedPage))
            {
                byte page = _armedPage;
                ClearExpectation();
                if (State == ItmLifecycleState.Recovery && _rung == Rung.FlipAway && page == _flipPage)
                {
                    // Flip page confirmed — now flip back to the target.
                    _rung = Rung.FlipBack;
                    StartRung();
                    return;
                }
                ConfirmSync(page, "push confirmed");
                return;
            }
            if (ExpectsTargetPush() && SetEqualsPage(set, _targetPage))
            {
                ClearExpectation();
                ConfirmSync(_targetPage, "push confirmed (early)");
                return;
            }

            // Unexpected push: adopt, never fight.
            switch (State)
            {
                case ItmLifecycleState.Synced:
                    CurrentPage = matchedPage;
                    SyncGeneration++;
                    _log("ITM: page changed at the wheel — now page " +
                         (matchedPage == 0 ? "?" : matchedPage.ToString()) + ": " + DescribeMap());
                    break;
                case ItmLifecycleState.AwaitPush:
                case ItmLifecycleState.Switching:
                case ItmLifecycleState.Recovery:
                case ItmLifecycleState.Unavailable:
                    // A COMPLETE different page than asked for (wheel button, late push from a
                    // boot-cold base): the display is demonstrably alive — sync to reality. Any
                    // queued host request is dropped rather than fought. A set matching no page
                    // is most likely a fragment of a change still in flight (the rest lands in
                    // a later accumulation) — resuming values on it would reopen the re-bound-
                    // handle hazard, so keep waiting and let the deadline/ladder decide.
                    if (matchedPage == 0)
                        break;
                    _pendingRequest = 0;
                    ClearExpectation();
                    _rung = Rung.None;
                    ConfirmSync(matchedPage, "adopted unexpected push");
                    break;
                default:
                    // Idle / Disabled / TelemetryIdle / BringUp: keep the adopted map, no
                    // transition. In gated-off states a push means someone else gated on —
                    // note it, don't fight it.
                    CurrentPage = matchedPage;
                    if (State == ItmLifecycleState.TelemetryIdle || State == ItmLifecycleState.Disabled)
                        _log("ITM: subscription push received while gated off — another program may be driving the display");
                    break;
            }
        }

        private bool ExpectsTargetPush()
        {
            return State == ItmLifecycleState.AwaitPush
                || State == ItmLifecycleState.Switching
                || State == ItmLifecycleState.Recovery;
        }

        private void ConfirmSync(byte page, string why)
        {
            bool recovered = State == ItmLifecycleState.Recovery || State == ItmLifecycleState.Unavailable;
            State = ItmLifecycleState.Synced;
            CurrentPage = page;
            _rung = Rung.None;
            _backoffIdx = 0;
            SyncGeneration++;
            _log("ITM: " + why + " — page " + (page == 0 ? "?" : page.ToString())
                 + (recovered ? " (recovered)" : "") + ": " + DescribeMap());

            if (_pendingRequest != 0)
            {
                byte p = _pendingRequest;
                _pendingRequest = 0;
                if (p != page)
                    BeginSwitch(p);
            }
        }

        // ── Command pump ─────────────────────────────────────────────────

        private void QueueStep(Cmd cmd, byte page = 0)
        {
            _steps.Add(new Step { Cmd = cmd, Page = page });
        }

        private void QueueGateOff()
        {
            ClearProcedure();
            QueueStep(Cmd.GateOff);
        }

        private void ArmOnDrain(byte page)
        {
            _armOnDrain = true;
            _armPage = page;
        }

        private void PumpSteps(long now)
        {
            while (_stepIdx < _steps.Count)
            {
                var step = _steps[_stepIdx];
                bool ok;
                switch (step.Cmd)
                {
                    case Cmd.GateOn:
                        ok = _encoder.SetItmMode(true);
                        break;
                    case Cmd.GateOff:
                        ok = _encoder.SetItmMode(false);
                        break;
                    case Cmd.Enable:
                        ok = _encoder.EnableItm();
                        break;
                    default:   // PageSet
                        if (now - _lastPageSetMs < PageSetSpacingMs)
                            return;   // spacing floor — resume next tick
                        ok = _encoder.SetPage(_deviceId, step.Page);
                        if (ok)
                            _lastPageSetMs = now;
                        break;
                }
                if (!ok)
                    return;   // declined — retry this step next tick
                _stepIdx++;
            }

            if (_steps.Count > 0)
            {
                _steps.Clear();
                _stepIdx = 0;
            }

            if (_armOnDrain)
            {
                _armOnDrain = false;
                bool pushArrived = _pushDuringProcedure;
                _pushDuringProcedure = false;
                _armedPage = _armPage;
                _deadlineAt = now + PushDeadlineMs;
                if (State == ItmLifecycleState.BringUp)
                    State = ItmLifecycleState.AwaitPush;

                // A push may have landed while the commands were still being accepted
                // (adopted with the transition deferred) — judge it now. Guarded on a push
                // actually having arrived during the procedure: the map alone can look
                // "already right" from stale pre-procedure state (e.g. a resume to the same
                // page the display was on before the exit), and that must never self-confirm.
                if (pushArrived && _accumCloseAt == 0 && _subs.Count > 0
                    && SetEqualsPage(SubscribedParamSet(), _armedPage))
                {
                    byte page = _armedPage;
                    ClearExpectation();
                    if (State == ItmLifecycleState.Recovery && _rung == Rung.FlipAway && page == _flipPage)
                    {
                        _rung = Rung.FlipBack;
                        StartRung();
                    }
                    else
                    {
                        ConfirmSync(page, "push confirmed (arrived during send)");
                    }
                }
            }
        }

        private void ClearProcedure()
        {
            _steps.Clear();
            _stepIdx = 0;
            _armOnDrain = false;
            _pushDuringProcedure = false;
            _quietUntil = 0;
        }

        private void ClearExpectation()
        {
            _armedPage = 0;
            _deadlineAt = 0;
        }

        // ── Page catalog helpers ─────────────────────────────────────────

        private readonly HashSet<ushort> _setScratch = new HashSet<ushort>();

        private HashSet<ushort> SubscribedParamSet()
        {
            _setScratch.Clear();
            foreach (var kv in _subs)
                _setScratch.Add(kv.Value.ParamId);
            return _setScratch;
        }

        // The wire page whose parameter set exactly matches, or 0 if none does. Set matching is
        // the only reliable identification: handles are allocation-strategy-specific, and the
        // page number itself is never echoed anywhere in a push.
        private byte PageForParamSet(HashSet<ushort> set)
        {
            foreach (var p in ItmDeviceCatalog.PagesFor(_deviceId))
            {
                if (p.Params.Count != set.Count || p.Params.Count == 0)
                    continue;
                bool all = true;
                for (int i = 0; i < p.Params.Count; i++)
                {
                    if (!set.Contains(p.Params[i]))
                    {
                        all = false;
                        break;
                    }
                }
                if (all)
                    return p.Number;
            }
            return 0;
        }

        private bool SetEqualsPage(HashSet<ushort> set, byte page)
        {
            IReadOnlyList<ushort> expected = null;
            foreach (var p in ItmDeviceCatalog.PagesFor(_deviceId))
            {
                if (p.Number == page)
                {
                    expected = p.Params;
                    break;
                }
            }
            // A page we have no catalog entry for (or the legacy page) can't be matched
            // exactly; accept any non-empty adopted set as that procedure's outcome.
            if (expected == null || expected.Count == 0)
                return set.Count > 0;

            if (expected.Count != set.Count)
                return false;
            for (int i = 0; i < expected.Count; i++)
                if (!set.Contains(expected[i]))
                    return false;
            return true;
        }

        // A telemetry (non-legacy) page suitable as a target: the candidate itself if it is
        // one, else the default page, else the device's first telemetry page.
        private byte KnownTelemetryPageOrDefault(byte candidate)
        {
            if (IsTelemetryPage(candidate))
                return candidate;
            if (IsTelemetryPage(DefaultPage))
                return DefaultPage;
            foreach (var p in ItmDeviceCatalog.PagesFor(_deviceId))
                if (p.Params.Count > 0)
                    return p.Number;
            return 1;
        }

        private bool IsTelemetryPage(byte page)
        {
            if (page == 0)
                return false;
            foreach (var p in ItmDeviceCatalog.PagesFor(_deviceId))
                if (p.Number == page)
                    return p.Params.Count > 0;
            return false;
        }

        // A different telemetry page to flip to — a genuine page change must push, so the flip
        // converts "no push" from ambiguous into a hard failure signal.
        private byte PickFlipPage(byte target)
        {
            foreach (var p in ItmDeviceCatalog.PagesFor(_deviceId))
                if (p.Params.Count > 0 && p.Number != target)
                    return p.Number;
            return target == 1 ? (byte)2 : (byte)1;
        }

        private static bool IsActiveState(ItmLifecycleState s)
        {
            return s == ItmLifecycleState.BringUp
                || s == ItmLifecycleState.AwaitPush
                || s == ItmLifecycleState.Switching
                || s == ItmLifecycleState.Synced
                || s == ItmLifecycleState.Recovery
                || s == ItmLifecycleState.Unavailable;
        }
    }
}
