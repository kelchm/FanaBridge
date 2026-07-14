using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;
using FanaBridge.Adapters;
using FanaBridge.Protocol;
using GameReaderCommon;
using Xunit;

namespace FanaBridge.Tests
{
    public class ItmTelemetryTests
    {
        // ── GameData construction (see FanatecDisplayDriverTests) ─────────
        // StatusDataBase is abstract with internal setters; its only concrete
        // subclass is StatusData<T>. Close it over object, create uninitialized,
        // and drive the internal setters by reflection.
        private static readonly Type StatusDataType =
            typeof(GameData).Assembly
                .GetType("GameReaderCommon.StatusData`1")
                .MakeGenericType(typeof(object));

        private static object NewStatus() =>
            FormatterServices.GetUninitializedObject(StatusDataType);

        private static void Set(object status, string property, object value) =>
            status.GetType().GetProperty(property).GetSetMethod(true)
                .Invoke(status, new[] { value });

        private static GameData Wrap(object status) =>
            new GameData { NewData = (StatusDataBase)status };

        // ── ItmValue decoders ────────────────────────────────────────────
        private static short AsI16(ItmValue v) => unchecked((short)(ushort)v.Raw);
        private static int AsI32(ItmValue v) => unchecked((int)v.Raw);
        private static byte AsU8(ItmValue v) => (byte)v.Raw;
        private static float AsF32(ItmValue v) => BitConverter.ToSingle(BitConverter.GetBytes(v.Raw), 0);
        private static string AsAscii(ItmValue v)
        {
            var sb = new System.Text.StringBuilder();
            uint raw = v.Raw;
            for (int i = 0; i < v.Size; i++) { sb.Append((char)(byte)(raw & 0xFF)); raw >>= 8; }
            return sb.ToString();
        }

        // ── ParamsFor ────────────────────────────────────────────────────

        [Fact]
        public void ParamsFor_LapInfo_HasExpectedOrder()
        {
            Assert.Equal(
                new ushort[] { 1, 4, 505, 501, 509, 510 },
                ParamsArray(ItmPage.LapInfo));
        }

        [Fact]
        public void ParamsFor_AllPages_LeadWithSpeedAndGear()
        {
            foreach (ItmPage page in new[]
                { ItmPage.LapInfo, ItmPage.FuelErsDrs, ItmPage.CarSettings, ItmPage.LapTimes, ItmPage.TyreTemps })
            {
                var ids = ParamsArray(page);
                Assert.Equal(ItmParam.Speed, ids[0]);
                Assert.Equal(ItmParam.Gear, ids[1]);
            }
        }

        [Fact]
        public void ParamsFor_Legacy_IsEmpty()
        {
            Assert.Empty(ParamsArray(ItmPage.Legacy));
        }

        // ── NameOf ───────────────────────────────────────────────────────

        [Fact]
        public void NameOf_GivesEveryPageANonEmptyDisplayName()
        {
            Assert.Equal("Lap Info", ItmTelemetry.NameOf(ItmPage.LapInfo));
            Assert.Equal("Car Settings", ItmTelemetry.NameOf(ItmPage.CarSettings));
            foreach (ItmPage page in System.Enum.GetValues(typeof(ItmPage)))
                Assert.False(string.IsNullOrEmpty(ItmTelemetry.NameOf(page)));
        }

        private static ushort[] ParamsArray(ItmPage page)
        {
            var list = ItmTelemetry.ParamsFor(page);
            var arr = new ushort[list.Count];
            for (int i = 0; i < list.Count; i++) arr[i] = list[i];
            return arr;
        }

        // ── Catalog ↔ mapper guard ───────────────────────────────────────
        // The Protocol/Adapters split puts the page catalog in ItmTelemetry and the value
        // encoders in ItmTelemetryMapper. Every param a page can carry must be encodable, or a
        // subscribed field would silently show dashes. This test is the drift guard.
        [Fact]
        public void EveryCatalogParam_HasAMapperEncoder()
        {
            foreach (ItmPage page in Enum.GetValues(typeof(ItmPage)))
                foreach (var id in ItmTelemetry.ParamsFor(page))
                    Assert.True(ItmTelemetryMapper.HasEncoder(id),
                        $"paramId {id} on page {page} has no mapper encoder");
        }

