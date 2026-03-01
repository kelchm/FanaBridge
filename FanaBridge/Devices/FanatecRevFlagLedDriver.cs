using System;
using System.Drawing;
using BA63Driver.Interfaces;
using BA63Driver.Mapper;

namespace FanaBridge
{
    /// <summary>
    /// BA63-compatible driver for Fanatec Rev (RPM) and Flag (status) LEDs.
    ///
    /// Both Rev and Flag LEDs are controlled via the col03 (64-byte) HID
    /// interface with per-LED RGB565 color, the same protocol used by button
    /// LEDs.  Each LED gets an independent 16-bit color; 0x0000 = off.
    ///
    /// Rev LEDs:  subcmd 0x00, 9 × RGB565 values.
    /// Flag LEDs: subcmd 0x01, 6 × RGB565 values.
    ///
    /// SimHub profile colors are converted directly to RGB565 with
    /// premultiplied alpha (brightness encoded in the color value).
    ///
    /// Layout in the physical mapper:
    ///   Physical 0 .. (RevCount-1)                  = Rev LEDs
    ///   Physical RevCount .. (RevCount+FlagCount-1)  = Flag LEDs
    /// </summary>
    public class FanatecRevFlagLedDriver : ILedButtonsDriver
    {
        private readonly WheelCapabilities _caps;
        private readonly PhysicalMapper _mapper;

        public FanatecRevFlagLedDriver(WheelCapabilities caps)
        {
            _caps = caps ?? throw new ArgumentNullException(nameof(caps));
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
                    device.ClearRevLeds();
                if (_caps.HasFlagLeds)
                    device.ClearFlagLeds();
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn("FanatecRevFlagLedDriver: Clear failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            // Hardware lifetime managed by FanatecPlugin.
        }

        // ── ILedButtonsDriver ────────────────────────────────────────────

        /// <summary>
        /// Called every frame by <c>LedsGenericManager</c>.  Resolves each
        /// physical LED through the mapper, converts to RGB565, then sends
        /// via the col03 (64-byte) interface:
        ///   - Rev LEDs  → SetRevLedState (subcmd 0x00, per-LED RGB565)
        ///   - Flag LEDs → SetFlagLedState (subcmd 0x01, per-LED RGB565)
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
                var revColors = new ushort[FanatecDevice.REV_LED_COUNT];

                for (int i = 0; i < _caps.RevLedCount && i < FanatecDevice.REV_LED_COUNT; i++)
                {
                    Color color = _mapper.GetColor(i, state, ignoreBrightness: false);
                    revColors[i] = ColorHelper.ToRgb565Premultiplied(color);
                }

                ok = device.SetRevLedState(revColors) && ok;
            }

            // ── Flag LEDs ────────────────────────────────────────────
            if (_caps.HasFlagLeds)
            {
                var flagColors = new ushort[FanatecDevice.FLAG_LED_COUNT];
                int flagBase = _caps.RevLedCount;

                for (int i = 0; i < _caps.FlagLedCount && i < FanatecDevice.FLAG_LED_COUNT; i++)
                {
                    Color color = _mapper.GetColor(flagBase + i, state, ignoreBrightness: false);
                    flagColors[i] = ColorHelper.ToRgb565Premultiplied(color);
                }

                ok = device.SetFlagLedState(flagColors) && ok;
            }

            return ok;
        }

        public IPhysicalMapper GetPhysicalMapper()
        {
            return _mapper;
        }

        // ── Mapper construction ──────────────────────────────────────────

        /// <summary>
        /// Builds the physical mapper.  All Rev and Flag LEDs are mapped as
        /// a single contiguous LED strip: physical 0..(Rev+Flag-1).
        /// </summary>
        private static PhysicalMapper BuildMapper(WheelCapabilities caps)
        {
            int total = caps.RevLedCount + caps.FlagLedCount;
            return new PhysicalMapper(new IMap[]
            {
                new LedRangeMap(0, total)
            });
        }
    }
}
