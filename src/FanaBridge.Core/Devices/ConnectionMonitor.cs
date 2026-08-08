using System;

namespace FanaBridge.Devices
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
        private readonly IWheelbaseConnection _wheelbase;
        private readonly Func<bool> _tryConnect;
        private readonly Action<string> _logWarn;
        private readonly Action<string> _logInfo;

        private bool _connected;
        private int _frameCounter;
        private int _reconnectCooldown;

        // Set by ForceReconnect (any thread), consumed by Update (frame thread).
        private volatile bool _forceReconnectPending;

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

        /// <param name="wheelbase">The connected wheelbase (owns its transport; identity + polling).</param>
        /// <param name="tryConnect">
        /// Delegate that attempts to connect the wheelbase (and its transport).
        /// Returns true on success. The monitor does not own connection logic
        /// so the plugin can apply PID overrides and other settings.
        /// </param>
        /// <param name="logWarn">Optional warning logger (defaults to no-op).</param>
        /// <param name="logInfo">Optional info logger (defaults to no-op).</param>
        public ConnectionMonitor(
            IWheelbaseConnection wheelbase,
            Func<bool> tryConnect,
            Action<string> logWarn = null,
            Action<string> logInfo = null)
        {
            _wheelbase = wheelbase ?? throw new ArgumentNullException(nameof(wheelbase));
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

            // Apply a pending ForceReconnect HERE, on the frame thread, so all
            // connect/adopt/identity I/O stays single-threaded — running it on
            // the requester's (UI) thread would race this method's own identity
            // drain on the same buffers and streams.
            if (_forceReconnectPending)
            {
                _forceReconnectPending = false;
                if (_connected)
                {
                    _wheelbase.Disconnect();
                    _connected = false;
                    Disconnected?.Invoke();
                }
                _reconnectCooldown = 0;
            }

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
            // expensive, so do it less frequently than the stream check)
            if (_frameCounter % HID_BUS_CHECK_INTERVAL == 0)
            {
                if (!_wheelbase.IsDevicePresent)
                {
                    _logWarn("FanaBridge: Device no longer on HID bus");
                    LastDisconnectReason = "Device no longer on the HID bus (powered off or unplugged).";
                    _wheelbase.Disconnect();
                    _connected = false;
                    _reconnectCooldown = COOLDOWN_MEDIUM;
                    Disconnected?.Invoke();
                    return false;
                }
            }
            else if (_frameCounter % STREAM_CHECK_INTERVAL == 0)
            {
                if (!_wheelbase.IsConnected)
                {
                    _logWarn("FanaBridge: Wheelbase disconnected");
                    LastDisconnectReason = "Wheelbase HID stream closed.";
                    _wheelbase.Disconnect();
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
                _wheelbase.UpdateIdentity();
            }
            catch (Exception ex)
            {
                _logWarn(
                    $"FanaBridge: Identity update failed, triggering reconnect: {ex.Message}");
                LastDisconnectReason = "Identity update failed: " + ex.Message;
                _wheelbase.Disconnect();
                _connected = false;
                _reconnectCooldown = COOLDOWN_SHORT;
                Disconnected?.Invoke();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Requests a disconnect + immediate reconnect. Safe from any thread
        /// (e.g. the settings UI): the actual work runs on the next frame's
        /// <see cref="Update"/>, keeping all device I/O on the frame thread.
        /// Connected/Disconnected fire from Update as the reconnect proceeds.
        /// </summary>
        public void ForceReconnect()
        {
            _logInfo("FanaBridge: ForceReconnect requested");
            _forceReconnectPending = true;
        }
    }
}
