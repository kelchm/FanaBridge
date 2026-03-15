using System;

namespace FanaBridge.Core
{
    /// <summary>
    /// Encapsulates the Fanatec device connection state machine:
    /// connect/disconnect detection, periodic heartbeat checks,
    /// reconnect cooldowns, and wheel identity polling.
    ///
    /// Called once per frame from <c>FanatecPlugin.DataUpdate()</c>.
    /// Fires events that the plugin forwards to SimHub.
    /// </summary>
    public class ConnectionMonitor
    {
        private readonly ISdkConnection _sdk;
        private readonly IDeviceConnection _device;
        private readonly Func<bool> _tryConnect;
        private readonly Action<string> _logWarn;
        private readonly Action<string> _logInfo;

        private bool _connected;
        private int _frameCounter;
        private int _reconnectCooldown;
        private int _wheelPollCooldown;

        // ── Heartbeat intervals (in frames) ────────────────────────────
        private const int HID_BUS_CHECK_INTERVAL = 120;
        private const int STREAM_CHECK_INTERVAL = 60;
        private const int WHEEL_POLL_INTERVAL = 30;

        // ── Reconnect cooldowns (in frames) ────────────────────────────
        private const int COOLDOWN_LONG = 300;
        private const int COOLDOWN_MEDIUM = 120;
        private const int COOLDOWN_SHORT = 60;

        /// <summary>Whether the Fanatec device is currently connected.</summary>
        public bool IsConnected => _connected;

        /// <summary>Fired when a connection is established.</summary>
        public event Action Connected;

        /// <summary>Fired when the connection is lost.</summary>
        public event Action Disconnected;

        /// <param name="sdk">Shared SDK manager (wheel identity, SDK connection).</param>
        /// <param name="device">Shared HID device (LED/display I/O).</param>
        /// <param name="tryConnect">
        /// Delegate that attempts to connect both the SDK and HID layers.
        /// Returns true on success. The monitor does not own connection logic
        /// so the plugin can apply PID overrides and other settings.
        /// </param>
        /// <param name="logWarn">Optional warning logger (defaults to no-op).</param>
        /// <param name="logInfo">Optional info logger (defaults to no-op).</param>
        public ConnectionMonitor(
            ISdkConnection sdk,
            IDeviceConnection device,
            Func<bool> tryConnect,
            Action<string> logWarn = null,
            Action<string> logInfo = null)
        {
            _sdk = sdk ?? throw new ArgumentNullException(nameof(sdk));
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _tryConnect = tryConnect ?? throw new ArgumentNullException(nameof(tryConnect));
            _logWarn = logWarn ?? (_ => { });
            _logInfo = logInfo ?? (_ => { });
        }

        /// <summary>
        /// Attempts the initial connection. Call once during plugin init,
        /// before the frame loop starts.
        /// </summary>
        /// <returns>True if the initial connection succeeded.</returns>
        public bool TryInitialConnect()
        {
            _connected = _tryConnect();
            return _connected;
        }

        /// <summary>
        /// Called once per frame. Handles reconnect attempts, heartbeat
        /// checks, and wheel identity polling.
        /// </summary>
        /// <returns>
        /// True if the device is connected and the caller should proceed
        /// with telemetry processing; false if disconnected or recovering.
        /// </returns>
        public bool Update()
        {
            _frameCounter++;

            if (!_connected)
            {
                if (_reconnectCooldown > 0)
                {
                    _reconnectCooldown--;
                    return false;
                }

                _connected = _tryConnect();
                if (!_connected)
                {
                    _reconnectCooldown = COOLDOWN_LONG;
                    return false;
                }

                Connected?.Invoke();
                return true;
            }

            // Verify device is still alive periodically (HID bus check is more
            // expensive, so do it less frequently than the stream check)
            if (_frameCounter % HID_BUS_CHECK_INTERVAL == 0)
            {
                if (!_device.IsDevicePresent)
                {
                    _logWarn("FanaBridge: Device no longer on HID bus");
                    _device.Disconnect();
                    _sdk.Disconnect();
                    _connected = false;
                    _reconnectCooldown = COOLDOWN_MEDIUM;
                    Disconnected?.Invoke();
                    return false;
                }
            }
            else if (_frameCounter % STREAM_CHECK_INTERVAL == 0)
            {
                if (!_device.IsConnected || !_sdk.IsConnected)
                {
                    _logWarn("FanaBridge: Device or SDK disconnected");
                    _connected = false;
                    _reconnectCooldown = COOLDOWN_LONG;
                    Disconnected?.Invoke();
                    return false;
                }
            }

            // Poll wheel identity (~every 0.5 s at 60 fps)
            if (_wheelPollCooldown <= 0)
            {
                try
                {
                    _sdk.PollWheelIdentity();
                }
                catch (Exception ex)
                {
                    _logWarn(
                        $"FanaBridge: SDK poll failed, triggering reconnect: {ex.Message}");
                    _connected = false;
                    _reconnectCooldown = COOLDOWN_SHORT;
                    Disconnected?.Invoke();
                    return false;
                }
                _wheelPollCooldown = WHEEL_POLL_INTERVAL;
            }
            else
            {
                _wheelPollCooldown--;
            }

            return true;
        }

        /// <summary>
        /// Forces a disconnect and immediate reconnect attempt.
        /// </summary>
        public void ForceReconnect()
        {
            _logInfo("FanaBridge: ForceReconnect requested");

            if (_connected)
            {
                _device.Disconnect();
                _sdk.Disconnect();
                _connected = false;
            }

            _reconnectCooldown = 0;
            _connected = _tryConnect();

            if (_connected)
                Connected?.Invoke();
            else
                Disconnected?.Invoke();
        }
    }
}
