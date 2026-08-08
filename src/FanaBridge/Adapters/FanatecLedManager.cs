using System;
using FanaBridge.Devices.Profiles;
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

        // Resolves the current plugin generation on every driver build, so a
        // rebuilt driver always targets the live hardware core even if the
        // plugin was replaced since construction (issue #37). Injected rather
        // than read from the singleton so the owning device instance and its
        // manager always agree on which generation they are talking to.
        private readonly Func<FanatecPlugin> _pluginResolver;

        // The profile the current driver was built from, and the profile last
        // seen at the source. Kept apart because a driver that fails to rebuild
        // leaves _lastDriverProfile stale: comparing against that alone would
        // tear the driver down and log on every frame until the rebuild starts
        // succeeding again.
        private WheelProfile _lastDriverProfile;
        private WheelProfile _lastObservedProfile;

        // Whether the "no driver" state has already been reported, so the
        // unavailable/available transitions log once rather than per frame.
        private bool _noDriverReported;

        /// <summary>
        /// Keep asking for a driver forever. The base class otherwise stops
        /// after a handful of attempts, which for this late-bound manager would
        /// mean a device that was idle while the plugin was down never recovers
        /// when it comes back.
        /// </summary>
        public override int? ReConnectAttempts => null;

        /// <summary>
        /// Parameterless constructor required by the <c>new()</c> constraint on
        /// <c>LedModuleSettings&lt;T&gt;</c>.  Not used at runtime — the
        /// <see cref="FanatecLedManager(DeviceConfig, Func{FanatecPlugin})"/>
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
        public FanatecLedManager(DeviceConfig config, Func<FanatecPlugin> pluginResolver)
        {
            _config = config;
            _pluginResolver = pluginResolver ?? (() => FanatecPlugin.Instance);
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
            try
            {
                // Bind only to the current generation's encoders. Keeping the
                // previous ones as a fallback would hand the driver a transport
                // belonging to a disposed generation (issue #37).
                var plugin = _pluginResolver();
                var leds = plugin?.Leds;
                var legacyLeds = plugin?.LegacyLeds;

                if (plugin == null || leds == null || legacyLeds == null)
                {
                    // Nothing to drive yet. Returning null pauses output; the
                    // driver constructor would otherwise throw, and this runs
                    // outside the base class's guarded section.
                    ReportNoDriver("no active plugin generation");
                    return null;
                }

                var caps = plugin.ResolveCapsFor(_config) ?? WheelCapabilities.None;
                var driver = new FanatecLedDriver(caps, leds, legacyLeds);

                _lastDriverProfile = caps.Profile;

                if (_noDriverReported)
                {
                    _noDriverReported = false;
                    SimHub.Logging.Current.Info(
                        "FanatecLedManager: runtime available again for " +
                        (caps.Name ?? "device") + " — LED output resuming");
                }

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
            catch (Exception ex)
            {
                ReportNoDriver(ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Records that no driver could be built, logging only on the
        /// transition so a device left without a runtime doesn't write a line
        /// per frame.
        /// </summary>
        private void ReportNoDriver(string reason)
        {
            if (_noDriverReported)
                return;

            _noDriverReported = true;
            SimHub.Logging.Current.Info(
                "FanatecLedManager: no LED driver for " +
                (_config?.Capabilities?.Name ?? "device") + " — " + reason +
                "; output paused until it returns");
        }

        /// <summary>
        /// If the active profile changed, tears down the current driver so
        /// the base class recreates it via <see cref="GetDriver"/> on the
        /// next frame.  Safe to call every frame — no-ops when unchanged.
        /// </summary>
        public void HotSwapIfNeeded(WheelCapabilities currentCaps)
        {
            if (currentCaps?.Profile == null || currentCaps.Profile == _lastObservedProfile)
                return;

            _lastObservedProfile = currentCaps.Profile;

            if (currentCaps.Profile == _lastDriverProfile)
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
