using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FanaBridge.Core.Leds;
using FanaBridge.Tests.TestDoubles;
using FanaBridge.Core.Transport;
using Xunit;

namespace FanaBridge.Tests.Core.Leds
{
    public class LedEncoderTests
    {
        // ── Test stub ──────────────────────────────────────────────────

        private class StubTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public List<byte[]> Col03Reports { get; } = new List<byte[]>();
            public int Col03MaxInputReportLength => 64;

            public bool SendCol01(byte[] data) => true;

            public bool SendCol03(byte[] data)
            {
                Col03Reports.Add((byte[])data.Clone());
                return true;
            }

            public IReportStream IdentityReports => FakeReportStream.Empty;
            public IReportStream ItmReports => FakeReportStream.Empty;
            public IReportStream SrmReports => FakeReportStream.Empty;
            public IReportStream TuningReports => FakeReportStream.Empty;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public int Col01MaxInputReportLength => 34;
            public IDisposable BeginBatch() => new NoOpDisposable();

            private class NoOpDisposable : IDisposable
            {
                public void Dispose() { }
            }
        }

        private static byte[] LastReport(StubTransport t, byte subcmd)
            => t.Col03Reports.Last(r => r[2] == subcmd);

        // ── Intensity payload / commit-byte layout ─────────────────────

        [Fact]
        public void IntensityPayload_LastByte_ReachesWire_NotClobberedByCommit()
        {
            var t = new StubTransport();
            var encoder = new LedEncoder(t);

            // Payload with a distinctive value in the LAST slot: it must land at
            // report offset 3 + (SIZE-1) = 17, with the commit flag after it at 18.
            var intensities = new byte[LedEncoder.INTENSITY_PAYLOAD_SIZE];
            intensities[intensities.Length - 1] = 5;

            Assert.True(encoder.SetButtonLedState(null, intensities));

            var report = LastReport(t, 0x03);
            Assert.Equal(5, report[3 + LedEncoder.INTENSITY_PAYLOAD_SIZE - 1]);   // offset 17
            Assert.Equal(1, report[3 + LedEncoder.INTENSITY_PAYLOAD_SIZE]);       // commit at 18
        }

        [Fact]
        public void IntensityPayload_LastSlotChange_IsResent_AndVisibleOnWire()
        {
            var t = new StubTransport();
            var encoder = new LedEncoder(t);

            var intensities = new byte[LedEncoder.INTENSITY_PAYLOAD_SIZE];
            encoder.SetButtonLedState(null, intensities);
            t.Col03Reports.Clear();

            // Change only the last slot — the resend must carry the change
            // (historically the commit byte overwrote this slot, so the change
            // was sent as identical bytes and then latched as delivered).
            intensities[intensities.Length - 1] = 7;
            Assert.True(encoder.SetButtonLedState(null, intensities));

            var report = LastReport(t, 0x03);
            Assert.Equal(7, report[3 + LedEncoder.INTENSITY_PAYLOAD_SIZE - 1]);
        }

        [Fact]
        public void SetButtonLedState_WrongPayloadLength_Rejected()
        {
            var t = new StubTransport();
            var encoder = new LedEncoder(t);

            Assert.False(encoder.SetButtonLedState(null, new byte[LedEncoder.INTENSITY_PAYLOAD_SIZE + 1]));
            Assert.False(encoder.SetButtonLedState(null, new byte[LedEncoder.INTENSITY_PAYLOAD_SIZE - 1]));
            Assert.Empty(t.Col03Reports);
        }

        // ── Dirty tracking / ForceDirty ────────────────────────────────

        [Fact]
        public void SetRevLedColors_Unchanged_SkipsWire()
        {
            var t = new StubTransport();
            var encoder = new LedEncoder(t);
            var colors = new ushort[] { 0x1234, 0x5678 };

            encoder.SetRevLedColors(colors);
            t.Col03Reports.Clear();

            Assert.True(encoder.SetRevLedColors(colors));
            Assert.Empty(t.Col03Reports);
        }

