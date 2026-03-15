using System;
using System.Collections.Generic;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests
{
    public class FanatecTuningControllerTests
    {
        // ── Test stub ────────────────────────────────────────────────────

        private class StubTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public int Col03MaxInputReportLength { get; set; } = 64;

            public List<byte[]> SentCol03Reports { get; } = new List<byte[]>();
            public List<byte[]> SentCol01Reports { get; } = new List<byte[]>();

            /// <summary>Queue of reports that ReadCol03 will return.</summary>
            public Queue<byte[]> ReadQueue { get; } = new Queue<byte[]>();

            /// <summary>
            /// When true, the first ReadCol03 call returns -1 (simulates
            /// drain completion), then subsequent calls use the queue.
            /// </summary>
            public bool DrainReturnsNegative { get; set; } = true;

            private bool _drained;

            public bool SendCol03(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                SentCol03Reports.Add(copy);
                return true;
            }

            public int ReadCol03(byte[] buffer, int timeoutMs)
            {
                // Simulate drain phase: first call returns -1 (no data)
                if (DrainReturnsNegative && !_drained)
                {
                    _drained = true;
                    return -1;
                }

                if (ReadQueue.Count == 0)
                    return -1;

                var report = ReadQueue.Dequeue();
                Array.Copy(report, buffer, Math.Min(report.Length, buffer.Length));
                return report.Length;
            }

            public bool SendCol01(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                SentCol01Reports.Add(copy);
                return true;
            }

            public IDisposable BeginBatch() => new NoOpDisposable();

            private sealed class NoOpDisposable : IDisposable
            {
                public void Dispose() { }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Builds a fake 64-byte tuning READ response from the BMR device
        /// with the given encoder mode byte at offset 18.
        /// </summary>
        private static byte[] FakeReadResponse(byte encoderMode)
        {
            var buf = new byte[64];
            buf[0] = 0xFF;
            buf[1] = FanatecTuningController.CMD_CLASS;
            buf[2] = FanatecTuningController.DEVICE_ID_BMR;
            buf[FanatecTuningController.READ_ENCODER_MODE_OFFSET] = encoderMode;
            return buf;
        }

        // ── BuildWriteBuffer ─────────────────────────────────────────────

        [Fact]
        public void BuildWriteBuffer_SetsHeader()
        {
            var readBuf = FakeReadResponse(0x01);
            var writeBuf = FanatecTuningController.BuildWriteBuffer(readBuf);

            Assert.Equal(0xFF, writeBuf[0]);
            Assert.Equal(FanatecTuningController.CMD_CLASS, writeBuf[1]);
            Assert.Equal(FanatecTuningController.SUBCMD_WRITE, writeBuf[2]);
        }

        [Fact]
        public void BuildWriteBuffer_ShiftsPayload()
        {
            var readBuf = FakeReadResponse(0x01);
            // Put a marker at read position 2 (device ID)
            readBuf[2] = 0xAA;
            readBuf[3] = 0xBB;

            var writeBuf = FanatecTuningController.BuildWriteBuffer(readBuf);

            // readBuf[2] should appear at writeBuf[3]
            Assert.Equal(0xAA, writeBuf[3]);
            Assert.Equal(0xBB, writeBuf[4]);
        }

        [Fact]
        public void BuildWriteBuffer_Length64()
        {
            var readBuf = new byte[64];
            var writeBuf = FanatecTuningController.BuildWriteBuffer(readBuf);

            Assert.Equal(64, writeBuf.Length);
        }

        // ── IsConnected ──────────────────────────────────────────────────

        [Fact]
        public void IsConnected_DelegatesToTransport()
        {
            var transport = new StubTransport { IsConnected = false };
            var controller = new FanatecTuningController(transport);
            Assert.False(controller.IsConnected);

            transport.IsConnected = true;
            Assert.True(controller.IsConnected);
        }

        // ── ReadEncoderMode ──────────────────────────────────────────────

        [Fact]
        public void ReadEncoderMode_ReturnsMode_WhenDeviceResponds()
        {
            var transport = new StubTransport();
            transport.ReadQueue.Enqueue(FakeReadResponse((byte)EncoderMode.Encoder));

            var controller = new FanatecTuningController(transport);
            var result = controller.ReadEncoderMode();

            Assert.Equal(EncoderMode.Encoder, result);
        }

        [Fact]
        public void ReadEncoderMode_ReturnsNull_WhenDisconnected()
        {
            var transport = new StubTransport { IsConnected = false };
            var controller = new FanatecTuningController(transport);

            Assert.Null(controller.ReadEncoderMode());
        }

        [Fact]
        public void ReadEncoderMode_ReturnsNull_WhenUnknownByte()
        {
            var transport = new StubTransport();
            transport.ReadQueue.Enqueue(FakeReadResponse(0xFE)); // not a valid EncoderMode

            string lastWarn = null;
            var controller = new FanatecTuningController(transport, logWarn: msg => lastWarn = msg);
            var result = controller.ReadEncoderMode();

            Assert.Null(result);
            Assert.Contains("unknown mode byte", lastWarn);
        }

        // ── ReadTuningStateRaw ───────────────────────────────────────────

        [Fact]
        public void ReadTuningStateRaw_Returns64Bytes()
        {
            var transport = new StubTransport();
            transport.ReadQueue.Enqueue(FakeReadResponse(0x01));

            var controller = new FanatecTuningController(transport);
            var raw = controller.ReadTuningStateRaw();

            Assert.NotNull(raw);
            Assert.Equal(64, raw.Length);
        }

        [Fact]
        public void ReadTuningStateRaw_ReturnsNull_WhenDisconnected()
        {
            var transport = new StubTransport { IsConnected = false };
            var controller = new FanatecTuningController(transport);

            Assert.Null(controller.ReadTuningStateRaw());
        }

        // ── SetEncoderMode ───────────────────────────────────────────────

        [Fact]
        public void SetEncoderMode_SendsWriteAndAckBurst()
        {
            var transport = new StubTransport();
            // Enqueue the read response for the read-modify-write cycle
            transport.ReadQueue.Enqueue(FakeReadResponse((byte)EncoderMode.Encoder));

            var controller = new FanatecTuningController(transport);
            bool ok = controller.SetEncoderMode(EncoderMode.Pulse);

            Assert.True(ok);

            // col03: 1 read request + 1 write = 2 reports
            Assert.Equal(2, transport.SentCol03Reports.Count);

            // The write report should have the new encoder mode
            var writeReport = transport.SentCol03Reports[1];
            Assert.Equal(0xFF, writeReport[0]);
            Assert.Equal(FanatecTuningController.CMD_CLASS, writeReport[1]);
            Assert.Equal(FanatecTuningController.SUBCMD_WRITE, writeReport[2]);
            Assert.Equal((byte)EncoderMode.Pulse, writeReport[FanatecTuningController.WRITE_ENCODER_MODE_OFFSET]);

            // col01: ack burst = ACK_BURST_COUNT * 2 (on + off)
            Assert.Equal(FanatecTuningController.ACK_BURST_COUNT * 2, transport.SentCol01Reports.Count);
        }

        [Fact]
        public void SetEncoderMode_ReturnsFalse_WhenReadFails()
        {
            var transport = new StubTransport();
            // Don't enqueue any read response — read will fail via timeout
            var controller = new FanatecTuningController(transport);
            bool ok = controller.SetEncoderMode(EncoderMode.Pulse);

            Assert.False(ok);
            // No write should have been sent (only the read request)
            Assert.Equal(1, transport.SentCol03Reports.Count);
            Assert.Empty(transport.SentCol01Reports);
        }

        [Fact]
        public void SetEncoderMode_AckBurst_HasCorrectPacketStructure()
        {
            var transport = new StubTransport();
            transport.ReadQueue.Enqueue(FakeReadResponse((byte)EncoderMode.Encoder));

            var controller = new FanatecTuningController(transport);
            controller.SetEncoderMode(EncoderMode.Pulse);

            // Verify the on-packet structure
            var onPacket = transport.SentCol01Reports[0];
            Assert.Equal(0x01, onPacket[0]);
            Assert.Equal(0xF8, onPacket[1]);
            Assert.Equal(0x09, onPacket[2]);
            Assert.Equal(0x01, onPacket[3]);
            Assert.Equal(FanatecTuningController.COL01_TUNING_ACK, onPacket[4]);
            Assert.Equal(0xFF, onPacket[5]);
            Assert.Equal(0x02, onPacket[6]);

            // Verify the off-packet structure
            var offPacket = transport.SentCol01Reports[1];
            Assert.Equal(0x01, offPacket[0]);
            Assert.Equal(FanatecTuningController.COL01_TUNING_ACK, offPacket[4]);
            Assert.Equal(0x00, offPacket[5]);
            Assert.Equal(0x00, offPacket[6]);
        }

        // ── Constructor ──────────────────────────────────────────────────

        [Fact]
        public void Constructor_NullTransport_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => new FanatecTuningController(null));
        }
    }
}
