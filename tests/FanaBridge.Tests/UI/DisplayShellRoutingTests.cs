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
        [Theory]
        [InlineData(DisplayType.Basic, DisplaySettings.ControlItm, false)]
        [InlineData(DisplayType.Basic, DisplaySettings.ControlLegacy, false)]
        [InlineData(DisplayType.Itm, DisplaySettings.ControlItm, true)]
        [InlineData(DisplayType.Itm, DisplaySettings.ControlLegacy, false)]
        [InlineData(DisplayType.Itm, DisplaySettings.ControlOff, true)]
        [InlineData(DisplayType.Itm, null, true)]
        public void TriggersRuleSet_LegacyUnlessItmWorldActiveOnItmWheel(
            DisplayType type, string? control, bool expectItmSet)
            => Assert.Equal(expectItmSet ? TriggerRuleSet.Itm : TriggerRuleSet.Legacy,
                DisplayShellRouting.TriggersRuleSetFor(type, control));

        [Theory]
        [InlineData(DisplayType.Basic, DisplaySettings.ControlLegacy, true)]
        [InlineData(DisplayType.Basic, DisplaySettings.ControlItm, true)]
        [InlineData(DisplayType.Basic, DisplaySettings.ControlOff, false)]
        [InlineData(DisplayType.Itm, DisplaySettings.ControlLegacy, true)]
        [InlineData(DisplayType.Itm, DisplaySettings.ControlItm, false)]
        [InlineData(DisplayType.Itm, DisplaySettings.ControlOff, false)]
        public void ShowLegacyOverview_BasicNotOff_ItmInLegacyControl(
            DisplayType type, string control, bool expected)
            => Assert.Equal(expected, DisplayShellRouting.ShowLegacyOverview(type, control));

        // ITM wheel + Legacy control: exactly one Overview shows, and it is the legacy one
        // (the pre-fix shell showed neither — only the old Display Mode section survived).
        [Fact]
        public void ItmWheelInLegacyControl_ShowsExactlyTheLegacyOverview()
        {
            Assert.False(DisplayShellRouting.ShowItmOverview(
                DisplayType.Itm, DisplaySettings.ControlLegacy));
            Assert.True(DisplayShellRouting.ShowLegacyOverview(
                DisplayType.Itm, DisplaySettings.ControlLegacy));
        }

        [Fact]
        public void UseWideLegacyFace_ItmOnly_SamePhysicalDisplay()
        {
            Assert.True(DisplayShellRouting.UseWideLegacyFace(DisplayType.Itm));
            Assert.False(DisplayShellRouting.UseWideLegacyFace(DisplayType.Basic));
        }

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

        [Theory]
        [InlineData(DisplayType.Itm, DisplaySettings.ControlItm, true, false, false)]
        [InlineData(DisplayType.Itm, DisplaySettings.ControlLegacy, false, true, false)]
        [InlineData(DisplayType.Itm, DisplaySettings.ControlOff, false, false, true)]
        [InlineData(DisplayType.Basic, DisplaySettings.ControlItm, false, true, false)]
        [InlineData(DisplayType.Basic, DisplaySettings.ControlOff, false, false, true)]
        public void V1OverviewSurfaceAfterV2Removed_NeverBlank(
            DisplayType type, string control,
            bool expectItm, bool expectLegacy, bool expectOff)
        {
            // v2→removed must restore a mode-dependent v1 surface (no blank panel).
            DisplayShellRouting.V1OverviewSurfaceAfterV2Removed(
                type, control, out bool itm, out bool legacy, out bool off);
            Assert.Equal(expectItm, itm);
            Assert.Equal(expectLegacy, legacy);
            Assert.Equal(expectOff, off);
            Assert.True(itm || legacy || off, "v2 removed must not leave a blank Overview");
        }

        [Theory]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        public void LeaveDiagnosticsAfterV2Removed_WhenDiagnosticsOpenAndDocGone(
            bool onDiagnostics, bool v2Live, bool expectLeave)
        {
            // v2 removed while Diagnostics visible → leave + restore v1 (never stuck/blank).
            Assert.Equal(
                expectLeave,
                DisplayShellRouting.LeaveDiagnosticsAfterV2Removed(onDiagnostics, v2Live));
            if (expectLeave)
            {
                DisplayShellRouting.V1OverviewSurfaceAfterV2Removed(
                    DisplayType.Itm, DisplaySettings.ControlItm,
                    out bool itm, out bool legacy, out bool off);
                Assert.True(itm || legacy || off, "restore law must yield a non-blank v1 surface");
            }
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

        // Page off (ITM wheel, DisplayMode "None"): the snapshot's resolve-only screen
        // name must not be claimed — the caption falls back to the mode ("None" → Blank).
        [Fact]
        public void LegacyMirrorCaption_PageInactive_IgnoresScreenName()
        {
            Assert.Equal("Blank", DisplayShellRouting.LegacyMirrorCaption(
                "Pit", "None", legacyPageActive: false));
            Assert.Equal("Gear", DisplayShellRouting.LegacyMirrorCaption(
                "Pit", "Gear", legacyPageActive: false));
        }

        [Fact]
        public void UseRuleDrivenSegments_RequiresThreeBytes()
        {
            Assert.False(DisplayShellRouting.UseRuleDrivenSegments(null));
            Assert.False(DisplayShellRouting.UseRuleDrivenSegments(new byte[] { 1, 2 }));
            Assert.True(DisplayShellRouting.UseRuleDrivenSegments(
                new byte[] { SevenSegment.Digit1, SevenSegment.Digit4, SevenSegment.Digit2 }));
        }

        // Mirror truth = wire truth: resolve-only segments (page off) never paint.
        [Fact]
        public void UseRuleDrivenSegments_PageInactive_NeverPaints()
            => Assert.False(DisplayShellRouting.UseRuleDrivenSegments(
                new byte[] { SevenSegment.Digit1, SevenSegment.Digit4, SevenSegment.Digit2 },
                legacyPageActive: false));

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
