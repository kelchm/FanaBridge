using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace FanaBridge
{
    /// <summary>
    /// Encoder operating mode for Fanatec button modules.
    /// Sent as byte 19 in the col03 tuning configuration report (cmd 0x03).
    /// </summary>
    public enum EncoderMode : byte
    {
        /// <summary>Relative / incremental — sends CW/CCW pulses.</summary>
        Encoder = 0x00,
        /// <summary>Absolute — sends position as individual button presses (pulse).</summary>
        Pulse = 0x01,
        /// <summary>Absolute — holds button for current position (constant).</summary>
        Constant = 0x02,
        /// <summary>Firmware auto-selects between modes based on interaction.</summary>
        Auto = 0x03,
    }
    /// <summary>
    /// HID communication layer for Fanatec wheel LED and display control.
    /// Ported from the standalone PoC to .NET Framework 4.8.
    /// </summary>
    public class FanatecDevice : IDisposable
    {
        // ── Protocol constants (col03 report format) ─────────────────────
        private const int LED_REPORT_LENGTH = 64;
        private const int DISPLAY_REPORT_LENGTH = 8;
        private const int REPORT_HEADER_SIZE = 3;   // [0xFF, 0x01, subcmd]
        private const int MAX_RGB565_PER_REPORT = (LED_REPORT_LENGTH - REPORT_HEADER_SIZE) / 2;  // 30

        // Col03 LED report sub-commands
        private const byte SUBCMD_REV_COLORS = 0x00;
        private const byte SUBCMD_FLAG_COLORS = 0x01;
        private const byte SUBCMD_BUTTON_COLORS = 0x02;
        private const byte SUBCMD_BUTTON_INTENSITIES = 0x03;

        // Button LED staging protocol — fixed byte offsets in the 64-byte report.
        // The commit-byte position limits button LEDs to MAX_BUTTON_LEDS.
        private const int BUTTON_COLOR_COMMIT_OFFSET = 27;
        private const int BUTTON_INTENSITY_COMMIT_OFFSET = 18;
        private const int MAX_BUTTON_LEDS = (BUTTON_COLOR_COMMIT_OFFSET - REPORT_HEADER_SIZE) / 2;  // 12

        /// <summary>
        /// Total bytes in the subcmd 0x03 intensity payload.
        /// Includes per-button intensity slots plus additional slots whose
        /// meaning varies by wheel (e.g. encoder indicator LEDs).
        /// </summary>
        public const int INTENSITY_PAYLOAD_SIZE = 16;

        // ── Tuning configuration report (col03, cmd class 0x03) ──────────
        // Byte layout differs between READ response and WRITE command:
        //   READ  response: [ff 03] [deviceId] [payload 0..60]  — 64 bytes
        //   WRITE command:  [ff 03] [subcmd=00] [deviceId] [payload 0..59] — 64 bytes
        // The WRITE format inserts the subcode at byte[2], shifting the
        // device-ID and payload right by one position.
        //
        // Subcodes (byte[2] in WRITE, or implicit in READ):
        //   0x00 = WRITE,  0x02 = READ,  0x03 = SAVE,
        //   0x04 = RESET,  0x06 = TOGGLE (standard/simplified mode).
        private const int TUNING_REPORT_LENGTH = LED_REPORT_LENGTH;  // 64 bytes, same interface
        private const byte TUNING_CMD_CLASS = 0x03;
        private const byte TUNING_SUBCMD_WRITE = 0x00;
        private const byte TUNING_SUBCMD_READ  = 0x02;
        private const byte TUNING_DEVICE_ID_BMR = 0x02;  // sub-device: Button Module Rally
        private const int TUNING_READ_TIMEOUT_MS = 1000;

        // Encoder mode byte position in the READ response (0-indexed).
        // In the WRITE buffer this shifts to READ offset + 1 due to the
        // inserted subcode byte.
        private const int TUNING_READ_ENCODER_MODE_OFFSET = 18;
        private const int TUNING_WRITE_ENCODER_MODE_OFFSET = 19;

        // Write buffer offsets 26/27/28 are unknown tuning params
        // (SDK normalizes 0→1, valid range 1–3).  Tested as per-encoder
        // mode selectors and apply-masks — neither hypothesis panned out.
        // They do NOT control per-encoder mode; encoder mode is global.

        // Col01 sub-command 0x06 — sent as a burst after tuning writes.
        // Purpose not fully understood; may be required for firmware to
        // acknowledge the configuration change.
        private const byte COL01_SUBCMD_TUNING_ACK = 0x06;
        private const int TUNING_ACK_BURST_COUNT = 4;
        private const int TUNING_ACK_BURST_DELAY_MS = 1;

        // ── Dirty tracking — skip redundant HID writes ───────────────────
        // Color tracking keyed by subcmd; missing entry = dirty (forces send).
        private readonly Dictionary<byte, ushort[]> _lastColors = new Dictionary<byte, ushort[]>();
        // Button intensity tracking (separate: unique staging protocol, byte[] payload)
        private byte[] _lastIntensities;

        // ── Pooled report buffers — avoid per-frame heap allocations ─────
        private readonly byte[] _ledReportBuf = new byte[LED_REPORT_LENGTH];
        private readonly byte[] _displayReportBuf = new byte[DISPLAY_REPORT_LENGTH];
        private readonly byte[] _displayTextSegs = new byte[3];  // reusable for DisplayText

        private HidDevice _ledDevice;       // col03: 64-byte LED control
        private HidStream _ledStream;

        private HidDevice _displayDevice;   // col01: 8-byte display/config
        private HidStream _displayStream;

        // Windows API fallback for col01 writes
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(
            SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        private SafeFileHandle _displayHandle;

        /// <summary>Product name from HID descriptor, or null if not connected.</summary>
        public string ProductName { get; private set; }

        /// <summary>The product ID we connected to, for presence checks.</summary>
        private int _connectedProductId;

        /// <summary>True if the HID streams appear to be open.</summary>
        public bool IsConnected
        {
            get
            {
                try
                {
                    return _ledStream != null && _ledStream.CanWrite;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Checks whether the USB device is still present on the HID bus.
        /// More reliable than IsConnected for detecting power-off / unplug.
        /// </summary>
        public bool IsDevicePresent
        {
            get
            {
                if (_connectedProductId == 0) return false;
                try
                {
                    return DeviceList.Local.GetHidDevices()
                        .Any(d => d.VendorID == FanatecSdkManager.FANATEC_VENDOR_ID
                               && d.ProductID == _connectedProductId);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Connects to a Fanatec device by product ID.
        /// Opens the col03 (LED) and col01 (display) HID interfaces.
        /// The product ID should come from FanatecSdkManager.ConnectedProductId.
        /// </summary>
        public bool Connect(int productId)
        {
            // Release any previous connection to prevent resource leaks
            Disconnect();

            try
            {
                var devices = DeviceList.Local.GetHidDevices()
                    .Where(d => d.VendorID == FanatecSdkManager.FANATEC_VENDOR_ID && d.ProductID == productId)
                    .ToList();

                if (devices.Count == 0)
                {
                    SimHub.Logging.Current.Info("FanatecDevice: No devices found for PID 0x" + productId.ToString("X4"));
                    return false;
                }

                // Find interfaces by HID collection from device path
                _ledDevice = devices.FirstOrDefault(d => d.DevicePath.Contains("col03"));
                _displayDevice = devices.FirstOrDefault(d => d.DevicePath.Contains("col01"));

                // Fallback: find LED interface by max output report length
                if (_ledDevice == null)
                {
                    _ledDevice = devices.FirstOrDefault(d =>
                        d.GetMaxOutputReportLength() == LED_REPORT_LENGTH ||
                        d.GetMaxOutputReportLength() == LED_REPORT_LENGTH + 1);
                }

                if (_ledDevice == null)
                {
                    SimHub.Logging.Current.Warn("FanatecDevice: No LED control interface (col03) found");
                    return false;
                }

                _ledStream = _ledDevice.Open();
                SimHub.Logging.Current.Info(string.Format(
                    "FanatecDevice: LED interface opened (MaxOutput={0})", _ledDevice.GetMaxOutputReportLength()));

                // Open col01 via HidStream (interrupt OUT — confirmed working in PoC)
                if (_displayDevice != null)
                {
                    try
                    {
                        _displayStream = _displayDevice.Open();
                        SimHub.Logging.Current.Info("FanatecDevice: Display interface (col01) opened via HidStream");
                    }
                    catch (Exception ex)
                    {
                        SimHub.Logging.Current.Warn("FanatecDevice: col01 HidStream failed: " + ex.Message);
                    }

                    // Also open raw handle as fallback
                    try
                    {
                        _displayHandle = CreateFile(
                            _displayDevice.DevicePath,
                            GENERIC_READ | GENERIC_WRITE,
                            FILE_SHARE_READ | FILE_SHARE_WRITE,
                            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                        if (_displayHandle.IsInvalid)
                        {
                            _displayHandle = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        SimHub.Logging.Current.Warn("FanatecDevice: col01 WriteFile handle failed: " + ex.Message);
                    }
                }

                try
                {
                    ProductName = _ledDevice.GetProductName();
                }
                catch
                {
                    ProductName = "Fanatec Device";
                }

                _connectedProductId = productId;
                SimHub.Logging.Current.Info("FanatecDevice: Connected to " + ProductName);
                return true;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("FanatecDevice: Connection error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Sends a 64-byte report on the LED interface (col03).
        /// </summary>
        private bool SendLedReport(byte[] data)
        {
            if (_ledStream == null) return false;

            try
            {
                _ledStream.Write(data);
                return true;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("FanatecDevice: LED write error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Sends an 8-byte report on the display interface (col01).
        /// Uses HidStream (interrupt OUT) as primary, WriteFile as fallback.
        /// </summary>
        private bool SendDisplayReport(byte[] data)
        {
            if (data.Length != DISPLAY_REPORT_LENGTH) return false;

            if (_displayStream != null)
            {
                try
                {
                    _displayStream.Write(data);
                    return true;
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn("FanatecDevice: Display stream write failed: " + ex.Message);
                }
            }

            if (_displayHandle != null && !_displayHandle.IsInvalid)
            {
                uint written;
                if (WriteFile(_displayHandle, data, (uint)data.Length, out written, IntPtr.Zero))
                {
                    return true;
                }
            }

            return false;
        }

        // =====================================================================
        // LED CONTROL
        // =====================================================================

        /// <summary>
        /// Sets button LED colors and the full intensity report using the staged
        /// commit protocol (subcmd 0x02 colors + subcmd 0x03 intensities).
        /// Skips HID writes when neither array has changed.
        /// </summary>
        /// <param name="colors">Per-button RGB565 values (max <see cref="MAX_BUTTON_LEDS"/>).</param>
        /// <param name="intensityPayload">Pre-composed intensity payload, exactly
        /// <see cref="INTENSITY_PAYLOAD_SIZE"/> bytes. The caller is responsible for
        /// placing button intensities, encoder intensities, etc. at the correct
        /// offsets for the current wheel configuration.</param>
        public bool SetButtonLedState(ushort[] colors, byte[] intensityPayload)
        {
            if (colors == null || colors.Length == 0 || colors.Length > MAX_BUTTON_LEDS) return false;
            if (intensityPayload == null || intensityPayload.Length != INTENSITY_PAYLOAD_SIZE) return false;

            int ledCount = colors.Length;

            // Check color changes via dictionary-based tracking
            ushort[] lastC;
            bool colorsChanged = true;
            if (_lastColors.TryGetValue(SUBCMD_BUTTON_COLORS, out lastC) && lastC.Length == ledCount)
            {
                colorsChanged = false;
                for (int i = 0; i < ledCount; i++)
                {
                    if (colors[i] != lastC[i]) { colorsChanged = true; break; }
                }
            }

            // Check intensity changes
            bool intensitiesChanged = true;
            if (_lastIntensities != null && _lastIntensities.Length == INTENSITY_PAYLOAD_SIZE)
            {
                intensitiesChanged = false;
                for (int i = 0; i < INTENSITY_PAYLOAD_SIZE; i++)
                {
                    if (intensityPayload[i] != _lastIntensities[i]) { intensitiesChanged = true; break; }
                }
            }

            if (!colorsChanged && !intensitiesChanged)
                return true;

            // Stage whichever reports changed, then commit with the last one.
            bool ok = true;

            if (colorsChanged && intensitiesChanged)
            {
                ok = SendButtonColorReport(colors, commit: false);
                ok = SendButtonIntensityReport(intensityPayload, commit: true) && ok;
            }
            else if (colorsChanged)
            {
                ok = SendButtonColorReport(colors, commit: true);
            }
            else
            {
                ok = SendButtonIntensityReport(intensityPayload, commit: true);
            }

            if (ok)
            {
                if (lastC == null || lastC.Length != ledCount)
                {
                    lastC = new ushort[ledCount];
                    _lastColors[SUBCMD_BUTTON_COLORS] = lastC;
                }
                Array.Copy(colors, lastC, ledCount);

                if (_lastIntensities == null)
                    _lastIntensities = new byte[INTENSITY_PAYLOAD_SIZE];
                Array.Copy(intensityPayload, _lastIntensities, INTENSITY_PAYLOAD_SIZE);
            }

            return ok;
        }

        /// <summary>
        /// Sets Rev LED colors via col03 (subcmd 0x00, per-LED RGB565).
        /// Color 0x0000 = off; non-zero = on with that color.
        /// Array length defines the LED count; dirty tracking is automatic.
        /// </summary>
        public bool SetRevLedColors(ushort[] colors)
        {
            return colors != null && SendSimpleLedColors(SUBCMD_REV_COLORS, colors);
        }

        /// <summary>
        /// Sets Flag LED colors via col03 (subcmd 0x01, per-LED RGB565).
        /// </summary>
        public bool SetFlagLedColors(ushort[] colors)
        {
            return colors != null && SendSimpleLedColors(SUBCMD_FLAG_COLORS, colors);
        }

        // ── Low-level report senders ─────────────────────────────────────

        /// <summary>
        /// Sends a simple (non-staged) LED color report.
        /// Builds a col03 report: [0xFF, 0x01, subcmd, ...RGB565 big-endian...].
        /// Skips the HID write when colors haven't changed since the last send.
        /// Uses the pooled _ledReportBuf to avoid per-frame allocations.
        /// </summary>
        private bool SendSimpleLedColors(byte subcmd, ushort[] colors)
        {
            int count = colors.Length;
            if (count == 0 || count > MAX_RGB565_PER_REPORT) return false;

            // Dirty check: missing entry or size mismatch forces a send
            ushort[] last;
            if (_lastColors.TryGetValue(subcmd, out last) && last.Length == count)
            {
                bool changed = false;
                for (int i = 0; i < count; i++)
                {
                    if (colors[i] != last[i]) { changed = true; break; }
                }
                if (!changed) return true;
            }

            // Reuse pooled buffer — zero the payload region then fill
            Array.Clear(_ledReportBuf, 0, LED_REPORT_LENGTH);
            _ledReportBuf[0] = 0xFF;
            _ledReportBuf[1] = 0x01;
            _ledReportBuf[2] = subcmd;

            for (int i = 0; i < count; i++)
            {
                int offset = REPORT_HEADER_SIZE + (i * 2);
                _ledReportBuf[offset]     = (byte)((colors[i] >> 8) & 0xFF);
                _ledReportBuf[offset + 1] = (byte)(colors[i] & 0xFF);
            }

            bool ok = SendLedReport(_ledReportBuf);
            if (ok)
            {
                if (last == null || last.Length != count)
                {
                    last = new ushort[count];
                    _lastColors[subcmd] = last;
                }
                Array.Copy(colors, last, count);
            }
            return ok;
        }

        private bool SendButtonColorReport(ushort[] colors, bool commit)
        {
            Array.Clear(_ledReportBuf, 0, LED_REPORT_LENGTH);
            _ledReportBuf[0] = 0xFF;
            _ledReportBuf[1] = 0x01;
            _ledReportBuf[2] = SUBCMD_BUTTON_COLORS;

            for (int i = 0; i < colors.Length; i++)
            {
                int offset = REPORT_HEADER_SIZE + (i * 2);
                _ledReportBuf[offset]     = (byte)((colors[i] >> 8) & 0xFF);
                _ledReportBuf[offset + 1] = (byte)(colors[i] & 0xFF);
            }

            _ledReportBuf[BUTTON_COLOR_COMMIT_OFFSET] = commit ? (byte)0x01 : (byte)0x00;
            return SendLedReport(_ledReportBuf);
        }

        private bool SendButtonIntensityReport(byte[] intensities, bool commit)
        {
            Array.Clear(_ledReportBuf, 0, LED_REPORT_LENGTH);
            _ledReportBuf[0] = 0xFF;
            _ledReportBuf[1] = 0x01;
            _ledReportBuf[2] = SUBCMD_BUTTON_INTENSITIES;

            Array.Copy(intensities, 0, _ledReportBuf, REPORT_HEADER_SIZE, intensities.Length);
            _ledReportBuf[BUTTON_INTENSITY_COMMIT_OFFSET] = commit ? (byte)0x01 : (byte)0x00;
            return SendLedReport(_ledReportBuf);
        }

        // =====================================================================
        // ENCODER / TUNING CONFIGURATION
        // =====================================================================

        /// <summary>
        /// Reads the current tuning state from the device.
        /// Sends an <c>ff 03 &lt;deviceId&gt; 00...00</c> request on col03
        /// and waits for the device's response on the col03 input endpoint.
        /// </summary>
        /// <returns>The 64-byte tuning state, or null on failure.</returns>
        private byte[] ReadTuningState(byte deviceId)
        {
            if (_ledStream == null) return null;

            var request = new byte[TUNING_REPORT_LENGTH];
            request[0] = 0xFF;
            request[1] = TUNING_CMD_CLASS;
            request[2] = TUNING_SUBCMD_READ;
            // bytes 3-63 are zero = "give me the current state"

            try
            {
                int savedTimeout = _ledStream.ReadTimeout;
                _ledStream.ReadTimeout = TUNING_READ_TIMEOUT_MS;

                try
                {
                    int maxInputLen = _ledDevice.GetMaxInputReportLength();
                    if (maxInputLen <= 0) maxInputLen = TUNING_REPORT_LENGTH;
                    var buf = new byte[maxInputLen];

                    // Drain any stale input reports before sending the read
                    // request.  After a write+ack cycle the device may have
                    // queued confirmation reports that would otherwise be
                    // mistaken for the fresh response.
                    int savedDrainTimeout = _ledStream.ReadTimeout;
                    _ledStream.ReadTimeout = 50;   // short timeout — just drain
                    try
                    {
                        while (true)
                            _ledStream.Read(buf, 0, buf.Length);
                    }
                    catch (TimeoutException) { /* buffer drained */ }
                    finally
                    {
                        _ledStream.ReadTimeout = savedDrainTimeout;
                    }

                    // Send the read request on col03
                    _ledStream.Write(request);

                    // Read responses until we get the matching tuning state
                    // (skip any unrelated input reports)
                    var deadline = DateTime.UtcNow.AddMilliseconds(TUNING_READ_TIMEOUT_MS);

                    while (DateTime.UtcNow < deadline)
                    {
                        int bytesRead = _ledStream.Read(buf, 0, buf.Length);
                        if (bytesRead >= 3 &&
                            buf[0] == 0xFF &&
                            buf[1] == TUNING_CMD_CLASS &&
                            buf[2] == deviceId)
                        {
                            var result = new byte[TUNING_REPORT_LENGTH];
                            Array.Copy(buf, result, Math.Min(bytesRead, TUNING_REPORT_LENGTH));
                            return result;
                        }
                    }

                    SimHub.Logging.Current.Warn("FanatecDevice: Tuning read — no matching response within timeout");
                    return null;
                }
                finally
                {
                    _ledStream.ReadTimeout = savedTimeout;
                }
            }
            catch (TimeoutException)
            {
                SimHub.Logging.Current.Warn("FanatecDevice: Tuning read timed out");
                return null;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("FanatecDevice: Tuning read error: " + ex.Message);
                return null;
            }
        }

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
            // Feature flag gate — refuse to write if tuning is disabled
            var settings = FanatecPlugin.Instance?.Settings;
            if (settings == null || !settings.EnableTuning)
            {
                SimHub.Logging.Current.Warn(
                    "FanatecDevice: SetEncoderMode blocked — tuning feature is disabled");
                return false;
            }

            SimHub.Logging.Current.Info(
                "FanatecDevice: Setting encoder mode to " + mode + " (0x" + ((byte)mode).ToString("X2") + ")");

            // 1. Read current tuning state
            var readBuf = ReadTuningState(TUNING_DEVICE_ID_BMR);
            if (readBuf == null)
            {
                SimHub.Logging.Current.Warn(
                    "FanatecDevice: Cannot set encoder mode — failed to read current tuning state");
                return false;
            }

            SimHub.Logging.Current.Info(
                "FanatecDevice: Current tuning state read OK (byte[" + TUNING_READ_ENCODER_MODE_OFFSET + "]=0x"
                + readBuf[TUNING_READ_ENCODER_MODE_OFFSET].ToString("X2") + ")");

            // 2. Build the WRITE buffer.
            //    READ response:  [ff 03] [deviceId] [payload...]
            //    WRITE command:  [ff 03] [00]       [deviceId] [payload...]
            //    Shift bytes 2..62 of the read response into bytes 3..63 of
            //    the write buffer, inserting the WRITE subcode at byte[2].
            var writeBuf = new byte[TUNING_REPORT_LENGTH];
            writeBuf[0] = 0xFF;
            writeBuf[1] = TUNING_CMD_CLASS;
            writeBuf[2] = TUNING_SUBCMD_WRITE;
            Array.Copy(readBuf, 2, writeBuf, 3, TUNING_REPORT_LENGTH - 3);

            // 3. Set the encoder mode in the WRITE buffer
            writeBuf[TUNING_WRITE_ENCODER_MODE_OFFSET] = (byte)mode;

            // 4. Write the buffer
            bool ok = SendLedReport(writeBuf);
            if (!ok)
            {
                SimHub.Logging.Current.Warn("FanatecDevice: Failed to send tuning config report");
                return false;
            }

            // 5. Send col01 0x06 acknowledgement burst
            ok = SendTuningAckBurst();
            if (!ok)
                SimHub.Logging.Current.Warn("FanatecDevice: Tuning ack burst failed (mode may still have been set)");

            return ok;
        }

        /// <summary>
        /// Reads the current encoder mode from the connected button module.
        /// Does NOT require the tuning feature flag — read-only operation.
        /// </summary>
        /// <returns>The current encoder mode, or null if the read fails.</returns>
        public EncoderMode? ReadEncoderMode()
        {
            var readBuf = ReadTuningState(TUNING_DEVICE_ID_BMR);
            if (readBuf == null)
            {
                SimHub.Logging.Current.Warn(
                    "FanatecDevice: ReadEncoderMode — failed to read tuning state");
                return null;
            }

            byte raw = readBuf[TUNING_READ_ENCODER_MODE_OFFSET];
            if (Enum.IsDefined(typeof(EncoderMode), raw))
                return (EncoderMode)raw;

            SimHub.Logging.Current.Warn(
                "FanatecDevice: ReadEncoderMode — unknown mode byte 0x" + raw.ToString("X2"));
            return null;
        }

        /// <summary>
        /// Returns the raw 64-byte tuning state from the button module,
        /// or null on failure.  Read-only, no feature flag required.
        /// </summary>
        public byte[] ReadTuningStateRaw()
        {
            return ReadTuningState(TUNING_DEVICE_ID_BMR);
        }

        /// <summary>
        /// Sends the col01 sub-command 0x06 burst pattern observed after every
        /// tuning configuration write.  The burst consists of alternating
        /// "on" (ff 02 00) and "off" (00 00 00) packets.
        /// </summary>
        private bool SendTuningAckBurst()
        {
            var onPacket = new byte[DISPLAY_REPORT_LENGTH];
            onPacket[0] = 0x01;  // Report ID
            onPacket[1] = 0xF8;
            onPacket[2] = 0x09;
            onPacket[3] = 0x01;
            onPacket[4] = COL01_SUBCMD_TUNING_ACK;
            onPacket[5] = 0xFF;
            onPacket[6] = 0x02;
            onPacket[7] = 0x00;

            var offPacket = new byte[DISPLAY_REPORT_LENGTH];
            offPacket[0] = 0x01;
            offPacket[1] = 0xF8;
            offPacket[2] = 0x09;
            offPacket[3] = 0x01;
            offPacket[4] = COL01_SUBCMD_TUNING_ACK;
            offPacket[5] = 0x00;
            offPacket[6] = 0x00;
            offPacket[7] = 0x00;

            bool ok = true;
            for (int i = 0; i < TUNING_ACK_BURST_COUNT; i++)
            {
                ok = SendDisplayReport(onPacket) && ok;
                Thread.Sleep(TUNING_ACK_BURST_DELAY_MS);
                ok = SendDisplayReport(offPacket) && ok;
                Thread.Sleep(TUNING_ACK_BURST_DELAY_MS);
            }

            return ok;
        }

        // =====================================================================
        // DISPLAY CONTROL
        // =====================================================================

        /// <summary>
        /// Sets the 3-digit 7-segment display.
        /// Matches the Linux kernel driver ftec_set_display() protocol.
        /// </summary>
        public bool SetDisplay(byte seg1, byte seg2, byte seg3)
        {
            _displayReportBuf[0] = 0x01;  // Report ID
            _displayReportBuf[1] = 0xF8;
            _displayReportBuf[2] = 0x09;
            _displayReportBuf[3] = 0x01;
            _displayReportBuf[4] = 0x02;
            _displayReportBuf[5] = seg1;
            _displayReportBuf[6] = seg2;
            _displayReportBuf[7] = seg3;

            return SendDisplayReport(_displayReportBuf);
        }

        /// <summary>
        /// Blank the display.
        /// </summary>
        public bool ClearDisplay()
        {
            return SetDisplay(SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank);
        }

        /// <summary>
        /// Display a gear number: -1=R, 0=N, 1-9.
        /// </summary>
        public bool DisplayGear(int gear)
        {
            byte seg;
            switch (gear)
            {
                case -1: seg = SevenSegment.R; break;
                case 0:  seg = SevenSegment.N; break;
                case 1:  seg = SevenSegment.Digit1; break;
                case 2:  seg = SevenSegment.Digit2; break;
                case 3:  seg = SevenSegment.Digit3; break;
                case 4:  seg = SevenSegment.Digit4; break;
                case 5:  seg = SevenSegment.Digit5; break;
                case 6:  seg = SevenSegment.Digit6; break;
                case 7:  seg = SevenSegment.Digit7; break;
                case 8:  seg = SevenSegment.Digit8; break;
                case 9:  seg = SevenSegment.Digit9; break;
                default: seg = SevenSegment.N; break;
            }

            return SetDisplay(SevenSegment.Blank, seg, SevenSegment.Blank);
        }

        /// <summary>
        /// Display a speed value (0-999) on the 3-digit display.
        /// </summary>
        public bool DisplaySpeed(int speed)
        {
            if (speed < 0 || speed > 999) speed = 0;

            byte seg1 = SevenSegment.GetDigitSegment(speed / 100);
            byte seg2 = SevenSegment.GetDigitSegment((speed / 10) % 10);
            byte seg3 = SevenSegment.GetDigitSegment(speed % 10);

            return SetDisplay(seg1, seg2, seg3);
        }

        /// <summary>
        /// Display up to 3 characters of text. Dots/commas are folded onto predecessor.
        /// Uses a pooled buffer to avoid per-call allocations.
        /// </summary>
        public bool DisplayText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return ClearDisplay();

            int segCount = 0;
            _displayTextSegs[0] = SevenSegment.Blank;
            _displayTextSegs[1] = SevenSegment.Blank;
            _displayTextSegs[2] = SevenSegment.Blank;

            foreach (char ch in text)
            {
                if ((ch == '.' || ch == ',') && segCount > 0)
                {
                    _displayTextSegs[segCount - 1] |= SevenSegment.Dot;
                }
                else
                {
                    _displayTextSegs[segCount] = SevenSegment.CharToSegment(ch);
                    segCount++;
                }

                if (segCount >= 3) break;
            }

            return SetDisplay(_displayTextSegs[0], _displayTextSegs[1], _displayTextSegs[2]);
        }

        // =====================================================================
        // LIFECYCLE
        // =====================================================================

        /// <summary>
        /// Closes all HID handles.
        /// </summary>
        public void Disconnect()
        {
            try { _displayStream?.Close(); } catch { }
            try { _displayStream?.Dispose(); } catch { }
            try { _ledStream?.Close(); } catch { }
            try { _ledStream?.Dispose(); } catch { }
            try { _displayHandle?.Close(); } catch { }
            try { _displayHandle?.Dispose(); } catch { }

            _displayStream = null;
            _ledStream = null;
            _displayHandle = null;
            _ledDevice = null;
            _displayDevice = null;
            _connectedProductId = 0;
            ProductName = null;

            // Force resend on next connect
            _lastColors.Clear();
            _lastIntensities = null;
        }

        /// <summary>
        /// Marks LED state as dirty so the next SetLedState call always
        /// sends to hardware, even if the values haven't changed.
        /// Call this when the physical wheel rim changes — the firmware
        /// resets its LED state but our tracking arrays still hold the
        /// previous instance's last output.
        /// </summary>
        public void ForceDirty()
        {
            _lastColors.Clear();
            _lastIntensities = null;
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
