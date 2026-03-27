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
    /// </summary>
    public class FanatecLedManager : LedsGenericManager<FanatecLedDriver>
    {
        private WheelCapabilities _caps;
        private readonly LedEncoder _leds;
        private readonly LegacyLedEncoder _legacyLeds;
        private readonly IDeviceTransport _transport;
        private FanatecLedDriver _driver;

        /// <summary>
        /// Parameterless constructor required by the <c>new()</c> constraint on
        /// <c>LedModuleSettings&lt;T&gt;</c>.  Not used at runtime — the
        /// <see cref="FanatecLedManager(WheelCapabilities, LedEncoder, LegacyLedEncoder, IDeviceTransport)"/>
        /// constructor is called explicitly and the instance is passed to LedModuleSettings.
        /// </summary>
        public FanatecLedManager()
        {
            _caps = WheelCapabilities.None;
        }

        /// <summary>
        /// Creates a manager configured for a specific wheel's LED layout.
        /// </summary>
        public FanatecLedManager(WheelCapabilities caps, LedEncoder leds, LegacyLedEncoder legacyLeds, IDeviceTransport transport)
        {
            _caps = caps ?? WheelCapabilities.None;
            _leds = leds;
            _legacyLeds = legacyLeds;
            _transport = transport;
        }

        // ── LedsGenericManager<T> overrides ──────────────────────────────

        /// <summary>
        /// Called by the base class when a connection is needed.
        /// Creates the unified BA63-compatible driver with the wheel's capabilities.
        /// </summary>
        public override FanatecLedDriver GetDriver()
        {
            _driver = new FanatecLedDriver(_caps, _leds, _legacyLeds, _transport);

            SimHub.Logging.Current.Info(
                "FanatecLedManager: Created driver for " + (_caps.Name ?? "unknown") +
                " (" + _caps.AllLedCount + " LEDs: rev=" + _caps.RevLedCount +
                ", flag=" + _caps.FlagLedCount + ", color=" + _caps.ColorLedCount +
                ", mono=" + _caps.MonoLedCount +
                ", legacyRev=" + _caps.LegacyRevLedCount +
                ", legacyRevRgb=" + _caps.LegacyRevRgbLedCount +
                ", legacyRevGlobal=" + _caps.LegacyRevGlobalLedCount +
                ", revStripe=" + _caps.RevStripeLedCount + ")");

            return _driver;
        }

        /// <summary>
        /// If the active profile changed, tears down the current driver so
        /// the base class recreates it via <see cref="GetDriver"/> with
        /// the new capabilities on the next frame.
        /// Safe to call every frame — no-ops when the profile hasn't changed.
        /// </summary>
        public void HotSwapIfNeeded(WheelCapabilities newCaps)
        {
            if (newCaps?.Profile == null || newCaps.Profile == _caps.Profile)
                return;

            SimHub.Logging.Current.Info(
                "FanatecLedManager: Swapping driver from '" +
                (_caps.Name ?? "?") + "' to '" + (newCaps.Name ?? "?") + "'");

            _caps = newCaps;
            Close();
        }

        // IsConnected() and GetPhysicalMapper() are sealed in the base class
        // and delegate to the driver automatically — no override needed.
    }
}
