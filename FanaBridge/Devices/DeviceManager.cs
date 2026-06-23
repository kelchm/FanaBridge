using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Transport;
using HidSharp;

namespace FanaBridge.Devices
{
    /// <summary>
    /// Owns the connected device(s) and exposes the merged peripheral view that SimHub
    /// adapters bind to. A <b>primary</b> carrier (the user's main base) keeps the proven
    /// single-device connect path — auto-detect / PID-override / reconnect — verbatim, and
    /// additional distinct-PID Fanatec devices are adopted as <b>secondary</b> slots, each
    /// its own carrier + <see cref="ConnectionMonitor"/> + <see cref="DeviceHandle"/>. This
    /// is what replaces the old single-wheelbase singleton.
    ///
    /// Grouping is by VID:PID for now, so two same-PID bases collapse to one (the accepted
    /// "rim on any base" limitation); per-port grouping waits on the device-key hardware
    /// validation. The primary/secondary split is a deliberate, marked simplification — the
    /// collection machinery (per-slot carrier/monitor/handle, merged peripherals, aggregate
    /// events) is uniform and is what per-device routing (P6) binds to; unifying discovery
    /// so the primary stops being special is a later, validation-gated step.
    /// </summary>
    internal sealed class DeviceManager : IDisposable
    {
        private readonly IReadOnlyList<IDeviceProbe> _probes;
        private readonly FanatecBaseDevice _device;        // primary carrier (slot 0)
        private readonly ConnectionMonitor _monitor;       // primary heartbeat
        private readonly List<DeviceSlot> _secondary = new List<DeviceSlot>();
        private readonly Func<int> _productIdOverride;
        private readonly Action<string> _logWarn;
        private readonly Action<string> _logInfo;

        private IReadOnlyList<Peripheral> _peripherals = Array.Empty<Peripheral>();

        // Set from HidSharp's background Changed thread; consumed on the frame thread in
        // Update(). A flag (not a queue) so a burst of interface arrivals coalesces into
        // one expedite — and so NO device I/O happens off the frame thread.
        private volatile bool _busChanged;

        // An additional adopted device: its own carrier, heartbeat, and (via the carrier)
        // its own transport + encoder handle.
        private sealed class DeviceSlot
        {
            public int Pid;
            public FanatecBaseDevice Device;
            public ConnectionMonitor Monitor;
        }

        /// <summary>Fired when the FIRST device connects (aggregate 0 → 1 transition).</summary>
        public event Action Connected;

        /// <summary>Fired when the LAST device disconnects (aggregate 1 → 0 transition).</summary>
        public event Action Disconnected;

        /// <summary>Fired when a settled identity change or a non-primary join/leave updates the peripheral set.</summary>
        public event Action PeripheralsChanged;

        /// <param name="productIdOverride">
        /// Returns a user-overridden product id to connect to, or 0 for auto-detect. When
        /// set, only that single device is adopted (no secondary discovery).
        /// </param>
        public DeviceManager(
            Func<int> productIdOverride = null,
            Action<string> logWarn = null,
            Action<string> logInfo = null)
        {
            _productIdOverride = productIdOverride;
            _logWarn = logWarn ?? (_ => { });
            _logInfo = logInfo ?? (_ => { });

            // The production probe registry. SrmProbe is deliberately NOT here yet: its
            // skeleton TryBind binds unconditionally, and its VID:PID set overlaps a
            // genuine CSL Elite (0EB7:0E03 / 0EB7:0005 — single-TLC, no col03, so declined
            // by FanatecBaseProbe), so wiring it in now would mis-bind a real base to a
            // no-op SRM driver. It joins this list once it has a real confirm/decline path
            // (SrmAdditivityTests already exercise the binder with both probes present).
            _probes = new IDeviceProbe[] { new FanatecBaseProbe() };
            _device = new FanatecBaseDevice(_probes);

            _device.SnapshotChanged += OnDeviceSnapshotChanged;

            _monitor = new ConnectionMonitor(_device, TryConnect, logWarn, logInfo);
            _monitor.Connected += OnAnyConnected;
            _monitor.Disconnected += OnAnyDisconnected;

            // Event-driven arrival/removal: react to HID bus changes at once instead of
            // waiting for the poll interval. The handler only sets a flag; the actual
            // (re)connect/teardown happens on the frame thread in Update().
            DeviceList.Local.Changed += OnHidBusChanged;
        }

