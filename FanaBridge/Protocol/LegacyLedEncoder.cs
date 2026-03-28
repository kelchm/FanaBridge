using System;
using FanaBridge.Transport;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// Encodes and sends LED control reports for legacy Fanatec wheels
    /// that use the col01 (8-byte) protocol instead of col03.
    ///
    /// Supports four LED modes:
    /// <list type="bullet">
    ///   <item><b>Bitmask rev LEDs</b> — 9-bit bitmask controlling individual LED on/off
    ///   state for non-RGB rims (e.g. CSSWBMWV2, CSWRFORM). Bitmask is packed in
    ///   RGB333 bit order per the SDK's FUN_1002c240.</item>
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
        private const byte SUBCMD_GLOBAL_ENABLE = 0x02;
        private const byte SUBCMD_REVSTRIPE_ENABLE = 0x06;
        private const byte SUBCMD_LED_DATA = 0x08;
        private const byte SUBCMD_LED_RGB_DATA = 0x0A;
        private const byte SUBCMD_FLAG_RGB_DATA = 0x0B;

        private readonly IDeviceTransport _transport;

        // ── Dirty tracking ─────────────────────────────────────────────────
        private ushort _lastBitmask = 0xFFFF; // Sentinel — forces first send
        private ushort _lastRevStripeColor = 0xFFFF;
        private uint _lastRev3BitPacked = 0xFFFFFFFF; // Sentinel for per-LED 3-bit rev
        private uint _lastFlag3BitPacked = 0xFFFFFFFF; // Sentinel for per-LED 3-bit flag

        // ── State tracking for enable sequences ────────────────────────────
        private bool _globalEnabled;
        private bool _revStripeEnabled;

        // ── Pooled report buffer ───────────────────────────────────────────
        private readonly byte[] _reportBuf = new byte[REPORT_LENGTH];

        public LegacyLedEncoder(IDeviceTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <summary>
        /// Sets bitmask rev LED state for non-RGB rims.
        /// Each element in <paramref name="onOff"/> maps to one LED (index 0 = LED 0).
        /// Sends a global enable on first use, then sends the bitmask via subcmd 0x08.
        /// Skips the HID write when the bitmask hasn't changed.
        /// </summary>
        /// <param name="onOff">Per-LED on/off state. Max 9 LEDs.</param>
        public bool SetLegacyRevOnOff(bool[] onOff)
        {
            if (onOff == null || onOff.Length == 0 || onOff.Length > 9) return false;

            // Pack 9-LED bitmask in RGB333 bit order (matches SDK FUN_1002c240):
            //   byte[4] = LED0 (bit 0)
            //   byte[5] = LED1(bit7) | LED2(bit6) | ... | LED8(bit0)
            // The SDK reverses the LED array then packs in groups of 8.
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

            // Ensure global rev LEDs are enabled
            if (!_globalEnabled)
            {
                if (!SendGlobalEnable(true))
                    return false;
                _globalEnabled = true;
            }

            // Send bitmask: [RID, F8, 09, 08, data_lo, data_hi, 00, 00]
            bool ok = SendLedData(dataLo, dataHi);
            if (ok)
                _lastBitmask = bitmask;
            return ok;
        }

        /// <summary>
        /// Sets the RevStripe color for single-strip rims.
        /// The <paramref name="rgb333"/> value is a packed RGB333 color from
        /// <see cref="ColorHelper.RgbToRgb333"/> (high byte = data_hi, low byte = data_lo).
        /// Sends the enable sequence on first use.
        /// Skips the HID write when the color hasn't changed.
        /// </summary>
        public bool SetRevStripeColor(ushort rgb333)
        {
            // Dirty check
            if (rgb333 == _lastRevStripeColor)
                return true;

            // Ensure RevStripe is enabled (inverted semantics: 0x00 = ON)
            if (!_revStripeEnabled)
            {
                if (!SendRevStripeEnable(true))
                    return false;
                if (!SendGlobalEnable(true))
                    return false;
                _revStripeEnabled = true;
                _globalEnabled = true;
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
        /// Sends a global enable on first use, then packs 27 bits into 4 data bytes.
        /// Skips the HID write when the packed state hasn't changed.
        /// </summary>
        /// <param name="rgbBools">Flat array: [LED0.R, LED0.G, LED0.B, LED1.R, ...]. Max 27 bytes.</param>
        public bool SetLegacyRev3Bit(byte[] rgbBools)
        {
            if (rgbBools == null || rgbBools.Length == 0 || rgbBools.Length > 27) return false;

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

            // Ensure global rev LEDs are enabled
            if (!_globalEnabled)
            {
                if (!SendGlobalEnable(true))
                    return false;
                _globalEnabled = true;
            }

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

            // Ensure global rev LEDs are enabled
            if (!_globalEnabled)
            {
                if (!SendGlobalEnable(true))
                    return false;
                _globalEnabled = true;
            }

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
        /// Clears all legacy LEDs — turns off bitmask LEDs or RevStripe.
        /// </summary>
        public void Clear()
        {
            // Send all-off bitmask / zero color
            SendLedData(0x00, 0x00);

            if (_globalEnabled)
            {
                SendGlobalEnable(false);
                _globalEnabled = false;
            }

            if (_revStripeEnabled)
            {
                SendRevStripeEnable(false);
                _revStripeEnabled = false;
            }

            ForceDirty();
        }

        /// <summary>
        /// Marks state as dirty so the next send always writes to hardware.
        /// Call when the physical wheel changes.
        /// </summary>
        public void ForceDirty()
        {
            _lastBitmask = 0xFFFF;
            _lastRevStripeColor = 0xFFFF;
            _lastRev3BitPacked = 0xFFFFFFFF;
            _lastFlag3BitPacked = 0xFFFFFFFF;
            _globalEnabled = false;
            _revStripeEnabled = false;
        }

        // ── Low-level report senders ───────────────────────────────────────

        /// <summary>
        /// Sends the Rev LED Global On/Off command.
        /// [RID, F8, 09, 02, enable, 00, 00, 00]
        /// </summary>
        private bool SendGlobalEnable(bool enable)
        {
            BuildReport(SUBCMD_GLOBAL_ENABLE, enable ? (byte)0x01 : (byte)0x00, 0x00, 0x00, 0x00);
            return _transport.SendCol01(_reportBuf);
        }

        /// <summary>
        /// Sends the RevStripe Enable/Disable command.
        /// Inverted semantics: 0x00 = ON, 0x01 = OFF.
        /// [RID, F8, 09, 06, enable, 00, 00, 00]
        /// </summary>
        private bool SendRevStripeEnable(bool enable)
        {
            BuildReport(SUBCMD_REVSTRIPE_ENABLE, enable ? (byte)0x00 : (byte)0x01, 0x00, 0x00, 0x00);
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
            // wheelbases. The SDK's transport (FUN_10014d70) overwrites byte[0]
            // with a device-specific report ID, but FanaBridge sends as-is.
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
