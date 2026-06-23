using System;
using FanaBridge.Devices;
using FanaBridge.Profiles;
using FanaBridge.Protocol;

namespace FanaBridge.Transport
{
    /// <summary>
    /// Represents a connected Fanatec wheelbase — the root of all communication.
    /// The PC only ever talks to the wheelbase; the attached wheel/hub and any
    /// button module are state the base reports, not independent connections.
    ///
    /// As of the device-architecture split this is a thin FAÇADE: a generic
    /// <see cref="FanatecBaseDevice"/> (transport + connect lifecycle + a bound
    /// per-class driver) plus a <see cref="CapabilityResolver"/>. The FF 08 wire
    /// exchange + base/wheel/module decode now live in <c>FanatecBaseDriver</c>; this
    /// type re-projects the driver's <see cref="DeviceSnapshot"/> into the flat
    /// identity properties existing SimHub adapters / UI still consume, so the split
    /// is behavior-preserving. It is removed once those callers bind to the peripheral
    /// view (the next phase).
    /// </summary>
    public class FanatecWheelbase : IDisposable, IServiceableDevice
    {
        /// <summary>Fanatec's USB vendor id — alias of <see cref="FanatecIds.VendorId"/>.</summary>
        public const ushort FANATEC_VENDOR_ID = FanatecIds.VendorId;

        private readonly FanatecBaseDevice _device = new FanatecBaseDevice();
        private readonly CapabilityResolver _resolver = new CapabilityResolver();
        private bool _disposed;

        public FanatecWheelbase()
        {
            _device.SnapshotChanged += OnSnapshotChanged;
        }

        /// <summary>The wheelbase's HID transport — used by LED/display/tuning encoders.</summary>
        public IDeviceTransport Transport => _device.Transport;

        // ── Connection state ─────────────────────────────────────────────

        /// <summary>Whether the wheelbase is connected (tracks the live transport).</summary>
        public bool IsConnected => _device.IsConnected;

        /// <summary>Whether the wheelbase's HID device is still present on the bus.</summary>
        public bool IsDevicePresent => _device.IsDevicePresent;

        /// <summary>The USB product ID of the connected wheelbase, or 0 if not connected.</summary>
        public int ConnectedProductId => _device.ConnectedProductId;

        /// <summary>Product name from the HID descriptor of the connected device.</summary>
        public string ProductName => _device.ProductName;

        /// <summary>
        /// Why the most recent connect attempt failed, or null after a successful
        /// connect. Surfaced so a "device not detected" regression is diagnosable
        /// without opening the SimHub log.
        /// </summary>
        public string LastConnectError => _device.LastConnectError;

        // ── Identity (re-projected from the bound driver's snapshot) ───────

        private DeviceSnapshot Snap => _device.Snapshot;

        private Attachment? WheelAttachment
        {
            get
            {
                var atts = Snap.Attachments;
                if (atts != null)
                    foreach (var a in atts)
                        if (a.Kind == PeripheralKind.Wheel || a.Kind == PeripheralKind.Hub)
                            return a;
                return null;
            }
        }

        private Attachment? ModuleAttachment
        {
            get
            {
                var atts = Snap.Attachments;
                if (atts != null)
                    foreach (var a in atts)
                        if (a.Kind == PeripheralKind.Module)
                            return a;
                return null;
            }
        }

        /// <summary>Raw BaseType byte from the FF 08 report (byte 0x02).</summary>
        public byte BaseType => Snap.BaseTypeByte;

        /// <summary>FanaBridge wheelbase code (e.g. "CSDDPlus"), or null if unrecognized.</summary>
        public string BaseCode => Snap.Code;

        /// <summary>Whether a wheel or hub is currently attached.</summary>
        public bool WheelDetected => WheelAttachment != null;

        /// <summary>
        /// Stable profile-match code for the attached wheel/hub (e.g. "PSWBMW",
        /// "PHUB"), or null when nothing is attached / the wire code is unrecognized.
        /// </summary>
        public string WheelCode => WheelAttachment?.Code;

        /// <summary>
        /// Raw attachment wire code (FF 08 byte 0x18) — the deepest, firmware-defined
        /// identifier. 0 when nothing is attached.
        /// </summary>
        public byte WheelWireCode => WheelAttachment?.WireCode ?? 0;

        /// <summary>Whether the attachment is a hub (accepts a button module).</summary>
        public bool IsHub => WheelAttachment?.Kind == PeripheralKind.Hub;

        /// <summary>FanaBridge button-module code ("PBME"/"PBMR"), or null when none.</summary>
        public string ModuleCode => ModuleAttachment?.Code;

        /// <summary>
        /// Raw module wire byte (FF 08 byte 0x1F) — the deepest module identifier.
        /// A non-zero value with a null <see cref="ModuleCode"/> means a module is
        /// present but not in the decode table (report it). 0 when no module.
        /// </summary>
        public byte ModuleWireCode => ModuleAttachment?.WireCode ?? 0;

