using System;
using System.Linq;
using FanaBridge.Identity;
using FanaBridge.Profiles;
using FanatecManaged;
using HidSharp;

namespace FanaBridge.Transport
{
    /// <summary>
    /// Resolves "what Fanatec hardware is present" — wheelbase, rim/hub, and
    /// button module — entirely over HID via the col03 <c>FF 08</c> system
    /// report. No Fanatec driver/service and no SimHub.FanatecManaged.dll: the
    /// identity tables are owned by FanaBridge (see <see cref="FanatecIdentity"/>).
    ///
    /// This is the single source of truth for wheel identity. Because it talks
    /// pure HID, it also works for Fanatec rims on non-Fanatec / SRM wheelbases,
    /// provided the base emits the FF 08 system report.
    /// </summary>
    public class FanatecSdkManager : IDisposable, ISdkConnection
    {
        public const ushort FANATEC_VENDOR_ID = 0x0EB7;

        // HID transport (col03) used to trigger + read the FF 08 system report.
        private readonly IDeviceTransport _transport;
        private bool _disposed;

        public FanatecSdkManager(IDeviceTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        // ── Connection state ─────────────────────────────────────────────

        /// <summary>Whether a Fanatec wheelbase has been located on the HID bus.</summary>
        public bool IsConnected { get; private set; }

        /// <summary>The USB product ID of the connected wheelbase, or 0 if not connected.</summary>
        public int ConnectedProductId { get; private set; }

        /// <summary>Product name from the HID descriptor of the connected device.</summary>
        public string ProductName { get; private set; }

        // ── Wheel identity ───────────────────────────────────────────────

        /// <summary>Whether a steering wheel rim is currently attached.</summary>
        public bool WheelDetected { get; private set; }

        /// <summary>The steering wheel / hub type, decoded from FF 08 byte 0x18.</summary>
        public M_FS_WHEEL_SWTYPE SteeringWheelType { get; private set; }
            = M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNINITIALIZED;

        /// <summary>The attached button-module type (decoded from FF 08 byte 0x1F).</summary>
        public M_FS_WHEEL_SW_MODULETYPE SubModuleType { get; private set; }
            = M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED;

        /// <summary>Whether the attached rim is a hub (accepts a button module).</summary>
        public bool IsHub { get; private set; }

        /// <summary>Friendly rim/hub name (e.g. "Podium Steering Wheel BMW M4 GT3").</summary>
        public string RimName { get; private set; }

        /// <summary>Friendly module name (e.g. "Podium Button Module Rally"), or null.</summary>
        public string ModuleName { get; private set; }

        /// <summary>Raw BaseType byte from the FF 08 report (FF 08 byte 0x02).</summary>
        public byte BaseType { get; private set; }

        /// <summary>Friendly wheelbase name (e.g. "ClubSport DD / ClubSport DD+").</summary>
        public string BaseName { get; private set; }

        /// <summary>Resolved capability profile for the current wheel + module combination.</summary>
        public WheelCapabilities CurrentCapabilities { get; private set; }
            = WheelCapabilities.None;

        /// <summary>
        /// Whether a wheel is identified (detected AND type is known, not just transitional).
        /// </summary>
        public bool WheelIdentified =>
            WheelDetected
            && SteeringWheelType != M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNINITIALIZED;

        /// <summary>Human-readable display name for the current wheel (including module).</summary>
        public string WheelDisplayName
        {
            get
            {
                if (!WheelDetected)
                    return "No wheel attached";
                // Prefer a matched profile's name; otherwise fall back to the
                // FF 08-decoded rim name so even unprofiled wheels show correctly.
                if (CurrentCapabilities != null && !string.IsNullOrEmpty(CurrentCapabilities.Name))
                    return CurrentCapabilities.Name;
                if (string.IsNullOrEmpty(RimName))
                    return "Detecting...";
                return string.IsNullOrEmpty(ModuleName) ? RimName : RimName + " + " + ModuleName;
            }
        }

        // ── Events ───────────────────────────────────────────────────────

        /// <summary>
        /// Fired when the detected wheel type or module changes, including transitions
        /// to/from undetected. The WheelCapabilities are already updated when this fires.
        /// </summary>
        public event Action<FanatecSdkManager> WheelChanged;

        // ── Discovery ────────────────────────────────────────────────────

        /// <summary>
        /// Scans the HID bus for a Fanatec wheelbase (a device exposing the
        /// 64-byte col03 interface) and records its PID + name. No SDK, no DLL.
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
                    SimHub.Logging.Current.Debug("FanatecSdkManager: No Fanatec devices found on HID bus");
                    return false;
                }

                // The wheelbase is the device that exposes the col03 (64-byte)
                // control interface; accessories (pedals, etc.) do not.
                int basePid = PickBasePid(fanatecDevices);
                if (basePid == 0)
                {
                    SimHub.Logging.Current.Warn("FanatecSdkManager: No Fanatec wheelbase (col03) interface found");
                    return false;
                }

