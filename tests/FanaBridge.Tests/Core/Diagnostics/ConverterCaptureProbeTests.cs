using FanaBridge.Diagnostics;
using Xunit;

namespace FanaBridge.Tests.Core.Diagnostics
{
    /// <summary>
    /// Unit tests for the pure decoders of the converter capture probe — the col01 tail classifier
    /// and the SRM DE FA -> 0xDD reply decoder. The live I/O (engage + reads) is not exercised here.
    /// </summary>
    public class ConverterCaptureProbeTests
    {
        // A 34-byte col01 report with the given identity tail.
        private static byte[] Col01(byte wire, byte type = 0, byte b1 = 0, byte b2 = 0)
        {
            var b = new byte[34];
            b[0] = 0x01;
            b[34 - 5] = 0x81; // SystemConfig
            b[34 - 4] = wire;
            b[34 - 3] = type;
            b[34 - 2] = b1;
            b[34 - 1] = b2;
            return b;
        }

        [Fact]
        public void Col01Tail_DirectRim_NamesTheHub()
        {
            var r = ConverterCaptureProbe.DescribeCol01Tail(Col01(0x0C), 34);
            Assert.Contains("rim 0x0C", r);
            Assert.Contains("PHUB", r);
        }

        [Fact]
        public void Col01Tail_ZeroRim_IsNothingAttached()
        {
            Assert.Contains("nothing attached", ConverterCaptureProbe.DescribeCol01Tail(Col01(0x00), 34));
        }

        [Fact]
        public void Col01Tail_ExtInfoType1_ShowsRawBytesButNotAModuleClaim()
        {
            // col01 is COARSE — a PBMR and a PBME both emit b1=0x15 — so we keep the RAW byte but must
            // NOT present a "PBME"/"PBMR" label (that would be a guess). The module comes from FF 08 / DE FA.
            var r = ConverterCaptureProbe.DescribeCol01Tail(Col01(0xFF, type: 0x01, b1: 0x15, b2: 0x06), 34);
            Assert.Contains("EXT_INFO type=1", r);
            Assert.Contains("b1=0x15", r);                    // raw byte kept …
            Assert.DoesNotContain("button module: PBME", r);  // … but no misleading interpretation
            Assert.DoesNotContain("button module: PBMR", r);
        }

        [Fact]
        public void Col01Tail_ExtInfoType2_IsAccessories()
        {
            var r = ConverterCaptureProbe.DescribeCol01Tail(Col01(0xFF, type: 0x02), 34);
            Assert.Contains("EXT_INFO type=2", r);
            Assert.Contains("accessories", r);
        }

        [Fact]
        public void DeFa_DecodesKitFwWheelIdAndModule()
        {
            // DD [kitMaj=0x04] [kitMin=0x10] [wheelId=0x0E] [wheelFw=0x03] [module=0x02 -> PBMR]
            var buf = new byte[] { 0xDD, 0x04, 0x10, 0x0E, 0x03, 0x02, 0x00, 0x00 };
            var line = ConverterCaptureProbe.DescribeDeFa(buf, 0, 0x00);
            Assert.Contains("wheelId=0x0E", line);
            Assert.Contains("PBMR", line);
            Assert.Contains("kit fw 4.10", line);
        }

        [Fact]
        public void DeFa_WheelIdZero_ReportsNoRim()
        {
            var buf = new byte[] { 0xDD, 0x04, 0x10, 0x00, 0x00, 0x01, 0x00, 0x00 };
            Assert.Contains("no rim attached", ConverterCaptureProbe.DescribeDeFa(buf, 0, 0x00));
        }
    }
}
