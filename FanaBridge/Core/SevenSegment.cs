namespace FanaBridge
{
    /// <summary>
    /// 7-segment display encoding table.
    /// Matches the Linux hid-fanatecff kernel driver segbits[] and
    /// the Fanatec SDK FSCmdLedSevenSegmentDisplayThreeDigits.
    ///
    ///   seg 0
    ///  ───────
    /// │       │
    /// 5       1
    /// │       │
    ///  ───────  seg 6
    /// │       │
    /// 4       2
    /// │       │
    ///  ───────  • seg 7 (dot)
    ///   seg 3
    /// </summary>
    public static class SevenSegment
    {
        // Digits
        public const byte Digit0 = 0x3F;
        public const byte Digit1 = 0x06;
        public const byte Digit2 = 0x5B;
        public const byte Digit3 = 0x4F;
        public const byte Digit4 = 0x66;
        public const byte Digit5 = 0x6D;
        public const byte Digit6 = 0x7D;
        public const byte Digit7 = 0x07;
        public const byte Digit8 = 0x7F;
        public const byte Digit9 = 0x6F;

        // Symbols
        public const byte Dot    = 0x80;
        public const byte Blank  = 0x00;
        public const byte Dash   = 0x40;
        public const byte Under  = 0x08;

        // Letters (7-segment approximations)
        public const byte A = 0x77;
        public const byte B = 0x7C;
        public const byte C = 0x58;
        public const byte D = 0x5E;
        public const byte E = 0x79;
        public const byte F = 0x71;
        public const byte G = 0x3D;
        public const byte H = 0x76;
        public const byte I = 0x06;
        public const byte J = 0x0E;
        public const byte K = 0x75;
        public const byte L = 0x38;
        public const byte M = 0x37;
        public const byte N = 0x54;
        public const byte O = 0x5C;
        public const byte P = 0x73;
        public const byte Q = 0x67;
        public const byte R = 0x50;
        public const byte S = 0x6D;
        public const byte T = 0x78;
        public const byte U = 0x3E;
        public const byte V = 0x18;
        public const byte W = 0x7E;
        public const byte X = 0x76;
        public const byte Y = 0x6E;
        public const byte Z = 0x5B;

        /// <summary>
        /// Converts an ASCII character to its 7-segment byte code.
        /// </summary>
        public static byte CharToSegment(char ch)
        {
            char upper = char.ToUpper(ch);

            // Digits
            if (upper >= '0' && upper <= '9')
            {
                return GetDigitSegment(upper - '0');
            }

            // Letters
            switch (upper)
            {
                case 'A': return A;
                case 'B': return B;
                case 'C': return C;
                case 'D': return D;
                case 'E': return E;
                case 'F': return F;
                case 'G': return G;
                case 'H': return H;
                case 'I': return I;
                case 'J': return J;
                case 'K': return K;
                case 'L': return L;
                case 'M': return M;
                case 'N': return N;
                case 'O': return O;
                case 'P': return P;
                case 'Q': return Q;
                case 'R': return R;
                case 'S': return S;
                case 'T': return T;
                case 'U': return U;
                case 'V': return V;
                case 'W': return W;
                case 'X': return X;
                case 'Y': return Y;
                case 'Z': return Z;

                // Symbols
                case '-': return Dash;
                case '_': return Under;
                case '.': return Dot;
                case ',': return Dot;
                case ' ': return Blank;

                default: return Blank;
            }
        }

        /// <summary>
        /// Returns the 7-segment code for a single digit (0-9).
        /// </summary>
        public static byte GetDigitSegment(int digit)
        {
            switch (digit)
            {
                case 0: return Digit0;
                case 1: return Digit1;
                case 2: return Digit2;
                case 3: return Digit3;
                case 4: return Digit4;
                case 5: return Digit5;
                case 6: return Digit6;
                case 7: return Digit7;
                case 8: return Digit8;
                case 9: return Digit9;
                default: return Blank;
            }
        }
    }
}
