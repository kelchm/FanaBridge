namespace FanaBridge
{
    /// <summary>
    /// Encoder operating mode for Fanatec button modules.
    /// Sent as byte 19 in the col03 tuning configuration report (cmd 0x03).
    /// </summary>
    public enum EncoderMode : byte
    {
        /// <summary>Relative / incremental — sends CW/CCW pulses.</summary>
        Encoder = 0x00,
        /// <summary>Absolute — sends position as individual button presses (pulse).</summary>
        Pulse = 0x01,
        /// <summary>Absolute — holds button for current position (constant).</summary>
        Constant = 0x02,
        /// <summary>Firmware auto-selects between modes based on interaction.</summary>
        Auto = 0x03,
    }
}
