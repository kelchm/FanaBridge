using System.Collections.Generic;
using System.Linq;
using FanaBridge.Devices.Profiles;
using FanaBridge.Plugin.UI;
using Xunit;

namespace FanaBridge.Tests.Plugin.UI
{
    public class WizardStateTests
    {
        // ── InputMappingState.EnsureLeds — build / rebuild semantics ────

        [Fact]
        public void EnsureLeds_FirstBuild_LaysOutColorThenMono()
        {
            var m = new InputMappingState();
            m.EnsureLeds(colorCount: 3, monoCount: 2);

            Assert.Equal(5, m.Leds.Count);
            Assert.All(m.Leds.Take(3), e => Assert.Equal(LedChannel.ButtonRgb, e.Channel));
            Assert.All(m.Leds.Skip(3), e => Assert.Equal(LedChannel.ButtonAuxIntensity, e.Channel));
            // Mono HwIndex continues after the color block (shared intensity payload).
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, m.Leds.Select(e => e.HwIndex));
            Assert.Equal(0, m.CurrentIndex);
        }

        [Fact]
        public void EnsureLeds_UnchangedCounts_KeepsListAndCaptures()
        {
            var m = new InputMappingState();
            m.EnsureLeds(2, 1);
            m.Leds[0].ButtonInputId = "BTN_A";
            m.CurrentIndex = 1;

            m.EnsureLeds(2, 1);   // plain Back/Next round-trip — nothing changed

            Assert.Equal("BTN_A", m.Leds[0].ButtonInputId);
            Assert.Equal(1, m.CurrentIndex);   // untouched — not repositioned
        }

        [Fact]
        public void EnsureLeds_ColorCountChanged_RebasesMonoHwIndexes()
        {
            // The E2 misattribution case: 3 color + 2 mono mapped, then the user
            // goes Back and corrects the color count to 2. Stale mono entries
            // carried HwIndex 3/4; the rebuilt list must carry 2/3, with the mono
            // captures still attached to the same mono LEDs (by ordinal).
            var m = new InputMappingState();
            m.EnsureLeds(3, 2);
            m.Leds[3].ButtonInputId = "MONO_1";
            m.Leds[4].ButtonInputId = "MONO_2";

            m.EnsureLeds(2, 2);

            var mono = m.Leds.Where(e => e.Channel == LedChannel.ButtonAuxIntensity).ToList();
            Assert.Equal(new[] { 2, 3 }, mono.Select(e => e.HwIndex));
            Assert.Equal("MONO_1", mono[0].ButtonInputId);
            Assert.Equal("MONO_2", mono[1].ButtonInputId);
        }

        [Fact]
        public void EnsureLeds_CountGrown_NewLedIsUnmappedAndCurrent()
        {
            var m = new InputMappingState();
            m.EnsureLeds(2, 0);
            m.Leds[0].ButtonInputId = "A";
            m.Leds[1].ButtonInputId = "B";
            m.CurrentIndex = 2;   // mapping was complete

            m.EnsureLeds(3, 0);   // user went Back: "it was 3, not 2"

            Assert.Equal(3, m.Leds.Count);
            Assert.Equal("A", m.Leds[0].ButtonInputId);
            Assert.Equal("B", m.Leds[1].ButtonInputId);
            Assert.Null(m.Leds[2].ButtonInputId);
            Assert.Equal(2, m.CurrentIndex);    // positioned at the new, unmapped LED
            Assert.False(m.IsComplete);
        }

        [Fact]
        public void EnsureLeds_CountShrunk_DropsExtraCaptures_AndCompletes()
        {
            var m = new InputMappingState();
            m.EnsureLeds(3, 0);
            foreach (var e in m.Leds) e.ButtonInputId = "BTN_" + e.HwIndex;

            m.EnsureLeds(2, 0);

            Assert.Equal(2, m.Leds.Count);
            Assert.Equal("BTN_0", m.Leds[0].ButtonInputId);
            Assert.Equal("BTN_1", m.Leds[1].ButtonInputId);
            Assert.True(m.IsComplete);   // everything remaining is mapped
        }

        [Fact]
        public void EnsureLeds_Rebuild_ResetsClassificationState()
        {
            var m = new InputMappingState();
            m.EnsureLeds(2, 0);
            m.Phase = MappingPhase.Classifying;
            m.ClassifyInputs.Add("BTN_X");

            m.EnsureLeds(1, 0);

            Assert.Equal(MappingPhase.WaitingForInput, m.Phase);
            Assert.Empty(m.ClassifyInputs);
        }

        [Fact]
        public void EnsureLeds_EncoderCaptures_CarryOver()
        {
            var m = new InputMappingState();
            m.EnsureLeds(1, 1);
            m.Leds[1].IsEncoder = true;
            m.Leds[1].RelativeCW = "ROT_CW";
            m.Leds[1].RelativeCCW = "ROT_CCW";
            m.Leds[1].AbsoluteInputs = new List<string> { "POS_1", "POS_2" };

            m.EnsureLeds(2, 1);   // color count corrected upward

            var mono = m.Leds.Single(e => e.Channel == LedChannel.ButtonAuxIntensity);
            Assert.Equal(2, mono.HwIndex);   // rebased: after the 2 color LEDs
            Assert.True(mono.IsEncoder);
            Assert.Equal("ROT_CW", mono.RelativeCW);
            Assert.Equal("ROT_CCW", mono.RelativeCCW);
            Assert.Equal(new[] { "POS_1", "POS_2" }, mono.AbsoluteInputs);
        }
    }
}
