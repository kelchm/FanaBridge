using FanaBridge.Diagnostics;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Locks the FF 08 firmware-version decode. Offsets come from the Fanatec driver
    /// (FWFUProtocolHidHandleSystemReport); the offset->component mapping follows the SDK
    /// struct order and is cross-checked against the Fanatec FW updater on a real
    /// ClubSport DD+ (PHUB + Button Module Rally): the updater showed Wheelbase 2.12.0.1,
    /// Podium Hub 6, BMR 1.0.3.1 — which match base[5..8], steering-wheel[0x1A..0x1D],
    /// button-module[0x21..0x24] respectively. A silent off-by-one or mislabel here
    /// mis-reports every version, so it is pinned to known-good bytes.
    /// </summary>
    public class FirmwareDecodeTests
    {
        // Real FF 08 capture from a ClubSport DD+ (PHUB + Button Module Rally)
        private static byte[] DdPlusFF08() => new byte[]
        {
            0xFF,0x08,0x0C,0x00,0x00,0x02,0x0B,0x01,0x03,0x00,0x03,0x81,0x01,0x00,0x02,0x02,
            0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x0C,0x03,0x06,0x00,0x00,0x00,0x00,0x02,
            0x00,0x01,0x00,0x03,0x01,0x00,0x03,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x02,0x00,
            0x00,0x02,0xB3,0x00,0x00,0x00,0x00,0x0F,0x00,0x0F,0x00,0x00,0x00,0x00,0x00,0x00
        };

        [Fact]
        public void DecodesExtendedLayout_WithUpdaterAlignedFields()
        {
            var fw = DiagnosticsReport.DecodeFirmware(DdPlusFF08());

            Assert.NotNull(fw);
            Assert.Equal(0x0C, fw.SystemConfig);          // 12 -> extended layout
            Assert.True(fw.Extended);
            Assert.Equal("2.11.1.3", fw.Wheelbase);       // [0x05..0x08]  (updater "Wheelbase")
            Assert.Equal("1.0.2.2", fw.Motor);            // [0x0C..0x0F]
            Assert.Equal("0", fw.WirelessQr);             // [0x13..0x16]  trailing-zero trimmed (FF 08 reports 0; updater reads it separately)
            Assert.Equal("6", fw.SteeringWheel);          // [0x1A..0x1D]  trailing-zero trimmed (updater "Podium Hub: 6")
            Assert.Equal("1.0.3.1", fw.ButtonModule);     // [0x21..0x24]  (updater "BMR: 1.0.3.1")
        }

        [Fact]
        public void DecodesLegacyLayout_WhenSystemConfigBelow6()
        {
            // SystemConfig = 3 (< 6) -> legacy: wheelbase is 16-bit LE of [5],[6];
            // the other fields are single bytes.
            var r = new byte[0x25];
            r[0] = 0xFF; r[1] = 0x08;
            r[2] = 0x03; r[3] = 0x00;          // SystemConfig = 3
            r[5] = 0x2A; r[6] = 0x01;          // wheelbase LE16 = 0x012A = 298
            r[0x0C] = 7;                        // motor
            r[0x13] = 9;                        // wireless QR
            r[0x1A] = 5;                        // steering wheel
            r[0x21] = 4;                        // button module

            var fw = DiagnosticsReport.DecodeFirmware(r);

            Assert.NotNull(fw);
            Assert.False(fw.Extended);
            Assert.Equal("298", fw.Wheelbase);
            Assert.Equal("7", fw.Motor);
            Assert.Equal("9", fw.WirelessQr);
            Assert.Equal("5", fw.SteeringWheel);
            Assert.Equal("4", fw.ButtonModule);
        }

        [Fact]
        public void ReturnsNull_WhenNoUsableReport()
        {
            Assert.Null(DiagnosticsReport.DecodeFirmware(null));
            Assert.Null(DiagnosticsReport.DecodeFirmware(new byte[8])); // too short for the version fields
        }
    }
}
