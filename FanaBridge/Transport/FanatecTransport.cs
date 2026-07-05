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
    public class FanatecTransport : IDisposable, IDeviceTransport
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

        /// <summary>
        /// Categorised reason the most recent <see cref="Connect"/> attempt failed
        /// (or <see cref="TransportConnectStatus.Connected"/> on success). Lets the
        /// caller surface an accurate reason instead of assuming exclusive-access
        /// contention for every failure — notably, a col01-only/legacy base that
        /// exposes no col03 interface is <see cref="TransportConnectStatus.NoCol03Interface"/>,
        /// not a held handle.
        /// </summary>
        public TransportConnectStatus LastConnectStatus { get; private set; }

        /// <summary>Outcome categories for <see cref="Connect"/>.</summary>
        public enum TransportConnectStatus
        {
            /// <summary>Connected successfully.</summary>
            Connected,
            /// <summary>No HID device with the requested VID/PID was present.</summary>
            NoDeviceForPid,
            /// <summary>Device(s) found, but none exposed a col03 (64-byte) control collection.</summary>
            NoCol03Interface,
            /// <summary>A col03 collection was found but could not be opened (held by another process).</summary>
            Col03OpenFailed,
            /// <summary>An unexpected error occurred while connecting.</summary>
            UnexpectedError,
        }

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
                        .Any(d => d.VendorID == FanatecWheelbase.FANATEC_VENDOR_ID
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
        /// The product ID should come from FanatecWheelbase.ConnectedProductId.
        /// </summary>
        public bool Connect(int productId)
        {
            // Release any previous connection to prevent resource leaks
            Disconnect();

            try
            {
                var devices = DeviceList.Local.GetHidDevices()
                    .Where(d => d.VendorID == FanatecWheelbase.FANATEC_VENDOR_ID && d.ProductID == productId)
                    .ToList();

                if (devices.Count == 0)
                {
                    LastConnectStatus = TransportConnectStatus.NoDeviceForPid;
                    SimHub.Logging.Current.Info("FanatecTransport: No devices found for PID 0x" + productId.ToString("X4"));
                    return false;
                }

                // Locate the col03 (LED/identity) control interface — by its &col03
                // path token, else by a 64-byte OUTPUT report. col03 can
                // enumerate a moment AFTER col01, so (mirroring the official SDK, which
                // opens it with a short retry) re-scan a few times before concluding it
                // is absent. Defensive length reads avoid a descriptor-query throw being
                // misclassified as an unexpected error.
                _displayDevice = devices.FirstOrDefault(d => d.DevicePath.Contains("col01"));
                _ledDevice = SelectCol03(devices);
                for (int attempt = 1; _ledDevice == null && attempt < COL03_FIND_ATTEMPTS; attempt++)
                {
                    Thread.Sleep(COL03_FIND_RETRY_MS);
                    devices = DeviceList.Local.GetHidDevices()
                        .Where(d => d.VendorID == FanatecWheelbase.FANATEC_VENDOR_ID && d.ProductID == productId)
                        .ToList();
                    _displayDevice = devices.FirstOrDefault(d => d.DevicePath.Contains("col01"));
                    _ledDevice = SelectCol03(devices);
                }

                if (_ledDevice == null)
                {
                    // Debug, not Warn: the wheelbase logs a single de-duped Warn for this;
                    // logging here every retry/poll would spam the log.
                    LastConnectStatus = TransportConnectStatus.NoCol03Interface;
                    SimHub.Logging.Current.Debug(
                        "FanatecTransport: No col03 (64-byte) control interface found for PID 0x"
                        + productId.ToString("X4") + " after " + COL03_FIND_ATTEMPTS + " attempt(s)");
                    return false;
                }

                try
                {
                    _ledStream = _ledDevice.Open();
                }
                catch (Exception ex)
                {
                    // The col03 collection exists but we could not open it — this is
                    // the only failure that is genuine exclusive-access contention.
                    LastConnectStatus = TransportConnectStatus.Col03OpenFailed;
                    SimHub.Logging.Current.Warn(
                        "FanatecTransport: col03 interface open failed (held by another process?): " + ex.Message);
                    return false;
                }

                SimHub.Logging.Current.Info(string.Format(
                    "FanatecTransport: LED interface opened (MaxOutput={0})", SafeMaxOutput(_ledDevice)));

                // Open col01 via HidStream (interrupt OUT — confirmed working in PoC)
                if (_displayDevice != null)
                {
                    try
                    {
                        _displayStream = _displayDevice.Open();
                        SimHub.Logging.Current.Info("FanatecTransport: Display interface (col01) opened via HidStream");
                    }
                    catch (Exception ex)
                    {
                        SimHub.Logging.Current.Warn("FanatecTransport: col01 HidStream failed: " + ex.Message);
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
                        SimHub.Logging.Current.Warn("FanatecTransport: col01 WriteFile handle failed: " + ex.Message);
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
                LastConnectStatus = TransportConnectStatus.Connected;
                SimHub.Logging.Current.Info("FanatecTransport: Connected to " + ProductName);
                return true;
            }
            catch (Exception ex)
            {
                LastConnectStatus = TransportConnectStatus.UnexpectedError;
                SimHub.Logging.Current.Error("FanatecTransport: Connection error: " + ex.Message);
                return false;
            }
        }

        // col03 can enumerate slightly after col01; the official SDK opens it with a
        // short retry, so we re-scan a few times before declaring it absent.
        private const int COL03_FIND_ATTEMPTS = 3;
        private const int COL03_FIND_RETRY_MS = 10;

        // col03 control interface: the &col03 node, else a node with a 64-byte OUTPUT
        // report (col03 is the LED/FF 08 WRITE channel). A 64-byte INPUT alone is NOT
        // col03 — e.g. a console/PS4-mode gamepad collection (col01) has a 64-byte input
        // report; selecting it would open the wrong endpoint and fail every LED write.
        private static HidDevice SelectCol03(System.Collections.Generic.List<HidDevice> devices)
        {
            return devices.FirstOrDefault(d => d.DevicePath.Contains("col03"))
                ?? devices.FirstOrDefault(d => IsCol03ReportLength(SafeMaxOutput(d)));
        }

        private static bool IsCol03ReportLength(int len) => len == 64 || len == 65;
        private static int SafeMaxOutput(HidDevice d) { try { return d.GetMaxOutputReportLength(); } catch { return 0; } }
        private static int SafeMaxInput(HidDevice d) { try { return d.GetMaxInputReportLength(); } catch { return 0; } }

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
                SimHub.Logging.Current.Warn("FanatecTransport: LED write error: " + ex.Message);
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
                    SimHub.Logging.Current.Warn("FanatecTransport: Display stream write failed: " + ex.Message);
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
            lock (_writeLock)
            {
                // Capture under the lock: Disconnect() nulls/disposes the stream
                // under the same lock, so a non-null local here stays valid for
                // the duration of the read.
                var stream = _ledStream;
                if (stream == null) return -1;

                int saved = stream.ReadTimeout;
                try
                {
                    stream.ReadTimeout = timeoutMs;
                    return stream.Read(buffer, 0, buffer.Length);
                }
                catch
                {
                    return -1;
                }
                finally
                {
                    try { stream.ReadTimeout = saved; } catch { }
                }
            }
        }

        int IDeviceTransport.Col03MaxInputReportLength
        {
            get
            {
                if (_ledDevice == null) return 64;
                int len = SafeMaxInput(_ledDevice);
                return len > 0 ? len : 64;
            }
        }

        bool IDeviceTransport.SendCol01(byte[] data)
        {
            lock (_writeLock)
            {
                return SendDisplayReport(data);
            }
        }

        int IDeviceTransport.ReadCol01(byte[] buffer, int timeoutMs)
        {
            lock (_writeLock)
            {
                // Capture under the lock, mirroring ReadCol03: Disconnect() nulls/disposes
                // the stream under the same lock, so a non-null local stays valid here.
                var stream = _displayStream;
                if (stream == null) return -1;

                int saved = stream.ReadTimeout;
                try
                {
                    stream.ReadTimeout = timeoutMs;
                    return stream.Read(buffer, 0, buffer.Length);
                }
                catch
                {
                    return -1;
                }
                finally
                {
                    try { stream.ReadTimeout = saved; } catch { }
                }
            }
        }

        int IDeviceTransport.Col01MaxInputReportLength
        {
            get
            {
                if (_displayDevice == null) return 34;
                int len = SafeMaxInput(_displayDevice);
                return len > 0 ? len : 34;
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
        /// Closes all HID handles. Holds the write lock so teardown can't race
        /// an in-flight send/read (which capture their stream under the same
        /// lock), avoiding use-after-dispose on the HID streams.
        /// </summary>
        public void Disconnect()
        {
            lock (_writeLock)
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
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