        // ── BuildValues guards ───────────────────────────────────────────

        [Fact]
        public void BuildValues_NullData_IsEmpty()
        {
            Assert.Empty(ItmTelemetryMapper.BuildValues(ItmPage.LapInfo, null));
            Assert.Empty(ItmTelemetryMapper.BuildValues(ItmPage.LapInfo, new GameData())); // NewData null
        }

        [Fact]
        public void BuildValues_LegacyPage_IsEmpty()
        {
            Assert.Empty(ItmTelemetryMapper.BuildValues(ItmPage.Legacy, Wrap(NewStatus())));
        }

        [Fact]
        public void BuildValues_AssignsSequentialHandles()
        {
            var values = ItmTelemetryMapper.BuildValues(ItmPage.LapInfo, Wrap(NewStatus()));

            for (int i = 0; i < values.Count; i++)
                Assert.Equal((byte)i, values[i].Handle);
        }

        [Fact]
        public void BuildValues_HandleBase_OffsetsHandles()
        {
            var values = ItmTelemetryMapper.BuildValues(ItmPage.LapInfo, Wrap(NewStatus()), handleBase: 2);

            for (int i = 0; i < values.Count; i++)
                Assert.Equal((byte)(2 + i), values[i].Handle);
        }

        // ── LapInfo encoding ─────────────────────────────────────────────

        [Fact]
        public void LapInfo_EncodesHeaderAndTimingFields()
        {
            var s = NewStatus();
            Set(s, "SpeedLocal", 142.4);
            Set(s, "Gear", "3");
            Set(s, "CurrentLap", 5);
            Set(s, "Position", 7);
            Set(s, "CurrentLapTime", TimeSpan.FromSeconds(83.5));
            Set(s, "LastLapTime", TimeSpan.FromSeconds(82.25));

            var v = ItmTelemetryMapper.BuildValues(ItmPage.LapInfo, Wrap(s));

            Assert.Equal(ItmParam.Speed, v[0].ParamId);
            Assert.Equal((short)142, AsI16(v[0]));     // SpeedLocal rounded
            Assert.Equal(ItmParam.Gear, v[1].ParamId);
            Assert.Equal((byte)3, AsU8(v[1]));
            Assert.Equal(ItmParam.Lap, v[2].ParamId);
            Assert.Equal((byte)5, AsU8(v[2]));
            Assert.Equal(ItmParam.Position, v[3].ParamId);
            Assert.Equal((byte)7, AsU8(v[3]));
            Assert.Equal(ItmParam.LapTime, v[4].ParamId);
            Assert.Equal(83.5f, AsF32(v[4]), 3);
            Assert.Equal(ItmParam.LastLapTime, v[5].ParamId);
            Assert.Equal(82.25f, AsF32(v[5]), 3);
        }

        [Fact]
        public void NonFiniteTelemetry_EncodesAsZero()
        {
            // NaN/Infinity must not slip through the clamp helpers into an undefined cast
            // (which could send a garbage value to the firmware).
            var s = NewStatus();
            Set(s, "SpeedLocal", double.NaN);
            Set(s, "OilTemperature", double.PositiveInfinity);
            Set(s, "ERSPercent", double.NaN);

            var lap = ItmTelemetryMapper.BuildValues(ItmPage.LapInfo, Wrap(s));
            Assert.Equal((short)0, AsI16(lap[0]));   // ClampSpeed(NaN) -> 0

            Assert.True(ItmTelemetryMapper.TryEncodeParam(ItmParam.OilTemp, 5, Wrap(s), out var oil));
            Assert.Equal((byte)0, AsU8(oil));        // ClampByte(Inf) -> 0

            Assert.True(ItmTelemetryMapper.TryEncodeParam(ItmParam.ErsLevel, 3, Wrap(s), out var ers));
            Assert.Equal(0, AsI32(ers));             // SafeRound(NaN) -> 0
        }