        [Fact]
        public void ForceDirty_UnchangedColors_AreResent()
        {
            var t = new StubTransport();
            var encoder = new LedEncoder(t);
            var colors = new ushort[] { 0x1234, 0x5678 };

            encoder.SetRevLedColors(colors);
            t.Col03Reports.Clear();

            encoder.ForceDirty();
            Assert.True(encoder.SetRevLedColors(colors));
            Assert.Single(t.Col03Reports);
        }

        [Fact]
        public void ForceDirty_UnchangedButtonState_IsResent()
        {
            var t = new StubTransport();
            var encoder = new LedEncoder(t);
            var colors = new ushort[] { 0x0F0F };
            var intensities = new byte[LedEncoder.INTENSITY_PAYLOAD_SIZE];
            intensities[0] = 7;

            encoder.SetButtonLedState(colors, intensities);
            t.Col03Reports.Clear();

            encoder.ForceDirty();
            Assert.True(encoder.SetButtonLedState(colors, intensities));
            Assert.Contains(t.Col03Reports, r => r[2] == 0x02);   // colors resent
            Assert.Contains(t.Col03Reports, r => r[2] == 0x03);   // intensities resent
        }

        [Fact]
        public void ForceDirty_BetweenSends_IsNotLost()
        {
            // ForceDirty sets a flag consumed on the sender's own thread, so state
            // is never mutated concurrently with a send. Two ForceDirty calls with
            // no send in between still yield exactly one forced resend.
            var t = new StubTransport();
            var encoder = new LedEncoder(t);
            var colors = new ushort[] { 0x1234 };

            encoder.SetRevLedColors(colors);
            encoder.ForceDirty();
            encoder.ForceDirty();
            t.Col03Reports.Clear();

            encoder.SetRevLedColors(colors);
            Assert.Single(t.Col03Reports);      // forced resend

            t.Col03Reports.Clear();
            encoder.SetRevLedColors(colors);
            Assert.Empty(t.Col03Reports);       // flag consumed — back to dirty tracking
        }

        // ── Concurrent callers (#80) ───────────────────────────────────

        [Fact]
        public async Task ConcurrentClear_CannotRewriteReportWhileRefreshIsBeingSent()
        {
            // The refresh's write blocks inside the transport; a clear arrives
            // meanwhile, as the driver's Clear() does when its wait on the
            // refresh task times out. The clear must wait for the refresh rather
            // than refill the shared report buffer under it.
            var t = new GatedTransport { GateOnWrite = 1 };
            var encoder = new LedEncoder(t);
            var lit = Enumerable.Repeat((ushort)0xF800, 9).ToArray();

            var refresh = Task.Run(() => encoder.SetRevLedColors(lit));
            Assert.True(t.Entered.Wait(TimeSpan.FromSeconds(5)), "refresh never reached the transport");

            var clear = Task.Run(() => encoder.SetRevLedColors(new ushort[9]));
            // Unguarded, the clear finishes here having rewritten the buffer. The
            // bounded wait only gives it the chance to; with the lock it stays blocked.
            bool clearRanDuringRefresh = await Task.WhenAny(clear, Task.Delay(500)) == clear;

            t.Release();
            Assert.True(await refresh);
            Assert.True(await clear);

            Assert.False(clearRanDuringRefresh, "clear ran while the refresh held the report buffer");

            // The refresh's report reached the wire intact...
            var sent = t.GatedWrite;
            Assert.Equal(0x00, sent[2]);
            for (int i = 0; i < lit.Length; i++)
            {
                Assert.Equal(0xF8, sent[3 + i * 2]);
                Assert.Equal(0x00, sent[4 + i * 2]);
            }

            // ...and the clear still followed it.
            var last = t.Writes.Last();
            Assert.Equal(0x00, last[2]);
            Assert.All(last.Skip(3), b => Assert.Equal(0, b));
        }
    }
}
