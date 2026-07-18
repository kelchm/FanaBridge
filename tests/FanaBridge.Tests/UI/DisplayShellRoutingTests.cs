using FanaBridge.Display.Host;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.UI.Display;
using Xunit;

namespace FanaBridge.Tests.UI
{
    /// <summary>
    /// Pure Display-tab shell routing: which Overview chrome a wheel gets, which rule
    /// set Triggers opens on, Virtual pages reachability, and legacy mirror caption /
    /// segment gates. No WPF.
    /// </summary>
    public class DisplayShellRoutingTests
    {
        [Fact]
        public void TriggersRuleSet_BasicIsLegacy_ItmIsItm()
        {
            Assert.Equal(TriggerRuleSet.Legacy,
                DisplayShellRouting.TriggersRuleSetFor(DisplayType.Basic));
            Assert.Equal(TriggerRuleSet.Itm,
                DisplayShellRouting.TriggersRuleSetFor(DisplayType.Itm));
        }

        [Theory]
        [InlineData(DisplayType.Basic, DisplaySettings.ControlLegacy, true)]
        [InlineData(DisplayType.Basic, DisplaySettings.ControlItm, true)]
        [InlineData(DisplayType.Basic, DisplaySettings.ControlOff, false)]
        [InlineData(DisplayType.Itm, DisplaySettings.ControlLegacy, false)]
        [InlineData(DisplayType.Itm, DisplaySettings.ControlItm, false)]
        [InlineData(DisplayType.Itm, DisplaySettings.ControlOff, false)]
        public void ShowLegacyOverview_BasicWhenNotOff(
            DisplayType type, string control, bool expected)
            => Assert.Equal(expected, DisplayShellRouting.ShowLegacyOverview(type, control));

        [Theory]
        [InlineData(DisplayType.Itm, DisplaySettings.ControlItm, true)]
        [InlineData(DisplayType.Itm, DisplaySettings.ControlLegacy, false)]
        [InlineData(DisplayType.Itm, DisplaySettings.ControlOff, false)]
        [InlineData(DisplayType.Basic, DisplaySettings.ControlItm, false)]
        [InlineData(DisplayType.Basic, DisplaySettings.ControlLegacy, false)]
        public void ShowItmOverview_OnlyItmControlOnItmWheel(
            DisplayType type, string control, bool expected)
            => Assert.Equal(expected, DisplayShellRouting.ShowItmOverview(type, control));

        [Fact]
        public void CanOpenVirtualPages_FalseOnlyWhenOff()
        {
            Assert.True(DisplayShellRouting.CanOpenVirtualPages(DisplaySettings.ControlItm));
            Assert.True(DisplayShellRouting.CanOpenVirtualPages(DisplaySettings.ControlLegacy));
            Assert.False(DisplayShellRouting.CanOpenVirtualPages(DisplaySettings.ControlOff));
        }

        [Fact]
        public void VirtualPagesLinkLabel_DiffersByWheel()
        {
            Assert.Equal("Edit virtual pages →",
                DisplayShellRouting.VirtualPagesLinkLabel(DisplayType.Basic));
            Assert.Equal("Legacy screens (Page 6)",
                DisplayShellRouting.VirtualPagesLinkLabel(DisplayType.Itm));
        }

        [Fact]
        public void LegacyMirrorCaption_ScreenNameWins_ElseDisplayMode_ElseBlank()
        {
            Assert.Equal("Pit",
                DisplayShellRouting.LegacyMirrorCaption("Pit", "Speed"));
            Assert.Equal("Gear",
                DisplayShellRouting.LegacyMirrorCaption(null, "Gear"));
            Assert.Equal("Blank",
                DisplayShellRouting.LegacyMirrorCaption(null, "None"));
            Assert.Equal("Blank",
                DisplayShellRouting.LegacyMirrorCaption(null, null));
        }

        [Fact]
        public void UseRuleDrivenSegments_RequiresThreeBytes()
        {
            Assert.False(DisplayShellRouting.UseRuleDrivenSegments(null));
            Assert.False(DisplayShellRouting.UseRuleDrivenSegments(new byte[] { 1, 2 }));
            Assert.True(DisplayShellRouting.UseRuleDrivenSegments(
                new byte[] { SevenSegment.Digit1, SevenSegment.Digit4, SevenSegment.Digit2 }));
        }

        // Off / mode-header gates stay on DisplayModeHeaderModel (P3) — pin the seam still holds.
        [Fact]
        public void ModeHeaderGates_Unchanged_StillOnDisplayModeHeaderModel()
        {
            Assert.True(DisplayModeHeaderModel.ShowModeHeader(
                isItm: true, isOverview: true, DisplaySettings.ControlItm));
            Assert.False(DisplayModeHeaderModel.ShowModeHeader(
                isItm: false, isOverview: true, DisplaySettings.ControlLegacy));
            Assert.True(DisplayModeHeaderModel.ShowModeHeader(
                isItm: false, isOverview: true, DisplaySettings.ControlOff));
            Assert.False(DisplayModeHeaderModel.ShowModeHeader(
                isItm: true, isOverview: false, DisplaySettings.ControlItm));
        }
    }
}
