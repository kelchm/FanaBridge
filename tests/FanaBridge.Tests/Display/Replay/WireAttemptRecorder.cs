using System;
using System.Collections.Generic;
using FanaBridge.Protocol;
using FanaBridge.Transport;

namespace FanaBridge.Tests.Display.Replay
{
    /// <summary>
    /// Recording <see cref="IConnectableTransport"/> that captures every attempted
    /// SendCol01/SendCol03 at the transport seam (seam-map §4.3), including declined
    /// sends. Payload buffers are defensively copied (encoders reuse report buffers).
    /// </summary>
    internal sealed class WireAttemptRecorder : IConnectableTransport
    {
        private readonly List<WireAttempt> _attempts = new List<WireAttempt>();
        private readonly object _gate = new object();
        private Func<long> _now = () => 0L;
        private int _frameIndex;
        private int _seqInFrame;
        private bool _acceptCol01 = true;
        private bool _acceptCol03 = true;

        public bool Connected { get; private set; }
        public FakeReportStream Identity { get; } = new FakeReportStream();
        public FakeReportStream Itm { get; } = new FakeReportStream();

        public IReadOnlyList<WireAttempt> Attempts
        {
            get { lock (_gate) return _attempts.ToArray(); }
        }

        public void BindClock(Func<long> now) => _now = now ?? (() => 0L);

        /// <summary>Begin a new DataUpdate frame (resets per-frame ordinal).</summary>
        public void BeginFrame(int frameIndex)
        {
            lock (_gate)
            {
                _frameIndex = frameIndex;
                _seqInFrame = 0;
            }
        }

        public void SetAccepts(bool col01, bool col03)
        {
            _acceptCol01 = col01;
            _acceptCol03 = col03;
        }

        public void SetAccepts(bool both) => SetAccepts(both, both);

        public bool Connect(int productId)
        {
            Connected = true;
            return true;
        }

        public void Disconnect() => Connected = false;
        public void Dispose() => Disconnect();
        public bool IsConnected => Connected;
        public bool IsDevicePresent => Connected;
        public FanatecTransport.TransportConnectStatus LastConnectStatus =>
            FanatecTransport.TransportConnectStatus.Connected;

        public bool SendCol03(byte[] data) => Record(Chan.Col03, data, _acceptCol03);
        public bool SendCol01(byte[] data) => Record(Chan.Col01, data, _acceptCol01);

        private bool Record(Chan channel, byte[] data, bool accept)
        {
            var copy = new byte[data.Length];
            Array.Copy(data, copy, data.Length);
            lock (_gate)
            {
                _attempts.Add(new WireAttempt(
                    _frameIndex,
                    _seqInFrame++,
                    _now(),
                    channel,
                    copy,
                    accept));
            }
            return accept;
        }

        public IReportStream IdentityReports => Identity;
        public IReportStream ItmReports => Itm;
        public IReportStream SrmReports => FakeReportStream.Empty;
        public IReportStream TuningReports => FakeReportStream.Empty;
        public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
        public int Col03MaxInputReportLength => 64;
        public int Col01MaxInputReportLength => 34;
        public IDisposable BeginBatch() => new NoOp();

        private sealed class NoOp : IDisposable
        {
            public void Dispose() { }
        }
    }
}
