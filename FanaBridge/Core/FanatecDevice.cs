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
        public const int LED_COUNT = 12;
        public const int INTENSITY_COUNT = 16;

        private const int LED_REPORT_LENGTH = 64;
        private const int DISPLAY_REPORT_LENGTH = 8;

        // Dirty tracking — skip redundant HID writes
        private readonly ushort[] _lastColors = new ushort[LED_COUNT];
        private readonly byte[] _lastIntensities = new byte[INTENSITY_COUNT];
        private bool _colorsDirty = true;
        private bool _intensitiesDirty = true;

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

            SimHub.Logging.Current.Info(
                "FanatecDevice: SendDisplayReport => " +
                BitConverter.ToString(data));

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
        /// Atomically updates LED colors and intensities in a single
        /// commit cycle.  Skips HID writes entirely when neither array
        /// has changed since the last successful send.
        ///
        /// This is the primary API that callers (DeviceInstance) should use
        /// every frame.  It solves two problems with the old separate
        /// SetColors/SetIntensities approach:
        ///   1. Dirty tracking was broken for the commit=true call
        ///      (it always sent even when nothing changed).
        ///   2. The staging + commit protocol requires coordination
        ///      across both reports which the caller shouldn't manage.
        /// </summary>
        /// <param name="colors">LED_COUNT-element array of RGB565 values</param>
        /// <param name="ledIntensities">LED_COUNT-element array of per-LED intensity values (0-7).
        /// Global intensity channels are always set to max internally.</param>
        /// <returns>True if hardware was updated (or already up-to-date).</returns>
        public bool SetLedState(ushort[] colors, byte[] ledIntensities)
        {
            if (colors == null || colors.Length != LED_COUNT) return false;
            if (ledIntensities == null || ledIntensities.Length != LED_COUNT) return false;

            // Build full intensity array: per-LED values + global channels at max
            var intensities = new byte[INTENSITY_COUNT];
            Array.Copy(ledIntensities, intensities, LED_COUNT);
            for (int i = LED_COUNT; i < INTENSITY_COUNT; i++)
                intensities[i] = 7;

            bool colorsChanged = _colorsDirty;
            bool intensitiesChanged = _intensitiesDirty;

            if (!colorsChanged)
            {
                for (int i = 0; i < LED_COUNT; i++)
                {
                    if (colors[i] != _lastColors[i])
                    {
                        colorsChanged = true;
                        break;
                    }
                }
            }

            if (!intensitiesChanged)
            {
                for (int i = 0; i < INTENSITY_COUNT; i++)
                {
                    if (intensities[i] != _lastIntensities[i])
                    {
                        intensitiesChanged = true;
                        break;
                    }
                }
            }

            // Nothing to do — hardware is already showing this state
            if (!colorsChanged && !intensitiesChanged)
                return true;

            // Stage whichever reports changed, then commit with the last one.
            // If only one changed we can send it directly with commit=true.
            bool ok = true;

            if (colorsChanged && intensitiesChanged)
            {
                ok = SendColorReport(colors, commit: false);
                ok = SendIntensityReport(intensities, commit: true) && ok;
            }
            else if (colorsChanged)
            {
                ok = SendColorReport(colors, commit: true);
            }
            else // intensitiesChanged
            {
                ok = SendIntensityReport(intensities, commit: true);
            }

            if (ok)
            {
                Array.Copy(colors, _lastColors, LED_COUNT);
                Array.Copy(intensities, _lastIntensities, INTENSITY_COUNT);
                _colorsDirty = false;
                _intensitiesDirty = false;
            }

            return ok;
        }

        // ── Low-level report senders (private) ───────────────────────────

        private bool SendColorReport(ushort[] colors, bool commit)
        {
            var report = new byte[LED_REPORT_LENGTH];
            report[0] = 0xFF;
            report[1] = 0x01;
            report[2] = 0x02;

            for (int i = 0; i < LED_COUNT; i++)
            {
                int offset = 3 + (i * 2);
                report[offset] = (byte)((colors[i] >> 8) & 0xFF);
                report[offset + 1] = (byte)(colors[i] & 0xFF);
            }

            report[27] = commit ? (byte)0x01 : (byte)0x00;
            return SendLedReport(report);
        }

        private bool SendIntensityReport(byte[] intensities, bool commit)
        {
            var report = new byte[LED_REPORT_LENGTH];
            report[0] = 0xFF;
            report[1] = 0x01;
            report[2] = 0x03;

            Array.Copy(intensities, 0, report, 3, INTENSITY_COUNT);
            report[18] = commit ? (byte)0x01 : (byte)0x00;
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
        // REV LED CONTROL (col03 LED interface)
        // =====================================================================
        //
        // Rev LEDs are controlled via the col03 (64-byte) LED interface using
        // per-LED RGB565 color, the same HID collection used by button LEDs.
        //
        // Report format:
        //   byte[0]     = 0xFF  (report ID)
        //   byte[1]     = 0x01  (command type)
        //   byte[2]     = 0x00  (subcmd: Rev LED colors — vs 0x02 for button LEDs)
        //   byte[3..20] = 9 × big-endian RGB565 (LED 0..8, 2 bytes each)
        //   byte[21..63]= 0x00  (padding)
        //
        // An LED with color 0x0000 is off; any non-zero RGB565 value turns it
        // on with that color.  No separate bitmask or override mode is needed.
        //
        // Verified via USB packet capture of the official Fanatec application
        // sending per-LED Rev LED colors through the col03 interrupt OUT endpoint.

        public const int REV_LED_COUNT = 9;

        private readonly ushort[] _lastRevColors = new ushort[REV_LED_COUNT];
        private bool _revColorsDirty = true;

        /// <summary>
        /// Sets all 9 Rev LEDs to the given RGB565 colors via col03.
        /// Color 0x0000 = off; non-zero = on with that color.
        /// Skips HID write if the colors haven't changed since last send.
        /// </summary>
        public bool SetRevLedState(ushort[] colors)
        {
            if (colors == null || colors.Length != REV_LED_COUNT) return false;

            bool changed = _revColorsDirty;
            if (!changed)
            {
                for (int i = 0; i < REV_LED_COUNT; i++)
                {
                    if (colors[i] != _lastRevColors[i])
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (!changed) return true;

            var report = new byte[LED_REPORT_LENGTH];
            report[0] = 0xFF;
            report[1] = 0x01;
            report[2] = 0x00;  // subcmd: Rev LED colors

            for (int i = 0; i < REV_LED_COUNT; i++)
            {
                int offset = 3 + (i * 2);
                report[offset]     = (byte)((colors[i] >> 8) & 0xFF);
                report[offset + 1] = (byte)(colors[i] & 0xFF);
            }

            bool ok = SendLedReport(report);
            if (ok)
            {
                Array.Copy(colors, _lastRevColors, REV_LED_COUNT);
                _revColorsDirty = false;
            }
            return ok;
        }

        /// <summary>
        /// Turn off all Rev LEDs.
        /// </summary>
        public bool ClearRevLeds()
        {
            return SetRevLedState(new ushort[REV_LED_COUNT]);
        }

        // =====================================================================
        // FLAG LED CONTROL (col03 LED interface)
        // =====================================================================
        //
        // Flag LEDs are controlled via the col03 (64-byte) LED interface using
        // per-LED RGB565 color, the same HID collection used by button LEDs
        // and Rev LEDs.
        //
        // Report format:
        //   byte[0]     = 0xFF  (report ID)
        //   byte[1]     = 0x01  (command type)
        //   byte[2]     = 0x01  (subcmd: Flag LED colors — vs 0x00 for Rev LEDs)
        //   byte[3..14] = 6 × big-endian RGB565 (LED 0..5, 2 bytes each)
        //   byte[15..63]= 0x00  (padding)
        //
        // An LED with color 0x0000 is off; any non-zero RGB565 value turns it
        // on with that color.
        //
        // Verified via USB packet capture of the official Fanatec application
        // sending per-LED Flag LED colors through the col03 interrupt OUT endpoint.

        public const int FLAG_LED_COUNT = 6;

        private readonly ushort[] _lastFlagColors = new ushort[FLAG_LED_COUNT];
        private bool _flagColorsDirty = true;

        /// <summary>
        /// Sets all 6 Flag LEDs to the given RGB565 colors via col03.
        /// Color 0x0000 = off; non-zero = on with that color.
        /// Skips HID write if the colors haven't changed since last send.
        /// </summary>
        public bool SetFlagLedState(ushort[] colors)
        {
            if (colors == null || colors.Length != FLAG_LED_COUNT) return false;

            bool changed = _flagColorsDirty;
            if (!changed)
            {
                for (int i = 0; i < FLAG_LED_COUNT; i++)
                {
                    if (colors[i] != _lastFlagColors[i])
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (!changed) return true;

            var report = new byte[LED_REPORT_LENGTH];
            report[0] = 0xFF;
            report[1] = 0x01;
            report[2] = 0x01;  // subcmd: Flag LED colors

            for (int i = 0; i < FLAG_LED_COUNT; i++)
            {
                int offset = 3 + (i * 2);
                report[offset]     = (byte)((colors[i] >> 8) & 0xFF);
                report[offset + 1] = (byte)(colors[i] & 0xFF);
            }

            bool ok = SendLedReport(report);
            if (ok)
            {
                Array.Copy(colors, _lastFlagColors, FLAG_LED_COUNT);
                _flagColorsDirty = false;
            }
            return ok;
        }

        /// <summary>
        /// Turn off all Flag LEDs.
        /// </summary>
        public bool ClearFlagLeds()
        {
            return SetFlagLedState(new ushort[FLAG_LED_COUNT]);
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
            _colorsDirty = true;
            _intensitiesDirty = true;
            _revColorsDirty = true;
            _flagColorsDirty = true;
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
            _colorsDirty = true;
            _intensitiesDirty = true;
            _revColorsDirty = true;
            _flagColorsDirty = true;
        }
    }
}
