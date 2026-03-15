using System;
using System.Threading;

namespace FanaBridge.Core
{
    /// <summary>
    /// Manages the Fanatec tuning configuration protocol (col03, cmd class 0x03).
    /// Handles the read-modify-write cycle for encoder mode and exposes raw
    /// tuning state reads.
    ///
    /// All HID I/O goes through <see cref="IDeviceTransport"/>, making the
    /// protocol logic testable without hardware.
    /// </summary>
    public class FanatecTuningController
    {
        // ── Protocol constants ───────────────────────────────────────────
        internal const int REPORT_LENGTH = 64;
        internal const byte CMD_CLASS = 0x03;
        internal const byte SUBCMD_WRITE = 0x00;
        internal const byte SUBCMD_READ = 0x02;
        internal const byte DEVICE_ID_BMR = 0x02;
        internal const int READ_TIMEOUT_MS = 1000;
        internal const int DRAIN_TIMEOUT_MS = 50;

        internal const int READ_ENCODER_MODE_OFFSET = 18;
        internal const int WRITE_ENCODER_MODE_OFFSET = 19;

        internal const byte COL01_TUNING_ACK = 0x06;
        internal const int ACK_BURST_COUNT = 4;
        internal const int ACK_BURST_DELAY_MS = 1;

        private readonly IDeviceTransport _transport;
        private readonly Action<string> _logWarn;
        private readonly Action<string> _logInfo;

        /// <param name="transport">HID transport for col03/col01 I/O.</param>
        /// <param name="logWarn">Optional warning logger (defaults to no-op).</param>
        /// <param name="logInfo">Optional info logger (defaults to no-op).</param>
        public FanatecTuningController(
            IDeviceTransport transport,
            Action<string> logWarn = null,
            Action<string> logInfo = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _logWarn = logWarn ?? (_ => { });
            _logInfo = logInfo ?? (_ => { });
        }

        /// <summary>Whether the underlying transport is connected.</summary>
        public bool IsConnected => _transport.IsConnected;

        /// <summary>
        /// Sets the encoder operating mode on the connected button module.
        /// Uses a read-modify-write cycle: reads the device's current tuning
        /// state, modifies the encoder mode byte, and writes it back.
        ///
        /// EXPERIMENTAL: Only the encoder mode byte is changed; all other
        /// tuning parameters are preserved from the current device state.
        /// </summary>
        /// <returns>True if the HID writes succeeded.</returns>
        public bool SetEncoderMode(EncoderMode mode)
        {
            using (_transport.BeginBatch())
            {
                _logInfo("FanatecTuning: Setting encoder mode to " + mode
                    + " (0x" + ((byte)mode).ToString("X2") + ")");

                // 1. Read current tuning state
                var readBuf = ReadTuningState(DEVICE_ID_BMR);
                if (readBuf == null)
                {
                    _logWarn("FanatecTuning: Cannot set encoder mode — failed to read current tuning state");
                    return false;
                }

                _logInfo("FanatecTuning: Current tuning state read OK (byte["
                    + READ_ENCODER_MODE_OFFSET + "]=0x"
                    + readBuf[READ_ENCODER_MODE_OFFSET].ToString("X2") + ")");

                // 2. Build the WRITE buffer
                var writeBuf = BuildWriteBuffer(readBuf);

                // 3. Set the encoder mode in the WRITE buffer
                writeBuf[WRITE_ENCODER_MODE_OFFSET] = (byte)mode;

                // 4. Write the buffer
                bool ok = _transport.SendCol03(writeBuf);
                if (!ok)
                {
                    _logWarn("FanatecTuning: Failed to send tuning config report");
                    return false;
                }

                // 5. Send col01 0x06 acknowledgement burst
                ok = SendAckBurst();
                if (!ok)
                    _logWarn("FanatecTuning: Ack burst failed (mode may still have been set)");

                return ok;
            }
        }

        /// <summary>
        /// Reads the current encoder mode from the connected button module.
        /// Read-only operation.
        /// </summary>
        /// <returns>The current encoder mode, or null if the read fails.</returns>
        public EncoderMode? ReadEncoderMode()
        {
            using (_transport.BeginBatch())
            {
                var readBuf = ReadTuningState(DEVICE_ID_BMR);
                if (readBuf == null)
                {
                    _logWarn("FanatecTuning: ReadEncoderMode — failed to read tuning state");
                    return null;
                }

                byte raw = readBuf[READ_ENCODER_MODE_OFFSET];
                if (Enum.IsDefined(typeof(EncoderMode), raw))
                    return (EncoderMode)raw;

                _logWarn("FanatecTuning: ReadEncoderMode — unknown mode byte 0x" + raw.ToString("X2"));
                return null;
            }
        }

