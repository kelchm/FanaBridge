using System;
using FanaBridge.Devices;

namespace FanaBridge.Transport
{
    /// <summary>
    /// Encapsulates the Fanatec device connection state machine:
    /// connect/disconnect detection, periodic heartbeat checks,
    /// reconnect cooldowns, and identity servicing.
    ///
    /// Called once per frame from <c>FanatecPlugin.DataUpdate()</c>.
    /// Fires events that the plugin forwards to SimHub.
    /// </summary>
    public class ConnectionMonitor
    {
        private readonly IServiceableDevice _device;
        private readonly Func<bool> _tryConnect;
        private readonly Action<string> _logWarn;
        private readonly Action<string> _logInfo;

        private bool _connected;
        private int _frameCounter;
        private int _reconnectCooldown;

        // Set by NotifyBusChanged while connected, to force a presence check on the
        // next Update regardless of the heartbeat interval (event-driven removal).
        private bool _forceBusCheck;

        // ── Heartbeat intervals (in frames) ────────────────────────────
        private const int HID_BUS_CHECK_INTERVAL = 120;
        private const int STREAM_CHECK_INTERVAL = 60;

        // ── Reconnect cooldowns (in frames) ────────────────────────────
        private const int COOLDOWN_LONG = 300;
        private const int COOLDOWN_MEDIUM = 120;
        private const int COOLDOWN_SHORT = 60;

        /// <summary>Whether the Fanatec device is currently connected.</summary>
        public bool IsConnected => _connected;

        /// <summary>
        /// Human-readable reason for the most recent runtime disconnect (HID-bus
        /// loss, stream drop, identity error), or null while connected. Surfaced
        /// so a dropped connection is diagnosable without opening the SimHub log.
        /// </summary>
        public string LastDisconnectReason { get; private set; }

        /// <summary>Fired when a connection is established.</summary>
        public event Action Connected;

        /// <summary>Fired when the connection is lost.</summary>
        public event Action Disconnected;

        /// <param name="device">The serviceable device (owns its transport; identity servicing).</param>
        /// <param name="tryConnect">
        /// Delegate that attempts to connect the device (and its transport).
        /// Returns true on success. The monitor does not own connection logic
        /// so the plugin can apply PID overrides and other settings.
        /// </param>
        /// <param name="logWarn">Optional warning logger (defaults to no-op).</param>
        /// <param name="logInfo">Optional info logger (defaults to no-op).</param>
        public ConnectionMonitor(
            IServiceableDevice device,
            Func<bool> tryConnect,
            Action<string> logWarn = null,
            Action<string> logInfo = null)
        {
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

                LastDisconnectReason = null;
                Connected?.Invoke();
                return true;
            }

            // Verify device is still alive periodically (HID bus check is more
            // expensive, so do it less frequently than the stream check). A bus-change
            // notification forces the check this frame so a removal is noticed at once
            // rather than up to one interval later.
            bool doBusCheck = _forceBusCheck || (_frameCounter % HID_BUS_CHECK_INTERVAL == 0);
            _forceBusCheck = false;
            if (doBusCheck)
            {
                if (!_device.IsDevicePresent)
                {
                    _logWarn("FanaBridge: Device no longer on HID bus");
                    LastDisconnectReason = "Device no longer on the HID bus (powered off or unplugged).";
                    _device.Disconnect();
                    _connected = false;
                    _reconnectCooldown = COOLDOWN_MEDIUM;
                    Disconnected?.Invoke();
                    return false;
                }
            }
            else if (_frameCounter % STREAM_CHECK_INTERVAL == 0)
            {
                if (!_device.IsConnected)
                {
                    _logWarn("FanaBridge: Wheelbase disconnected");
                    LastDisconnectReason = "Wheelbase HID stream closed.";
                    _device.Disconnect();
                    _connected = false;
                    _reconnectCooldown = COOLDOWN_LONG;
                    Disconnected?.Invoke();
                    return false;
                }
            }

            // Service identity every frame: drain pushed FF 08 reports and advance
            // the settle timer. The base pushes on change, so this is cheap (a
            // non-blocking drain) and only commits once a change has settled.
            try
            {
                _device.Service();
            }
            catch (Exception ex)
            {
                _logWarn(
                    $"FanaBridge: Identity update failed, triggering reconnect: {ex.Message}");
                LastDisconnectReason = "Identity update failed: " + ex.Message;
                _device.Disconnect();
                _connected = false;
                _reconnectCooldown = COOLDOWN_SHORT;
                Disconnected?.Invoke();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Signals that the HID bus topology changed (a device arrived or left), so the
        /// next <see cref="Update"/> acts immediately instead of waiting for the poll
        /// interval: retry the connection now if disconnected, or re-check device
        /// presence now if connected. Called from the device manager's debounced
        /// DeviceList.Changed handler (already marshalled to the frame thread), so it
        /// only flips state — it performs no I/O itself.
        /// </summary>
        public void NotifyBusChanged()
        {
            if (_connected)
                _forceBusCheck = true;   // re-check presence next Update (catch a removal at once)
            else
                _reconnectCooldown = 0;  // retry the connection next Update (catch an arrival at once)
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
                _connected = false;
            }

            _reconnectCooldown = 0;
            _connected = _tryConnect();

            if (_connected)
            {
                LastDisconnectReason = null;
                Connected?.Invoke();
            }
            else
            {
                Disconnected?.Invoke();
            }
        }
    }
}
