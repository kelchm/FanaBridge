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
    /// SimHub.FanatecManaged.dll. The identity DECODE is transport-agnostic (it
    /// parses whatever FF 08 frame it is handed), but discovery and connection are
    /// currently scoped to the Fanatec VID (0x0EB7) in <see cref="FanatecTransport"/>,
    /// so a base enumerating under a different VID (e.g. an SRM kit on its own VID)
    /// is not yet connected here.
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
            _ingestSrm = OnDrainSrm;
            _bufferItm = BufferItmReport;
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
        private readonly SrmConverterIdentity _srmReader = new SrmConverterIdentity();
        // Converter identity: while unidentified we ping the SRM DE FA channel (harmless — a genuine
        // base does not answer). Its 0xDD reply arrives via the shared col03 drain into _ingestSrm.
        private const int SrmQueryMs = 1000;              // re-ping cadence while unidentified
        private long _lastSrmQueryMs;
        private SrmConverterIdentity.Result? _pendingSrm;  // a 0xDD reply seen in the drain, committed after it
        private readonly IdentitySettler _settler = new IdentitySettler(IdentitySettleMs);
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly Action<SystemReportReader.Reading> _ingest;
        private readonly Action<byte[]> _ingestSrm;
        private long _lastEnableMs;
        private long _drainNow;

        // ── ITM subscription reports (col03-IN FF 05 pushes) ──────────────
        // The firmware pushes these on ITM page changes; they tell the host which
        // params to display at which handles. Buffered here during the identity
        // drain and consumed by the ITM display driver each frame.
        private readonly Action<byte[]> _bufferItm;
        private readonly object _itmLock = new object();
        private readonly System.Collections.Generic.List<byte[]> _itmReports =
            new System.Collections.Generic.List<byte[]>();
        private const int ItmReportBufferCap = 32;
        private bool _itmDropWarned;

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

        /// <summary>
        /// Raw module wire byte (FF 08 byte 0x1F) — the deepest module identifier.
        /// A non-zero value with a null <see cref="ModuleCode"/> means a module is
        /// present but not in the decode table (report it). 0 when no module.
        /// </summary>
        public byte ModuleWireCode { get; private set; }

        /// <summary>
        /// Whether the connected device is an SRM Conversion Kit (a wheel-side base impersonator),
        /// identified via its <c>DE FA</c> channel rather than <c>FF 08</c>. Its wheel is hard-wired,
        /// so this identity is fixed for the connection. False for a genuine Fanatec base.
        /// </summary>
        public bool IsSrmConverter { get; private set; }

        /// <summary>SRM Conversion Kit firmware (e.g. "6.12") when <see cref="IsSrmConverter"/>, else null.</summary>
        public string SrmKitFirmware { get; private set; }

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
        /// Whether the base's FF 08 identity has been read since connecting. False
        /// in the brief window between the HID transport opening and the first
        /// identity commit — connected but not yet identified. Derived from
        /// <see cref="BaseType"/> (0 until the first commit, reset to 0 on
        /// disconnect), so there is no separate flag to keep in sync.
        /// </summary>
        public bool HasIdentity => BaseType != 0 || IsSrmConverter;

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
                string module =
                    ModuleCode != null          ? " + " + ModuleCode :
                    IsHub && ModuleWireCode != 0 ? " + Module 0x" + ModuleWireCode.ToString("X2") + " (please report)" :
                                                   "";
                return label + module;
            }
        }

        // ── Friendly display names (UI only) ──────────────────────────────

        /// <summary>Friendly wheelbase name (e.g. "ClubSport DD+"), or null if unrecognized.</summary>
        public string BaseFriendlyName => FanatecIdentity.FriendlyBase(BaseCode);

        /// <summary>Friendly attached wheel/hub name (e.g. "Podium Hub"), or null if unrecognized.</summary>
        public string AttachmentFriendlyName => FanatecIdentity.FriendlyAttachment(WheelCode);

        /// <summary>Friendly button-module name (e.g. "Button Module Rally"), or null when none/unrecognized.</summary>
        public string ModuleFriendlyName => FanatecIdentity.FriendlyModule(ModuleCode);

        // ── Diagnostics ──────────────────────────────────────────────────

        /// <summary>
        /// The full bytes of the most recently drained FF 08 system report (a
        /// private copy), or null if none has been received. Updated on every
        /// reading — not just committed changes — so a capture taken on a
        /// sitting-still unrecognized wheel still reflects the live frame.
        /// </summary>
        public byte[] LastRawReport { get; private set; }

        /// <summary>
        /// Why the most recent connect attempt failed (no Fanatec device, no col03
        /// interface, HID open failure, …), or null after a successful connect.
        /// Surfaced so a "device not detected" regression is diagnosable without
        /// opening the SimHub log.
        /// </summary>
        public string LastConnectError { get; private set; }

        // De-dupes connect-failure logging: ConnectionMonitor retries every few seconds,
        // so a given failure is logged once (until it changes or a connect succeeds),
        // while LastConnectError is still updated every attempt for the live UI/status.
        private string _lastLoggedConnectError;

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
                    return FailConnect("No Fanatec devices (VID 0x0EB7) found on the HID bus.");

                // The wheelbase is the device that exposes the col03 (64-byte)
                // control interface; accessories (pedals, etc.) do not.
                int basePid = PickBasePid(fanatecDevices);
                if (basePid == 0)
                    return FailConnect("Fanatec device(s) present, but no col03 (64-byte) interface found.");

                return Adopt(basePid, fanatecDevices);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("FanatecWheelbase: AutoConnect error: " + ex.Message);
                LastConnectError = "AutoConnect error: " + ex.Message;
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
                LastConnectError = "Connect error: " + ex.Message;
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
                return FailConnect(DescribeConnectFailure(_transport.LastConnectStatus, ProductName, productId));

            ConnectedProductId = productId;
            LastConnectError = null;          // connected successfully
            _lastLoggedConnectError = null;   // re-arm failure logging for the next drop

            SimHub.Logging.Current.Info(string.Format(
                "FanatecWheelbase: {0} (PID 0x{1:X4})", ProductName, productId));

            // Enable the system report (turns on the firmware's push-on-change) and
            // read the initial identity once. From here on UpdateIdentity only
            // listens for pushes — no triggering.
            try
            {
                long connectNow = _clock.ElapsedMilliseconds;
                _lastEnableMs = connectNow;
                _lastSrmQueryMs = connectNow - SrmQueryMs;   // allow an immediate SRM ping if FF 08 is silent
                _pendingSrm = null;
                if (_reportReader.ReadInitial(_transport, out var reading))
                    IngestReading(reading, connectNow);
                // If FF 08 was silent this might be an SRM kit (or a slow genuine base). No probe here:
                // UpdateIdentity keeps reading FF 08 and pings DE FA while unidentified; whichever wins.

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

        // Pick the wheelbase PID: prefer a Fanatec device exposing a 64-byte report.
        // Deliberately looser than the transport's col03 check (which requires a 64-byte
        // OUTPUT): matching a 64-byte INPUT too lets an input-only base still be adopted,
        // so the transport can report NoCol03Interface rather than the base vanishing here.
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

        // Records the connect-failure reason for the live UI/status, logging it only when
        // it changes — ConnectionMonitor retries every few seconds, so logging every
        // attempt would spam identical lines.
        private bool FailConnect(string message)
        {
            LastConnectError = message;
            if (!string.Equals(message, _lastLoggedConnectError, StringComparison.Ordinal))
            {
                SimHub.Logging.Current.Warn("FanatecWheelbase: " + message);
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

            // An SRM converter's identity is a fixed one-shot — nothing to drain or re-settle once
            // committed. Genuine bases fall through to the unchanged FF 08 path.
            if (IsSrmConverter)
                return false;

            long now = _clock.ElapsedMilliseconds;

            // Keep the push-on-change subscription alive (the enable can lapse).
            if (now - _lastEnableMs >= IdentityReEnableMs)
            {
                _lastEnableMs = now;
                try { _reportReader.Enable(_transport); }
                catch { /* transient; the next tick retries */ }
            }

            // One col03 read loop, dispatched by signature: FF 08 -> _ingest, 0xDD -> _ingestSrm, FF 05 -> ITM.
            _drainNow = now;
            _reportReader.DrainPushes(_transport, _ingest, _bufferItm, _ingestSrm);

            // Commit a converter identity the drain routed to us — after the batch, so WheelChanged
            // isn't raised under the transport write lock.
            if (_pendingSrm.HasValue)
            {
                var srm = _pendingSrm.Value;
                _pendingSrm = null;
                CommitSrmIdentity(srm);
            }

            bool changed = _settler.Tick(now, out _, out _);
            _identityStable = _settler.IsStable;
            if (changed && !IsSrmConverter)
                CommitIdentity();

            // Still unidentified and idle? Ping the DE FA channel (harmless to a genuine base). Any 0xDD
            // reply is picked up by the next drain. Once FF 08 identifies a base, we never get here.
            if (!WheelDetected && _identityStable && !IsSrmConverter && now - _lastSrmQueryMs >= SrmQueryMs)
            {
                _lastSrmQueryMs = now;
                try { _srmReader.SendQuery(_transport); }
                catch { /* transient */ }
            }

            return changed;
        }

        // Drain callback: record the reading and offer it to the settler. _drainNow
        // carries the current clock so the cached delegate needs no per-frame closure.
        private void OnDrainReading(SystemReportReader.Reading r) => IngestReading(r, _drainNow);

        // Drain callback for a 0xDD frame: decode + stash; UpdateIdentity commits it after the batch.
        private void OnDrainSrm(byte[] frame)
        {
            if (IsSrmConverter || _pendingSrm.HasValue) return;
            if (SrmConverterIdentity.TryDecodeFrame(frame, frame.Length, out var srm))
                _pendingSrm = srm;
        }

        // Drain callback for ITM subscription reports: buffer them for the ITM driver.
        private void BufferItmReport(byte[] report)
        {
            lock (_itmLock)
            {
                // Bound the buffer so a consumer that stalls (e.g. during the setup wizard)
                // can't grow it without limit. Evict the OLDEST when full — only the latest
                // subscription state matters, so keeping the newest reports is what the
                // driver needs. Warn once so a stall is diagnosable.
                if (_itmReports.Count >= ItmReportBufferCap)
                {
                    _itmReports.RemoveAt(0);
                    if (!_itmDropWarned)
                    {
                        _itmDropWarned = true;
                        SimHub.Logging.Current.Warn(
                            "FanatecWheelbase: ITM subscription buffer full (" + ItmReportBufferCap +
                            ") — dropping oldest reports; is the ITM driver draining?");
                    }
                }
                _itmReports.Add(report);
            }
        }

        /// <summary>
        /// Passes each ITM subscription report the firmware has pushed since the last
        /// call to <paramref name="handler"/>, then clears the buffer. Called each frame
        /// by the ITM display driver so it can follow the wheel's current page.
        /// </summary>
        public void DrainItmReports(Action<byte[]> handler)
        {
            if (handler == null) return;
            byte[][] batch;
            lock (_itmLock)
            {
                if (_itmReports.Count == 0) return;
                batch = _itmReports.ToArray();
                _itmReports.Clear();
            }
            foreach (var r in batch)
                handler(r);
        }

        private void IngestReading(SystemReportReader.Reading r, long now)
        {
            _lastReading = r;
            LastRawReport = r.Raw;   // retain per-reading, even before/without a settled commit
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
            ModuleWireCode = isHub ? _lastReading.ModRaw : (byte)0;   // 0x1F is only meaningful on a hub
            BaseType = _lastReading.BaseType;
            BaseCode = FanatecIdentity.DecodeBaseCode(_lastReading.BaseType);

            ResolveCapabilities("Wheel changed");
            WheelChanged?.Invoke(this);
        }

        // Commit a fixed SRM converter identity from the DE FA channel (a one-shot — no settler). Rim +
        // module come from DE FA, not FF 08; BaseType stays 0 (the emulated base is not a real model).
        private void CommitSrmIdentity(SrmConverterIdentity.Result srm)
        {
            IsSrmConverter = true;
            SrmKitFirmware = srm.KitFirmware;

            byte wire = srm.WheelId;
            bool isHub = FanatecIdentity.IsHub(wire);

            WheelDetected = wire != 0;
            WheelCode = srm.WheelCode;                       // SRM map (handles 0x17); null when unknown id
            WheelWireCode = wire;
            IsHub = isHub;
            ModuleCode = isHub ? srm.ModuleCode : null;      // module only meaningful on a hub
            ModuleWireCode = isHub ? srm.ModuleRaw : (byte)0;
            BaseType = 0;
            BaseCode = null;
            _identityStable = true;                          // fixed identity — always settled

            ResolveCapabilities("SRM converter identified");
            WheelChanged?.Invoke(this);
            SimHub.Logging.Current.Info(string.Format(
                "FanatecWheelbase: SRM Conversion Kit — Wheel={0} (id 0x{1:X2}), Module={2}, KitFw={3}",
                WheelCode ?? "unknown", wire, ModuleCode ?? "(none)", SrmKitFirmware ?? "?"));
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
                "FanatecWheelbase: {0} — Base={1} (0x{2:X2}), Detected={3}, Wheel={4} (wire 0x{5:X2}), Module={6} (0x{7:X2}), Override={8}, Caps={9}",
                logContext,
                BaseCode ?? "(unknown)",
                BaseType,
                WheelDetected,
                WheelCode ?? "(unrecognized)",
                WheelWireCode,
                ModuleCode ?? "(none)",
                ModuleWireCode,
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
            lock (_itmLock) _itmReports.Clear();

            ConnectedProductId = 0;
            ProductName = null;
            WheelDetected = false;
            WheelCode = null;
            WheelWireCode = 0;
            IsHub = false;
            ModuleCode = null;
            ModuleWireCode = 0;
            BaseType = 0;
            BaseCode = null;
            IsSrmConverter = false;
            SrmKitFirmware = null;
            _pendingSrm = null;
            CurrentCapabilities = WheelCapabilities.None;
            LastRawReport = null;
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
