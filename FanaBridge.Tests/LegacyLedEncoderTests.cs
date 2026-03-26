using System;
using System.Collections.Generic;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests
{
    public class LegacyLedEncoderTests
    {
        // ── Test stub ──────────────────────────────────────────────────

        private class StubTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public List<byte[]> Col01Reports { get; } = new List<byte[]>();
            public List<byte[]> Col03Reports { get; } = new List<byte[]>();
            public int Col03MaxInputReportLength => 64;

            public bool SendCol01(byte[] data)
            {
                Col01Reports.Add((byte[])data.Clone());
                return true;
            }

            public bool SendCol03(byte[] data)
            {
                Col03Reports.Add((byte[])data.Clone());
                return true;
            }

            public int ReadCol03(byte[] buffer, int timeoutMs) => -1;
            public IDisposable BeginBatch() => new NoOpDisposable();

            private class NoOpDisposable : IDisposable
            {
                public void Dispose() { }
            }
        }

        // ── Bitmask rev LED tests ──────────────────────────────────────

        [Fact]
        public void SetLegacyRevLeds_AllOff_SendsZeroBitmask()
        {
            var transport = new StubTransport();
            var encoder = new LegacyLedEncoder(transport);

            encoder.SetLegacyRevLeds(new bool[9]);

            // Should have: 1 global enable + 1 LED data
            Assert.Equal(2, transport.Col01Reports.Count);

            // Global enable: subcmd 0x02, enable=0x01
            Assert.Equal(0x02, transport.Col01Reports[0][3]);
            Assert.Equal(0x01, transport.Col01Reports[0][4]);

            // LED data: subcmd 0x08, bitmask=0x00
            Assert.Equal(0x08, transport.Col01Reports[1][3]);
            Assert.Equal(0x00, transport.Col01Reports[1][4]);
            Assert.Equal(0x00, transport.Col01Reports[1][5]);
        }

        [Fact]
        public void SetLegacyRevLeds_AllOn_SendsFullBitmask()
        {
            var transport = new StubTransport();
            var encoder = new LegacyLedEncoder(transport);
            var allOn = new bool[] { true, true, true, true, true, true, true, true, true };

            encoder.SetLegacyRevLeds(allOn);

            // LED data report
            var report = transport.Col01Reports[1];
            Assert.Equal(0x08, report[3]);
            Assert.Equal(0xFF, report[4]); // lower 8 bits
            Assert.Equal(0x01, report[5]); // bit 8
        }

        [Fact]
        public void SetLegacyRevLeds_SpecificPattern_CorrectBitmask()
        {
            var transport = new StubTransport();
            var encoder = new LegacyLedEncoder(transport);
            // LEDs 0, 1, 2 on
            var pattern = new bool[] { true, true, true, false, false, false, false, false, false };

            encoder.SetLegacyRevLeds(pattern);

            var report = transport.Col01Reports[1];
            Assert.Equal(0x07, report[4]); // 0b00000111
            Assert.Equal(0x00, report[5]);
        }

        [Fact]
        public void SetLegacyRevLeds_SameValueTwice_SkipsSecondSend()
        {
            var transport = new StubTransport();
            var encoder = new LegacyLedEncoder(transport);
            var pattern = new bool[] { true, false, false, false, false, false, false, false, false };

            encoder.SetLegacyRevLeds(pattern);
            int countAfterFirst = transport.Col01Reports.Count;

            encoder.SetLegacyRevLeds(pattern);
            Assert.Equal(countAfterFirst, transport.Col01Reports.Count); // No new report
        }

        [Fact]
        public void SetLegacyRevLeds_DifferentValue_SendsAgain()
        {
            var transport = new StubTransport();
            var encoder = new LegacyLedEncoder(transport);
            var pattern1 = new bool[] { true, false, false, false, false, false, false, false, false };
            var pattern2 = new bool[] { false, true, false, false, false, false, false, false, false };

            encoder.SetLegacyRevLeds(pattern1);
            int countAfterFirst = transport.Col01Reports.Count;

            encoder.SetLegacyRevLeds(pattern2);
            Assert.True(transport.Col01Reports.Count > countAfterFirst);
        }

        // ── RevStripe tests ────────────────────────────────────────────

        [Fact]
        public void SetRevStripeColor_SendsEnableSequenceThenColor()
        {
            var transport = new StubTransport();
            var encoder = new LegacyLedEncoder(transport);

            // Red in RGB333: data_hi=0x38, data_lo=0x00 → packed 0x3800
            encoder.SetRevStripeColor(0x3800);

            // Should have: RevStripe enable + global enable + LED data = 3 reports
            Assert.Equal(3, transport.Col01Reports.Count);

            // RevStripe enable: subcmd 0x06, inverted 0x00 = ON
            Assert.Equal(0x06, transport.Col01Reports[0][3]);
            Assert.Equal(0x00, transport.Col01Reports[0][4]);

            // Global enable: subcmd 0x02, enable=0x01
            Assert.Equal(0x02, transport.Col01Reports[1][3]);
            Assert.Equal(0x01, transport.Col01Reports[1][4]);

            // Color data: subcmd 0x08, data_lo=0x00, data_hi=0x38
            Assert.Equal(0x08, transport.Col01Reports[2][3]);
            Assert.Equal(0x00, transport.Col01Reports[2][4]); // data_lo
            Assert.Equal(0x38, transport.Col01Reports[2][5]); // data_hi
        }

        [Fact]
        public void SetRevStripeColor_SameColorTwice_SkipsSecondSend()
        {
            var transport = new StubTransport();
            var encoder = new LegacyLedEncoder(transport);

            encoder.SetRevStripeColor(0x3800);
            int countAfterFirst = transport.Col01Reports.Count;

            encoder.SetRevStripeColor(0x3800);
            Assert.Equal(countAfterFirst, transport.Col01Reports.Count);
        }

        // ── ForceDirty tests ───────────────────────────────────────────

        [Fact]
        public void ForceDirty_AllowsResend()
        {
            var transport = new StubTransport();
            var encoder = new LegacyLedEncoder(transport);
            var pattern = new bool[] { true, false, false, false, false, false, false, false, false };

            encoder.SetLegacyRevLeds(pattern);
            int countAfterFirst = transport.Col01Reports.Count;

            encoder.ForceDirty();
            encoder.SetLegacyRevLeds(pattern);
            Assert.True(transport.Col01Reports.Count > countAfterFirst);
        }
    }
}
