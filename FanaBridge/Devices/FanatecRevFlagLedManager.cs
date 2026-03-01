using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

namespace FanaBridge
{
    /// <summary>
    /// Bridges <see cref="FanatecRevFlagLedDriver"/> into SimHub's
    /// <c>ILedDeviceManager</c> pipeline via <c>LedsGenericManager&lt;T&gt;</c>.
    ///
    /// Used for devices that have Rev (RPM) and/or Flag (status) LEDs
    /// controlled via the col01 display interface, such as the Podium
    /// Button Module Endurance.
    /// </summary>
    public class FanatecRevFlagLedManager : LedsGenericManager<FanatecRevFlagLedDriver>
    {
        private readonly WheelCapabilities _caps;
        private FanatecRevFlagLedDriver _driver;

        /// <summary>
        /// Parameterless constructor required by the <c>new()</c> constraint.
        /// </summary>
        public FanatecRevFlagLedManager()
        {
            _caps = WheelCapabilities.None;
        }

        /// <summary>
        /// Creates a manager configured for a specific wheel's Rev/Flag LED layout.
        /// </summary>
        public FanatecRevFlagLedManager(WheelCapabilities caps)
        {
            _caps = caps ?? WheelCapabilities.None;
        }

        public override FanatecRevFlagLedDriver GetDriver()
        {
            _driver = new FanatecRevFlagLedDriver(_caps);

            SimHub.Logging.Current.Info(
                "FanatecRevFlagLedManager: Created driver for " + (_caps.Name ?? "unknown") +
                " (rev=" + _caps.RevLedCount + ", flag=" + _caps.FlagLedCount + ")");

            return _driver;
        }
    }
}
