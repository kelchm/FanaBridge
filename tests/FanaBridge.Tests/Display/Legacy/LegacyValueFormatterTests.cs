using System.Collections.Generic;
using FanaBridge.Display.Legacy;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests.Display.Legacy
{
    /// <summary>
    /// Byte-level goldens for <see cref="LegacyValueFormatter"/>, in the style of
    /// <c>LegacyDisplayDriverTests</c>: every assertion is against
    /// <see cref="SevenSegment"/> constants. The four absorbed driver modes must match
    /// that driver's read/render semantics (SpeedLocal, clamps, ParseGear, brackets).
    /// </summary>
    public class LegacyValueFormatterTests
    {
        private static (byte, byte, byte) Segs(string text)
        {
            var f = LegacyValueFormatter.Render(text);
            return (f[0], f[1], f[2]);
        }

        // ── Render (text windowing) ──────────────────────────────────────

        [Fact]
        public void Render_Empty_IsBlankFrame()
        {
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                Segs(""));
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                Segs(null));
        }

        [Fact]
        public void Render_ThreeChars_LeftAligned()
        {
            Assert.Equal(
                (SevenSegment.P, SevenSegment.I, SevenSegment.T),
                Segs("PIT"));
        }

        [Fact]
        public void Render_ShortText_PadsWithBlank()
        {
            Assert.Equal(
                (SevenSegment.Digit4, SevenSegment.Blank, SevenSegment.Blank),
                Segs("4"));
        }

        [Fact]
        public void Render_LongText_WindowsFirstThree()
        {
            Assert.Equal(
                (SevenSegment.H, SevenSegment.E, SevenSegment.L),
                Segs("HELLO"));
        }

        [Fact]
        public void Render_FoldsDotOntoPrevious()
        {
            // "-1.5" → dash, digit1|dot, digit5
            Assert.Equal(
                (SevenSegment.Dash, (byte)(SevenSegment.Digit1 | SevenSegment.Dot), SevenSegment.Digit5),
                Segs("-1.5"));
        }

        // ── Speed (DisplaySpeed parity: zero-padded) ─────────────────────

        [Fact]
        public void Speed_UsesRoundedSpeedLocal_ZeroPadded()
        {
            // 88 → "088" → Digit0, Digit8, Digit8 (matches DisplayEncoder.DisplaySpeed)
            Assert.Equal("088", LegacyValueFormatter.FormatSpeed(88));
            Assert.Equal(
                (SevenSegment.Digit0, SevenSegment.Digit8, SevenSegment.Digit8),
                Segs(LegacyValueFormatter.FormatSpeed(88)));
        }

        [Fact]
        public void Speed_RoundsToNearestInt()
        {
            Assert.Equal("100", LegacyValueFormatter.FormatSpeed(99.6));
        }

        [Theory]
        [InlineData(-5, "000")]
        [InlineData(1234, "999")]
        public void Speed_ClampsToRange(double speedLocal, string expected)
        {
            Assert.Equal(expected, LegacyValueFormatter.FormatSpeed(speedLocal));
        }

        // ── Gear (DisplayGear parity: centered glyph) ────────────────────

        [Theory]
        [InlineData("R", SevenSegment.R)]
        [InlineData("N", SevenSegment.N)]
        [InlineData("3", SevenSegment.Digit3)]
        [InlineData("reverse", SevenSegment.R)]
        [InlineData("", SevenSegment.N)]
        [InlineData("garbage", SevenSegment.N)]
        public void Gear_RendersCenteredGlyph(string gear, byte expectedMiddle)
        {
            string text = LegacyValueFormatter.FormatGear(gear);
            Assert.Equal(
                (SevenSegment.Blank, expectedMiddle, SevenSegment.Blank),
                Segs(text));
        }

        [Theory]
        [InlineData("R", -1)]
        [InlineData("N", 0)]
        [InlineData("3", 3)]
        [InlineData("REVERSE", -1)]
        [InlineData("neutral", 0)]
        [InlineData("", 0)]
        [InlineData("garbage", 0)]
        public void ParseGear_MatchesDriver(string gear, int expected)
            => Assert.Equal(expected, LegacyValueFormatter.ParseGear(gear));

        // ── GearAndSpeed (clock-injected overlay) ────────────────────────

        [Fact]
        public void GearAndSpeed_ShowsGear_InsideOverlayWindow()
        {
            // nowMs - gearChangedAtMs < 2000 → gear
            string text = LegacyValueFormatter.FormatGearAndSpeed(
                "5", speedLocal: 200, gearChangedAtMs: 1000, nowMs: 2500);
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Digit5, SevenSegment.Blank),
                Segs(text));
        }

        [Fact]
        public void GearAndSpeed_ShowsSpeed_AfterOverlayExpires()
        {
            string text = LegacyValueFormatter.FormatGearAndSpeed(
                "5", speedLocal: 200, gearChangedAtMs: 1000, nowMs: 3000);
            Assert.Equal("200", text);
            Assert.Equal(
                (SevenSegment.Digit2, SevenSegment.Digit0, SevenSegment.Digit0),
                Segs(text));
        }

        [Fact]
        public void GearAndSpeed_OverlayBoundary_IsExclusiveOfDuration()
        {
            // exactly GearOverlayMs after change → speed (driver uses UtcNow < until)
            string atBoundary = LegacyValueFormatter.FormatGearAndSpeed(
                "5", 88, gearChangedAtMs: 0, nowMs: LegacyValueFormatter.GearOverlayMs);
            Assert.Equal("088", atBoundary);

            string justInside = LegacyValueFormatter.FormatGearAndSpeed(
                "5", 88, gearChangedAtMs: 0, nowMs: LegacyValueFormatter.GearOverlayMs - 1);
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Digit5, SevenSegment.Blank),
                Segs(justInside));
        }

        // ── GearBrackets ─────────────────────────────────────────────────

        [Fact]
        public void GearBrackets_NoBrackets_BelowRedline()
        {
            string text = LegacyValueFormatter.FormatGearBrackets("3", rpms: 6000, redLineReached: 0);
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Digit3, SevenSegment.Blank),
                Segs(text));
        }

        [Fact]
        public void GearBrackets_ShowsBrackets_AtRedline()
        {
            string text = LegacyValueFormatter.FormatGearBrackets("3", rpms: 8000, redLineReached: 1);
            Assert.Equal("[3]", text);
            Assert.Equal(
                (SevenSegment.BracketLeft, SevenSegment.Digit3, SevenSegment.BracketRight),
                Segs(text));
        }

        [Fact]
        public void GearBrackets_NoBrackets_WhenRpmsZero()
        {
            // redline flag set but no rpms → brackets require both
            string text = LegacyValueFormatter.FormatGearBrackets("3", rpms: 0, redLineReached: 1);
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Digit3, SevenSegment.Blank),
                Segs(text));
        }

        [Fact]
        public void GearBrackets_ReverseWithBrackets()
        {
            string text = LegacyValueFormatter.FormatGearBrackets("R", rpms: 100, redLineReached: 1);
            Assert.Equal(
                (SevenSegment.BracketLeft, SevenSegment.R, SevenSegment.BracketRight),
                Segs(text));
        }

        // ── Rpm / Position / Fuel ────────────────────────────────────────

        [Fact]
        public void Rpm_DividesByTen_Clamps()
        {
            Assert.Equal("080", LegacyValueFormatter.FormatRpm(800));   // 800/10
            Assert.Equal("999", LegacyValueFormatter.FormatRpm(20000));
            Assert.Equal("000", LegacyValueFormatter.FormatRpm(-10));
            Assert.Equal(
                (SevenSegment.Digit0, SevenSegment.Digit8, SevenSegment.Digit0),
                Segs(LegacyValueFormatter.FormatRpm(800)));
        }

        [Fact]
        public void Position_Clamps()
        {
            Assert.Equal("012", LegacyValueFormatter.FormatPosition(12));
            Assert.Equal("999", LegacyValueFormatter.FormatPosition(1500));
            Assert.Equal("000", LegacyValueFormatter.FormatPosition(-1));
        }

        [Fact]
        public void Fuel_RoundsAndClamps()
        {
            Assert.Equal("042", LegacyValueFormatter.FormatFuel(41.6));
            Assert.Equal("999", LegacyValueFormatter.FormatFuel(10000));
            Assert.Equal("000", LegacyValueFormatter.FormatFuel(-3));
        }

        // ── Property ─────────────────────────────────────────────────────

        private sealed class DictReader : IPropertyReader
        {
            private readonly Dictionary<string, double> _nums;
            public DictReader(Dictionary<string, double> nums) => _nums = nums;

            public bool TryGetNumber(PropertySpec spec, out double value)
            {
                if (spec != null && spec.Name != null && _nums.TryGetValue(spec.Name, out value))
                    return true;
                value = 0;
                return false;
            }

            public bool TryGetBool(PropertySpec spec, out bool value)
            {
                value = false;
                return false;
            }
        }

        [Fact]
        public void Property_ReadsNumber_Clamps()
        {
            var reader = new DictReader(new Dictionary<string, double> { ["Fuel"] = 12.4 });
            var source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = "Fuel" };
            Assert.Equal("012", LegacyValueFormatter.FormatProperty(reader, source));
        }

        [Fact]
        public void Property_Missing_ReturnsNull()
        {
            var reader = new DictReader(new Dictionary<string, double>());
            var source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = "Fuel" };
            Assert.Null(LegacyValueFormatter.FormatProperty(reader, source));
            Assert.Null(LegacyValueFormatter.FormatProperty(null, source));
            Assert.Null(LegacyValueFormatter.FormatProperty(reader, null));
        }

        // ── Text passthrough ─────────────────────────────────────────────

        [Fact]
        public void FormatText_Passthrough()
        {
            Assert.Equal("PIT", LegacyValueFormatter.FormatText("PIT"));
            Assert.Equal("", LegacyValueFormatter.FormatText(null));
        }
    }
}
