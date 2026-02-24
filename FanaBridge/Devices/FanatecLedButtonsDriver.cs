using System;
using System.Drawing;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;

namespace FanaBridge
{
    /// <summary>
    /// BA63-compatible driver for Fanatec wheel LEDs.
    ///
    /// Implements <see cref="ILedButtonsDriver"/> so it integrates with SimHub's
    /// native <c>LedsGenericManager&lt;T&gt;</c> pipeline.  The framework calls
    /// <see cref="SendLeds"/> every frame with a <see cref="LedDeviceState"/>
    /// containing per-group Color arrays and brightness values.  This driver
    /// resolves each physical LED through the <see cref="PhysicalMapper"/>,
    /// packs the result to BGR565, and sends to hardware via the shared
    /// <see cref="FanatecDevice"/>.
    /// </summary>
    public class FanatecLedButtonsDriver : ILedButtonsDriver
    {
        private readonly WheelCapabilities _caps;
        private readonly PhysicalMapper _mapper;

        // Reusable frame buffers — avoid per-frame allocations.
        private readonly ushort[] _frameColors = new ushort[FanatecDevice.LED_COUNT];
        private readonly byte[] _frameIntensities = new byte[FanatecDevice.LED_COUNT];

        public FanatecLedButtonsDriver(WheelCapabilities caps)
        {
            _caps = caps ?? throw new ArgumentNullException(nameof(caps));
            _mapper = BuildMapper(caps);
        }

        // ── IDriver properties ───────────────────────────────────────────

        /// <summary>
        /// Connected when the shared HID device is open and the SDK reports
        /// the correct wheel type (checked by the manager/device-instance).
        /// </summary>
        public bool IsConnected
        {
            get
            {
                try
                {
                    var device = FanatecPlugin.Instance?.Device;
                    return device != null && device.IsConnected;
                }
                catch
                {
                    return false;
                }
            }
        }

        public string SerialNumber => null;

        public string FirmwareVersion => null;

        // ── IDriver methods ──────────────────────────────────────────────

        /// <summary>Turn off all LEDs on the hardware.</summary>
        public void Clear()
        {
            try
            {
                var device = FanatecPlugin.Instance?.Device;
                if (device == null) return;
                device.SetLedState(new ushort[FanatecDevice.LED_COUNT], new byte[FanatecDevice.LED_COUNT]);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("FanatecLedButtonsDriver: Clear failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            // Nothing to dispose — hardware lifetime is managed by FanatecPlugin.
        }

        // ── ILedButtonsDriver ────────────────────────────────────────────

        /// <summary>
        /// Called every frame by <c>LedsGenericManager</c>.  Resolves each
        /// physical LED through the mapper (which applies brightness) and
        /// packs the result into BGR565 for the Fanatec HID protocol.
        /// </summary>
        public bool SendLeds(LedDeviceState state, bool forceRefresh)
        {
            var device = FanatecPlugin.Instance?.Device;
            if (device == null || !device.IsConnected)
                return false;

            Array.Clear(_frameColors, 0, _frameColors.Length);
            Array.Clear(_frameIntensities, 0, _frameIntensities.Length);

            int totalPhysical = _caps.TotalLedCount;
            for (int physIdx = 0; physIdx < totalPhysical && physIdx < FanatecDevice.LED_COUNT; physIdx++)
            {
                // PhysicalMapper resolves logical group → physical index,
                // applies ColorOrder and brightness scaling.
                Color color = _mapper.GetColor(physIdx, state, ignoreBrightness: false);
                _frameColors[physIdx] = ColorHelper.ToRgb565(color);
                _frameIntensities[physIdx] = 7; // max hardware intensity — brightness is software-side
            }

            return device.SetLedState(_frameColors, _frameIntensities);
        }

        /// <summary>
        /// Returns the logical-to-physical LED mapping used by the framework
        /// to route button/encoder LED groups to hardware positions.
        /// </summary>
        public IPhysicalMapper GetPhysicalMapper()
        {
            return _mapper;
        }

        // ── Mapper construction ──────────────────────────────────────────

        /// <summary>
        /// Builds a <see cref="PhysicalMapper"/> from the wheel's capabilities.
        /// All LEDs are mapped as "buttons" in a single contiguous range —
        /// this matches the LedModuleOptions configuration where we combine
        /// button and encoder LEDs into ButtonsCount.
        /// </summary>
        private static PhysicalMapper BuildMapper(WheelCapabilities caps)
        {
            // Single ButtonRangeMap covers all LEDs (physical 0..N-1)
            // since we combine button + encoder LEDs in LedModuleOptions.ButtonsCount
            return new PhysicalMapper(new IMap[]
            {
                new ButtonRangeMap(0, caps.TotalLedCount)
            });
        }
    }
}
