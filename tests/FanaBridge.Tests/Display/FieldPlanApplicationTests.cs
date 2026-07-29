using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Composition;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Host;
using FanaBridge.Display.Legacy;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Protocol;
using GameReaderCommon;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Pre-E8 G2: FieldRegionPlan → <see cref="ItmTelemetryMapper.ConfigureFromPlans"/>
    /// byte pins. Plans are produced by the real <see cref="FrameComposer"/> from small
    /// v2 configs; assertions hit mapper value/suffix outputs (ItmScreenGoldenTests-style
    /// exact bytes/strings), not the runtime wiring (that is E8).
    /// </summary>
    public class FieldPlanApplicationTests
    {
        // ── GameData / reader harness ────────────────────────────────────

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

        // ── Composer helpers (mirror FrameComposerLawTests shape) ────────

        private static CarrierTickSnapshot Snap(string id, bool active)
            => new CarrierTickSnapshot(
                id, conditionSatisfied: active, active, freshFire: false,
                firedThisTick: false, eligible: true,
                expiresAtMs: 0, remainingMs: null);

        private static DisplayConfigV2 Normalize(DisplayConfigV2 doc)
            => DisplayConfigV2Validator.Normalize(doc, _ => { });

        private static FrameComposer Composer(
            DisplayConfigV2 doc,
            IReadOnlyDictionary<ushort, FieldCapability> caps)
        {
            var options = new FrameComposerOptions
            {
                Capabilities = caps,
                DeviceKey = "test",
                PrimaryHostByParam = FieldCapability.PrimaryHostMapFromCapabilities(caps),
            };
            return new FrameComposer(Normalize(doc), options);
        }

        private static FieldCapability Cap(
            ushort paramId,
            bool? suffixSupported = true,
            int? suffixWidth = 5,
            bool? ascii = false,
            string primaryHost = "tyreTemps")
            => new FieldCapability
            {
                ParamId = paramId,
                SuffixSupported = suffixSupported,
                SuffixWidth = suffixWidth,
                ValueNumeric = true,
                ValueAscii = ascii,
                Overridable = true,
                PrimaryHostCatalogPageId = primaryHost,
                HostCatalogPageIds = new List<string> { primaryHost },
            };

        private static ContentObject Text(string t)
            => new ContentObject { Kind = ContentKind.Text, Text = t };

        private static ContentObject Prop(string name, string? format = null)
            => new ContentObject
            {
                Kind = ContentKind.Property,
                Source = new ValueSource
                {
                    Kind = ValueSourceKind.SimHubProperty,
                    Name = name,
                },
                Format = format,
            };

        private static FieldOverride Ov(
            string id, FieldWrites writes, ContentObject content,
            ContentEffect effect = ContentEffect.None,
            FieldAlignment align = FieldAlignment.Left)
            => new FieldOverride
            {
                Id = id,
                Writes = writes,
                Content = content,
                Effect = effect,
                Alignment = align,
                Condition = new Condition
                {
                    Source = new ValueSource
                    {
                        Kind = ValueSourceKind.BuiltIn,
                        Name = BuiltInProperties.PitLimiterOn,
                    },
                    Operator = ConditionOperator.IsTrue,
                },
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
            };

        private static DisplayConfigV2 FieldDoc(
            ushort paramId,
            FieldBase bas,
            params FieldOverride[] overrides)
            => new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-x", Name = "X",
                        Base = new ContentWithEffect { Content = Text("XXX") },
                    },
                },
                Fields = new Dictionary<ushort, FieldEntry>
                {
                    [paramId] = new FieldEntry
                    {
                        Base = bas ?? new FieldBase(),
                        Overrides = overrides?.ToList() ?? new List<FieldOverride>(),
                    },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-x",
                        },
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
            };

        private static FrameComposerTickInput In(
            long now, string host, params CarrierTickSnapshot[] snaps)
            => new FrameComposerTickInput
            {
                NowMs = now,
                SegmentHostedPageId = "p-x",
                DisplayedDestinationId = DestinationIds.Itm(host),
                Content = new SegmentContentContext { InGame = true },
                CarrierSnapshots = snaps,
            };

        private static FieldRegionPlan PlanAt(
            FrameComposer composer, long now, string host, params CarrierTickSnapshot[] snaps)
        {
            var r = composer.Tick(In(now, host, snaps));
            return Assert.Single(r.FieldPlans);
        }

        // ── Cases ────────────────────────────────────────────────────────

        [Fact]
        public void SuffixOnlyWrite_EmitsAlignedText_NotUnit()
        {
            // Tyre field: suffix "!" replaces the unit; value stays built-in.
            var doc = FieldDoc(ItmParam.TyreFlTemp,
                new FieldBase { Format = FieldFormats.Unit },
                Ov("o-bang", FieldWrites.Suffix, Text("!")));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [ItmParam.TyreFlTemp] = Cap(ItmParam.TyreFlTemp, suffixWidth: 1),
            };
            var plan = PlanAt(Composer(doc, caps), 0, "tyreTemps", Snap("o-bang", true));
            Assert.Equal(SuffixOwner.Override, plan.SuffixOwner);
            Assert.Equal(FieldFormats.Bare, plan.ValueFormat); // composer coercion
            Assert.Equal("!", plan.AlignedSuffixText);

            var mapper = new ItmTelemetryMapper();
            mapper.ConfigureFromPlans(new[] { plan }, new FakeReader());

            var s = NewStatus();
            Set(s, "TyreTemperatureFrontLeft", 94.0);
            Set(s, "TemperatureUnit", "C");
            Assert.True(mapper.TryEncodeParam(ItmParam.TyreFlTemp, 2, Wrap(s), out var v));
            Assert.Equal((byte)94, AsU8(v)); // built-in value (value-only not overridden)

            Assert.True(mapper.TryResolveSuffix(ItmParam.TyreFlTemp, Wrap(s), out var suffix));
            Assert.Equal("!", suffix);
            // Unit path must not re-fill with "C" either.
            Assert.True(mapper.TryGetUnitSuffix(ItmParam.TyreFlTemp, Wrap(s), out var unit));
            Assert.Equal("!", unit);
        }

        [Fact]
        public void ValueOnlyWrite_EncodesOverride_BaseSuffixComputed()
        {
            // Value-only: source override defaults format to bare (suffix stays mapper-
            // computed, not plan-owned). Explicit withTotal on the content keeps the total.
            var doc = FieldDoc(ItmParam.Fuel,
                new FieldBase { Format = FieldFormats.WithTotal },
                Ov("o-val", FieldWrites.Value,
                    Prop("Custom.Fuel", format: FieldFormats.WithTotal)));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [ItmParam.Fuel] = Cap(ItmParam.Fuel, primaryHost: "fuelErsDrs"),
            };
            var plan = PlanAt(Composer(doc, caps), 0, "fuelErsDrs", Snap("o-val", true));
            Assert.True(plan.ValueFromOverride);
            Assert.Equal(SuffixOwner.BaseComputed, plan.SuffixOwner);
            Assert.Equal(FieldFormats.WithTotal, plan.ValueFormat);

            var reader = new FakeReader { Numbers = { ["Custom.Fuel"] = 16.9692 } };
            var mapper = new ItmTelemetryMapper();
            mapper.ConfigureFromPlans(new[] { plan }, reader);

            var s = NewStatus();
            Set(s, "Fuel", 5.0);
            Set(s, "MaxFuel", 90.0);
            Assert.True(mapper.TryEncodeParam(ItmParam.Fuel, 2, Wrap(s), out var v));
            Assert.Equal(17.0f, AsF32(v), 3); // typed path still rounds 1dp

            // BaseComputed + explicit withTotal → tank total (not plan text).
            Assert.True(mapper.TryResolveTotalSuffix(ItmParam.Fuel, Wrap(s), out var suffix));
            Assert.Equal("/90", suffix);
        }

        [Fact]
        public void BothWrite_ValueAndSuffix_BareCoercionBlocksTotal()
        {
            // FN over withTotal: suffix "FN", format coerced bare → no "/90".
            var doc = FieldDoc(ItmParam.Fuel,
                new FieldBase { Format = FieldFormats.WithTotal },
                Ov("o-both", FieldWrites.Both, Prop("Custom.Fuel", format: FieldFormats.WithTotal)));
            // Content is property for value; for Both with property content the composer
            // treats suffix as property-sourced (SuffixText null). Use text content that
            // paints both — value property via a value-only style: use text for suffix
            // region by writing Both with text content (value unrenderable for dynamic
            // would degrade). Instead: suffix text via writes:both with text, and set
            // base source for value... Simpler fixture: writes both with text "FN" for
            // ascii-capable field, assert suffix; then a separate property value case.
            // Fuel is numeric — text into value degrades. Use value property + suffix text:
            // two overrides? Winner is one. Use property content + writes:both — suffix
            // is property-sourced (SuffixSource set). For pin: encode from property and
            // bare total.
            doc = FieldDoc(ItmParam.Fuel,
                new FieldBase { Format = FieldFormats.WithTotal },
                Ov("o-both", FieldWrites.Both, Prop("Custom.Fuel")));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [ItmParam.Fuel] = Cap(ItmParam.Fuel, primaryHost: "fuelErsDrs"),
            };
            var plan = PlanAt(Composer(doc, caps), 0, "fuelErsDrs", Snap("o-both", true));
            Assert.True(plan.ValueFromOverride);
            Assert.Equal(SuffixOwner.Override, plan.SuffixOwner);
            Assert.Equal(FieldFormats.Bare, plan.ValueFormat);

            var reader = new FakeReader { Numbers = { ["Custom.Fuel"] = 12.3 } };
            var mapper = new ItmTelemetryMapper();
            mapper.ConfigureFromPlans(new[] { plan }, reader);

            var s = NewStatus();
            Set(s, "Fuel", 5.0);
            Set(s, "MaxFuel", 90.0);
            Assert.True(mapper.TryEncodeParam(ItmParam.Fuel, 2, Wrap(s), out var v));
            Assert.Equal(12.3f, AsF32(v), 3);

            // Property-sourced suffix: resolve via the property reader, then the same
            // left-align + catalog-width pad static suffixes use (Cap default width 5).
            Assert.True(mapper.TryResolveTotalSuffix(ItmParam.Fuel, Wrap(s), out var suffix));
            Assert.Equal("12.3 ", suffix);
            Assert.Equal("12.3 ", plan.AlignedSuffixText);
        }

        [Fact]
        public void PropertySuffix_RightAlign_And_OverflowClamp_MatchStaticPath()
        {
            // Multi-char property value, right-aligned into width 5 → observable pad.
            var rightDoc = FieldDoc(ItmParam.TyreFlTemp,
                new FieldBase(),
                Ov("o-right", FieldWrites.Suffix, Prop("Custom.Tag"),
                    align: FieldAlignment.Right));
            var rightCaps = new Dictionary<ushort, FieldCapability>
            {
                [ItmParam.TyreFlTemp] = Cap(ItmParam.TyreFlTemp, suffixWidth: 5),
            };
            var rightPlan = PlanAt(Composer(rightDoc, rightCaps), 0, "tyreTemps",
                Snap("o-right", true));
            Assert.Equal(FieldAlignment.Right, rightPlan.Alignment);
            Assert.Equal(5, rightPlan.SuffixWidth);

            var reader = new FakeReader { Numbers = { ["Custom.Tag"] = 42 } };
            var mapper = new ItmTelemetryMapper();
            mapper.ConfigureFromPlans(new[] { rightPlan }, reader);
            Assert.Equal("   42", rightPlan.AlignedSuffixText);

            var s = NewStatus();
            Set(s, "TemperatureUnit", "C");
            Assert.True(mapper.TryResolveSuffix(ItmParam.TyreFlTemp, Wrap(s), out var rightSuf));
            Assert.Equal("   42", rightSuf);

            // Overflow: rendered "123456" clamped to catalog width 5 before align.
            var overDoc = FieldDoc(ItmParam.TyreFlTemp,
                new FieldBase(),
                Ov("o-over", FieldWrites.Suffix, Prop("Custom.Big"),
                    align: FieldAlignment.Left));
            var overCaps = new Dictionary<ushort, FieldCapability>
            {
                [ItmParam.TyreFlTemp] = Cap(ItmParam.TyreFlTemp, suffixWidth: 5),
            };
            var overPlan = PlanAt(Composer(overDoc, overCaps), 0, "tyreTemps",
                Snap("o-over", true));
            reader.Numbers["Custom.Big"] = 123456;
            mapper.ConfigureFromPlans(new[] { overPlan }, reader);
            Assert.Equal("12345", overPlan.AlignedSuffixText);
            Assert.Equal("12345", overPlan.SuffixText);
            Assert.True(mapper.TryResolveSuffix(ItmParam.TyreFlTemp, Wrap(s), out var overSuf));
            Assert.Equal("12345", overSuf);
        }

        [Fact]
        public void SuffixBlink_EffectVisibleFalse_WidthBlank_ThenOn()
        {
            var doc = FieldDoc(ItmParam.TyreFlTemp,
                new FieldBase(),
                Ov("o-bang", FieldWrites.Suffix, Text("!"), ContentEffect.Blink));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [ItmParam.TyreFlTemp] = Cap(ItmParam.TyreFlTemp, suffixWidth: 1),
            };
            var c = Composer(doc, caps);

            var onPlan = PlanAt(c, 0, "tyreTemps", Snap("o-bang", true));
            Assert.True(onPlan.EffectVisible);
            Assert.Equal("!", onPlan.AlignedSuffixText);

            var offPlan = PlanAt(c, LegacyEffectClock.BlinkHalfPeriodMs, "tyreTemps",
                Snap("o-bang", true));
            Assert.False(offPlan.EffectVisible);
            Assert.Equal(" ", offPlan.AlignedSuffixText);

            var mapper = new ItmTelemetryMapper();
            var s = NewStatus();
            Set(s, "TemperatureUnit", "C");

            mapper.ConfigureFromPlans(new[] { onPlan }, null);
            Assert.True(mapper.TryResolveSuffix(ItmParam.TyreFlTemp, Wrap(s), out var onSuf));
            Assert.Equal("!", onSuf);

            mapper.ConfigureFromPlans(new[] { offPlan }, null);
            Assert.True(mapper.TryResolveSuffix(ItmParam.TyreFlTemp, Wrap(s), out var offSuf));
            Assert.Equal(" ", offSuf);
        }

        [Fact]
        public void PropertySuffix_RendersViaReader_BlinkBlanks_ValueOnlyAndStaticWireSafe()
        {
            // Property-sourced suffix + blink: on → rendered text; off → width blank.
            var prop = Prop("Custom.Tag");
            var doc = FieldDoc(ItmParam.TyreFlTemp,
                new FieldBase(),
                Ov("o-prop", FieldWrites.Suffix, prop, ContentEffect.Blink));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [ItmParam.TyreFlTemp] = Cap(ItmParam.TyreFlTemp, suffixWidth: 1),
            };
            var c = Composer(doc, caps);
            var reader = new FakeReader { Numbers = { ["Custom.Tag"] = 7 } };

            var onPlan = PlanAt(c, 0, "tyreTemps", Snap("o-prop", true));
            Assert.True(onPlan.EffectVisible);
            Assert.NotNull(onPlan.SuffixSource);
            Assert.Equal("Custom.Tag", onPlan.SuffixSource.Name);

            var mapper = new ItmTelemetryMapper();
            mapper.ConfigureFromPlans(new[] { onPlan }, reader);
            Assert.Equal("7", onPlan.AlignedSuffixText);
            var s = NewStatus();
            Set(s, "TemperatureUnit", "C");
            Assert.True(mapper.TryResolveSuffix(ItmParam.TyreFlTemp, Wrap(s), out var onSuf));
            Assert.Equal("7", onSuf);

            var offPlan = PlanAt(c, LegacyEffectClock.BlinkHalfPeriodMs, "tyreTemps",
                Snap("o-prop", true));
            Assert.False(offPlan.EffectVisible);
            mapper.ConfigureFromPlans(new[] { offPlan }, reader);
            Assert.Equal(" ", offPlan.AlignedSuffixText);
            Assert.True(mapper.TryResolveSuffix(ItmParam.TyreFlTemp, Wrap(s), out var offSuf));
            Assert.Equal(" ", offSuf);

            // Wire-safety: value-only and static-suffix plans stay byte-identical.
            var valueOnly = FieldDoc(ItmParam.Fuel,
                new FieldBase { Format = FieldFormats.WithTotal },
                Ov("o-val", FieldWrites.Value, Prop("Custom.Fuel", format: FieldFormats.WithTotal)));
            var staticSuf = FieldDoc(ItmParam.TyreFlTemp,
                new FieldBase(),
                Ov("o-bang", FieldWrites.Suffix, Text("!")));
            var fuelCaps = new Dictionary<ushort, FieldCapability>
            {
                [ItmParam.Fuel] = Cap(ItmParam.Fuel, primaryHost: "fuelErsDrs"),
            };
            var tyreCaps = new Dictionary<ushort, FieldCapability>
            {
                [ItmParam.TyreFlTemp] = Cap(ItmParam.TyreFlTemp, suffixWidth: 1),
            };
            var valPlan = PlanAt(Composer(valueOnly, fuelCaps), 0, "fuelErsDrs", Snap("o-val", true));
            var statPlan = PlanAt(Composer(staticSuf, tyreCaps), 0, "tyreTemps", Snap("o-bang", true));
            var valMapper = new ItmTelemetryMapper();
            valMapper.ConfigureFromPlans(new[] { valPlan },
                new FakeReader { Numbers = { ["Custom.Fuel"] = 16.9692 } });
            var statMapper = new ItmTelemetryMapper();
            statMapper.ConfigureFromPlans(new[] { statPlan }, null);

            Set(s, "Fuel", 5.0);
            Set(s, "MaxFuel", 90.0);
            Assert.True(valMapper.TryEncodeParam(ItmParam.Fuel, 2, Wrap(s), out var v));
            Assert.Equal(17.0f, AsF32(v), 3);
            Assert.True(valMapper.TryResolveTotalSuffix(ItmParam.Fuel, Wrap(s), out var baseSuf));
            Assert.Equal("/90", baseSuf);

            Assert.True(statMapper.TryResolveSuffix(ItmParam.TyreFlTemp, Wrap(s), out var stSuf));
            Assert.Equal("!", stSuf);
        }

        [Fact]
        public void OneDecimal_RoundsScalarBeforeEncode()
        {
            // DeltaOwnBest default registry rounds 2dp; plan format oneDecimal → 1dp.
            var doc = FieldDoc(ItmParam.DeltaOwnBest,
                new FieldBase(),
                Ov("o-d", FieldWrites.Value,
                    Prop("Custom.Delta", format: ItmTelemetryMapper.FormatOneDecimal)));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [ItmParam.DeltaOwnBest] = Cap(ItmParam.DeltaOwnBest, suffixSupported: false,
                    primaryHost: "fuelErsDrs"),
            };
            var plan = PlanAt(Composer(doc, caps), 0, "fuelErsDrs", Snap("o-d", true));
            Assert.Equal(ItmTelemetryMapper.FormatOneDecimal, plan.ValueFormat);

            var reader = new FakeReader { Numbers = { ["Custom.Delta"] = 1.26 } };
            var mapper = new ItmTelemetryMapper();
            mapper.ConfigureFromPlans(new[] { plan }, reader);

            Assert.True(mapper.TryEncodeParam(ItmParam.DeltaOwnBest, 4, Wrap(NewStatus()), out var v));
            // ApplyValueFormat → 1.3; scalar encoder then Math.Round(..., 2) → 1.3f.
            Assert.Equal(1.3f, AsF32(v), 3);
        }

        [Fact]
        public void BareFormatCoercion_SuffixOverride_ClearsTempUnit()
        {
            // Even if plan.ValueFormat were left as unit (defensive mapper path), Override
            // must coerce bare. Composer already sets bare; re-assert via mapper after apply.
            var doc = FieldDoc(ItmParam.OilTemp,
                new FieldBase { Format = FieldFormats.Unit },
                Ov("o-x", FieldWrites.Suffix, Text("X")));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [ItmParam.OilTemp] = Cap(ItmParam.OilTemp, suffixWidth: 1,
                    primaryHost: "carSettings"),
            };
            var plan = PlanAt(Composer(doc, caps), 0, "carSettings", Snap("o-x", true));
            Assert.Equal(FieldFormats.Bare, plan.ValueFormat);

            // Defensive: if a caller forgot coercion on the plan object, mapper still bares.
            plan.ValueFormat = FieldFormats.Unit;
            var mapper = new ItmTelemetryMapper();
            mapper.ConfigureFromPlans(new[] { plan }, null);
            Assert.Equal(FieldFormats.Bare, mapper.EffectiveFormat(ItmParam.OilTemp));

            var s = NewStatus();
            Set(s, "TemperatureUnit", "F");
            Assert.True(mapper.TryGetUnitSuffix(ItmParam.OilTemp, Wrap(s), out var suffix));
            Assert.Equal("X", suffix); // plan text, not "F"
        }

        [Fact]
        public void CapabilityDegraded_NoSuffixRegion_ChildInert_BasePath()
        {
            // Suffix write on a field with suffix.supported=false → no winner; base format.
            var doc = FieldDoc(ItmParam.TcSetting,
                new FieldBase(),
                Ov("o-s", FieldWrites.Suffix, Text("!")));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [ItmParam.TcSetting] = Cap(ItmParam.TcSetting, suffixSupported: false,
                    suffixWidth: null, primaryHost: "carSettings"),
            };
            var plan = PlanAt(Composer(doc, caps), 0, "carSettings", Snap("o-s", true));
            Assert.Null(plan.WinnerCarrierId);
            Assert.Equal(SuffixOwner.BaseComputed, plan.SuffixOwner);
            var d = Assert.Single(plan.DegradedChildren);
            Assert.Equal(FieldDegradeReason.SuffixNotSupported, d.Reason);

            var mapper = new ItmTelemetryMapper();
            mapper.ConfigureFromPlans(new[] { plan }, null);

            var s = NewStatus();
            Set(s, "TCLevel", 3);
            Assert.True(mapper.TryEncodeParam(ItmParam.TcSetting, 1, Wrap(s), out var v));
            Assert.Equal((byte)3, AsU8(v));

            // No plan-owned suffix; TC is not a total/temp → no ParamDefs suffix.
            Assert.False(mapper.TryResolveSuffix(ItmParam.TcSetting, Wrap(s), out _));
        }
    }
}
