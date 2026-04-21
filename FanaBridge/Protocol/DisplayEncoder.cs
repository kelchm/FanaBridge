using System;
using FanaBridge.Transport;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// Encodes and sends display control reports for the Fanatec
    /// 3-digit 7-segment display via the col01 HID interface.
    /// </summary>
    public class DisplayEncoder
    {
        private const int REPORT_LENGTH = 8;

        private readonly IDeviceTransport _transport;

        // ── Pooled buffers — avoid per-frame heap allocations ────────────
        private readonly byte[] _reportBuf = new byte[REPORT_LENGTH];
        private readonly byte[] _textSegs = new byte[3];

        public DisplayEncoder(IDeviceTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <summary>
        /// Sets the 3-digit 7-segment display.
        /// Matches the Linux kernel driver ftec_set_display() protocol.
        /// </summary>
        public bool SetDisplay(byte seg1, byte seg2, byte seg3)
        {
            _reportBuf[0] = 0x01;  // Report ID
            _reportBuf[1] = 0xF8;
            _reportBuf[2] = 0x09;
            _reportBuf[3] = 0x01;
            _reportBuf[4] = 0x02;
            _reportBuf[5] = seg1;
            _reportBuf[6] = seg2;
            _reportBuf[7] = seg3;

            return _transport.SendCol01(_reportBuf);
        }

        /// <summary>
        /// Blank the display.
        /// </summary>
        public bool ClearDisplay()
        {
            return SetDisplay(SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank);
        }

        /// <summary>
        /// Display a gear number: -1=R, 0=N, 1-9.
        /// </summary>
        public bool DisplayGear(int gear)
        {
            return SetDisplay(SevenSegment.Blank, GearToSegment(gear), SevenSegment.Blank);
        }

        /// <summary>
        /// Display a gear number with optional upshift brackets: -1=R, 0=N, 1-9.
        /// When <paramref name="showBrackets"/> is true, renders [n] using the outer digit positions.
        /// </summary>
        public bool DisplayGearBracketed(int gear, bool showBrackets)
        {
            byte left  = showBrackets ? SevenSegment.BracketLeft  : SevenSegment.Blank;
            byte right = showBrackets ? SevenSegment.BracketRight : SevenSegment.Blank;
            return SetDisplay(left, GearToSegment(gear), right);
        }

        private static byte GearToSegment(int gear)
        {
            switch (gear)
            {
                case -1: return SevenSegment.R;
                case 0:  return SevenSegment.N;
                case 1:  return SevenSegment.Digit1;
                case 2:  return SevenSegment.Digit2;
                case 3:  return SevenSegment.Digit3;
                case 4:  return SevenSegment.Digit4;
                case 5:  return SevenSegment.Digit5;
                case 6:  return SevenSegment.Digit6;
                case 7:  return SevenSegment.Digit7;
                case 8:  return SevenSegment.Digit8;
                case 9:  return SevenSegment.Digit9;
                default: return SevenSegment.N;
            }
        }

        /// <summary>
        /// Display a speed value (0-999) on the 3-digit display.
        /// </summary>
        public bool DisplaySpeed(int speed)
        {
            if (speed < 0 || speed > 999) speed = 0;

            byte seg1 = SevenSegment.GetDigitSegment(speed / 100);
            byte seg2 = SevenSegment.GetDigitSegment((speed / 10) % 10);
            byte seg3 = SevenSegment.GetDigitSegment(speed % 10);

            return SetDisplay(seg1, seg2, seg3);
        }

        /// <summary>
        /// Display up to 3 characters of text. Dots/commas are folded onto predecessor.
        /// Uses a pooled buffer to avoid per-call allocations.
        /// </summary>
        public bool DisplayText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return ClearDisplay();

            int segCount = 0;
            _textSegs[0] = SevenSegment.Blank;
            _textSegs[1] = SevenSegment.Blank;
            _textSegs[2] = SevenSegment.Blank;

            foreach (char ch in text)
            {
                if ((ch == '.' || ch == ',') && segCount > 0)
                {
                    _textSegs[segCount - 1] |= SevenSegment.Dot;
                }
                else
                {
                    _textSegs[segCount] = SevenSegment.CharToSegment(ch);
                    segCount++;
                }

                if (segCount >= 3) break;
            }

            return SetDisplay(_textSegs[0], _textSegs[1], _textSegs[2]);
        }
    }
}
