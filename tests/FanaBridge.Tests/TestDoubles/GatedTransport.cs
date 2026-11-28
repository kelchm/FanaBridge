using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    /// <summary>
    /// Runs a call that may contend for a lock, and waits until it has either
    /// finished or blocked. A concurrency test then knows the call has reached
    /// its decisive point before the test releases the other side: unguarded
    /// code has already run to completion, guarded code is waiting on the lock.
    /// </summary>
    internal sealed class ContendedCall
    {
        // How long the call must stay blocked before it counts as waiting on
        // the lock, so a brief runtime-internal wait isn't mistaken for it.
        private static readonly TimeSpan BlockedFor = TimeSpan.FromMilliseconds(50);

        private readonly Thread _thread;
        private readonly ManualResetEventSlim _started = new ManualResetEventSlim();
        private Exception? _error;

        private ContendedCall(Action action)
        {
            _thread = new Thread(() =>
            {
                _started.Set();
                // Captured rather than left unhandled: on a raw thread that would
                // take down the test host instead of failing the test.
                try { action(); }
                catch (Exception ex) { _error = ex; }
            }) { IsBackground = true };
        }

        public bool IsAlive => _thread.IsAlive;

        /// <summary>Waits for the call to finish and rethrows anything it threw.</summary>
        public bool Join(TimeSpan timeout)
        {
            if (!_thread.Join(timeout))
                return false;
            if (_error != null)
                throw new InvalidOperationException("the contended call threw", _error);
            return true;
        }

        public static ContendedCall StartAndWaitUntilBlockedOrDone(Action action, TimeSpan timeout)
        {
            var call = new ContendedCall(action);
            call._thread.Start();
            if (!call._started.Wait(timeout))
                throw new TimeoutException("the call never started");

            var deadline = Stopwatch.StartNew();
            var blocked = new Stopwatch();
            while (call._thread.IsAlive)
            {
                if (deadline.Elapsed > timeout)
                    throw new TimeoutException("the call neither finished nor blocked");

                if ((call._thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0)
                {
                    if (!blocked.IsRunning) blocked.Start();
                    else if (blocked.Elapsed >= BlockedFor) break;
                }
                else
                {
                    blocked.Reset();
                }
                Thread.Sleep(1);
            }
            return call;
        }
    }
}
