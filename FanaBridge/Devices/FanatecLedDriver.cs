using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;
using FanaBridge;
using FanaBridge.Core;

namespace FanaBridge.Devices
{
    /// <summary>
    /// Unified BA63-compatible driver for all Fanatec LED types.
    ///
    /// Driven entirely by a <see cref="WheelProfile"/>'s LED list.
    /// Each frame, the driver iterates the profile's LED definitions in
    /// order (array index = SimHub logical index), resolves the color
    /// through the mapper, and dispatches to the correct
    /// <see cref="FanatecDevice"/> hardware method based on the LED's
    /// <see cref="LedChannel"/>.
    ///
    /// SimHub physical layout (contiguous, defined by LED array order):
    ///   [0 .. RevFlagCount-1]                     Rev + Flag LEDs
    ///   [RevFlagCount .. RevFlagCount+ButtonCount-1]  Button LEDs (color + mono)
    ///
    /// Hardware dispatch:
    ///   Rev LEDs    → subcmd 0x00 RGB565  (SetRevLedColors)
    ///   Flag LEDs   → subcmd 0x01 RGB565  (SetFlagLedColors)
    ///   Color LEDs  → subcmd 0x02 RGB565  (SetButtonLedState)
    ///   Mono LEDs   → subcmd 0x03 intensity (SetButtonLedState)
    /// </summary>
    public class FanatecLedDriver : ILedButtonsDriver
    {
        private readonly WheelCapabilities _caps;
        private readonly WheelProfile _profile;
        private readonly PhysicalMapper _mapper;

        // Pre-built dispatch table: for each logical LED index, the channel
        // and hardware index to write to.  Built once from the profile.
        private readonly LedChannel[] _ledChannels;
        private readonly int[] _ledHwIndices;

        // Reusable frame buffers — avoid per-frame heap allocations.
        // Sized from the profile's channel counts.
        private readonly ushort[] _revColors;
        private readonly ushort[] _flagColors;
        private readonly ushort[] _buttonColors;   // sized to max color hw index + 1
        private readonly byte[] _intensityPayload;

        // Track how many color-channel slots the hardware needs
        private readonly int _colorSlotCount;

        // Color conversion delegate for button LEDs — RGB565 or RGB555
        // depending on the hardware. Resolved once in the constructor.
        private readonly Func<Color, ushort> _buttonColorConverter;

        public FanatecLedDriver(WheelCapabilities caps)
        {
            _caps = caps ?? throw new ArgumentNullException(nameof(caps));
            _profile = caps.Profile;

            // Build dispatch tables from the profile
            int ledCount = _profile?.Leds?.Count ?? 0;
            _ledChannels = new LedChannel[ledCount];
            _ledHwIndices = new int[ledCount];

            int maxColorHwIndex = -1;

            for (int i = 0; i < ledCount; i++)
            {
                var led = _profile.Leds[i];
                _ledChannels[i] = led.Channel;
                _ledHwIndices[i] = led.HwIndex;

                if (led.Channel == LedChannel.Color && led.HwIndex > maxColorHwIndex)
                    maxColorHwIndex = led.HwIndex;
            }

            _colorSlotCount = maxColorHwIndex + 1;

            _revColors = new ushort[caps.RevLedCount];
            _flagColors = new ushort[caps.FlagLedCount];
            _buttonColors = new ushort[Math.Max(_colorSlotCount, 0)];
            _intensityPayload = new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE];

            _buttonColorConverter = caps.ColorFormat == ColorFormat.Rgb555
                ? (Func<Color, ushort>)ColorHelper.ToRgb555Premultiplied
                : ColorHelper.ToRgb565Premultiplied;

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
                if (_colorSlotCount > 0 || _caps.MonoLedCount > 0)
                    device.SetButtonLedState(new ushort[_colorSlotCount],
                                              new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE]);
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
        /// Called every frame by <c>LedsGenericManager</c>.  Iterates the
        /// profile's LED list, resolves each color through the mapper, and
        /// dispatches to the hardware via the appropriate channel method.
        /// </summary>
        public bool SendLeds(LedDeviceState state, bool forceRefresh)
        {
            var device = FanatecPlugin.Instance?.Device;
            if (device == null || !device.IsConnected)
                return false;

            bool ok = true;

            // Clear frame buffers
            Array.Clear(_revColors, 0, _revColors.Length);
            Array.Clear(_flagColors, 0, _flagColors.Length);
            Array.Clear(_buttonColors, 0, _buttonColors.Length);
            Array.Clear(_intensityPayload, 0, _intensityPayload.Length);

            // ── Per-LED dispatch ─────────────────────────────────────
            for (int i = 0; i < _ledChannels.Length; i++)
            {
                Color color = _mapper.GetColor(i, state, ignoreBrightness: false);
                int hw = _ledHwIndices[i];

                switch (_ledChannels[i])
                {
                    case LedChannel.Rev:
                        if (hw < _revColors.Length)
                            _revColors[hw] = ColorHelper.ToRgb565Premultiplied(color);
                        break;

                    case LedChannel.Flag:
                        if (hw < _flagColors.Length)
                            _flagColors[hw] = ColorHelper.ToRgb565Premultiplied(color);
                        break;

                    case LedChannel.Color:
                        if (hw < _buttonColors.Length)
                        {
                            _buttonColors[hw] = _buttonColorConverter(color);
                            _intensityPayload[hw] = 7; // max — brightness is in the color
                        }
                        break;

                    case LedChannel.Mono:
                        if (hw < _intensityPayload.Length)
                            _intensityPayload[hw] = ColorHelper.ColorToIntensity(color);
                        break;
                }
            }

            // ── Send to hardware ─────────────────────────────────────
            if (_caps.HasRevLeds)
                ok = device.SetRevLedColors(_revColors) && ok;

            if (_caps.HasFlagLeds)
                ok = device.SetFlagLedColors(_flagColors) && ok;

            if (_colorSlotCount > 0 || _caps.MonoLedCount > 0)
                ok = device.SetButtonLedState(_buttonColors, _intensityPayload) && ok;

            return ok;
        }

        public IPhysicalMapper GetPhysicalMapper()
        {
            return _mapper;
        }

        // ── Mapper construction ──────────────────────────────────────────

        /// <summary>
        /// Builds a <see cref="PhysicalMapper"/> with map regions matching
        /// the SimHub layout:
        ///   Rev + Flag LEDs → <c>LedRangeMap</c>    (telemetry LED strip)
        ///   Button LEDs     → <c>ButtonRangeMap</c>  (button lighting)
        ///
        /// All color + mono LEDs are grouped under ButtonRangeMap since
        /// SimHub's native devices don't distinguish encoder vs. button LEDs.
        /// </summary>
        private static PhysicalMapper BuildMapper(WheelCapabilities caps)
        {
            var maps = new List<IMap>();

            int revFlagCount = caps.RevFlagCount;
            if (revFlagCount > 0)
                maps.Add(new LedRangeMap(0, revFlagCount));

            int buttonCount = caps.ButtonLedCount;
            if (buttonCount > 0)
                maps.Add(new ButtonRangeMap(0, buttonCount));

            return new PhysicalMapper(maps.ToArray());
        }
    }
}
