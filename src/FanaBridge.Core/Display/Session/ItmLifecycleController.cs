using System;
using System.Collections.Generic;

using FanaBridge.Protocol;

namespace FanaBridge.Display.Session
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
    /// - The <c>FF 05 02</c> gate is the real screen control, but it is <b>never used at idle</b>
    ///   — a game exit clears the fields to placeholders (DisplayReset) and keeps the ITM page
    ///   visible, so the display never drops to the legacy 7-segment view on its own. Cold
    ///   entries also DisplayReset so the page comes up showing placeholders, never a previous
    ///   session's cached values. Gate-off is reserved for the user's explicit ITM-off and for
    ///   the recovery ladder's last-resort gate-cycle rung (its brief drop to legacy is the
    ///   escape hatch that re-establishes a wedged display).
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

        /// <summary>The page the display starts on: targeted by cold entries (connect / wheel
        /// change / power cycle) and re-established at each game start. The wheel button
        /// navigates from there within a session. Read live.</summary>
        public byte DefaultPage { get; set; } = 1;

        /// <summary>
        /// Whether a game starting re-establishes <see cref="DefaultPage"/> (the built-in
        /// behavior, on by default). The display-rules runtime turns this off while it owns
        /// page policy: its engine performs the same revert through <see cref="RequestPage"/>
        /// (resting target → base page on the in-game rising edge), and a switch the
        /// controller initiates on its own is indistinguishable from wheel-button navigation
        /// to the layers above — they would dismiss their rules over a page change no one
        /// made. With the revert suppressed, a game start still repaints the current page
        /// in place (the exit cleared the fields to placeholders). Read live.
        /// </summary>
        public bool GameStartPageRevert { get; set; } = true;

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

        /// <summary>Values (and ParamDefs) may only be sent while this is true: Synced, with no
        /// push currently accumulating and no grace window open — either means the page may be
        /// changing under us, and mid-change values can land on re-bound handles.</summary>
        public bool ValuesAllowed => State == ItmLifecycleState.Synced
            && _accumCloseAt == 0 && _graceUntil == 0;

        // Lazily-rebuilt snapshot of the handle map, so per-frame consumers don't pay a
        // SortedDictionary enumerator allocation ~85 times a second for a map that only
        // changes when a push arrives.
        private KeyValuePair<byte, ItmSubscription>[] _subsSnapshot;

        /// <summary>The adopted handle map: host handle → subscription (param + declared type),
        /// ordered by handle. Built exclusively from firmware pushes.</summary>
        public IReadOnlyList<KeyValuePair<byte, ItmSubscription>> Subscriptions
        {
            get
            {
                if (_subsSnapshot == null)
                {
                    _subsSnapshot = new KeyValuePair<byte, ItmSubscription>[_subs.Count];
                    int i = 0;
                    foreach (var kv in _subs)
                        _subsSnapshot[i++] = kv;
                }
                return _subsSnapshot;
            }
        }

        /// <summary>Number of parameters the firmware currently has subscribed.</summary>
        public int SubscriptionCount => _subs.Count;

        // ── Internal state ───────────────────────────────────────────────
        private enum Cmd { GateOn, GateOff, Enable, PageSet, Reset }

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
        private bool _judgeAtDrain;  // an accumulation closed mid-procedure — judge it once the queue drains
        private bool _startPending;  // Start() was called; the cold entry runs on the next Tick

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

        // Telemetry-liveness edge tracking (game exit clears fields; game return repaints).
        private bool _wasLive;

        // The user's on/off setting, and whether it has been enforced since the last
        // Stop(). Un-applied after every Stop so a reconnect re-asserts "off" — the
        // re-appearing device (power cycle, re-seat) comes up with whatever gate setting
        // persisted, not necessarily the user's.
        private bool _userEnabled = true;
        private bool _enabledApplied;

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
        /// Begins the lifecycle with a full cold bring-up on the next <see cref="Tick"/> —
        /// deferred one tick so the caller's per-frame settings sync (notably
        /// <see cref="DefaultPage"/>) lands before the bring-up target is chosen. Idempotent —
        /// a no-op unless Idle, so it is safe to call every frame while connected.
        /// </summary>
        public void Start()
        {
            if (State != ItmLifecycleState.Idle || _startPending || !_userEnabled)
                return;
            _startPending = true;
        }

        /// <summary>
        /// Stops the lifecycle (connection lost): back to Idle, all session state dropped,
        /// nothing sent. A later <see cref="Start"/> re-runs the cold bring-up, and the
        /// user's on/off setting is re-enforced on the next <see cref="SetUserEnabled"/>.
        /// </summary>
        public void Stop()
        {
            State = ItmLifecycleState.Idle;
            AbandonInFlight();
            _subs.Clear();
            _subsSnapshot = null;
            CurrentPage = 0;
            _pendingRequest = 0;
            _backoffIdx = 0;
            _wasLive = false;
            _startPending = false;
            _enabledApplied = false;
        }

        /// <summary>
        /// Applies the user's ITM on/off setting (safe to call every frame; edges are detected
        /// internally, and the setting is re-enforced once after every <see cref="Stop"/>).
        /// Off sends a single gate-off — the same persistent state the vendor software's ITM
        /// switch sets — and goes dormant. On re-arms the cold bring-up.
        /// </summary>
        public void SetUserEnabled(bool enabled)
        {
            if (_enabledApplied && enabled == _userEnabled)
                return;
            _userEnabled = enabled;
            _enabledApplied = true;

            if (!enabled)
            {
                // Enforce "off" from any state, including Idle (user may have ITM disabled
                // from the start, or the device may have just reconnected with the gate
                // setting persisted on — the display must be gated off either way).
                AbandonInFlight();
                _startPending = false;
                State = ItmLifecycleState.Disabled;
                QueueGateOff();
                _log("ITM: disabled by user — gating off (setting persists until re-enabled here or in vendor software)");
            }
            else if (State == ItmLifecycleState.Disabled)
            {
                // Re-enable: arm a cold bring-up (runs on this same Tick). The driver's
                // Update applies the setting just before Tick, so re-enabling in settings
                // brings the display up in the same frame, as the old driver did.
                State = ItmLifecycleState.Idle;
                AbandonInFlight();
                _startPending = true;
            }
        }

        /// <summary>
        /// Host page request (e.g. the default-page setting changed). In Synced this starts the
        /// switch procedure — which works whether or not a game is running (the page previews on
        /// the wheel either way). While another procedure is in flight it is queued and applied
        /// after the next sync, unless a wheel-button change supersedes it (adopt, never fight).
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
            _subsSnapshot = null;

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

            // A requested start runs here rather than in Start() itself, so the caller's
            // per-frame settings sync (DefaultPage in particular) has landed before the
            // bring-up target is chosen.
            if (_startPending)
            {
                _startPending = false;
                ColdEntry("start");
            }

            // Telemetry live→dead edge (game exit): clear the fields to placeholders so no
            // stale numbers linger. The ITM page stays visible — no gate-off; dropping to
            // legacy is reserved for the recovery escape hatch, never idle. Only from Synced
            // (there's a page to clear); a mid-transition exit just lets its procedure resolve.
            bool becameLive = telemetryLive && !_wasLive;
            if (_wasLive && !telemetryLive && State == ItmLifecycleState.Synced)
                QueueStep(Cmd.Reset);
            // Game start (telemetry returns) from a settled Synced display: re-establish the
            // default page. The DefaultPage setting is a real default — each game launch starts
            // on it — while the wheel button owns navigation within a session. If the display is
            // already on the default (the common case), just repaint over the placeholders.
            if (becameLive && State == ItmLifecycleState.Synced)
            {
                byte def = EffectiveDefaultPage();
                if (CurrentPage != def && GameStartPageRevert)
                    BeginSwitch(def);
                else
                    SyncGeneration++;
            }
            _wasLive = telemetryLive;

            // Judge a completed push accumulation — unless a procedure's commands are still
            // being sent, in which case the judgment is deferred to the queue drain so that
            // every push goes through the one judgment path with the expectation armed.
            if (_accumCloseAt != 0 && now >= _accumCloseAt)
            {
                _accumCloseAt = 0;
                if (_stepIdx < _steps.Count || _armOnDrain)
                    _judgeAtDrain = true;
                else
                    JudgeAccumulation(now);
            }

            // Grace expiry. Grace opens in Synced when a push left the map in a state that
            // doesn't form a page: empty (unsubscribe-all with nothing following) or a partial
            // set (a change fragmented slower than the accumulation window). Anything arriving
            // meanwhile re-judges; expiry means nothing followed.
            if (_graceUntil != 0 && now >= _graceUntil && _accumCloseAt == 0)
            {
                _graceUntil = 0;
                if (State == ItmLifecycleState.Synced)
                {
                    var set = SubscribedParamSet();
                    if (set.Count == 0)
                    {
                        // An unsubscribe-all with nothing following means the display moved to
                        // the legacy ITM page (no telemetry parameters) — the wheel button
                        // reaches it, and it's a valid destination. Adopt it; never fight it
                        // back to a telemetry page (that was the "can't select legacy" bug).
                        CurrentPage = LegacyPageNumber();
                        SyncGeneration++;
                        _log("ITM: on the legacy ITM page (no telemetry parameters)");
                    }
                    else
                    {
                        // A stable set that matches no catalog page — the firmware knows
                        // pages we don't. Adopt it; values flow for the encodable params.
                        CurrentPage = PageForParamSet(set);
                        SyncGeneration++;
                        _log("ITM: adopted uncataloged page set: " + DescribeMap());
                    }
                }
            }

            // Switching: after the quiet window, send the PageSet — but only once no push is
            // accumulating. A push arriving during the quiet window is evidence the display is
            // already changing pages (a wheel-button change or late firmware activity); sending
            // a PageSet over it would reintroduce the concurrent-traffic hazard. Wait for the
            // accumulation to be judged first — it may adopt that page and drop the host request
            // (the judgment above cleared _accumCloseAt, so this fires the same tick it resolves).
            if (State == ItmLifecycleState.Switching && _quietUntil != 0 && now >= _quietUntil
                && _accumCloseAt == 0)
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

        /// <summary>
        /// Optional cold-entry page override. When non-null and returns a device-valid
        /// wire page, <see cref="ColdEntry"/> targets that page instead of
        /// <see cref="EffectiveDefaultPage"/>. The runtime derives this from the live
        /// document at command time (playlist entry step); non-playlist documents
        /// return null so the H5 cold burst stays byte-identical to EffectiveDefaultPage.
        /// </summary>
        public Func<byte?> ColdEntryPageProvider { get; set; }

        // Cold entry (start, restart, wheel change, power cycle): gate + enable + PageSet, each
        // latched on transport accept; confirmation only ever comes from the push. The enable is
        // kept for official-software parity — it has never been observed to do anything, and no
        // state transition depends on it. The handle map is dropped: host state must never be
        // used to infer firmware state.
        //
        // PageSet spacing is also cleared: a full cold restart must emit PageSet inside the
        // bring-up burst (before any face paint on the next frame path). Prior-session
        // spacing would otherwise push the cold PageSet two frames later — the H5 residual
        // under hosted-only v2 (recent park-on-Legacy SetPage + reconnect AdvanceMs < spacing).
        // Mid-session recovery rungs still honor PageSetSpacingMs (not a cold entry).
        private void ColdEntry(string why)
        {
            AbandonInFlight();
            _subs.Clear();
            _subsSnapshot = null;
            CurrentPage = 0;
            _pendingRequest = 0;
            _lastPageSetMs = -1_000_000_000;

            byte page = ResolveColdEntryPage();
            _targetPage = page;
            State = ItmLifecycleState.BringUp;
            QueueStep(Cmd.Reset);       // clear the firmware's stale field cache first, so the
                                        // page comes up showing placeholders, not a previous
                                        // session's values (gate-independent — safe before gate-on)
            QueueStep(Cmd.GateOn);
            QueueStep(Cmd.Enable);
            QueueStep(Cmd.PageSet, page);
            ArmOnDrain(page);
            _log("ITM: bring-up (" + why + ") — reset + gate + enable + PageSet(" + page + ")");
        }

        /// <summary>
        /// Cold-entry target: provider override when it names a real page on this device;
        /// otherwise <see cref="EffectiveDefaultPage"/> (non-playlist byte-identical path).
        /// </summary>
        private byte ResolveColdEntryPage()
        {
            byte fallback = EffectiveDefaultPage();
            var provider = ColdEntryPageProvider;
            if (provider == null)
                return fallback;
            byte? overridePage = provider();
            if (overridePage is byte page && page != 0 && PageInfoFor(page) != null)
                return page;
            return fallback;
        }

        private void BeginSwitch(byte page)
        {
            _targetPage = page;
            State = ItmLifecycleState.Switching;
            ClearExpectation();
            _graceUntil = 0;
            _quietUntil = _now() + SwitchQuietMs;   // values are suspended from this instant
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
            // A PageSet to the legacy page can correctly push nothing (it carries no telemetry
            // parameters, and it may already be shown). A missed push there means we're on the
            // legacy page — confirm it, don't recover away from it.
            if ((State == ItmLifecycleState.AwaitPush || State == ItmLifecycleState.Switching)
                && IsLegacyPage(_targetPage))
            {
                ConfirmSync(_targetPage, "on the legacy ITM page (no push expected)");
                return;
            }

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
            AbandonInFlight();
            // UnavailableBackoffMs is publicly settable — tolerate a null/empty list rather
            // than crashing the lifecycle on a bad tunable.
            var steps = UnavailableBackoffMs;
            int backoff;
            if (steps == null || steps.Count == 0)
                backoff = 5_000;
            else
            {
                backoff = steps[Math.Min(_backoffIdx, steps.Count - 1)];
                if (_backoffIdx < steps.Count - 1)
                    _backoffIdx++;
            }
            _retryAt = _now() + backoff;
            _log("ITM: recovery ladder exhausted for page " + _targetPage + " — display unavailable, retrying in "
                 + (backoff / 1000) + " s. If this persists, another program may be driving the display.");
        }

        // ── Push judgment ────────────────────────────────────────────────

        // Judges the state of the handle map after an accumulation window closes. Matching is
        // on the parameter SET (handles are setup-specific and can re-bind); the map itself was
        // already adopted entry-by-entry as reports arrived. Runs only with the command queue
        // drained (mid-procedure closes are deferred to the drain), so the armed expectation
        // is always in place when its push is judged.
        private void JudgeAccumulation(long now)
        {
            var set = SubscribedParamSet();

            if (set.Count == 0)
            {
                // Unsubscribe-only push (no telemetry parameters).
                // - If we asked for the legacy page, this IS its confirming push — the legacy
                //   ITM page carries no parameters, so an unsubscribe-all is all it emits.
                if (ExpectsTargetPush() && IsLegacyPage(_targetPage))
                {
                    ClearExpectation();
                    _graceUntil = 0;
                    ConfirmSync(_targetPage, "on the legacy ITM page");
                    return;
                }
                // - In Synced it may be the front half of a page change (subs arrive within
                //   grace) or the display moving to the legacy page (wheel button) — grace
                //   decides (see the grace-expiry handling in Tick).
                if (State == ItmLifecycleState.Synced)
                    _graceUntil = now + UnsubGraceMs;
                return;
            }

            byte matchedPage = PageForParamSet(set);

            // The armed expectation confirms the procedure.
            if (_armedPage != 0 && SetEqualsPage(set, _armedPage))
            {
                byte page = _armedPage;
                ClearExpectation();
                _graceUntil = 0;
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

            // A push for the in-flight target that lands before the expectation is armed
            // (early) or after a rung re-targeted it (late). NOT during the flip-away rung:
            // there the flip PageSet is already accepted, so the display is about to move
            // again — confirming the target now would resume values mid-transition and
            // abandon the flip; wait for the flip page's push instead.
            if (ExpectsTargetPush() && SetEqualsPage(set, _targetPage)
                && !(State == ItmLifecycleState.Recovery && _rung == Rung.FlipAway))
            {
                ClearExpectation();
                _graceUntil = 0;
                ConfirmSync(_targetPage, "push confirmed (early)");
                return;
            }

            // Unexpected push: adopt, never fight.
            switch (State)
            {
                case ItmLifecycleState.Synced:
                    if (matchedPage == 0)
                    {
                        // A set that forms no page is most likely a fragment of a change
                        // still in flight (fragmented slower than the accumulation window).
                        // Suspend values (grace) and wait for the rest; grace expiry adopts
                        // whatever proved stable.
                        _graceUntil = now + UnsubGraceMs;
                        return;
                    }
                    _graceUntil = 0;
                    CurrentPage = matchedPage;
                    SyncGeneration++;
                    _log("ITM: page changed at the wheel — now page " + matchedPage + ": " + DescribeMap());
                    break;
                case ItmLifecycleState.AwaitPush:
                case ItmLifecycleState.Switching:
                case ItmLifecycleState.Recovery:
                case ItmLifecycleState.Unavailable:
                    // A COMPLETE different page than asked for (wheel button, late push from a
                    // boot-cold base): the display is demonstrably alive — sync to reality. Any
                    // queued host request is dropped rather than fought. Two look-alikes are
                    // NOT that and must not resolve the procedure:
                    // - a set matching no page is a fragment mid-flight; the rest lands in a
                    //   later accumulation — resuming values on it would reopen the re-bound-
                    //   handle hazard, so let the deadline/ladder decide;
                    // - a set matching the page we're LEAVING is a straggler re-announcement,
                    //   not a user action — confirming it would silently cancel the switch;
                    // - during flip-away the accepted flip PageSet means the display is about
                    //   to move again — wait for the flip push (or the deadline).
                    if (matchedPage == 0 || matchedPage == CurrentPage
                        || (State == ItmLifecycleState.Recovery && _rung == Rung.FlipAway))
                        break;
                    _pendingRequest = 0;
                    ClearExpectation();
                    _rung = Rung.None;
                    _graceUntil = 0;
                    ConfirmSync(matchedPage, "adopted unexpected push");
                    break;
                default:
                    // Idle / Disabled: keep the adopted map, no transition. While the user has
                    // ITM gated off, a push means someone else gated it on — note it, don't fight.
                    CurrentPage = matchedPage;
                    if (State == ItmLifecycleState.Disabled)
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

            // Reached a synced display with no game feeding it — a bring-up at connect or a
            // page change while idle. Clear the fields to placeholders so the page shows --- ,
            // not a previous session's cached values; a game arriving repaints over them. (Not
            // for the legacy page, which has no fields.)
            if (!_wasLive && !IsLegacyPage(page))
                QueueStep(Cmd.Reset);

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
                    case Cmd.Reset:
                        ok = _encoder.ResetDisplay();   // FF 05 05 01 — clear cached field values
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
                _armedPage = _armPage;
                _deadlineAt = now + PushDeadlineMs;
                if (State == ItmLifecycleState.BringUp)
                    State = ItmLifecycleState.AwaitPush;
            }

            // A push accumulation that closed while the commands were still being accepted
            // is judged now, with the expectation armed — the one JudgeAccumulation path
            // handles it exactly as if it had arrived after the drain. Judging is strictly
            // push-driven: with no push, the map alone can look "already right" from stale
            // pre-procedure state (e.g. a resume to the page shown before the exit), and
            // that must never self-confirm.
            if (_judgeAtDrain)
            {
                _judgeAtDrain = false;
                JudgeAccumulation(now);
            }
        }

        private void ClearProcedure()
        {
            _steps.Clear();
            _stepIdx = 0;
            _armOnDrain = false;
            _judgeAtDrain = false;
            _quietUntil = 0;
        }

        private void ClearExpectation()
        {
            _armedPage = 0;
            _deadlineAt = 0;
        }

        // Drops everything in flight: the command procedure, the push expectation, an open
        // accumulation, the grace window, and the recovery rung. Used by every transition
        // that abandons the current activity (stop, cold entry, user off) — hand-picked subsets
        // at each site drift, and a survivor (e.g. an open accumulation crossing a cold entry)
        // can mis-attribute a stale push to the new session.
        private void AbandonInFlight()
        {
            ClearProcedure();
            ClearExpectation();
            _accumCloseAt = 0;
            _graceUntil = 0;
            _rung = Rung.None;
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

        // The catalog entry for a wire page number on this device, or null if unknown.
        private ItmPageInfo PageInfoFor(byte page)
        {
            if (page != 0)
                foreach (var p in ItmDeviceCatalog.PagesFor(_deviceId))
                    if (p.Number == page)
                        return p;
            return null;
        }

        private bool SetEqualsPage(HashSet<ushort> set, byte page)
        {
            var info = PageInfoFor(page);
            // An unknown page (no catalog entry) can't be matched exactly — accept any non-empty
            // set as that procedure's outcome. Armed pages are always catalog pages, so this is
            // only a safety net.
            if (info == null)
                return set.Count > 0;
            // The legacy page carries no parameters, so its confirming push is the empty
            // unsubscribe-all (handled earlier in JudgeAccumulation). A NON-empty set reaching
            // here is a telemetry page and must never match legacy — otherwise we'd confirm
            // "on legacy" while holding telemetry subscriptions.
            if (info.Params.Count == 0)
                return set.Count == 0;

            var expected = info.Params;
            if (expected.Count != set.Count)
                return false;
            for (int i = 0; i < expected.Count; i++)
                if (!set.Contains(expected[i]))
                    return false;
            return true;
        }

        // The legacy ITM page has no telemetry parameters — its PageSet pushes only an
        // unsubscribe-all (or nothing, if already there), which the matcher must treat as a
        // valid destination rather than a dropped-subscriptions failure.
        private bool IsLegacyPage(byte page)
        {
            var info = PageInfoFor(page);
            return info != null && info.IsLegacy;
        }

        // The device's legacy page number, or 0 if it has none.
        private byte LegacyPageNumber()
        {
            foreach (var p in ItmDeviceCatalog.PagesFor(_deviceId))
                if (p.IsLegacy)
                    return p.Number;
            return 0;
        }

        // The configured starting page resolved to a real page on this device. Guards against a
        // 0 (unset) or an out-of-range value (e.g. a page number saved for a different display)
        // becoming a PageSet(0) or a target no push can ever confirm — falls back to the
        // device's first page.
        private byte EffectiveDefaultPage()
        {
            if (PageInfoFor(DefaultPage) != null)
                return DefaultPage;
            var pages = ItmDeviceCatalog.PagesFor(_deviceId);
            return pages.Count > 0 ? pages[0].Number : (byte)1;
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
    }
}
