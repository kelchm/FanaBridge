using FanaBridge.Protocol;
using FanaBridge.SegmentDisplay;
using FanaBridge.SegmentDisplay.Rendering;
using FanaBridge.Shared.Conditions;
using Xunit;

namespace FanaBridge.Tests.SegmentDisplay
{
    public class RenderPipelineTests
    {
        // ── Formatters ──────────────────────────────────────────────

        [Theory]
        [InlineData("123.7", "124")]
        [InlineData("0", "0")]
        [InlineData("99.4", "99")]
        public void NumberFormatter_RoundsToInteger(string input, string expected)
        {
            var stage = new NumberFormatter();
            var frame = stage.Process(new SegmentDisplayFrame { Text = input }, Ctx());
            Assert.Equal(expected, frame.Text);
        }

        [Theory]
        [InlineData("42.56", "42.6")]
        [InlineData("7", "7.0")]
        public void DecimalFormatter_OneDecimalPlace(string input, string expected)
        {
            var stage = new DecimalFormatter();
            var frame = stage.Process(new SegmentDisplayFrame { Text = input }, Ctx());
            Assert.Equal(expected, frame.Text);
        }

        [Theory]
        [InlineData("3", "3")]
        [InlineData("0", "N")]
        [InlineData("-1", "R")]
        [InlineData("REVERSE", "R")]
        [InlineData("N", "N")]
        [InlineData("NEUTRAL", "N")]
        public void GearFormatter_MapsGears(string input, string expected)
        {
            var stage = new GearFormatter();
            var frame = stage.Process(new SegmentDisplayFrame { Text = input }, Ctx());
            Assert.Equal(expected, frame.Text);
        }

        [Fact]
        public void GearFormatter_EmptyInput_ReturnsN()
        {
            var stage = new GearFormatter();
            var frame = stage.Process(new SegmentDisplayFrame { Text = "" }, Ctx());
            Assert.Equal("N", frame.Text);
        }

        [Fact]
        public void TextPassthrough_PreservesText()
        {
            var stage = new TextPassthrough();
            var frame = stage.Process(new SegmentDisplayFrame { Text = "PIT" }, Ctx());
            Assert.Equal("PIT", frame.Text);
        }

        [Fact]
        public void TextPassthrough_NullBecomesEmpty()
        {
            var stage = new TextPassthrough();
            var frame = stage.Process(new SegmentDisplayFrame { Text = null }, Ctx());
            Assert.Equal("", frame.Text);
        }

        // ── Alignment ───────────────────────────────────────────────

        [Fact]
        public void AlignStage_RightAlignNumber()
        {
            var stage = new AlignStage(AlignmentType.Auto, SegmentFormat.Number);
            var frame = stage.Process(new SegmentDisplayFrame { Text = "42" }, Ctx());
            Assert.Equal(" 42", frame.Text);
        }

        [Fact]
        public void AlignStage_CenterGear()
        {
            var stage = new AlignStage(AlignmentType.Auto, SegmentFormat.Gear);
            var frame = stage.Process(new SegmentDisplayFrame { Text = "3" }, Ctx());
            Assert.Equal(" 3 ", frame.Text);
        }

        [Fact]
        public void AlignStage_LeftAlignText()
        {
            var stage = new AlignStage(AlignmentType.Auto, SegmentFormat.Text);
            var frame = stage.Process(new SegmentDisplayFrame { Text = "AB" }, Ctx());
            Assert.Equal("AB", frame.Text); // no padding for left-align
        }

        [Fact]
        public void AlignStage_DotFoldsIntoSegment()
        {
            // "1.2" is 2 segments (dot folds into "1"), so right-align should add 1 space
            var stage = new AlignStage(AlignmentType.Right, SegmentFormat.Number);
            var frame = stage.Process(new SegmentDisplayFrame { Text = "1.2" }, Ctx());
            Assert.Equal(" 1.2", frame.Text);
        }

        [Fact]
        public void AlignStage_ThreeSegments_NoPadding()
        {
            var stage = new AlignStage(AlignmentType.Right, SegmentFormat.Number);
            var frame = stage.Process(new SegmentDisplayFrame { Text = "123" }, Ctx());
            Assert.Equal("123", frame.Text);
        }

        // ── Truncation ──────────────────────────────────────────────

        [Fact]
        public void TruncateStage_Right_KeepsFirst3Segments()
        {
            var stage = new TruncateStage(OverflowType.TruncateRight);
            var frame = stage.Process(new SegmentDisplayFrame { Text = "12345" }, Ctx());
            Assert.Equal("123", frame.Text);
        }

        [Fact]
        public void TruncateStage_Left_KeepsLast3Segments()
        {
            var stage = new TruncateStage(OverflowType.TruncateLeft);
            var frame = stage.Process(new SegmentDisplayFrame { Text = "12345" }, Ctx());
            Assert.Equal("345", frame.Text);
        }

