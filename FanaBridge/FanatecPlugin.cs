using FanaBridge.Core;
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
        /// hardware without owning their own SDK/HID connections.
        /// Set during Init(), cleared during End().
        /// </summary>
        public static FanatecPlugin Instance { get; private set; }

        public FanatecPluginSettings Settings { get; set; }

        private FanatecSdkManager _sdk;
        private FanatecDevice _device;
        private ConnectionMonitor _connectionMonitor;
        private FanatecTuningController _tuning;
        private LedEncoder _leds;
        private DisplayEncoder _display;

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

        /// <summary>Name of the connected device (for UI binding).</summary>
        public string DeviceName => _sdk?.ProductName ?? "Not connected";

        /// <summary>Name of the currently detected steering wheel.</summary>
        public string WheelName => _sdk?.WheelDisplayName ?? "Unknown";

        /// <summary>Current wheel capabilities (for UI binding).</summary>
        public WheelCapabilities CurrentCapabilities => _sdk?.CurrentCapabilities ?? WheelCapabilities.None;

        /// <summary>Shared SDK manager — used by DeviceInstance wrappers to query wheel identity.</summary>
        public FanatecSdkManager SdkManager => _sdk;

        /// <summary>Shared HID device — used by DeviceInstance wrappers for hardware I/O.</summary>
        public FanatecDevice Device => _device;

        /// <summary>Shared LED encoder — used by DeviceInstance LED drivers and wizard.</summary>
        public LedEncoder Leds => _leds;

        /// <summary>Shared display encoder — used by DeviceInstance display managers and wizard.</summary>
        public DisplayEncoder Display => _display;

        /// <summary>Shared tuning controller — used by TuningSettingsPanel for encoder config.</summary>
        public FanatecTuningController Tuning => _tuning;

        public PluginManager PluginManager { get; set; }

        public ImageSource PictureIcon => new BitmapImage(new Uri(
            "pack://application:,,,/FanaBridge;component/Resources/Images/plugin-icon.png"));

        public string LeftMenuTitle => "FanaBridge";

        public void Init(PluginManager pluginManager)
        {
            Instance = this;
            SimHub.Logging.Current.Info("FanaBridge: Init starting");

            Settings = this.ReadCommonSettings<FanatecPluginSettings>(
                "FanaBridgeSettings",
                () => new FanatecPluginSettings());

            _sdk = new FanatecSdkManager();
            _device = new FanatecDevice();
            _leds = new LedEncoder(_device);
            _display = new DisplayEncoder(_device);
            _tuning = new FanatecTuningController(
                _device,
                msg => SimHub.Logging.Current.Warn(msg),
                msg => SimHub.Logging.Current.Info(msg));

            // Wire up profile override resolution from plugin settings
            _sdk.ProfileOverrideResolver = (matchKey) =>
            {
                if (Settings.ProfileOverrides != null
                    && Settings.ProfileOverrides.TryGetValue(matchKey, out var overrideId))
                    return overrideId;
                return null;
            };

            _connectionMonitor = new ConnectionMonitor(
                _sdk, _device, TryConnect,
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

            // Attempt initial connection
            _connectionMonitor.TryInitialConnect();

            // --- Properties ---
            this.AttachDelegate("FanaBridge.Connected", () => _connectionMonitor.IsConnected);
            this.AttachDelegate("FanaBridge.DeviceName", () => _sdk.ProductName ?? "Not connected");
            this.AttachDelegate("FanaBridge.WheelName", () => _sdk.WheelDisplayName);
            this.AttachDelegate("FanaBridge.WheelDetected", () => _sdk.WheelDetected);
            this.AttachDelegate("FanaBridge.WheelType", () => (int)_sdk.SteeringWheelType);
            this.AttachDelegate("FanaBridge.ModuleType", () => (int)_sdk.SubModuleType);
            this.AttachDelegate("FanaBridge.Capabilities.ButtonLedCount", () => _sdk.CurrentCapabilities.ButtonLedCount);
            this.AttachDelegate("FanaBridge.Capabilities.ColorLedCount", () => _sdk.CurrentCapabilities.ColorLedCount);
            this.AttachDelegate("FanaBridge.Capabilities.MonoLedCount", () => _sdk.CurrentCapabilities.MonoLedCount);
            this.AttachDelegate("FanaBridge.Capabilities.TotalLedCount", () => _sdk.CurrentCapabilities.AllLedCount);
            this.AttachDelegate("FanaBridge.Capabilities.DisplayType", () => _sdk.CurrentCapabilities.Display.ToString());

            // --- Events ---
            this.AddEvent("DeviceConnected");
            this.AddEvent("DeviceDisconnected");
            this.AddEvent("WheelChanged");

            _sdk.WheelChanged += (manager) =>
            {
                SimHub.Logging.Current.Info("FanaBridge: Wheel changed to " + manager.WheelDisplayName);

                // The physical rim just changed — firmware resets LED state
                // but our dirty-tracking arrays still hold the old instance's
                // last output.  Force a full resend on the next frame so the
                // new DeviceInstance's first write always reaches hardware.
                _leds.ForceDirty();

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

            _sdk?.Dispose();
            _device?.Dispose();
        }

        /// <summary>
        /// Forces a disconnect and immediate reconnect attempt. Called from UI.
        /// </summary>
        public void ForceReconnect()
        {
            _connectionMonitor.ForceReconnect();
            StateChanged?.Invoke();
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
                bool sdkConnected;
                if (Settings.ProductIdOverride != 0)
                {
                    SimHub.Logging.Current.Info($"FanaBridge: Using PID override 0x{Settings.ProductIdOverride:X4}");
                    sdkConnected = _sdk.Connect(Settings.ProductIdOverride);
                }
                else
                {
                    sdkConnected = _sdk.AutoConnect();
                }

                if (!sdkConnected)
                    return false;

                bool hidConnected = _device.Connect(_sdk.ConnectedProductId);
                if (!hidConnected)
                {
                    SimHub.Logging.Current.Warn("FanaBridge: SDK connected but HID open failed");
                    _sdk.Disconnect();
                    return false;
                }

                SimHub.Logging.Current.Info($"FanaBridge: Connected to {_sdk.ProductName} (PID 0x{_sdk.ConnectedProductId:X4})");
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
