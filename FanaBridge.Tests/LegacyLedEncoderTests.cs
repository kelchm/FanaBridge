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

        // ── LegacyRevRgb tests (subcmd 0x0A) ─────────────────────────

        [Fact]
        public void SetLegacyRevRgb_AllRed_CorrectBitPacking()
        {
            var transport = new StubTransport();
            var encoder = new LegacyLedEncoder(transport);

            // 9 LEDs all red: R=1, G=0, B=0 for each
            var buf = new byte[27];
            for (int i = 0; i < 9; i++)
            {
                buf[i * 3] = 1;     // R
                buf[i * 3 + 1] = 0; // G
                buf[i * 3 + 2] = 0; // B
            }

            encoder.SetLegacyRevRgb(buf);

            // Global enable + RGB data = 2 reports
            Assert.Equal(2, transport.Col01Reports.Count);

            // Global enable
            Assert.Equal(0x02, transport.Col01Reports[0][3]);
            Assert.Equal(0x01, transport.Col01Reports[0][4]);

            // RGB data: subcmd 0x0A
            var report = transport.Col01Reports[1];
            Assert.Equal(0x0A, report[3]);

            // All red: bit pattern per LED is 001 (R=1,G=0,B=0)
            // LED0: bits 0-2 = 001, LED1: bits 3-5 = 001, LED2: bits 6-7 + next byte bit 0
            // data[0] = 0b01_001_001 = 0x49  (LED2.R=1 at bit6, LED1.R=1 at bit3, LED0.R=1 at bit0)
            Assert.Equal(0x49, report[4]);
            // data[1] = 0b0_001_001_0 = LED5.R at bit7=0... let me compute:
            //   bit8=LED2.B=0, bit9=LED3.R=1, bit10=LED3.G=0, bit11=LED3.B=0,
            //   bit12=LED4.R=1, bit13=LED4.G=0, bit14=LED4.B=0, bit15=LED5.R=1
            //   = 0b10010010 = 0x92
            Assert.Equal(0x92, report[5]);
            // data[2]: bit16=LED5.G=0, bit17=LED5.B=0, bit18=LED6.R=1, bit19=LED6.G=0,
            //   bit20=LED6.B=0, bit21=LED7.R=1, bit22=LED7.G=0, bit23=LED7.B=0
            //   = 0b00100100 = 0x24
            Assert.Equal(0x24, report[6]);
            // data[3]: bit24=LED8.R=1, bit25=LED8.G=0, bit26=LED8.B=0 = 0b00000001 = 0x01
            Assert.Equal(0x01, report[7]);
        }

        [Fact]
        public void SetLegacyRevRgb_SdkDefaultPattern_YellowRedBlue()
        {
            var transport = new StubTransport();
            var encoder = new LegacyLedEncoder(transport);

            // SDK default: LED0-2=yellow(R=1,G=1,B=0), LED3-5=red(R=1,G=0,B=0), LED6-8=blue(R=0,G=0,B=1)
            var buf = new byte[27];
            // Yellow: LEDs 0-2
            for (int i = 0; i < 3; i++) { buf[i * 3] = 1; buf[i * 3 + 1] = 1; }
            // Red: LEDs 3-5
            for (int i = 3; i < 6; i++) { buf[i * 3] = 1; }
            // Blue: LEDs 6-8
            for (int i = 6; i < 9; i++) { buf[i * 3 + 2] = 1; }

            encoder.SetLegacyRevRgb(buf);

            var report = transport.Col01Reports[1];
            Assert.Equal(0x0A, report[3]);

            // LED0: R=1,G=1,B=0 → bits 0,1,2 = 011 = 0x03
            // LED1: R=1,G=1,B=0 → bits 3,4,5 = 011 = 0x18
            // LED2: R=1,G=1     → bits 6,7   = 11
            // data[0] = 0b11_011_011 = 0xDB
            Assert.Equal(0xDB, report[4]);

            // LED2: B=0 → bit8=0
            // LED3: R=1,G=0,B=0 → bits 9,10,11 = 001 → bit9=1
            // LED4: R=1,G=0,B=0 → bits 12,13,14 = 001 → bit12=1
            // LED5: R=1 → bit15=1
            // data[1] = 0b10010010 = 0x92  (bit15,bit12,bit9 set, rest 0)
            Assert.Equal(0x92, report[5]);

            // LED5: G=0,B=0 → bits 16,17 = 0
            // LED6: R=0,G=0,B=1 → bits 18,19,20 = 100 → bit20=1
            // LED7: R=0,G=0,B=1 → bits 21,22,23 = 100 → bit23=1
            // data[2] = 0b10100000 + bit20 = 0b10_100_100_00 wait...
            // bit16=LED5.G=0, bit17=LED5.B=0, bit18=LED6.R=0, bit19=LED6.G=0,
            // bit20=LED6.B=1, bit21=LED7.R=0, bit22=LED7.G=0, bit23=LED7.B=1
            // = 0b10100000... no: bit20 is the 5th bit (index 4 from LSB)
            // 0b_1_0_0_1_0_0_0_0 = bit20(val 0x10) + bit23(val 0x80) ??? wait
            // bit20 = 1<<(20-16) = 1<<4 = 0x10
            // bit23 = 1<<(23-16) = 1<<7 = 0x80
            // data[2] = 0x10 | 0x80 = 0x90
            Assert.Equal(0x90, report[6]);

            // LED8: R=0,G=0,B=1 → bit24=0, bit25=0, bit26=1
            // bit26 = 1<<(26-24) = 1<<2 = 0x04
            // data[3] = 0x04
            Assert.Equal(0x04, report[7]);
        }

        [Fact]
        public void SetLegacyRevRgb_AllOff_SendsZeroData()
        {
            var transport = new StubTransport();
            var encoder = new LegacyLedEncoder(transport);

            encoder.SetLegacyRevRgb(new byte[27]);

            var report = transport.Col01Reports[1];
            Assert.Equal(0x0A, report[3]);
            Assert.Equal(0x00, report[4]);
            Assert.Equal(0x00, report[5]);
            Assert.Equal(0x00, report[6]);
            Assert.Equal(0x00, report[7]);
        }

        [Fact]
        public void SetLegacyRevRgb_SameStateTwice_SkipsSecondSend()
        {
            var transport = new StubTransport();
            var encoder = new LegacyLedEncoder(transport);

            var buf = new byte[27];
            buf[0] = 1; // LED0 red

            encoder.SetLegacyRevRgb(buf);
            int countAfterFirst = transport.Col01Reports.Count;

            encoder.SetLegacyRevRgb(buf);
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
