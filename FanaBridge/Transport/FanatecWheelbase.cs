using System;
using System.Linq;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using HidSharp;

namespace FanaBridge.Transport
{
    /// <summary>
    /// Represents a connected Fanatec wheelbase — the root of all communication.
    /// The PC only ever talks to the wheelbase; the attached wheel/hub and any
    /// button module are state the base reports, not independent connections.
    ///
    /// Owns the wheelbase's identity (base model) and its current attachment
    /// (wheel or hub, + optional module), read entirely over HID via the col03
    /// <c>FF 08</c> system report — no Fanatec driver/service and no
    /// SimHub.FanatecManaged.dll. Because it talks pure HID, identity also works
    /// for Fanatec wheels on non-Fanatec / SRM bases that emit the FF 08 report.
    ///
    /// This is the natural home for wheelbase-native capabilities (e.g. a CSL
    /// Elite base's own rev LEDs) and the seam for compositional capability
    /// resolution (base ⊕ wheel/hub ⊕ module) — see <see cref="ResolveCapabilities"/>.
    /// It is also instantiable per base, so supporting multiple wheelbases later
    /// is a matter of holding a collection rather than unwinding singletons.
    /// </summary>
    // ARCHITECTURE: this class is base-specific — it directly owns the FF 08
    // exchange + the base/wheel/module model. The future device model SPLITS it
    // (a generic Device/manager holding a transport + a bound per-class driver
    // carrying the FF 08 logic), it does not simply rename. See the device-
    // architecture direction note (Connection / IIdentitySource / Device).
    public class FanatecWheelbase : IDisposable, IWheelbaseConnection
    {
        public const ushort FANATEC_VENDOR_ID = 0x0EB7;

        // The wheelbase OWNS its HID transport (col01 + col03 I/O). Identity is
        // read through it, and encoders reach it via Transport.
        private readonly FanatecTransport _transport = new FanatecTransport();
        private bool _disposed;

        public FanatecWheelbase()
        {
            _ingest = OnDrainReading;
        }

        /// <summary>The wheelbase's HID transport — used by LED/display/tuning encoders.</summary>
        public IDeviceTransport Transport => _transport;

        // ── Identity acquisition (enable-once, then listen) ────────────────
        // The base PUSHES the FF 08 system report on every attachment change after
        // a single enable, and is silent otherwise. We enable + read once on
        // connect, then UpdateIdentity drains pushed reports via the reader — no
        // polling. A changed reading is held by the settler until it goes quiet
        // (riding out the firmware's transient reconnect flap), then committed.
        private const int IdentitySettleMs = 200;
        private const int IdentityReEnableMs = 10000;   // keep the push subscription alive

        private readonly SystemReportReader _reportReader = new SystemReportReader();
        private readonly IdentitySettler _settler = new IdentitySettler(IdentitySettleMs);
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly Action<SystemReportReader.Reading> _ingest;
        private long _lastEnableMs;
        private long _drainNow;

        // Most recent drained reading; decoded into the public properties on commit.
        private SystemReportReader.Reading _lastReading;

        private volatile bool _identityStable = true;

        // ── Connection state ─────────────────────────────────────────────

        /// <summary>
        /// Whether the wheelbase is connected. Tracks the live transport rather
        /// than a latched flag, so a dropped HID stream is reflected immediately
        /// (and trips <see cref="ConnectionMonitor"/>'s stream check) instead of
        /// reporting connected until the next bus scan.
        /// </summary>
        public bool IsConnected => _transport.IsConnected;

        /// <summary>Whether the wheelbase's HID device is still present on the bus.</summary>
        public bool IsDevicePresent => _transport.IsDevicePresent;

        /// <summary>The USB product ID of the connected wheelbase, or 0 if not connected.</summary>
        public int ConnectedProductId { get; private set; }

        /// <summary>Product name from the HID descriptor of the connected device.</summary>
        public string ProductName { get; private set; }

        // ── Wheelbase identity ───────────────────────────────────────────

        /// <summary>Raw BaseType byte from the FF 08 report (byte 0x02).</summary>
        public byte BaseType { get; private set; }

