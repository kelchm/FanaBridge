using System;
using System.Collections.Generic;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests.Protocol
{
    public class ReportElicitTests
    {
        // Transport stub whose stream answers scripted responses when a request
        // is SENT (mirroring a device replying to the request, after the elicit's
        // stale-flush has already run).
        private sealed class StubTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public int Col03MaxInputReportLength => 64;
            public int Col01MaxInputReportLength => 34;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public bool SendCol01(byte[] data) => true;

            public FakeReportStream Stream { get; } = new FakeReportStream();
            public IReportStream IdentityReports => FakeReportStream.Empty;
            public IReportStream ItmReports => FakeReportStream.Empty;
            public IReportStream SrmReports => FakeReportStream.Empty;
            public IReportStream TuningReports => Stream;

            public List<byte[]> Sent { get; } = new List<byte[]>();
            public List<int> SentAtBatchDepth { get; } = new List<int>();
            public Queue<byte[]> RespondOnSend { get; } = new Queue<byte[]>();
            public bool FailSends { get; set; }

            private int _batchDepth;

            public bool SendCol03(byte[] data)
            {
                if (FailSends) return false;
                Sent.Add((byte[])data.Clone());
                SentAtBatchDepth.Add(_batchDepth);
                while (RespondOnSend.Count > 0)
                    Stream.Enqueue(RespondOnSend.Dequeue());
                return true;
            }

            public IDisposable BeginBatch()
            {
                _batchDepth++;
                return new Scope(this);
            }

            private sealed class Scope : IDisposable
            {
                private StubTransport? _t;
                public Scope(StubTransport t) { _t = t; }
                public void Dispose() { if (_t != null) { _t._batchDepth--; _t = null; } }
            }
        }

        private static byte[] FrameOf(params byte[] head)
        {
            var b = new byte[64];
            Array.Copy(head, b, head.Length);
            return b;
        }

        private static readonly Func<byte[], int, bool> MatchMarker7 =
            (f, n) => n > 0 && f[0] == 7;

        [Fact]
        public void Elicit_ReturnsMatchedResponse()
        {
            var t = new StubTransport();
            t.RespondOnSend.Enqueue(FrameOf(7, 0xAA));

            var buf = new byte[64];
            int n = ReportElicit.Elicit(t, t.Stream, new[] { FrameOf(1) }, MatchMarker7, 100, buf);

            Assert.Equal(64, n);
            Assert.Equal(0xAA, buf[1]);
            Assert.Single(t.Sent);
        }

        [Fact]
        public void Elicit_FlushesStaleFramesBeforeSending()
        {
            var t = new StubTransport();
            t.Stream.Enqueue(FrameOf(7, 0x01)); // stale frame that WOULD match
            t.RespondOnSend.Enqueue(FrameOf(7, 0x02)); // the real response

            var buf = new byte[64];
            int n = ReportElicit.Elicit(t, t.Stream, new[] { FrameOf(1) }, MatchMarker7, 100, buf);

            Assert.Equal(1, t.Stream.FlushCount);
            Assert.Equal(64, n);
            Assert.Equal(0x02, buf[1]); // got the fresh response, not the stale one
        }

        [Fact]
        public void Elicit_SkipsNonMatchingFrames()
        {
            var t = new StubTransport();
            t.RespondOnSend.Enqueue(FrameOf(9)); // wrong frame on our own stream
            t.RespondOnSend.Enqueue(FrameOf(7)); // the match

            var buf = new byte[64];
            int n = ReportElicit.Elicit(t, t.Stream, new[] { FrameOf(1) }, MatchMarker7, 100, buf);

            Assert.Equal(64, n);
            Assert.Equal(7, buf[0]);
        }

        [Fact]
        public void Elicit_NoResponse_ReturnsMinusOne()
        {
            var t = new StubTransport();

            var buf = new byte[64];
            int n = ReportElicit.Elicit(t, t.Stream, new[] { FrameOf(1) }, MatchMarker7, 100, buf);

            Assert.Equal(-1, n);
        }

        [Fact]
        public void Elicit_SendFailure_ReturnsMinusOne()
        {
            var t = new StubTransport { FailSends = true };

            var buf = new byte[64];
            int n = ReportElicit.Elicit(t, t.Stream, new[] { FrameOf(1) }, MatchMarker7, 100, buf);

            Assert.Equal(-1, n);
        }

        [Fact]
        public void Elicit_SendsAllRequestsInOrder_UnderOneBatch()
        {
            var t = new StubTransport();
            t.RespondOnSend.Enqueue(FrameOf(7));

            var buf = new byte[64];
            ReportElicit.Elicit(t, t.Stream, new[] { FrameOf(1), FrameOf(2) }, MatchMarker7, 100, buf);

            Assert.Equal(2, t.Sent.Count);
            Assert.Equal(1, t.Sent[0][0]);
            Assert.Equal(2, t.Sent[1][0]);
            Assert.All(t.SentAtBatchDepth, depth => Assert.Equal(1, depth));
        }
    }
}
