using FanaBridge.Protocol;
using FanaBridge.UI.Display;
using Xunit;

namespace FanaBridge.Tests.UI
{
    /// <summary>
    /// Keeper pins for the surviving seven-segment face bit helpers.
    /// </summary>
    public class SevenSegmentFaceRenderTests
    {
        [Fact]
        public void SegmentAndDotBits_MatchSevenSegmentEncoding()
        {
            byte eight = SevenSegment.Digit8;
            for (int i = 0; i < 7; i++)
                Assert.True(SevenSegmentFaceRender.IsSegmentLit(eight, i));
            Assert.False(SevenSegmentFaceRender.IsDotLit(eight));
            Assert.False(SevenSegmentFaceRender.IsSegmentLit(eight, -1));
            Assert.False(SevenSegmentFaceRender.IsSegmentLit(eight, 7));

            byte withDot = (byte)(SevenSegment.Digit1 | SevenSegment.Dot);
            Assert.True(SevenSegmentFaceRender.IsDotLit(withDot));
            Assert.True(SevenSegmentFaceRender.IsSegmentLit(withDot, 1));
            Assert.True(SevenSegmentFaceRender.IsSegmentLit(withDot, 2));
            Assert.False(SevenSegmentFaceRender.IsSegmentLit(withDot, 0));
        }
    }
}
