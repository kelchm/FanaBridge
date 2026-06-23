using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Transport;
using HidSharp;

namespace FanaBridge.Devices
{
    /// <summary>
    /// A generic device carrier: owns one HID <see cref="FanatecTransport"/> and the
    /// connect/disconnect lifecycle, and binds a per-class <see cref="IDeviceDriver"/>
    /// to it via priority-ordered probes. Knows nothing about FF 08 — the wire/decode
    /// logic lives in the bound driver — so adding pedals/shifter/SRM later is a new
    /// probe + driver, not a change here.
    ///
    /// This is the half of the old <c>FanatecWheelbase</c> that is NOT base-specific
    /// (the base-specific half became <see cref="FanatecBaseDriver"/>); it is held by
    /// the <see cref="DeviceManager"/>, which SimHub adapters reach through the
    /// peripheral view.
    /// </summary>
    internal sealed class FanatecBaseDevice : IServiceableDevice, IDisposable
    {
        // The device OWNS its HID transport; the bound driver reads identity through it
        // and encoders reach it via Transport.
        private readonly FanatecTransport _transport = new FanatecTransport();

        // Phase 1 holds a single base probe; the manager owns the probe list later.
        private readonly IDeviceProbe _probe = new FanatecBaseProbe();

        private FanatecBaseDriver _driver;
        private bool _disposed;

        /// <summary>Fired when the bound driver commits a settled identity change.</summary>
        public event Action<FanatecBaseDevice> SnapshotChanged;

        /// <summary>The device's HID transport — used by LED/display/tuning encoders.</summary>
        public IDeviceTransport Transport => _transport;

        public bool IsConnected => _transport.IsConnected;
        public bool IsDevicePresent => _transport.IsDevicePresent;

        /// <summary>USB product id of the connected device, or 0.</summary>
        public int ConnectedProductId { get; private set; }

        /// <summary>Product name from the HID descriptor of the connected device.</summary>
        public string ProductName { get; private set; }

        /// <summary>
        /// Why the most recent connect attempt failed, or null after a successful
        /// connect. Left intact across <see cref="Disconnect"/> (it reflects the last
        /// attempt) and cleared on the next successful connect.
        /// </summary>
        public string LastConnectError { get; private set; }

        // De-dupes connect-failure logging across ConnectionMonitor's retries.
        private string _lastLoggedConnectError;

        /// <summary>
        /// The bound driver's current identity snapshot, or an empty stable snapshot
        /// when nothing is bound.
        /// </summary>
        public DeviceSnapshot Snapshot => _driver?.Snapshot ?? new DeviceSnapshot { Stable = true };

        // ── Discovery ────────────────────────────────────────────────────

        /// <summary>
        /// Scans the HID bus for a Fanatec wheelbase (a device exposing the 64-byte
        /// col03 interface) and binds to it.
        /// </summary>
        public bool AutoConnect()
        {
            if (_disposed) return false;
            Disconnect();

            try
            {
                var fanatecDevices = DeviceList.Local.GetHidDevices()
                    .Where(d => d.VendorID == FanatecIds.VendorId)
                    .ToList();

                if (fanatecDevices.Count == 0)
                    return FailConnect("No Fanatec devices (VID 0x0EB7) found on the HID bus.");

                int basePid = PickBasePid(fanatecDevices);
                if (basePid == 0)
                    return FailConnect("Fanatec device(s) present, but no col03 (64-byte) interface found.");

                return Adopt(basePid, fanatecDevices);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("FanatecBaseDevice: AutoConnect error: " + ex.Message);
                LastConnectError = "AutoConnect error: " + ex.Message;
                return false;
            }
        }

        /// <summary>Connects to a specific product id (user-overridden auto-detection).</summary>
        public bool Connect(int productId)
        {
            if (_disposed) return false;
            Disconnect();

            try
            {
                var fanatecDevices = DeviceList.Local.GetHidDevices()
                    .Where(d => d.VendorID == FanatecIds.VendorId)
                    .ToList();
                return Adopt(productId, fanatecDevices);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("FanatecBaseDevice: Connect error: " + ex.Message);
                LastConnectError = "Connect error: " + ex.Message;
                return false;
            }
        }

