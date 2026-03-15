using FanaBridge.Core;
using Xunit;

namespace FanaBridge.Tests
{
    public class ColorHelperTests
    {
        [Fact]
        public void RgbToRgb565_Black_IsZero()
        {
            Assert.Equal(0, ColorHelper.RgbToRgb565(0, 0, 0));
        }

        [Fact]
        public void RgbToRgb565_White_AllBitsSet()
        {
            // Pure white: 5 red bits + 6 green bits + 5 blue bits = 0xFFFF
            Assert.Equal(0xFFFF, ColorHelper.RgbToRgb565(255, 255, 255));
        }

        [Theory]
        [InlineData(255, 0, 0, 0x001F)] // red   -> low 5 bits
        [InlineData(0, 255, 0, 0x07E0)] // green -> middle 6 bits
        [InlineData(0, 0, 255, 0xF800)] // blue  -> high 5 bits
        public void RgbToRgb565_PrimaryColors_CorrectBitPacking(byte r, byte g, byte b, ushort expected)
        {
            Assert.Equal(expected, ColorHelper.RgbToRgb565(r, g, b));
        }
    }
}
