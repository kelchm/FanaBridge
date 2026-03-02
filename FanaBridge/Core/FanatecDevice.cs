using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace FanaBridge
{
    /// <summary>
    /// HID communication layer for Fanatec wheel LED and display control.
    /// Ported from the standalone PoC to .NET Framework 4.8.
    /// </summary>
    public class FanatecDevice
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

        // ── Dirty tracking — skip redundant HID writes ───────────────────
        // Color tracking keyed by subcmd; missing entry = dirty (forces send).
        private readonly Dictionary<byte, ushort[]> _lastColors = new Dictionary<byte, ushort[]>();
        // Button intensity tracking (separate: unique staging protocol, byte[] payload)
        private byte[] _lastIntensities;

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

            var report = new byte[LED_REPORT_LENGTH];
            report[0] = 0xFF;
            report[1] = 0x01;
            report[2] = subcmd;

            for (int i = 0; i < count; i++)
            {
                int offset = REPORT_HEADER_SIZE + (i * 2);
                report[offset]     = (byte)((colors[i] >> 8) & 0xFF);
                report[offset + 1] = (byte)(colors[i] & 0xFF);
            }

            bool ok = SendLedReport(report);
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
            var report = new byte[LED_REPORT_LENGTH];
            report[0] = 0xFF;
            report[1] = 0x01;
            report[2] = SUBCMD_BUTTON_COLORS;

            for (int i = 0; i < colors.Length; i++)
            {
                int offset = REPORT_HEADER_SIZE + (i * 2);
                report[offset]     = (byte)((colors[i] >> 8) & 0xFF);
                report[offset + 1] = (byte)(colors[i] & 0xFF);
            }

            report[BUTTON_COLOR_COMMIT_OFFSET] = commit ? (byte)0x01 : (byte)0x00;
            return SendLedReport(report);
        }

        private bool SendButtonIntensityReport(byte[] intensities, bool commit)
        {
            var report = new byte[LED_REPORT_LENGTH];
            report[0] = 0xFF;
            report[1] = 0x01;
            report[2] = SUBCMD_BUTTON_INTENSITIES;

            Array.Copy(intensities, 0, report, REPORT_HEADER_SIZE, intensities.Length);
            report[BUTTON_INTENSITY_COMMIT_OFFSET] = commit ? (byte)0x01 : (byte)0x00;
            return SendLedReport(report);
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
            var report = new byte[DISPLAY_REPORT_LENGTH];
            report[0] = 0x01;  // Report ID
            report[1] = 0xF8;
            report[2] = 0x09;
            report[3] = 0x01;
            report[4] = 0x02;
            report[5] = seg1;
            report[6] = seg2;
            report[7] = seg3;

            return SendDisplayReport(report);
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
        /// </summary>
        public bool DisplayText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return ClearDisplay();

            var segs = new List<byte>();
            foreach (char ch in text)
            {
                if ((ch == '.' || ch == ',') && segs.Count > 0)
                {
                    segs[segs.Count - 1] |= SevenSegment.Dot;
                }
                else
                {
                    segs.Add(SevenSegment.CharToSegment(ch));
                }

                if (segs.Count >= 3) break;
            }

            while (segs.Count < 3)
                segs.Add(SevenSegment.Blank);

            return SetDisplay(segs[0], segs[1], segs[2]);
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
    }
}
