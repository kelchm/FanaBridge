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
    /// Handles Rev (RPM), Flag (status), Button, and Encoder LEDs through
    /// a single driver instance.  The framework calls <see cref="SendLeds"/>
    /// every frame; this driver splits the physical LED range into regions
    /// and routes each to the appropriate <see cref="FanatecDevice"/> method.
    ///
    /// SimHub physical layout (contiguous, for mapper addressing):
    ///   [0 .. RevCount-1]                                         Rev LEDs
    ///   [RevCount .. RevCount+FlagCount-1]                        Flag LEDs
    ///   [RevCount+FlagCount .. RevCount+FlagCount+ButtonCount-1]  Button LEDs
    ///   [RevCount+FlagCount+ButtonCount .. AllLedCount-1]         Encoder LEDs
    ///
    /// Hardware color array mapping:
    ///   For wheels with RGB encoder LEDs (e.g. BMR), both button and encoder
    ///   colors are placed into a single subcmd 0x02 color array using
    ///   per-wheel index maps (<see cref="WheelCapabilities.EncoderColorIndices"/>
    ///   and <see cref="WheelCapabilities.BuildButtonColorIndices"/>).
    ///   For wheels with monochrome encoders (e.g. M4 GT3), encoder brightness
    ///   is placed at a config-driven offset in the subcmd 0x03 intensity payload.
    /// </summary>
    public class FanatecLedDriver : ILedButtonsDriver
    {
        private readonly WheelCapabilities _caps;
        private readonly PhysicalMapper _mapper;

        // Region boundaries in the physical index space
        private readonly int _revStart;
        private readonly int _flagStart;
        private readonly int _buttonStart;
        private readonly int _encoderStart;

        // Hardware index maps for interleaved RGB LEDs — built once from
        // WheelCapabilities.  null when encoders are monochrome or absent.
        private readonly int[] _buttonHwIndices;
        private readonly int[] _encoderHwIndices;

        // Reusable frame buffers — avoid per-frame heap allocations.
        private readonly ushort[] _revColors;
        private readonly ushort[] _flagColors;
        private readonly ushort[] _buttonColors;   // sized to ButtonColorLedCount
        private readonly byte[] _encoderIntensities;
        private readonly byte[] _intensityPayload;

        public FanatecLedDriver(WheelCapabilities caps)
        {
            _caps = caps ?? throw new ArgumentNullException(nameof(caps));
            _revStart = 0;
            _flagStart = caps.RevLedCount;
            _buttonStart = caps.RevLedCount + caps.FlagLedCount;
            _encoderStart = caps.RevLedCount + caps.FlagLedCount + caps.ButtonLedCount;

            _revColors = new ushort[caps.RevLedCount];
            _flagColors = new ushort[caps.FlagLedCount];
            _buttonColors = new ushort[caps.ButtonColorLedCount];
            _encoderIntensities = new byte[caps.HasMonochromeEncoders ? caps.EncoderLedCount : 0];
            _intensityPayload = new byte[FanatecDevice.INTENSITY_PAYLOAD_SIZE];

            // Pre-compute hardware index maps for interleaved encoders
            _buttonHwIndices = caps.BuildButtonColorIndices();  // null when not needed
            _encoderHwIndices = caps.EncoderColorIndices;       // null when monochrome/absent

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
                if (_caps.ButtonColorLedCount > 0 || _caps.HasMonochromeEncoders)
                    device.SetButtonLedState(new ushort[_caps.ButtonColorLedCount],
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
        /// Called every frame by <c>LedsGenericManager</c>.  Resolves each
        /// physical LED through the mapper, packs to premultiplied-alpha
        /// RGB565, then routes each region to the hardware:
        ///   Rev LEDs     → FanatecDevice.SetRevLedColors  (subcmd 0x00)
        ///   Flag LEDs    → FanatecDevice.SetFlagLedColors (subcmd 0x01)
        ///   Button LEDs  → FanatecDevice.SetButtonLedState (subcmd 0x02 + 0x03)
        ///   Encoder LEDs → FanatecDevice.SetEncoderIntensities (merged into subcmd 0x03)
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

            // ── Encoder LEDs (monochrome intensity-only) ─────────────
            // Only for wheels with monochrome encoders (EncoderIntensityOffset > 0).
            // RGB encoder LEDs are handled in the button section below.
            if (_caps.HasMonochromeEncoders)
            {
                Array.Clear(_encoderIntensities, 0, _encoderIntensities.Length);

                for (int i = 0; i < _caps.EncoderLedCount; i++)
                {
                    Color color = _mapper.GetColor(_encoderStart + i, state, ignoreBrightness: false);
                    _encoderIntensities[i] = ColorHelper.ColorToIntensity(color);
                }
            }

            // ── Button LEDs + intensity payload ──────────────────────
            // The color array covers both button and RGB encoder LEDs,
            // placed at their correct hardware indices via the index maps.
            if (_caps.ButtonColorLedCount > 0 || _caps.HasMonochromeEncoders)
            {
                Array.Clear(_buttonColors, 0, _buttonColors.Length);
                Array.Clear(_intensityPayload, 0, _intensityPayload.Length);

                if (_caps.HasRgbEncoders)
                {
                    // Interleaved layout — use hardware index maps.
                    // Button colors → mapped hardware positions
                    for (int i = 0; i < _caps.ButtonLedCount; i++)
                    {
                        int hw = _buttonHwIndices[i];
                        Color color = _mapper.GetColor(_buttonStart + i, state, ignoreBrightness: false);
                        _buttonColors[hw] = ColorHelper.ToRgb565Premultiplied(color);
                        _intensityPayload[hw] = 7;
                    }
                    // RGB encoder colors → mapped hardware positions
                    for (int i = 0; i < _caps.EncoderLedCount; i++)
                    {
                        int hw = _encoderHwIndices[i];
                        Color color = _mapper.GetColor(_encoderStart + i, state, ignoreBrightness: false);
                        _buttonColors[hw] = ColorHelper.ToRgb565Premultiplied(color);
                        _intensityPayload[hw] = 7;
                    }
                }
                else
                {
                    // Simple contiguous layout — no interleaving.
                    for (int i = 0; i < _caps.ButtonLedCount; i++)
                    {
                        Color color = _mapper.GetColor(_buttonStart + i, state, ignoreBrightness: false);
                        _buttonColors[i] = ColorHelper.ToRgb565Premultiplied(color);
                        _intensityPayload[i] = 7;
                    }
                }

                // Monochrome encoder intensities — placed at config-driven offset
                if (_caps.HasMonochromeEncoders)
                {
                    int offset = _caps.EncoderIntensityOffset;
                    for (int i = 0; i < _caps.EncoderLedCount; i++)
                        _intensityPayload[offset + i] = _encoderIntensities[i];
                }

                ok = device.SetButtonLedState(_buttonColors, _intensityPayload) && ok;
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
        ///   Rev + Flag LEDs → <c>LedRangeMap</c>     (telemetry LED strip)
        ///   Button LEDs     → <c>ButtonRangeMap</c>   (button lighting)
        ///   Encoder LEDs    → <c>EncodersRangeMap</c> (encoder indicators)
        /// </summary>
        private static PhysicalMapper BuildMapper(WheelCapabilities caps)
        {
            var maps = new List<IMap>();

            int revFlagCount = caps.RevLedCount + caps.FlagLedCount;
            if (revFlagCount > 0)
                maps.Add(new LedRangeMap(0, revFlagCount));

            if (caps.ButtonLedCount > 0)
                maps.Add(new ButtonRangeMap(0, caps.ButtonLedCount));

            if (caps.EncoderLedCount > 0)
                maps.Add(new EncodersRangeMap(0, caps.EncoderLedCount));

            return new PhysicalMapper(maps.ToArray());
        }
    }
}
