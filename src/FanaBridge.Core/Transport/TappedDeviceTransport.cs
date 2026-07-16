using System;

namespace FanaBridge.Transport
{
    /// <summary>
    /// Receives a byte-for-byte copy of every col03 OUT report the tapped
    /// transport <b>accepted</b> (the underlying send returned success). This is the
    /// wire-driven digital twin's feed: a passive observer that consumes exactly what
    /// went out, never the host's intent. The frame passed in is a fresh copy owned by
    /// the observer — the transport retains no reference to it.
    ///
    /// Purity contract (the observer MUST honour it): do not block, do not throw
    /// (exceptions are caught and isolated by <see cref="TappedDeviceTransport"/>, but a
    /// throwing observer still loses that frame), and never call back into the transport.
    /// The observer holds no transport reference and has no way to emit a frame — the
    /// tap is one-directional by construction.
    /// </summary>
    public interface ICol03SendObserver
    {
        /// <summary>
        /// Invoked synchronously on the sending thread after an accepted <c>SendCol03</c>,
        /// with a private copy of the report bytes. For the ITM encoder this is the
        /// SimHub DataUpdate thread (the same thread that composes the frame), so the
        /// hand-off is a plain call — no queue, no cross-thread marshalling.
        /// </summary>
        void OnCol03Sent(byte[] frame);
    }

    /// <summary>
    /// An <see cref="IDeviceTransport"/> decorator that taps accepted col03 sends and
    /// hands byte copies to an optional observer. Wrapped around the transport at the
    /// single <c>new ItmEncoder(...)</c> construction site so that <b>all</b> ITM OUT
    /// traffic — driver value updates and lifecycle bring-up alike, which share that one
    /// encoder — passes the tap. It deliberately does <b>not</b> wrap the shared
    /// <see cref="FanatecTransport"/> itself, which would drag in LED/tuning/engage
    /// traffic the twin has no business seeing.
    ///
    /// Every member other than <see cref="SendCol03"/> is a transparent pass-through, so
    /// the encoder cannot tell it is wrapped. Observer purity is enforced structurally:
    /// <list type="bullet">
    /// <item>The tap fires only <b>after</b> the inner send returns success — a twin fed
    /// attempted sends would diverge from the hardware exactly when the transport is
    /// flaky, the moment a diagnostic twin matters most.</item>
    /// <item>Bytes are copied <b>synchronously</b> before the observer sees them: the
    /// encoder reuses a pooled report buffer, so a retained reference would be mutated by
    /// the next frame.</item>
    /// <item>Observer exceptions are caught and isolated — a faulty observer never fails
    /// a send (the wheel must keep working regardless of the twin).</item>
    /// <item>When no observer is attached the send path is a single null check with zero
    /// allocation, so an unattached tap costs nothing.</item>
    /// </list>
    /// </summary>
    public sealed class TappedDeviceTransport : IDeviceTransport
    {
        private readonly IDeviceTransport _inner;
        private readonly Action<string> _warn;

        // Volatile: attach/detach may run on a different thread than SendCol03. A single
        // reference read on the send path is the whole fast path when unattached.
        private volatile ICol03SendObserver _observer;

        // Latch so a persistently-throwing observer produces one warning, not a per-frame
        // log flood on the DataUpdate thread.
        private bool _observerFaulted;

        public TappedDeviceTransport(IDeviceTransport inner, Action<string> warn = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _warn = warn;
        }

        /// <summary>
        /// Attaches the observer that receives copies of accepted col03 sends. Replaces any
        /// previous observer. Passing null detaches (equivalent to <see cref="DetachObserver"/>).
        /// </summary>
        public void AttachObserver(ICol03SendObserver observer)
        {
            _observer = observer;
            _observerFaulted = false;
        }

        /// <summary>Detaches the observer; the send path returns to its zero-cost fast path.</summary>
        public void DetachObserver() => _observer = null;

        /// <summary>
        /// Detaches <paramref name="observer"/> only if it is the one currently attached.
        /// This one tap is shared by every device instance bound to the same hardware
        /// core, so a torn-down instance must never unhook a different instance's twin —
        /// which would happen on a wheel-type swap if the new instance attached before
        /// the old one detached. Identity-guarded detach makes teardown order-independent.
        /// </summary>
        public void DetachObserver(ICol03SendObserver observer)
        {
            if (ReferenceEquals(_observer, observer))
                _observer = null;
        }

        public bool SendCol03(byte[] data)
        {
            bool ok = _inner.SendCol03(data);

            // Tap AFTER success only, and only when someone is listening.
            if (ok)
            {
                var observer = _observer;
                if (observer != null && data != null)
                {
                    try
                    {
                        var copy = new byte[data.Length];
                        Array.Copy(data, copy, data.Length);
                        observer.OnCol03Sent(copy);
                    }
                    catch (Exception ex)
                    {
                        if (!_observerFaulted)
                        {
                            _observerFaulted = true;
                            _warn?.Invoke("FanaBridge: ITM frame observer threw; further faults suppressed: " + ex.Message);
                        }
                    }
                }
            }

            return ok;
        }

        // ── Transparent pass-through of every other transport member ──────────

        public bool IsConnected => _inner.IsConnected;
        public IReportStream IdentityReports => _inner.IdentityReports;
        public IReportStream ItmReports => _inner.ItmReports;
        public IReportStream SrmReports => _inner.SrmReports;
        public IReportStream TuningReports => _inner.TuningReports;
        public int Col03MaxInputReportLength => _inner.Col03MaxInputReportLength;
        public bool SendCol01(byte[] data) => _inner.SendCol01(data);
        public int ReadCol01(byte[] buffer, int timeoutMs) => _inner.ReadCol01(buffer, timeoutMs);
        public int Col01MaxInputReportLength => _inner.Col01MaxInputReportLength;
        public IDisposable BeginBatch() => _inner.BeginBatch();
    }
}
