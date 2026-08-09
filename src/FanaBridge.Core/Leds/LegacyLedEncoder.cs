using System;
using FanaBridge.Core.Transport;

namespace FanaBridge.Core.Leds
{
    /// <summary>
    /// Encodes and sends LED control reports for legacy Fanatec wheels
    /// that use the col01 (8-byte) protocol instead of col03.
    ///
    /// Supports four LED modes:
    /// <list type="bullet">
    ///   <item><b>Bitmask rev LEDs</b> — 9-bit bitmask controlling individual LED on/off
    ///   state for non-RGB rims (e.g. CSSWBMWV2, CSWRFORM). Bitmask is packed in
    ///   the wire's RGB333 bit order (see <see cref="SetLegacyRevOnOff"/>).</item>
    ///   <item><b>RevStripe</b> — single RGB333 color controlling the entire LED strip
    ///   as one unit (CSLRP1X, CSLRP1PS4, CSLRWRC).</item>
    ///   <item><b>3-bit rev LEDs</b> — per-LED 1-bit-per-channel color (7 colors + off)
    ///   via subcmd 0x0A for RGB-capable rims on col01.</item>
    ///   <item><b>3-bit flag LEDs</b> — per-LED 1-bit-per-channel color (7 colors + off)
    ///   via subcmd 0x0B for flag LEDs on col01.</item>
    /// </list>
    /// </summary>
    public class LegacyLedEncoder
    {
        // ── col01 report constants ─────────────────────────────────────────
        private const int REPORT_LENGTH = 8;

        // Subcmd bytes (byte[3] in the report)
        //
        // 0x02 is deliberately absent: it addresses a separate white LED on the rim,
        // not the rev LEDs, so it has no business in an LED write path.
        private const byte SUBCMD_REVSTRIPE_ENABLE = 0x06;
        private const byte SUBCMD_REV_LED_MODE = 0x07;
        private const byte SUBCMD_LED_DATA = 0x08;
        private const byte SUBCMD_LED_RGB_DATA = 0x0A;
        private const byte SUBCMD_FLAG_RGB_DATA = 0x0B;

        private readonly IDeviceTransport _transport;

        // ── Dirty tracking ─────────────────────────────────────────────────
        private ushort _lastBitmask = 0xFFFF; // Sentinel — forces first send
        private ushort _lastRevStripeColor = 0xFFFF;
        private uint _lastRev3BitPacked = 0xFFFFFFFF; // Sentinel for per-LED 3-bit rev
        private uint _lastFlag3BitPacked = 0xFFFFFFFF; // Sentinel for per-LED 3-bit flag
        // Set by ForceDirty (any thread), consumed at the top of each send path on
        // the sender's own thread — a ForceDirty landing mid-send must not be
        // overwritten by that send's re-latch of the old state.
        private volatile bool _forceDirty;

        // ── State tracking ─────────────────────────────────────────────────
        // 0x06 is a stored enable preference, while 0x07 selects how the next 0x08
        // payload is interpreted. A bitmask write invalidates only the latter;
        // ForceDirty invalidates both when the physical connection may have changed.
        private bool _revStripeEnabled;
        private bool _revStripeColorModeAsserted;

        // ── Pooled report buffer ───────────────────────────────────────────
        private readonly byte[] _reportBuf = new byte[REPORT_LENGTH];

