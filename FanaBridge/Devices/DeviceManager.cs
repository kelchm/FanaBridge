using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Transport;
using HidSharp;

namespace FanaBridge.Devices
{
    /// <summary>
    /// Owns the connected device(s) and exposes the merged peripheral view that SimHub
    /// adapters bind to. Today it holds exactly one <see cref="FanatecBaseDevice"/> and
    /// composes the existing <see cref="ConnectionMonitor"/> as that device's per-frame
    /// heartbeat; the collection + hot-plug arrive in the next phase. This is what
    /// replaces the <c>FanatecPlugin.Instance.Wheelbase</c> singleton.
    /// </summary>
    internal sealed class DeviceManager : IDisposable
    {
        private readonly FanatecBaseDevice _device = new FanatecBaseDevice();
        private readonly ConnectionMonitor _monitor;
        private readonly Func<int> _productIdOverride;
        private readonly Action<string> _logWarn;
        private readonly Action<string> _logInfo;

        private IReadOnlyList<Peripheral> _peripherals = Array.Empty<Peripheral>();

        // Set from HidSharp's background Changed thread; consumed on the frame thread in
        // Update(). A flag (not a queue) so a burst of interface arrivals coalesces into
        // one expedite — and so NO device I/O happens off the frame thread.
        private volatile bool _busChanged;

        /// <summary>Fired when a connection is established.</summary>
        public event Action Connected;

        /// <summary>Fired when the connection is lost.</summary>
        public event Action Disconnected;

        /// <summary>Fired when a settled identity change updates the peripheral set.</summary>
        public event Action PeripheralsChanged;

        /// <param name="productIdOverride">
        /// Returns a user-overridden product id to connect to, or 0 for auto-detect.
        /// </param>
        public DeviceManager(
            Func<int> productIdOverride = null,
            Action<string> logWarn = null,
            Action<string> logInfo = null)
        {
            _productIdOverride = productIdOverride;
            _logWarn = logWarn ?? (_ => { });
            _logInfo = logInfo ?? (_ => { });

            _device.SnapshotChanged += OnDeviceSnapshotChanged;

            _monitor = new ConnectionMonitor(_device, TryConnect, logWarn, logInfo);
            _monitor.Connected += () => { RebuildPeripherals(); Connected?.Invoke(); };
            _monitor.Disconnected += () => { RebuildPeripherals(); Disconnected?.Invoke(); };

            // Event-driven arrival/removal: react to HID bus changes at once instead of
            // waiting for the poll interval. The handler only sets a flag; the actual
            // (re)connect/teardown happens on the frame thread in Update().
            DeviceList.Local.Changed += OnHidBusChanged;
        }

        private void OnHidBusChanged(object sender, DeviceListChangedEventArgs e) => _busChanged = true;

        // ── Connection lifecycle (delegated to the per-device heartbeat) ──

        public bool TryInitialConnect() => _monitor.TryInitialConnect();
        public void ForceReconnect() => _monitor.ForceReconnect();

        public bool Update()
        {
            // Drain a pending bus-change notification on the frame thread, so an arrival
            // reconnects (or a removal tears down) this frame rather than at the next
            // poll interval. The poll heartbeat remains as a backstop for missed events.
            if (_busChanged)
            {
                _busChanged = false;
                _monitor.NotifyBusChanged();
            }
            return _monitor.Update();
        }

        private bool TryConnect()
        {
            try
            {
                int pid = _productIdOverride?.Invoke() ?? 0;
                bool connected;
                if (pid != 0)
                {
                    _logInfo($"FanaBridge: Using PID override 0x{pid:X4}");
                    connected = _device.Connect(pid);
                }
                else
                {
                    connected = _device.AutoConnect();
                }

                if (!connected)
                    return false;

                _logInfo($"FanaBridge: Connected to {_device.ProductName} (PID 0x{_device.ConnectedProductId:X4})");
                return true;
            }
            catch (Exception ex)
            {
                _logWarn($"FanaBridge: Connection failed: {ex.Message}");
                return false;
            }
        }

        // ── Aggregate state ───────────────────────────────────────────────

        public bool IsConnected => _monitor.IsConnected;
        public string DeviceName => _device.ProductName ?? "Not connected";
        public string LastConnectError => _device.LastConnectError;
        public string LastDisconnectReason => _monitor.LastDisconnectReason;
        public int PrimaryBaseProductId => _device.ConnectedProductId;
        public IDeviceTransport PrimaryTransport => _device.Transport;

        public DeviceSnapshot PrimarySnapshot => _device.Snapshot;
        public bool HasIdentity => _device.Snapshot.HasIdentity;
        public bool IdentityStable => _device.Snapshot.Stable;

        // ── Peripheral view ───────────────────────────────────────────────

        public IReadOnlyList<Peripheral> Peripherals => _peripherals;
        public Peripheral BasePeripheral => _peripherals.FirstOrDefault(p => p.Kind == PeripheralKind.Base);
        public Peripheral AttachedWheel =>
            _peripherals.FirstOrDefault(p => p.Kind == PeripheralKind.Wheel || p.Kind == PeripheralKind.Hub);
        public Peripheral AttachedModule => _peripherals.FirstOrDefault(p => p.Kind == PeripheralKind.Module);

        public Peripheral FindPeripheral(PeripheralKind kind, string code) =>
            _peripherals.FirstOrDefault(p => p.Kind == kind
                && string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase));

        private void OnDeviceSnapshotChanged(FanatecBaseDevice device)
        {
            RebuildPeripherals();
            PeripheralsChanged?.Invoke();
        }

        private void RebuildPeripherals() => _peripherals = BuildPeripherals(_device.Snapshot);

        /// <summary>
        /// Merge a device snapshot into the flat peripheral set: a Base peripheral
        /// (once identity is committed) plus each hosted attachment. The hub's empty
        /// module slot (no module: wire 0, code null) is dropped so it never surfaces
        /// as a phantom Module peripheral. Pure; exposed for unit testing.
        /// </summary>
        internal static List<Peripheral> BuildPeripherals(DeviceSnapshot snap)
        {
            var list = new List<Peripheral>(3);

            if (snap.HasIdentity)
                list.Add(new Peripheral(PeripheralKind.Base, snap.Code, snap.BaseTypeByte, snap.Stable));

            if (snap.Attachments != null)
            {
                foreach (var a in snap.Attachments)
                {
                    if (a.Kind == PeripheralKind.Module && a.WireCode == 0 && a.Code == null)
                        continue;
                    list.Add(new Peripheral(a.Kind, a.Code, a.WireCode, snap.Stable));
                }
            }

            return list;
        }

        public void Dispose()
        {
            try { DeviceList.Local.Changed -= OnHidBusChanged; } catch { /* never throw from teardown */ }
            _device.SnapshotChanged -= OnDeviceSnapshotChanged;
            _device.Dispose();
        }
    }
}
