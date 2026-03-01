using System;
using System.Collections.Generic;
using System.Drawing;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;

namespace FanaBridge
{
    /// <summary>
    /// Unified BA63-compatible driver for all Fanatec LED types.
    ///
    /// Handles Rev (RPM), Flag (status), and Button/Encoder LEDs through
    /// a single driver instance.  The framework calls <see cref="SendLeds"/>
    /// every frame; this driver splits the physical LED range into regions
    /// and routes each to the appropriate <see cref="FanatecDevice"/> method.
    ///
    /// Physical layout (contiguous):
    ///   [0 .. RevCount-1]                         Rev LEDs
    ///   [RevCount .. RevCount+FlagCount-1]        Flag LEDs
    ///   [RevCount+FlagCount .. AllLedCount-1]     Button/Encoder LEDs
    ///
    /// All colors use premultiplied-alpha RGB565 so brightness is encoded
    /// directly in the 16-bit color value with full 5-6-5 resolution,
    /// instead of the hardware's coarse 3-bit intensity channel.
    /// </summary>
    public class FanatecLedDriver : ILedButtonsDriver
    {
        private readonly WheelCapabilities _caps;
        private readonly PhysicalMapper _mapper;

        // Region boundaries in the physical index space
        private readonly int _revStart;
        private readonly int _flagStart;
        private readonly int _buttonStart;

        // Reusable frame buffers — avoid per-frame heap allocations.
        // Sizes come from WheelCapabilities, not hardcoded protocol constants.
        private readonly ushort[] _revColors;
        private readonly ushort[] _flagColors;
        private readonly ushort[] _buttonColors;
        private readonly byte[] _buttonIntensities;

        public FanatecLedDriver(WheelCapabilities caps)
        {
            _caps = caps ?? throw new ArgumentNullException(nameof(caps));
            _revStart = 0;
            _flagStart = caps.RevLedCount;
            _buttonStart = caps.RevLedCount + caps.FlagLedCount;

            _revColors = new ushort[caps.RevLedCount];
            _flagColors = new ushort[caps.FlagLedCount];
            _buttonColors = new ushort[caps.TotalLedCount];
            _buttonIntensities = new byte[caps.TotalLedCount];

            _mapper = BuildMapper(caps);
        }

        // ── IDriver properties ───────────────────────────────────────────

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

        public void Clear()
        {
            try
            {
                var device = FanatecPlugin.Instance?.Device;
                if (device == null) return;

                if (_caps.HasRevLeds)
                    device.SetRevLedColors(new ushort[_caps.RevLedCount]);
                if (_caps.HasFlagLeds)
                    device.SetFlagLedColors(new ushort[_caps.FlagLedCount]);
                if (_caps.TotalLedCount > 0)
                    device.SetButtonLedState(new ushort[_caps.TotalLedCount],
                                              new byte[_caps.TotalLedCount]);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("FanatecLedDriver: Clear failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            // Hardware lifetime managed by FanatecPlugin.
        }

        // ── ILedButtonsDriver ────────────────────────────────────────────

        /// <summary>
        /// Called every frame by <c>LedsGenericManager</c>.  Resolves each
        /// physical LED through the mapper, packs to premultiplied-alpha
        /// RGB565, then routes each region to the hardware:
        ///   Rev LEDs    → FanatecDevice.SetRevLedColors  (subcmd 0x00)
        ///   Flag LEDs   → FanatecDevice.SetFlagLedColors (subcmd 0x01)
        ///   Button LEDs → FanatecDevice.SetButtonLedState (subcmd 0x02 + 0x03)
        /// </summary>
        public bool SendLeds(LedDeviceState state, bool forceRefresh)
        {
            var device = FanatecPlugin.Instance?.Device;
            if (device == null || !device.IsConnected)
                return false;

            bool ok = true;

            // ── Rev LEDs ─────────────────────────────────────────────
            if (_caps.HasRevLeds)
            {
                Array.Clear(_revColors, 0, _revColors.Length);

                for (int i = 0; i < _caps.RevLedCount; i++)
                {
                    Color color = _mapper.GetColor(_revStart + i, state, ignoreBrightness: false);
                    _revColors[i] = ColorHelper.ToRgb565Premultiplied(color);
                }

                ok = device.SetRevLedColors(_revColors) && ok;
            }

            // ── Flag LEDs ────────────────────────────────────────────
            if (_caps.HasFlagLeds)
            {
                Array.Clear(_flagColors, 0, _flagColors.Length);

                for (int i = 0; i < _caps.FlagLedCount; i++)
                {
                    Color color = _mapper.GetColor(_flagStart + i, state, ignoreBrightness: false);
                    _flagColors[i] = ColorHelper.ToRgb565Premultiplied(color);
                }

                ok = device.SetFlagLedColors(_flagColors) && ok;
            }

            // ── Button/Encoder LEDs ──────────────────────────────────
            if (_caps.TotalLedCount > 0)
            {
                Array.Clear(_buttonColors, 0, _buttonColors.Length);
                Array.Clear(_buttonIntensities, 0, _buttonIntensities.Length);

                for (int i = 0; i < _caps.TotalLedCount; i++)
                {
                    Color color = _mapper.GetColor(_buttonStart + i, state, ignoreBrightness: false);
                    _buttonColors[i] = ColorHelper.ToRgb565Premultiplied(color);
                    _buttonIntensities[i] = 7; // Max intensity — brightness is in the color
                }

                ok = device.SetButtonLedState(_buttonColors, _buttonIntensities) && ok;
            }

            return ok;
        }

        public IPhysicalMapper GetPhysicalMapper()
        {
            return _mapper;
        }

        // ── Mapper construction ──────────────────────────────────────────

        /// <summary>
        /// Builds a <see cref="PhysicalMapper"/> with the correct map type
        /// for each LED region:
        ///   Rev + Flag LEDs → <c>LedRangeMap</c>  (telemetry LED strip)
        ///   Button LEDs     → <c>ButtonRangeMap</c> (button lighting)
        /// </summary>
        private static PhysicalMapper BuildMapper(WheelCapabilities caps)
        {
            var maps = new List<IMap>();

            int revFlagCount = caps.RevLedCount + caps.FlagLedCount;
            if (revFlagCount > 0)
                maps.Add(new LedRangeMap(0, revFlagCount));

            if (caps.TotalLedCount > 0)
                maps.Add(new ButtonRangeMap(0, caps.TotalLedCount));

            return new PhysicalMapper(maps.ToArray());
        }
    }
}
