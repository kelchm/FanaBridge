using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

namespace FanaBridge
{
    /// <summary>
    /// Bridges <see cref="FanatecLedButtonsDriver"/> into SimHub's
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
    public class FanatecLedManager : LedsGenericManager<FanatecLedButtonsDriver>
    {
        private readonly WheelCapabilities _caps;
        private FanatecLedButtonsDriver _driver;

        /// <summary>
        /// Parameterless constructor required by the <c>new()</c> constraint on
        /// <c>LedModuleSettings&lt;T&gt;</c>.  Not used at runtime — the
        /// <see cref="FanatecLedManager(WheelCapabilities)"/> constructor is
        /// called explicitly and the instance is passed to LedModuleSettings.
        /// </summary>
        public FanatecLedManager()
        {
            // Fallback: use empty caps.  The real constructor below is preferred.
            _caps = WheelCapabilities.None;
        }

        /// <summary>
        /// Creates a manager configured for a specific wheel's LED layout.
        /// </summary>
        public FanatecLedManager(WheelCapabilities caps)
        {
            _caps = caps ?? WheelCapabilities.None;
        }

        // ── LedsGenericManager<T> overrides ──────────────────────────────

        /// <summary>
        /// Called by the base class when a connection is needed.
        /// Creates the BA63-compatible driver with the wheel's capabilities.
        /// </summary>
        public override FanatecLedButtonsDriver GetDriver()
        {
            _driver = new FanatecLedButtonsDriver(_caps);

            SimHub.Logging.Current.Info(
                "FanatecLedManager: Created driver for " + (_caps.Name ?? "unknown") +
                " (" + _caps.TotalLedCount + " LEDs)");

            return _driver;
        }

        // IsConnected() and GetPhysicalMapper() are sealed in the base class
        // and delegate to the driver automatically — no override needed.
    }
}
