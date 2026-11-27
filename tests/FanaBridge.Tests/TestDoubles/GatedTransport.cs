using System;
using System.Collections.Generic;
using System.Threading;
using FanaBridge.Core.Transport;

namespace FanaBridge.Tests.TestDoubles
{
    /// <summary>
    /// Transport fake for forcing an interleaving between two encoder callers.
    /// Write number <see cref="GateOnWrite"/> (1-based, counted across col01 and
    /// col03) signals <see cref="Entered"/> and blocks inside the send until
    /// <see cref="Release"/> is called. It then records the caller's buffer as it
    /// stands at that moment — what a real transport would put on the wire if
    /// another caller had rewritten the buffer in the meantime.
    /// </summary>
    internal sealed class GatedTransport : IDeviceTransport
    {
        private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

        private readonly object _lock = new object();
        private readonly List<byte[]> _writes = new List<byte[]>();
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim();
        private int _count;

        public int GateOnWrite { get; set; }
        public ManualResetEventSlim Entered { get; } = new ManualResetEventSlim();

        /// <summary>The gated write as it was finally sent.</summary>
        public byte[] GatedWrite { get; private set; } = Array.Empty<byte>();

        /// <summary>Every write in the order the transport accepted it.</summary>
        public byte[][] Writes { get { lock (_lock) { return _writes.ToArray(); } } }

        public void Release() => _release.Set();

        private bool Send(byte[] data)
        {
            bool gated = Interlocked.Increment(ref _count) == GateOnWrite;
            if (gated)
            {
                Entered.Set();
                _release.Wait(GateTimeout);
            }

            var copy = (byte[])data.Clone();
            lock (_lock)
            {
                _writes.Add(copy);
                if (gated) GatedWrite = copy;
            }
            return true;
        }

        public bool IsConnected => true;
        public bool SendCol01(byte[] data) => Send(data);
        public bool SendCol03(byte[] data) => Send(data);
        public IReportStream IdentityReports => FakeReportStream.Empty;
        public IReportStream ItmReports => FakeReportStream.Empty;
        public IReportStream SrmReports => FakeReportStream.Empty;
        public IReportStream TuningReports => FakeReportStream.Empty;
        public int Col03MaxInputReportLength => 64;
        public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
        public int Col01MaxInputReportLength => 34;
        public IDisposable BeginBatch() => new NoOp();

        private sealed class NoOp : IDisposable { public void Dispose() { } }
    }
}
