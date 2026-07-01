using FanaBridge.Adapters;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using FanaBridge.UI;
using GameReaderCommon;
using SimHub.Plugins;
using System;
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

        private FanatecWheelbase _wheelbase;
        private ConnectionMonitor _connectionMonitor;
        private FanatecTuningController _tuning;
        private LedEncoder _leds;
        private LegacyLedEncoder _legacyLeds;
        private DisplayEncoder _display;
        private ItmEncoder _itm;

        /// <summary>Fired when connection status or wheel identity changes. May fire from any thread.</summary>
        public event Action StateChanged;

        /// <summary>
        /// When true, device instances skip all LED and display output so the
        /// profile wizard can send probe signals without being overwritten by
        /// SimHub's frame-by-frame updates.  Set by the wizard dialog.
        /// </summary>
        public bool WizardActive { get; set; }

        /// <summary>Whether the Fanatec device is currently connected (for UI binding).</summary>
        public bool IsDeviceConnected => _connectionMonitor?.IsConnected == true;

        /// <summary>
        /// When disconnected, a short reason for it (the latest connect-attempt
        /// failure, else the latest runtime disconnect reason); null while
        /// connected. Surfaced on the Status row and in the diagnostics capture.
        /// </summary>
        public string StatusDetail =>
            IsDeviceConnected
                ? null
                : (_wheelbase?.LastConnectError ?? _connectionMonitor?.LastDisconnectReason);

        /// <summary>Name of the connected device (for UI binding).</summary>
        public string DeviceName => _wheelbase?.ProductName ?? "Not connected";

        /// <summary>Name of the currently detected steering wheel.</summary>
        public string WheelName => _wheelbase?.DisplayName ?? "Unknown";

        /// <summary>Current wheel capabilities (for UI binding).</summary>
        public WheelCapabilities CurrentCapabilities => _wheelbase?.CurrentCapabilities ?? WheelCapabilities.None;

        /// <summary>
        /// Resolves the capabilities a specific device descriptor should use.
        /// Returns the live, currently-active capabilities (which respect any
        /// user override) only when the connected wheel actually matches this
        /// <paramref name="config"/>; otherwise returns the config's own
        /// registration capabilities. <see cref="CurrentCapabilities"/> is
        /// global, so unrelated device instances must NOT consume it directly —
        /// this is the single guard that prevents the connected wheel's caps
        /// from leaking into every descriptor.
        /// </summary>
        public WheelCapabilities ResolveCapsFor(DeviceConfig config)
        {
            if (config == null)
                return WheelCapabilities.None;

            var wheelbase = _wheelbase;
            var current = CurrentCapabilities;
            if (current?.Profile != null && wheelbase != null
                && config.MatchesAttachment(
                    wheelbase.WheelDetected, wheelbase.WheelCode, wheelbase.ModuleCode))
            {
                return current;
            }

            return config.Capabilities ?? WheelCapabilities.None;
        }

        /// <summary>The connected wheelbase — used by DeviceInstance wrappers to query wheel identity.</summary>
        // ARCHITECTURE: single-device assumption. When multi-device support lands
        // (pedals/shifter/SRM), this becomes a DeviceManager owning a collection;
        // don't add callers that bake in "exactly one base". See the device-
        // architecture direction note (Connection / IIdentitySource / Device).
        public FanatecWheelbase Wheelbase => _wheelbase;

        /// <summary>Shared HID transport — used by DeviceInstance wrappers for hardware I/O.</summary>
        public IDeviceTransport Transport => _wheelbase?.Transport;

        /// <summary>Shared LED encoder (col03) — used by DeviceInstance LED drivers and wizard.</summary>
        public LedEncoder Leds => _leds;

        /// <summary>Shared legacy LED encoder (col01) — used by DeviceInstance LED drivers for legacy/RevStripe wheels.</summary>
        public LegacyLedEncoder LegacyLeds => _legacyLeds;

        /// <summary>Shared display encoder — used by DeviceInstance display managers and wizard.</summary>
        public DisplayEncoder Display => _display;

        /// <summary>Shared ITM encoder (col03) — used by DeviceInstance ITM display drivers.</summary>
        public ItmEncoder Itm => _itm;

        /// <summary>Shared tuning controller — used by TuningSettingsPanel for encoder config.</summary>
        public FanatecTuningController Tuning => _tuning;

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

            // The wheelbase owns the HID transport and reads the FF 08 identity
            // report through it (no SimHub.FanatecManaged.dll). Encoders share
            // that same transport for hardware I/O.
            _wheelbase = new FanatecWheelbase();
            var transport = _wheelbase.Transport;
            _leds = new LedEncoder(transport);
            _legacyLeds = new LegacyLedEncoder(transport);
            _display = new DisplayEncoder(transport);
            _itm = new ItmEncoder(transport);
            _tuning = new FanatecTuningController(
                transport,
                msg => SimHub.Logging.Current.Warn(msg),
                msg => SimHub.Logging.Current.Info(msg));

            // Wire up profile override resolution from plugin settings
            _wheelbase.ProfileOverrideResolver = (matchKey) =>
            {
                if (string.IsNullOrEmpty(matchKey))
                    return null;
                if (Settings.ProfileOverrides != null
                    && Settings.ProfileOverrides.TryGetValue(matchKey, out var overrideId))
                    return overrideId;
                return null;
            };

            _connectionMonitor = new ConnectionMonitor(
                _wheelbase, TryConnect,
                msg => SimHub.Logging.Current.Warn(msg),
                msg => SimHub.Logging.Current.Info(msg));

            _connectionMonitor.Connected += () =>
            {
                this.TriggerEvent("DeviceConnected");
                StateChanged?.Invoke();
            };

            _connectionMonitor.Disconnected += () =>
            {
                this.TriggerEvent("DeviceDisconnected");
                StateChanged?.Invoke();
            };

            // Publish the singleton only now that every shared field is
            // constructed. DeviceInstance wrappers reach back through
            // Instance.Wheelbase / Instance.Transport, so exposing it earlier
            // would let them observe a half-built plugin (null Wheelbase).
            Instance = this;

            // Attempt initial connection
            _connectionMonitor.TryInitialConnect();

            // --- Properties ---
            this.AttachDelegate("FanaBridge.Connected", () => _connectionMonitor.IsConnected);
            this.AttachDelegate("FanaBridge.DeviceName", () => _wheelbase.ProductName ?? "Not connected");
            this.AttachDelegate("FanaBridge.BaseName", () =>
                _wheelbase.IsConnected ? (_wheelbase.BaseCode ?? "Unknown") : "Not connected");
            this.AttachDelegate("FanaBridge.WheelName", () => _wheelbase.DisplayName);
            this.AttachDelegate("FanaBridge.ModuleName", () => _wheelbase.ModuleCode ?? "None");
            this.AttachDelegate("FanaBridge.DisplayName", () => _wheelbase.DisplayName);
            this.AttachDelegate("FanaBridge.IsHub", () => _wheelbase.IsHub);
            this.AttachDelegate("FanaBridge.WheelDetected", () => _wheelbase.WheelDetected);
            this.AttachDelegate("FanaBridge.WheelCode", () => _wheelbase.WheelCode ?? "");
            this.AttachDelegate("FanaBridge.WheelWireCode", () => (int)_wheelbase.WheelWireCode);
            this.AttachDelegate("FanaBridge.ModuleType", () => _wheelbase.ModuleCode ?? "");
            this.AttachDelegate("FanaBridge.Capabilities.ButtonLedCount", () => _wheelbase.CurrentCapabilities.ButtonLedCount);
            this.AttachDelegate("FanaBridge.Capabilities.ButtonRgbCount", () => _wheelbase.CurrentCapabilities.ButtonRgbCount);
            this.AttachDelegate("FanaBridge.Capabilities.ButtonAuxIntensityCount", () => _wheelbase.CurrentCapabilities.ButtonAuxIntensityCount);
            this.AttachDelegate("FanaBridge.Capabilities.TotalLedCount", () => _wheelbase.CurrentCapabilities.AllLedCount);
            this.AttachDelegate("FanaBridge.Capabilities.DisplayType", () => _wheelbase.CurrentCapabilities.Display.ToString());

            // --- Events ---
            this.AddEvent("DeviceConnected");
            this.AddEvent("DeviceDisconnected");
            this.AddEvent("WheelChanged");

            _wheelbase.WheelChanged += (manager) =>
            {
                SimHub.Logging.Current.Info("FanaBridge: Wheel changed to " + manager.DisplayName);

                // The physical rim just changed — firmware resets LED state
                // but our dirty-tracking arrays still hold the old instance's
                // last output.  Force a full resend on the next frame so the
                // new DeviceInstance's first write always reaches hardware.
                _leds.ForceDirty();
                _legacyLeds.ForceDirty();

                this.TriggerEvent("WheelChanged");
                StateChanged?.Invoke();
            };

            SimHub.Logging.Current.Info(
                $"FanaBridge: Init complete, connected={_connectionMonitor.IsConnected}");
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (!_connectionMonitor.Update())
                return;

            if (!data.GameRunning || data.NewData == null)
                return;
        }

        public void End(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("FanaBridge: End");
            Instance = null;

            this.SaveCommonSettings("FanaBridgeSettings", Settings);

            if (_connectionMonitor?.IsConnected == true)
            {
                try
                {
                    _display.ClearDisplay();
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn($"FanaBridge: Cleanup error: {ex.Message}");
                }
            }

            _wheelbase?.Dispose();
        }

        /// <summary>
        /// Forces a disconnect and immediate reconnect attempt. Called from UI.
        /// </summary>
        public void ForceReconnect()
        {
            // ConnectionMonitor.ForceReconnect() already fires Connected/Disconnected,
            // which invoke StateChanged via the event subscriptions set up in Init().
            _connectionMonitor.ForceReconnect();
        }

        /// <summary>
        /// Builds a read-only, GitHub-ready diagnostics snapshot of current device
        /// detection (HID interface inventory + decoded identity + raw FF 08 frame).
        /// Called from the settings UI's "Copy Debug Info" link. Sends nothing
        /// to the device — it only re-enumerates the bus and formats held state.
        /// </summary>
        public string BuildDiagnosticsReport()
        {
            return DiagnosticsReport.Build(_wheelbase, IsDeviceConnected, StatusDetail, BuildIdentity.Full);
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

        private bool TryConnect()
        {
            try
            {
                bool connected;
                if (Settings.ProductIdOverride != 0)
                {
                    SimHub.Logging.Current.Info($"FanaBridge: Using PID override 0x{Settings.ProductIdOverride:X4}");
                    connected = _wheelbase.Connect(Settings.ProductIdOverride);
                }
                else
                {
                    connected = _wheelbase.AutoConnect();
                }

                if (!connected)
                    return false;

                SimHub.Logging.Current.Info($"FanaBridge: Connected to {_wheelbase.ProductName} (PID 0x{_wheelbase.ConnectedProductId:X4})");
                return true;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"FanaBridge: Connection failed: {ex.Message}");
                return false;
            }
        }
    }
}
