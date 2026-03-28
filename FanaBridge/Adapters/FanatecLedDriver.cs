using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using BA63Driver.Base;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;

namespace FanaBridge.Adapters
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
    ///   [RevFlagCount .. RevFlagCount+ButtonCount-1]  Button LEDs
    ///
    /// Hardware dispatch (col03 — 64-byte reports):
    ///   RevRgb             → subcmd 0x00 RGB565  (SetRevLedColors)
    ///   FlagRgb            → subcmd 0x01 RGB565  (SetFlagLedColors)
    ///   ButtonRgb          → subcmd 0x02 RGB565  (SetButtonLedState)
    ///   ButtonAuxIntensity → subcmd 0x03 intensity (SetButtonLedState)
    ///
    /// Hardware dispatch (col01 — 8-byte reports via LegacyLedEncoder):
    ///   LegacyRevOnOff     → subcmd 0x08 bitmask (per-LED on/off)
    ///   LegacyRevStripe    → subcmd 0x08 RGB333  (single color for entire strip)
    ///   LegacyRev3Bit      → subcmd 0x0A per-LED 3-bit color (7 colors + off)
    ///   LegacyFlag3Bit     → subcmd 0x0B per-LED 3-bit color for flag LEDs
    /// </summary>
    public class FanatecLedDriver : DriverBase, ILedButtonsDriver
    {
        private readonly WheelCapabilities _caps;
        private readonly WheelProfile _profile;
        private readonly PhysicalMapper _mapper;
        private readonly LedEncoder _leds;
        private readonly LegacyLedEncoder _legacyLeds;
        private readonly IDeviceTransport _transport;

        // Pre-built dispatch table: for each logical LED index, the channel
        // and hardware index to write to.  Built once from the profile.
        private readonly LedChannel[] _ledChannels;
        private readonly int[] _ledHwIndices;

        // Ping-pong frame buffers — zero per-frame heap allocations.
        // We only start a new task after the previous one completes, so
        // buffer[_ping] is always safe to build while buffer[1-_ping] is sent.
        private readonly ushort[][] _revRgbBufs;          // [2][RevRgbCount]
        private readonly ushort[][] _flagRgbBufs;         // [2][FlagRgbCount]
        private readonly ushort[][] _buttonRgbBufs;       // [2][_buttonRgbSlotCount]
        private readonly byte[][] _intensityBufs;         // [2][INTENSITY_PAYLOAD_SIZE]
        private readonly bool[][] _legacyRevOnOffBufs;    // [2][LegacyRevOnOffCount]
        private readonly byte[][] _legacyRev3BitBufs;     // [2][LegacyRev3BitCount * 3]
        private readonly byte[][] _legacyFlag3BitBufs;    // [2][LegacyFlag3BitCount * 3]
        private readonly ushort[] _legacyRevStripeBufs;   // [2] — one RGB333 value per slot
        private int _ping = 0;

        // Track how many ButtonRgb slots the hardware needs
        private readonly int _buttonRgbSlotCount;

        // Color conversion delegate for button LEDs — RGB565 or RGB555
        // depending on the hardware. Resolved once in the constructor.
        private readonly Func<Color, ushort> _buttonColorConverter;

        // Async write state — mirrors the typical SimHub driver pattern.
        // SendLeds kicks off a Task.Run; if the previous write is still
        // in-flight the frame is dropped rather than blocking.
        private Task _refreshTask;

        public FanatecLedDriver(WheelCapabilities caps, LedEncoder leds, LegacyLedEncoder legacyLeds, IDeviceTransport transport)
        {
            _caps = caps ?? throw new ArgumentNullException(nameof(caps));
            _leds = leds ?? throw new ArgumentNullException(nameof(leds));
            _legacyLeds = legacyLeds;
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _profile = caps.Profile;

            // Build dispatch tables from the profile
            int ledCount = _profile?.Leds?.Count ?? 0;
            _ledChannels = new LedChannel[ledCount];
            _ledHwIndices = new int[ledCount];

            int maxButtonRgbHwIndex = -1;

            for (int i = 0; i < ledCount; i++)
            {
                var led = _profile.Leds[i];
                _ledChannels[i] = led.Channel;
                _ledHwIndices[i] = led.HwIndex;

                if (led.Channel == LedChannel.ButtonRgb && led.HwIndex > maxButtonRgbHwIndex)
                    maxButtonRgbHwIndex = led.HwIndex;
            }

            _buttonRgbSlotCount = maxButtonRgbHwIndex + 1;

            _revRgbBufs = new[] { new ushort[caps.RevRgbCount], new ushort[caps.RevRgbCount] };
            _flagRgbBufs = new[] { new ushort[caps.FlagRgbCount], new ushort[caps.FlagRgbCount] };
            _buttonRgbBufs = new[] { new ushort[Math.Max(_buttonRgbSlotCount, 0)],
                                     new ushort[Math.Max(_buttonRgbSlotCount, 0)] };
            _intensityBufs = new[] { new byte[LedEncoder.INTENSITY_PAYLOAD_SIZE],
                                     new byte[LedEncoder.INTENSITY_PAYLOAD_SIZE] };
            _legacyRevOnOffBufs = new[] { new bool[caps.LegacyRevOnOffCount], new bool[caps.LegacyRevOnOffCount] };
            _legacyRev3BitBufs = new[] { new byte[caps.LegacyRev3BitCount * 3], new byte[caps.LegacyRev3BitCount * 3] };
            _legacyFlag3BitBufs = new[] { new byte[caps.LegacyFlag3BitCount * 3], new byte[caps.LegacyFlag3BitCount * 3] };
            _legacyRevStripeBufs = new ushort[2];

            _buttonColorConverter = caps.ColorFormat == ColorFormat.Rgb555
                ? (Func<Color, ushort>)ColorHelper.ToRgb555Premultiplied
                : ColorHelper.ToRgb565Premultiplied;

            _mapper = BuildMapper(caps);
        }

        // ── IDriver properties ───────────────────────────────────────────
        // IsConnected, SerialNumber, FirmwareVersion inherited from DriverBase.

        // ── IDriver methods ──────────────────────────────────────────────

        public void Clear()
        {
            try
            {
                _refreshTask?.Wait(2000);
            }
            catch { }

            try
            {
                if (_caps.HasRevRgb)
                    _leds.SetRevLedColors(new ushort[_caps.RevRgbCount]);
                if (_caps.HasFlagRgb)
                    _leds.SetFlagLedColors(new ushort[_caps.FlagRgbCount]);
                if (_buttonRgbSlotCount > 0 || _caps.ButtonAuxIntensityCount > 0)
                    _leds.SetButtonLedState(new ushort[_buttonRgbSlotCount],
                                              new byte[LedEncoder.INTENSITY_PAYLOAD_SIZE]);
                if ((_caps.HasLegacyRevOnOff || _caps.HasLegacyRevStripe || _caps.HasLegacyRev3Bit || _caps.HasLegacyFlag3Bit) && _legacyLeds != null)
                    _legacyLeds.Clear();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("FanatecLedDriver: Clear failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            try
            {
                _refreshTask?.Wait(2000);
            }
            catch { }

            IsConnected = false;
            // Hardware lifetime managed by FanatecPlugin — don't close HID streams.
        }

        // ── ILedButtonsDriver ────────────────────────────────────────────

        /// <summary>
        /// Called every frame by <c>LedsGenericManager</c>.  Iterates the
        /// profile's LED list, resolves each color through the mapper, and
        /// dispatches to the hardware via the appropriate channel method.
        /// </summary>
        public bool SendLeds(LedDeviceState state, bool forceRefresh)
        {
            if (!IsConnected || !_transport.IsConnected)
                return false;

            // Skip unchanged frames — avoid color conversion and USB I/O
            if (!stateComparer.HasChanged(state) && !forceRefresh)
                return true;

            // Drop this frame if the previous write is still in flight
            if (_refreshTask != null)
            {
                var t = _refreshTask;
                if (t != null && t.Status != TaskStatus.RanToCompletion
                              && t.Status != TaskStatus.Faulted)
                    return true;
            }

            // ── Build into the current ping-pong slot ────────────────
            // The previous task (using the other slot) is guaranteed complete
            // by the in-flight check above, so writing here is race-free.
            int ping = _ping;
            var revRgbColors = _revRgbBufs[ping];
            var flagRgbColors = _flagRgbBufs[ping];
            var buttonRgbBuf = _buttonRgbBufs[ping];
            var intensities = _intensityBufs[ping];
            var legacyRevOnOffBuf = _legacyRevOnOffBufs[ping];
            var legacyRev3BitBuf = _legacyRev3BitBufs[ping];
            var legacyFlag3BitBuf = _legacyFlag3BitBufs[ping];
            ushort legacyRevStripeColor = 0;

            Array.Clear(revRgbColors, 0, revRgbColors.Length);
            Array.Clear(flagRgbColors, 0, flagRgbColors.Length);
            Array.Clear(buttonRgbBuf, 0, buttonRgbBuf.Length);
            Array.Clear(intensities, 0, intensities.Length);
            Array.Clear(legacyRevOnOffBuf, 0, legacyRevOnOffBuf.Length);
            Array.Clear(legacyRev3BitBuf, 0, legacyRev3BitBuf.Length);
            Array.Clear(legacyFlag3BitBuf, 0, legacyFlag3BitBuf.Length);

            for (int i = 0; i < _ledChannels.Length; i++)
            {
                Color color = _mapper.GetColor(i, state, ignoreBrightness: false);
                int hw = _ledHwIndices[i];

                switch (_ledChannels[i])
                {
                    case LedChannel.RevRgb:
                        if (hw < revRgbColors.Length)
                            revRgbColors[hw] = ColorHelper.ToRgb565Premultiplied(color);
                        break;

                    case LedChannel.FlagRgb:
                        if (hw < flagRgbColors.Length)
                            flagRgbColors[hw] = ColorHelper.ToRgb565Premultiplied(color);
                        break;

                    case LedChannel.ButtonRgb:
                        if (hw < buttonRgbBuf.Length)
                        {
                            buttonRgbBuf[hw] = _buttonColorConverter(color);
                            intensities[hw] = 7; // max — brightness is in the color
                        }
                        break;

                    case LedChannel.ButtonAuxIntensity:
                        if (hw < intensities.Length)
                            intensities[hw] = ColorHelper.ColorToIntensity(color);
                        break;

                    case LedChannel.LegacyRevOnOff:
                        if (hw < legacyRevOnOffBuf.Length)
                            legacyRevOnOffBuf[hw] = ColorHelper.ColorToIntensity(color) > 0;
                        break;

                    case LedChannel.LegacyRevStripe:
                        legacyRevStripeColor = ColorHelper.ToRgb333Premultiplied(color);
                        break;

                    case LedChannel.LegacyRev3Bit:
                        {
                            int rgbBase = hw * 3;
                            if (rgbBase + 2 < legacyRev3BitBuf.Length)
                            {
                                var (r, g, b) = ColorHelper.ColorToRgbBools(color);
                                legacyRev3BitBuf[rgbBase] = r ? (byte)1 : (byte)0;
                                legacyRev3BitBuf[rgbBase + 1] = g ? (byte)1 : (byte)0;
                                legacyRev3BitBuf[rgbBase + 2] = b ? (byte)1 : (byte)0;
                            }
                        }
                        break;

                    case LedChannel.LegacyFlag3Bit:
                        {
                            int flagBase = hw * 3;
                            if (flagBase + 2 < legacyFlag3BitBuf.Length)
                            {
                                var (r, g, b) = ColorHelper.ColorToRgbBools(color);
                                legacyFlag3BitBuf[flagBase] = r ? (byte)1 : (byte)0;
                                legacyFlag3BitBuf[flagBase + 1] = g ? (byte)1 : (byte)0;
                                legacyFlag3BitBuf[flagBase + 2] = b ? (byte)1 : (byte)0;
                            }
                        }
                        break;
                }
            }

            bool sendButtonLeds = _buttonRgbSlotCount > 0 || _caps.ButtonAuxIntensityCount > 0;
            bool sendLegacyRevOnOff = _caps.HasLegacyRevOnOff;
            bool sendLegacyRev3Bit = _caps.HasLegacyRev3Bit;
            bool sendLegacyFlag3Bit = _caps.HasLegacyFlag3Bit;
            bool sendLegacyRevStripe = _caps.HasLegacyRevStripe;
            var capturedRevStripeColor = legacyRevStripeColor;

            // ── Send to hardware asynchronously ──────────────────────
            // Capture the local array references (no Clone needed — the ping-pong
            // guarantees the background task finishes before we write this slot again).
            _refreshTask = Task.Run(() =>
            {
                try
                {
                    if (_caps.HasRevRgb)
                        _leds.SetRevLedColors(revRgbColors);

                    if (_caps.HasFlagRgb)
                        _leds.SetFlagLedColors(flagRgbColors);

                    if (sendButtonLeds)
                        _leds.SetButtonLedState(buttonRgbBuf, intensities);

                    if (sendLegacyRevOnOff && _legacyLeds != null)
                        _legacyLeds.SetLegacyRevOnOff(legacyRevOnOffBuf);

                    if (sendLegacyRev3Bit && _legacyLeds != null)
                        _legacyLeds.SetLegacyRev3Bit(legacyRev3BitBuf);

                    if (sendLegacyFlag3Bit && _legacyLeds != null)
                        _legacyLeds.SetLegacyFlag3Bit(legacyFlag3BitBuf);

                    if (sendLegacyRevStripe && _legacyLeds != null)
                        _legacyLeds.SetRevStripeColor(capturedRevStripeColor);
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Error(
                        "FanatecLedDriver: Write failed, marking disconnected: " + ex.Message);
                    IsConnected = false;
                }
                finally
                {
                    _refreshTask = null;
                }
            });

            // Advance to the other slot for the next frame
            _ping = 1 - ping;

            stateComparer.SetLastState(state);
            return true;
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
        /// All ButtonRgb + ButtonAuxIntensity LEDs are grouped under
        /// ButtonRangeMap since SimHub's native devices don't distinguish
        /// encoder vs. button LEDs.
        /// </summary>
        private static PhysicalMapper BuildMapper(WheelCapabilities caps)
        {
            var maps = new List<IMap>();

            int revFlagCount = caps.RevFlagCount;
            if (revFlagCount > 0)
                maps.Add(new LedRangeMap(0, revFlagCount));

            int buttonCount = caps.ButtonLedCount;
            if (buttonCount > 0)
                maps.Add(new ButtonRangeMap(revFlagCount, buttonCount));

            return new PhysicalMapper(maps.ToArray());
        }
    }
}