        [Theory]
        [InlineData("N", 0)]
        [InlineData("", 0)]
        [InlineData("R", 0xFF)]      // reverse sentinel (-1 as uint8, rendered "r")
        [InlineData("reverse", 0xFF)]
        [InlineData("4", 4)]
        [InlineData("garbage", 0)]
        public void Gear_ParsesToUint8(string gear, int expected)
        {
            var s = NewStatus();
            Set(s, "Gear", gear);

            var v = ItmTelemetryMapper.BuildValues(ItmPage.LapInfo, Wrap(s));

            Assert.Equal((byte)expected, AsU8(v[1]));
        }

        [Theory]
        [InlineData(-10, 0)]              // negative speed clamps to 0
        [InlineData(40000, short.MaxValue)] // beyond Int16 clamps
        public void Speed_ClampsToInt16Range(double speedLocal, int expected)
        {
            var s = NewStatus();
            Set(s, "SpeedLocal", speedLocal);

            var v = ItmTelemetryMapper.BuildValues(ItmPage.LapInfo, Wrap(s));

            Assert.Equal((short)expected, AsI16(v[0]));
        }

        // ── CarSettings encoding ─────────────────────────────────────────

        [Fact]
        public void CarSettings_BrakeBias_SentAsTenthsOfPercentInt32()
        {
            var s = NewStatus();
            Set(s, "BrakeBias", 51.2);   // percentage from SimHub

            var v = ItmTelemetryMapper.BuildValues(ItmPage.CarSettings, Wrap(s));

            // Order (per capture): Speed, Gear, TC, ABS, EngineMap, OilTemp, BrakeBias.
            // Int32 in tenths of a percent — 51.2% => 512 (confirmed by capture).
            Assert.Equal(ItmParam.BrakeBias, v[6].ParamId);
            Assert.Equal(4, v[6].Size);
            Assert.Equal(512, AsI32(v[6]));
        }

        [Fact]
        public void CarSettings_BrakeBias_ClampsTo1000()
        {
            var s = NewStatus();
            Set(s, "BrakeBias", 150.0);   // absurd; clamps to 1000 (100.0%)

            var v = ItmTelemetryMapper.BuildValues(ItmPage.CarSettings, Wrap(s));

            Assert.Equal(1000, AsI32(v[6]));
        }

        [Fact]
        public void CarSettings_EncodesSettingsBytes()
        {
            var s = NewStatus();
            Set(s, "TCLevel", 4);
            Set(s, "ABSLevel", 2);
            Set(s, "EngineMap", 6);
            Set(s, "OilTemperature", 98.7);

            var v = ItmTelemetryMapper.BuildValues(ItmPage.CarSettings, Wrap(s));

            Assert.Equal(ItmParam.TcSetting, v[2].ParamId);
            Assert.Equal((byte)4, AsU8(v[2]));
            Assert.Equal(ItmParam.AbsSetting, v[3].ParamId);
            Assert.Equal((byte)2, AsU8(v[3]));
            // ENGINE_MAPPING is ASCII text — map 6 travels as the single byte '6'.
            Assert.Equal(ItmParam.EngineMapping, v[4].ParamId);
            Assert.Equal((byte)1, v[4].Size);
            Assert.Equal("6", AsAscii(v[4]));
            Assert.Equal(ItmParam.OilTemp, v[5].ParamId);
            Assert.Equal((byte)99, AsU8(v[5]));   // 98.7 rounds to 99
        }

        // ── FuelErsDrs encoding ──────────────────────────────────────────

        [Fact]
        public void FuelErsDrs_EncodesFuelErsAndDrsFlags()
        {
            var s = NewStatus();
            Set(s, "Fuel", 12.5);
            Set(s, "ERSPercent", 73.0);
            Set(s, "DRSAvailable", 1);
            Set(s, "DRSEnabled", 0);

            var v = ItmTelemetryMapper.BuildValues(ItmPage.FuelErsDrs, Wrap(s));

            Assert.Equal(ItmParam.Fuel, v[2].ParamId);
            Assert.Equal(12.5f, AsF32(v[2]), 3);
            Assert.Equal(ItmParam.ErsLevel, v[3].ParamId);
            Assert.Equal(73, AsI32(v[3]));
            Assert.Equal(ItmParam.DrsZone, v[4].ParamId);
            Assert.Equal((byte)1, AsU8(v[4]));
            Assert.Equal(ItmParam.DrsActive, v[5].ParamId);
            Assert.Equal((byte)0, AsU8(v[5]));
        }

