using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using FanaBridge.Tests.Display.TestSupport;
using GameReaderCommon;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>Phase E7 B: itmField read-path seam — pure plumbing; nothing live consumes it.</summary>
    public class ItmFieldValueReaderTests
    {
        // GameData construction (see ItmTelemetryTests).
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

        [Fact]
        public void Buffer_PublishAndRead()
        {
            var buf = new ItmFieldValueBuffer();
            Assert.False(buf.TryGetNumber(0x42, out _));
            buf.Publish(0x42, 17.5);
            Assert.True(buf.TryGetNumber(0x42, out double v));
            Assert.Equal(17.5, v);
        }

        [Fact]
        public void Buffer_Clear_WipesValues()
        {
            var buf = new ItmFieldValueBuffer();
            buf.Publish(1, 1);
            buf.Clear();
            Assert.False(buf.TryGetNumber(1, out _));
        }

        [Fact]
        public void Composite_ItmField_ReadsBuffer()
        {
            var buf = new ItmFieldValueBuffer();
            buf.Publish(66, 9);
            var reader = new PropertyReaderWithItmFields(new DictReader { RequireKind = PropertyKind.BuiltIn }, buf);
            var spec = new PropertySpec { Kind = PropertyKind.ItmField, Name = "66" };
            Assert.True(reader.TryGetNumber(spec, out double v));
            Assert.Equal(9, v);
        }

        [Fact]
        public void Composite_ItmField_HexName()
        {
            var buf = new ItmFieldValueBuffer();
            buf.Publish(0x42, 3);
            var reader = new PropertyReaderWithItmFields(new DictReader { RequireKind = PropertyKind.BuiltIn }, buf);
            var spec = new PropertySpec { Kind = PropertyKind.ItmField, Name = "0x42" };
            Assert.True(reader.TryGetNumber(spec, out double v));
            Assert.Equal(3, v);
        }

        [Fact]
        public void Composite_ItmField_Missing_ReturnsFalse()
        {
            var reader = new PropertyReaderWithItmFields(new DictReader { RequireKind = PropertyKind.BuiltIn }, new ItmFieldValueBuffer());
            var spec = new PropertySpec { Kind = PropertyKind.ItmField, Name = "1" };
            Assert.False(reader.TryGetNumber(spec, out _));
        }

        [Fact]
        public void Composite_ItmField_NullSink_ReturnsFalse()
        {
            var reader = new PropertyReaderWithItmFields(new DictReader { RequireKind = PropertyKind.BuiltIn }, itmFields: null);
            var spec = new PropertySpec { Kind = PropertyKind.ItmField, Name = "1" };
            Assert.False(reader.TryGetNumber(spec, out _));
        }

        [Fact]
        public void Composite_BuiltIn_DelegatesToInner()
        {
            var inner = new DictReader { RequireKind = PropertyKind.BuiltIn };
            inner.Numbers[BuiltInProperties.Fuel] = 12;
            var reader = new PropertyReaderWithItmFields(inner, new ItmFieldValueBuffer());
            var spec = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = BuiltInProperties.Fuel };
            Assert.True(reader.TryGetNumber(spec, out double v));
            Assert.Equal(12, v);
        }

        [Fact]
        public void TryParseParamId_DecimalAndHex()
        {
            Assert.True(PropertyReaderWithItmFields.TryParseParamId("66", out ushort a));
            Assert.Equal((ushort)66, a);
            Assert.True(PropertyReaderWithItmFields.TryParseParamId("0x42", out ushort b));
            Assert.Equal((ushort)0x42, b);
            Assert.False(PropertyReaderWithItmFields.TryParseParamId("self", out _));
            Assert.False(PropertyReaderWithItmFields.TryParseParamId("", out _));
        }

        // ── Natural pre-wire scalar publish (E7-OPUS-01/02/15) ───────────

        [Fact]
        public void Publish_Fuel_NaturalScalar_NotWireBits()
        {
            var buf = new ItmFieldValueBuffer();
            var mapper = new ItmTelemetryMapper { ParamValueSink = buf };
            var s = NewStatus();
            Set(s, "Fuel", 4.2);
            Assert.True(mapper.TryEncodeParam(ItmParam.Fuel, 1, Wrap(s), out _));
            Assert.True(buf.TryGetNumber(ItmParam.Fuel, out double v));
            Assert.Equal(4.2, v, 5);
        }

        [Fact]
        public void Publish_BrakeBias_Percent_NotTenths()
        {
            var buf = new ItmFieldValueBuffer();
            var mapper = new ItmTelemetryMapper { ParamValueSink = buf };
            var s = NewStatus();
            Set(s, "BrakeBias", 51.2);
            Assert.True(mapper.TryEncodeParam(ItmParam.BrakeBias, 1, Wrap(s), out var wire));
            // Wire is tenths (512); published natural is percent (51.2).
            Assert.Equal(512, unchecked((int)wire.Raw));
            Assert.True(buf.TryGetNumber(ItmParam.BrakeBias, out double v));
            Assert.Equal(51.2, v, 5);
        }

        [Fact]
        public void Publish_CarAhead_PositiveGap_NotNegatedWire()
        {
            var buf = new ItmFieldValueBuffer();
            var mapper = new ItmTelemetryMapper { ParamValueSink = buf };
            // Without opponents, gap is 0 — pin sign agreement via override path below;
            // built-in with empty opponents still publishes 0 (not NaN / not bits).
            var s = NewStatus();
            Assert.True(mapper.TryEncodeParam(ItmParam.CarAhead, 1, Wrap(s), out _));
            Assert.True(buf.TryGetNumber(ItmParam.CarAhead, out double v));
            Assert.Equal(0.0, v, 5);
        }

        [Fact]
        public void Publish_AsciiParams_GearAndEngineMapping_Skipped()
        {
            var buf = new ItmFieldValueBuffer();
            var mapper = new ItmTelemetryMapper { ParamValueSink = buf };
            var s = NewStatus();
            Set(s, "Gear", "N");
            Set(s, "EngineMap", 10);
            Assert.True(mapper.TryEncodeParam(ItmParam.Gear, 1, Wrap(s), out _));
            Assert.True(mapper.TryEncodeParam(ItmParam.EngineMapping, 2, Wrap(s), out _));
            Assert.False(buf.TryGetNumber(ItmParam.Gear, out _));
            Assert.False(buf.TryGetNumber(ItmParam.EngineMapping, out _));
        }

        [Fact]
        public void Publish_Identical_WithAndWithoutOverride_BrakeBiasAndCarAhead()
        {
            var s = NewStatus();
            Set(s, "BrakeBias", 51.2);

            var builtIn = new ItmFieldValueBuffer();
            var m1 = new ItmTelemetryMapper { ParamValueSink = builtIn };
            Assert.True(m1.TryEncodeParam(ItmParam.BrakeBias, 1, Wrap(s), out _));
            Assert.True(builtIn.TryGetNumber(ItmParam.BrakeBias, out double b1));

            var withOverride = new ItmFieldValueBuffer();
            var m2 = new ItmTelemetryMapper { ParamValueSink = withOverride };
            var props = new DictReader { RequireKind = PropertyKind.BuiltIn };
            props.Numbers["Fuel"] = 51.2; // any numeric source
            m2.Configure(
                new Dictionary<ushort, FieldMapping>
                {
                    [ItmParam.BrakeBias] = new FieldMapping
                    {
                        Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = "Fuel" },
                    },
                },
                props);
            // Override source name is Fuel built-in but we only care the scalar is 51.2.
            // DictReader keys by BuiltIn name — use a real built-in name mapped to 51.2.
            props.Numbers[BuiltInProperties.Fuel] = 51.2;
            m2.Configure(
                new Dictionary<ushort, FieldMapping>
                {
                    [ItmParam.BrakeBias] = new FieldMapping
                    {
                        Source = new PropertySpec
                        {
                            Kind = PropertyKind.BuiltIn,
                            Name = BuiltInProperties.Fuel,
                        },
                    },
                },
                props);
            Assert.True(m2.TryEncodeParam(ItmParam.BrakeBias, 1, Wrap(s), out _));
            Assert.True(withOverride.TryGetNumber(ItmParam.BrakeBias, out double b2));
            Assert.Equal(b1, b2, 5);
            Assert.Equal(51.2, b1, 5);

            // CarAhead: built-in and override both publish +gap natural unit.
            var c1 = new ItmFieldValueBuffer();
            var m3 = new ItmTelemetryMapper { ParamValueSink = c1 };
            Assert.True(m3.TryEncodeParam(ItmParam.CarAhead, 1, Wrap(s), out _));
            Assert.True(c1.TryGetNumber(ItmParam.CarAhead, out double a1));

            var c2 = new ItmFieldValueBuffer();
            var m4 = new ItmTelemetryMapper { ParamValueSink = c2 };
            props.Numbers[BuiltInProperties.Fuel] = 1.25;
            m4.Configure(
                new Dictionary<ushort, FieldMapping>
                {
                    [ItmParam.CarAhead] = new FieldMapping
                    {
                        Source = new PropertySpec
                        {
                            Kind = PropertyKind.BuiltIn,
                            Name = BuiltInProperties.Fuel,
                        },
                    },
                },
                props);
            Assert.True(m4.TryEncodeParam(ItmParam.CarAhead, 1, Wrap(s), out _));
            Assert.True(c2.TryGetNumber(ItmParam.CarAhead, out double a2));
            Assert.Equal(1.25, a2, 5);
            // Built-in with no opponents is 0; override is 1.25 — units match (positive gap).
            Assert.True(a1 >= 0);
            Assert.True(a2 > 0);
        }

        [Fact]
        public void BuildValues_PublishesBuiltIn_SameSiteAsTryEncode()
        {
            var buf = new ItmFieldValueBuffer();
            var mapper = new ItmTelemetryMapper { ParamValueSink = buf };
            var s = NewStatus();
            Set(s, "Fuel", 4.2);
            Set(s, "SpeedLocal", 100.0);
            Set(s, "Gear", "3");
            Set(s, "CurrentLap", 1);
            Set(s, "Position", 2);
            Set(s, "CurrentLapTime", TimeSpan.FromSeconds(10));
            Set(s, "LastLapTime", TimeSpan.FromSeconds(20));
            IReadOnlyList<ItmValue> values = mapper.BuildValues(ItmPage.FuelErsDrs, Wrap(s));
            Assert.NotEmpty(values);
            Assert.True(buf.TryGetNumber(ItmParam.Fuel, out double v));
            Assert.Equal(4.2, v, 5);
            // Gear is ASCII — not published even via BuildValues.
            Assert.False(buf.TryGetNumber(ItmParam.Gear, out _));
        }
    }
}
