using System;
using System.Collections.Generic;
using FanaBridge.Protocol;
using FanaBridge.Protocol.Schema;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Phase 4 — golden-frame coverage that locks the wire format end to end,
    /// filling the two gaps the refactor identified: the col03 LedEncoder byte
    /// output and the FF 08 identity decode. Vectors are derived from the protocol
    /// reference (e.g. the FF 08 worked example in protocol.md).
    /// </summary>
    public class GoldenFrameTests
    {
        // ── FF 08 identity decode ────────────────────────────────────────

        [Theory]
        // base, wire, module, expectedBase, expectedAttachment, expectedModule
        [InlineData(0x0C, 0x0C, 0x02, "CSDDPlus", "PHUB", "PBMR")]   // protocol.md worked example
        [InlineData(0x08, 0x18, 0x00, "PDD2",     "GTSWX", null)]    // wheel, no module
        [InlineData(0x0A, 0x01, 0x01, "CSLDD",    "CSSWBMW", "PBME")] // wheel + endurance module
        public void Ff08Frame_DecodesToExpectedCodes(
            byte baseType, byte wire, byte module,
            string expectBase, string expectAttachment, string expectModule)
        {
            // Build a system report at the SystemReport definition's offsets.
            var frame = new byte[Wire.Col03Length];
            Wire.BeginCol03(frame, Wire.Col03.SystemClass, 0x00);
            frame[SystemReport.BaseType.Offset] = baseType;
            frame[SystemReport.WheelCode.Offset] = wire;
            frame[SystemReport.Module.Offset] = module;

            Assert.Equal(expectBase, FanatecIdentity.DecodeBaseCode(frame[SystemReport.BaseType.Offset]));
            Assert.Equal(expectAttachment, FanatecIdentity.DecodeCode(frame[SystemReport.WheelCode.Offset]));
            Assert.Equal(expectModule, FanatecIdentity.DecodeModule(frame[SystemReport.Module.Offset]));
        }

        // ── col03 LedEncoder byte output ─────────────────────────────────

        [Fact]
        public void LedEncoder_RevColors_EmitBigEndianRgb565Frame()
        {
            var t = new RecordingTransport();
            var enc = new LedEncoder(t);

            const ushort red = 0xF800;   // RGB565 red
            const ushort green = 0x07E0; // RGB565 green
            Assert.True(enc.SetRevLedColors(new ushort[] { red, green }));

            var f = Assert.Single(t.Col03Reports);
            Assert.Equal(Wire.Col03.ReportId, f[0]); // 0xFF
            Assert.Equal(Wire.Col03.LedClass, f[1]); // 0x01
            Assert.Equal(0x00, f[2]);                // rev-colors subcmd
            // Big-endian: high byte then low byte.
            Assert.Equal(0xF8, f[3]);
            Assert.Equal(0x00, f[4]);
            Assert.Equal(0x07, f[5]);
            Assert.Equal(0xE0, f[6]);
            Assert.Equal(Wire.Col03Length, f.Length);
        }

        [Fact]
        public void LedEncoder_UnchangedColors_SkipsRedundantWrite()
        {
            var t = new RecordingTransport();
            var enc = new LedEncoder(t);
            var colors = new ushort[] { 0xF800 };

            Assert.True(enc.SetRevLedColors(colors));
            Assert.True(enc.SetRevLedColors(colors)); // identical — dirty tracking should skip
            Assert.Single(t.Col03Reports);
        }

        // ── Test transport ───────────────────────────────────────────────

        private sealed class RecordingTransport : IDeviceTransport
        {
            public List<byte[]> Col03Reports { get; } = new List<byte[]>();

            public bool IsConnected => true;
            public int Col03MaxInputReportLength => Wire.Col03Length;

            public bool SendCol03(byte[] data)
            {
                Col03Reports.Add((byte[])data.Clone());
                return true;
            }

            public bool SendCol01(byte[] data) => true;
            public int ReadCol03(byte[] buffer, int timeoutMs) => -1;
            public IDisposable BeginBatch() => new NoOp();

            private sealed class NoOp : IDisposable { public void Dispose() { } }
        }
    }
}
