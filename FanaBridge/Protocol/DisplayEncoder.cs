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
            byte seg;
            switch (gear)
            {
                case -1: seg = SevenSegment.R; break;
                case 0:  seg = SevenSegment.N; break;
                case 1:  seg = SevenSegment.Digit1; break;
                case 2:  seg = SevenSegment.Digit2; break;
                case 3:  seg = SevenSegment.Digit3; break;
                case 4:  seg = SevenSegment.Digit4; break;
                case 5:  seg = SevenSegment.Digit5; break;
                case 6:  seg = SevenSegment.Digit6; break;
                case 7:  seg = SevenSegment.Digit7; break;
                case 8:  seg = SevenSegment.Digit8; break;
                case 9:  seg = SevenSegment.Digit9; break;
                default: seg = SevenSegment.N; break;
            }

            return SetDisplay(SevenSegment.Blank, seg, SevenSegment.Blank);
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