        /// <summary>FanaBridge wheelbase code (e.g. "CSDDPlus"), or null if unrecognized.</summary>
        public string BaseCode { get; private set; }

        // ── Attachment (wheel or hub) identity ───────────────────────────

        /// <summary>Whether a wheel or hub is currently attached.</summary>
        public bool WheelDetected { get; private set; }

        /// <summary>
        /// Stable profile-match code for the attached wheel/hub (e.g. "PSWBMW",
        /// "PHUB"), or null when nothing is attached / the wire code is unrecognized.
        /// </summary>
        public string WheelCode { get; private set; }

        /// <summary>
        /// Raw attachment wire code (FF 08 byte 0x18) — the deepest, firmware-defined
        /// identifier. 0 when nothing is attached.
        /// </summary>
        public byte WheelWireCode { get; private set; }

        /// <summary>Whether the attachment is a hub (accepts a button module).</summary>
        public bool IsHub { get; private set; }

        /// <summary>FanaBridge button-module code ("PBME"/"PBMR"), or null when none.</summary>
        public string ModuleCode { get; private set; }

        /// <summary>Resolved capability profile for the current wheel + module combination.</summary>
        public WheelCapabilities CurrentCapabilities { get; private set; }
            = WheelCapabilities.None;

        /// <summary>
        /// Whether a wheel/hub is identified (attached AND recognized, not just transitional).
        /// </summary>
        public bool WheelIdentified =>
            WheelDetected && !string.IsNullOrEmpty(WheelCode);

        /// <summary>
        /// Whether the attachment identity is settled (not mid-transition). False
        /// while a changed reading is still settling — consumers should treat the
        /// device as not-yet-connected and suppress output during that window.
        /// </summary>
        public bool IdentityStable => _identityStable;

        /// <summary>
        /// Display name for the current wheel/hub: the matched profile's name, else
        /// the FanaBridge code (or an EXT_INFO / unknown marker for unmapped bytes).
        /// </summary>
        public string DisplayName
        {
            get
            {
                if (!WheelDetected)
                    return "No wheel attached";
                if (CurrentCapabilities != null && !string.IsNullOrEmpty(CurrentCapabilities.Name))
                    return CurrentCapabilities.Name;
                string label =
                    WheelCode != null     ? WheelCode :
                    WheelWireCode == 0xFF ? "EXT_INFO (extended-identity wheel — please report)" :
                    "Unknown (0x" + WheelWireCode.ToString("X2") + ")";
                return ModuleCode == null ? label : label + " + " + ModuleCode;
            }
        }

        // ── Events ───────────────────────────────────────────────────────

        /// <summary>
        /// Fired when the attached wheel/hub or module changes, including transitions
        /// to/from undetected. The capabilities are already updated when this fires.
        /// </summary>
        public event Action<FanatecWheelbase> WheelChanged;

        // ── Configuration ────────────────────────────────────────────────

        /// <summary>
        /// Optional callback that returns a profile override ID for a given
        /// wheel match key (e.g. "PHUB_PBMR").  Set by the plugin to integrate
        /// with <see cref="FanatecPluginSettings.ProfileOverrides"/>.
        /// Return null or empty to use default auto-resolution.
        /// </summary>
        public Func<string, string> ProfileOverrideResolver { get; set; }

        // ── Discovery ────────────────────────────────────────────────────

