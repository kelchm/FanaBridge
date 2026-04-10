using System.Collections.Generic;
using FanaBridge.Protocol;
using FanaBridge.SegmentDisplay;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests.SegmentDisplay
{
    public class HardwareSegmentDisplayTests
    {
        // ── Dedup ───────────────────────────────────────────────────

        [Fact]
        public void Send_FirstCall_WritesToHardware()
        {
            var (display, transport, _) = Create();

            display.Send(0x01, 0x02, 0x03);

            Assert.Single(transport.Col01Writes);
        }

        [Fact]
        public void Send_SameBytes_SkipsWrite()
        {
            var (display, transport, _) = Create();

            display.Send(0x01, 0x02, 0x03);
            display.Send(0x01, 0x02, 0x03);

            Assert.Single(transport.Col01Writes);
        }

        [Fact]
        public void Send_DifferentBytes_Writes()
        {
            var (display, transport, _) = Create();

            display.Send(0x01, 0x02, 0x03);
            display.Send(0x04, 0x05, 0x06);

            Assert.Equal(2, transport.Col01Writes.Count);
        }

        // ── Keepalive ───────────────────────────────────────────────

        [Fact]
        public void Keepalive_BeforeTimeout_NoWrite()
        {
            long nowMs = 0;
            var (display, transport, _) = Create(() => nowMs);

            display.Send(0x01, 0x02, 0x03);
            Assert.Single(transport.Col01Writes);

            nowMs = HardwareSegmentDisplay.KeepAliveMs - 1;
            display.Keepalive();

            Assert.Single(transport.Col01Writes); // no extra write
        }

        [Fact]
        public void Keepalive_AfterTimeout_ResendsLastFrame()
        {
            long nowMs = 0;
            var (display, transport, _) = Create(() => nowMs);

            display.Send(0x01, 0x02, 0x03);

            nowMs = HardwareSegmentDisplay.KeepAliveMs + 1;
            display.Keepalive();

            Assert.Equal(2, transport.Col01Writes.Count);
            // Second write should have same segment bytes as first
            Assert.Equal(transport.Col01Writes[0][5], transport.Col01Writes[1][5]);
            Assert.Equal(transport.Col01Writes[0][6], transport.Col01Writes[1][6]);
            Assert.Equal(transport.Col01Writes[0][7], transport.Col01Writes[1][7]);
        }

        [Fact]
        public void Keepalive_NoPriorSend_NoWrite()
        {
            var (display, transport, _) = Create();
            display.Keepalive();
            Assert.Empty(transport.Col01Writes);
        }

        // ── Dedup + staleness ───────────────────────────────────────

        [Fact]
        public void Send_SameBytesButStale_Writes()
        {
            long nowMs = 0;
            var (display, transport, _) = Create(() => nowMs);

            display.Send(0x01, 0x02, 0x03);

            nowMs = HardwareSegmentDisplay.KeepAliveMs + 1;
            display.Send(0x01, 0x02, 0x03); // same bytes but stale

            Assert.Equal(2, transport.Col01Writes.Count);
        }

        // ── Clear ───────────────────────────────────────────────────

        [Fact]
        public void Clear_WritesBlankToHardware()
        {
            var (display, transport, _) = Create();

            display.Clear();

            Assert.Single(transport.Col01Writes);
            // Blank segments are 0x00 (SevenSegment.Blank)
            Assert.Equal(0x00, transport.Col01Writes[0][5]);
            Assert.Equal(0x00, transport.Col01Writes[0][6]);
            Assert.Equal(0x00, transport.Col01Writes[0][7]);
        }

        // ── Helpers ─────────────────────────────────────────────────

        private static (HardwareSegmentDisplay display, SpyTransport transport, DisplayEncoder encoder)
            Create(System.Func<long> clock = null)
        {
            var transport = new SpyTransport();
            var encoder = new DisplayEncoder(transport);
            var display = new HardwareSegmentDisplay(encoder, clock ?? (() => 0));
            return (display, transport, encoder);
        }

        private class SpyTransport : IDeviceTransport
        {
            public List<byte[]> Col01Writes { get; } = new List<byte[]>();

            public bool IsConnected { get { return true; } }
            public int Col03MaxInputReportLength { get { return 64; } }

            public bool SendCol01(byte[] report)
            {
                Col01Writes.Add((byte[])report.Clone());
                return true;
            }

            public bool SendCol03(byte[] report) { return true; }
            public int ReadCol03(byte[] buffer, int timeoutMs) { return -1; }
            public System.IDisposable BeginBatch() { return new NoOpDisposable(); }

            private class NoOpDisposable : System.IDisposable
            {
                public void Dispose() { }
            }
        }
    }
}
