using FanaBridge.Adapters;
using FanaBridge.Devices;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using FanaBridge.UI;
using GameReaderCommon;
using SimHub.Plugins;
using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FanaBridge
{
    [PluginDescription("Fanatec wheel LED and display control via HID")]
    [PluginAuthor("kelchm")]
    [PluginName("FanaBridge")]
    public class FanatecPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        /// <summary>
        /// Singleton reference so DeviceInstance wrappers can access the shared
        /// hardware without owning their own HID connections.
        /// Set during Init(), cleared during End().
        /// </summary>
        public static FanatecPlugin Instance { get; private set; }

        public FanatecPluginSettings Settings { get; set; }

        // The device manager owns the connected device(s) and the peripheral view.
        // It replaces the old single FanatecWheelbase singleton.
        private DeviceManager _manager;
        private readonly CapabilityResolver _resolver = new CapabilityResolver();
        private WheelCapabilities _currentCaps = WheelCapabilities.None;


        /// <summary>Fired when connection status or wheel identity changes. May fire from any thread.</summary>
        public event Action StateChanged;

        /// <summary>
        /// Fired when the attached wheel/hub or module identity changes (a settled
        /// change). Distinct from <see cref="StateChanged"/> (which also fires on
        /// connect/disconnect) so identity-only consumers can react narrowly.
        /// </summary>
        public event Action IdentityChanged;

        /// <summary>
        /// When true, device instances skip all LED and display output so the
        /// profile wizard can send probe signals without being overwritten by
        /// SimHub's frame-by-frame updates.  Set by the wizard dialog.
        /// </summary>
        public bool WizardActive { get; set; }

        /// <summary>Whether the Fanatec device is currently connected (for UI binding).</summary>
        public bool IsDeviceConnected => _manager?.IsConnected == true;

        /// <summary>
        /// When disconnected, a short reason for it (the latest connect-attempt
        /// failure, else the latest runtime disconnect reason); null while
        /// connected. Surfaced on the Status row and in the diagnostics capture.
        /// </summary>
        public string StatusDetail =>
            IsDeviceConnected
                ? null
                : (_manager?.LastConnectError ?? _manager?.LastDisconnectReason);

        /// <summary>Name of the connected device (for UI binding).</summary>
        public string DeviceName => _manager?.DeviceName ?? "Not connected";

        /// <summary>Name of the currently detected steering wheel.</summary>
        public string WheelName => ComputeDisplayName();

        /// <summary>Whether the attachment identity is settled (not mid-transition).</summary>
        public bool IdentityStable => _manager?.IdentityStable == true;

        /// <summary>Whether the base's FF 08 identity has been read since connecting.</summary>
        public bool HasIdentity => _manager?.HasIdentity == true;

        /// <summary>Current wheel capabilities (for UI binding).</summary>
        public WheelCapabilities CurrentCapabilities => _currentCaps ?? WheelCapabilities.None;

        // ── Peripheral view (the stable identity API adapters / fast-follows bind to) ──

        /// <summary>The connected wheelbase peripheral, or null.</summary>
        public Peripheral PrimaryBasePeripheral => _manager?.BasePeripheral;

        /// <summary>The attached wheel/hub peripheral, or null.</summary>
        public Peripheral AttachedWheelPeripheral => _manager?.AttachedWheel;

        /// <summary>The attached button-module peripheral, or null.</summary>
        public Peripheral AttachedModulePeripheral => _manager?.AttachedModule;

        /// <summary>All currently-surfaced peripherals.</summary>
        public IReadOnlyList<Peripheral> Peripherals => _manager?.Peripherals ?? Array.Empty<Peripheral>();

        /// <summary>USB product id of the connected base, or 0.</summary>
        public int PrimaryBaseProductId => _manager?.PrimaryBaseProductId ?? 0;

        /// <summary>
        /// An immutable, display-oriented snapshot of the current base + attachment
        /// identity. The settings UI and diagnostics capture read this instead of a
        /// device object.
        /// </summary>
        public WheelIdentity Identity
        {
            get
            {
                var mgr = _manager;
                if (mgr == null)
                    return WheelIdentity.None;

                var snap = mgr.PrimarySnapshot;
                var w = mgr.AttachedWheel;
                var m = mgr.AttachedModule;

                bool detected = w != null;
                string wheelCode = w?.Code;
                byte wheelWire = w?.WireCode ?? 0;
                bool isHub = w?.Kind == PeripheralKind.Hub;
                string moduleCode = m?.Code;
                byte moduleWire = m?.WireCode ?? 0;

                return new WheelIdentity(
                    snap.HasIdentity, snap.Stable,
                    snap.BaseTypeByte, snap.Code, FanatecIdentity.FriendlyBase(snap.Code),
                    detected, wheelCode, wheelWire, isHub, FanatecIdentity.FriendlyAttachment(wheelCode),
                    moduleCode, moduleWire, FanatecIdentity.FriendlyModule(moduleCode),
                    IdentityFormatter.DisplayName(detected, wheelCode, wheelWire, isHub, moduleCode, moduleWire, _currentCaps?.Name),
                    snap.LastRawReport);
            }
        }

        private string ComputeDisplayName()
        {
            var w = _manager?.AttachedWheel;
            var m = _manager?.AttachedModule;
            bool detected = w != null;
            return IdentityFormatter.DisplayName(
                detected, w?.Code, w?.WireCode ?? 0, w?.Kind == PeripheralKind.Hub,
                m?.Code, m?.WireCode ?? 0, _currentCaps?.Name);
        }

        /// <summary>
        /// Whether the wheel/hub(+module) currently attached matches a specific device
        /// descriptor. The single predicate that replaces reaching into wheelbase
        /// identity fields — a DeviceInstance asks this about its own config.
        /// </summary>
        public bool MatchesAttachedWheel(DeviceConfig config)
        {
            if (config == null)
                return false;
            var w = _manager?.AttachedWheel;
            if (w == null)
                return false;
            return config.MatchesAttachment(true, w.Code, _manager.AttachedModule?.Code);
        }

        /// <summary>
        /// Resolves the capabilities a specific device descriptor should use.
        /// Returns the live, currently-active capabilities (which respect any user
        /// override) only when the connected wheel actually matches this
        /// <paramref name="config"/>; otherwise returns the config's own registration
        /// capabilities. <see cref="CurrentCapabilities"/> is global, so unrelated
        /// device instances must NOT consume it directly — this is the single guard
        /// that prevents the connected wheel's caps from leaking into every descriptor.
        /// </summary>
        public WheelCapabilities ResolveCapsFor(DeviceConfig config)
        {
            if (config == null)
                return WheelCapabilities.None;

            var current = _currentCaps;
            if (current?.Profile != null && MatchesAttachedWheel(config))
                return current;

            return config.Capabilities ?? WheelCapabilities.None;
        }

        /// <summary>Shared HID transport — used by DeviceInstance wrappers for hardware I/O.</summary>
        public IDeviceTransport Transport => _manager?.PrimaryTransport;

        // The encoders are owned per-device by the carrier's DeviceHandle. These
        // accessors forward to the primary device's handle for now; per-device
        // resolution (a DeviceInstance reading ITS device's handle) lands in P6.

        /// <summary>col03 LED encoder of the primary device — used by DeviceInstance LED drivers and wizard.</summary>
        public LedEncoder Leds => _manager?.PrimaryHandle?.Leds;

        /// <summary>col01 legacy LED encoder of the primary device — used by DeviceInstance LED drivers for legacy/RevStripe wheels.</summary>
        public LegacyLedEncoder LegacyLeds => _manager?.PrimaryHandle?.LegacyLeds;

        /// <summary>Display encoder of the primary device — used by DeviceInstance display managers and wizard.</summary>
        public DisplayEncoder Display => _manager?.PrimaryHandle?.Display;

        /// <summary>Tuning controller of the primary device — used by TuningSettingsPanel for encoder config.</summary>
        public FanatecTuningController Tuning => _manager?.PrimaryHandle?.Tuning;

        public PluginManager PluginManager { get; set; }

        public ImageSource PictureIcon => new BitmapImage(new Uri(
            "pack://application:,,,/FanaBridge;component/Resources/Images/plugin-icon.png"));

        public string LeftMenuTitle => "FanaBridge";

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("FanaBridge: Init starting");

            Settings = this.ReadCommonSettings<FanatecPluginSettings>(
                "FanaBridgeSettings",
                () => new FanatecPluginSettings());

            // The manager owns the HID transport and reads the FF 08 identity report
            // through its bound driver (no SimHub.FanatecManaged.dll). Each device's
            // encoder set is owned by its carrier's DeviceHandle, built over that same
            // transport; the plugin's encoder accessors forward to the primary handle.
            _manager = new DeviceManager(
                () => Settings.ProductIdOverride,
                msg => SimHub.Logging.Current.Warn(msg),
                msg => SimHub.Logging.Current.Info(msg));

            // Wire up profile override resolution from plugin settings.
            _resolver.ProfileOverrideResolver = (matchKey) =>
            {
                if (string.IsNullOrEmpty(matchKey))
                    return null;
                if (Settings.ProfileOverrides != null
                    && Settings.ProfileOverrides.TryGetValue(matchKey, out var overrideId))
                    return overrideId;
                return null;
            };

            _manager.Connected += () =>
            {
                this.TriggerEvent("DeviceConnected");
                StateChanged?.Invoke();
            };

            _manager.Disconnected += () =>
            {
                _currentCaps = WheelCapabilities.None;
                this.TriggerEvent("DeviceDisconnected");
                StateChanged?.Invoke();
            };

            _manager.PeripheralsChanged += () =>
            {
                ResolveCaps("Wheel changed");
                RaiseIdentityChanged();
            };

            // Publish the singleton only now that every shared field is constructed.
            // DeviceInstance wrappers reach back through Instance.Transport / the
            // identity API, so exposing it earlier would let them observe a half-built
            // plugin.
            Instance = this;

            // Attempt initial connection
            _manager.TryInitialConnect();

            // --- Properties ---
            this.AttachDelegate("FanaBridge.Connected", () => _manager.IsConnected);
            this.AttachDelegate("FanaBridge.DeviceName", () => _manager.DeviceName);
            this.AttachDelegate("FanaBridge.BaseName", () =>
                _manager.IsConnected ? (_manager.PrimarySnapshot.Code ?? "Unknown") : "Not connected");
            this.AttachDelegate("FanaBridge.WheelName", () => ComputeDisplayName());
            this.AttachDelegate("FanaBridge.ModuleName", () => _manager.AttachedModule?.Code ?? "None");
            this.AttachDelegate("FanaBridge.DisplayName", () => ComputeDisplayName());
            this.AttachDelegate("FanaBridge.IsHub", () => _manager.AttachedWheel?.Kind == PeripheralKind.Hub);
            this.AttachDelegate("FanaBridge.WheelDetected", () => _manager.AttachedWheel != null);
            this.AttachDelegate("FanaBridge.WheelCode", () => _manager.AttachedWheel?.Code ?? "");
            this.AttachDelegate("FanaBridge.WheelWireCode", () => (int)(_manager.AttachedWheel?.WireCode ?? 0));
            this.AttachDelegate("FanaBridge.ModuleType", () => _manager.AttachedModule?.Code ?? "");
            this.AttachDelegate("FanaBridge.Capabilities.ButtonLedCount", () => CurrentCapabilities.ButtonLedCount);
            this.AttachDelegate("FanaBridge.Capabilities.ButtonRgbCount", () => CurrentCapabilities.ButtonRgbCount);
            this.AttachDelegate("FanaBridge.Capabilities.ButtonAuxIntensityCount", () => CurrentCapabilities.ButtonAuxIntensityCount);
            this.AttachDelegate("FanaBridge.Capabilities.TotalLedCount", () => CurrentCapabilities.AllLedCount);
            this.AttachDelegate("FanaBridge.Capabilities.DisplayType", () => CurrentCapabilities.Display.ToString());

            // --- Events ---
            this.AddEvent("DeviceConnected");
            this.AddEvent("DeviceDisconnected");
            this.AddEvent("WheelChanged");

            SimHub.Logging.Current.Info(
                $"FanaBridge: Init complete, connected={_manager.IsConnected}");
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (!_manager.Update())
                return;

            if (!data.GameRunning || data.NewData == null)
                return;
        }

        public void End(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("FanaBridge: End");
            Instance = null;

            this.SaveCommonSettings("FanaBridgeSettings", Settings);

            if (_manager?.IsConnected == true)
            {
                try
                {
                    Display?.ClearDisplay();
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn($"FanaBridge: Cleanup error: {ex.Message}");
                }
            }

            _manager?.Dispose();
        }

        /// <summary>
        /// Forces a disconnect and immediate reconnect attempt. Called from UI.
        /// </summary>
        public void ForceReconnect()
        {
            // ForceReconnect fires Connected/Disconnected, which invoke StateChanged
            // via the event subscriptions set up in Init().
            _manager.ForceReconnect();
        }

        /// <summary>
        /// Re-evaluates capabilities against the current profile store. Call after a
        /// profile override change or a <see cref="WheelProfileStore.Reload"/> to pick
        /// up new profiles without a SimHub restart or a physical wheel change.
        /// </summary>
        public void RefreshCapabilities()
        {
            if (_manager?.AttachedWheel == null)
                return;

            ResolveCaps("RefreshCapabilities");

            // A profile-override change (a UI action, not a hardware rim swap) altered the
            // active caps, so the LED layout may differ — force the affected device's next
            // write to reach hardware. Scoped to the primary device today; becomes the
            // resolved device once DeviceInstances route to their own handle in P6.
            _manager?.PrimaryHandle?.ForceLedResend();
            RaiseIdentityChanged();
        }

        // Resolve capabilities for the attached wheel/hub(+module), respecting a user
        // override, and log the decode. The single place capabilities are resolved.
        private void ResolveCaps(string logContext)
        {
            var w = _manager?.AttachedWheel;
            if (w == null)
            {
                _currentCaps = WheelCapabilities.None;
                return;
            }

            var module = _manager.AttachedModule;
            _currentCaps = _resolver.Resolve(w.Code, module?.Code, out var overrideId);

            var snap = _manager.PrimarySnapshot;
            SimHub.Logging.Current.Info(string.Format(
                "FanaBridge: {0} — Base={1} (0x{2:X2}), Wheel={3} (wire 0x{4:X2}), Module={5} (0x{6:X2}), Override={7}, Caps={8}",
                logContext,
                snap.Code ?? "(unknown)",
                snap.BaseTypeByte,
                w.Code ?? "(unrecognized)",
                w.WireCode,
                module?.Code ?? "(none)",
                module?.WireCode ?? 0,
                overrideId ?? "(auto)",
                _currentCaps.Name ?? "(none)"));
        }

        // Re-broadcasts a settled identity change to SimHub + UI consumers. The LED
        // dirty-reset on a hardware rim change now lives on the committing carrier
        // (FanatecBaseDevice.Service), scoped to that one device, so this method only
        // raises events — it no longer force-resends a single shared encoder set.
        private void RaiseIdentityChanged()
        {
            this.TriggerEvent("WheelChanged");
            IdentityChanged?.Invoke();
            StateChanged?.Invoke();
        }

        /// <summary>
        /// Builds a read-only, GitHub-ready diagnostics snapshot of current device
        /// detection (HID interface inventory + decoded identity + raw FF 08 frame).
        /// Called from the settings UI's "Copy Debug Info" link. Sends nothing to the
        /// device — it only re-enumerates the bus and formats held state.
        /// </summary>
        public string BuildDiagnosticsReport()
        {
            return DiagnosticsReport.Build(Identity, IsDeviceConnected, StatusDetail, BuildIdentity.Full);
        }

        /// <summary>
        /// Persists the current <see cref="Settings"/> to SimHub's storage.
        /// Called from the settings UI when profile overrides change.
        /// </summary>
        public void SaveSettings()
        {
            this.SaveCommonSettings("FanaBridgeSettings", Settings);
        }

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }
    }
}