        // ── Value rounding (dodge the firmware's per-digit carry bug) ─────
        // The ITM firmware renders a decimal field as whole.round(frac*10^N) with NO
        // carry into the integer part, so an unrounded value just below a round-up
        // boundary misrenders (16.9692 -> "16.10"). The official app pre-rounds each
        // float to its display precision; we match it. Fuel = 1 decimal; delta and the
        // car-gap fields = 2 decimals. Time fields truncate in firmware, so they are
        // deliberately left unrounded.

        [Fact]
        public void Fuel_IsRoundedToOneDecimal()
        {
            var s = NewStatus();
            Set(s, "Fuel", 16.9692);
            Assert.True(ItmTelemetryMapper.TryEncodeParam(ItmParam.Fuel, 8, Wrap(s), out var v));
            Assert.Equal(17.0f, AsF32(v), 3);   // 16.9692 -> 17.0, not the raw boundary value
        }

        [Fact]
        public void DeltaOwnBest_IsRoundedToTwoDecimals()
        {
            var s = NewStatus();
            Set(s, "DeltaToSessionBest", (double?)1.997);
            Assert.True(ItmTelemetryMapper.TryEncodeParam(ItmParam.DeltaOwnBest, 12, Wrap(s), out var v));
            Assert.Equal(2.0f, AsF32(v), 3);
        }

        [Fact]
        public void CarGaps_AreRoundedToTwoDecimals()
        {
            var s = NewStatus();
            Set(s, "OpponentsAheadOnTrack", OpponentList(1.997));    // -> 2.00
            Set(s, "OpponentsBehindOnTrack", OpponentList(1.992));   // -> 1.99
            Assert.True(ItmTelemetryMapper.TryEncodeParam(ItmParam.CarAhead, 10, Wrap(s), out var ahead));
            Assert.True(ItmTelemetryMapper.TryEncodeParam(ItmParam.CarBehind, 11, Wrap(s), out var behind));
            Assert.Equal(2.0f, AsF32(ahead), 3);
            Assert.Equal(1.99f, AsF32(behind), 3);
        }

        // ── TyreTemps encoding ───────────────────────────────────────────

        // ── Subscription report parsing (firmware-driven path) ───────────

        // Real tyre-page subscription report from the official-software capture:
        // FF 05 01, then [03][fwHandle][paramId-LE][unit] entries.
        private static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        [Fact]
        public void ParseSubscriptionReport_TyrePage_DecodesHandlesAndParams()
        {
            // h0=SPEED, h1=GEAR, h0x82=FL(42), h0x83=RL(48) — four complete entries.
            var report = HexToBytes("ff05010300010034030104001203822a00320383300032");
            var subs = ItmTelemetry.ParseSubscriptionReport(report, report.Length);

            Assert.Equal(4, subs.Count);
            Assert.Equal(0, subs[0].Handle); Assert.Equal(ItmParam.Speed, subs[0].ParamId);
            Assert.Equal(1, subs[1].Handle); Assert.Equal(ItmParam.Gear, subs[1].ParamId);
            // 0x82 -> host handle 2 (slot-marker bit cleared)
            Assert.Equal(2, subs[2].Handle); Assert.Equal(ItmParam.TyreFlTemp, subs[2].ParamId);
            Assert.Equal(3, subs[3].Handle); Assert.Equal(ItmParam.TyreRlTemp, subs[3].ParamId);
            Assert.All(subs, s => Assert.False(s.IsUnsubscribe));
        }

