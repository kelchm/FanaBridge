using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Bridges <see cref="FanatecLedDriver"/> into SimHub's
    /// <c>ILedDeviceManager</c> pipeline via <c>LedsGenericManager&lt;T&gt;</c>.
    ///
    /// Each <see cref="FanatecWheelDeviceInstance"/> creates one of these with
    /// the appropriate <see cref="WheelCapabilities"/> and passes it to
    /// <c>LedModuleSettings&lt;FanatecLedManager&gt;</c> as a pre-created driver
    /// (via <c>LedModuleOptions.LedDriver</c> or the constructor overload).
    ///
    /// The <c>LedsGenericManager</c> base class handles:
    ///   • Building <c>LedDeviceState</c> from per-group <c>Func&lt;Color[]&gt;</c>
    ///   • Routing through <c>PhysicalMapper</c> to <c>SendLeds()</c>
    ///   • Connection/reconnection lifecycle and events
    ///   • Force-refresh timers
    ///
    /// The LED module is built once at startup from whichever profile is
    /// active at that time (built-in default, or user override if set).
    /// The driver is rebuilt live when the active profile changes — see
    /// <see cref="HotSwapIfNeeded"/>.  If the new profile has a different
    /// LED count, the module's slot count is stale until SimHub restarts.
    /// </summary>
    public class FanatecLedManager : LedsGenericManager<FanatecLedDriver>
    {
        private readonly LedEncoder _leds;
        private readonly LegacyLedEncoder _legacyLeds;
        private readonly IDeviceTransport _transport;

        // Track which profile the current driver was built from,
        // so HotSwapIfNeeded can detect changes.
        private WheelProfile _lastDriverProfile;

        /// <summary>
        /// Parameterless constructor required by the <c>new()</c> constraint on
        /// <c>LedModuleSettings&lt;T&gt;</c>.  Not used at runtime — the
        /// <see cref="FanatecLedManager(WheelCapabilities, LedEncoder, LegacyLedEncoder, IDeviceTransport)"/>
        /// constructor is called explicitly and the instance is passed to LedModuleSettings.
        /// </summary>
        public FanatecLedManager()
        {
        }

        /// <summary>
        /// Creates a manager configured for a specific wheel's LED layout.
        /// The <paramref name="caps"/> determine the LED module's slot capacity
        /// (set once at startup from whichever profile is active at that time).
        /// The driver built by <see cref="GetDriver"/> may use different caps
        /// if the user switches profiles at runtime.
        /// </summary>
        public FanatecLedManager(WheelCapabilities caps, LedEncoder leds, LegacyLedEncoder legacyLeds, IDeviceTransport transport)
        {
            _leds = leds;
            _legacyLeds = legacyLeds;
            _transport = transport;
        }

        // ── LedsGenericManager<T> overrides ──────────────────────────────

        /// <summary>
        /// Called by the base class when a connection is needed.
        /// Reads the currently-active capabilities from the plugin singleton
        /// (respecting any user profile override) and builds a driver for them.
        /// </summary>
        public override FanatecLedDriver GetDriver()
        {
            // Use the runtime-resolved profile, not the static registration caps
            var caps = FanatecPlugin.Instance?.CurrentCapabilities ?? WheelCapabilities.None;
            _lastDriverProfile = caps.Profile;

            var driver = new FanatecLedDriver(caps, _leds, _legacyLeds, _transport);

            SimHub.Logging.Current.Info(
                "FanatecLedManager: Created driver for " + (caps.Name ?? "unknown") +
                " (" + caps.AllLedCount + " LEDs: rev=" + caps.RevLedCount +
                ", flag=" + caps.FlagLedCount + ", color=" + caps.ColorLedCount +
                ", mono=" + caps.MonoLedCount +
                ", legacyRev=" + caps.LegacyRevLedCount +
                ", legacyRevRgb=" + caps.LegacyRevRgbLedCount +
                ", legacyRevGlobal=" + caps.LegacyRevGlobalLedCount +
                ", revStripe=" + caps.RevStripeLedCount + ")");

            return driver;
        }

        /// <summary>
        /// If the active profile changed, tears down the current driver so
        /// the base class recreates it via <see cref="GetDriver"/> on the
        /// next frame.  Safe to call every frame — no-ops when unchanged.
        /// </summary>
        public void HotSwapIfNeeded(WheelCapabilities currentCaps)
        {
            if (currentCaps?.Profile == null || currentCaps.Profile == _lastDriverProfile)
                return;

            SimHub.Logging.Current.Info(
                "FanatecLedManager: Active profile changed to '" +
                (currentCaps.Name ?? "?") + "' — triggering driver rebuild");

            Close();
            // _lastDriverProfile will be updated in the next GetDriver() call
        }

        // IsConnected() and GetPhysicalMapper() are sealed in the base class
        // and delegate to the driver automatically — no override needed.
    }
}
