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
        public void ColorToRgbBools_BelowLowestIntensityStep_IsOff()
        {
            // A=1 is 0.4% brightness — below the hardware's lowest step, so the LED
            // is off rather than lit at full red. Same cut-off legacyRevOnOff uses,
            // so both col01 paths agree on when an LED counts as lit.
            var c = System.Drawing.Color.FromArgb(1, 255, 0, 0);
            Assert.Equal(0, ColorHelper.ColorToIntensity(c));

            var (r, g, b) = ColorHelper.ColorToRgbBools(c);
            Assert.False(r);
            Assert.False(g);
            Assert.False(b);
        }

        [Fact]
        public void ColorToRgbBools_TraceChannel_DoesNotShiftTheHue()
        {
            // Thresholding each channel at "greater than zero" turned visually pure
            // red into yellow, and lit a channel for any value a gradient passed
            // through.
            var (r, g, b) = ColorHelper.ColorToRgbBools(System.Drawing.Color.FromArgb(255, 255, 1, 0));
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

        // ── RGB333 palette snapping (#76) ──────────────────────────────
        //
        // The col01 0x08 payload is ambiguous: RGB333 (0,4,0) is byte-identical to
        // the "LED 0 only" on/off pattern, and the driver stack reacts to it by
        // switching the rim out of color mode. Snapping to the eight saturated
        // values makes that encoding unreachable.

        // packed RGB333 for each saturated combination
        private const ushort PalOff = 0x0000;
        private const ushort PalRed = 0x3800;
        private const ushort PalGreen = 0xC001;
        private const ushort PalBlue = 0x0700;
        private const ushort PalCyan = 0xC701;
        private const ushort PalMagenta = 0x3F00;
        private const ushort PalYellow = 0xF801;
        private const ushort PalWhite = 0xFF01;
        private const ushort Trigger = 0x0001;

        private static readonly ushort[] Palette =
        {
            PalOff, PalRed, PalGreen, PalBlue, PalCyan, PalMagenta, PalYellow, PalWhite
        };

        [Theory]
        [InlineData(255, 0, 0, PalRed)]
        [InlineData(0, 255, 0, PalGreen)]
        [InlineData(0, 0, 255, PalBlue)]
        [InlineData(0, 255, 255, PalCyan)]
        [InlineData(255, 0, 255, PalMagenta)]
        [InlineData(255, 255, 0, PalYellow)]
        [InlineData(255, 255, 255, PalWhite)]
        [InlineData(0, 0, 0, PalOff)]
        public void RgbToRgb333Palette_SaturatedInputs_RoundTrip(byte r, byte g, byte b, ushort expected)
        {
            Assert.Equal(expected, ColorHelper.RgbToRgb333Palette(r, g, b));
        }

        [Fact]
        public void RgbToRgb333Palette_MidDarkGreen_NeverProducesTrigger()
        {
            // The whole 128-159 green band quantizes to g3=4 without snapping.
            for (byte g = 128; g <= 159; g++)
                Assert.NotEqual(Trigger, ColorHelper.RgbToRgb333Palette(0, g, 0));
        }

        [Fact]
        public void RgbToRgb333Palette_NearBlackSingleChannel_KeepsItsHue()
        {
            // max=1: a truncated max/2 threshold of 0 counts the zero channels as lit
            // and turns this white.
            Assert.Equal(PalRed, ColorHelper.RgbToRgb333Palette(1, 0, 0));
            Assert.Equal(PalBlue, ColorHelper.RgbToRgb333Palette(0, 0, 1));
        }

        [Fact]
        public void RgbToRgb333Palette_ChannelAtExactlyHalf_IsLit()
        {
            // 10 is exactly half of 20 — inclusive, and not subject to truncation.
            Assert.Equal(PalYellow, ColorHelper.RgbToRgb333Palette(20, 10, 0));
            Assert.Equal(PalRed, ColorHelper.RgbToRgb333Palette(20, 9, 0));
        }

        [Theory]
        // Thresholding after premultiplication made these three disagree: red,
        // yellow, red — same color, three neighbouring alphas.
        [InlineData(255)]
        [InlineData(248)]
        [InlineData(240)]
        public void ToRgb333PalettePremultiplied_NearBlackHue_DoesNotDependOnAlpha(int alpha)
        {
            var c = System.Drawing.Color.FromArgb(alpha, 20, 9, 0);
            Assert.Equal(PalRed, ColorHelper.ToRgb333PalettePremultiplied(c));
        }

        [Fact]
        public void ToRgb333PalettePremultiplied_HueSurvivesDimming()
        {
            // Amber: thresholding channels against a fixed level would drop green
            // before red and turn this red, then black, as brightness falls.
            for (int alpha = 255; alpha >= 32; alpha -= 1)
            {
                var c = System.Drawing.Color.FromArgb(alpha, 255, 200, 0);
                Assert.Equal(PalYellow, ColorHelper.ToRgb333PalettePremultiplied(c));
            }
        }

        [Fact]
        public void ToRgb333PalettePremultiplied_UnlitColorIsOff()
        {
            // Matches the rule legacyRevOnOff uses, so both col01 channels agree.
            var c = System.Drawing.Color.FromArgb(4, 255, 255, 255);
            Assert.Equal(0, ColorHelper.ColorToIntensity(c));
            Assert.Equal(PalOff, ColorHelper.ToRgb333PalettePremultiplied(c));
        }

        [Fact]
        public void ToRgb333PalettePremultiplied_OnlyEverEmitsPaletteValues()
        {
            foreach (var alpha in new[] { 255, 200, 160, 128, 96, 64, 32, 16, 8, 0 })
                for (int r = 0; r < 256; r += 5)
                    for (int g = 0; g < 256; g += 5)
                        for (int b = 0; b < 256; b += 5)
                        {
                            var v = ColorHelper.ToRgb333PalettePremultiplied(
                                System.Drawing.Color.FromArgb(alpha, r, g, b));
                            Assert.Contains(v, Palette);
                        }
        }
    }
}