        [Fact]
        public void ParseSubscriptionReport_CapturesDeclaredDataType()
        {
            // Same tyre-page report: each entry's 5th byte is the firmware's declared slot
            // type (0x34 = i16 speed, 0x12 = u8 gear, 0x32 = u8 temps).
            var report = HexToBytes("ff05010300010034030104001203822a00320383300032");
            var subs = ItmTelemetry.ParseSubscriptionReport(report, report.Length);

            Assert.Equal((byte)0x34, subs[0].DataType);
            Assert.Equal((byte)0x12, subs[1].DataType);
            Assert.Equal((byte)0x32, subs[2].DataType);
            Assert.Equal((byte)0x32, subs[3].DataType);
        }

        [Fact]
        public void IsTextType_LowNibbleOne_Only()
        {
            Assert.True(ItmTelemetry.IsTextType(0x01));
            Assert.True(ItmTelemetry.IsTextType(0x11));
            Assert.False(ItmTelemetry.IsTextType(0x12));   // PBME GEAR (u8)
            Assert.False(ItmTelemetry.IsTextType(0x34));   // SPEED (i16)
            Assert.False(ItmTelemetry.IsTextType(0x00));   // unknown / seeded
        }

        [Fact]
        public void ParseSubscriptionReport_DecodesUnsubscribe()
        {
            // FF 05 01, then [03][h][FF FF][unit] entries = unsubscribe
            var report = HexToBytes("ff05010300ffff340301ffff12");
            var subs = ItmTelemetry.ParseSubscriptionReport(report, report.Length);

            Assert.Equal(2, subs.Count);
            Assert.True(subs[0].IsUnsubscribe);
            Assert.True(subs[1].IsUnsubscribe);
            Assert.Equal(0, subs[0].Handle);
            Assert.Equal(1, subs[1].Handle);
        }

        [Fact]
        public void ParseSubscriptionReport_NonItm_IsEmpty()
        {
            Assert.Empty(ItmTelemetry.ParseSubscriptionReport(HexToBytes("ff080c00"), 4));
            Assert.Empty(ItmTelemetry.ParseSubscriptionReport(null, 0));
        }

        [Fact]
        public void ParseSubscriptionReport_FiltersToGivenDevice()
        {
            // One entry for device 4 (Bentley): [04][h0][SPEED=0001][unit 34]
            var report = HexToBytes("ff0501" + "0400010034");

            // The default device (3) skips the device-4 entry.
            Assert.Empty(ItmTelemetry.ParseSubscriptionReport(report, report.Length));

            // Asking for device 4 accepts it.
            var subs = ItmTelemetry.ParseSubscriptionReport(report, report.Length, (byte)4);
            Assert.Single(subs);
            Assert.Equal(ItmParam.Speed, subs[0].ParamId);
            Assert.Equal(0, subs[0].Handle);
        }

        [Fact]
        public void ParseSubscriptionReport_InterleavedDevices_CollectsAllMatching()
        {
            // dev3 h0 SPEED, dev4 h0 SPEED (other display), dev3 h1 GEAR. The middle entry must be
            // skipped, not stop the scan, so both device-3 entries are still collected.
            var report = HexToBytes("ff0501" + "0300010034" + "0400010034" + "0301040034");
            var subs = ItmTelemetry.ParseSubscriptionReport(report, report.Length);   // default device 3

            Assert.Equal(2, subs.Count);
            Assert.Equal(0, subs[0].Handle);
            Assert.Equal(ItmParam.Speed, subs[0].ParamId);
            Assert.Equal(1, subs[1].Handle);
            Assert.Equal(ItmParam.Gear, subs[1].ParamId);
        }

        [Fact]
        public void TryEncodeParam_KnownParam_Encodes()
        {
            var s = NewStatus();
            Set(s, "TyreTemperatureFrontLeft", 88.0);

            Assert.True(ItmTelemetryMapper.TryEncodeParam(ItmParam.TyreFlTemp, 2, Wrap(s), out var v));
            Assert.Equal(2, v.Handle);
            Assert.Equal(ItmParam.TyreFlTemp, v.ParamId);
            Assert.Equal((byte)88, AsU8(v));
        }

