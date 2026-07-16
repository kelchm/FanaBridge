using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// <see cref="ItmValueRenderer"/> reproduces the display's per-parameter value
    /// formats exactly as the official quick guide renders them — every quirk pinned:
    /// space-before-slash totals, zero-padded position, oil temp without a space vs
    /// tire temps with one, comma-decimal brake bias, signed gap/delta seconds,
    /// mm:ss.mmm lap times, DRS dots, and the per-display gear forms.
    /// </summary>
    public class ItmValueRendererTests
    {
        // ── Laps / position / fuel (slash-totals, spaced) ────────────────

        [Fact]
        public void Lap_SpaceBeforeSlashTotal()
            => Assert.Equal("15 /73", ItmValueRenderer.Render(
                ItmParam.Lap, ItmValue.UInt8(0, ItmParam.Lap, 15), "/73"));

        [Fact]
        public void Position_ZeroPaddedToTwo()
            => Assert.Equal("02 /20", ItmValueRenderer.Render(
                ItmParam.Position, ItmValue.UInt8(0, ItmParam.Position, 2), "/20"));

        // The fuel field prints its single fraction digit unconditionally
        // (hardware-observed): a whole wire level renders "49.0", never "49".
        [Fact]
        public void Fuel_WholeLevel_KeepsTheForcedDecimal_SpacedCapacitySuffix()
            => Assert.Equal("49.0 /106L", ItmValueRenderer.Render(
                ItmParam.Fuel, ItmValue.Float32(0, ItmParam.Fuel, 49f), "/106L"));

        [Fact]
        public void Fuel_FractionalLevel_KeepsTheFieldsSingleDecimal()
            => Assert.Equal("49.3", ItmValueRenderer.Render(
                ItmParam.Fuel, ItmValue.Float32(0, ItmParam.Fuel, 49.3f)));

        [Fact]
        public void BlankSuffix_TheDriversActiveClear_RendersAsNone()
            => Assert.Equal("15", ItmValueRenderer.Render(
                ItmParam.Lap, ItmValue.UInt8(0, ItmParam.Lap, 15), " "));

        [Fact]
        public void NoSuffix_NoTrailingSpace()
            => Assert.Equal("15", ItmValueRenderer.Render(
                ItmParam.Lap, ItmValue.UInt8(0, ItmParam.Lap, 15)));

        // ── Lap times (mm:ss.mmm) ────────────────────────────────────────

        // Time fields truncate milliseconds rather than round (hardware-observed;
        // the mapper leaves them unrounded for the same reason). The 51.196 case
        // discriminates: float32(51.196) = 51.19599915, so truncation drops to
        // .195 where rounding would show .196.
        [Theory]
        [InlineData(96.911f, "01:36.911")]
        [InlineData(134.169f, "02:14.169")]
        [InlineData(53.562f, "00:53.562")]
        [InlineData(51.196f, "00:51.195")]
        public void LapTimes_RenderAsMinutesSecondsMilliseconds(float seconds, string expected)
        {
            Assert.Equal(expected, ItmValueRenderer.Render(
                ItmParam.LapTime, ItmValue.Float32(0, ItmParam.LapTime, seconds)));
            Assert.Equal(expected, ItmValueRenderer.Render(
                ItmParam.LastLapTime, ItmValue.Float32(0, ItmParam.LastLapTime, seconds)));
            Assert.Equal(expected, ItmValueRenderer.Render(
                ItmParam.BestLapTime, ItmValue.Float32(0, ItmParam.BestLapTime, seconds)));
        }

        // ── ERS / DRS / delta ────────────────────────────────────────────

        [Fact]
        public void Ers_IntegerPercent()
            => Assert.Equal("55%", ItmValueRenderer.Render(
                ItmParam.ErsLevel, ItmValue.Int32(0, ItmParam.ErsLevel, 55)));

        [Fact]
        public void DrsFlags_FilledAndHollowDots()
        {
            Assert.Equal(ItmValueRenderer.DrsDotOn, ItmValueRenderer.Render(
                ItmParam.DrsZone, ItmValue.UInt8(0, ItmParam.DrsZone, 1)));
            Assert.Equal(ItmValueRenderer.DrsDotOff, ItmValueRenderer.Render(
                ItmParam.DrsActive, ItmValue.UInt8(0, ItmParam.DrsActive, 0)));
        }

        // The delta field renders two decimals like the car-gap fields
        // (hardware-observed precision; every wire delta is pre-rounded to 2 dp by
        // the mapper). Both cases discriminate against a 3-dp format, which would
        // append a trailing digit ("-0.490s" / "1.200s") the display never shows.
        [Fact]
        public void Delta_SignedTwoDecimals_WithSecondsSuffix()
        {
            Assert.Equal("-0.49s", ItmValueRenderer.Render(
                ItmParam.DeltaOwnBest, ItmValue.Float32(0, ItmParam.DeltaOwnBest, -0.49f)));
            Assert.Equal("1.20s", ItmValueRenderer.Render(
                ItmParam.DeltaOwnBest, ItmValue.Float32(0, ItmParam.DeltaOwnBest, 1.2f)));
        }

        [Fact]
        public void Gaps_SignedTwoDecimals_WithSecondsSuffix()
        {
            Assert.Equal("0.92s", ItmValueRenderer.Render(
                ItmParam.CarAhead, ItmValue.Float32(0, ItmParam.CarAhead, 0.92f)));
            Assert.Equal("-6.42s", ItmValueRenderer.Render(
                ItmParam.CarBehind, ItmValue.Float32(0, ItmParam.CarBehind, -6.42f)));
        }

        // ── Car settings ─────────────────────────────────────────────────

        [Fact]
        public void TcAndAbs_ZeroPaddedToTwo()
        {
            Assert.Equal("08", ItmValueRenderer.Render(
                ItmParam.TcSetting, ItmValue.UInt8(0, ItmParam.TcSetting, 8)));
            Assert.Equal("12", ItmValueRenderer.Render(
                ItmParam.AbsSetting, ItmValue.UInt8(0, ItmParam.AbsSetting, 12)));
        }

        [Fact]
        public void EngineMap_AsciiVerbatim()
        {
            Assert.Equal("3", ItmValueRenderer.Render(
                ItmParam.EngineMapping, ItmValue.Ascii(0, ItmParam.EngineMapping, "3")));
            Assert.Equal("10", ItmValueRenderer.Render(
                ItmParam.EngineMapping, ItmValue.Ascii(0, ItmParam.EngineMapping, "10")));
        }

        [Fact]
        public void OilTemp_ZeroPaddedThree_NoSpaceBeforeUnit()
            => Assert.Equal("098C", ItmValueRenderer.Render(
                ItmParam.OilTemp, ItmValue.UInt8(0, ItmParam.OilTemp, 98), "C"));

        [Fact]
        public void BrakeBias_CommaDecimalPercent_FromInt32Tenths()
        {
            Assert.Equal("56,4%", ItmValueRenderer.Render(
                ItmParam.BrakeBias, ItmValue.Int32(0, ItmParam.BrakeBias, 564)));
            Assert.Equal("50,0%", ItmValueRenderer.Render(
                ItmParam.BrakeBias, ItmValue.Int32(0, ItmParam.BrakeBias, 500)));
        }

        // ── Tire temps (spaced unit — unlike oil temp) ───────────────────

        [Fact]
        public void TireTemps_ZeroPaddedThree_WithSpaceBeforeUnit()
        {
            Assert.Equal("075 C", ItmValueRenderer.Render(
                ItmParam.TyreFlTemp, ItmValue.UInt8(0, ItmParam.TyreFlTemp, 75), "C"));
            Assert.Equal("082 C", ItmValueRenderer.Render(
                ItmParam.TyreRlTemp, ItmValue.UInt8(0, ItmParam.TyreRlTemp, 82), "C"));
        }

        // ── Center zone (speed + gear) ───────────────────────────────────

        [Fact]
        public void Speed_PlainInteger()
            => Assert.Equal("268", ItmValueRenderer.Render(
                ItmParam.Speed, ItmValue.Int16(0, ItmParam.Speed, 268)));

        [Fact]
        public void Gear_NumericForm_NeutralReverseAndDigits()
        {
            // Numeric-slot displays (dataType u8): 0 = neutral, 0xFF = reverse.
            Assert.Equal("N", ItmValueRenderer.Render(
                ItmParam.Gear, ItmValue.UInt8(1, ItmParam.Gear, 0), null, 0x12));
            Assert.Equal("R", ItmValueRenderer.Render(
                ItmParam.Gear, ItmValue.UInt8(1, ItmParam.Gear, 0xFF), null, 0x12));
            Assert.Equal("6", ItmValueRenderer.Render(
                ItmParam.Gear, ItmValue.UInt8(1, ItmParam.Gear, 6), null, 0x12));
            // Unknown dataType falls back to the numeric reading.
            Assert.Equal("4", ItmValueRenderer.Render(
                ItmParam.Gear, ItmValue.UInt8(1, ItmParam.Gear, 4)));
        }

        [Fact]
        public void Gear_TextForm_UppercasesTheWireChars()
        {
            // Text-slot displays receive lowercase 'n'/'r' and digits on the wire.
            Assert.Equal("N", ItmValueRenderer.Render(
                ItmParam.Gear, ItmValue.Ascii(1, ItmParam.Gear, "n"), null, 0x11));
            Assert.Equal("R", ItmValueRenderer.Render(
                ItmParam.Gear, ItmValue.Ascii(1, ItmParam.Gear, "r"), null, 0x11));
            Assert.Equal("5", ItmValueRenderer.Render(
                ItmParam.Gear, ItmValue.Ascii(1, ItmParam.Gear, "5"), null, 0x11));
        }

        // ── Placeholders (post-reset / nothing sent) ─────────────────────

        [Fact]
        public void Placeholders_PerFieldFamily()
        {
            Assert.Equal("--- / -", ItmValueRenderer.Placeholder(ItmParam.Lap));
            Assert.Equal("--- / -", ItmValueRenderer.Placeholder(ItmParam.Position));
            Assert.Equal("--- / -", ItmValueRenderer.Placeholder(ItmParam.Fuel));
            Assert.Equal("--:--.-", ItmValueRenderer.Placeholder(ItmParam.LapTime));
            Assert.Equal("--:--.-", ItmValueRenderer.Placeholder(ItmParam.LastLapTime));
            Assert.Equal("--:--.-", ItmValueRenderer.Placeholder(ItmParam.BestLapTime));
            Assert.Equal("-", ItmValueRenderer.Placeholder(ItmParam.Gear));
            Assert.Equal(ItmValueRenderer.DrsDotOff, ItmValueRenderer.Placeholder(ItmParam.DrsZone));
            Assert.Equal(ItmValueRenderer.DrsDotOff, ItmValueRenderer.Placeholder(ItmParam.DrsActive));
            Assert.Equal("---", ItmValueRenderer.Placeholder(ItmParam.Speed));
            Assert.Equal("---", ItmValueRenderer.Placeholder(ItmParam.OilTemp));
        }
    }
}
