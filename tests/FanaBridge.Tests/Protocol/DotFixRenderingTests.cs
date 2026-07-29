using System;
using FanaBridge.Display;
using FanaBridge.Display.Legacy;
using FanaBridge.Display.Schema2;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests.Protocol
{
    /// <summary>
    /// Pinned owner fixtures for the segment text-path fold law (DECISIONS §7e
    /// REALIGNMENT #3). Fit/scroll and scroll windows are decided on FOLDED position
    /// count from <see cref="SevenSegment.EncodeWithDots"/>, never raw char length.
    /// Numeric goldens live elsewhere and must stay byte-unchanged.
    /// </summary>
    public class DotFixRenderingTests
    {
        private static byte D(byte seg) => (byte)(seg | SevenSegment.Dot);
        private static byte BlankDot => SevenSegment.Dot;

        // ── EncodeWithDots fold law ──────────────────────────────────────

        [Fact]
        public void DotFix_TrailingThird_FoldsToThreePositions()
        {
            // "A.b.c." = 3 folded positions → FITS; trailing dot on c is kept.
            var encoded = SevenSegment.EncodeWithDots("A.b.c.");
            Assert.Equal(3, encoded.Count);
            Assert.Equal(new byte[]
            {
                D(SevenSegment.A),
                D(SevenSegment.B),
                D(SevenSegment.C),
            }, encoded);
        }

        [Fact]
        public void DotFix_TrailingThird_RenderAndNoneEffect_NoScroll()
        {
            var frame = LegacyValueFormatter.Render("A.b.c.");
            Assert.Equal(new byte[]
            {
                D(SevenSegment.A),
                D(SevenSegment.B),
                D(SevenSegment.C),
            }, frame);

            // Scroll is inert when folded count ≤ 3.
            var scrolled = LegacyEffectClock.Apply("A.b.c.", ContentEffect.Scroll, nowMs: 10_000);
            Assert.Equal(frame, scrolled);
        }

        [Fact]
        public void DotFix_ScrollWindows_FullFrameSequence()
        {
            // "A.b.c.d" = 4 folded positions → scrolls.
            // LegacyEffectClock convention: content + 3 trailing blanks (wrap supplies
            // lead-in after the clear). Ring length 7.
            var encoded = SevenSegment.EncodeWithDots("A.b.c.d");
            Assert.Equal(4, encoded.Count);
            Assert.Equal(new byte[]
            {
                D(SevenSegment.A),
                D(SevenSegment.B),
                D(SevenSegment.C),
                SevenSegment.D,
            }, encoded);

            // Full sequence: every step of the 7-slot ring.
            byte a = D(SevenSegment.A);
            byte b = D(SevenSegment.B);
            byte c = D(SevenSegment.C);
            byte d = SevenSegment.D;
            byte _ = SevenSegment.Blank;

            var expected = new (byte, byte, byte)[]
            {
                (a, b, c), // step 0: (A. b. c.)
                (b, c, d), // step 1: (b. c. d)
                (c, d, _), // step 2
                (d, _, _), // step 3
                (_, _, _), // step 4: full clear
                (_, _, a), // step 5: wrap lead-in
                (_, a, b), // step 6
            };

            for (int step = 0; step < expected.Length; step++)
            {
                long nowMs = (long)step * LegacyEffectClock.ScrollStepMs;
                var frame = LegacyEffectClock.Apply("A.b.c.d", ContentEffect.Scroll, nowMs);
                Assert.Equal(
                    new byte[] { expected[step].Item1, expected[step].Item2, expected[step].Item3 },
                    frame);
            }

            // Wrap: step 7 ≡ step 0
            Assert.Equal(
                LegacyEffectClock.Apply("A.b.c.d", ContentEffect.Scroll, 0),
                LegacyEffectClock.Apply("A.b.c.d", ContentEffect.Scroll,
                    (long)expected.Length * LegacyEffectClock.ScrollStepMs));
        }

        [Fact]
        public void DotFix_AllDots_ThreeBlankDotPositions()
        {
            // "..." = 3 folded positions → [blank|dot × 3], no collapse.
            var encoded = SevenSegment.EncodeWithDots("...");
            Assert.Equal(3, encoded.Count);
            Assert.Equal(new byte[] { BlankDot, BlankDot, BlankDot }, encoded);

            Assert.Equal(
                new byte[] { BlankDot, BlankDot, BlankDot },
                LegacyValueFormatter.Render("..."));
        }

        [Fact]
        public void DotFix_LeadingDot_BlankDotThenDigit()
        {
            // ".5" → [blank|dot, 5]
            var encoded = SevenSegment.EncodeWithDots(".5");
            Assert.Equal(2, encoded.Count);
            Assert.Equal(BlankDot, encoded[0]);
            Assert.Equal(SevenSegment.Digit5, encoded[1]);

            Assert.Equal(
                new byte[] { BlankDot, SevenSegment.Digit5, SevenSegment.Blank },
                LegacyValueFormatter.Render(".5"));
        }

        [Fact]
        public void DotFix_SingleDot_OneDottedBlank()
        {
            var encoded = SevenSegment.EncodeWithDots(".");
            Assert.Single(encoded);
            Assert.Equal(BlankDot, encoded[0]);

            Assert.Equal(
                new byte[] { BlankDot, SevenSegment.Blank, SevenSegment.Blank },
                LegacyValueFormatter.Render("."));
        }

        [Fact]
        public void DotFix_MixedScroll_1_2_3_4()
        {
            // "1.2.3.4" = 4 folded positions → scrolls; content windows keep dots attached.
            var encoded = SevenSegment.EncodeWithDots("1.2.3.4");
            Assert.Equal(4, encoded.Count);
            Assert.Equal(new byte[]
            {
                D(SevenSegment.Digit1),
                D(SevenSegment.Digit2),
                D(SevenSegment.Digit3),
                SevenSegment.Digit4,
            }, encoded);

            byte d1 = D(SevenSegment.Digit1);
            byte d2 = D(SevenSegment.Digit2);
            byte d3 = D(SevenSegment.Digit3);
            byte d4 = SevenSegment.Digit4;

            Assert.Equal(
                new byte[] { d1, d2, d3 },
                LegacyEffectClock.Apply("1.2.3.4", ContentEffect.Scroll, 0));
            Assert.Equal(
                new byte[] { d2, d3, d4 },
                LegacyEffectClock.Apply("1.2.3.4", ContentEffect.Scroll,
                    LegacyEffectClock.ScrollStepMs));
        }

        [Fact]
        public void DotFix_DisplayText_KeepsTrailingThirdDot()
        {
            // Settings-panel fit path: DisplayText must not drop the fold onto position 3.
            var transport = new RecordingCol01();
            var display = new DisplayEncoder(transport);

            Assert.True(display.DisplayText("A.b.c."));
            Assert.Equal(
                new byte[] { D(SevenSegment.A), D(SevenSegment.B), D(SevenSegment.C) },
                transport.LastSegments);
        }

        [Fact]
        public void DotFix_DisplayText_AllDots()
        {
            var transport = new RecordingCol01();
            var display = new DisplayEncoder(transport);

            Assert.True(display.DisplayText("..."));
            Assert.Equal(
                new byte[] { BlankDot, BlankDot, BlankDot },
                transport.LastSegments);
        }

        [Fact]
        public void DotFix_FitGate_UsesFoldedCount_NotRawLength()
        {
            // Raw "A.b.c." is 6 chars; folded is 3 → must fit (scroll inert).
            Assert.Equal(3, SevenSegment.EncodeWithDots("A.b.c.").Count);
            Assert.True(SegmentText.IsRenderableText("A.b.c."));

            // Raw "A.b.c.d" is 7 chars; folded is 4 → scroll.
            Assert.Equal(4, SevenSegment.EncodeWithDots("A.b.c.d").Count);
            Assert.False(SegmentText.IsRenderableText("A.b.c.d"));
            Assert.True(SegmentText.IsRenderableMessage("A.b.c.d"));
        }

        [Fact]
        public void DotFix_TruncateToFoldedPositions_KeepsDotsWithChars()
        {
            // Raw Substring(0,3) on "A.b.c.d" is "A.b" — wrong. Folded prefix is "A.b.c.".
            Assert.Equal("A.b.c.", SevenSegment.TruncateToFoldedPositions("A.b.c.d", 3));
            Assert.Equal("TOO", SevenSegment.TruncateToFoldedPositions("TOOLONG", 3));
            Assert.Equal("1.2.3.", SevenSegment.TruncateToFoldedPositions("1.2.3.4", 3));
        }

        /// <summary>Minimal col01 sink that records the last SetDisplay segment triple.</summary>
        private sealed class RecordingCol01 : IDeviceTransport
        {
            public byte[] LastSegments { get; private set; }

            public bool IsConnected => true;
            public int Col03MaxInputReportLength => 64;
            public int Col01MaxInputReportLength => 34;
            public IReportStream IdentityReports => FakeReportStream.Empty;
            public IReportStream ItmReports => FakeReportStream.Empty;
            public IReportStream SrmReports => FakeReportStream.Empty;
            public IReportStream TuningReports => FakeReportStream.Empty;

            public bool SendCol01(byte[] report)
            {
                // DisplayEncoder.SetDisplay: [01 F8 09 01 02 seg0 seg1 seg2]
                LastSegments = new[] { report[5], report[6], report[7] };
                return true;
            }

            public bool SendCol03(byte[] report) => true;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public IDisposable BeginBatch() => new NoOp();

            private sealed class NoOp : IDisposable
            {
                public void Dispose() { }
            }
        }
    }
}
