namespace FanaBridge.Protocol
{
    /// <summary>
    /// Central definitions for the two Fanatec HID report framings. Every encoder
    /// used to hand-poke these header bytes; routing through <see cref="Wire"/>
    /// keeps the framing in one place and gives the protocol docs a single thing
    /// to cite.
    ///
    /// The builders write the header into the CALLER'S buffer and return it. They
    /// deliberately do NOT allocate and do NOT clear the payload region — callers
    /// own their pooled buffers and their own zeroing / dirty-tracking policy.
    /// </summary>
    public static class Wire
    {
        /// <summary>col03 reports are 64 bytes, zero-padded; first byte is 0xFF.</summary>
        public const int Col03Length = 64;

        /// <summary>col01 reports are 8 bytes; first byte is the device report ID.</summary>
        public const int Col01Length = 8;

        /// <summary>col03 (64-byte) framing: <c>[0xFF, cmdClass, subcmd, ...]</c>.</summary>
        public static class Col03
        {
            /// <summary>Fixed first byte of every col03 report.</summary>
            public const byte ReportId = 0xFF;

            /// <summary>Command class 0x01 — LED control.</summary>
            public const byte LedClass = 0x01;
            /// <summary>Command class 0x03 — tuning menu.</summary>
            public const byte TuningClass = 0x03;
            /// <summary>Command class 0x08 — system report (identity).</summary>
            public const byte SystemClass = 0x08;
        }

        /// <summary>col01 (8-byte) framing: <c>[ReportId, 0xF8, 0x09, b3, ...]</c>.</summary>
        public static class Col01
        {
            /// <summary>Report ID byte FanaBridge uses for col01 commands.</summary>
            public const byte ReportId = 0x01;
            /// <summary>Marks a Fanatec control command.</summary>
            public const byte FanatecMarker = 0xF8;
            /// <summary>General control command class.</summary>
            public const byte ControlClass = 0x09;

            /// <summary>
            /// byte[3] value selecting the "extended operations" group, whose
            /// actual operation lives in byte[4] (7-segment display, CBP, tuning
            /// ack). Direct subcommands (LED control) put their subcmd in byte[3]
            /// instead.
            /// </summary>
            public const byte GroupExtended = 0x01;
        }

        /// <summary>
        /// Writes the col03 header <c>[0xFF, cmdClass, subcmd]</c> into
        /// <paramref name="buf"/>[0..2] and returns <paramref name="buf"/>. Leaves
        /// buf[3..] untouched and allocates nothing.
        /// </summary>
        public static byte[] BeginCol03(byte[] buf, byte cmdClass, byte subcmd)
        {
            buf[0] = Col03.ReportId;
            buf[1] = cmdClass;
            buf[2] = subcmd;
            return buf;
        }

        /// <summary>
        /// Writes the col01 header <c>[0x01, 0xF8, 0x09, b3]</c> into
        /// <paramref name="buf"/>[0..3] and returns <paramref name="buf"/>.
        /// <paramref name="b3"/> is the direct subcommand or
        /// <see cref="Col01.GroupExtended"/>. Leaves buf[4..] untouched and
        /// allocates nothing.
        /// </summary>
        public static byte[] BeginCol01(byte[] buf, byte b3)
        {
            buf[0] = Col01.ReportId;
            buf[1] = Col01.FanatecMarker;
            buf[2] = Col01.ControlClass;
            buf[3] = b3;
            return buf;
        }
    }
}
