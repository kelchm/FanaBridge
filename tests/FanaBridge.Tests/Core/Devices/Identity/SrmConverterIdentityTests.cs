using FanaBridge.Devices.Identity;
using Xunit;

namespace FanaBridge.Tests.Core.Devices.Identity
{
    /// <summary>
    /// Unit tests for the SRM Conversion Kit <c>0xDD</c> identity decode. The wire bytes in
    /// <see cref="Decode_RealCapture_Run1"/> are the actual reply simanthrop's kit returned, so this
    /// pins the decoder to validated hardware. The I/O (DE FA send/read) is not exercised here.
    /// </summary>
    public class SrmConverterIdentityTests
    {
        [Fact]
        public void Decode_RealCapture_Run1_CSSWFORMV2()
        {
            // simanthrop, kit fw 6.12 — verbatim: FF DD 06 12 0A 2F 00 …  (0xDD at index 1)
            var buf = new byte[] { 0xFF, 0xDD, 0x06, 0x12, 0x0A, 0x2F, 0x00, 0x00 };
            var r = SrmConverterIdentity.Decode(buf, 1, buf.Length);
            Assert.Equal(0x0A, r.WheelId);
            Assert.Equal("CSSWFORMV2", r.WheelCode);
            Assert.Equal("6.12", r.KitFirmware);
            Assert.Equal(0, r.ModuleRaw);
            Assert.Null(r.ModuleCode);
        }

        [Fact]
        public void Decode_HubWithModule_ResolvesBoth()
        {
            // Documented but UNVALIDATED for SRM converters: PHUB (0x0C) + PBMR (module 2).
            // DE FA carries the hub and the module as separate bytes, so this decodes cleanly.
            var buf = new byte[] { 0x00, 0xDD, 0x07, 0x00, 0x0C, 0x00, 0x02, 0x00 };
            var r = SrmConverterIdentity.Decode(buf, 1, buf.Length);
            Assert.Equal("PHUB", r.WheelCode);
            Assert.Equal(2, r.ModuleRaw);
            Assert.Equal("PBMR", r.ModuleCode);
        }

        [Fact]
        public void DecodeSrmWheel_0x17_IsWrcNotPswbmw()
        {
            // The one collision: SRM 0x17 = CSL WRC V2, NOT the Fanatec wire 0x17 alias (PSWBMW).
            Assert.Equal("CSLESWWRC", SrmConverterIdentity.DecodeSrmWheel(0x17));
            Assert.Equal("PSWBMW", SrmConverterIdentity.DecodeSrmWheel(0x0F)); // the real PSWBMW id
        }

        [Fact]
        public void DecodeSrmWheel_ZeroIsNoRim_UnknownIsNull()
        {
            Assert.Null(SrmConverterIdentity.DecodeSrmWheel(0x00)); // no rim attached
            Assert.Null(SrmConverterIdentity.DecodeSrmWheel(0xEE)); // unmapped id
        }

        [Fact]
        public void DecodeSrmWheel_Hubs_ResolveToHubCodes()
        {
            Assert.Equal("PHUB", SrmConverterIdentity.DecodeSrmWheel(0x0C));
            Assert.Equal("CSLSWUH", SrmConverterIdentity.DecodeSrmWheel(0x11));
            Assert.Equal("CSUHV2", SrmConverterIdentity.DecodeSrmWheel(0x15));
        }

        [Fact]
        public void DecodeSrmWheel_UniversalHubRows_MatchTheWireTable()
        {
            // 0x04=CSSWUH, 0x06=CSSWUHX, 0x05=gap — straight from the shared wire table.
            Assert.Equal("CSSWUH", SrmConverterIdentity.DecodeSrmWheel(0x04));
            Assert.Equal("CSSWUHX", SrmConverterIdentity.DecodeSrmWheel(0x06));
            Assert.Null(SrmConverterIdentity.DecodeSrmWheel(0x05)); // gap — no wheel
        }

        [Fact]
        public void DecodeSrmWheel_NewerWheels_ResolveViaSharedTable()
        {
            // Delegating to the wire table means wheels added there resolve for converters too —
            // no separate SRM map to drift out of sync.
            Assert.Equal("CSSWFORMV3", SrmConverterIdentity.DecodeSrmWheel(0x1C));
            Assert.Equal("GTSWX", SrmConverterIdentity.DecodeSrmWheel(0x18));
            Assert.Equal("CSLSWGT3", SrmConverterIdentity.DecodeSrmWheel(0x1D));
        }
    }
}
