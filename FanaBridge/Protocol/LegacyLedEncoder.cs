using System;
using FanaBridge.Transport;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// Encodes and sends LED control reports for legacy Fanatec wheels
    /// that use the col01 (8-byte) protocol instead of col03.
    ///
    /// Supports two LED modes:
    /// <list type="bullet">
    ///   <item><b>Bitmask rev LEDs</b> — 9-bit bitmask controlling individual LED on/off
    ///   state for non-RGB rims (e.g. CSSWBMWV2, CSWRFORM).</item>
    ///   <item><b>RevStripe</b> — single RGB333 color controlling the entire LED strip
    ///   as one unit (CSLRP1X, CSLRP1PS4, CSLRWRC).</item>
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

        private readonly IDeviceTransport _transport;

        // ── Dirty tracking ─────────────────────────────────────────────────
        private ushort _lastBitmask = 0xFFFF; // Sentinel — forces first send
        private ushort _lastRevStripeColor = 0xFFFF;

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
        public bool SetLegacyRevLeds(bool[] onOff)
        {
            if (onOff == null || onOff.Length == 0 || onOff.Length > 9) return false;

            // Build the 9-bit bitmask
            ushort bitmask = 0;
            for (int i = 0; i < onOff.Length; i++)
            {
                if (onOff[i])
                    bitmask |= (ushort)(1 << i);
            }

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
            bool ok = SendLedData((byte)(bitmask & 0xFF), (byte)((bitmask >> 8) & 0xFF));
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
        /// Builds a col01 report in the pooled buffer.
        /// Format: [ReportID, 0xF8, 0x09, subcmd, b4, b5, b6, b7]
        /// ReportID (byte[0]) is overwritten by the transport layer.
        /// </summary>
        private void BuildReport(byte subcmd, byte b4, byte b5, byte b6, byte b7)
        {
            _reportBuf[0] = 0x01; // Placeholder report ID — transport overwrites
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
