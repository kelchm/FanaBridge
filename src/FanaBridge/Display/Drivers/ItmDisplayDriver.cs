using System;
using System.Collections.Generic;
using FanaBridge.Core.Display.Protocol;
using FanaBridge.Core.Display.Session;
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

        /// <summary>Whether to show the "/total laps" suffix on the lap field.</summary>
        public bool ShowLapTotal { get; set; } = true;

        /// <summary>Whether to show the "/field size" suffix on the position field.</summary>
        public bool ShowPositionTotal { get; set; } = true;

        /// <summary>
        /// The ITM page (wire page number) targeted by bring-up; the wheel's display button
        /// navigates from there. Changing it live requests a confirmed page switch.
        /// </summary>
        public byte DefaultPage { get; set; } = 1;

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
        /// Begins the ITM lifecycle (cold bring-up) on the next <see cref="Update"/>.
        /// Idempotent — a no-op unless idle, so it is safe to call every frame.
        /// </summary>
        public void Start()
        {
            _lifecycle.DefaultPage = DefaultPage;
            _lifecycle.Start();
        }

        /// <summary>
        /// Stops driving and returns to idle (connection lost), dropping all subscriptions.
        /// A later <see cref="Start"/> re-runs the cold bring-up.
        /// </summary>
        public void Stop()
        {
            _lifecycle.Stop();
            ResetSendState();
        }

        /// <summary>
        /// A wheel/hub/module change from the identity layer. A hot-swap resets the display
        /// cold without any trace on the ITM channel itself — the lifecycle restarts from
        /// bring-up and nothing is sent until the fresh push confirms.
        /// </summary>
        public void OnWheelChanged()
        {
            // The cold entry runs immediately — make sure it targets the current setting.
            _lifecycle.DefaultPage = DefaultPage;
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

            // Settings flow into the lifecycle: the default page (cold-entry target) and the
            // user's on/off switch. A settings change of the default page is edge-detected and
            // requested live, so the wheel button isn't fought between changes.
            _lifecycle.DefaultPage = DefaultPage;
            _lifecycle.SetUserEnabled(Enabled);
            if (_lastRequestedPage == null)
                _lastRequestedPage = DefaultPage;
            else if (DefaultPage != _lastRequestedPage.Value)
            {
                _lastRequestedPage = DefaultPage;
                _lifecycle.RequestPage(DefaultPage);
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
            var subs = _lifecycle.Subscriptions;
            for (int i = 0; i < subs.Count; i++)
            {
                var kv = subs[i];
                if (ItmTelemetryMapper.TryEncodeParam(kv.Value.ParamId, kv.Key, data, kv.Value.DataType, out var v))
                    _valueBuf.Add(v);
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
    }
}
