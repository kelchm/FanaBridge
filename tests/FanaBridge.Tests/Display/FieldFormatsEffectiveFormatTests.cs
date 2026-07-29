using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Precedence matrix for the shared <see cref="FieldFormats.EffectiveFormat"/>
    /// helper (explicit &gt; override-default-bare &gt; Show*Total toggle &gt; family default).
    /// Mapper and Pages editor both delegate here — keep them in lockstep.
    /// </summary>
    public class FieldFormatsEffectiveFormatTests
    {
        // ── Explicit wins ────────────────────────────────────────────────

        [Theory]
        [InlineData(ItmParam.Lap, FieldFormats.Bare, true, true, true)]
        [InlineData(ItmParam.Lap, FieldFormats.WithTotal, false, false, true)]
        [InlineData(ItmParam.Position, FieldFormats.Bare, true, true, true)]
        [InlineData(ItmParam.Fuel, FieldFormats.Bare, false, true, true)]
        [InlineData(ItmParam.OilTemp, FieldFormats.Bare, false, true, true)]
        [InlineData(ItmParam.TyreFlTemp, FieldFormats.Unit, true, true, true)]
        public void Explicit_WinsOverToggleAndOverride(
            ushort paramId, string explicitFormat,
            bool hasOverride, bool showLap, bool showPos)
        {
            Assert.Equal(explicitFormat, FieldFormats.EffectiveFormat(
                paramId, explicitFormat, hasOverride, showLap, showPos));
        }

        // ── Source override → bare for total/temp ────────────────────────

        [Theory]
        [InlineData(ItmParam.Lap)]
        [InlineData(ItmParam.Position)]
        [InlineData(ItmParam.Fuel)]
        [InlineData(ItmParam.OilTemp)]
        [InlineData(ItmParam.TyreRrTemp)]
        public void SourceOverride_NoExplicit_DefaultsBare(ushort paramId)
        {
            Assert.Equal(FieldFormats.Bare, FieldFormats.EffectiveFormat(
                paramId, null, hasSourceOverride: true,
                showLapTotal: true, showPositionTotal: true));
            Assert.Equal(FieldFormats.Bare, FieldFormats.EffectiveFormat(
                paramId, "", hasSourceOverride: true,
                showLapTotal: false, showPositionTotal: false));
        }

        [Fact]
        public void SourceOverride_GearAndSpeed_FamilyDefaults()
        {
            // Gear/speed now have format families (task #23 / design 8c).
            Assert.Equal(FieldFormats.Whole, FieldFormats.EffectiveFormat(
                ItmParam.Speed, null, hasSourceOverride: true,
                showLapTotal: true, showPositionTotal: true));
            Assert.Equal(FieldFormats.Neutral, FieldFormats.EffectiveFormat(
                ItmParam.Gear, null, hasSourceOverride: true,
                showLapTotal: true, showPositionTotal: true));
        }

        [Fact]
        public void SourceOverride_StillNoFamily_ReturnsNull()
        {
            // A param outside all families still returns null.
            Assert.Null(FieldFormats.EffectiveFormat(
                ItmParam.BrakeBias, null, hasSourceOverride: true,
                showLapTotal: true, showPositionTotal: true));
        }

        // ── Toggle migration (no mapping) ────────────────────────────────

        [Theory]
        [InlineData(true, FieldFormats.WithTotal)]
        [InlineData(false, FieldFormats.Bare)]
        public void Toggle_Lap_NoOverride(bool showLapTotal, string expected)
        {
            Assert.Equal(expected, FieldFormats.EffectiveFormat(
                ItmParam.Lap, null, false, showLapTotal, showPositionTotal: true));
        }

        [Theory]
        [InlineData(true, FieldFormats.WithTotal)]
        [InlineData(false, FieldFormats.Bare)]
        public void Toggle_Position_NoOverride(bool showPositionTotal, string expected)
        {
            Assert.Equal(expected, FieldFormats.EffectiveFormat(
                ItmParam.Position, null, false, showLapTotal: true, showPositionTotal));
        }

        // ── Family defaults ──────────────────────────────────────────────

        [Fact]
        public void FamilyDefault_Fuel_WithTotal()
        {
            Assert.Equal(FieldFormats.WithTotal, FieldFormats.EffectiveFormat(
                ItmParam.Fuel, null, false, true, true));
        }

        [Theory]
        [InlineData(ItmParam.OilTemp)]
        [InlineData(ItmParam.TyreFlTemp)]
        [InlineData(ItmParam.TyreFrTemp)]
        [InlineData(ItmParam.TyreRlTemp)]
        [InlineData(ItmParam.TyreRrTemp)]
        public void FamilyDefault_Temp_Unit(ushort paramId)
        {
            Assert.Equal(FieldFormats.Unit, FieldFormats.EffectiveFormat(
                paramId, null, false, true, true));
        }

        [Fact]
        public void FamilyDefault_GearAndSpeed()
        {
            Assert.Equal(FieldFormats.Whole, FieldFormats.EffectiveFormat(
                ItmParam.Speed, null, false, true, true));
            Assert.Equal(FieldFormats.Neutral, FieldFormats.EffectiveFormat(
                ItmParam.Gear, null, false, true, true));
        }

        [Fact]
        public void NonFormatParam_NoOverride_ReturnsNull()
        {
            Assert.Null(FieldFormats.EffectiveFormat(
                ItmParam.BrakeBias, null, false, true, true));
            Assert.Null(FieldFormats.EffectiveFormat(
                ItmParam.TcSetting, null, false, true, true));
        }
    }
}
