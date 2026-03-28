using System;
using System.Linq;
using FanaBridge.Profiles;
using FanatecManaged;
using HidSharp;

namespace FanaBridge.Transport
{
    /// <summary>
    /// Manages the Fanatec SDK connection lifecycle and provides unified
    /// access to device identity, wheel type, and capabilities.
    ///
    /// This is the single source of truth for "what Fanatec hardware is present."
    /// It replaces the previous approach of requiring a hard-coded product ID.
    /// </summary>
    public class FanatecSdkManager : IDisposable, ISdkConnection
    {
        public const ushort FANATEC_VENDOR_ID = 0x0EB7;

        private WheelInterface _wheelInterface;
        private bool _disposed;

        // ── Connection state ─────────────────────────────────────────────

        /// <summary>Whether the SDK is connected to a Fanatec wheelbase.</summary>
        public bool IsConnected { get; private set; }

        /// <summary>The USB product ID of the connected wheelbase, or 0 if not connected.</summary>
        public int ConnectedProductId { get; private set; }

        /// <summary>Product name from the HID descriptor of the connected device.</summary>
        public string ProductName { get; private set; }

        // ── Wheel identity ───────────────────────────────────────────────

        /// <summary>Whether a steering wheel rim is currently attached.</summary>
        public bool WheelDetected { get; private set; }

        /// <summary>The raw steering wheel type enum from the SDK.</summary>
        public M_FS_WHEEL_SWTYPE SteeringWheelType { get; private set; }
            = M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNINITIALIZED;

        /// <summary>The raw sub-module type enum (button module).</summary>
        public M_FS_WHEEL_SW_MODULETYPE SubModuleType { get; private set; }
            = M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED;

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
                if (!WheelIdentified)
                    return "Detecting...";
                return CurrentCapabilities.Name;
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
        /// Scans the HID bus for any Fanatec device and attempts to connect
        /// via the SDK. Returns true if a wheelbase was found and connected.
        /// No product ID is required — this is fully automatic.
        /// </summary>
        public bool AutoConnect()
        {
            if (_disposed) return false;

            // Release any existing connection first
            Disconnect();

            try
            {
                // Find all Fanatec HID devices, grouped by product ID
                var fanatecPids = DeviceList.Local.GetHidDevices()
                    .Where(d => d.VendorID == FANATEC_VENDOR_ID)
                    .Select(d => d.ProductID)
                    .Distinct()
                    .ToList();

                if (fanatecPids.Count == 0)
                {
                    SimHub.Logging.Current.Debug("FanatecSdkManager: No Fanatec devices found on HID bus");
                    return false;
                }

                SimHub.Logging.Current.Info(string.Format(
                    "FanatecSdkManager: Found {0} Fanatec PID(s): {1}",
                    fanatecPids.Count,
                    string.Join(", ", fanatecPids.Select(p => "0x" + p.ToString("X4")))));

                // Try connecting the SDK to each PID until one succeeds
                foreach (int pid in fanatecPids)
                {
                    if (TrySdkConnect(pid))
                    {
                        // Resolve product name from the HID descriptor
                        try
                        {
                            var device = DeviceList.Local.GetHidDevices()
                                .FirstOrDefault(d => d.VendorID == FANATEC_VENDOR_ID && d.ProductID == pid);
                            ProductName = device?.GetProductName() ?? "Fanatec Device";
                        }
                        catch
                        {
                            ProductName = "Fanatec Device";
                        }

                        ConnectedProductId = pid;
                        SimHub.Logging.Current.Info(string.Format(
                            "FanatecSdkManager: Connected to {0} (PID 0x{1:X4})",
                            ProductName, pid));

                        // Do initial wheel poll (best-effort, may get UNINITIALIZED)
                        try { PollWheelIdentity(); }
                        catch (Exception ex)
                        {
                            SimHub.Logging.Current.Warn("FanatecSdkManager: Initial poll failed: " + ex.Message);
                        }
                        return true;
                    }
                }

                SimHub.Logging.Current.Warn("FanatecSdkManager: SDK Connect failed for all detected PIDs");
                return false;
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

            if (TrySdkConnect(productId))
            {
                try
                {
                    var device = DeviceList.Local.GetHidDevices()
                        .FirstOrDefault(d => d.VendorID == FANATEC_VENDOR_ID && d.ProductID == productId);
                    ProductName = device?.GetProductName() ?? "Fanatec Device";
                }
                catch
                {
                    ProductName = "Fanatec Device";
                }

                ConnectedProductId = productId;
                SimHub.Logging.Current.Info(string.Format(
                    "FanatecSdkManager: Connected to {0} (PID 0x{1:X4})",
                    ProductName, productId));

                try { PollWheelIdentity(); }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn("FanatecSdkManager: Initial poll failed: " + ex.Message);
                }
                return true;
            }

            return false;
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
        /// Polls the SDK for current wheel identity. Call periodically (not every frame).
        /// Returns true if the wheel type changed since last poll.
        /// </summary>
        public bool PollWheelIdentity()
        {
            if (!IsConnected || _wheelInterface == null)
                return false;

            var info = _wheelInterface.GetDeviceInfo();

            var prevType = SteeringWheelType;
            var prevModule = SubModuleType;
            var prevDetected = WheelDetected;

            WheelDetected = info.Detected;
            SteeringWheelType = info.SteeringWheelType;
            SubModuleType = info.SubModuleType;

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
                "FanatecSdkManager: {0} — Detected={1}, Type={2}, Module={3}, Override={4}, Caps={5} (ButtonRgb={6}, ButtonAuxIntensity={7}, RevRgb={8}, FlagRgb={9}, Display={10})",
                logContext,
                WheelDetected,
                SteeringWheelType,
                SubModuleType,
                overrideId ?? "(auto)",
                CurrentCapabilities.Name ?? "(none)",
                CurrentCapabilities.ButtonRgbCount,
                CurrentCapabilities.ButtonAuxIntensityCount,
                CurrentCapabilities.RevRgbCount,
                CurrentCapabilities.FlagRgbCount,
                CurrentCapabilities.Display));
        }

        // ── Lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Disconnects the SDK and resets all state.
        /// </summary>
        public void Disconnect()
        {
            try
            {
                if (_wheelInterface != null && IsConnected)
                {
                    _wheelInterface.Release();
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("FanatecSdkManager: Release error: " + ex.Message);
            }

            _wheelInterface = null;
            IsConnected = false;
            ConnectedProductId = 0;
            ProductName = null;
            WheelDetected = false;
            SteeringWheelType = M_FS_WHEEL_SWTYPE.FS_WHEEL_SWTYPE_UNINITIALIZED;
            SubModuleType = M_FS_WHEEL_SW_MODULETYPE.FS_WHEEL_SW_MODULETYPE_UNINITIALIZED;
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

        // ── Internal ─────────────────────────────────────────────────────

        private bool TrySdkConnect(int productId)
        {
            try
            {
                var wi = new WheelInterface();
                bool result = wi.Connect(productId);

                if (result)
                {
                    _wheelInterface = wi;
                    IsConnected = true;
                    return true;
                }

                SimHub.Logging.Current.Info(
                    "FanatecSdkManager: SDK Connect returned false for PID 0x" + productId.ToString("X4"));
                return false;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(string.Format(
                    "FanatecSdkManager: SDK Connect threw for PID 0x{0:X4}: {1}",
                    productId, ex.Message));
                return false;
            }
        }
    }
}
