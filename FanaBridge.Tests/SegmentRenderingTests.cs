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
