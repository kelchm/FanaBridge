using FanaBridge.Transport;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// The single home for col03 frame-family wire signatures. The transport's
    /// reader thread routes every inbound frame through <see cref="TryClassify"/>;
    /// protocol decoders reuse the same predicates so a frame can never be
    /// classified one way at the pump and another way at the consumer.
    ///
    /// All scans tolerate a leading report-id byte and are allocation-free
    /// (this runs on the reader thread for every inbound report).
    /// </summary>
    internal static class Col03FrameClassifier
    {
        // The shared scan: an 0xFF-prefixed signature pair at offsets 0..2
        // (tolerating a leading report-id byte), bounded so the pair and
        // everything before maxOffset stays inside the frame.
        private static int FindPair(byte[] buf, int len, byte second, int maxOffset)
        {
            for (int i = 0; i <= maxOffset && i + 1 < len; i++)
                if (buf[i] == 0xFF && buf[i + 1] == second)
                    return i;
            return -1;
        }

        /// <summary>
        /// Locates the FF 08 system-report signature (offsets 0..2), requiring the
        /// frame to be long enough to hold the module byte — a shorter FF 08
        /// fragment is NOT an identity report and must not reach the decoder.
        /// Returns the signature offset, or -1.
        /// </summary>
        public static int FindIdentitySignature(byte[] buf, int len)
        {
            int limit = len - (FanatecIdentity.OffModule + 1);
            if (limit > 2) limit = 2;
            return limit < 0 ? -1 : FindPair(buf, len, 0x08, limit);
        }

        /// <summary>
        /// Locates the FF 03 tuning-response signature (offsets 0..2, same
        /// report-id tolerance the router applies). Returns the offset, or -1.
        /// </summary>
        public static int FindTuningSignature(byte[] buf, int len)
            => FindPair(buf, len, 0x03, 2);

        /// <summary>
        /// True for an SRM DE FA <c>0xDD</c> identity reply: 0xDD at offset 0 (raw)
        /// or 1 (behind a report-id) — NOT deeper, so an FF 08 / FF 05 frame is
        /// never mistaken for one — with the 6-byte reply payload present.
        /// </summary>
        public static bool IsSrm(byte[] buf, int len, out int sig)
        {
            for (int i = 0; i <= 1 && i < len; i++)
                if (buf[i] == 0xDD)
                {
                    sig = i;
                    return len >= i + 6;
                }
            sig = -1;
            return false;
        }

        /// <summary>
        /// Locates the FF 05 ITM signature (offsets 0..2, same report-id
        /// tolerance the router applies). Returns the offset, or -1. Consumers
        /// needing a specific subcommand check the byte after the pair.
        /// </summary>
        public static int FindItmSignature(byte[] buf, int len)
            => FindPair(buf, len, 0x05, 2);

        /// <summary>True for an FF 05 ITM subscription/page push (offsets 0..2).</summary>
        public static bool IsItm(byte[] buf, int len)
            => FindItmSignature(buf, len) >= 0;

        /// <summary>True for an FF 03 tuning response (offsets 0..2).</summary>
        public static bool IsTuning(byte[] buf, int len)
            => FindTuningSignature(buf, len) >= 0;

        /// <summary>
        /// Classifies a frame into its family, or returns false for axis/other
        /// frames (which are dropped — same fate they had under the old
        /// signature-skip drains). Precedence mirrors the historical dispatch
        /// order (Identity → Srm → Itm), so pathological frames like
        /// <c>FF DD FF 08 …</c> classify the same way they always routed.
        /// </summary>
        public static bool TryClassify(byte[] buf, int len, out Col03Family family)
        {
            if (buf != null && len > 0)
            {
                if (FindIdentitySignature(buf, len) >= 0) { family = Col03Family.Identity; return true; }
                if (IsSrm(buf, len, out _)) { family = Col03Family.Srm; return true; }
                if (IsItm(buf, len)) { family = Col03Family.Itm; return true; }
                if (IsTuning(buf, len)) { family = Col03Family.Tuning; return true; }
            }
            family = default;
            return false;
        }
    }
}
