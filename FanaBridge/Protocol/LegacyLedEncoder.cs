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
        private const byte SUBCMD_LED_RGB_DATA = 0x0A;

        private readonly IDeviceTransport _transport;

        // ── Dirty tracking ─────────────────────────────────────────────────
        private ushort _lastBitmask = 0xFFFF; // Sentinel — forces first send
        private ushort _lastRevStripeColor = 0xFFFF;
        private uint _lastRgbPacked = 0xFFFFFFFF; // Sentinel for per-LED RGB
        private uint _lastGlobalCombined = 0xFFFFFFFF; // Sentinel for global color (stores ushort, sentinel forces first send)

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
        /// Sets per-LED RGB boolean state for RGB-capable rims via col01 subcmd 0x0A.
        /// Each LED gets 3 consecutive bytes (R, G, B) in <paramref name="rgbBools"/>,
        /// where any nonzero value means "on" for that channel. Max 9 LEDs (27 bytes).
        /// Sends a global enable on first use, then packs 27 bits into 4 data bytes.
        /// Skips the HID write when the packed state hasn't changed.
        /// </summary>
        /// <param name="rgbBools">Flat array: [LED0.R, LED0.G, LED0.B, LED1.R, ...]. Max 27 bytes.</param>
        public bool SetLegacyRevRgb(byte[] rgbBools)
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
            if (packed == _lastRgbPacked)
                return true;

            // Ensure global rev LEDs are enabled
            if (!_globalEnabled)
            {
                SimHub.Logging.Current.Info("LegacyLedEncoder: Sending global enable for RevRgb path");
                if (!SendGlobalEnable(true))
                    return false;
                _globalEnabled = true;
            }

            // Send: [RID, F8, 09, 0A, data0, data1, data2, data3]
            byte d0 = (byte)(packed & 0xFF);
            byte d1 = (byte)((packed >> 8) & 0xFF);
            byte d2 = (byte)((packed >> 16) & 0xFF);
            byte d3 = (byte)((packed >> 24) & 0xFF);
            SimHub.Logging.Current.Info(
                $"LegacyLedEncoder: SendRgb subcmd=0x0A data=[{d0:X2} {d1:X2} {d2:X2} {d3:X2}] packed=0x{packed:X8}");
            bool ok = SendLedRgbData(d0, d1, d2, d3);
            if (ok)
                _lastRgbPacked = packed;
            return ok;
        }

        /// <summary>
        /// Sets per-LED on/off via subcmd 0x08 with the bitmask packed in RGB333
        /// bit order. The PBME firmware interprets subcmd 0x08 data using the RGB333
        /// layout as a 9-bit bitmask, with fixed per-LED colors:
        /// <list type="bullet">
        ///   <item>LEDs 0-2 → Green channel bits (G2,G1,G0) → Yellow</item>
        ///   <item>LEDs 3-5 → Red channel bits (R2,R1,R0) → Red</item>
        ///   <item>LEDs 6-8 → Blue channel bits (B2,B1,B0) → Blue</item>
        /// </list>
        /// </summary>
        /// <param name="rgb333">Ignored — color is fixed by LED position on PBME.</param>
        /// <param name="onOff">Per-LED on/off state. Max 9 LEDs.</param>
        public bool SetLegacyRevGlobal(ushort rgb333, bool[] onOff)
        {
            if (onOff == null || onOff.Length == 0 || onOff.Length > 9) return false;

            // Pack the 9-LED bitmask into RGB333 bit positions:
            //   G channel: LED0→G2(MSB), LED1→G1, LED2→G0(LSB)
            //   R channel: LED3→R2(MSB), LED4→R1, LED5→R0(LSB)
            //   B channel: LED6→B2(MSB), LED7→B1, LED8→B0(LSB)
            int g3 = (onOff.Length > 0 && onOff[0] ? 4 : 0)
                   | (onOff.Length > 1 && onOff[1] ? 2 : 0)
                   | (onOff.Length > 2 && onOff[2] ? 1 : 0);
            int r3 = (onOff.Length > 3 && onOff[3] ? 4 : 0)
                   | (onOff.Length > 4 && onOff[4] ? 2 : 0)
                   | (onOff.Length > 5 && onOff[5] ? 1 : 0);
            int b3 = (onOff.Length > 6 && onOff[6] ? 4 : 0)
                   | (onOff.Length > 7 && onOff[7] ? 2 : 0)
                   | (onOff.Length > 8 && onOff[8] ? 1 : 0);

            byte dataHi = (byte)(((g3 & 0x03) << 6) | (r3 << 3) | b3);
            byte dataLo = (byte)((g3 >> 2) & 0x01);
            ushort packed = (ushort)((dataHi << 8) | dataLo);

            // Dirty check
            if (packed == _lastGlobalCombined)
                return true;

            // Ensure global rev LEDs are enabled
            if (!_globalEnabled)
            {
                SimHub.Logging.Current.Info("LegacyLedEncoder: Sending global enable for RevGlobal path");
                if (!SendGlobalEnable(true))
                    return false;
                _globalEnabled = true;
            }

            SimHub.Logging.Current.Info(
                $"LegacyLedEncoder: SendGlobal rgb333=[{dataLo:X2} {dataHi:X2}] G={g3} R={r3} B={b3}");
            bool ok = SendLedData(dataLo, dataHi);
            if (ok)
                _lastGlobalCombined = packed;
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
            _lastRgbPacked = 0xFFFFFFFF;
            _lastGlobalCombined = 0xFFFFFFFF;
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
        /// Sends per-LED RGB data via subcmd 0x0A.
        /// [RID, F8, 09, 0A, d0, d1, d2, d3]
        /// </summary>
        private bool SendLedRgbData(byte d0, byte d1, byte d2, byte d3)
        {
            BuildReport(SUBCMD_LED_RGB_DATA, d0, d1, d2, d3);
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
