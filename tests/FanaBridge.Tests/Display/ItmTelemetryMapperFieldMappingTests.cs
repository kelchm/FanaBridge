using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using GameReaderCommon;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Phase 6b: field-mapping overrides and the format layer on
    /// <see cref="ItmTelemetryMapper"/> — source hit/miss/fallback, format vocabulary
    /// effective-format resolution (incl. overridden-source default-bare and the
    /// Show*Total toggle migration), and suffix emit.
    /// </summary>
    public class ItmTelemetryMapperFieldMappingTests
    {
        // ── GameData harness ─────────────────────────────────────────────

        private static readonly Type StatusDataType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.StatusData`1")!
                .MakeGenericType(typeof(object));

        private static object NewStatus() =>
            FormatterServices.GetUninitializedObject(StatusDataType);

        private static void Set(object status, string property, object value) =>
            status.GetType().GetProperty(property)!.GetSetMethod(true)!
                .Invoke(status, new[] { value });

        private static GameData Wrap(object status) =>
            new GameData { NewData = (StatusDataBase)status };

        private static float AsF32(ItmValue v) =>
            BitConverter.ToSingle(BitConverter.GetBytes(v.Raw), 0);

        private static byte AsU8(ItmValue v) => (byte)v.Raw;

        // ── Fake property reader ─────────────────────────────────────────

        private sealed class FakeReader : IPropertyReader
        {
            public readonly Dictionary<string, double?> Numbers =
                new Dictionary<string, double?>(StringComparer.Ordinal);

            public bool TryGetNumber(PropertySpec spec, out double value)
            {
                value = 0;
                if (spec == null || string.IsNullOrEmpty(spec.Name)) return false;
                if (!Numbers.TryGetValue(spec.Name, out var n) || n == null) return false;
                value = n.Value;
                return true;
            }

            public bool TryGetBool(PropertySpec spec, out bool value)
            {
                value = false;
                if (!TryGetNumber(spec, out double n)) return false;
                value = n != 0;
                return true;
            }
        }

        private static FieldMapping BuiltIn(string name, string? format = null) =>
            new FieldMapping
            {
                Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = name },
                Format = format,
            };

        private static FieldMapping SimHub(string name, string? format = null) =>
            new FieldMapping
            {
                Source = new PropertySpec { Kind = PropertyKind.SimHubProperty, Name = name },
                Format = format,
            };

        // ── Override resolution ──────────────────────────────────────────

        [Fact]
        public void Override_Hit_EncodesThroughTypedPath()
        {
            var mapper = new ItmTelemetryMapper();
            var reader = new FakeReader();
            // Fuel override at 16.9692 must still round 1dp → 17.0 Float32.
            reader.Numbers["Custom.Fuel"] = 16.9692;
            mapper.Configure(
                new Dictionary<ushort, FieldMapping>
                {
                    [ItmParam.Fuel] = SimHub("Custom.Fuel"),
                },
                reader);

            var s = NewStatus();
            Set(s, "Fuel", 5.0);   // built-in would encode 5.0
            Assert.True(mapper.TryEncodeParam(ItmParam.Fuel, 2, Wrap(s), out var v));
            Assert.Equal(17.0f, AsF32(v), 3);
        }

        [Fact]
        public void Override_Miss_FallsBackToBuiltIn_NotStale()
        {
            var mapper = new ItmTelemetryMapper();
            var reader = new FakeReader();
            // First frame: property present → override.
            reader.Numbers["Custom.Lap"] = 9;
            mapper.Configure(
                new Dictionary<ushort, FieldMapping>
                {
                    [ItmParam.Lap] = SimHub("Custom.Lap"),
                },
                reader);

            var s = NewStatus();
            Set(s, "CurrentLap", 3);
            Assert.True(mapper.TryEncodeParam(ItmParam.Lap, 2, Wrap(s), out var hit));
            Assert.Equal((byte)9, AsU8(hit));

            // Second frame: property gone → built-in (3), never the prior override (9).
            reader.Numbers.Remove("Custom.Lap");
            Assert.True(mapper.TryEncodeParam(ItmParam.Lap, 2, Wrap(s), out var miss));
            Assert.Equal((byte)3, AsU8(miss));
        }

        [Fact]
        public void Override_NoReader_UsesBuiltIn()
        {
            var mapper = new ItmTelemetryMapper();
            mapper.Configure(
                new Dictionary<ushort, FieldMapping>
                {
                    [ItmParam.Position] = SimHub("Custom.Pos"),
                },
                properties: null);

            var s = NewStatus();
            Set(s, "Position", 7);
            Assert.True(mapper.TryEncodeParam(ItmParam.Position, 3, Wrap(s), out var v));
            Assert.Equal((byte)7, AsU8(v));
        }

        [Fact]
        public void Override_EmptyMappings_ByteIdenticalToDefault()
        {
            var with = new ItmTelemetryMapper();
            var without = new ItmTelemetryMapper();
            with.Configure(new Dictionary<ushort, FieldMapping>(), new FakeReader());

            var s = NewStatus();
            Set(s, "Fuel", 12.34);
            Set(s, "CurrentLap", 4);
            Assert.True(with.TryEncodeParam(ItmParam.Fuel, 1, Wrap(s), out var a));
            Assert.True(without.TryEncodeParam(ItmParam.Fuel, 1, Wrap(s), out var b));
            Assert.Equal(a.Raw, b.Raw);
            Assert.Equal(a.Size, b.Size);
        }

        // ── Format / suffix ──────────────────────────────────────────────

        [Theory]
        [InlineData(true, null, FieldFormats.WithTotal)]
        [InlineData(false, null, FieldFormats.Bare)]
        [InlineData(false, FieldFormats.WithTotal, FieldFormats.WithTotal)] // explicit wins
        [InlineData(true, FieldFormats.Bare, FieldFormats.Bare)]
        public void ToggleMigration_Lap(bool showTotal, string? explicitFormat, string expected)
        {
            var mapper = new ItmTelemetryMapper { ShowLapTotal = showTotal };
            if (explicitFormat != null)
            {
                mapper.Configure(
                    new Dictionary<ushort, FieldMapping>
                    {
                        // Source present so the mapping is real; format is under test.
                        [ItmParam.Lap] = BuiltIn(BuiltInProperties.CurrentLap, explicitFormat),
                    },
                    new FakeReader());
            }
            Assert.Equal(expected, mapper.EffectiveFormat(ItmParam.Lap));
        }

        [Theory]
        [InlineData(true, null, FieldFormats.WithTotal)]
        [InlineData(false, null, FieldFormats.Bare)]
        [InlineData(false, FieldFormats.WithTotal, FieldFormats.WithTotal)]
        public void ToggleMigration_Position(bool showTotal, string? explicitFormat, string expected)
        {
            var mapper = new ItmTelemetryMapper { ShowPositionTotal = showTotal };
            if (explicitFormat != null)
            {
                mapper.Configure(
                    new Dictionary<ushort, FieldMapping>
                    {
                        [ItmParam.Position] = BuiltIn(BuiltInProperties.Position, explicitFormat),
                    },
                    new FakeReader());
            }
            Assert.Equal(expected, mapper.EffectiveFormat(ItmParam.Position));
        }

        [Fact]
        public void OverriddenSource_DefaultsToBare_UnlessFormatExplicit()
        {
            var mapper = new ItmTelemetryMapper();
            var reader = new FakeReader();
            reader.Numbers["X"] = 1;
            mapper.Configure(
                new Dictionary<ushort, FieldMapping>
                {
                    [ItmParam.Lap] = SimHub("X"),                          // no format
                    [ItmParam.Fuel] = SimHub("X", FieldFormats.WithTotal), // explicit
                    [ItmParam.OilTemp] = SimHub("X"),                      // temp → bare
                    [ItmParam.TyreFlTemp] = SimHub("X", FieldFormats.Unit),
                },
                reader);

            Assert.Equal(FieldFormats.Bare, mapper.EffectiveFormat(ItmParam.Lap));
            Assert.Equal(FieldFormats.WithTotal, mapper.EffectiveFormat(ItmParam.Fuel));
            Assert.Equal(FieldFormats.Bare, mapper.EffectiveFormat(ItmParam.OilTemp));
            Assert.Equal(FieldFormats.Unit, mapper.EffectiveFormat(ItmParam.TyreFlTemp));
        }

        [Fact]
        public void Suffix_Bare_ClearsTotal()
        {
            var mapper = new ItmTelemetryMapper();
            mapper.ShowLapTotal = true;
            mapper.Configure(
                new Dictionary<ushort, FieldMapping>
                {
                    [ItmParam.Lap] = BuiltIn(BuiltInProperties.CurrentLap, FieldFormats.Bare),
                },
                new FakeReader());

            var s = NewStatus();
            Set(s, "CurrentLap", 5);
            Set(s, "TotalLaps", 34);
            Assert.True(mapper.TryResolveTotalSuffix(ItmParam.Lap, Wrap(s), out var suffix));
            Assert.Equal(" ", suffix);
        }

        [Fact]
        public void Suffix_WithTotal_EmitsTotal()
        {
            var mapper = new ItmTelemetryMapper();
            var s = NewStatus();
            Set(s, "CurrentLap", 5);
            Set(s, "TotalLaps", 34);
            Assert.True(mapper.TryResolveTotalSuffix(ItmParam.Lap, Wrap(s), out var suffix));
            Assert.Equal("/34", suffix);
        }

        [Fact]
        public void Suffix_ToggleOff_ActsAsBare()
        {
            var mapper = new ItmTelemetryMapper { ShowPositionTotal = false };
            var s = NewStatus();
            Set(s, "Position", 3);
            Set(s, "OpponentsCount", 20);
            Assert.True(mapper.TryResolveTotalSuffix(ItmParam.Position, Wrap(s), out var suffix));
            Assert.Equal(" ", suffix);
        }

        [Fact]
        public void Suffix_TempBare_ClearsUnit()
        {
            var mapper = new ItmTelemetryMapper();
            mapper.Configure(
                new Dictionary<ushort, FieldMapping>
                {
                    [ItmParam.OilTemp] = BuiltIn(BuiltInProperties.OilTemperature, FieldFormats.Bare),
                },
                new FakeReader());

            var s = NewStatus();
            Set(s, "TemperatureUnit", "C");
            Assert.True(mapper.TryGetUnitSuffix(ItmParam.OilTemp, Wrap(s), out var suffix));
            Assert.Equal(" ", suffix);
        }

        [Fact]
        public void Suffix_TempDefault_EmitsUnit()
        {
            var mapper = new ItmTelemetryMapper();
            var s = NewStatus();
            Set(s, "TemperatureUnit", "F");
            Assert.True(mapper.TryGetUnitSuffix(ItmParam.TyreFlTemp, Wrap(s), out var suffix));
            Assert.Equal("F", suffix);
        }

        [Fact]
        public void Suffix_OverriddenTotal_DefaultsBare()
        {
            var mapper = new ItmTelemetryMapper();
            mapper.Configure(
                new Dictionary<ushort, FieldMapping>
                {
                    [ItmParam.Fuel] = SimHub("Custom.Fuel"), // no format → bare
                },
                new FakeReader { Numbers = { ["Custom.Fuel"] = 12.0 } });

            var s = NewStatus();
            Set(s, "Fuel", 12.0);
            Set(s, "MaxFuel", 90.0);
            Assert.True(mapper.TryResolveTotalSuffix(ItmParam.Fuel, Wrap(s), out var suffix));
            Assert.Equal(" ", suffix);
        }
    }
}
