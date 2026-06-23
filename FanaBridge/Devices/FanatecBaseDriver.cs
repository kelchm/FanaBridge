using System;
using System.Collections.Generic;
using FanaBridge.Protocol;
using FanaBridge.Transport;

namespace FanaBridge.Devices
{
    /// <summary>
    /// The Fanatec wheelbase driver: the FF 08 system-report exchange + base/wheel/
    /// module decode + settle, lifted verbatim from <c>FanatecWheelbase</c>. Owns the
    /// wire/timing pieces (<see cref="SystemReportReader"/>, <see cref="IdentitySettler"/>)
    /// and projects a settled <see cref="DeviceSnapshot"/> (a Base device hosting a
    /// Wheel/Hub and optional Module). Identity is enable-once-then-listen: a single
    /// enable on connect, then a non-blocking drain of pushed reports each
    /// <see cref="Service"/>; a changed reading is committed only once it settles.
    /// </summary>
    internal sealed class FanatecBaseDriver : IDeviceDriver
    {
        // Keep the push subscription alive; the firmware's enable can lapse.
        private const int IdentitySettleMs = 200;
        private const int IdentityReEnableMs = 10000;

        private readonly IDeviceTransport _io;
        private readonly SystemReportReader _reportReader = new SystemReportReader();
        private readonly IdentitySettler _settler = new IdentitySettler(IdentitySettleMs);
        private readonly Func<long> _now;
        private readonly Action<SystemReportReader.Reading> _ingest;

        private long _lastEnableMs;
        private long _drainNow;

        // Most recent drained reading; decoded into the snapshot on commit.
        private SystemReportReader.Reading _lastReading;

        // The projected snapshot. Stable/LastRawReport update outside of a commit
        // (every tick / every reading respectively); identity fields update on commit.
        private DeviceSnapshot _snapshot = new DeviceSnapshot { Stable = true };

        /// <summary>Fired when a settled identity change is committed to <see cref="Snapshot"/>.</summary>
        public event Action<IDeviceDriver> SnapshotChanged;

        /// <param name="io">The transport this driver reads identity through.</param>
        /// <param name="nowMs">
        /// Millisecond clock for the enable cadence + settle window. Defaults to a
        /// real <see cref="System.Diagnostics.Stopwatch"/>; injected by tests so settle
        /// timing is deterministic (the settler is already time-injected).
        /// </param>
        public FanatecBaseDriver(IDeviceTransport io, Func<long> nowMs = null)
        {
            _io = io ?? throw new ArgumentNullException(nameof(io));
            _ingest = OnDrainReading;
            if (nowMs != null)
            {
                _now = nowMs;
            }
            else
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                _now = () => clock.ElapsedMilliseconds;
            }
        }

        public DeviceClass Class => DeviceClass.Base;

        public bool IsConnected => _io.IsConnected;

        public DeviceSnapshot Snapshot => _snapshot;

        /// <summary>
        /// Enable the firmware's push-on-change and read the initial identity once.
        /// Best-effort: a base is still adopted if the initial read fails (it will
        /// push on the next attachment change). Also runs the one-time non-blocking
        /// drain self-check. Called by the probe at bind time.
        /// </summary>
        public void Initialize()
        {
            try
            {
                _lastEnableMs = _now();
                if (_reportReader.ReadInitial(_io, out var reading))
                    IngestReading(reading, _now());

                // Confirm the per-frame drain is non-blocking on this hardware — a
                // blocking ReadCol03(buf, 0) would stall the frame thread. Done once
                // now that the input is quiet; leaves a permanent regression guard.
                double drainMs = _reportReader.ProbeDrainLatencyMs(_io, 5);
                if (drainMs > 1.0)
                    SimHub.Logging.Current.Warn(string.Format(
                        "FanatecBaseDriver: idle identity drain took {0:F2} ms — expected non-blocking (<1 ms); per-frame identity polling may stutter.", drainMs));
                else
                    SimHub.Logging.Current.Info(string.Format(
                        "FanatecBaseDriver: identity drain is non-blocking ({0:F2} ms idle)", drainMs));
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("FanatecBaseDriver: Initial identity read failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Drain pushed FF 08 reports and advance the settle timer. The base pushes on
        /// every attachment change (no polling), so this is a cheap non-blocking drain
        /// that only commits once a change has settled. Returns true on a new commit.
        /// </summary>
        public bool Service()
        {
            if (!IsConnected)
                return false;

            long now = _now();

            // Keep the push-on-change subscription alive (the enable can lapse).
            if (now - _lastEnableMs >= IdentityReEnableMs)
            {
                _lastEnableMs = now;
                try { _reportReader.Enable(_io); }
                catch { /* transient; the next tick retries */ }
            }

            _drainNow = now;
            _reportReader.DrainPushes(_io, _ingest);

            bool changed = _settler.Tick(now, out _, out _);
            _snapshot.Stable = _settler.IsStable;
            if (changed)
                CommitIdentity();
            return changed;
        }

        // Drain callback: record the reading and offer it to the settler. _drainNow
        // carries the current clock so the cached delegate needs no per-frame closure.
        private void OnDrainReading(SystemReportReader.Reading r) => IngestReading(r, _drainNow);

        private void IngestReading(SystemReportReader.Reading r, long now)
        {
            _lastReading = r;
            _snapshot.LastRawReport = r.Raw;   // retain per-reading, even before/without a settled commit
            byte effModule = FanatecIdentity.IsHub(r.Wire) ? r.ModRaw : (byte)0;
            _settler.Offer(r.Wire, effModule, now);
        }

        // Project the latest (settled) reading into the snapshot. Mirrors the old
        // FanatecWheelbase.CommitIdentity decode exactly:
        //   wire 0x18 → wheel/hub (+ IsHub), module 0x1F only meaningful on a hub,
        //   BaseType 0x02 → base code.
        private void CommitIdentity()
        {
            byte wire = _lastReading.Wire;
            bool isHub = FanatecIdentity.IsHub(wire);

            var attachments = new List<Attachment>(2);
            if (wire != 0)
            {
                attachments.Add(new Attachment
                {
                    Kind = isHub ? PeripheralKind.Hub : PeripheralKind.Wheel,
                    Code = FanatecIdentity.DecodeCode(wire),
                    WireCode = wire,
                });
            }
            if (isHub)
            {
                // 0x1F is only meaningful on a hub. Carried even when 0/unrecognized so
                // an unmapped module is still reportable ("please report").
                attachments.Add(new Attachment
                {
                    Kind = PeripheralKind.Module,
                    Code = FanatecIdentity.DecodeModule(_lastReading.ModRaw),
                    WireCode = _lastReading.ModRaw,
                });
            }

            _snapshot = new DeviceSnapshot
            {
                Class = DeviceClass.Base,
                Code = FanatecIdentity.DecodeBaseCode(_lastReading.BaseType),
                BaseTypeByte = _lastReading.BaseType,
                Stable = _settler.IsStable,
                HasIdentity = _lastReading.BaseType != 0,
                Attachments = attachments.ToArray(),
                LastRawReport = _lastReading.Raw,
            };

            SnapshotChanged?.Invoke(this);
        }

        public void Dispose()
        {
            // The transport is owned by the carrier, not the driver — only drop our
            // own state here.
            _settler.Reset();
            _lastReading = default;
            _snapshot = new DeviceSnapshot { Stable = true };
        }
    }
}
