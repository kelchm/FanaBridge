namespace FanaBridge.Protocol
{
    /// <summary>
    /// Shared bit-packing for the col01 per-LED 3-bit color reports. A flat array
    /// of per-channel on/off bytes is packed LSB-first into a 32-bit word (bit i =
    /// element i, set when the element is nonzero). Used by both the rev (subcmd
    /// 0x0A, up to 27 bits) and flag (subcmd 0x0B, up to 18 bits) encoders, which
    /// then splat the word into wire bytes.
    /// </summary>
    internal static class BitPacking
    {
        /// <summary>
        /// Packs <paramref name="values"/> LSB-first: bit i is set when
        /// <c>values[i] != 0</c>. The caller bounds the length (at most 32) and
        /// splits the returned word into wire bytes.
        /// </summary>
        public static uint PackLsbFirst(byte[] values)
        {
            uint packed = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] != 0)
                    packed |= 1u << i;
            }
            return packed;
        }
    }
}
