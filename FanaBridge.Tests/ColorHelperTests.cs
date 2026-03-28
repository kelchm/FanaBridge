using FanaBridge.Protocol;
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

        // ── RGB333 tests ───────────────────────────────────────────────

        [Fact]
        public void RgbToRgb333_Black_IsZero()
        {
            Assert.Equal((ushort)0, ColorHelper.RgbToRgb333(0, 0, 0));
        }

        [Theory]
        // From protocol doc: Red = data_lo=0x00, data_hi=0x38
        [InlineData(255, 0, 0, 0x3800)]
        // From protocol doc: Green = data_lo=0x01, data_hi=0xC0
        [InlineData(0, 255, 0, 0xC001)]
        // From protocol doc: Blue = data_lo=0x00, data_hi=0x07
        [InlineData(0, 0, 255, 0x0700)]
        // From protocol doc: Yellow = data_lo=0x01, data_hi=0xF8
        [InlineData(255, 255, 0, 0xF801)]
        public void RgbToRgb333_KnownColors_MatchProtocolDoc(byte r, byte g, byte b, ushort expected)
        {
            Assert.Equal(expected, ColorHelper.RgbToRgb333(r, g, b));
        }
        // ── ColorToRgbBools tests ──────────────────────────────────────

        [Fact]
        public void ColorToRgbBools_OpaqueWhite_AllTrue()
        {
            var c = System.Drawing.Color.FromArgb(255, 255, 255, 255);
            var (r, g, b) = ColorHelper.ColorToRgbBools(c);
            Assert.True(r);
            Assert.True(g);
            Assert.True(b);
        }

        [Fact]
        public void ColorToRgbBools_OpaqueBlack_AllFalse()
        {
            var c = System.Drawing.Color.FromArgb(255, 0, 0, 0);
            var (r, g, b) = ColorHelper.ColorToRgbBools(c);
            Assert.False(r);
            Assert.False(g);
            Assert.False(b);
        }

        [Fact]
        public void ColorToRgbBools_FullyTransparent_AllFalse()
        {
            var c = System.Drawing.Color.FromArgb(0, 255, 255, 255);
            var (r, g, b) = ColorHelper.ColorToRgbBools(c);
            Assert.False(r);
            Assert.False(g);
            Assert.False(b);
        }

        [Fact]
        public void ColorToRgbBools_LowAlpha_BelowRoundingThreshold_False()
        {
            // R=1, A=1 → premultiplied ≈ 0.004, rounds to 0 → false
            var c = System.Drawing.Color.FromArgb(1, 1, 0, 0);
            var (r, g, b) = ColorHelper.ColorToRgbBools(c);
            Assert.False(r);
            Assert.False(g);
            Assert.False(b);
        }

        [Fact]
        public void ColorToRgbBools_LowAlpha_AboveRoundingThreshold_True()
        {
            // R=255, A=1 → premultiplied = 1.0, rounds to 1 → true
            var c = System.Drawing.Color.FromArgb(1, 255, 0, 0);
            var (r, g, b) = ColorHelper.ColorToRgbBools(c);
            Assert.True(r);
            Assert.False(g);
            Assert.False(b);
        }

        [Fact]
        public void ColorToRgbBools_HalfAlpha_SelectiveChannels()
        {
            // A=128 → a ≈ 0.502; R=255 → 128.0 (true), G=0 → 0 (false), B=200 → 100.4 (true)
            var c = System.Drawing.Color.FromArgb(128, 255, 0, 200);
            var (r, g, b) = ColorHelper.ColorToRgbBools(c);
            Assert.True(r);
            Assert.False(g);
            Assert.True(b);
        }
    }
}
