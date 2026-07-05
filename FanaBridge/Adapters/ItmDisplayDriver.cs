using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Protocol;
using GameReaderCommon;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Drives a Fanatec ITM telemetry display, firmware-driven: it enables ITM once and
    /// sends rate-limited value updates for exactly the parameters the firmware has
    /// subscribed. No keepalive is needed — the display has no idle timeout (hardware-
    /// confirmed); the value stream, or simply the last frame it holds, keeps it lit.
    ///
    /// This driver follows the wheel's subscription reports: when the ITM page changes, the
    /// firmware pushes subscription reports on col03-IN telling the host which parameter
    /// sits at which handle for the new page (see
    /// <see cref="ItmTelemetry.ParseSubscriptionReport"/>); the host echoes values back at
    /// those handles.
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

        // ── Tunables (ms) ────────────────────────────────────────────────
        /// <summary>Minimum spacing between value-update sends (caps the rate).</summary>
        public int ValueIntervalMs { get; set; } = 40;

        /// <summary>Whether to show the "/total laps" suffix on the lap field.</summary>
        public bool ShowLapTotal { get; set; } = true;

        /// <summary>Whether to show the "/field size" suffix on the position field.</summary>
        public bool ShowPositionTotal { get; set; } = true;

        /// <summary>
        /// The ITM page (wire page number) forced on bring-up; the wheel's display button
        /// navigates from there. Read once per bring-up. Defaults to page 1 (Lap Info).
        /// </summary>
        public byte DefaultPage { get; set; } = 1;

        /// <summary>
        /// Whether the ITM display is enabled. Set false to turn ITM off (the driver sends
        /// the firmware "ITM off" command and goes dormant); set true to re-enable (the
        /// next <see cref="Update"/> re-runs bring-up). Read live each frame.
        /// </summary>
        public bool Enabled { get; set; } = true;

        // ── State ────────────────────────────────────────────────────────
        private enum Phase { Idle, Enabling, Running, Disabled }
        private Phase _phase = Phase.Idle;

        private long _lastValuesMs;
        private byte _lastPageApplied;   // last page we forced via SetPage — edge-detects a settings change

        // Firmware-driven subscription map: host handle -> parameter ID. Kept sorted by
        // handle so the dirty-tracking comparison sees a stable order.
        private readonly SortedDictionary<byte, ushort> _subs = new SortedDictionary<byte, ushort>();
        private ItmValue[] _lastValues;
        private bool _loggedFirstValues;
        private string _lastSlotDefsSig = "";   // last ParamDefs suffix set, to skip redundant writes
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
        }

        private static Func<long> DefaultClock()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            return () => sw.ElapsedMilliseconds;
        }

        /// <summary>True once ITM is enabled and values are flowing.</summary>
        public bool IsRunning => _phase == Phase.Running;

        /// <summary>The number of parameters the firmware currently has subscribed.</summary>
        public int SubscriptionCount => _subs.Count;

        /// <summary>
        /// Begins ITM bring-up (a single Enable) on the next <see cref="Update"/>.
        /// Idempotent — a no-op unless idle, so it is safe to call every frame.
        /// </summary>
        public void Start()
        {
            if (_phase == Phase.Idle)
                _phase = Phase.Enabling;
        }

        /// <summary>
        /// Stops driving and returns to idle, dropping all subscriptions. A later
        /// <see cref="Start"/> re-enables.
        /// </summary>
        public void Stop()
        {
            _phase = Phase.Idle;
            ResetSession();
        }

        /// <summary>Drops all per-session state (subscriptions, dirty tracking, one-shot logs).</summary>
        private void ResetSession()
        {
            _subs.Clear();
            _lastValues = null;
            _loggedFirstValues = false;
            _lastSlotDefsSig = "";
            _wasTelemetryLive = false;
            _pendingExitReset = false;
        }

        // Game-exit tracking: values must never be painted from SimHub's stale
        // post-exit telemetry, and the fields already on the display would hold
        // their last values forever (no idle timeout). On the live→not-live
        // transition a DisplayReset reverts every field to its placeholder
        // (e.g. "--- / -", "--:--.-") — session, page, and subscriptions stay
        // put. The reset is retried across ticks until the transport accepts
        // it. (The Legacy ITM page is unaffected by the reset; its content is
        // cleared separately by the col01 display driver's exit blank.)
        private bool _wasTelemetryLive;
        private bool _pendingExitReset;

        /// <summary>
        /// Applies a firmware ITM subscription report (col03-IN, pushed on a wheel-button
        /// page change): subscribes/updates each handle's parameter, removes unsubscribed
        /// handles. Safe to call before <see cref="Start"/> — the map is simply pre-seeded.
        /// </summary>
        public void OnSubscriptionReport(byte[] report)
        {
            var subs = ItmTelemetry.ParseSubscriptionReport(report, report?.Length ?? 0, _deviceId);
            if (subs.Count == 0)
                return;

            foreach (var s in subs)
            {
                if (s.IsUnsubscribe)
                    _subs.Remove(s.Handle);
                else
                    _subs[s.Handle] = s.ParamId;
            }

            _lastValues = null;   // subscription set changed — force a fresh value send
            _log("ITM: subscriptions now — " + Describe());
            // ParamDefs (suffixes) are refreshed from Update(), where telemetry is
            // available for the dynamic "/total" suffixes.
        }

        // Sends ParamDefs declaring each subscribed param's display suffix — a static
        // unit (e.g. "C", "L") or a dynamic total ("/34" for lap/position). Cosmetic:
        // the value renders from ValueUpdate regardless. Slot ID = 0x80 | handle, per
        // the capture. Only writes when the suffix set actually changes (subscription
        // change or a moving total), so it does not flood the bus.
        private void UpdateSlotDefs(GameData data)
        {
            List<ItmParamDef> defs = null;
            var sig = new System.Text.StringBuilder();

            foreach (var kv in _subs)
            {
                string suffix;
                if (ItmTelemetryMapper.TryGetUnitSuffix(kv.Value, data, out suffix))
                {
                    // Static unit label (e.g. "C").
                }
                else if (ItmTelemetryMapper.IsTotalParam(kv.Value))
                {
                    // Lap/position/fuel: always emit an entry so a total that disappears is
                    // actively cleared — a zero-length suffix does NOT overwrite the firmware's
                    // default "/0", so we write a blank " " to clear it. Fuel is special: with no
                    // tank capacity it falls back to the unit label ("L"/"G") rather than a blank,
                    // so a bare fuel value still reads as fuel.
                    suffix = ShowTotalFor(kv.Value)
                          && ItmTelemetryMapper.TryGetTotalSuffix(kv.Value, data, out var total)
                        ? total
                        : (kv.Value == ItmParam.Fuel ? ItmTelemetryMapper.FuelUnitLabel(data) : " ");
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
            _lastSlotDefsSig = s;

            if (defs != null)
            {
                _encoder.SetParamDefs(defs, _deviceId);
                _log("ITM: ParamDefs sent — suffixes: " + s);
            }
        }

        private bool ShowTotalFor(ushort paramId)
        {
            if (paramId == ItmParam.Lap) return ShowLapTotal;
            if (paramId == ItmParam.Position) return ShowPositionTotal;
            if (paramId == ItmParam.Fuel) return true;   // fuel/capacity has no user toggle
            return false;
        }

        /// <summary>Drives the state machine one tick. Call once per frame while connected.</summary>
        public void Update(GameData data)
        {
            // User turned ITM off: send the firmware "ITM off" command, then stay
            // dormant (no values) so the display stays off, as the Fanatec software does.
            // Only latch Disabled once the off command was actually accepted — a
            // declined write is retried next tick. Re-enabling drops back into
            // bring-up below.
            if (!Enabled)
            {
                if (_phase != Phase.Disabled)
                {
                    if (!_encoder.SetItmMode(false))   // FF 05 02 00
                        return;
                    ResetSession();
                    _phase = Phase.Disabled;
                    _log("ITM: disabled by user — sent ITM off");
                }
                return;
            }

            // Enabled again after being disabled — re-arm bring-up.
            if (_phase == Phase.Disabled)
                _phase = Phase.Enabling;

            if (_phase == Phase.Idle)
                return;

            long now = _now();
            bool telemetryLive = data != null && data.GameRunning && data.NewData != null;

            if (_phase == Phase.Enabling)
            {
                // Bring-up: gate ITM on (FF 05 02 01), start the session (FF 02 02 00), then force
                // the configured default page (FF 05 04 <dev> <page>) so the display matches our
                // seed and shows correct values right away. The wheel button navigates from there;
                // detecting the wheel's current page on cold start instead is deferred — see #43.
                _encoder.SetItmMode(true);              // FF 05 02 01 — firmware ITM gate on
                _encoder.EnableItm();                   // FF 02 02 00 — start the display session
                _encoder.SetPage(_deviceId, DefaultPage);  // force the configured default page
                _lastPageApplied = DefaultPage;
                SeedInitialSubscriptions();             // the default page's params
                _log("ITM: enabled — seeded " + _subs.Count + " params, following firmware subscriptions");
                _phase = Phase.Running;
                // Record liveness on this tick too — a game exiting right after a
                // single live frame must still trigger the exit reset below.
                _wasTelemetryLive = telemetryLive;
                return;
            }

            // Running

            // Game just exited (or an earlier exit reset is still pending): DisplayReset
            // reverts every field to its placeholder rendering so the last telemetry
            // frame doesn't sit on the display forever. The session, page, and firmware
            // subscriptions stay put — values simply flow again when the next game
            // starts. An ITM off→on cycle does NOT clear fields (hardware-verified:
            // the firmware retains their values), so this reset is the one command
            // that does. Retried until the transport accepts it.
            if (_pendingExitReset || (_wasTelemetryLive && !telemetryLive))
            {
                _pendingExitReset = true;
                if (!_encoder.ResetDisplay())      // declined — retry next tick
                    return;
                _pendingExitReset = false;
                _wasTelemetryLive = false;
                _lastValues = null;                // repaint everything when telemetry returns
                _log("ITM: game exited — display reset (fields back to placeholders)");
                return;
            }
            _wasTelemetryLive = telemetryLive;

            // If the user changed the default page in settings, switch the display to it live
            // (edge-triggered, so we don't fight the wheel button between changes). Runs in
            // idle too — this is the settings panel's live preview.
            if (DefaultPage != _lastPageApplied)
            {
                _encoder.SetPage(_deviceId, DefaultPage);
                _lastPageApplied = DefaultPage;
                // Deliberately DON'T reseed here — keep the current subscriptions until the firmware
                // pushes the new page's set. Unlike bring-up (which has no prior state), a live switch
                // has valid subs to lose: if the PageSet flakes, or targets the page already shown (no
                // push comes back), a speculative reseed would strand the display on wrong-page handles
                // with nothing to correct it. The existing subs stay valid for whatever is displayed.
                _log("ITM: default page changed — forcing page " + DefaultPage);
            }

            // Values only flow while a game is feeding telemetry: SimHub keeps the last
            // telemetry values around after a game exits, so painting from stale data
            // would resurrect exactly the frozen frame the exit reset just cleared.
            if (!telemetryLive)
                return;

            UpdateSlotDefs(data);   // refresh unit/total suffixes when they change
            if (now - _lastValuesMs >= ValueIntervalMs)
            {
                SendSubscribedValues(data);
                _lastValuesMs = now;
            }
        }

        // Bring-up forces the default page via SetPage(device, DefaultPage), which makes the
        // firmware push that page's subscription — but that push takes ~tens of ms to arrive, and
        // a bare Enable announces nothing. Pre-seed the default page's handle→param map so values
        // flow in that gap; the firmware's push then confirms/replaces it. Seeds only when empty.
        private void SeedInitialSubscriptions()
        {
            if (_subs.Count > 0)
                return;
            var ids = SeedParamsForPage(DefaultPage);
            for (int h = 0; h < ids.Count; h++)
                _subs[(byte)h] = ids[h];
        }

        // The params the given page carries on this driver's device (empty for an unknown page
        // or the legacy page). Used only to seed handles 0..N-1 before the firmware's push lands.
        private IReadOnlyList<ushort> SeedParamsForPage(byte page)
        {
            foreach (var p in ItmDeviceCatalog.PagesFor(_deviceId))
                if (p.Number == page)
                    return p.Params;
            return Array.Empty<ushort>();
        }

        private void SendSubscribedValues(GameData data)
        {
            if (_subs.Count == 0)
                return;

            _valueBuf.Clear();
            foreach (var kv in _subs)
            {
                if (ItmTelemetryMapper.TryEncodeParam(kv.Value, kv.Key, data, out var v))
                    _valueBuf.Add(v);
                else if (_unencodableWarned.Add(kv.Value))
                    // Firmware subscribed a parameter outside our page layouts — it will
                    // render as dashes. Note it once so the gap is diagnosable.
                    _log("ITM: no encoder for subscribed param " + kv.Value +
                         " (handle " + kv.Key + ") — field will show dashes");
            }

            if (_valueBuf.Count == 0 || !HasChanged(_valueBuf))
                return;

            // Only record the values as last-sent (and log the first update) when the send
            // actually succeeded — a transport failure must not suppress the retry, or the
            // display would stay stale until a value changes.
            if (!_encoder.SendValues(_valueBuf, _deviceId))
                return;

            Remember(_valueBuf);

            if (!_loggedFirstValues)
            {
                _loggedFirstValues = true;
                _log("ITM: first value update — " + _valueBuf.Count + " params: " + Describe());
            }
        }

        private string Describe()
            => string.Join(" ", _subs.Select(kv => "h" + kv.Key + "=p" + kv.Value));

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