        private void OnHidBusChanged(object sender, DeviceListChangedEventArgs e) => _busChanged = true;

        // ── Connection lifecycle ──────────────────────────────────────────

        public bool TryInitialConnect()
        {
            bool ok = _monitor.TryInitialConnect();
            if (ok) Reconcile();   // primary up → discover any additional devices
            return ok;
        }

        public void ForceReconnect()
        {
            _monitor.ForceReconnect();
            foreach (var s in _secondary)
                s.Monitor.ForceReconnect();
            Reconcile();
        }

        public bool Update()
        {
            // Drain a pending bus-change notification on the frame thread, so an arrival
            // reconnects (or a removal tears down) this frame rather than at the next
            // poll interval. The poll heartbeat remains as a backstop for missed events.
            bool rescan = _busChanged;
            _busChanged = false;
            if (rescan)
            {
                _monitor.NotifyBusChanged();
                foreach (var s in _secondary)
                    s.Monitor.NotifyBusChanged();
            }

            bool primaryWasConnected = _monitor.IsConnected;
            _monitor.Update();
            // The primary just came up — rescan to spawn secondaries now that it owns its PID.
            if (!primaryWasConnected && _monitor.IsConnected)
                rescan = true;

            if (rescan)
                Reconcile();

            // Tick every secondary heartbeat (index loop: Reconcile may have grown the list).
            for (int i = 0; i < _secondary.Count; i++)
                _secondary[i].Monitor.Update();

            return IsConnected;
        }

        // The primary connect path — auto-detect or PID override — lifted verbatim. The
        // primary carrier is never recreated, so encoder handles cached by adapters stay valid.
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

        // ── Secondary discovery / reconcile ───────────────────────────────

        // Adopt additional distinct-PID base-like devices, and prune ones that have left.
        // No-op for the single-device case (no extra base-like PIDs beyond the primary's).
        private void Reconcile()
        {
            var desired = DiscoverSecondaryPids();

            foreach (var pid in desired)
                if (_secondary.All(s => s.Pid != pid))
                    AddSecondarySlot(pid);

            for (int i = _secondary.Count - 1; i >= 0; i--)
            {
                var s = _secondary[i];
                if (!desired.Contains(s.Pid) && !s.Monitor.IsConnected)
                    RemoveSecondarySlot(i);
            }
        }

        private List<int> DiscoverSecondaryPids()
        {
            // Override forces a single device; secondaries only make sense once the primary
            // is connected and owns its PID (so we never race it for the same device).
            bool overrideActive = (_productIdOverride?.Invoke() ?? 0) != 0;
            if (overrideActive || !_monitor.IsConnected)
                return new List<int>();

            List<HidDevice> fanatec;
            try
            {
                fanatec = DeviceList.Local.GetHidDevices()
                    .Where(d => d.VendorID == FanatecIds.VendorId)
                    .ToList();
            }
            catch
            {
                return new List<int>();
            }

            var baseLike = fanatec
                .Select(d => d.ProductID)
                .Distinct()
                .Where(pid => fanatec.Any(d => d.ProductID == pid && IsBaseLikeInterface(d)));

            return SecondaryPidsFrom(baseLike, _device.ConnectedProductId);
        }

        /// <summary>
        /// Pure selection of which base-like PIDs become secondary slots: every distinct
        /// base-like PID except the one the primary already owns. Exposed for unit testing.
        /// </summary>
        internal static List<int> SecondaryPidsFrom(IEnumerable<int> baseLikePids, int primaryPid)
            => baseLikePids.Where(p => p != primaryPid).Distinct().ToList();

