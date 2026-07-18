using FanaBridge.Display.Legacy;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using FanaBridge.UI.Display;
using Xunit;

namespace FanaBridge.Tests.UI
{
    /// <summary>
    /// Pure 3-char face helpers: segment/dot bits and Virtual-pages LIVE preview bytes
    /// (formatter → effect clock). The WPF face control is construction-only smoke.
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

            byte withDot = (byte)(SevenSegment.Digit1 | SevenSegment.Dot);
            Assert.True(SevenSegmentFaceRender.IsDotLit(withDot));
            Assert.True(SevenSegmentFaceRender.IsSegmentLit(withDot, 1)); // digit 1 uses bits 1+2
            Assert.False(SevenSegmentFaceRender.IsSegmentLit(withDot, 0));
        }

        [Fact]
        public void PreviewSegments_NullScreen_IsBlank()
        {
            Assert.Equal(SevenSegmentFaceRender.BlankFrame(),
                SevenSegmentFaceRender.PreviewSegments(null));
        }

        [Fact]
        public void PreviewSegments_Text_RendersViaFormatter()
        {
            var screen = new LegacyScreen
            {
                Id = "p",
                Text = "PIT",
                ContentKind = LegacyContentKind.Text,
                Effect = LegacyEffect.None,
            };
            Assert.Equal(LegacyValueFormatter.Render("PIT"),
                SevenSegmentFaceRender.PreviewSegments(screen));
        }

        [Fact]
        public void PreviewSegments_Speed_UsesDemoValue()
        {
            var screen = new LegacyScreen
            {
                Id = "s",
                ContentKind = LegacyContentKind.Speed,
            };
            string expected = LegacyValueFormatter.FormatSpeed(SevenSegmentFaceRender.DemoSpeed);
            Assert.Equal(LegacyValueFormatter.Render(expected),
                SevenSegmentFaceRender.PreviewSegments(screen));
        }

        [Fact]
        public void PreviewSegments_Blink_OffPhaseIsBlank()
        {
            var screen = new LegacyScreen
            {
                Id = "b",
                Text = "HI",
                ContentKind = LegacyContentKind.Text,
                Effect = LegacyEffect.Blink,
            };
            var on = SevenSegmentFaceRender.PreviewSegments(screen, 0);
            var off = SevenSegmentFaceRender.PreviewSegments(
                screen, LegacyEffectClock.BlinkHalfPeriodMs);
            Assert.Equal(LegacyValueFormatter.Render("HI"), on);
            Assert.Equal(SevenSegmentFaceRender.BlankFrame(), off);
        }

        [Fact]
        public void PreviewSegments_PropertyWithoutSource_Blanks()
        {
            var screen = new LegacyScreen
            {
                Id = "p",
                ContentKind = LegacyContentKind.Property,
            };
            Assert.Equal(SevenSegmentFaceRender.BlankFrame(),
                SevenSegmentFaceRender.PreviewSegments(screen));
        }

        [Fact]
        public void PreviewSegments_PropertyWithSource_UsesDemoReader()
        {
            var screen = new LegacyScreen
            {
                Id = "p",
                ContentKind = LegacyContentKind.Property,
                Source = new PropertySpec
                {
                    Kind = PropertyKind.BuiltIn,
                    Name = BuiltInProperties.Fuel,
                },
            };
            // Demo property is 88 → "088".
            Assert.Equal(
                LegacyValueFormatter.Render(
                    LegacyValueFormatter.FormatProperty(
                        // Same clamp/pad the demo reader feeds.
                        new Always(SevenSegmentFaceRender.DemoProperty),
                        screen.Source)),
                SevenSegmentFaceRender.PreviewSegments(screen));
        }

        private sealed class Always : IPropertyReader
        {
            private readonly double _value;
            public Always(double value) => _value = value;
            public bool TryGetNumber(PropertySpec spec, out double value)
            {
                value = _value;
                return true;
            }
            public bool TryGetBool(PropertySpec spec, out bool value)
            {
                value = false;
                return false;
            }
        }
    }
}