                return Adopt(basePid, fanatecDevices);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error("FanatecSdkManager: AutoConnect error: " + ex.Message);
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
                SimHub.Logging.Current.Error("FanatecSdkManager: Connect error: " + ex.Message);
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

            ConnectedProductId = productId;
            IsConnected = true;

            SimHub.Logging.Current.Info(string.Format(
                "FanatecSdkManager: Wheelbase {0} (PID 0x{1:X4})", ProductName, productId));

            // Best-effort initial poll. The HID transport may not be open yet
            // (it's connected after us in the plugin's connect sequence), in
            // which case this is a no-op until the periodic poll runs.
            try { PollWheelIdentity(); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("FanatecSdkManager: Initial poll failed: " + ex.Message);
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

        // ── Polling ──────────────────────────────────────────────────────

        /// <summary>
        /// Optional callback that returns a profile override ID for a given
        /// wheel match key (e.g. "PHUB_PBMR").  Set by the plugin to integrate
        /// with <see cref="FanatecPluginSettings.ProfileOverrides"/>.
        /// Return null or empty to use default auto-resolution.
        /// </summary>
        public Func<string, string> ProfileOverrideResolver { get; set; }

        /// <summary>
        /// Reads the FF 08 system report and updates wheel identity. Call
        /// periodically (not every frame). Returns true if identity changed.
        /// </summary>
        public bool PollWheelIdentity()
        {
            if (!IsConnected)
                return false;

            if (!Ff08IdentityReader.TryRead(_transport, ConnectedProductId, out var id))
                return false; // transport not ready or no FF 08 report this round

            var prevType = SteeringWheelType;
            var prevModule = SubModuleType;
            var prevDetected = WheelDetected;

            WheelDetected = id.Detected;
            SteeringWheelType = id.SteeringWheelType;
            SubModuleType = id.ModuleType;
            IsHub = id.IsHub;
            RimName = id.RimName;
            ModuleName = id.ModuleName;
            BaseType = id.BaseType;
            BaseName = id.BaseName;

            bool changed = prevType != SteeringWheelType
                || prevModule != SubModuleType
                || prevDetected != WheelDetected;

            if (changed)
            {
                ResolveCapabilities("Wheel changed");
                WheelChanged?.Invoke(this);
            }

            return changed;
        }

        /// <summary>
        /// Forces a re-evaluation of wheel capabilities against the current
        /// profile store.  Call after <see cref="WheelProfileStore.Reload"/>
        /// to pick up newly-saved profiles without requiring a SimHub restart
        /// or a physical wheel type change.
        /// </summary>
        public void RefreshCapabilities()
        {
            if (!WheelDetected)
                return;

            ResolveCapabilities("RefreshCapabilities");
            WheelChanged?.Invoke(this);
        }

        /// <summary>
        /// Shared implementation: resolves the best profile for the current
        /// wheel, respecting any user override from the plugin settings.
        /// </summary>
        private void ResolveCapabilities(string logContext)
        {
            if (!WheelDetected)
            {
                CurrentCapabilities = WheelCapabilities.None;
                return;
            }

            string wheelCode = WheelProfileStore.StripWheelPrefix(SteeringWheelType.ToString());
            string moduleCode = SubModuleType == M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED
                ? null
                : WheelProfileStore.StripModulePrefix(SubModuleType.ToString());

            // Build the match key the same way profiles build their IDs
            string matchKey = wheelCode;
            if (moduleCode != null)
                matchKey += "_" + moduleCode;

            // Check for a user override
            string overrideId = ProfileOverrideResolver?.Invoke(matchKey);

            var profile = WheelProfileStore.FindByWheelType(wheelCode, moduleCode, overrideId);
            CurrentCapabilities = profile != null
                ? new WheelCapabilities(profile)
                : WheelCapabilities.None;

            SimHub.Logging.Current.Info(string.Format(
                "FanatecSdkManager: {0} — Base={1}, Detected={2}, Type={3}, Module={4}, Override={5}, Caps={6}",
                logContext,
                BaseName ?? "(unknown)",
                WheelDetected,
                SteeringWheelType,
                SubModuleType,
                overrideId ?? "(auto)",
                CurrentCapabilities.Name ?? "(none)"));
        }

        // ── Lifecycle ────────────────────────────────────────────────────

        /// <summary>Resets all identity state. The HID transport is owned elsewhere.</summary>
        public void Disconnect()
        {
            IsConnected = false;
            ConnectedProductId = 0;
            ProductName = null;
            WheelDetected = false;
            SteeringWheelType = M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNINITIALIZED;
            SubModuleType = M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED;
            IsHub = false;
            RimName = null;
            ModuleName = null;
            BaseType = 0;
            BaseName = null;
            CurrentCapabilities = WheelCapabilities.None;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Disconnect();
            }
        }
    }
}