        /// <summary>
        /// Scans the HID bus for a Fanatec wheelbase (a device exposing the
        /// 64-byte col03 interface) and records its PID + name.
        /// </summary>
        public bool AutoConnect()
        {
            if (_disposed) return false;
            Disconnect();

            try
            {
                var fanatecDevices = DeviceList.Local.GetHidDevices()
                    .Where(d => d.VendorID == FANATEC_VENDOR_ID)
                    .ToList();

                if (fanatecDevices.Count == 0)
                {
                    SimHub.Logging.Current.Debug("FanatecWheelbase: No Fanatec devices found on HID bus");
                    return false;
                }

                // The wheelbase is the device that exposes the col03 (64-byte)
                // control interface; accessories (pedals, etc.) do not.
                int basePid = PickBasePid(fanatecDevices);
                if (basePid == 0)
                {
                    SimHub.Logging.Current.Warn("FanatecWheelbase: No Fanatec wheelbase (col03) interface found");
                    return false;
                }

                return Adopt(basePid, fanatecDevices);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("FanatecWheelbase: AutoConnect error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Connects to a specific product ID. Use this when the user has
        /// overridden auto-detection in settings.
        /// </summary>
        public bool Connect(int productId)
        {
            if (_disposed) return false;
            Disconnect();

            try
            {
                var fanatecDevices = DeviceList.Local.GetHidDevices()
                    .Where(d => d.VendorID == FANATEC_VENDOR_ID)
                    .ToList();
                return Adopt(productId, fanatecDevices);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("FanatecWheelbase: Connect error: " + ex.Message);
                return false;
            }
        }

        private bool Adopt(int productId, System.Collections.Generic.List<HidDevice> fanatecDevices)
        {
            try
            {
                var device = fanatecDevices.FirstOrDefault(d => d.ProductID == productId);
                ProductName = SafeProductName(device);
            }
            catch
            {
                ProductName = "Fanatec Device";
            }

            // Open the HID transport for this base — identity + all I/O flow through it.
            if (!_transport.Connect(productId))
            {
                SimHub.Logging.Current.Warn(string.Format(
                    "FanatecWheelbase: HID open failed for {0} (PID 0x{1:X4})", ProductName, productId));
                return false;
            }

            ConnectedProductId = productId;

            SimHub.Logging.Current.Info(string.Format(
                "FanatecWheelbase: {0} (PID 0x{1:X4})", ProductName, productId));

            // Enable the system report (turns on the firmware's push-on-change) and
            // read the initial identity once. From here on UpdateIdentity only
            // listens for pushes — no triggering.
            try
            {
                _lastEnableMs = _clock.ElapsedMilliseconds;
                if (_reportReader.ReadInitial(_transport, out var reading))
                    IngestReading(reading, _clock.ElapsedMilliseconds);

                // Confirm the per-frame drain is non-blocking on this hardware — a
                // blocking ReadCol03(buf, 0) would stall the frame thread. Done once
                // now that the input is quiet; leaves a permanent regression guard.
                double drainMs = _reportReader.ProbeDrainLatencyMs(_transport, 5);
                if (drainMs > 1.0)
                    SimHub.Logging.Current.Warn(string.Format(
                        "FanatecWheelbase: idle identity drain took {0:F2} ms — expected non-blocking (<1 ms); per-frame identity polling may stutter.", drainMs));
                else
                    SimHub.Logging.Current.Info(string.Format(
                        "FanatecWheelbase: identity drain is non-blocking ({0:F2} ms idle)", drainMs));
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("FanatecWheelbase: Initial identity read failed: " + ex.Message);
            }
            return true;
        }

        // Pick the wheelbase PID: prefer a Fanatec device exposing a 64-byte
        // col03 report (input or output); fall back to the first Fanatec PID.
        private static int PickBasePid(System.Collections.Generic.List<HidDevice> devices)
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

        private static string SafeProductName(HidDevice device)
        {
            try { return device?.GetProductName() ?? "Fanatec Device"; }
            catch { return "Fanatec Device"; }
        }

        // ── Identity ──────────────────────────────────────────────────────

        /// <summary>
        /// Drains pushed FF 08 system reports and advances the settle timer. Called
        /// each frame by <see cref="ConnectionMonitor"/>. The base pushes on every
        /// attachment change (no polling); a changed reading is committed only once
        /// it has settled. Returns true when a new identity was committed this call.
        /// </summary>
        public bool UpdateIdentity()
        {
            if (!IsConnected)
                return false;

            long now = _clock.ElapsedMilliseconds;

            // Keep the push-on-change subscription alive (the enable can lapse).
            if (now - _lastEnableMs >= IdentityReEnableMs)
            {
                _lastEnableMs = now;
                try { _reportReader.Enable(_transport); }
                catch { /* transient; the next tick retries */ }
            }

            _drainNow = now;
            _reportReader.DrainPushes(_transport, _ingest);

            bool changed = _settler.Tick(now, out _, out _);
            _identityStable = _settler.IsStable;
            if (changed)
                CommitIdentity();
            return changed;
        }

        // Drain callback: record the reading and offer it to the settler. _drainNow
        // carries the current clock so the cached delegate needs no per-frame closure.
        private void OnDrainReading(SystemReportReader.Reading r) => IngestReading(r, _drainNow);

        private void IngestReading(SystemReportReader.Reading r, long now)
        {
            _lastReading = r;
            byte effModule = FanatecIdentity.IsHub(r.Wire) ? r.ModRaw : (byte)0;
            _settler.Offer(r.Wire, effModule, now);
        }

        // Commit the latest (settled) reading into the public identity properties.
        private void CommitIdentity()
        {
            byte wire = _lastReading.Wire;
            bool isHub = FanatecIdentity.IsHub(wire);

            WheelDetected = wire != 0;
            WheelCode = FanatecIdentity.DecodeCode(wire);
            WheelWireCode = wire;
            IsHub = isHub;
            ModuleCode = isHub ? FanatecIdentity.DecodeModule(_lastReading.ModRaw) : null;
            BaseType = _lastReading.BaseType;
            BaseCode = FanatecIdentity.DecodeBaseCode(_lastReading.BaseType);

            ResolveCapabilities("Wheel changed");
            WheelChanged?.Invoke(this);
        }

        /// <summary>
        /// Forces a re-evaluation of capabilities against the current profile store.
        /// Call after <see cref="WheelProfileStore.Reload"/> to pick up newly-saved
        /// profiles without requiring a SimHub restart or a physical wheel change.
        /// </summary>
        public void RefreshCapabilities()
        {
            if (!WheelDetected)
                return;

            ResolveCapabilities("RefreshCapabilities");
            WheelChanged?.Invoke(this);
        }

        /// <summary>
        /// Resolves the capability profile for the current attachment, respecting
        /// any user override from the plugin settings.
        ///
        /// COMPOSITION SEAM: today this resolves a single profile for the attached
        /// wheel/hub(+module). The full model is compositional —
        ///   EffectiveCapabilities = wheelbase-native ⊕ wheel/hub ⊕ module
        /// (e.g. a CSL Elite base contributes its own rev LEDs; a hub contributes
        /// native features plus the module's). The base currently contributes
        /// nothing, so single-source resolution is equivalent — but new sources
        /// should be merged here rather than bolted on elsewhere. Tracked in #16.
        /// </summary>
        private void ResolveCapabilities(string logContext)
        {
            if (!WheelDetected)
            {
                CurrentCapabilities = WheelCapabilities.None;
                return;
            }

            // WheelCode and ModuleCode are already the profile-match keys.
            string wheelCode = WheelCode;
            string moduleCode = ModuleCode;

            string matchKey = WheelProfileStore.MakeMatchKey(wheelCode, moduleCode);

            // Check for a user override
            string overrideId = ProfileOverrideResolver?.Invoke(matchKey);

            var profile = WheelProfileStore.FindByWheelType(wheelCode, moduleCode, overrideId);
            CurrentCapabilities = profile != null
                ? new WheelCapabilities(profile)
                : WheelCapabilities.None;

            SimHub.Logging.Current.Info(string.Format(
                "FanatecWheelbase: {0} — Base={1}, Detected={2}, Wheel={3} (wire 0x{4:X2}), Module={5}, Override={6}, Caps={7}",
                logContext,
                BaseCode ?? "(unknown)",
                WheelDetected,
                WheelCode ?? "(unrecognized)",
                WheelWireCode,
                ModuleCode ?? "(none)",
                overrideId ?? "(auto)",
                CurrentCapabilities.Name ?? "(none)"));
        }

        // ── Lifecycle ────────────────────────────────────────────────────

        /// <summary>Closes the owned HID transport and resets all identity state.</summary>
        public void Disconnect()
        {
            _transport.Disconnect();
            _settler.Reset();
            _identityStable = true;
            _lastReading = default;

            ConnectedProductId = 0;
            ProductName = null;
            WheelDetected = false;
            WheelCode = null;
            WheelWireCode = 0;
            IsHub = false;
            ModuleCode = null;
            BaseType = 0;
            BaseCode = null;
            CurrentCapabilities = WheelCapabilities.None;
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
