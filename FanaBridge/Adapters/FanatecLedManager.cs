using System;
using FanaBridge.Devices;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using SimHub.Plugins.OutputPlugins.GraphicalDash.PSE;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// Bridges <see cref="FanatecLedDriver"/> into SimHub's
    /// <c>ILedDeviceManager</c> pipeline via <c>LedsGenericManager&lt;T&gt;</c>.
    ///
    /// Each <see cref="FanatecWheelDeviceInstance"/> creates one of these and passes it to
    /// <c>LedModuleSettings&lt;FanatecLedManager&gt;</c> as a pre-created driver.
    ///
    /// The manager does NOT hold a fixed encoder set. It is given two resolvers — one for
    /// the output <see cref="DeviceHandle"/> (transport + encoders) and one for the
    /// capabilities — both keyed to THIS instance's descriptor. So the driver is always
    /// (re)built over whichever connected device currently hosts the descriptor's rim:
    /// rebuild on a profile-override change (caps) OR on the device changing under it
    /// (handle), e.g. the rim moved to another base. See <see cref="MaybeRebuild"/>.
    /// </summary>
    public class FanatecLedManager : LedsGenericManager<FanatecLedDriver>
    {
        private readonly DeviceConfig _config;
        private readonly Func<DeviceHandle> _handleProvider;
        private readonly Func<WheelCapabilities> _capsProvider;

        // Track what the current driver was built from, so MaybeRebuild can detect both a
        // profile/caps change and a change of the underlying device (handle).
        private WheelProfile _lastDriverProfile;
        private DeviceHandle _lastHandle;

        /// <summary>
        /// Parameterless constructor required by the <c>new()</c> constraint on
        /// <c>LedModuleSettings&lt;T&gt;</c>.  Not used at runtime — the resolver
        /// constructor is called explicitly and the instance is passed to LedModuleSettings.
        /// </summary>
        public FanatecLedManager()
        {
        }

        /// <summary>
        /// Creates a manager bound to a specific device descriptor. <paramref name="handleProvider"/>
        /// returns the output handle of the connected device hosting this descriptor's rim
        /// (or a fallback handle when none is connected); <paramref name="capsProvider"/>
        /// returns the caps resolved for this descriptor (live when hosted, registration
        /// otherwise). Both are re-evaluated whenever the driver is (re)built.
        /// </summary>
        public FanatecLedManager(
            DeviceConfig config,
            Func<DeviceHandle> handleProvider,
            Func<WheelCapabilities> capsProvider)
        {
            _config = config;
            _handleProvider = handleProvider;
            _capsProvider = capsProvider;
        }

        // ── LedsGenericManager<T> overrides ──────────────────────────────

        /// <summary>
        /// Called by the base class when a connection is needed. Builds a driver from the
        /// CURRENT resolved handle + caps for this descriptor, so output always targets the
        /// device that hosts this rim — never a process-global encoder set.
        /// </summary>
        public override FanatecLedDriver GetDriver()
        {
            var handle = _handleProvider?.Invoke();
            var caps = _capsProvider?.Invoke() ?? WheelCapabilities.None;

            _lastDriverProfile = caps.Profile;
            _lastHandle = handle;

            var driver = new FanatecLedDriver(caps, handle?.Leds, handle?.LegacyLeds);

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
        /// If the active profile OR the underlying device changed, tears down the current
        /// driver so the base class recreates it via <see cref="GetDriver"/> on the next
        /// frame. Safe to call every frame — no-ops when nothing changed. The handle only
        /// changes across a reconnect (a rim cannot be on two bases at once), so this never
        /// fires mid-connected-frame.
        /// </summary>
        public void MaybeRebuild()
        {
            var caps = _capsProvider?.Invoke();
            if (caps?.Profile == null)
                return;   // nothing resolvable right now — keep the current driver

            var handle = _handleProvider?.Invoke();
            if (caps.Profile == _lastDriverProfile && ReferenceEquals(handle, _lastHandle))
                return;

            SimHub.Logging.Current.Info(
                "FanatecLedManager: rebuild — profile='" + (caps.Name ?? "?") +
                "', deviceChanged=" + (!ReferenceEquals(handle, _lastHandle)));

            Close();
            // _lastDriverProfile / _lastHandle are refreshed on the next GetDriver() call.
        }

        // IsConnected() and GetPhysicalMapper() are sealed in the base class
        // and delegate to the driver automatically — no override needed.
    }
}
