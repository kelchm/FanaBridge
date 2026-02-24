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

        private bool _connected;
        private int _frameCounter;
        private int _reconnectCooldown;
        private int _wheelPollCooldown;

        /// <summary>Fired when connection status or wheel identity changes. May fire from any thread.</summary>
        public event Action StateChanged;

        /// <summary>Whether the Fanatec device is currently connected (for UI binding).</summary>
        public bool IsDeviceConnected => _connected;

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
            _frameCounter = 0;
            _reconnectCooldown = 0;
            _wheelPollCooldown = 0;

            // Attempt initial connection
            _connected = TryConnect();

            // --- Properties ---
            this.AttachDelegate("FanaBridge.Connected", () => _connected);
            this.AttachDelegate("FanaBridge.DeviceName", () => _sdk.ProductName ?? "Not connected");
            this.AttachDelegate("FanaBridge.WheelName", () => _sdk.WheelDisplayName);
            this.AttachDelegate("FanaBridge.WheelDetected", () => _sdk.WheelDetected);
            this.AttachDelegate("FanaBridge.WheelType", () => (int)_sdk.SteeringWheelType);
            this.AttachDelegate("FanaBridge.ModuleType", () => (int)_sdk.SubModuleType);
            this.AttachDelegate("FanaBridge.Capabilities.ButtonLedCount", () => _sdk.CurrentCapabilities.ButtonLedCount);
            this.AttachDelegate("FanaBridge.Capabilities.EncoderLedCount", () => _sdk.CurrentCapabilities.EncoderLedCount);
            this.AttachDelegate("FanaBridge.Capabilities.TotalLedCount", () => _sdk.CurrentCapabilities.TotalLedCount);
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
                _device.ForceDirty();

                this.TriggerEvent("WheelChanged");
                StateChanged?.Invoke();
            };

            SimHub.Logging.Current.Info($"FanaBridge: Init complete, connected={_connected}");
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            _frameCounter++;

            if (!_connected)
            {
                if (_reconnectCooldown > 0)
                {
                    _reconnectCooldown--;
                    return;
                }

                _connected = TryConnect();
                if (!_connected)
                {
                    _reconnectCooldown = 300;
                    return;
                }

                this.TriggerEvent("DeviceConnected");
                StateChanged?.Invoke();
            }

            // Verify device is still alive periodically (HID bus check is more
            // expensive, so do it less frequently than the stream check)
            if (_frameCounter % 120 == 0)
            {
                if (!_device.IsDevicePresent)
                {
                    SimHub.Logging.Current.Warn("FanaBridge: Device no longer on HID bus");
                    _device.Disconnect();
                    _sdk.Disconnect();
                    _connected = false;
                    _reconnectCooldown = 120;
                    this.TriggerEvent("DeviceDisconnected");
                    StateChanged?.Invoke();
                    return;
                }
            }
            else if (_frameCounter % 60 == 0)
            {
                if (!_device.IsConnected || !_sdk.IsConnected)
                {
                    SimHub.Logging.Current.Warn("FanaBridge: Device or SDK disconnected");
                    _connected = false;
                    _reconnectCooldown = 300;
                    this.TriggerEvent("DeviceDisconnected");
                    StateChanged?.Invoke();
                    return;
                }
            }

            // Poll wheel identity (~every 2 seconds)
            if (_wheelPollCooldown <= 0)
            {
                try
                {
                    _sdk.PollWheelIdentity();
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn($"FanaBridge: SDK poll failed, triggering reconnect: {ex.Message}");
                    _connected = false;
                    _reconnectCooldown = 60; // shorter cooldown — device may still be present
                    this.TriggerEvent("DeviceDisconnected");
                    StateChanged?.Invoke();
                    return;
                }
                _wheelPollCooldown = 30;  // ~0.5 s at 60 fps
            }
            else
            {
                _wheelPollCooldown--;
            }

            if (!data.GameRunning || data.NewData == null)
                return;
        }

        public void End(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("FanaBridge: End");
            Instance = null;

            this.SaveCommonSettings("FanaBridgeSettings", Settings);

            if (_connected)
            {
                try
                {
                    _device.ClearDisplay();
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn($"FanaBridge: Cleanup error: {ex.Message}");
                }
            }

            _sdk?.Dispose();
            _device?.Disconnect();
        }

        /// <summary>
        /// Forces a disconnect and immediate reconnect attempt. Called from UI.
        /// </summary>
        public void ForceReconnect()
        {
            SimHub.Logging.Current.Info("FanaBridge: ForceReconnect requested");

            if (_connected)
            {
                _device?.Disconnect();
                _sdk?.Disconnect();
                _connected = false;
                StateChanged?.Invoke();
            }

            _reconnectCooldown = 0;
            _connected = TryConnect();
            StateChanged?.Invoke();
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
