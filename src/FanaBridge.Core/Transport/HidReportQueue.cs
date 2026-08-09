using System;
using System.Collections.Generic;
using System.Threading;

namespace FanaBridge.Core.Transport
{
    /// <summary>
    /// Bounded FIFO of HID input reports, bridging the blocking reader thread to
    /// its single owning consumer (one queue per <see cref="Col03Family"/>).
    /// <see cref="TryRead"/> never throws: it returns the report length, or -1
    /// when nothing arrives within the timeout (0 = check and return
    /// immediately). This is what keeps the idle per-frame drain exception-free —
    /// polling the HID handle directly with a 0 ms timeout makes HidSharp throw
    /// a TimeoutException per call, and SimHub's first-chance handler logs every
    /// throw as a full ERROR stack trace (one per frame, ~8 MB of log per minute).
    /// </summary>
    internal sealed class HidReportQueue : IReportStream
    {
        private readonly object _sync = new object();
        private readonly Queue<byte[]> _reports = new Queue<byte[]>();
        private readonly int _capacity;
        private bool _closed;

        // Non-consuming observers (see IReportStream.Tap). Copy-on-write so the
        // reader thread can snapshot without holding a lock during callbacks.
        private volatile Action<byte[]>[] _taps = Array.Empty<Action<byte[]>>();

        public HidReportQueue(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        /// <summary>
        /// Enqueues a private copy of the first <paramref name="length"/> bytes.
        /// When full, the oldest report is dropped: if the consumer stalls (e.g.
        /// the plugin manager is restarting between games), the freshest device
        /// state is what a late reader needs.
        /// </summary>
        public void Enqueue(byte[] report, int length)
        {
            if (report == null || length <= 0) return;
            if (length > report.Length) length = report.Length;
            var copy = new byte[length];
            Array.Copy(report, copy, length);
            lock (_sync)
            {
                if (_closed) return;
                if (_reports.Count >= _capacity) _reports.Dequeue();
                _reports.Enqueue(copy);
                Monitor.PulseAll(_sync);
            }

            // Observers get their own copy (the queued one belongs to the consumer)
            // and run outside the lock so a slow observer can't stall TryRead.
            var taps = _taps;
            for (int i = 0; i < taps.Length; i++)
            {
                var tapCopy = new byte[length];
                Array.Copy(report, tapCopy, length);
                try { taps[i](tapCopy); } catch { /* observer's problem, not the pump's */ }
            }
        }

        /// <summary>
        /// Dequeues the next report into <paramref name="destination"/>, waiting
        /// up to <paramref name="timeoutMs"/> for one to arrive. Returns the
        /// number of bytes copied, or -1 on empty timeout / after <see cref="Close"/>.
        /// </summary>
        public int TryRead(byte[] destination, int timeoutMs)
        {
            if (destination == null) return -1;
            lock (_sync)
            {
                if (_reports.Count == 0)
                {
                    if (_closed || timeoutMs <= 0) return -1;
                    // Environment.TickCount subtraction is wrap-safe
                    int start = Environment.TickCount;
                    int remaining = timeoutMs;
                    while (_reports.Count == 0)
                    {
                        if (_closed || remaining <= 0) return -1;
                        Monitor.Wait(_sync, remaining);
                        remaining = timeoutMs - (Environment.TickCount - start);
                    }
                }

                var report = _reports.Dequeue();
                int n = Math.Min(report.Length, destination.Length);
                Array.Copy(report, destination, n);
                return n;
            }
        }

        /// <summary>Drops any pending reports without closing the queue.</summary>
        public void Flush()
        {
            lock (_sync)
            {
                _reports.Clear();
            }
        }

        /// <summary>
        /// Registers a non-consuming observer of every enqueued report (its own
        /// private copy, invoked on the reader thread). Dispose the token to
        /// unregister. See <see cref="IReportStream.Tap"/>.
        /// </summary>
        public IDisposable Tap(Action<byte[]> observer)
        {
            if (observer == null) throw new ArgumentNullException(nameof(observer));
            lock (_sync)
            {
                var taps = _taps;
                var next = new Action<byte[]>[taps.Length + 1];
                Array.Copy(taps, next, taps.Length);
                next[taps.Length] = observer;
                _taps = next;
            }
            return new TapToken(this, observer);
        }

        private void RemoveTap(Action<byte[]> observer)
        {
            lock (_sync)
            {
                var taps = _taps;
                int idx = Array.IndexOf(taps, observer);
                if (idx < 0) return;
                var next = new Action<byte[]>[taps.Length - 1];
                Array.Copy(taps, 0, next, 0, idx);
                Array.Copy(taps, idx + 1, next, idx, taps.Length - idx - 1);
                _taps = next;
            }
        }

        private sealed class TapToken : IDisposable
        {
            private HidReportQueue _queue;
            private readonly Action<byte[]> _observer;

            public TapToken(HidReportQueue queue, Action<byte[]> observer)
            {
                _queue = queue;
                _observer = observer;
            }

            public void Dispose()
            {
                _queue?.RemoveTap(_observer);
                _queue = null;
            }
        }

        /// <summary>
        /// Drops queued reports, refuses further input, and wakes blocked
        /// readers (they return -1). A closed queue stays closed — Connect
        /// creates a fresh instance per session.
        /// </summary>
        public void Close()
        {
            lock (_sync)
            {
                _closed = true;
                _reports.Clear();
                Monitor.PulseAll(_sync);
            }
        }
    }
}
