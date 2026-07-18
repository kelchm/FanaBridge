using FanaBridge.Display.Legacy;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests.Display.Legacy
{
    /// <summary>
    /// Clock-injected effect frames for <see cref="LegacyEffectClock"/>. Asserts against
    /// <see cref="SevenSegment"/> byte constants — same style as
    /// <c>LegacyDisplayDriverTests</c>.
    /// </summary>
    public class LegacyEffectClockTests
    {
        private static (byte, byte, byte) Apply(string text, LegacyEffect effect, long nowMs)
        {
            var f = LegacyEffectClock.Apply(text, effect, nowMs);
            return (f[0], f[1], f[2]);
        }

        // ── None / Unknown ───────────────────────────────────────────────

        [Fact]
        public void None_RendersStaticFrame()
        {
            Assert.Equal(
                (SevenSegment.P, SevenSegment.I, SevenSegment.T),
                Apply("PIT", LegacyEffect.None, nowMs: 0));
        }

        [Fact]
        public void Unknown_TreatedAsNone()
        {
            Assert.Equal(
                (SevenSegment.P, SevenSegment.I, SevenSegment.T),
                Apply("PIT", LegacyEffect.Unknown, nowMs: 9999));
        }

        // ── Scroll ───────────────────────────────────────────────────────

        [Fact]
        public void Scroll_Inert_WhenTextFitsInThreePositions()
        {
            // Same frame at every clock — no motion for short text.
            var a = Apply("PIT", LegacyEffect.Scroll, nowMs: 0);
            var b = Apply("PIT", LegacyEffect.Scroll, nowMs: 10_000);
            Assert.Equal((SevenSegment.P, SevenSegment.I, SevenSegment.T), a);
            Assert.Equal(a, b);
        }

        [Fact]
        public void Scroll_AdvancesWindow_EveryStepMs()
        {
            // "HELLO" encodes to 5 positions; pad +3 blanks → 8-slot ring.
            // step 0 @ 0ms: H E L
            Assert.Equal(
                (SevenSegment.H, SevenSegment.E, SevenSegment.L),
                Apply("HELLO", LegacyEffect.Scroll, nowMs: 0));

            // step 1 @ 400ms: E L L
            Assert.Equal(
                (SevenSegment.E, SevenSegment.L, SevenSegment.L),
                Apply("HELLO", LegacyEffect.Scroll, nowMs: LegacyEffectClock.ScrollStepMs));

            // step 2 @ 800ms: L L O
            Assert.Equal(
                (SevenSegment.L, SevenSegment.L, SevenSegment.O),
                Apply("HELLO", LegacyEffect.Scroll, nowMs: LegacyEffectClock.ScrollStepMs * 2));

            // step 5 @ 2000ms: first pad blank + blanks → (Blank, Blank, Blank) after O
            // encoded: H E L L O _ _ _  (indices 0..7); step 5 → indices 5,6,7 = blank³
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                Apply("HELLO", LegacyEffect.Scroll, nowMs: LegacyEffectClock.ScrollStepMs * 5));
        }

        [Fact]
        public void Scroll_WrapsAround()
        {
            // 8 slots; step 8 ≡ step 0
            var step0 = Apply("HELLO", LegacyEffect.Scroll, nowMs: 0);
            var step8 = Apply("HELLO", LegacyEffect.Scroll,
                nowMs: LegacyEffectClock.ScrollStepMs * 8);
            Assert.Equal(step0, step8);
        }

        // ── Blink / Flash ────────────────────────────────────────────────

        [Fact]
        public void Blink_OnPhase_ShowsText()
        {
            // phase 0 for nowMs in [0, 500)
            Assert.Equal(
                (SevenSegment.P, SevenSegment.I, SevenSegment.T),
                Apply("PIT", LegacyEffect.Blink, nowMs: 0));
            Assert.Equal(
                (SevenSegment.P, SevenSegment.I, SevenSegment.T),
                Apply("PIT", LegacyEffect.Blink, nowMs: LegacyEffectClock.BlinkHalfPeriodMs - 1));
        }

        [Fact]
        public void Blink_OffPhase_IsBlank()
        {
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                Apply("PIT", LegacyEffect.Blink, nowMs: LegacyEffectClock.BlinkHalfPeriodMs));
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                Apply("PIT", LegacyEffect.Blink, nowMs: LegacyEffectClock.BlinkHalfPeriodMs * 2 - 1));
        }

        [Fact]
        public void Blink_ReturnsToOn_AfterFullPeriod()
        {
            Assert.Equal(
                (SevenSegment.P, SevenSegment.I, SevenSegment.T),
                Apply("PIT", LegacyEffect.Blink, nowMs: LegacyEffectClock.BlinkHalfPeriodMs * 2));
        }

        [Fact]
        public void Flash_BehavesLikeBlink()
        {
            // Defensive runtime path — validator coerces Flash→Blink, but the clock
            // treats Flash as Blink if it ever sees one.
            Assert.Equal(
                Apply("PIT", LegacyEffect.Blink, nowMs: 0),
                Apply("PIT", LegacyEffect.Flash, nowMs: 0));
            Assert.Equal(
                Apply("PIT", LegacyEffect.Blink, nowMs: LegacyEffectClock.BlinkHalfPeriodMs),
                Apply("PIT", LegacyEffect.Flash, nowMs: LegacyEffectClock.BlinkHalfPeriodMs));
        }

        // ── Centered gear text still works through the clock ─────────────

        [Fact]
        public void None_PreservesCenteredGearGlyph()
        {
            string gear = LegacyValueFormatter.FormatGear("3");
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Digit3, SevenSegment.Blank),
                Apply(gear, LegacyEffect.None, nowMs: 0));
        }
    }
}