        public LegacyLedEncoder(IDeviceTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <summary>
        /// Sets bitmask rev LED state for non-RGB rims.
        /// Each element in <paramref name="onOff"/> maps to one LED (index 0 = LED 0).
        /// Sends the bitmask via subcmd 0x08.
        /// Skips the HID write when the bitmask hasn't changed.
        /// </summary>
        /// <param name="onOff">Per-LED on/off state. Max 9 LEDs.</param>
        public bool SetLegacyRevOnOff(bool[] onOff)
        {
            if (onOff == null || onOff.Length == 0 || onOff.Length > 9) return false;

            ConsumePendingForceDirty();

            // Pack the 9-LED bitmask in the wire's RGB333 bit order:
            //   byte[4] = LED0 (bit 0)
            //   byte[5] = LED1(bit7) | LED2(bit6) | ... | LED8(bit0)
            // LED0 sits alone in the low byte; LEDs 1-8 pack into the high byte
            // most-significant-first.
            byte dataLo = (byte)(onOff[0] ? 0x01 : 0x00);
            byte dataHi = 0;
            for (int i = 1; i < onOff.Length && i <= 8; i++)
            {
                if (onOff[i])
                    dataHi |= (byte)(1 << (8 - i));
            }

            ushort bitmask = (ushort)((dataHi << 8) | dataLo);

            // Dirty check
            if (bitmask == _lastBitmask)
                return true;

            // A bitmask write can itself make the driver stack switch the rim into
            // pattern mode, and it overwrites the shared 0x08 payload. The next
            // stripe write therefore has to restore both color mode and color, even
            // when the requested color matches the one sent before this pattern.
            _revStripeColorModeAsserted = false;
            _lastRevStripeColor = 0xFFFF;

            // Send bitmask: [RID, F8, 09, 08, data_lo, data_hi, 00, 00]
            bool ok = SendLedData(dataLo, dataHi);
            if (ok)
                _lastBitmask = bitmask;
            return ok;
        }

        /// <summary>
        /// Sets the RevStripe color for single-strip rims.
        /// The <paramref name="rgb333"/> value is a packed RGB333 color from
        /// <see cref="ColorHelper.RgbToRgb333Palette"/> (high byte = data_hi, low byte = data_lo).
        /// Asserts color mode on first use.
        /// Skips the HID write when the color hasn't changed.
        /// </summary>
        /// <remarks>
        /// Values outside the eight-color palette are snapped here rather than
        /// trusted from the caller. RGB333 <c>0x0001</c> is byte-identical to the
        /// "LED 0 only" pattern and makes the driver stack switch the rim out of
        /// color mode — so the guarantee has to hold at the wire boundary, not only
        /// in whichever caller happens to be wired up today.
        /// </remarks>
        public bool SetRevStripeColor(ushort rgb333)
        {
            ConsumePendingForceDirty();

            rgb333 = SnapToPalette(rgb333);

            // The cached color is meaningful only while color mode is still known
            // to hold. Bitmask writes invalidate both facts above.
            if (rgb333 == _lastRevStripeColor && _revStripeColorModeAsserted)
                return true;

            // Entering color mode or writing color data invalidates the cached
            // bitmask state that shares this 0x08 payload. Do this before sending
            // so partial success cannot leave a later bitmask shortcut armed.
            _lastBitmask = 0xFFFF;

            // Enable the stripe once per connection, and assert color mode whenever
            // a bitmask write has made its interpretation of 0x08 uncertain.
            //
            // 0x06 is a stored preference rather than a per-frame control, so it is
            // sent once and never with the disable value — a user whose stripe is
            // switched off has no other way to turn it back on from here, but
            // flipping it off again on every clear would be trampling their setting.
            //
            // 0x07 recovers a rim another application left interpreting 0x08 as an
            // on/off pattern; nothing in a color write alone undoes that.
            if (!_revStripeEnabled)
            {
                if (!SendRevStripeEnable())
                    return false;
                _revStripeEnabled = true;
            }

            if (!_revStripeColorModeAsserted)
            {
                if (!SendRevLedMode(false))
                    return false;
                _revStripeColorModeAsserted = true;
            }

            byte dataHi = (byte)((rgb333 >> 8) & 0xFF);
            byte dataLo = (byte)(rgb333 & 0xFF);

            bool ok = SendLedData(dataLo, dataHi);
            if (ok)
                _lastRevStripeColor = rgb333;
            return ok;
        }

        /// <summary>
        /// Sets per-LED 3-bit color state for rev LEDs via col01 subcmd 0x0A.
        /// Each LED gets 3 consecutive bytes (R, G, B) in <paramref name="rgbBools"/>,
        /// where any nonzero value means "on" for that channel (1 bit per channel =
        /// 7 colors + off). Max 9 LEDs (27 bytes).
        /// Packs 27 bits into 4 data bytes.
        /// Skips the HID write when the packed state hasn't changed.
        /// </summary>
        /// <param name="rgbBools">Flat array: [LED0.R, LED0.G, LED0.B, LED1.R, ...]. Max 27 bytes.</param>
        public bool SetLegacyRev3Bit(byte[] rgbBools)
        {
            if (rgbBools == null || rgbBools.Length == 0 || rgbBools.Length > 27) return false;

            ConsumePendingForceDirty();

            // Pack 27 booleans into 4 bytes, LSB-first
            uint packed = 0;
            for (int i = 0; i < rgbBools.Length; i++)
            {
                if (rgbBools[i] != 0)
                    packed |= 1u << i;
            }

            // Dirty check
            if (packed == _lastRev3BitPacked)
                return true;

            // Send: [RID, F8, 09, 0A, data0, data1, data2, data3]
            bool ok = SendLedRgbData(
                (byte)(packed & 0xFF),
                (byte)((packed >> 8) & 0xFF),
                (byte)((packed >> 16) & 0xFF),
                (byte)((packed >> 24) & 0xFF));
            if (ok)
                _lastRev3BitPacked = packed;
            return ok;
        }

        /// <summary>
        /// Sets per-LED 3-bit color state for flag LEDs via col01 subcmd 0x0B.
        /// Each LED gets 3 consecutive bytes (R, G, B) in <paramref name="rgbBools"/>,
        /// where any nonzero value means "on" for that channel (1 bit per channel =
        /// 7 colors + off). Max 6 LEDs (18 bytes).
        /// Packs 18 bits into 4 data bytes, LSB-first (same layout as subcmd 0x0A
        /// but for 6 flag LEDs instead of 9 rev LEDs).
        /// Skips the HID write when the packed state hasn't changed.
        /// </summary>
        /// <param name="rgbBools">Flat array: [LED0.R, LED0.G, LED0.B, LED1.R, ...]. Max 18 bytes.</param>
        public bool SetLegacyFlag3Bit(byte[] rgbBools)
        {
            if (rgbBools == null || rgbBools.Length == 0 || rgbBools.Length > 18) return false;

            ConsumePendingForceDirty();

            // Pack 18 booleans into 4 bytes, LSB-first
            uint packed = 0;
            for (int i = 0; i < rgbBools.Length; i++)
            {
                if (rgbBools[i] != 0)
                    packed |= 1u << i;
            }

            // Dirty check
            if (packed == _lastFlag3BitPacked)
                return true;

            // Send: [RID, F8, 09, 0B, d0, d1, d2, d3]
            bool ok = SendFlagRgbData(
                (byte)(packed & 0xFF),
                (byte)((packed >> 8) & 0xFF),
                (byte)((packed >> 16) & 0xFF),
                (byte)((packed >> 24) & 0xFF));
            if (ok)
                _lastFlag3BitPacked = packed;
            return ok;
        }

        /// <summary>
        /// Clears every legacy LED channel this encoder has driven.
        /// </summary>
        public void Clear()
        {
            // Each channel has to be zeroed on its own subcommand — a 0x08 frame does
            // nothing for LEDs addressed via 0x0A or 0x0B, which would otherwise stay
            // lit at their last color. Untouched channels are left alone so we don't
            // write to hardware the profile never claimed.
            //
            // Nothing else is sent: turning LEDs off is not a reason to touch the
            // rim's white LED or its stored preferences.
            if (_lastBitmask != 0xFFFF || _lastRevStripeColor != 0xFFFF)
                SendLedData(0x00, 0x00);

            if (_lastRev3BitPacked != 0xFFFFFFFF)
                SendLedRgbData(0x00, 0x00, 0x00, 0x00);

            if (_lastFlag3BitPacked != 0xFFFFFFFF)
                SendFlagRgbData(0x00, 0x00, 0x00, 0x00);

            ForceDirty();
        }

        /// <summary>
        /// Marks state as dirty so the next send always writes to hardware.
        /// Call when the physical wheel changes. Safe from any thread: sets a
        /// flag consumed on the sender's own thread.
        /// </summary>
        public void ForceDirty()
        {
            _forceDirty = true;
        }

        // Applies a pending ForceDirty on the sender's thread, before the dirty
        // check. If ForceDirty lands mid-send, the flag stays set for the next
        // send — a forced resend is never lost.
        private void ConsumePendingForceDirty()
        {
            if (!_forceDirty) return;
            _forceDirty = false;
            _lastBitmask = 0xFFFF;
            _lastRevStripeColor = 0xFFFF;
            _lastRev3BitPacked = 0xFFFFFFFF;
            _lastFlag3BitPacked = 0xFFFFFFFF;
            _revStripeEnabled = false;
            _revStripeColorModeAsserted = false;
        }

        // ── Low-level report senders ───────────────────────────────────────

        /// <summary>
        /// Forces a packed RGB333 value onto the eight-color palette, unpacking it
        /// and re-snapping rather than trusting the caller.
        /// </summary>
        private static ushort SnapToPalette(ushort rgb333)
        {
            int dataHi = (rgb333 >> 8) & 0xFF;
            int dataLo = rgb333 & 0xFF;

            // Inverse of ColorHelper.RgbToRgb333
            int g3 = ((dataLo & 0x01) << 2) | ((dataHi >> 6) & 0x03);
            int r3 = (dataHi >> 3) & 0x07;
            int b3 = dataHi & 0x07;

            return ColorHelper.RgbToRgb333Palette(
                (byte)(r3 * 255 / 7), (byte)(g3 * 255 / 7), (byte)(b3 * 255 / 7));
        }

        /// <summary>
        /// Enables the RevStripe. Inverted semantics — <c>0x00</c> means on. There is
        /// deliberately no disable: this is stored state, not a per-frame control.
        /// [RID, F8, 09, 06, 00, 00, 00, 00]
        /// </summary>
        private bool SendRevStripeEnable()
        {
            BuildReport(SUBCMD_REVSTRIPE_ENABLE, 0x00, 0x00, 0x00, 0x00);
            return _transport.SendCol01(_reportBuf);
        }

        /// <summary>
        /// Selects how the rim interprets subsequent <c>0x08</c> writes.
        /// <c>false</c> = the payload is an RGB333 color, <c>true</c> = it is a
        /// per-LED on/off pattern.
        /// [RID, F8, 09, 07, mode, 00, 00, 00]
        /// </summary>
        private bool SendRevLedMode(bool bitmask)
        {
            BuildReport(SUBCMD_REV_LED_MODE, bitmask ? (byte)0x01 : (byte)0x00, 0x00, 0x00, 0x00);
            return _transport.SendCol01(_reportBuf);
        }

        /// <summary>
        /// Sends LED data via subcmd 0x08.
        /// [RID, F8, 09, 08, data_lo, data_hi, 00, 00]
        /// </summary>
        private bool SendLedData(byte dataLo, byte dataHi)
        {
            BuildReport(SUBCMD_LED_DATA, dataLo, dataHi, 0x00, 0x00);
            return _transport.SendCol01(_reportBuf);
        }

        /// <summary>
        /// Sends per-LED RGB data for rev LEDs via subcmd 0x0A.
        /// [RID, F8, 09, 0A, d0, d1, d2, d3]
        /// </summary>
        private bool SendLedRgbData(byte d0, byte d1, byte d2, byte d3)
        {
            BuildReport(SUBCMD_LED_RGB_DATA, d0, d1, d2, d3);
            return _transport.SendCol01(_reportBuf);
        }

        /// <summary>
        /// Sends per-LED RGB data for flag LEDs via subcmd 0x0B.
        /// [RID, F8, 09, 0B, d0, d1, d2, d3]
        /// </summary>
        private bool SendFlagRgbData(byte d0, byte d1, byte d2, byte d3)
        {
            BuildReport(SUBCMD_FLAG_RGB_DATA, d0, d1, d2, d3);
            return _transport.SendCol01(_reportBuf);
        }

        /// <summary>
        /// Builds a col01 report in the pooled buffer.
        /// Format: [ReportID, 0xF8, 0x09, subcmd, b4, b5, b6, b7]
        /// </summary>
        private void BuildReport(byte subcmd, byte b4, byte b5, byte b6, byte b7)
        {
            // Report ID 0x01 is correct for col01 commands on current-generation
            // wheelbases. Some wheelbases expect a device-specific report ID in
            // byte[0], but FanaBridge sends 0x01 as-is.
            _reportBuf[0] = 0x01;
            _reportBuf[1] = 0xF8;
            _reportBuf[2] = 0x09;
            _reportBuf[3] = subcmd;
            _reportBuf[4] = b4;
            _reportBuf[5] = b5;
            _reportBuf[6] = b6;
            _reportBuf[7] = b7;
        }
    }
}