        [Fact]
        public void TryEncodeParam_UnknownParam_ReturnsFalse()
        {
            Assert.False(ItmTelemetryMapper.TryEncodeParam(9999, 0, Wrap(NewStatus()), out _));
        }

        // ── GEAR per-display encoding (declared dataType steers the wire form) ──

        [Theory]
        [InlineData("N", "n")]
        [InlineData("", "n")]
        [InlineData("3", "3")]
        [InlineData("9", "9")]
        [InlineData("R", "r")]
        [InlineData("reverse", "r")]
        [InlineData("garbage", "n")]
        public void TryEncodeParam_Gear_TextSlot_SendsAsciiChar(string gear, string expected)
        {
            // A display that declares GEAR as text (e.g. Formula V3) takes the ASCII form
            // the official software sends: lowercase 'n'/'r', digits literal.
            var s = NewStatus();
            Set(s, "Gear", gear);

            Assert.True(ItmTelemetryMapper.TryEncodeParam(ItmParam.Gear, 1, Wrap(s), 0x11, out var v));
            Assert.Equal((byte)1, v.Size);
            Assert.Equal(expected, AsAscii(v));
        }

        [Theory]
        [InlineData(0x12)]   // PBME's declared u8 type
        [InlineData(0x00)]   // unknown (host-seeded before the push lands)
        public void TryEncodeParam_Gear_NumericOrUnknownSlot_SendsNumericByte(int dataType)
        {
            var s = NewStatus();
            Set(s, "Gear", "3");

            Assert.True(ItmTelemetryMapper.TryEncodeParam(ItmParam.Gear, 1, Wrap(s), (byte)dataType, out var v));
            Assert.Equal((byte)1, v.Size);
            Assert.Equal((byte)3, AsU8(v));
        }

        [Fact]
        public void TryEncodeParam_EngineMapping_SentAsAsciiText()
        {
            // ENGINE_MAPPING is ASCII on the wire: map 3 => single byte '3' (0x33).
            var s = NewStatus();
            Set(s, "EngineMap", 3);
            Assert.True(ItmTelemetryMapper.TryEncodeParam(ItmParam.EngineMapping, 4, Wrap(s), out var v));
            Assert.Equal((byte)1, v.Size);
            Assert.Equal("3", AsAscii(v));
        }

        [Fact]
        public void TryEncodeParam_EngineMapping_TwoDigitIsTwoBytes()
        {
            // Map 10 => two bytes '1','0' (matches official capture: p26 sz2 = 3130).
            var s = NewStatus();
            Set(s, "EngineMap", 10);
            Assert.True(ItmTelemetryMapper.TryEncodeParam(ItmParam.EngineMapping, 4, Wrap(s), out var v));
            Assert.Equal((byte)2, v.Size);
            Assert.Equal("10", AsAscii(v));
        }

        // ── Car ahead / behind gaps ──────────────────────────────────────

        private static readonly Type OpponentType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.Opponent");

