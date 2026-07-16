using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Encode→decode round-trip property tests: drive the real <see cref="ItmEncoder"/>
    /// through a recording transport, feed the emitted reports back through
    /// <see cref="ItmFrameDecoder"/>, and assert the decoded frames reconstruct exactly
    /// what was encoded. The decoder is the encoder's inverse — every frame family the
    /// encoder emits must round-trip byte-for-byte, and every malformed input must decode
    /// to <see cref="ItmFrameType.Unknown"/> without throwing.
    /// </summary>
    public class ItmFrameDecoderTests
    {
        // ── Recording transport (ITM uses col03 only) ────────────────────
        private sealed class RecordingTransport : IDeviceTransport
        {
            public bool IsConnected => true;
            public int Col03MaxInputReportLength => 64;
            public List<byte[]> Sent { get; } = new List<byte[]>();

            public bool SendCol03(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                Sent.Add(copy);
                return true;
            }

            public bool SendCol01(byte[] data) => true;
            public IReportStream IdentityReports => FakeReportStream.Empty;
            public IReportStream ItmReports => FakeReportStream.Empty;
            public IReportStream SrmReports => FakeReportStream.Empty;
            public IReportStream TuningReports => FakeReportStream.Empty;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public int Col01MaxInputReportLength => 34;
            public IDisposable BeginBatch() => new Scope();
            private sealed class Scope : IDisposable { public void Dispose() { } }
        }

        private static List<ItmFrame> Emit(Action<ItmEncoder> emit, out RecordingTransport t)
        {
            var transport = t = new RecordingTransport();
            var encoder = new ItmEncoder(transport);
            emit(encoder);
            return transport.Sent.Select(r => ItmFrameDecoder.Decode(r)).ToList();
        }

        // Flatten the ValueUpdate entries across however many reports a batch spanned.
        private static List<ItmValueEntry> AllValues(IEnumerable<ItmFrame> frames)
        {
            var list = new List<ItmValueEntry>();
            foreach (var f in frames)
            {
                Assert.Equal(ItmFrameType.ValueUpdate, f.Type);
                list.AddRange(f.Values);
            }
            return list;
        }

        private static List<ItmParamDefEntry> AllDefs(IEnumerable<ItmFrame> frames)
        {
            var list = new List<ItmParamDefEntry>();
            foreach (var f in frames)
            {
                Assert.Equal(ItmFrameType.ParamDefs, f.Type);
                list.AddRange(f.ParamDefs);
            }
            return list;
        }

        // ── Lifecycle single-frame families ──────────────────────────────

        [Fact]
        public void SessionEnable_RoundTrips()
        {
            var frames = Emit(e => Assert.True(e.EnableItm()), out _);
            var f = Assert.Single(frames);
            Assert.Equal(ItmFrameType.SessionEnable, f.Type);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Gate_RoundTripsState(bool on)
        {
            var frames = Emit(e => Assert.True(e.SetItmMode(on)), out _);
            var f = Assert.Single(frames);
            Assert.Equal(ItmFrameType.Gate, f.Type);
            Assert.Equal(on, f.GateOn);
        }

        [Fact]
        public void DisplayReset_RoundTrips()
        {
            var frames = Emit(e => Assert.True(e.ResetDisplay()), out _);
            var f = Assert.Single(frames);
            Assert.Equal(ItmFrameType.DisplayReset, f.Type);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(3, 3)]
        [InlineData(4, 6)]
        public void PageSet_RoundTripsDeviceAndPage(byte device, byte page)
        {
            var frames = Emit(e => Assert.True(e.SetPage(device, page)), out _);
            var f = Assert.Single(frames);
            Assert.Equal(ItmFrameType.PageSet, f.Type);
            Assert.Equal(device, f.DeviceId);
            Assert.Equal(page, f.Page);
        }

        // ── ValueUpdate framing ──────────────────────────────────────────

        [Fact]
        public void ValueUpdate_SingleEntry_RoundTrips()
        {
            var v = ItmValue.Int16(0, ItmParam.Speed, 142);
            var frames = Emit(e => Assert.True(e.SendValues(new[] { v }, 3)), out _);
            var entry = Assert.Single(AllValues(frames));
            AssertValueMatches(v, 3, entry);
        }

        [Fact]
        public void ValueUpdate_MixedSizes_MultiEntry_RoundTrips()
        {
            var values = new[]
            {
                ItmValue.UInt8(0, ItmParam.Gear, 5),
                ItmValue.Int16(1, ItmParam.Speed, -3),
                ItmValue.Int32(2, ItmParam.Rpm, 0x01020304),
                ItmValue.Float32(3, ItmParam.LapTime, 88.531f),
            };
            var frames = Emit(e => Assert.True(e.SendValues(values, 3)), out _);
            var entries = AllValues(frames);
            Assert.Equal(values.Length, entries.Count);
            for (int i = 0; i < values.Length; i++)
                AssertValueMatches(values[i], 3, entries[i]);
        }

        [Fact]
        public void ValueUpdate_OverflowSplit_ReconstructsFullSetInOrder()
        {
            // 9 Int32 entries (9 bytes each) overflow one 64-byte report → 2 reports.
            var values = new List<ItmValue>();
            for (int i = 0; i < 9; i++)
                values.Add(ItmValue.Int32((byte)i, ItmParam.Rpm, i * 100000));

            var frames = Emit(e => Assert.True(e.SendValues(values, 4)), out _);
            Assert.Equal(2, frames.Count);   // batch atomicity: consecutive reports, one family
            var entries = AllValues(frames);
            Assert.Equal(values.Count, entries.Count);
            for (int i = 0; i < values.Count; i++)
                AssertValueMatches(values[i], 4, entries[i]);
        }

        [Fact]
        public void ValueUpdate_NaNSanitized_DecodesToZeroBits()
        {
            // The encoder sanitizes NaN/Inf to 0 before it hits the wire; the decoder must
            // observe the sanitized bits (the twin renders what was SENT, not the intent).
            var values = new[]
            {
                ItmValue.Float32(0, ItmParam.Fuel, float.NaN),
                ItmValue.Float32(1, ItmParam.Fuel, float.PositiveInfinity),
                ItmValue.Float32(2, ItmParam.Fuel, float.NegativeInfinity),
            };
            var frames = Emit(e => Assert.True(e.SendValues(values, 3)), out _);
            foreach (var entry in AllValues(frames))
            {
                Assert.Equal(4, entry.Size);
                Assert.Equal(0u, entry.Raw);
            }
        }

        [Fact]
        public void ValueUpdate_AsciiText_RoundTripsBytesAndWidth()
        {
            // ENGINE_MAPPING-style 3-char ASCII value ("100").
            var v = ItmValue.Ascii(4, ItmParam.EngineMapping, "100");
            var frames = Emit(e => Assert.True(e.SendValues(new[] { v }, 3)), out _);
            var entry = Assert.Single(AllValues(frames));
            AssertValueMatches(v, 3, entry);
            Assert.Equal((byte)3, entry.Size);
            Assert.Equal((uint)('1' | ('0' << 8) | ('0' << 16)), entry.Raw);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        public void ValueUpdate_EachWidth_RoundTripsRawBits(byte size)
        {
            uint raw = size == 1 ? 0xA5u : size == 2 ? 0xBEEFu : 0xDEADBEEFu;
            var v = new ItmValue(7, ItmParam.OilTemp, size, raw & MaskFor(size));
            var frames = Emit(e => Assert.True(e.SendValues(new[] { v }, 3)), out _);
            var entry = Assert.Single(AllValues(frames));
            AssertValueMatches(v, 3, entry);
        }

        // ── ParamDefs framing ────────────────────────────────────────────

        [Fact]
        public void ParamDefs_NoSuffix_RoundTrips()
        {
            var def = new ItmParamDef(0x82);
            var frames = Emit(e => Assert.True(e.SetParamDefs(new[] { def }, 3)), out _);
            var entry = Assert.Single(AllDefs(frames));
            AssertDefMatches(def, 3, entry);
            Assert.Empty(entry.Suffix);
            Assert.Equal((byte)2, entry.Handle);   // slot 0x82 → handle 2
        }

        [Fact]
        public void ParamDefs_WithSuffixAndPosition_RoundTrips()
        {
            var defs = new[]
            {
                ItmParamDef.WithSuffix(0x82, "/0"),
                ItmParamDef.WithSuffix(0x85, "C"),
                new ItmParamDef(0x88, 0x0102),
            };
            var frames = Emit(e => Assert.True(e.SetParamDefs(defs, 3)), out _);
            var entries = AllDefs(frames);
            Assert.Equal(defs.Length, entries.Count);
            for (int i = 0; i < defs.Length; i++)
                AssertDefMatches(defs[i], 3, entries[i]);
        }

        [Fact]
        public void ParamDefs_MaxSuffixLength_RoundTrips()
        {
            var def = ItmParamDef.WithSuffix(0x83, new string('x', ItmEncoder.MaxSuffixLength));
            var frames = Emit(e => Assert.True(e.SetParamDefs(new[] { def }, 3)), out _);
            var entry = Assert.Single(AllDefs(frames));
            AssertDefMatches(def, 3, entry);
            Assert.Equal(ItmEncoder.MaxSuffixLength, entry.Suffix.Length);
        }

        [Fact]
        public void ParamDefs_OverflowSplit_ReconstructsFullSet()
        {
            var big = new string('x', ItmEncoder.MaxSuffixLength);
            var defs = new[]
            {
                ItmParamDef.WithSuffix(0x82, big),
                ItmParamDef.WithSuffix(0x83, big),
            };
            var frames = Emit(e => Assert.True(e.SetParamDefs(defs, 3)), out _);
            Assert.Equal(2, frames.Count);
            var entries = AllDefs(frames);
            Assert.Equal(defs.Length, entries.Count);
            for (int i = 0; i < defs.Length; i++)
                AssertDefMatches(defs[i], 3, entries[i]);
        }

        // ── Never-stuck: malformed / unknown inputs ──────────────────────

        [Fact]
        public void Decode_Null_IsUnknown()
        {
            Assert.Equal(ItmFrameType.Unknown, ItmFrameDecoder.Decode(null).Type);
        }

        [Fact]
        public void Decode_Empty_IsUnknown()
        {
            Assert.Equal(ItmFrameType.Unknown, ItmFrameDecoder.Decode(new byte[0]).Type);
        }

        [Fact]
        public void Decode_NoPrefix_IsUnknown()
        {
            Assert.Equal(ItmFrameType.Unknown, ItmFrameDecoder.Decode(new byte[64]).Type);
        }

        [Fact]
        public void Decode_UnknownDisplaySubcommand_IsUnknown()
        {
            var buf = new byte[64];
            buf[0] = 0xFF; buf[1] = 0x05; buf[2] = 0x7E;   // no such FF 05 subcommand
            Assert.Equal(ItmFrameType.Unknown, ItmFrameDecoder.Decode(buf).Type);
        }

        [Fact]
        public void Decode_UnknownCommandClass_IsUnknown()
        {
            var buf = new byte[64];
            buf[0] = 0xFF; buf[1] = 0x77; buf[2] = 0x01;
            Assert.Equal(ItmFrameType.Unknown, ItmFrameDecoder.Decode(buf).Type);
        }

        [Fact]
        public void Decode_GarbledValueSize_StopsCleanlyKeepingGoodEntries()
        {
            // Valid first entry (dev 3, size 1), then a second entry with an illegal size 9.
            var buf = new byte[64];
            buf[0] = 0xFF; buf[1] = 0x05; buf[2] = 0x01;
            // entry 1: dev, handle, idLo, idHi, size=1, value
            buf[3] = 3; buf[4] = 0; buf[5] = 4; buf[6] = 0; buf[7] = 1; buf[8] = 0x2A;
            // entry 2: dev, handle, idLo, idHi, size=9 (garbled)
            buf[9] = 3; buf[10] = 1; buf[11] = 1; buf[12] = 0; buf[13] = 9;

            var f = ItmFrameDecoder.Decode(buf);
            Assert.Equal(ItmFrameType.ValueUpdate, f.Type);
            var entry = Assert.Single(f.Values);   // good entry kept, garbled stride stops parse
            Assert.Equal((byte)3, entry.DeviceId);
            Assert.Equal(0x2Au, entry.Raw);
        }

        [Fact]
        public void Decode_TruncatedValue_DoesNotThrowOrOverread()
        {
            // Header + entry claiming size 4 but only 2 value bytes present in a short buffer.
            var buf = new byte[] { 0xFF, 0x05, 0x01, 3, 0, 2, 0, 4, 0x01, 0x02 };
            var f = ItmFrameDecoder.Decode(buf);
            Assert.Equal(ItmFrameType.ValueUpdate, f.Type);
            Assert.Empty(f.Values);   // truncated entry dropped, no over-read
        }

        [Fact]
        public void Decode_ToleratesLeadingReportId()
        {
            // 0xFF at offset 1 (behind a report-id byte), same tolerance the inbound
            // classifier applies.
            var buf = new byte[64];
            buf[0] = 0x00; buf[1] = 0xFF; buf[2] = 0x05; buf[3] = 0x04; buf[4] = 3; buf[5] = 2;
            var f = ItmFrameDecoder.Decode(buf);
            Assert.Equal(ItmFrameType.PageSet, f.Type);
            Assert.Equal((byte)3, f.DeviceId);
            Assert.Equal((byte)2, f.Page);
        }

        // ── Helpers ──────────────────────────────────────────────────────

        private static uint MaskFor(byte size) => size >= 4 ? 0xFFFFFFFFu : (uint)((1UL << (8 * size)) - 1);

        private static void AssertValueMatches(ItmValue expected, byte deviceId, ItmValueEntry actual)
        {
            Assert.Equal(deviceId, actual.DeviceId);
            Assert.Equal(expected.Handle, actual.Handle);
            Assert.Equal(expected.ParamId, actual.ParamId);
            Assert.Equal(expected.Size, actual.Size);
            Assert.Equal(expected.Raw, actual.Raw);
        }

        private static void AssertDefMatches(ItmParamDef expected, byte deviceId, ItmParamDefEntry actual)
        {
            Assert.Equal(deviceId, actual.DeviceId);
            Assert.Equal(expected.SlotId, actual.SlotId);
            Assert.Equal(expected.Position, actual.Position);
            var expectedSuffix = expected.Suffix ?? Array.Empty<byte>();
            Assert.Equal(expectedSuffix, actual.Suffix);
        }
    }
}
