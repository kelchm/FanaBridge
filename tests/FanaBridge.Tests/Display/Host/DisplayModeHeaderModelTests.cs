using FanaBridge.Display.Host;
using Xunit;

namespace FanaBridge.Tests.Display.Host
{
    /// <summary>
    /// Pure DISPLAY MODE header decisions used by <c>DisplayTabPanel</c> — segment ids,
    /// mock-verbatim hints, header visibility (incl. Off-trap), and turn-back-on target.
    /// </summary>
    public class DisplayModeHeaderModelTests
    {
        [Theory]
        [InlineData(DisplaySettings.ControlItm, DisplayModeHeaderModel.SegmentItm)]
        [InlineData(DisplaySettings.ControlLegacy, DisplayModeHeaderModel.SegmentLegacy)]
        [InlineData(DisplaySettings.ControlOff, DisplayModeHeaderModel.SegmentOff)]
        [InlineData("itm", DisplayModeHeaderModel.SegmentItm)]
        [InlineData("LEGACY", DisplayModeHeaderModel.SegmentLegacy)]
        [InlineData("off", DisplayModeHeaderModel.SegmentOff)]
        [InlineData("Garbage", DisplayModeHeaderModel.SegmentItm)]
        [InlineData(null, DisplayModeHeaderModel.SegmentItm)]
        public void SegmentIdFor_MapsControl(string? control, string expected)
            => Assert.Equal(expected, DisplayModeHeaderModel.SegmentIdFor(control));

        [Theory]
        [InlineData(DisplayModeHeaderModel.SegmentItm, DisplaySettings.ControlItm)]
        [InlineData(DisplayModeHeaderModel.SegmentLegacy, DisplaySettings.ControlLegacy)]
        [InlineData(DisplayModeHeaderModel.SegmentOff, DisplaySettings.ControlOff)]
        [InlineData("unknown", DisplaySettings.ControlItm)]
        [InlineData(null, DisplaySettings.ControlItm)]
        public void ControlForSegment_MapsSegment(string? segmentId, string expected)
            => Assert.Equal(expected, DisplayModeHeaderModel.ControlForSegment(segmentId));

        [Fact]
        public void ModeHint_MatchesMockCopy_ForEachControl()
        {
            Assert.Equal(
                "Legacy only shows just the 3-character display; Off hands the display back to the game.",
                DisplayModeHeaderModel.ModeHint(DisplaySettings.ControlItm));
            Assert.Equal(
                "Only the 3-character legacy display is used.",
                DisplayModeHeaderModel.ModeHint(DisplaySettings.ControlLegacy));
            Assert.Equal(
                "FanaBridge leaves the display alone.",
                DisplayModeHeaderModel.ModeHint(DisplaySettings.ControlOff));
        }

        [Theory]
        // ITM Overview: always show
        [InlineData(true, true, DisplaySettings.ControlItm, true)]
        [InlineData(true, true, DisplaySettings.ControlLegacy, true)]
        [InlineData(true, true, DisplaySettings.ControlOff, true)]
        // ITM editor (not Overview): only while Off
        [InlineData(true, false, DisplaySettings.ControlItm, false)]
        [InlineData(true, false, DisplaySettings.ControlLegacy, false)]
        [InlineData(true, false, DisplaySettings.ControlOff, true)]
        // Basic wheel: only while Off (Off-trap guard)
        [InlineData(false, true, DisplaySettings.ControlItm, false)]
        [InlineData(false, true, DisplaySettings.ControlLegacy, false)]
        [InlineData(false, true, DisplaySettings.ControlOff, true)]
        [InlineData(false, false, DisplaySettings.ControlOff, true)]
        [InlineData(false, false, DisplaySettings.ControlLegacy, false)]
        public void ShowModeHeader_ItmOverviewOrAnyWheelWhenOff(
            bool isItm, bool isOverview, string control, bool expected)
            => Assert.Equal(expected, DisplayModeHeaderModel.ShowModeHeader(isItm, isOverview, control));

        [Fact]
        public void TurnBackOnControl_ItmOnItmWheels_LegacyOnBasic()
        {
            Assert.Equal(DisplaySettings.ControlItm, DisplayModeHeaderModel.TurnBackOnControl(isItm: true));
            Assert.Equal(DisplaySettings.ControlLegacy, DisplayModeHeaderModel.TurnBackOnControl(isItm: false));
        }

        [Theory]
        [InlineData(DisplaySettings.ControlOff, true)]
        [InlineData("off", true)]
        [InlineData(DisplaySettings.ControlItm, false)]
        [InlineData(DisplaySettings.ControlLegacy, false)]
        [InlineData(null, false)]
        public void IsOff_RecognizesOffOnly(string? control, bool expected)
            => Assert.Equal(expected, DisplayModeHeaderModel.IsOff(control));

        [Fact]
        public void IsSameControl_ComparesControlOnly_Ordinal()
        {
            // Spec no-op: re-selecting the same control is a no-op regardless of any
            // ItmEnabled mirror state the caller may hold separately.
            Assert.True(DisplayModeHeaderModel.IsSameControl(
                DisplaySettings.ControlLegacy, DisplaySettings.ControlLegacy));
            Assert.True(DisplayModeHeaderModel.IsSameControl(
                DisplaySettings.ControlItm, DisplaySettings.ControlItm));
            Assert.True(DisplayModeHeaderModel.IsSameControl(
                DisplaySettings.ControlOff, DisplaySettings.ControlOff));
            Assert.False(DisplayModeHeaderModel.IsSameControl(
                DisplaySettings.ControlLegacy, DisplaySettings.ControlItm));
            // Ordinal: casing mismatch is not the same control (canonicalization happens first).
            Assert.False(DisplayModeHeaderModel.IsSameControl("legacy", DisplaySettings.ControlLegacy));
        }
    }
}