        [Fact]
        public void TruncateStage_DotAware_Right()
        {
            // "1.234" = segments: 1.(dot folds), 2, 3, 4 → keep first 3 = "1.23"
            var stage = new TruncateStage(OverflowType.TruncateRight);
            var frame = stage.Process(new SegmentDisplayFrame { Text = "1.234" }, Ctx());
            Assert.Equal("1.23", frame.Text);
        }

        [Fact]
        public void TruncateStage_FitsAlready_Unchanged()
        {
            var stage = new TruncateStage(OverflowType.TruncateRight);
            var frame = stage.Process(new SegmentDisplayFrame { Text = "AB" }, Ctx());
            Assert.Equal("AB", frame.Text);
        }

        // ── Scroll ──────────────────────────────────────────────────

        [Fact]
        public void ScrollStage_ShortText_PassesThrough()
        {
            var stage = new ScrollStage(250);
            var frame = stage.Process(new SegmentDisplayFrame { Text = "AB" }, Ctx(0));
            Assert.Null(frame.Segments); // not set → encode stage handles it
        }

        [Fact]
        public void ScrollStage_LongText_SetsSegments()
        {
            var stage = new ScrollStage(250);
            var frame = stage.Process(new SegmentDisplayFrame { Text = "HELLO" }, Ctx(0));
            Assert.NotNull(frame.Segments);
            Assert.Equal(3, frame.Segments.Length);
        }

        [Fact]
        public void ScrollStage_AdvancesOverTime()
        {
            var stage = new ScrollStage(100);
            var frame1 = stage.Process(new SegmentDisplayFrame { Text = "ABCDE" }, Ctx(0));
            var frame2 = stage.Process(new SegmentDisplayFrame { Text = "ABCDE" }, Ctx(200));

            Assert.NotNull(frame1.Segments);
            Assert.NotNull(frame2.Segments);
            // At different times, segments should differ (scroll has moved)
            Assert.False(
                frame1.Segments[0] == frame2.Segments[0] &&
                frame1.Segments[1] == frame2.Segments[1] &&
                frame1.Segments[2] == frame2.Segments[2]);
        }

        // ── Effects ─────────────────────────────────────────────────

        [Fact]
        public void BlinkStage_OnPhase_NotSuppressed()
        {
            var stage = new BlinkStage(500, 500);
            var frame = stage.Process(new SegmentDisplayFrame { Text = "PIT" }, Ctx(100));
            Assert.False(frame.SuppressOutput);
        }

        [Fact]
        public void BlinkStage_OffPhase_Suppressed()
        {
            var stage = new BlinkStage(500, 500);
            var frame = stage.Process(new SegmentDisplayFrame { Text = "PIT" }, Ctx(600));
            Assert.True(frame.SuppressOutput);
        }

        [Fact]
        public void BlinkStage_CyclesRepeat()
        {
            var stage = new BlinkStage(500, 500);
            // At 1100ms: 1100 % 1000 = 100 → on phase
            var frame = stage.Process(new SegmentDisplayFrame { Text = "PIT" }, Ctx(1100));
            Assert.False(frame.SuppressOutput);
        }

        [Fact]
        public void FlashStage_DuringFlash_AlternatesSuppression()
        {
            var stage = new FlashStage(3, 150);
            // On phase: 0-149ms, off phase: 150-299ms
            var on = stage.Process(new SegmentDisplayFrame { Text = "X" }, Ctx(50));
            var off = stage.Process(new SegmentDisplayFrame { Text = "X" }, Ctx(200));
            Assert.False(on.SuppressOutput);
            Assert.True(off.SuppressOutput);
        }

        [Fact]
        public void FlashStage_AfterCount_Solid()
        {
            var stage = new FlashStage(2, 150);
            // 2 flashes × 300ms cycle = 600ms total flash time
            var frame = stage.Process(new SegmentDisplayFrame { Text = "X" }, Ctx(700));
            Assert.False(frame.SuppressOutput);
        }

        [Fact]
        public void FlashStage_ContinuousCount0_NeverGosSolid()
        {
            var stage = new FlashStage(0, 150);
            // At 10000ms, still flashing: 10000 % 300 = 100 → on phase
            var frame = stage.Process(new SegmentDisplayFrame { Text = "X" }, Ctx(10000));
            Assert.False(frame.SuppressOutput);
            // At 10150ms: 10150 % 300 = 250 → off phase
            var frame2 = stage.Process(new SegmentDisplayFrame { Text = "X" }, Ctx(10150));
            Assert.True(frame2.SuppressOutput);
        }

        // ── Segment Encoding ────────────────────────────────────────

