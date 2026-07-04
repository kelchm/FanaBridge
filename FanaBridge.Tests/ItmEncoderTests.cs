using System;
using System.Collections.Generic;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests
{
    public class ItmEncoderTests
    {
        // ── Test stub ────────────────────────────────────────────────────

        /// <summary>
        /// Records every col03 report the encoder emits. ITM uses only the col03
        /// path; SendCol03 can be made to fail to exercise the AND-of-sends result.
        /// </summary>
        private class RecordingTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public int Col03MaxInputReportLength { get; set; } = 64;
            public bool FailSends { get; set; }

            public List<byte[]> SentCol03Reports { get; } = new List<byte[]>();
            public int BatchDepth { get; private set; }

            public bool SendCol03(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                SentCol03Reports.Add(copy);
                return !FailSends;
            }

            public bool SendCol01(byte[] data) => true;
            public int ReadCol03(byte[] buffer, int timeoutMs) => -1;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public int Col01MaxInputReportLength => 34;

            public IDisposable BeginBatch()
            {
                BatchDepth++;
                return new BatchScope(this);
            }

            private sealed class BatchScope : IDisposable
            {
                private readonly RecordingTransport _t;
                private bool _done;
                public BatchScope(RecordingTransport t) { _t = t; }
                public void Dispose() { if (!_done) { _done = true; _t.BatchDepth--; } }
            }

            public byte[] Last => SentCol03Reports[SentCol03Reports.Count - 1];
        }

        private static ItmEncoder MakeEncoder(out RecordingTransport transport)
        {
            transport = new RecordingTransport();
            return new ItmEncoder(transport);
        }

        // ── Construction ─────────────────────────────────────────────────

        [Fact]
        public void Ctor_NullTransport_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ItmEncoder(null));
        }

        // ── Enable ───────────────────────────────────────────────────────

        [Fact]
        public void EnableItm_BuildsExpectedFrame()
        {
            var encoder = MakeEncoder(out var t);

            Assert.True(encoder.EnableItm());

            var r = t.Last;
            Assert.Equal(64, r.Length);
            Assert.Equal(0xFF, r[0]);
            Assert.Equal(0x02, r[1]);   // command class — ITM enable
            Assert.Equal(0x02, r[2]);   // sub-command
            Assert.Equal(0x00, r[3]);   // default page
        }

        [Fact]
        public void EnableItm_CarriesPageByte()
        {
            var encoder = MakeEncoder(out var t);

            encoder.EnableItm(4);

            Assert.Equal(0x04, t.Last[3]);
        }

        [Fact]
        public void SetItmMode_BuildsGateFrame()
        {
            var encoder = MakeEncoder(out var t);

            encoder.SetItmMode(true);
            var on = t.Last;
            Assert.Equal(0xFF, on[0]);
            Assert.Equal(0x05, on[1]);   // ITM display class
            Assert.Equal(0x02, on[2]);   // ITM mode gate
            Assert.Equal(0x01, on[3]);   // on

            encoder.SetItmMode(false);
            Assert.Equal(0x00, t.Last[3]);   // off
        }

        // ── PageSet ──────────────────────────────────────────────────────

        [Fact]
        public void SetPage_BuildsExpectedFrame()
        {
            var encoder = MakeEncoder(out var t);

            encoder.SetPage(ItmDevice.BmeOrGtswx, 3);

            var r = t.Last;
            Assert.Equal(0xFF, r[0]);
            Assert.Equal(0x05, r[1]);
            Assert.Equal(0x04, r[2]);
            Assert.Equal(0x03, r[3]);   // device ID 3 (BME/GTSWX)
            Assert.Equal(0x03, r[4]);   // page
        }

        [Fact]
        public void SetPage_DeviceEnumMapsToWireId()
        {
            var encoder = MakeEncoder(out var t);

            encoder.SetPage(ItmDevice.Base, 1);
            Assert.Equal(0x01, t.Last[3]);

            encoder.SetPage(ItmDevice.Bentley, 2);
            Assert.Equal(0x04, t.Last[3]);
        }

        // ── Keepalive ────────────────────────────────────────────────────

        [Fact]
        public void SendKeepalive_BuildsExpectedFrame()
        {
            var encoder = MakeEncoder(out var t);

            encoder.SendKeepalive();

            var r = t.Last;
            Assert.Equal(0xFF, r[0]);
            Assert.Equal(0x05, r[1]);
            Assert.Equal(0x04, r[2]);
            Assert.Equal(0x02, r[3]);
            Assert.Equal(0x0B, r[4]);
        }

        // ── ValueUpdate framing ──────────────────────────────────────────

        [Fact]
        public void SendValues_PacksHeaderAndEntry()
        {
            var encoder = MakeEncoder(out var t);

            // SPEED (id 1), Int16 LE, value 142 = 0x008E
            Assert.True(encoder.SendValues(new[] { ItmValue.Int16(0, 1, 142) }));

            var r = t.Last;
            Assert.Equal(0xFF, r[0]);
            Assert.Equal(0x05, r[1]);
            Assert.Equal(0x01, r[2]);   // subcmd ValueUpdate
            // entry: marker, handle, idLo, idHi, size, value LE
            Assert.Equal(0x03, r[3]);   // marker (0x03, confirmed by capture — not 0x01)
            Assert.Equal(0x00, r[4]);   // handle
            Assert.Equal(0x01, r[5]);   // param id lo
            Assert.Equal(0x00, r[6]);   // param id hi
            Assert.Equal(0x02, r[7]);   // size
            Assert.Equal(0x8E, r[8]);   // value lo
            Assert.Equal(0x00, r[9]);   // value hi
        }

        [Fact]
        public void SendValues_UInt8_EmitsSingleValueByte()
        {
            var encoder = MakeEncoder(out var t);

            // GEAR (id 4), Uint8, value 5
            encoder.SendValues(new[] { ItmValue.UInt8(2, 4, 5) });

            var r = t.Last;
            Assert.Equal(0x03, r[3]);   // marker
            Assert.Equal(0x02, r[4]);   // handle
            Assert.Equal(0x04, r[5]);   // id lo
            Assert.Equal(0x00, r[6]);   // id hi
            Assert.Equal(0x01, r[7]);   // size
            Assert.Equal(0x05, r[8]);   // value
        }

        [Fact]
        public void SendValues_Int32_EmitsLittleEndian()
        {
            var encoder = MakeEncoder(out var t);

            // RPM (id 2), Int32, value 0x01020304
            encoder.SendValues(new[] { ItmValue.Int32(1, 2, 0x01020304) });

            var r = t.Last;
            Assert.Equal(0x04, r[7]);   // size
            Assert.Equal(0x04, r[8]);   // LE byte 0
            Assert.Equal(0x03, r[9]);
            Assert.Equal(0x02, r[10]);
            Assert.Equal(0x01, r[11]);
        }

        [Fact]
        public void SendValues_Float32_EmitsIeee754LittleEndian()
        {
            var encoder = MakeEncoder(out var t);

            // 1.0f = 0x3F800000
            encoder.SendValues(new[] { ItmValue.Float32(0, 509, 1.0f) });

            var r = t.Last;
            Assert.Equal(0x04, r[7]);   // size
            Assert.Equal(0x00, r[8]);
            Assert.Equal(0x00, r[9]);
            Assert.Equal(0x80, r[10]);
            Assert.Equal(0x3F, r[11]);
        }

        [Fact]
        public void Float32_SanitizesNaNAndInfinity()
        {
            // NaN/Inf would wedge the firmware — they must become 0 (bits 0x00000000).
            Assert.Equal(0u, ItmValue.Float32(0, 1, float.NaN).Raw);
            Assert.Equal(0u, ItmValue.Float32(0, 1, float.PositiveInfinity).Raw);
            Assert.Equal(0u, ItmValue.Float32(0, 1, float.NegativeInfinity).Raw);
        }

        [Fact]
        public void Float32_ClampsAbsurdMagnitude()
        {
            var v = ItmValue.Float32(0, 1, 1e9f);
            float decoded = BitConverter.ToSingle(BitConverter.GetBytes(v.Raw), 0);
            Assert.Equal(1_000_000f, decoded);
        }

        [Fact]
        public void SendValues_MultipleEntries_PackedIntoOneReport()
        {
            var encoder = MakeEncoder(out var t);

            encoder.SendValues(new[]
            {
                ItmValue.Int16(0, 1, 100),   // 7 bytes
                ItmValue.UInt8(1, 4, 3),     // 6 bytes
            });

            Assert.Single(t.SentCol03Reports);
            var r = t.Last;
            // second entry starts right after the first (3 header + 7)
            Assert.Equal(0x03, r[10]);   // marker of 2nd entry
            Assert.Equal(0x01, r[11]);   // handle
            Assert.Equal(0x04, r[12]);   // id lo
        }

        [Fact]
        public void SendValues_OverflowSplitsAcrossReports()
        {
            var encoder = MakeEncoder(out var t);

            // 9 Int32 entries × 9 bytes = 81 bytes payload > 61 → needs 2 reports.
            var values = new List<ItmValue>();
            for (int i = 0; i < 9; i++)
                values.Add(ItmValue.Int32((byte)i, 2, i));

            Assert.True(encoder.SendValues(values));
            Assert.Equal(2, t.SentCol03Reports.Count);
            // every report keeps the FF 05 01 header
            foreach (var r in t.SentCol03Reports)
            {
                Assert.Equal(0xFF, r[0]);
                Assert.Equal(0x05, r[1]);
                Assert.Equal(0x01, r[2]);
            }
        }

        [Fact]
        public void SendValues_UsesBatchForAtomicity()
        {
            var encoder = MakeEncoder(out var t);

            encoder.SendValues(new[] { ItmValue.Int16(0, 1, 1) });

            Assert.Equal(0, t.BatchDepth);   // batch opened and released
        }

        [Theory]
        [InlineData(null)]
        public void SendValues_NullList_ReturnsFalse(IReadOnlyList<ItmValue> values)
        {
            var encoder = MakeEncoder(out var t);
            Assert.False(encoder.SendValues(values));
            Assert.Empty(t.SentCol03Reports);
        }

        [Fact]
        public void SendValues_EmptyList_ReturnsFalse()
        {
            var encoder = MakeEncoder(out var t);
            Assert.False(encoder.SendValues(new ItmValue[0]));
            Assert.Empty(t.SentCol03Reports);
        }

        [Fact]
        public void SendValues_TooManyParams_ReturnsFalse()
        {
            var encoder = MakeEncoder(out var t);

            var values = new List<ItmValue>();
            for (int i = 0; i < ItmEncoder.MaxParams + 1; i++)
                values.Add(ItmValue.UInt8(0, 4, 0));

            Assert.False(encoder.SendValues(values));
            Assert.Empty(t.SentCol03Reports);
        }

        [Fact]
        public void SendValues_BadSize_ReturnsFalse()
        {
            var encoder = MakeEncoder(out var t);

            // Widths outside 1..4 are invalid (0 and 5 here). Size 3 is valid — ASCII text.
            Assert.False(encoder.SendValues(new[] { new ItmValue(0, 1, 0, 0) }));
            Assert.False(encoder.SendValues(new[] { new ItmValue(0, 1, 5, 0) }));
        }

        [Fact]
        public void SendValues_AsciiThreeBytes_IsSent()
        {
            var encoder = MakeEncoder(out var t);

            // ENGINE_MAPPING-style 3-char ASCII value ("100") must be accepted and framed.
            Assert.True(encoder.SendValues(new[] { ItmValue.Ascii(4, 26, "100") }));
            var r = t.Last;
            // FF 05 01 <marker=03> <handle=04> <id 26 LE=1a00> <size=03> '1' '0' '0'
            var expected = new byte[] { 0xFF, 0x05, 0x01, 0x03, 0x04, 0x1A, 0x00, 0x03, 0x31, 0x30, 0x30 };
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], r[i]);
        }

        [Fact]
        public void SendValues_PropagatesTransportFailure()
        {
            var encoder = MakeEncoder(out var t);
            t.FailSends = true;

            Assert.False(encoder.SendValues(new[] { ItmValue.UInt8(0, 4, 1) }));
        }

        // ── ParamDefs framing ────────────────────────────────────────────

        [Fact]
        public void SetParamDefs_PacksHeaderAndEntry()
        {
            var encoder = MakeEncoder(out var t);

            Assert.True(encoder.SetParamDefs(new[] { new ItmParamDef(0x82) }));

            var r = t.Last;
            Assert.Equal(0xFF, r[0]);
            Assert.Equal(0x05, r[1]);
            Assert.Equal(0x03, r[2]);   // subcmd ParamDefs
            // entry: marker, slotId, posLo, posHi, suffixLen
            Assert.Equal(0x03, r[3]);   // marker
            Assert.Equal(0x82, r[4]);   // slot id
            Assert.Equal(0x00, r[5]);   // pos lo
            Assert.Equal(0x00, r[6]);   // pos hi
            Assert.Equal(0x00, r[7]);   // suffix length
        }

        [Fact]
        public void SetParamDefs_EncodesSuffixBytes()
        {
            var encoder = MakeEncoder(out var t);

            // "/0" total-companion suffix from the protocol reference.
            encoder.SetParamDefs(new[] { ItmParamDef.WithSuffix(0x82, "/0") });

            var r = t.Last;
            Assert.Equal(0x82, r[4]);
            Assert.Equal(0x02, r[7]);   // suffix length
            Assert.Equal((byte)'/', r[8]);
            Assert.Equal((byte)'0', r[9]);
        }

        [Fact]
        public void SetParamDefs_EncodesPositionLittleEndian()
        {
            var encoder = MakeEncoder(out var t);

            encoder.SetParamDefs(new[] { new ItmParamDef(0x85, 0x0102) });

            var r = t.Last;
            Assert.Equal(0x02, r[5]);   // pos lo
            Assert.Equal(0x01, r[6]);   // pos hi
        }

        [Fact]
        public void SetParamDefs_OverflowSplitsAcrossReports()
        {
            var encoder = MakeEncoder(out var t);

            // Each entry with a max suffix nearly fills a report, forcing a split.
            var big = new string('x', ItmEncoder.MaxSuffixLength);
            var defs = new[]
            {
                ItmParamDef.WithSuffix(0x82, big),
                ItmParamDef.WithSuffix(0x83, big),
            };

            Assert.True(encoder.SetParamDefs(defs));
            Assert.Equal(2, t.SentCol03Reports.Count);
            foreach (var r in t.SentCol03Reports)
            {
                Assert.Equal(0xFF, r[0]);
                Assert.Equal(0x05, r[1]);
                Assert.Equal(0x03, r[2]);
            }
        }

        [Fact]
        public void SetParamDefs_SuffixTooLong_ReturnsFalse()
        {
            var encoder = MakeEncoder(out var t);

            var tooLong = new string('x', ItmEncoder.MaxSuffixLength + 1);
            Assert.False(encoder.SetParamDefs(new[] { ItmParamDef.WithSuffix(0x82, tooLong) }));
        }

        [Fact]
        public void SetParamDefs_NullOrEmpty_ReturnsFalse()
        {
            var encoder = MakeEncoder(out var t);

            Assert.False(encoder.SetParamDefs(null));
            Assert.False(encoder.SetParamDefs(new ItmParamDef[0]));
            Assert.Empty(t.SentCol03Reports);
        }

        [Fact]
        public void SetParamDefs_TooManyParams_ReturnsFalse()
        {
            var encoder = MakeEncoder(out var t);

            var defs = new List<ItmParamDef>();
            for (int i = 0; i < ItmEncoder.MaxParams + 1; i++)
                defs.Add(new ItmParamDef(0x82));

            Assert.False(encoder.SetParamDefs(defs));
            Assert.Empty(t.SentCol03Reports);
        }
    }
}
