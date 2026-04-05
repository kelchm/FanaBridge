using FanaBridge.Adapters;
using Xunit;

namespace FanaBridge.Tests
{
    public class SegmentRenderingTests
    {
        [Theory]
        [InlineData("123", "123")]
        [InlineData("12345", "123")]
        [InlineData("", "")]
        [InlineData("AB", "AB")]
        public void TruncateTo3_TruncatesAtThreeDisplayChars(string input, string expected)
        {
            Assert.Equal(expected, SegmentRendering.TruncateTo3(input));
        }

        [Fact]
        public void TruncateTo3_DotsDoNotCountAsDisplayChars()
        {
            Assert.Equal("1.23", SegmentRendering.TruncateTo3("1.234"));
        }

        [Fact]
        public void TruncateTo3_NullReturnsEmpty()
        {
            Assert.Equal("", SegmentRendering.TruncateTo3(null));
        }

        // ── TruncateTo3 with TruncateLeft ──────────────────────────

        [Theory]
        [InlineData("1.05.3", "05.3")]   // 4 segments → keep rightmost 3
        [InlineData("12.34", "2.34")]    // 4 segments (1, 2., 3, 4) → keep rightmost 3
        [InlineData("12345", "345")]     // 5 segments → keep rightmost 3
        [InlineData("123", "123")]       // fits — unchanged
        [InlineData("1.2", "1.2")]       // fits — unchanged
        [InlineData("", "")]
        public void TruncateTo3_TruncateLeft_KeepsRightmostSegments(string input, string expected)
        {
            Assert.Equal(expected, SegmentRendering.TruncateTo3(input, OverflowStrategy.TruncateLeft));
        }

        [Fact]
        public void TruncateTo3_TruncateLeft_NullReturnsEmpty()
        {
            Assert.Equal("", SegmentRendering.TruncateTo3(null, OverflowStrategy.TruncateLeft));
        }

        // ── ApplyOverflow ──────────────────────────────────────────

        [Fact]
        public void ApplyOverflow_Scroll_ReturnsUnchanged()
        {
            Assert.Equal("1.05.3", SegmentRendering.ApplyOverflow("1.05.3", OverflowStrategy.Scroll));
        }

        [Fact]
        public void ApplyOverflow_TruncateLeft_DropsLeftSegments()
        {
            Assert.Equal("05.3", SegmentRendering.ApplyOverflow("1.05.3", OverflowStrategy.TruncateLeft));
        }

        [Fact]
        public void ApplyOverflow_TruncateRight_DropsRightSegments()
        {
            // "1.05.3" = 4 segments (1., 0, 5., 3) → keep first 3 → "1.05."
            Assert.Equal("1.05.", SegmentRendering.ApplyOverflow("1.05.3", OverflowStrategy.TruncateRight));
        }

        // ── ResolveOverflow ────────────────────────────────────────

        [Theory]
        [InlineData(DisplayFormat.Text, OverflowStrategy.Scroll)]
        [InlineData(DisplayFormat.Time, OverflowStrategy.TruncateLeft)]
        [InlineData(DisplayFormat.Number, OverflowStrategy.TruncateRight)]
        [InlineData(DisplayFormat.Decimal, OverflowStrategy.TruncateRight)]
        [InlineData(DisplayFormat.Gear, OverflowStrategy.TruncateRight)]
        public void ResolveOverflow_Auto_ResolvesPerFormat(DisplayFormat format, OverflowStrategy expected)
        {
            Assert.Equal(expected, SegmentRendering.ResolveOverflow(OverflowStrategy.Auto, format));
        }

        [Theory]
        [InlineData(OverflowStrategy.Scroll)]
        [InlineData(OverflowStrategy.TruncateLeft)]
        [InlineData(OverflowStrategy.TruncateRight)]
        public void ResolveOverflow_Explicit_PassesThrough(OverflowStrategy overflow)
        {
            Assert.Equal(overflow, SegmentRendering.ResolveOverflow(overflow, DisplayFormat.Number));
        }

        // ── EncodeText ─────────────────────────────────────────────

        [Fact]
        public void EncodeText_DotFoldsIntoPredecessor()
        {
            var encoded = SegmentRendering.EncodeText("1.2");
            Assert.Equal(2, encoded.Count);
            Assert.True((encoded[0] & 0x80) != 0); // dot bit set on first char
        }

        [Fact]
        public void EncodeText_EmptyReturnsEmpty()
        {
            Assert.Empty(SegmentRendering.EncodeText(""));
            Assert.Empty(SegmentRendering.EncodeText(null));
        }
    }
}
