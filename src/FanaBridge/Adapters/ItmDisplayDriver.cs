using System;
using System.Collections.Generic;
using FanaBridge.Protocol;
using GameReaderCommon;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Drives a Fanatec ITM telemetry display: maps SimHub telemetry to the parameters the
    /// firmware has subscribed, paces the sends, and maintains the ParamDefs unit/total
    /// suffixes. The <b>lifecycle</b> — bring-up, page switches, push confirmation, game-exit
    /// gating, recovery — lives in <see cref="ItmLifecycleController"/>; this driver only
    /// sends while the controller says the display is synced (<see cref="ItmLifecycleController.ValuesAllowed"/>)
    /// and repaints whenever a push is adopted (<see cref="ItmLifecycleController.SyncGeneration"/>):
    /// after any resync the display shows stale firmware-cached values until the first fresh send.
    ///
    /// First values after a sync are double-tapped (~20 ms apart, matching official-software
    /// post-switch behavior), and ParamDefs go out <i>after</i> the value double-tap.
    ///
    /// Sits above <see cref="ItmEncoder"/> (framing) and <see cref="ItmTelemetry"/>
    /// (value encoding + subscription parsing). The clock is injectable so the timing is
    /// unit testable.
    /// </summary>
    public class ItmDisplayDriver
    {
        private readonly ItmEncoder _encoder;
        private readonly byte _deviceId;   // which display this driver targets (PageSet + per-entry id)
        private readonly Func<long> _now;
        private readonly Action<string> _log;
        private readonly ItmLifecycleController _lifecycle;

        // ── Tunables (ms) ────────────────────────────────────────────────
        /// <summary>Minimum spacing between value-update sends (caps the rate).</summary>
        public int ValueIntervalMs { get; set; } = 40;

        /// <summary>
        /// Gap before the tight second value send after a sync (double-tap). The first values
        /// after a page change are double-tapped ~20 ms apart, matching the official software's
        /// post-switch behavior — free insurance against a single lost first paint.
        /// </summary>
        public int ValueDoubleTapMs { get; set; } = 20;

        /// <summary>
        /// Maximum age of the on-display values before they are re-sent even when unchanged.
        /// ValueUpdate is unacked, so a lost frame would otherwise stay wrong until the value
        /// next changes — which in practice only matters when the whole set is static (any
        /// single changed value already re-sends the full buffer at <see cref="ValueIntervalMs"/>
        /// cadence). Cheap insurance: one report per interval. Same rationale as the ParamDefs
        /// double-tap; there is no confirmed observation of a dropped ValueUpdate (an earlier
        /// lab sighting turned out to be col03 co-driver contention).
        /// </summary>
        public int RefreshIntervalMs { get; set; } = 500;

        /// <summary>
        /// Gap before the tight second ParamDefs send (double-tap). ParamDefs is unacked
        /// and a single send is occasionally dropped by the firmware; the official app
        /// double-taps ~49ms apart to prime the decoration so it sticks.
        /// </summary>
        public int DefDoubleTapMs { get; set; } = 50;

        /// <summary>Minimum spacing between display-values snapshot compositions (the UI
        /// mirror's data). Change-gated on top of this floor: nothing recomposes while no
        /// sent value/suffix/page/state changed.</summary>
        public int ValuesSnapshotIntervalMs { get; set; } = 250;

        /// <summary>Whether to show the "/total laps" suffix on the lap field.</summary>
        public bool ShowLapTotal { get; set; } = true;

        /// <summary>Whether to show the "/field size" suffix on the position field.</summary>
        public bool ShowPositionTotal { get; set; } = true;

        /// <summary>
        /// The built-in page policy's base page (the user's ItmDefaultPage setting, as a
        /// wire page number): targeted by bring-up, and the page the wheel's display
        /// button navigates from. Read live each frame; changing it while the built-in
        /// policy owns pages requests a confirmed page switch (edge-detected inside
        /// <see cref="Update"/>). While an external owner holds page policy
        /// (<see cref="SetPagePolicy"/>) this setting is retained but dormant — it takes
        /// over again on <see cref="RestoreBuiltInPagePolicy"/>.
        /// </summary>
        public byte DefaultPage { get; set; } = 1;

        // ── Page policy ──────────────────────────────────────────────────
        // Exactly one owner at a time decides which page the display rests on:
        //
        //   Built-in (default): the DefaultPage setting is the resting page. The
        //   lifecycle re-establishes it at every game start (GameStartPageRevert) and
        //   cold entries target it.
        //
        //   External (display rules): SetPagePolicy hands the resting page to the rule
        //   stack's base page. The lifecycle's own game-start revert is suppressed for
        //   the whole tenure, because the revert belongs to whoever owns page policy:
        //   the rule engine performs the same revert itself (resting target → base on
        //   the in-game rising edge, routed through the page director), and a switch
        //   the lifecycle initiated on its own would read upstream as phantom
        //   wheel-button navigation, dismissing rules the user never touched (brief
        //   §4.6 history: the phantom-manual bug).
        //
        // The handoff is edge-triggered — called at stack build/teardown and on a base
        // change within a live stack, never as per-frame reconciliation. Page requests
        // ride the existing edge detection in UpdateCore, so a handoff that changes the
        // effective base switches the display live on the next Update, exactly like a
        // DefaultPage settings change does in the built-in mode.

        // Non-null while an external owner holds page policy (its base wire page).
        private byte? _externalBasePage;

        /// <summary>
        /// Hands page policy to an external owner whose resting page is
        /// <paramref name="baseWirePage"/> (the rule stack's base page). Call on stack
        /// build and whenever the base changes within a live stack.
        /// </summary>
        public void SetPagePolicy(byte baseWirePage) => _externalBasePage = baseWirePage;

        /// <summary>
        /// Returns page policy to the built-in owner (the <see cref="DefaultPage"/>
        /// setting, game-start revert re-enabled). Call on stack teardown; also implied
        /// by <see cref="Stop"/> — every Stop edge coincides with a stack teardown, and
        /// the next session must bring up under the setting's policy.
        /// </summary>
        public void RestoreBuiltInPagePolicy() => _externalBasePage = null;

        /// <summary>True while an external owner (the display rules) holds page policy.</summary>
        public bool HasExternalPagePolicy => _externalBasePage != null;

        // The resting page under the current policy owner.
        private byte EffectiveBasePage => _externalBasePage ?? DefaultPage;

        /// <summary>
        /// Whether the ITM display is enabled. Set false to turn ITM off (the display is gated
        /// off — the same persistent state the vendor software's ITM switch sets — and the
        /// driver goes dormant); set true to re-enable. Read live each frame; applied to the
        /// lifecycle inside <see cref="Update"/> so all controller mutation stays on the
        /// update thread.
        /// </summary>
        public bool Enabled { get; set; } = true;

        // ── State ────────────────────────────────────────────────────────
        private long _lastValuesMs;
        private long _lastSendOkMs;    // last accepted value send — drives the periodic re-assert
        // Edge-detects a default-page settings change; null = re-baseline on the next Update
        // (fresh driver or post-Stop), so the first frame never reads as a change.
        private byte? _lastRequestedPage;

        // Post-sync repaint: first values immediately, a tight second tap, then ParamDefs.
        private enum Paint { None, First, SecondTap }
        private Paint _paint = Paint.None;
        private long _paintTap2At;
        private int _lastSyncGen;

        private ItmValue[] _lastValues;
        private bool _loggedFirstValues;
        private string _lastSlotDefsSig = "";   // last ParamDefs suffix set, to skip redundant writes
        private long _defTap2DueMs;               // when to fire the tight second def tap (0 = none)
        private List<ItmParamDef> _defTap2Defs;   // the defs to re-send as that second tap
        // Reused per-tick send buffer (avoids a per-frame allocation).
        private readonly List<ItmValue> _valueBuf = new List<ItmValue>();
        // Subscribed paramIds we've already warned have no encoder — log each once.
        private readonly HashSet<ushort> _unencodableWarned = new HashSet<ushort>();

        // ── Display-values snapshot (the UI mirror's data) ───────────────
        // Pure observation of the send path above: composed from the values/suffixes as
        // last sent, never touching the encoder — the wire behavior must stay
        // byte-identical with or without an observer.
        private volatile DisplayValuesSnapshot _valuesSnapshot;
        private bool _snapDirty;                    // a sent value/suffix edged since the last compose
        private ItmLifecycleState _snapState = (ItmLifecycleState)(-1);   // -1 = compose on first Update
        private byte _snapWirePage;
        private int _snapSyncGen;
        private bool _snapFieldsReset;              // mirror of the lifecycle's game-exit DisplayReset
        private bool _snapWasLive;                  // driver-side copy of the telemetry-liveness edge
        private long _snapComposedMs = long.MinValue / 2;
        // The per-param suffixes as last latched on the wire (paramId → suffix), and the
        // per-frame scratch UpdateSlotDefs fills while it builds its signature.
        private readonly Dictionary<ushort, string> _sentSuffixes = new Dictionary<ushort, string>();
        // GEAR's declared wire form (text vs numeric) as of the send that produced
        // _lastValues — latched with the values because the live subscription map can
        // drop the gear entry mid-page-change while the latched ASCII byte is still on
        // display, and decoding it with the wrong form misreads '6' (0x36) as 54.
        private byte _sentGearDataType;
        private readonly List<KeyValuePair<ushort, string>> _suffixScratch =
            new List<KeyValuePair<ushort, string>>();

        public ItmDisplayDriver(ItmEncoder encoder, Func<long> nowMs = null, Action<string> log = null,
            byte deviceId = ItmEncoder.DefaultDeviceId)
        {
            _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
            _now = nowMs ?? DefaultClock();
            _log = log ?? (_ => { });
            _deviceId = deviceId;
            _lifecycle = new ItmLifecycleController(encoder, deviceId, _now, _log);
        }

        private static Func<long> DefaultClock()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            return () => sw.ElapsedMilliseconds;
        }

        /// <summary>The lifecycle state machine — exposed for status surfacing and tuning.</summary>
        public ItmLifecycleController Lifecycle => _lifecycle;

        /// <summary>True while the display is push-confirmed in sync (values may flow).</summary>
        public bool IsRunning => _lifecycle.State == ItmLifecycleState.Synced;

        /// <summary>The number of parameters the firmware currently has subscribed.</summary>
        public int SubscriptionCount => _lifecycle.SubscriptionCount;

        /// <summary>
        /// The latest display-values snapshot — what the ITM display is showing, rendered
        /// from the values this driver last sent — or null before the first
        /// <see cref="Update"/> (and after <see cref="Stop"/>). Volatile; safe to read
        /// from any thread.
        /// </summary>
        public DisplayValuesSnapshot ValuesSnapshot => _valuesSnapshot;

        /// <summary>
        /// Begins the ITM lifecycle (cold bring-up) on the next <see cref="Update"/>.
        /// Idempotent — a no-op unless idle, so it is safe to call every frame.
        /// </summary>
        public void Start()
        {
            _lifecycle.DefaultPage = EffectiveBasePage;
            _lifecycle.Start();
        }

        /// <summary>
        /// Stops driving and returns to idle (connection lost), dropping all subscriptions.
        /// A later <see cref="Start"/> re-runs the cold bring-up. Page policy returns to
        /// the built-in owner: the stack that held it is torn down at every Stop edge,
        /// and a rebuilt stack re-takes policy explicitly.
        /// </summary>
        public void Stop()
        {
            _lifecycle.Stop();
            ResetSendState();
            ResetValuesSnapshot();
            RestoreBuiltInPagePolicy();
        }

        // Drops the published values snapshot and its change trackers — the same
        // teardown edge as the device instance's ITM status snapshot, so a stale
        // "what the display shows" can never outlive the session it described.
        private void ResetValuesSnapshot()
        {
            _valuesSnapshot = null;
            _snapDirty = false;
            _snapState = (ItmLifecycleState)(-1);   // recompose on the next Update
            _snapWirePage = 0;
            _snapSyncGen = 0;
            _snapFieldsReset = false;
            _snapWasLive = false;
            _snapComposedMs = long.MinValue / 2;
            _sentSuffixes.Clear();
            _sentGearDataType = 0;
        }

        /// <summary>
        /// A wheel/hub/module change from the identity layer. A hot-swap resets the display
        /// cold without any trace on the ITM channel itself — the lifecycle restarts from
        /// bring-up and nothing is sent until the fresh push confirms.
        /// </summary>
        public void OnWheelChanged()
        {
            // The cold entry runs immediately — make sure it targets the current
            // policy owner's resting page (the stack that owned policy is rebuilt for
            // the new wheel from the same config, so its base stays valid here).
            _lifecycle.DefaultPage = EffectiveBasePage;
            _lifecycle.OnWheelChanged();
        }

        /// <summary>Drops the driver-side send latches (dirty tracking, def signatures, taps).</summary>
        private void ResetSendState()
        {
            _lastValues = null;
            _loggedFirstValues = false;
            _lastSlotDefsSig = "";
            _defTap2DueMs = 0;
            _defTap2Defs = null;
            _paint = Paint.None;
            _lastRequestedPage = null;
        }

        /// <summary>
        /// Applies a firmware ITM subscription report (col03-IN push): parsed and handed to the
        /// lifecycle, which adopts the entries in every state and judges the accumulated set.
        /// </summary>
        public void OnSubscriptionReport(byte[] report)
        {
            var subs = ItmTelemetry.ParseSubscriptionReport(report, report?.Length ?? 0, _deviceId);
            if (subs.Count == 0)
                return;
            _lifecycle.OnPush(subs);
        }

        /// <summary>Drives the lifecycle and the value/defs pipeline one tick. Call once per frame while connected.</summary>
        public void Update(GameData data)
        {
            long now = _now();
            bool telemetryLive = data != null && data.GameRunning && data.NewData != null;

            // The state before the lifecycle ticks: the game-exit DisplayReset is queued
            // from Synced on the live→dead edge inside Tick, so the mirror below must
            // judge the same edge against the same (pre-Tick) state.
            var statePreTick = _lifecycle.State;

            UpdateCore(data, now, telemetryLive);

            // ── Display-values snapshot (observation only — nothing here sends) ──
            // Mirror the lifecycle's game-exit DisplayReset: from the same edge, the
            // hardware fields revert to placeholders until the next successful send.
            if (_snapWasLive && !telemetryLive && statePreTick == ItmLifecycleState.Synced
                && !_snapFieldsReset)
            {
                _snapFieldsReset = true;
                _snapDirty = true;
            }
            _snapWasLive = telemetryLive;
            MaybeComposeValuesSnapshot(now);
        }

        // The send pipeline proper (lifecycle tick, post-sync paints, values, defs) —
        // extracted so the snapshot step above runs on every frame regardless of which
        // early-out this path takes, without touching any send ordering.
        private void UpdateCore(GameData data, long now, bool telemetryLive)
        {
            // The page policy flows into the lifecycle: the current owner's resting
            // page (cold-entry target) and — revert belongs to the policy owner — the
            // game-start revert only while the built-in owner holds policy. A change of
            // the effective base (a DefaultPage settings change in the built-in mode, a
            // policy handoff, or a base change within a live stack) is edge-detected
            // and requested live, so the wheel button isn't fought between changes.
            byte basePage = EffectiveBasePage;
            _lifecycle.DefaultPage = basePage;
            _lifecycle.GameStartPageRevert = _externalBasePage == null;
            _lifecycle.SetUserEnabled(Enabled);
            if (_lastRequestedPage == null)
                _lastRequestedPage = basePage;
            else if (basePage != _lastRequestedPage.Value)
            {
                _lastRequestedPage = basePage;
                _lifecycle.RequestPage(basePage);
            }

            _lifecycle.Tick(telemetryLive);

            // A new sync generation = a push was adopted (bring-up, page change, resume,
            // recovery). The firmware may be showing stale cached values and its suffix
            // decorations may be gone — repaint everything: values immediately, a tight
            // second tap, then ParamDefs.
            if (_lifecycle.SyncGeneration != _lastSyncGen)
            {
                _lastSyncGen = _lifecycle.SyncGeneration;
                _lastValues = null;
                _lastSlotDefsSig = "";
                _defTap2DueMs = 0;
                _defTap2Defs = null;
                _paint = Paint.First;
            }

            // Values (and defs) flow only while push-confirmed in sync — mid-switch traffic
            // is the identified cause of dropped switches, and handles can re-bind.
            if (!_lifecycle.ValuesAllowed)
                return;

            // ...and only while a game is feeding telemetry: SimHub keeps the last telemetry
            // values around after a game exits, and painting from stale data would resurrect
            // exactly the frozen frame the exit DisplayReset just cleared to placeholders.
            if (!telemetryLive)
                return;

            switch (_paint)
            {
                case Paint.First:
                    // Immediate post-sync paint, bypassing the interval and the change gate.
                    switch (TrySendValues(data, now, force: true))
                    {
                        case SendOutcome.Sent:
                            _paint = Paint.SecondTap;
                            _paintTap2At = now + ValueDoubleTapMs;
                            break;
                        case SendOutcome.NothingToSend:
                            _paint = Paint.None;   // nothing encodable — no tap needed
                            break;
                            // Declined: retry next tick.
                    }
                    break;

                case Paint.SecondTap:
                    if (now >= _paintTap2At && TrySendValues(data, now, force: true) != SendOutcome.Declined)
                        _paint = Paint.None;
                    break;

                default:
                    if (now - _lastValuesMs >= ValueIntervalMs)
                    {
                        TrySendValues(data, now, force: false);
                        _lastValuesMs = now;
                    }
                    break;
            }

            // ParamDefs go out after the post-sync value double-tap completes (values-then-defs,
            // matching the official software's post-switch ordering), then on every suffix change.
            if (_paint == Paint.None)
                UpdateSlotDefs(data, now);
        }

        // Sends ParamDefs declaring each subscribed param's display suffix — a static
        // unit (e.g. "C", "L") or a dynamic total ("/34" for lap/position). Cosmetic:
        // the value renders from ValueUpdate regardless. Slot ID = 0x80 | handle, per
        // the capture. Only writes when the suffix set actually changes (subscription
        // change or a moving total), so it does not flood the bus.
        private void UpdateSlotDefs(GameData data, long now)
        {
            // Tight double-tap: re-send the just-sent defs once, ~DefDoubleTapMs later.
            // ParamDefs is unacked and a single send is occasionally dropped by the firmware;
            // the tight second tap (matching the official app's ~49 ms) makes it stick.
            // A declined tap keeps its due time so it retries next tick.
            if (_defTap2DueMs != 0 && now >= _defTap2DueMs)
            {
                if (_defTap2Defs == null || _encoder.SetParamDefs(_defTap2Defs, _deviceId))
                    _defTap2DueMs = 0;
            }

            List<ItmParamDef> defs = null;
            var sig = new System.Text.StringBuilder();
            _suffixScratch.Clear();

            var subs = _lifecycle.Subscriptions;
            for (int i = 0; i < subs.Count; i++)
            {
                var kv = subs[i];
                ushort paramId = kv.Value.ParamId;
                string suffix;
                if (ItmTelemetryMapper.TryGetUnitSuffix(paramId, data, out suffix))
                {
                    // Static unit label (e.g. "C").
                }
                else if (ItmTelemetryMapper.IsTotalParam(paramId))
                {
                    // Lap/position/fuel: always emit an entry so a total that disappears is
                    // actively cleared — a zero-length suffix does NOT overwrite the firmware's
                    // default "/0", so we write a blank " " to clear it. Fuel is special: with no
                    // tank capacity it falls back to the unit label ("L"/"G") rather than a blank,
                    // so a bare fuel value still reads as fuel.
                    suffix = ShowTotalFor(paramId)
                          && ItmTelemetryMapper.TryGetTotalSuffix(paramId, data, out var total)
                        ? total
                        : (paramId == ItmParam.Fuel ? ItmTelemetryMapper.FuelUnitLabel(data) : " ");
                }
                else
                {
                    continue;
                }

                sig.Append(kv.Key).Append('=').Append(suffix).Append(';');
                if (defs == null) defs = new List<ItmParamDef>();
                defs.Add(ItmParamDef.WithSuffix((byte)(0x80 | kv.Key), suffix));
                _suffixScratch.Add(new KeyValuePair<ushort, string>(paramId, suffix));
            }

            string s = sig.ToString();
            if (s == _lastSlotDefsSig)
                return;   // unchanged — nothing to send

            if (defs == null)
            {
                _lastSlotDefsSig = s;   // nothing on the wire for this set — just record it
                RememberSuffixes();
                return;
            }

            // Latch the signature only when the transport accepts the write — otherwise a
            // declined ParamDefs send would never be retried until the suffix set happens
            // to change again, leaving the display undecorated (blank/wrong suffixes).
            if (!_encoder.SetParamDefs(defs, _deviceId))
                return;
            _lastSlotDefsSig = s;
            RememberSuffixes();
            _defTap2Defs = defs;                  // schedule the tight second tap
            _defTap2DueMs = now + DefDoubleTapMs;
            _log("ITM: ParamDefs sent — suffixes: " + s);
        }

        private bool ShowTotalFor(ushort paramId)
        {
            if (paramId == ItmParam.Lap) return ShowLapTotal;
            if (paramId == ItmParam.Position) return ShowPositionTotal;
            if (paramId == ItmParam.Fuel) return true;   // fuel/capacity has no user toggle
            return false;
        }

        private enum SendOutcome { Sent, NothingToSend, Declined }

        // Encodes the subscribed values and sends them. force bypasses the change gate (used
        // by the post-sync paint and its double-tap); the periodic re-assert bypasses it too.
        private SendOutcome TrySendValues(GameData data, long now, bool force)
        {
            _valueBuf.Clear();
            byte gearDataType = 0;
            var subs = _lifecycle.Subscriptions;
            for (int i = 0; i < subs.Count; i++)
            {
                var kv = subs[i];
                if (ItmTelemetryMapper.TryEncodeParam(kv.Value.ParamId, kv.Key, data, kv.Value.DataType, out var v))
                {
                    _valueBuf.Add(v);
                    if (kv.Value.ParamId == ItmParam.Gear)
                        gearDataType = kv.Value.DataType;
                }
                else if (_unencodableWarned.Add(kv.Value.ParamId))
                    // Firmware subscribed a parameter outside our page layouts — it will
                    // render as dashes. Note it once so the gap is diagnosable.
                    _log("ITM: no encoder for subscribed param " + kv.Value.ParamId +
                         " (handle " + kv.Key + ") — field will show dashes");
            }

            if (_valueBuf.Count == 0)
                return SendOutcome.NothingToSend;

            // Re-assert even unchanged values every RefreshIntervalMs: ValueUpdate is unacked,
            // and change-gated sending alone would leave a lost frame wrong on the display
            // until the value next changes.
            bool refresh = now - _lastSendOkMs >= RefreshIntervalMs;
            if (!force && !refresh && !HasChanged(_valueBuf))
                return SendOutcome.NothingToSend;

            // Only record the values as last-sent (and log the first update) when the send
            // actually succeeded — a transport failure must not suppress the retry, or the
            // display would stay stale until a value changes.
            if (!_encoder.SendValues(_valueBuf, _deviceId))
                return SendOutcome.Declined;

            // Snapshot bookkeeping (observation only): a send that changed what's on the
            // display — different values, or the first paint over placeholders — marks
            // the values snapshot for recomposition.
            if (_snapFieldsReset || HasChanged(_valueBuf))
                _snapDirty = true;
            _snapFieldsReset = false;

            _lastSendOkMs = now;
            Remember(_valueBuf);
            _sentGearDataType = gearDataType;   // latched with the values it decoded

            if (!_loggedFirstValues)
            {
                _loggedFirstValues = true;
                _log("ITM: first value update — " + _valueBuf.Count + " params: " + _lifecycle.DescribeMap());
            }
            return SendOutcome.Sent;
        }

        private bool HasChanged(IReadOnlyList<ItmValue> values)
        {
            if (_lastValues == null || _lastValues.Length != values.Count)
                return true;
            for (int i = 0; i < values.Count; i++)
            {
                var a = _lastValues[i];
                var b = values[i];
                if (a.ParamId != b.ParamId || a.Size != b.Size || a.Raw != b.Raw || a.Handle != b.Handle)
                    return true;
            }
            return false;
        }

        private void Remember(IReadOnlyList<ItmValue> values)
        {
            if (_lastValues == null || _lastValues.Length != values.Count)
                _lastValues = new ItmValue[values.Count];
            for (int i = 0; i < values.Count; i++)
                _lastValues[i] = values[i];
        }

        // ── Display-values snapshot composition ──────────────────────────
        // Everything below is observation of the send state above: it reads the
        // last-sent values/suffixes and the lifecycle, allocates only when it actually
        // composes, and never touches the encoder — wire behavior is byte-identical
        // with or without a snapshot consumer.

        // Latches the per-param suffixes exactly when the ParamDefs signature latches,
        // so the snapshot always renders the suffixes as last accepted on the wire.
        private void RememberSuffixes()
        {
            _sentSuffixes.Clear();
            for (int i = 0; i < _suffixScratch.Count; i++)
                _sentSuffixes[_suffixScratch[i].Key] = _suffixScratch[i].Value;
            _snapDirty = true;
        }

        // Change-gated, throttled recomposition: only when a sent value/suffix (dirty
        // flag), the lifecycle state, the page, or the sync generation edged — and no
        // more often than ValuesSnapshotIntervalMs. A change landing inside the window
        // stays dirty and composes on a later tick.
        private void MaybeComposeValuesSnapshot(long now)
        {
            var state = _lifecycle.State;
            byte wire = _lifecycle.CurrentPage;
            int gen = _lifecycle.SyncGeneration;
            if (!_snapDirty && state == _snapState && wire == _snapWirePage && gen == _snapSyncGen)
                return;
            if (now - _snapComposedMs < ValuesSnapshotIntervalMs)
            {
                _snapDirty = true;   // hold the change until the throttle window passes
                return;
            }
            _snapDirty = false;
            _snapState = state;
            _snapWirePage = wire;
            _snapSyncGen = gen;
            _snapComposedMs = now;
            _valuesSnapshot = ComposeValuesSnapshot(state, wire, now);
        }

        private DisplayValuesSnapshot ComposeValuesSnapshot(ItmLifecycleState state, byte wire, long now)
        {
            // Resolve the wire page to its content identity on this device.
            ItmPageInfo info = null;
            if (wire != 0)
            {
                var pages = ItmDeviceCatalog.PagesFor(_deviceId);
                for (int i = 0; i < pages.Count; i++)
                    if (pages[i].Number == wire) { info = pages[i]; break; }
            }

            // Placeholders when nothing has been sent since the last sync (post-bring-up,
            // post-page-change) or the game-exit reset cleared the fields — matching the
            // DisplayReset the lifecycle sent to the hardware.
            bool placeholders = _lastValues == null || _snapFieldsReset;

            var layout = info != null ? ItmDisplayLayout.For(info.Page) : null;
            DisplayValueSlot lt = null, lb = null, rt = null, rb = null;
            string gear = null, speed = null;
            if (layout != null && layout.HasSlots)
            {
                lt = BuildSlot(layout.LeftTop, placeholders);
                lb = BuildSlot(layout.LeftBottom, placeholders);
                rt = BuildSlot(layout.RightTop, placeholders);
                rb = BuildSlot(layout.RightBottom, placeholders);
                gear = RenderField(ItmParam.Gear, placeholders);
                speed = RenderField(ItmParam.Speed, placeholders);
            }

            string pageName = info != null ? info.Name : (wire != 0 ? "Page " + wire : null);
            return new DisplayValuesSnapshot(
                info != null ? info.Page : (ItmPage?)null, wire, pageName, state, placeholders,
                lt, lb, rt, rb, gear, speed, now, DateTime.UtcNow);
        }

        private DisplayValueSlot BuildSlot(ItmDisplaySlot slot, bool placeholders)
        {
            var fields = new DisplayValueField[slot.Fields.Count];
            for (int i = 0; i < fields.Length; i++)
            {
                var f = slot.Fields[i];
                fields[i] = new DisplayValueField(f.ParamId, f.Label, RenderField(f.ParamId, placeholders));
            }
            return new DisplayValueSlot(slot.Label, fields);
        }

        // The display string for one parameter: the value as last sent (with the suffix
        // as last latched, and — for GEAR — the wire form latched at that same send;
        // the live subscription map may already have dropped the entry mid-page-change).
        private string RenderField(ushort paramId, bool placeholders)
        {
            if (!placeholders && TryGetLastValue(paramId, out var value))
            {
                _sentSuffixes.TryGetValue(paramId, out var suffix);
                byte dataType = paramId == ItmParam.Gear ? _sentGearDataType : (byte)0;
                return ItmValueRenderer.Render(paramId, value, suffix, dataType);
            }
            return ItmValueRenderer.Placeholder(paramId);
        }

        private bool TryGetLastValue(ushort paramId, out ItmValue value)
        {
            var last = _lastValues;
            if (last != null)
                for (int i = 0; i < last.Length; i++)
                    if (last[i].ParamId == paramId) { value = last[i]; return true; }
            value = default;
            return false;
        }
    }
}