        [Fact]
        public void SegmentEncodeStage_EncodesText()
        {
            var stage = new SegmentEncodeStage();
            var frame = stage.Process(new SegmentDisplayFrame { Text = "123" }, Ctx());
            Assert.NotNull(frame.Segments);
            Assert.Equal(3, frame.Segments.Length);
            Assert.Equal(SevenSegment.Digit1, frame.Segments[0]);
            Assert.Equal(SevenSegment.Digit2, frame.Segments[1]);
            Assert.Equal(SevenSegment.Digit3, frame.Segments[2]);
        }

        [Fact]
        public void SegmentEncodeStage_DotFolds()
        {
            var stage = new SegmentEncodeStage();
            var frame = stage.Process(new SegmentDisplayFrame { Text = "1.2" }, Ctx());
            Assert.NotNull(frame.Segments);
            Assert.Equal((byte)(SevenSegment.Digit1 | SevenSegment.Dot), frame.Segments[0]);
            Assert.Equal(SevenSegment.Digit2, frame.Segments[1]);
        }

        [Fact]
        public void SegmentEncodeStage_SkipsIfSegmentsAlreadySet()
        {
            var existing = new byte[] { 0xAA, 0xBB, 0xCC };
            var stage = new SegmentEncodeStage();
            var frame = stage.Process(new SegmentDisplayFrame { Text = "123", Segments = existing }, Ctx());
            Assert.Same(existing, frame.Segments);
        }

        [Fact]
        public void SegmentEncodeStage_EmptyText_AllBlank()
        {
            var stage = new SegmentEncodeStage();
            var frame = stage.Process(new SegmentDisplayFrame { Text = "" }, Ctx());
            Assert.Equal(SevenSegment.Blank, frame.Segments[0]);
            Assert.Equal(SevenSegment.Blank, frame.Segments[1]);
            Assert.Equal(SevenSegment.Blank, frame.Segments[2]);
        }

        // ── Full Pipeline ───────────────────────────────────────────

        [Fact]
        public void Pipeline_GearLayer_FormatsAndEncodes()
        {
            var layer = new SegmentDisplayLayer
            {
                Content = new PropertyContent
                {
                    PropertyName = "Gear",
                    Format = SegmentFormat.Gear,
                },
                Condition = new AlwaysActive(),
                Alignment = AlignmentType.Auto,
                Overflow = OverflowType.Auto,
            };

            var pipeline = RenderPipeline.ForLayer(layer);
            var frame = pipeline.Process(new SegmentDisplayFrame { Text = "3" }, Ctx());

            Assert.NotNull(frame.Segments);
            Assert.Equal(3, frame.Segments.Length);
            // Gear "3" centered = " 3 " → [Blank, Digit3, Blank]
            Assert.Equal(SevenSegment.Blank, frame.Segments[0]);
            Assert.Equal(SevenSegment.Digit3, frame.Segments[1]);
            Assert.Equal(SevenSegment.Blank, frame.Segments[2]);
        }

        [Fact]
        public void Pipeline_SpeedLayer_RightAligns()
        {
            var layer = new SegmentDisplayLayer
            {
                Content = new PropertyContent
                {
                    PropertyName = "Speed",
                    Format = SegmentFormat.Number,
                },
                Condition = new AlwaysActive(),
                Alignment = AlignmentType.Auto,
                Overflow = OverflowType.Auto,
            };

            var pipeline = RenderPipeline.ForLayer(layer);
            var frame = pipeline.Process(new SegmentDisplayFrame { Text = "42" }, Ctx());

            Assert.NotNull(frame.Segments);
            // "42" right-aligned = " 42" → [Blank, Digit4, Digit2]
            Assert.Equal(SevenSegment.Blank, frame.Segments[0]);
            Assert.Equal(SevenSegment.Digit4, frame.Segments[1]);
            Assert.Equal(SevenSegment.Digit2, frame.Segments[2]);
        }

        [Fact]
        public void Pipeline_WithBlinkEffect_SuppressesDuringOffPhase()
        {
            var layer = new SegmentDisplayLayer
            {
                Content = new FixedTextContent { Text = "PIT" },
                Condition = new AlwaysActive(),
                Alignment = AlignmentType.Center,
                Overflow = OverflowType.Auto,
                Effects = new SegmentEffect[] { new BlinkEffect { OnMs = 500, OffMs = 500 } },
            };

            var pipeline = RenderPipeline.ForLayer(layer);

            // On phase
            var onFrame = pipeline.Process(new SegmentDisplayFrame { Text = "PIT" }, Ctx(100));
            Assert.False(onFrame.SuppressOutput);

            // Off phase
            var offFrame = pipeline.Process(new SegmentDisplayFrame { Text = "PIT" }, Ctx(600));
            Assert.True(offFrame.SuppressOutput);
        }

        // ── Helpers ─────────────────────────────────────────────────

        private static RenderContext Ctx(long elapsedMs = 0)
        {
            return new RenderContext { ElapsedMs = elapsedMs, FrameMs = 16 };
        }
    }
}
