using System;
using FanaBridge.Core.Display.Protocol;
using Xunit;

namespace FanaBridge.Tests.Core.Display.Protocol
{
    public class ItmTelemetryTests
    {
        // ── ParamsFor ────────────────────────────────────────────────────

        [Fact]
        public void ParamsFor_LapInfo_HasExpectedOrder()
        {
            Assert.Equal(
                new ushort[] { 1, 4, 505, 501, 509, 510 },
                ParamsArray(ItmPage.LapInfo));
        }

        [Fact]
        public void ParamsFor_AllPages_LeadWithSpeedAndGear()
        {
            foreach (ItmPage page in new[]
                { ItmPage.LapInfo, ItmPage.FuelErsDrs, ItmPage.CarSettings, ItmPage.LapTimes, ItmPage.TyreTemps })
            {
                var ids = ParamsArray(page);
                Assert.Equal(ItmParam.Speed, ids[0]);
                Assert.Equal(ItmParam.Gear, ids[1]);
            }
        }

        [Fact]
        public void ParamsFor_Legacy_IsEmpty()
        {
            Assert.Empty(ParamsArray(ItmPage.Legacy));
        }

        // ── NameOf ───────────────────────────────────────────────────────

        [Fact]
        public void NameOf_GivesEveryPageANonEmptyDisplayName()
        {
            Assert.Equal("Lap Info", ItmTelemetry.NameOf(ItmPage.LapInfo));
            Assert.Equal("Car Settings", ItmTelemetry.NameOf(ItmPage.CarSettings));
            foreach (ItmPage page in System.Enum.GetValues(typeof(ItmPage)))
                Assert.False(string.IsNullOrEmpty(ItmTelemetry.NameOf(page)));
        }

        private static ushort[] ParamsArray(ItmPage page)
        {
            var list = ItmTelemetry.ParamsFor(page);
            var arr = new ushort[list.Count];
            for (int i = 0; i < list.Count; i++) arr[i] = list[i];
            return arr;
        }

        // ── Subscription report parsing (firmware-driven path) ───────────

        // Real tyre-page subscription report from the official-software capture:
        // FF 05 01, then [03][fwHandle][paramId-LE][unit] entries.
        private static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        [Fact]
        public void ParseSubscriptionReport_TyrePage_DecodesHandlesAndParams()
        {
            // h0=SPEED, h1=GEAR, h0x82=FL(42), h0x83=RL(48) — four complete entries.
            var report = HexToBytes("ff05010300010034030104001203822a00320383300032");
            var subs = ItmTelemetry.ParseSubscriptionReport(report, report.Length);

            Assert.Equal(4, subs.Count);
            Assert.Equal(0, subs[0].Handle); Assert.Equal(ItmParam.Speed, subs[0].ParamId);
            Assert.Equal(1, subs[1].Handle); Assert.Equal(ItmParam.Gear, subs[1].ParamId);
            // 0x82 -> host handle 2 (slot-marker bit cleared)
            Assert.Equal(2, subs[2].Handle); Assert.Equal(ItmParam.TyreFlTemp, subs[2].ParamId);
            Assert.Equal(3, subs[3].Handle); Assert.Equal(ItmParam.TyreRlTemp, subs[3].ParamId);
            Assert.All(subs, s => Assert.False(s.IsUnsubscribe));
        }

        [Fact]
        public void ParseSubscriptionReport_CapturesDeclaredDataType()
        {
            // Same tyre-page report: each entry's 5th byte is the firmware's declared slot
            // type (0x34 = i16 speed, 0x12 = u8 gear, 0x32 = u8 temps).
            var report = HexToBytes("ff05010300010034030104001203822a00320383300032");
            var subs = ItmTelemetry.ParseSubscriptionReport(report, report.Length);

            Assert.Equal((byte)0x34, subs[0].DataType);
            Assert.Equal((byte)0x12, subs[1].DataType);
            Assert.Equal((byte)0x32, subs[2].DataType);
            Assert.Equal((byte)0x32, subs[3].DataType);
        }

        [Fact]
        public void IsTextType_LowNibbleOne_Only()
        {
            Assert.True(ItmTelemetry.IsTextType(0x01));
            Assert.True(ItmTelemetry.IsTextType(0x11));
            Assert.False(ItmTelemetry.IsTextType(0x12));   // PBME GEAR (u8)
            Assert.False(ItmTelemetry.IsTextType(0x34));   // SPEED (i16)
            Assert.False(ItmTelemetry.IsTextType(0x00));   // unknown / seeded
        }

        [Fact]
        public void ParseSubscriptionReport_DecodesUnsubscribe()
        {
            // FF 05 01, then [03][h][FF FF][unit] entries = unsubscribe
            var report = HexToBytes("ff05010300ffff340301ffff12");
            var subs = ItmTelemetry.ParseSubscriptionReport(report, report.Length);

            Assert.Equal(2, subs.Count);
            Assert.True(subs[0].IsUnsubscribe);
            Assert.True(subs[1].IsUnsubscribe);
            Assert.Equal(0, subs[0].Handle);
            Assert.Equal(1, subs[1].Handle);
        }

        [Fact]
        public void ParseSubscriptionReport_NonItm_IsEmpty()
        {
            Assert.Empty(ItmTelemetry.ParseSubscriptionReport(HexToBytes("ff080c00"), 4));
            Assert.Empty(ItmTelemetry.ParseSubscriptionReport(null, 0));
        }

        [Fact]
        public void ParseSubscriptionReport_FiltersToGivenDevice()
        {
            // One entry for device 4 (Bentley): [04][h0][SPEED=0001][unit 34]
            var report = HexToBytes("ff0501" + "0400010034");

            // The default device (3) skips the device-4 entry.
            Assert.Empty(ItmTelemetry.ParseSubscriptionReport(report, report.Length));

            // Asking for device 4 accepts it.
            var subs = ItmTelemetry.ParseSubscriptionReport(report, report.Length, (byte)4);
            Assert.Single(subs);
            Assert.Equal(ItmParam.Speed, subs[0].ParamId);
            Assert.Equal(0, subs[0].Handle);
        }

        [Fact]
        public void ParseSubscriptionReport_InterleavedDevices_CollectsAllMatching()
        {
            // dev3 h0 SPEED, dev4 h0 SPEED (other display), dev3 h1 GEAR. The middle entry must be
            // skipped, not stop the scan, so both device-3 entries are still collected.
            var report = HexToBytes("ff0501" + "0300010034" + "0400010034" + "0301040034");
            var subs = ItmTelemetry.ParseSubscriptionReport(report, report.Length);   // default device 3

            Assert.Equal(2, subs.Count);
            Assert.Equal(0, subs[0].Handle);
            Assert.Equal(ItmParam.Speed, subs[0].ParamId);
            Assert.Equal(1, subs[1].Handle);
            Assert.Equal(ItmParam.Gear, subs[1].ParamId);
        }
    }
}