        /// <summary>
        /// Returns the raw 64-byte tuning state from the button module,
        /// or null on failure. Read-only.
        /// </summary>
        public byte[] ReadTuningStateRaw()
        {
            using (_transport.BeginBatch())
            {
                return ReadTuningState(DEVICE_ID_BMR);
            }
        }

        // ── Protocol internals (internal for testing) ────────────────────

        /// <summary>
        /// Builds a WRITE buffer from a READ response by shifting the payload
        /// right by one byte and inserting the WRITE subcode.
        /// </summary>
        internal static byte[] BuildWriteBuffer(byte[] readBuf)
        {
            var writeBuf = new byte[REPORT_LENGTH];
            writeBuf[0] = 0xFF;
            writeBuf[1] = CMD_CLASS;
            writeBuf[2] = SUBCMD_WRITE;
            Array.Copy(readBuf, 2, writeBuf, 3, REPORT_LENGTH - 3);
            return writeBuf;
        }

        /// <summary>
        /// Reads the current tuning state from the device.
        /// Sends a READ request on col03 and waits for the matching response.
        /// Must be called inside a batch.
        /// </summary>
        private byte[] ReadTuningState(byte deviceId)
        {
            if (!_transport.IsConnected) return null;

            var request = new byte[REPORT_LENGTH];
            request[0] = 0xFF;
            request[1] = CMD_CLASS;
            request[2] = SUBCMD_READ;

            try
            {
                int maxInputLen = _transport.Col03MaxInputReportLength;
                if (maxInputLen <= 0) maxInputLen = REPORT_LENGTH;
                var buf = new byte[maxInputLen];

                // Drain any stale input reports (short timeout)
                while (_transport.ReadCol03(buf, DRAIN_TIMEOUT_MS) >= 0) { }

                // Send the read request on col03
                _transport.SendCol03(request);

                // Read responses until we get the matching tuning state
                var deadline = DateTime.UtcNow.AddMilliseconds(READ_TIMEOUT_MS);

                while (DateTime.UtcNow < deadline)
                {
                    int bytesRead = _transport.ReadCol03(buf, READ_TIMEOUT_MS);
                    if (bytesRead >= 3 &&
                        buf[0] == 0xFF &&
                        buf[1] == CMD_CLASS &&
                        buf[2] == deviceId)
                    {
                        var result = new byte[REPORT_LENGTH];
                        Array.Copy(buf, result, Math.Min(bytesRead, REPORT_LENGTH));
                        return result;
                    }
                }

                _logWarn("FanatecTuning: Read — no matching response within timeout");
                return null;
            }
            catch (Exception ex)
            {
                _logWarn("FanatecTuning: Read error: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Sends the col01 sub-command 0x06 burst pattern after tuning writes.
        /// Must be called inside the write lock.
        /// </summary>
        private bool SendAckBurst()
        {
            var onPacket = new byte[8];
            onPacket[0] = 0x01;
            onPacket[1] = 0xF8;
            onPacket[2] = 0x09;
            onPacket[3] = 0x01;
            onPacket[4] = COL01_TUNING_ACK;
            onPacket[5] = 0xFF;
            onPacket[6] = 0x02;
            onPacket[7] = 0x00;

            var offPacket = new byte[8];
            offPacket[0] = 0x01;
            offPacket[1] = 0xF8;
            offPacket[2] = 0x09;
            offPacket[3] = 0x01;
            offPacket[4] = COL01_TUNING_ACK;
            offPacket[5] = 0x00;
            offPacket[6] = 0x00;
            offPacket[7] = 0x00;

            bool ok = true;
            for (int i = 0; i < ACK_BURST_COUNT; i++)
            {
                ok = _transport.SendCol01(onPacket) && ok;
                Thread.Sleep(ACK_BURST_DELAY_MS);
                ok = _transport.SendCol01(offPacket) && ok;
                Thread.Sleep(ACK_BURST_DELAY_MS);
            }

            return ok;
        }
    }
}
