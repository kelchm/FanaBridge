using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests.Transport
{
    public class Col03FrameClassifierTests
    {
        // A frame long enough to satisfy the FF 08 length rule (module byte at
        // sig + 0x1F must be inside the frame).
        private static byte[] Frame(int length, params byte[] head)
        {
            var b = new byte[length];
            for (int i = 0; i < head.Length; i++) b[i] = head[i];
            return b;
        }

        private static Col03Family Classify(byte[] frame)
        {
            Assert.True(Col03FrameClassifier.TryClassify(frame, frame.Length, out var family));
            return family;
        }

        // ── Identity (FF 08) ──────────────────────────────────────────────

        [Theory]
        [InlineData(0)] // raw
        [InlineData(1)] // behind a report-id
        [InlineData(2)] // behind two prefix bytes
        public void Ff08_AtToleratedOffsets_IsIdentity(int offset)
        {
            var frame = new byte[64];
            frame[offset] = 0xFF;
            frame[offset + 1] = 0x08;
            Assert.Equal(Col03Family.Identity, Classify(frame));
        }

        [Fact]
        public void Ff08_AtOffset3_IsNotIdentity()
        {
            var frame = Frame(64, 0x00, 0x00, 0x00, 0xFF, 0x08);
            Assert.False(Col03FrameClassifier.TryClassify(frame, frame.Length, out _));
        }

        [Fact]
        public void Ff08_TooShortForModuleByte_IsNotIdentity()
        {
            // Sig at 0 needs len >= 0x20; 0x1F is one byte short.
            var frame = Frame(0x1F, 0xFF, 0x08);
            Assert.False(Col03FrameClassifier.TryClassify(frame, frame.Length, out _));
        }

        [Fact]
        public void FindIdentitySignature_ReturnsOffset()
        {
            var frame = new byte[64];
            frame[1] = 0xFF;
            frame[2] = 0x08;
            Assert.Equal(1, Col03FrameClassifier.FindIdentitySignature(frame, frame.Length));
        }

        // ── SRM (0xDD) ────────────────────────────────────────────────────

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void Dd_AtOffset0Or1_WithPayload_IsSrm(int offset)
        {
            var frame = new byte[16];
            frame[offset] = 0xDD;
            Assert.Equal(Col03Family.Srm, Classify(frame));
        }

        [Fact]
        public void Dd_AtOffset2_IsNotSrm()
        {
            var frame = Frame(16, 0x00, 0x00, 0xDD);
            Assert.False(Col03FrameClassifier.TryClassify(frame, frame.Length, out _));
        }

        [Fact]
        public void Dd_WithTruncatedPayload_IsNotSrm()
        {
            // 0xDD at offset 1 needs len >= 7; 6 is one short.
            var frame = Frame(6, 0x00, 0xDD);
            Assert.False(Col03FrameClassifier.TryClassify(frame, frame.Length, out _));
        }

        [Fact]
        public void Dd_MinimalReply_LengthBoundaryHolds()
        {
            // 0xDD at offset 0 with exactly the 6-byte reply is valid.
            var frame = Frame(6, 0xDD);
            Assert.Equal(Col03Family.Srm, Classify(frame));
        }

        // ── ITM (FF 05) / Tuning (FF 03) ──────────────────────────────────

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Ff05_AtToleratedOffsets_IsItm(int offset)
        {
            var frame = new byte[64];
            frame[offset] = 0xFF;
            frame[offset + 1] = 0x05;
            Assert.Equal(Col03Family.Itm, Classify(frame));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Ff03_AtToleratedOffsets_IsTuning(int offset)
        {
            var frame = new byte[64];
            frame[offset] = 0xFF;
            frame[offset + 1] = 0x03;
            Assert.Equal(Col03Family.Tuning, Classify(frame));
        }

        // ── Precedence and noise ──────────────────────────────────────────

        [Fact]
        public void Precedence_FfDdFf08_ClassifiesAsIdentity()
        {
            // 0xDD at offset 1 AND FF 08 at offset 2 — the historical dispatch
            // checked FF 08 first, so Identity must win.
            var frame = Frame(64, 0xFF, 0xDD, 0xFF, 0x08);
            Assert.Equal(Col03Family.Identity, Classify(frame));
        }

        [Fact]
        public void ShortFf08_WithDd_FallsThroughToSrm()
        {
            // Too short for the FF 08 length rule, but a valid 0xDD reply.
            var frame = Frame(16, 0xDD, 0xFF, 0x08);
            Assert.Equal(Col03Family.Srm, Classify(frame));
        }

        [Fact]
        public void AxisJunk_IsUnclassified()
        {
            var frame = Frame(64, 0x01, 0x80, 0x7F, 0x00, 0x40);
            Assert.False(Col03FrameClassifier.TryClassify(frame, frame.Length, out _));
        }

        [Fact]
        public void NullOrEmpty_IsUnclassified()
        {
            Assert.False(Col03FrameClassifier.TryClassify(null, 0, out _));
            Assert.False(Col03FrameClassifier.TryClassify(new byte[0], 0, out _));
        }
    }
}
