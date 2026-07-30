using System;
using System.Collections.Generic;
using FanaBridge.Display.Session;
using FanaBridge.Protocol;
using GameReaderCommon;

namespace FanaBridge.Display.Drivers
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

        /// <summary>
        /// Whether to show the "/total laps" suffix on the lap field. Migrating into the
        /// format layer: the mapper owns suffix decisions; this property is the settings
        /// toggle mirror (honored + written for one release — same pattern as itmEnabled
        /// in P3). Toggle=false with no explicit format acts as Format=bare.
        /// </summary>
        public bool ShowLapTotal
        {
            get => _mapper.ShowLapTotal;
            set => _mapper.ShowLapTotal = value;
        }

        /// <summary>
        /// Whether to show the "/field size" suffix on the position field. See
        /// <see cref="ShowLapTotal"/> — same format-layer migration.
        /// </summary>
        public bool ShowPositionTotal
        {
            get => _mapper.ShowPositionTotal;
            set => _mapper.ShowPositionTotal = value;
        }

        /// <summary>The per-device telemetry mapper (built-in registry + field overrides).</summary>
        public ItmTelemetryMapper Mapper => _mapper;

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
        private long _lastIdleDefsMs;  // paces the idle ParamDefs path (signature-gated besides)
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
        // Defensive MaxParams warn-once (firmware should never announce >16).
        private bool _warnedOverMaxParams;
        // Per-device mapper (built-in default encoder registry). Shared helpers that do not
        // need instance state (e.g. NearestGap) stay static on the mapper type.
        private readonly ItmTelemetryMapper _mapper;

        public ItmDisplayDriver(ItmEncoder encoder, Func<long> nowMs = null, Action<string> log = null,
            byte deviceId = ItmEncoder.DefaultDeviceId)
        {
            _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
            _now = nowMs ?? DefaultClock();
            _log = log ?? (_ => { });
            _deviceId = deviceId;
            _lifecycle = new ItmLifecycleController(encoder, deviceId, _now, _log);
            _mapper = new ItmTelemetryMapper();
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
            RestoreBuiltInPagePolicy();
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
            UpdateCore(data, now, telemetryLive);
        }

        // The send pipeline proper: lifecycle tick, post-sync paints, values, defs.
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

            // Values flow only while a game is feeding telemetry: SimHub keeps the last
            // telemetry values around after a game exits, and painting from stale data
            // would resurrect exactly the frozen frame the exit DisplayReset just cleared
            // to placeholders. ParamDefs are different: suffixes/decorations are authored
            // content, not telemetry — change-gated by signature, they must land at idle
            // too or a config edit stays invisible until the next game start (idle parity;
            // dynamic totals resolve bare without live data).
            if (!telemetryLive)
            {
                // Signature-gated like the in-game path, plus interval-paced: a
                // blinking authored suffix must not turn idle into a ParamDefs
                // firehose (values pacing does this job in game).
                if (now - _lastIdleDefsMs >= ValueIntervalMs)
                {
                    _lastIdleDefsMs = now;
                    UpdateSlotDefs(data, now, telemetryLive: false);
                }
                return;
            }

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
                UpdateSlotDefs(data, now, telemetryLive: true);
        }

        // Sends ParamDefs declaring each subscribed param's display suffix — a static
        // unit (e.g. "C", "L") or a dynamic total ("/34" for lap/position). Cosmetic:
        // the value renders from ValueUpdate regardless. Slot ID = 0x80 | handle, per
        // the capture. Only writes when the suffix set actually changes (subscription
        // change or a moving total), so it does not flood the bus.
        private void UpdateSlotDefs(GameData data, long now, bool telemetryLive)
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

            var subs = _lifecycle.Subscriptions;
            // Defensive assert at the defs/values seam: firmware hard cap is
            // ItmEncoder.MaxParams (16). Real pages are always ≤16 by firmware
            // construction — this branch is dormant-safe (unreachable on today's paths).
            AssertSubscriptionBudget(subs.Count);
            for (int i = 0; i < subs.Count; i++)
            {
                var kv = subs[i];
                ushort paramId = kv.Value.ParamId;
                string suffix;
                // Mapper owns the format layer (withTotal|bare / unit|bare, overridden-
                // source default-bare, Show*Total toggle migration). Always emit for
                // temps and totals so a suffix that disappears is actively cleared —
                // a zero-length suffix does NOT overwrite the firmware's default "/0".
                if (_mapper.TryGetUnitSuffix(paramId, data, out suffix))
                {
                    // Temperature unit label (or blank when format=bare).
                }
                else if (_mapper.TryResolveTotalSuffix(paramId, data, out suffix))
                {
                    // Lap/position/fuel total (or blank / fuel unit-label fallback).
                    // Telemetry-derived: at idle a send would actively CLEAR (or paint
                    // stale) the decoration the firmware still shows — skip the entry
                    // entirely; authored (plan-owned) suffixes stay idle-live.
                    if (!telemetryLive && !_mapper.HasPlanOwnedSuffix(paramId))
                        continue;
                }
                else
                {
                    continue;
                }

                // Wire ceiling: one over-long suffix would reject the WHOLE ParamDefs
                // batch (SetParamDefs pre-validates) and blank every field's
                // decoration — clamp at the single wire seam instead.
                if (suffix != null && suffix.Length > ItmEncoder.MaxSuffixLength)
                    suffix = suffix.Substring(0, ItmEncoder.MaxSuffixLength);

                sig.Append(kv.Key).Append('=').Append(suffix).Append(';');
                if (defs == null) defs = new List<ItmParamDef>();
                defs.Add(ItmParamDef.WithSuffix((byte)(0x80 | kv.Key), suffix));
            }

            string s = sig.ToString();
            if (s == _lastSlotDefsSig)
                return;   // unchanged — nothing to send

            if (defs == null)
            {
                _lastSlotDefsSig = s;   // nothing on the wire for this set — just record it
                return;
            }

            // Latch the signature only when the transport accepts the write — otherwise a
            // declined ParamDefs send would never be retried until the suffix set happens
            // to change again, leaving the display undecorated (blank/wrong suffixes).
            if (!_encoder.SetParamDefs(defs, _deviceId))
                return;
            _lastSlotDefsSig = s;
            _defTap2Defs = defs;                  // schedule the tight second tap
            _defTap2DueMs = now + DefDoubleTapMs;
            _log("ITM: ParamDefs sent — suffixes: " + s);
        }

        private enum SendOutcome { Sent, NothingToSend, Declined }

        // Encodes the subscribed values and sends them. force bypasses the change gate (used
        // by the post-sync paint and its double-tap); the periodic re-assert bypasses it too.
        private SendOutcome TrySendValues(GameData data, long now, bool force)
        {
            _valueBuf.Clear();
            var subs = _lifecycle.Subscriptions;
            // Same defensive MaxParams seam as UpdateSlotDefs (dormant on today's paths).
            AssertSubscriptionBudget(subs.Count);
            for (int i = 0; i < subs.Count; i++)
            {
                var kv = subs[i];
                if (_mapper.TryEncodeParam(kv.Value.ParamId, kv.Key, data, kv.Value.DataType, out var v))
                {
                    _valueBuf.Add(v);
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

            _lastSendOkMs = now;
            Remember(_valueBuf);

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

        /// <summary>
        /// Defensive degrade-visible assert at the ParamDefs/SendValues seam when the
        /// firmware announces more than <see cref="ItmEncoder.MaxParams"/> subscriptions.
        /// Dormant on today's paths: real pages are ≤16 by firmware construction and
        /// <see cref="ItmLifecycleController"/> only adopts the announced set.
        /// </summary>
        private void AssertSubscriptionBudget(int count)
        {
            if (count <= ItmEncoder.MaxParams || _warnedOverMaxParams)
                return;
            _warnedOverMaxParams = true;
            _log("ITM: firmware announced " + count + " subscriptions (cap "
                + ItmEncoder.MaxParams + ") — over-budget set is degrade-visible");
        }
    }
}