        /// <summary>Resolved capability profile for the current wheel + module combination.</summary>
        public WheelCapabilities CurrentCapabilities { get; private set; } = WheelCapabilities.None;

        /// <summary>
        /// Whether a wheel/hub is identified (attached AND recognized, not just transitional).
        /// </summary>
        public bool WheelIdentified => WheelDetected && !string.IsNullOrEmpty(WheelCode);

        /// <summary>
        /// Whether the attachment identity is settled (not mid-transition). False while
        /// a changed reading is still settling — consumers should treat the device as
        /// not-yet-connected and suppress output during that window.
        /// </summary>
        public bool IdentityStable => Snap.Stable;

        /// <summary>
        /// Whether the base's FF 08 identity has been read since connecting. False in
        /// the brief window between the HID transport opening and the first identity
        /// commit — connected but not yet identified.
        /// </summary>
        public bool HasIdentity => Snap.HasIdentity;

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
        /// The full bytes of the most recently drained FF 08 system report (a private
        /// copy), or null if none has been received. Updated on every reading — not
        /// just committed changes — so a capture on a sitting-still unrecognized wheel
        /// still reflects the live frame.
        /// </summary>
        public byte[] LastRawReport => Snap.LastRawReport;

        // ── Events ───────────────────────────────────────────────────────

        /// <summary>
        /// Fired when the attached wheel/hub or module changes, including transitions
        /// to/from undetected. The capabilities are already updated when this fires.
        /// </summary>
        public event Action<FanatecWheelbase> WheelChanged;

        // ── Configuration ────────────────────────────────────────────────

        /// <summary>
        /// Optional callback that returns a profile override ID for a given wheel match
        /// key (e.g. "PHUB_PBMR"). Set by the plugin to integrate with
        /// <see cref="FanatecPluginSettings.ProfileOverrides"/>. Return null or empty
        /// to use default auto-resolution.
        /// </summary>
        public Func<string, string> ProfileOverrideResolver
        {
            get => _resolver.ProfileOverrideResolver;
            set => _resolver.ProfileOverrideResolver = value;
        }

        // ── Discovery / connection ────────────────────────────────────────

        /// <summary>
        /// Scans the HID bus for a Fanatec wheelbase (a device exposing the 64-byte
        /// col03 interface) and records its PID + name.
        /// </summary>
        public bool AutoConnect() => !_disposed && _device.AutoConnect();

        /// <summary>
        /// Connects to a specific product ID. Use this when the user has overridden
        /// auto-detection in settings.
        /// </summary>
        public bool Connect(int productId) => !_disposed && _device.Connect(productId);

        // ── Identity servicing ────────────────────────────────────────────

        /// <summary>
        /// Drains pushed FF 08 system reports and advances the settle timer. Called
        /// each frame by <see cref="ConnectionMonitor"/>. A settled change resolves
        /// capabilities and raises <see cref="WheelChanged"/> via the snapshot event.
        /// Returns true when a new identity was committed this call.
        /// </summary>
        public bool Service() => _device.Service();

        // Snapshot committed by the bound driver: resolve capabilities (so they are
        // current before observers run), then re-surface as WheelChanged.
        private void OnSnapshotChanged(FanatecBaseDevice device)
        {
            ResolveAndLog("Wheel changed");
            WheelChanged?.Invoke(this);
        }

        /// <summary>
        /// Forces a re-evaluation of capabilities against the current profile store.
        /// Call after <see cref="WheelProfileStore.Reload"/> to pick up newly-saved
        /// profiles without a SimHub restart or a physical wheel change.
        /// </summary>
        public void RefreshCapabilities()
        {
            if (!WheelDetected)
                return;

            ResolveAndLog("RefreshCapabilities");
            WheelChanged?.Invoke(this);
        }

        // Resolve capabilities for the current attachment (respecting a user override)
        // and log the decode, mirroring the old FanatecWheelbase.ResolveCapabilities.
        private void ResolveAndLog(string logContext)
        {
            if (!WheelDetected)
            {
                CurrentCapabilities = WheelCapabilities.None;
                return;
            }

            CurrentCapabilities = _resolver.Resolve(WheelCode, ModuleCode, out var overrideId);

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
            _device.Disconnect();
            CurrentCapabilities = WheelCapabilities.None;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _device.SnapshotChanged -= OnSnapshotChanged;
                _device.Dispose();
            }
        }

        // ── Connect-failure messaging ─────────────────────────────────────

        /// <summary>
        /// Maps the transport's categorised connect outcome to a concise reason.
        /// Forwards to <see cref="FanatecBaseDevice.DescribeConnectFailure"/>; kept here
        /// so existing callers/tests reach it unchanged.
        /// </summary>
        internal static string DescribeConnectFailure(
            FanatecTransport.TransportConnectStatus status, string productName, int productId)
            => FanatecBaseDevice.DescribeConnectFailure(status, productName, productId);
    }
}
