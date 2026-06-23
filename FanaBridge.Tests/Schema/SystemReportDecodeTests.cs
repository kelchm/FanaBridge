using FanaBridge.Protocol.Schema;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Locks the schema-owned decode seam: <see cref="SystemReport.Decode"/> extracts
    /// the three identity bytes at the field offsets, relative to the leading 0xFF.
    /// This is the boundary the device model (FanatecBaseDriver) consumes.
    /// </summary>
    public class SystemReportDecodeTests
    {
        private static byte[] FrameAt(int sig, byte baseType, byte wheel, byte module)
        {
            var f = new byte[sig + SystemReport.Module.Offset + 1];
            f[sig] = 0xFF; // framing/signature is the reader's job; Decode only reads the field offsets
            f[sig + SystemReport.BaseType.Offset] = baseType;
            f[sig + SystemReport.WheelCode.Offset] = wheel;
            f[sig + SystemReport.Module.Offset] = module;
            return f;
        }

        [Fact]
        public void Decode_ExtractsFieldsAtTheirOffsets()
        {
            var v = SystemReport.Decode(FrameAt(0, baseType: 12, wheel: 0x0C, module: 0x02), 0);

            Assert.Equal(12, v.BaseType);
            Assert.Equal(0x0C, v.WheelCode);
            Assert.Equal(0x02, v.Module);
        }

        [Fact]
        public void Decode_HonorsTheSignatureOffset()
        {
            // A leading report-id byte shifts the whole frame; offsets are relative to
            // the 0xFF, so decoding at sig=1 must read the same logical fields.
            var v = SystemReport.Decode(FrameAt(1, baseType: 11, wheel: 0x0F, module: 0x00), 1);

            Assert.Equal(11, v.BaseType);
            Assert.Equal(0x0F, v.WheelCode);
            Assert.Equal(0x00, v.Module);
        }
    }
}
