using System;
using System.Collections.Generic;
using System.Threading;

namespace FanaBridge.Transport
{
    /// <summary>
    /// Bounded FIFO of HID input reports, bridging a blocking reader thread to
    /// the per-frame consumers. <see cref="TryRead"/> never throws: it returns
    /// the report length, or -1 when nothing arrives within the timeout
    /// (0 = check and return immediately). This is what keeps the idle
    /// per-frame drain exception-free — polling the HID handle directly with a
    /// 0 ms timeout makes HidSharp throw a TimeoutException per call, and
    /// SimHub's first-chance handler logs every throw as a full ERROR stack
    /// trace (one per frame, ~8 MB of log per minute).
    /// </summary>
    internal sealed class HidReportQueue
    {
        private readonly object _sync = new object();
        private readonly Queue<byte[]> _reports = new Queue<byte[]>();
        private readonly int _capacity;
        private bool _closed;

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