        private bool Adopt(int productId, List<HidDevice> fanatecDevices)
        {
            HidDevice device = null;
            try
            {
                device = fanatecDevices.FirstOrDefault(d => d.ProductID == productId);
                ProductName = SafeProductName(device);
            }
            catch
            {
                ProductName = "Fanatec Device";
            }

            // Open the HID transport for this device — identity + all I/O flow through it.
            if (!_transport.Connect(productId))
                return FailConnect(DescribeConnectFailure(_transport.LastConnectStatus, ProductName, productId));

            ConnectedProductId = productId;
            LastConnectError = null;          // connected successfully
            _lastLoggedConnectError = null;   // re-arm failure logging for the next drop

            SimHub.Logging.Current.Info(string.Format(
                "FanatecBaseDevice: {0} (PID 0x{1:X4})", ProductName, productId));

            // Bind a driver via the probe. The probe confirms via read-only identify
            // I/O and seeds the initial identity.
            var pidDevices = fanatecDevices.Where(d => d.ProductID == productId).ToList();
            var group = new HidDeviceGroup(FanatecIds.VendorId, productId, pidDevices, ProductName);

            if (_probe.CouldMatch(group) && _probe.TryBind(group, _transport) is FanatecBaseDriver driver)
            {
                _driver = driver;
                _driver.SnapshotChanged += OnDriverSnapshotChanged;
            }
            else
            {
                // CouldMatch already held (we opened col03), so this is unexpected.
                SimHub.Logging.Current.Warn(
                    "FanatecBaseDevice: no driver bound despite an open col03 interface.");
            }

            return true;
        }

        // Pick the wheelbase PID: prefer a Fanatec device exposing a 64-byte report.
        // Deliberately looser than the transport's col03 check (which requires a 64-byte
        // OUTPUT): matching a 64-byte INPUT too lets an input-only base still be adopted,
        // so the transport can report NoCol03Interface rather than the base vanishing here.
        private static int PickBasePid(List<HidDevice> devices)
        {
            foreach (var d in devices)
            {
                try
                {
                    if (d.GetMaxOutputReportLength() >= 64 || d.GetMaxInputReportLength() >= 64)
                        return d.ProductID;
                }
                catch { /* descriptor query can throw on busy handles */ }
            }
            return devices.Select(d => d.ProductID).FirstOrDefault();
        }

        private void OnDriverSnapshotChanged(IDeviceDriver driver) => SnapshotChanged?.Invoke(this);

        // ── Service ──────────────────────────────────────────────────────

        /// <summary>
        /// Services the bound driver one tick (drain + settle). Returns true when a new
        /// identity was committed this call.
        /// </summary>
        public bool Service()
        {
            if (!IsConnected || _driver == null)
                return false;
            return _driver.Service();
        }

        // ── Failure messaging ────────────────────────────────────────────

        // Records the connect-failure reason for the live UI/status, logging it only
        // when it changes — ConnectionMonitor retries every few seconds.
        private bool FailConnect(string message)
        {
            LastConnectError = message;
            if (!string.Equals(message, _lastLoggedConnectError, StringComparison.Ordinal))
            {
                SimHub.Logging.Current.Warn("FanatecBaseDevice: " + message);
                _lastLoggedConnectError = message;
            }
            return false;
        }

        // Maps the transport's categorised connect outcome to a concise reason (used for
        // both the UI status line and the de-duped log). Only Col03OpenFailed is genuine
        // exclusive-access contention.
        internal static string DescribeConnectFailure(
            FanatecTransport.TransportConnectStatus status, string productName, int productId)
        {
            switch (status)
            {
                case FanatecTransport.TransportConnectStatus.NoDeviceForPid:
                    return string.Format("No HID device for {0} (PID 0x{1:X4}) — powered off, unplugged, or mode change.",
                        productName, productId);
                case FanatecTransport.TransportConnectStatus.NoCol03Interface:
                    return string.Format("No col03 (64-byte) interface for {0} (PID 0x{1:X4}) — console mode or no col03 wheel; set PC mode (red LED).",
                        productName, productId);
                case FanatecTransport.TransportConnectStatus.Col03OpenFailed:
                    return string.Format("col03 interface for {0} (PID 0x{1:X4}) held by another process (Fanatec app / another sim?).",
                        productName, productId);
                default:
                    return string.Format("HID open failed for {0} (PID 0x{1:X4}).", productName, productId);
            }
        }

        private static string SafeProductName(HidDevice device)
        {
            try { return device?.GetProductName() ?? "Fanatec Device"; }
            catch { return "Fanatec Device"; }
        }

        // ── Lifecycle ────────────────────────────────────────────────────

        /// <summary>Drops the bound driver and closes the owned HID transport.</summary>
        public void Disconnect()
        {
            if (_driver != null)
            {
                _driver.SnapshotChanged -= OnDriverSnapshotChanged;
                _driver.Dispose();
                _driver = null;
            }

            _transport.Disconnect();
            ConnectedProductId = 0;
            ProductName = null;
            // LastConnectError is intentionally left intact — it reflects the most
            // recent connect attempt and is cleared on the next successful connect.
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Disconnect();
                _transport.Dispose();
            }
        }
    }
}