        private static IList OpponentList(params double?[] relGaps)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(OpponentType));
            var setter = OpponentType.GetProperty("RelativeGapToPlayer").GetSetMethod(true);
            foreach (var g in relGaps)
            {
                var opp = FormatterServices.GetUninitializedObject(OpponentType);
                setter.Invoke(opp, new object?[] { g });
                list.Add(opp);
            }
            return list;
        }

        [Fact]
        public void CarAhead_NoOpponents_IsZero()
        {
            Assert.True(ItmTelemetryMapper.TryEncodeParam(ItmParam.CarAhead, 4, Wrap(NewStatus()), out var v));
            Assert.Equal(0f, AsF32(v), 3);
        }

        [Fact]
        public void CarAheadBehind_UseNearestOpponentGap()
        {
            var s = NewStatus();
            Set(s, "OpponentsAheadOnTrack", OpponentList(2.5, 0.8, 5.0));    // nearest = 0.8
            Set(s, "OpponentsBehindOnTrack", OpponentList(-1.2, -3.4));     // nearest = 1.2 (abs)

            // Both gaps are positive magnitudes — the firmware renders the field's own sign,
            // so a negative value would show a doubled sign on the display.
            Assert.True(ItmTelemetryMapper.TryEncodeParam(ItmParam.CarAhead, 4, Wrap(s), out var ahead));
            Assert.Equal(0.8f, AsF32(ahead), 3);
            Assert.True(ItmTelemetryMapper.TryEncodeParam(ItmParam.CarBehind, 5, Wrap(s), out var behind));
            Assert.Equal(1.2f, AsF32(behind), 3);
        }

        // ── Total suffixes (lap/position) ────────────────────────────────

        [Fact]
        public void TotalSuffix_ShownForPlausibleTotals()
        {
            var s = NewStatus();
            Set(s, "TotalLaps", 34); Set(s, "CurrentLap", 5);
            Set(s, "OpponentsCount", 20); Set(s, "Position", 7);

            Assert.True(ItmTelemetryMapper.TryGetTotalSuffix(ItmParam.Lap, Wrap(s), out var lap));
            Assert.Equal("/34", lap);
            Assert.True(ItmTelemetryMapper.TryGetTotalSuffix(ItmParam.Position, Wrap(s), out var pos));
            Assert.Equal("/20", pos);   // field = OpponentsCount (list includes the player)
        }

        [Fact]
        public void TotalSuffix_SuppressedWhenGameLacksRaceStructure()
        {
            // Forza-like: TotalLaps 0, only 1 reported opponent while in P7.
            var s = NewStatus();
            Set(s, "TotalLaps", 0); Set(s, "CurrentLap", 1);
            Set(s, "OpponentsCount", 1); Set(s, "Position", 7);

            Assert.False(ItmTelemetryMapper.TryGetTotalSuffix(ItmParam.Lap, Wrap(s), out _));
            Assert.False(ItmTelemetryMapper.TryGetTotalSuffix(ItmParam.Position, Wrap(s), out _));
        }

        [Fact]
        public void TotalSuffix_NoneForParamWithoutTotal()
        {
            Assert.False(ItmTelemetryMapper.TryGetTotalSuffix(ItmParam.Speed, Wrap(NewStatus()), out _));
        }

        [Fact]
        public void TotalSuffix_FuelShowsCapacity()
        {
            // The stock app renders fuel as value/capacity (e.g. "/23"), not a unit label.
            var s = NewStatus();
            Set(s, "Fuel", 12.0); Set(s, "MaxFuel", 23.0);
            Assert.True(ItmTelemetryMapper.TryGetTotalSuffix(ItmParam.Fuel, Wrap(s), out var fuel));
            Assert.Equal("/23", fuel);
        }

        [Fact]
        public void TotalSuffix_FuelSuppressedWhenNoCapacity()
        {
            // Games that don't report a tank capacity get no "/0" — fall back to bare value.
            var s = NewStatus();
            Set(s, "Fuel", 12.0); Set(s, "MaxFuel", 0.0);
            Assert.False(ItmTelemetryMapper.TryGetTotalSuffix(ItmParam.Fuel, Wrap(s), out _));
        }

        [Fact]
        public void TyreTemps_EncodesAllFourCorners()
        {
            var s = NewStatus();
            Set(s, "TyreTemperatureFrontLeft", 85.0);
            Set(s, "TyreTemperatureFrontRight", 86.0);
            Set(s, "TyreTemperatureRearLeft", 90.0);
            Set(s, "TyreTemperatureRearRight", 91.0);

            var v = ItmTelemetryMapper.BuildValues(ItmPage.TyreTemps, Wrap(s));

            // Order (per capture): FL, RL, FR, RR
            Assert.Equal(ItmParam.TyreFlTemp, v[2].ParamId);
            Assert.Equal((byte)85, AsU8(v[2]));
            Assert.Equal(ItmParam.TyreRlTemp, v[3].ParamId);
            Assert.Equal((byte)90, AsU8(v[3]));
            Assert.Equal(ItmParam.TyreFrTemp, v[4].ParamId);
            Assert.Equal((byte)86, AsU8(v[4]));
            Assert.Equal(ItmParam.TyreRrTemp, v[5].ParamId);
            Assert.Equal((byte)91, AsU8(v[5]));
        }
    }
}