        private void AddSecondarySlot(int pid)
        {
            var device = new FanatecBaseDevice(_probes);
            device.SnapshotChanged += OnDeviceSnapshotChanged;

            var slot = new DeviceSlot { Pid = pid, Device = device };
            slot.Monitor = new ConnectionMonitor(device, () => TryConnectSecondary(device, pid), _logWarn, _logInfo);
            slot.Monitor.Connected += OnAnyConnected;
            slot.Monitor.Disconnected += OnAnyDisconnected;
            _secondary.Add(slot);

            _logInfo($"FanaBridge: adopting additional device PID 0x{pid:X4}");
            slot.Monitor.TryInitialConnect();
            if (slot.Monitor.IsConnected)
                OnAnyConnected();   // surface its peripherals (TryInitialConnect fires no event)
        }

        private bool TryConnectSecondary(FanatecBaseDevice device, int pid)
        {
            try
            {
                if (!device.Connect(pid))
                    return false;
                _logInfo($"FanaBridge: Connected to {device.ProductName} (PID 0x{pid:X4})");
                return true;
            }
            catch (Exception ex)
            {
                _logWarn($"FanaBridge: Secondary connection failed: {ex.Message}");
                return false;
            }
        }

        private void RemoveSecondarySlot(int index)
        {
            var s = _secondary[index];
            s.Device.SnapshotChanged -= OnDeviceSnapshotChanged;
            s.Device.Dispose();
            _secondary.RemoveAt(index);
            _logInfo($"FanaBridge: released device PID 0x{s.Pid:X4}");
        }

        // Mirrors FanatecBaseDevice.PickBasePid's pre-filter: a base exposes a 64-byte
        // (col03) report — output preferred, input accepted so an input-only base still groups.
        private static bool IsBaseLikeInterface(HidDevice d)
        {
            try { return d.GetMaxOutputReportLength() >= 64 || d.GetMaxInputReportLength() >= 64; }
            catch { return false; }
        }

        // ── Aggregate transitions ─────────────────────────────────────────

        private int ConnectedCount() =>
            (_monitor.IsConnected ? 1 : 0) + _secondary.Count(s => s.Monitor.IsConnected);

        private void OnAnyConnected()
        {
            RebuildPeripherals();
            if (ConnectedCount() == 1)
                Connected?.Invoke();          // 0 → 1: the device came online
            else
                PeripheralsChanged?.Invoke();  // an additional device joined
        }

        private void OnAnyDisconnected()
        {
            RebuildPeripherals();
            if (ConnectedCount() == 0)
                Disconnected?.Invoke();        // last device left
            else
                PeripheralsChanged?.Invoke();  // one of several left
        }

        // ── Aggregate state (primary-scoped; "Primary*" == slot 0) ────────

        public bool IsConnected => _monitor.IsConnected || _secondary.Any(s => s.Monitor.IsConnected);
        public string DeviceName => _device.ProductName ?? "Not connected";
        public string LastConnectError => _device.LastConnectError;
        public string LastDisconnectReason => _monitor.LastDisconnectReason;
        public int PrimaryBaseProductId => _device.ConnectedProductId;
        public IDeviceTransport PrimaryTransport => _device.Transport;

        /// <summary>The primary device's output surface (transport + encoder set).</summary>
        public DeviceHandle PrimaryHandle => _device.Handle;

        /// <summary>Stable physical-device key of the primary device, or null when disconnected.</summary>
        public string PrimaryDeviceKey => _device.DeviceKey;

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

        // Primary first (so the FirstOrDefault-by-kind accessors keep returning the primary
        // device's peripheral), then each connected secondary's peripherals appended.
        private void RebuildPeripherals()
        {
            var list = new List<Peripheral>(BuildPeripherals(_device.Snapshot));
            foreach (var s in _secondary)
                if (s.Monitor.IsConnected)
                    list.AddRange(BuildPeripherals(s.Device.Snapshot));
            _peripherals = list;
        }

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

            foreach (var s in _secondary)
            {
                s.Device.SnapshotChanged -= OnDeviceSnapshotChanged;
                s.Device.Dispose();
            }
            _secondary.Clear();
        }
    }
}
