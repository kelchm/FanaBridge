using FanaBridge.Profiles;
using FanaBridge.Protocol;
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
        private readonly DeviceConfig _config;

        // Last-known encoders — refreshed from FanatecPlugin.Instance on every
        // driver build so a rebuilt driver always targets the live hardware
        // core, even if the plugin was replaced since construction (issue #37).
        // The constructor values only seed the fallback for the (unexpected)
        // case where Instance is null at build time.
        private LedEncoder _leds;
        private LegacyLedEncoder _legacyLeds;

        // Track which profile the current driver was built from,
        // so HotSwapIfNeeded can detect changes.
        private WheelProfile _lastDriverProfile;

        /// <summary>
        /// Parameterless constructor required by the <c>new()</c> constraint on
        /// <c>LedModuleSettings&lt;T&gt;</c>.  Not used at runtime — the
        /// <see cref="FanatecLedManager(DeviceConfig, LedEncoder, LegacyLedEncoder)"/>
        /// constructor is called explicitly and the instance is passed to LedModuleSettings.
        /// </summary>
        public FanatecLedManager()
        {
        }

        /// <summary>
        /// Creates a manager bound to a specific device descriptor. The driver
        /// is (re)built by <see cref="GetDriver"/> from the caps that
        /// <see cref="FanatecPlugin.ResolveCapsFor"/> resolves for this
        /// <paramref name="config"/> — live caps when this descriptor is the
        /// connected wheel, otherwise its registration caps.
        /// </summary>
        public FanatecLedManager(DeviceConfig config, LedEncoder leds, LegacyLedEncoder legacyLeds)
        {
            _config = config;
            _leds = leds;
            _legacyLeds = legacyLeds;
        }

        // ── LedsGenericManager<T> overrides ──────────────────────────────

        /// <summary>
        /// Called by the base class when a connection is needed. Builds a driver
        /// from the capabilities resolved for THIS descriptor (respecting any
        /// user profile override) — never the raw global caps, which would build
        /// a driver for whatever wheel is currently connected.
        /// </summary>
        public override FanatecLedDriver GetDriver()
        {
            var plugin = FanatecPlugin.Instance;
            if (plugin != null)
            {
                // Re-resolve the encoders so a rebuilt driver binds to the live
                // hardware core rather than whichever generation constructed us.
                _leds = plugin.Leds ?? _leds;
                _legacyLeds = plugin.LegacyLeds ?? _legacyLeds;
            }

            var caps = plugin?.ResolveCapsFor(_config) ?? WheelCapabilities.None;
            _lastDriverProfile = caps.Profile;

            var driver = new FanatecLedDriver(caps, _leds, _legacyLeds);

            SimHub.Logging.Current.Info(
                "FanatecLedManager: Created driver for " + (caps.Name ?? "unknown") +
                " (" + caps.AllLedCount + " LEDs: revRgb=" + caps.RevRgbCount +
                ", flagRgb=" + caps.FlagRgbCount + ", buttonRgb=" + caps.ButtonRgbCount +
                ", buttonAuxIntensity=" + caps.ButtonAuxIntensityCount +
                ", legacyRevOnOff=" + caps.LegacyRevOnOffCount +
                ", legacyRev3Bit=" + caps.LegacyRev3BitCount +
                ", legacyFlag3Bit=" + caps.LegacyFlag3BitCount +
                ", legacyRevStripe=" + caps.LegacyRevStripeCount + ")");

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
