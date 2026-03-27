using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace FanaBridge.Transport
{
    /// <summary>
    /// HID transport layer for Fanatec wheel hardware.
    /// Manages device lifecycle (connect/disconnect) and provides the
    /// <see cref="IDeviceTransport"/> interface used by protocol encoders
    /// (LEDs, display, tuning).
    /// </summary>
    public class FanatecDevice : IDisposable, IDeviceConnection, IDeviceTransport
    {
        private const int DISPLAY_REPORT_LENGTH = 8;

        // ── Write serialization ────────────────────────────────────────────
        // IDeviceTransport.SendCol03 / SendCol01 acquire this lock per-call.
        // IDeviceTransport.BeginBatch acquires it for multi-report sequences.
        private readonly object _writeLock = new object();

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
                        d.GetMaxOutputReportLength() == 64 ||
                        d.GetMaxOutputReportLength() == 65);
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

        // ── IDeviceTransport implementation ──────────────────────────────

        bool IDeviceTransport.SendCol03(byte[] data)
        {
            lock (_writeLock)
            {
                return SendLedReport(data);
            }
        }

        int IDeviceTransport.ReadCol03(byte[] buffer, int timeoutMs)
        {
            if (_ledStream == null) return -1;
            lock (_writeLock)
            {
                int saved = _ledStream.ReadTimeout;
                try
                {
                    _ledStream.ReadTimeout = timeoutMs;
                    return _ledStream.Read(buffer, 0, buffer.Length);
                }
                catch
                {
                    return -1;
                }
                finally
                {
                    _ledStream.ReadTimeout = saved;
                }
            }
        }

        int IDeviceTransport.Col03MaxInputReportLength
        {
            get
            {
                if (_ledDevice == null) return 64;
                int len = _ledDevice.GetMaxInputReportLength();
                return len > 0 ? len : 64;
            }
        }

        private int _col01LogCounter;

        bool IDeviceTransport.SendCol01(byte[] data)
        {
            // Sample every 100th report to avoid log spam
            if (Interlocked.Increment(ref _col01LogCounter) % 100 == 1)
            {
                SimHub.Logging.Current.Info(
                    "FanatecDevice: SendCol01 [" +
                    string.Join(" ", data.Select(b => b.ToString("X2"))) + "]");
            }
            lock (_writeLock)
            {
                return SendDisplayReport(data);
            }
        }

        IDisposable IDeviceTransport.BeginBatch()
        {
            Monitor.Enter(_writeLock);
            return new BatchToken(_writeLock);
        }

        private sealed class BatchToken : IDisposable
        {
            private readonly object _lock;
            private bool _released;

            public BatchToken(object @lock) { _lock = @lock; }

            public void Dispose()
            {
                if (!_released)
                {
                    _released = true;
                    Monitor.Exit(_lock);
                }
            }
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
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
