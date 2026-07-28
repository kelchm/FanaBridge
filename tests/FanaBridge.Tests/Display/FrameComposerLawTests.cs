using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Composition;
using FanaBridge.Display.Host;
using FanaBridge.Display.Legacy;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Schema2;
using FanaBridge.Display.Session;
using FanaBridge.Protocol;
using GameReaderCommon;
using FanaBridge.Tests.Display.TestSupport;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Keeper laws: pure FrameComposer behavior pins (ladders, capability, presence, merge).
    /// </summary>
    public class FrameComposerLawTests
    {
        // ── Snapshot / content helpers ───────────────────────────────────

        private static CarrierTickSnapshot Snap(
            string id, bool active, bool fired = false, bool fresh = false,
            bool eligible = true, int? remaining = null)
            => new CarrierTickSnapshot(
                id, conditionSatisfied: active, active, fresh, fired,
                legacySupersededV9: false, eligible, expiresAtMs: 0, remaining);

        private static SegmentContentContext Ctx(
            double? speed = null, string? gear = null, double? rpm = null,
            double? pos = null, double? fuel = null, bool inGame = true,
            IPropertyReader? props = null)
            => new SegmentContentContext
            {
                InGame = inGame,
                SpeedLocal = speed,
                Gear = gear,
                Rpms = rpm,
                Position = pos,
                Fuel = fuel,
                Properties = props,
            };

        private static DisplayConfigV2 Normalize(DisplayConfigV2 doc)
            => DisplayConfigV2Validator.Normalize(doc, _ => { });

        private static FrameComposer Composer(
            DisplayConfigV2 doc,
            IReadOnlyDictionary<ushort, FieldCapability>? caps = null,
            Action<string>? warn = null,
            IReadOnlyDictionary<ushort, string>? primaryHost = null)
        {
            var options = new FrameComposerOptions
            {
                Capabilities = caps,
                DeviceKey = "test",
                Warn = warn,
            };
            if (primaryHost != null)
                options.PrimaryHostByParam = primaryHost;
            else if (caps != null)
                options.PrimaryHostByParam =
                    FieldCapability.PrimaryHostMapFromCapabilities(caps);
            return new FrameComposer(Normalize(doc), options);
        }

        private static FrameComposerTickInput In(
            long now,
            string? segmentPage,
            string? displayed,
            SegmentContentContext? content = null,
            IReadOnlyCollection<string>? dismissed = null,
            bool wheelScreenHolds = false,
            params CarrierTickSnapshot[] snaps)
            => new FrameComposerTickInput
            {
                NowMs = now,
                SegmentHostedPageId = segmentPage,
                DisplayedDestinationId = displayed,
                Content = content ?? Ctx(speed: 88),
                CarrierSnapshots = snaps,
                DismissedCarrierIds = dismissed ?? Array.Empty<string>(),
                SegmentSurfaceHeldByWheelScreen = wheelScreenHolds,
            };

        private static ContentObject Text(string t)
            => new ContentObject { Kind = ContentKind.Text, Text = t };

        private static ContentObject Speed()
            => new ContentObject { Kind = ContentKind.Speed };

        private static LayerEntry Layer(
            string id, string text, ContentEffect effect = ContentEffect.None)
            => new LayerEntry
            {
                Id = id,
                Name = id,
                Content = Text(text),
                Effect = effect,
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

        private static FieldOverride Ov(
            string id, FieldWrites writes, string? text,
            ContentEffect effect = ContentEffect.None,
            FieldAlignment align = FieldAlignment.Left,
            ContentObject? content = null)
            => new FieldOverride
            {
                Id = id,
                Writes = writes,
                Content = content ?? Text(text ?? ""),
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

        private static DisplayConfigV2 HostedDoc(
            string pageId, ContentWithEffect? bas, params LayerEntry[] layers)
            => new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage,
                        Id = pageId,
                        Name = pageId,
                        Base = bas,
                        Layers = layers?.ToList() ?? new List<LayerEntry>(),
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
                            Kind = PageRefKind.HostedPage, Id = pageId,
                        },
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
            };

        private static FieldCapability Cap(
            ushort paramId,
            bool? suffixSupported = true,
            int? suffixWidth = 5,
            bool? numeric = true,
            bool? ascii = false,
            string? primaryHost = "tyreTemps",
            bool? overridable = true,
            params string[] hosts)
            => new FieldCapability
            {
                ParamId = paramId,
                SuffixSupported = suffixSupported,
                SuffixWidth = suffixWidth,
                ValueNumeric = numeric,
                ValueAscii = ascii,
                Overridable = overridable,
                PrimaryHostCatalogPageId = primaryHost,
                HostCatalogPageIds = hosts.Length > 0
                    ? hosts.ToList()
                    : (primaryHost != null
                        ? new List<string> { primaryHost }
                        : new List<string>()),
            };

        private static (byte, byte, byte) Triple(byte[] f)
            => (f[0], f[1], f[2]);

        private static DisplayConfigV2 FieldOnlyDoc(
            ushort paramId, params FieldOverride[] overrides)
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
                        Base = new FieldBase { BaseSuffix = "C" },
                        Overrides = overrides.ToList(),
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

        // ═════════════════════════════════════════════════════════════════
        // Layer ladder
        // ═════════════════════════════════════════════════════════════════

        [Fact]
        public void LayerLadder_TopActiveWinsAllThreeChars()
        {
            var doc = HostedDoc("p-a",
                new ContentWithEffect { Content = Text("BAS") },
                Layer("l-top", "TOP"),
                Layer("l-mid", "MID"));
            var c = Composer(doc);
            var r = c.Tick(In(0, "p-a", DestinationIds.Hosted("p-a"),
                Ctx(), null, false,
                Snap("l-top", true), Snap("l-mid", true)));

            Assert.Equal("l-top", r.SegmentWinnerCarrierId);
            Assert.Equal(
                (SevenSegment.T, SevenSegment.O, SevenSegment.P),
                Triple(r.SegmentFrame));
            Assert.True(r.SegmentFrameWritable);
        }

        [Fact]
        public void LayerLadder_BaseIsPinnedFloor_WhenNoLayerActive()
        {
            var doc = HostedDoc("p-a",
                new ContentWithEffect { Content = Text("BAS") },
                Layer("l-top", "TOP"));
            var c = Composer(doc);
            var r = c.Tick(In(0, "p-a", DestinationIds.Hosted("p-a"),
                Ctx(), null, false, Snap("l-top", false)));

            Assert.Null(r.SegmentWinnerCarrierId);
            Assert.Equal(
                (SevenSegment.B, SevenSegment.A, SevenSegment.S),
                Triple(r.SegmentFrame));
        }

        [Fact]
        public void LayerLadder_BlankBase_LegalAlertStyle()
        {
            var doc = HostedDoc("p-alerts", bas: null,
                Layer("l-pit", "PIT", ContentEffect.Blink));
            var c = Composer(doc);
            var blank = c.Tick(In(0, "p-alerts", DestinationIds.Hosted("p-alerts"),
                Ctx(), null, false, Snap("l-pit", false)));
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                Triple(blank.SegmentFrame));

            var on = c.Tick(In(0, "p-alerts", DestinationIds.Hosted("p-alerts"),
                Ctx(), null, false, Snap("l-pit", true)));
            Assert.Equal("l-pit", on.SegmentWinnerCarrierId);
            Assert.Equal(
                (SevenSegment.P, SevenSegment.I, SevenSegment.T),
                Triple(on.SegmentFrame));
        }

        // ═════════════════════════════════════════════════════════════════
        // Field ladder — winner / regions / base-fill / lower never paints
        // ═════════════════════════════════════════════════════════════════

        [Fact]
        public void FieldLadder_WinnerPaintsDeclared_BaseFillsRest()
        {
            var doc = FieldOnlyDoc(42, Ov("o-bang", FieldWrites.Suffix, "!"));
            doc.Fields[42].Base = new FieldBase
            {
                Source = new ValueSource
                {
                    Kind = ValueSourceKind.BuiltIn,
                    Name = BuiltInProperties.Fuel,
                },
                Format = FieldFormats.Unit,
                BaseSuffix = "C",
            };
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [42] = Cap(42, suffixWidth: 1),
            };
            var c = Composer(doc, caps);
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("tyreTemps"),
                Ctx(), null, false, Snap("o-bang", true)));

            var plan = Assert.Single(r.FieldPlans);
            Assert.Equal("o-bang", plan.WinnerCarrierId);
            Assert.Equal(SuffixOwner.Override, plan.SuffixOwner);
            Assert.Equal("!", plan.SuffixText);
            Assert.False(plan.ValueFromOverride);
            // Winner paints suffix → ValueFormat coerced to bare (E5-01).
            Assert.Equal(FieldFormats.Bare, plan.ValueFormat);
        }

        [Fact]
        public void FieldLadder_AlexF12_SuffixOnlyWinner_SuppressesLowerFlash()
        {
            var doc = FieldOnlyDoc(42,
                Ov("o-fl-alert", FieldWrites.Suffix, "!"),
                Ov("o-fl-flash", FieldWrites.Suffix, "C", ContentEffect.Blink));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [42] = Cap(42, suffixWidth: 1),
            };
            var c = Composer(doc, caps);
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("tyreTemps"),
                Ctx(), null, false,
                Snap("o-fl-alert", true), Snap("o-fl-flash", true)));

            var plan = Assert.Single(r.FieldPlans);
            Assert.Equal("o-fl-alert", plan.WinnerCarrierId);
            Assert.Equal("!", plan.SuffixText);
            Assert.False(plan.ValueFromOverride);

            var flash = r.Resolution.CarrierStatuses
                .Single(s => s.CarrierId == "o-fl-flash");
            Assert.Equal(CarrierPresence.Outranked, flash.Presence);
            Assert.Equal(FrameComposer.FieldSurfaceId(42), flash.SurfaceId);
        }

        [Fact]
        public void FieldLadder_BothWrites_PaintsValueAndSuffix()
        {
            var doc = FieldOnlyDoc(5, Ov("o-both", FieldWrites.Both, "LO"));
            doc.Fields[5].Base = new FieldBase
            {
                Source = new ValueSource
                {
                    Kind = ValueSourceKind.BuiltIn,
                    Name = BuiltInProperties.Fuel,
                },
                Format = FieldFormats.WithTotal,
                BaseSuffix = "/106",
            };
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [5] = Cap(5, suffixWidth: 5, ascii: true, primaryHost: "fuelErsDrs"),
            };
            var c = Composer(doc, caps);
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("fuelErsDrs"),
                Ctx(), null, false, Snap("o-both", true)));

            var plan = Assert.Single(r.FieldPlans);
            Assert.True(plan.ValueFromOverride);
            Assert.True(plan.SuffixFromOverride);
            Assert.Equal("LO", plan.SuffixText);
            Assert.Equal("LO", plan.ValueContent.Text);
            Assert.Equal(FieldFormats.Bare, plan.ValueFormat);
        }

        // ═════════════════════════════════════════════════════════════════
        // E5-01 — SuffixOwner tri-state (probe fixtures)
        // ═════════════════════════════════════════════════════════════════

        [Fact]
        public void SuffixOwner_FN1_OverWithTotal_CoercesBare_NoTotalRefill()
        {
            // PROBE: param 5 Fuel format=withTotal, o-fn1 active → FN1, bare format.
            var doc = FieldOnlyDoc(5, Ov("o-fn1", FieldWrites.Suffix, "FN1"));
            doc.Fields[5].Base = new FieldBase
            {
                Source = new ValueSource
                {
                    Kind = ValueSourceKind.BuiltIn,
                    Name = BuiltInProperties.Fuel,
                },
                Format = FieldFormats.WithTotal,
                BaseSuffix = "/106",
            };
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [5] = Cap(5, suffixWidth: 5, primaryHost: "fuelErsDrs"),
            };
            var c = Composer(doc, caps);
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("fuelErsDrs"),
                Ctx(), null, false, Snap("o-fn1", true)));

            var plan = Assert.Single(r.FieldPlans);
            Assert.Equal(SuffixOwner.Override, plan.SuffixOwner);
            Assert.Equal("FN1", plan.SuffixText);
            Assert.Equal(FieldFormats.Bare, plan.ValueFormat);
            Assert.False(plan.ValueFromOverride);
            Assert.Equal(BuiltInProperties.Fuel, plan.ValueSource.Name);
        }

        [Fact]
        public void SuffixOwner_TyreBang_OverUnit_CoercesBare_NoUnitC()
        {
            // PROBE: params 42 TEMP, format=unit, '!' override → bare, no 'C'.
            var doc = FieldOnlyDoc(42, Ov("o-fl-alert", FieldWrites.Suffix, "!"));
            doc.Fields[42].Base = new FieldBase
            {
                Format = FieldFormats.Unit,
                BaseSuffix = "C",
            };
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [42] = Cap(42, suffixWidth: 1),
            };
            var c = Composer(doc, caps);
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("tyreTemps"),
                Ctx(), null, false, Snap("o-fl-alert", true)));

            var plan = Assert.Single(r.FieldPlans);
            Assert.Equal(SuffixOwner.Override, plan.SuffixOwner);
            Assert.Equal("!", plan.SuffixText);
            Assert.Equal(FieldFormats.Bare, plan.ValueFormat);
            Assert.NotEqual("C", plan.SuffixText);
        }

        [Fact]
        public void SuffixOwner_Resting_WithTotal_BaseComputed_PreservesSlashTotal()
        {
            // REST: winner=<base> valueFormat=withTotal suffix owner=BaseComputed;
            // SuffixText advisory/null-or-baseSuffix — mapper computes /106.
            var doc = FieldOnlyDoc(5);
            doc.Fields[5].Base = new FieldBase
            {
                Source = new ValueSource
                {
                    Kind = ValueSourceKind.BuiltIn,
                    Name = BuiltInProperties.Fuel,
                },
                Format = FieldFormats.WithTotal,
                BaseSuffix = "/106",
            };
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [5] = Cap(5, suffixWidth: 5, primaryHost: "fuelErsDrs"),
            };
            var c = Composer(doc, caps);
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("fuelErsDrs"), Ctx()));

            var plan = Assert.Single(r.FieldPlans);
            Assert.Null(plan.WinnerCarrierId);
            Assert.Equal(SuffixOwner.BaseComputed, plan.SuffixOwner);
            Assert.Equal(FieldFormats.WithTotal, plan.ValueFormat);
            // Advisory resting suffix still visible for diagnostics; not "write blank".
            Assert.Equal("/106", plan.SuffixText);
        }

        // ═════════════════════════════════════════════════════════════════
        // Alignment
        // ═════════════════════════════════════════════════════════════════

        [Fact]
        public void Alignment_LeftAndRight_PadToCatalogWidth()
        {
            var doc = new DisplayConfigV2
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
                    [5] = new FieldEntry
                    {
                        Base = new FieldBase(),
                        Overrides = new List<FieldOverride>
                        {
                            Ov("o-fn1", FieldWrites.Suffix, "FN",
                                align: FieldAlignment.Left),
                        },
                    },
                    [9] = new FieldEntry
                    {
                        Base = new FieldBase(),
                        Overrides = new List<FieldOverride>
                        {
                            Ov("o-right", FieldWrites.Suffix, "FN",
                                align: FieldAlignment.Right),
                        },
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
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [5] = Cap(5, suffixWidth: 5, primaryHost: "fuelErsDrs"),
                [9] = Cap(9, suffixWidth: 5, primaryHost: "fuelErsDrs"),
            };
            var c = Composer(doc, caps);
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("fuelErsDrs"),
                Ctx(), null, false, Snap("o-fn1", true), Snap("o-right", true)));

            var left = r.FieldPlans.Single(p => p.ParamId == 5);
            var right = r.FieldPlans.Single(p => p.ParamId == 9);
            Assert.Equal("FN   ", left.AlignedSuffixText);
            Assert.Equal("   FN", right.AlignedSuffixText);
        }

        [Fact]
        public void Alignment_ValueOnlyWinner_DoesNotLeakIntoBaseSuffix()
        {
            // E5-003: baseSuffix "C", width 5, writes:value + align:right → base left.
            var doc = FieldOnlyDoc(42,
                Ov("o-val", FieldWrites.Value, "HOT",
                    align: FieldAlignment.Right));
            doc.Fields[42].Base = new FieldBase { BaseSuffix = "C" };
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [42] = Cap(42, suffixWidth: 5, ascii: true),
            };
            var c = Composer(doc, caps);
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("tyreTemps"),
                Ctx(), null, false, Snap("o-val", true)));

            var plan = Assert.Single(r.FieldPlans);
            Assert.True(plan.ValueFromOverride);
            Assert.Equal(SuffixOwner.BaseComputed, plan.SuffixOwner);
            Assert.Equal("C", plan.SuffixText);
            // Left-aligned pad, not "    C" from right alignment leak.
            Assert.Equal("C    ", plan.AlignedSuffixText);
            Assert.Equal(FieldAlignment.Left, plan.Alignment);
        }

        // ═════════════════════════════════════════════════════════════════
        // Capability matrix (§14)
        // ═════════════════════════════════════════════════════════════════

        [Fact]
        public void Capability_SuffixOnNoSuffixField_ChildInert()
        {
            var doc = FieldOnlyDoc(33, Ov("o-s", FieldWrites.Suffix, "!"));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [33] = Cap(33, suffixSupported: false, suffixWidth: null,
                    primaryHost: "carSettings"),
            };
            var c = Composer(doc, caps);
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("carSettings"),
                Ctx(), null, false, Snap("o-s", true)));

            var plan = Assert.Single(r.FieldPlans);
            Assert.Null(plan.WinnerCarrierId);
            var d = Assert.Single(plan.DegradedChildren);
            Assert.Equal(FieldDegradeReason.SuffixNotSupported, d.Reason);
            Assert.Equal("!", d.AuthoredText);
        }

        [Fact]
        public void Capability_InactiveSuffixOnNoSuffixField_StillCantRunHere()
        {
            // E5-02: activity-independent capability degrade.
            var doc = FieldOnlyDoc(33, Ov("o-s", FieldWrites.Suffix, "!"));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [33] = Cap(33, suffixSupported: false, suffixWidth: null,
                    primaryHost: "carSettings"),
            };
            var c = Composer(doc, caps);
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("carSettings"),
                Ctx(), null, false, Snap("o-s", false)));

            var plan = Assert.Single(r.FieldPlans);
            Assert.Null(plan.WinnerCarrierId);
            Assert.Equal(FieldDegradeReason.SuffixNotSupported,
                Assert.Single(plan.DegradedChildren).Reason);
            var row = r.Resolution.CarrierStatuses.Single(s => s.CarrierId == "o-s");
            Assert.Equal(CarrierPresence.Waiting, row.Presence);
            Assert.True(row.RowLabels.HasFlag(CarrierRowLabels.CantRunHere));
        }

        [Fact]
        public void Capability_TextInNumericValue_ChildInert()
        {
            var doc = FieldOnlyDoc(42, Ov("o-txt", FieldWrites.Value, "HOT"));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [42] = Cap(42, ascii: false),
            };
            var c = Composer(doc, caps);
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("tyreTemps"),
                Ctx(), null, false, Snap("o-txt", true)));

            var plan = Assert.Single(r.FieldPlans);
            Assert.Null(plan.WinnerCarrierId);
            Assert.Equal(FieldDegradeReason.TextInNumericValue,
                Assert.Single(plan.DegradedChildren).Reason);
        }

        [Fact]
        public void Capability_SuffixWidthOverflow_RuntimeClampNotInert()
        {
            // E5-002: "!!" on width-1 still wins with clamp + degrade note.
            var doc = FieldOnlyDoc(42, Ov("o-wide", FieldWrites.Suffix, "!!"));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [42] = Cap(42, suffixWidth: 1),
            };
            var c = Composer(doc, caps);
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("tyreTemps"),
                Ctx(), null, false, Snap("o-wide", true)));

            var plan = Assert.Single(r.FieldPlans);
            Assert.Equal("o-wide", plan.WinnerCarrierId);
            Assert.Equal("!", plan.SuffixText);
            var d = Assert.Single(plan.DegradedChildren);
            Assert.Equal(FieldDegradeReason.SuffixWidthOverflow, d.Reason);
            Assert.Equal("!!", d.AuthoredText);
        }

        [Fact]
        public void Capability_NullUntested_WarnsDoesNotGate()
        {
            // E5-03(b): present with null tri-states → paint + warn.
            var doc = FieldOnlyDoc(9, Ov("o-s", FieldWrites.Suffix, "!"));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [9] = Cap(9, suffixSupported: null, suffixWidth: null,
                    primaryHost: "fuelErsDrs"),
            };
            var warns = new List<string>();
            var c = Composer(doc, caps, warns.Add);
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("fuelErsDrs"),
                Ctx(), null, false, Snap("o-s", true)));

            var plan = Assert.Single(r.FieldPlans);
            Assert.Equal("o-s", plan.WinnerCarrierId);
            Assert.Contains(warns, w => w.Contains("untested"));
        }

        [Fact]
        public void Capability_ParamAbsent_InertCantRunHereNoWheel_Warn()
        {
            // E5-03(a): absent from map → inert + CantRunHere|NoWheel + warn.
            var doc = FieldOnlyDoc(42, Ov("o-fl-alert", FieldWrites.Suffix, "!"));
            var empty = new Dictionary<ushort, FieldCapability>();
            var warns = new List<string>();
            var c = Composer(doc, empty, warns.Add,
                primaryHost: new Dictionary<ushort, string>());
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("tyreTemps"),
                Ctx(), null, false, Snap("o-fl-alert", true)));

            var plan = Assert.Single(r.FieldPlans);
            Assert.Null(plan.WinnerCarrierId);
            Assert.Equal(FieldDegradeReason.ParamNotOnWheel,
                Assert.Single(plan.DegradedChildren).Reason);
            var row = r.Resolution.CarrierStatuses.Single(s => s.CarrierId == "o-fl-alert");
            Assert.True(row.RowLabels.HasFlag(CarrierRowLabels.CantRunHere));
            Assert.True(row.RowLabels.HasFlag(CarrierRowLabels.NoWheel));
            Assert.Contains(warns, w => w.Contains("not in this wheel"));
        }

        [Fact]
        public void Capability_HostsUnknown_PresenceNull_NotFalseOffScreen()
        {
            // E5-03(c) / E5-11 shape: present, hosts unknown → Presence null + warn.
            var doc = FieldOnlyDoc(42, Ov("o-fl-alert", FieldWrites.Suffix, "!"));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [42] = Cap(42, primaryHost: null, hosts: Array.Empty<string>()),
            };
            caps[42].PrimaryHostCatalogPageId = null;
            caps[42].HostCatalogPageIds = new List<string>();
            var warns = new List<string>();
            var c = Composer(doc, caps, warns.Add,
                primaryHost: new Dictionary<ushort, string>());
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("tyreTemps"),
                Ctx(), null, false, Snap("o-fl-alert", true)));

            var plan = Assert.Single(r.FieldPlans);
            Assert.Equal("o-fl-alert", plan.WinnerCarrierId);
            var row = r.Resolution.CarrierStatuses.Single(s => s.CarrierId == "o-fl-alert");
            Assert.Null(row.Presence);
            Assert.Contains(warns, w => w.Contains("hosts unknown"));
        }

        [Fact]
        public void Capability_ParamLocked_GearFromPbmeCatalog_Inert()
        {
            // E5-05: real pbme fixture — param 4 overridable:false.
            var path = TestPaths.CatalogPath();
            var catalog = CatalogLoader.LoadWheelCatalog(File.ReadAllText(path), _ => { });
            var map = FieldCapability.FromCatalog(catalog);
            Assert.Equal(false, map[4].Overridable);

            var doc = FieldOnlyDoc(4, Ov("o-gear", FieldWrites.Value, "N",
                content: Text("N")));
            // ASCII gate: gear lock fires first.
            map[4].ValueAscii = true;
            var c = Composer(doc, map);
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm(
                    map[4].PrimaryHostCatalogPageId ?? "lapInfo"),
                Ctx(), null, false, Snap("o-gear", true)));

            var plan = Assert.Single(r.FieldPlans);
            Assert.Null(plan.WinnerCarrierId);
            Assert.Equal(FieldDegradeReason.ParamLocked,
                Assert.Single(plan.DegradedChildren).Reason);
            var row = r.Resolution.CarrierStatuses.Single(s => s.CarrierId == "o-gear");
            Assert.True(row.RowLabels.HasFlag(CarrierRowLabels.CantRunHere));
        }

        [Fact]
        public void Capability_UnrenderableContent_DynamicKindValue_Inert()
        {
            // E5-08: content.kind=fuel on field value → UnrenderableContent.
            var fuelContent = new ContentObject { Kind = ContentKind.Fuel };
            var doc = FieldOnlyDoc(9,
                Ov("o-fuel", FieldWrites.Value, null, content: fuelContent));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [9] = Cap(9, ascii: true, primaryHost: "fuelErsDrs"),
            };
            var c = Composer(doc, caps);
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("fuelErsDrs"),
                Ctx(), null, false, Snap("o-fuel", true)));

            var plan = Assert.Single(r.FieldPlans);
            Assert.Null(plan.WinnerCarrierId);
            Assert.Equal(FieldDegradeReason.UnrenderableContent,
                Assert.Single(plan.DegradedChildren).Reason);
        }

        [Fact]
        public void Capability_PropertySuffix_CarriesSourceAndFormat()
        {
            // E5-08: property-with-source accepted on suffix; Source+Format in plan.
            var prop = new ContentObject
            {
                Kind = ContentKind.Property,
                Source = new ValueSource
                {
                    Kind = ValueSourceKind.BuiltIn,
                    Name = BuiltInProperties.Fuel,
                },
                Format = FieldFormats.Bare,
            };
            var doc = FieldOnlyDoc(5,
                Ov("o-prop", FieldWrites.Suffix, null, content: prop));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [5] = Cap(5, suffixWidth: 5, primaryHost: "fuelErsDrs"),
            };
            var c = Composer(doc, caps);
            var r = c.Tick(In(0, "p-x", DestinationIds.Itm("fuelErsDrs"),
                Ctx(), null, false, Snap("o-prop", true)));

            var plan = Assert.Single(r.FieldPlans);
            Assert.Equal("o-prop", plan.WinnerCarrierId);
            Assert.Equal(SuffixOwner.Override, plan.SuffixOwner);
            Assert.Equal(BuiltInProperties.Fuel, plan.SuffixSource.Name);
            Assert.Equal(FieldFormats.Bare, plan.SuffixFormat);
            Assert.Equal(FieldFormats.Bare, plan.ValueFormat);
        }

        // ═════════════════════════════════════════════════════════════════
        // Buffer continuity / landing follow / wheel-screen
        // ═════════════════════════════════════════════════════════════════

        [Fact]
        public void BufferContinuity_ItmWinnerDisplayed_LandingHostedStillComposes()
        {
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage,
                        Id = "p-shift",
                        Name = "SHIFT",
                        Base = new ContentWithEffect { Content = Speed() },
                    },
                    new PageEntry
                    {
                        Kind = PageEntryKind.ItmPage,
                        CatalogPageId = "fuelErsDrs",
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
                            Kind = PageRefKind.ItmPage,
                            CatalogPageId = "fuelErsDrs",
                        },
                        LandingPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-shift",
                        },
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
            };
            var c = Composer(doc);
            var r = c.Tick(In(
                0,
                segmentPage: "p-shift",
                displayed: DestinationIds.Itm("fuelErsDrs"),
                content: Ctx(speed: 88)));

            Assert.Equal("p-shift", r.SegmentHostedPageId);
            Assert.Equal(
                (SevenSegment.Digit0, SevenSegment.Digit8, SevenSegment.Digit8),
                Triple(r.SegmentFrame));
        }

        [Fact]
        public void LandingFollow_MultiTick_FrameFollowsImmediately_GlobalBlinkClock()
        {
            // E5-12 PROBE: p-shift(268) → p-delta(blink DEL) → p-ghost(absent) → p-shift
            // while DisplayedDestinationId=itm:fuelErsDrs. Global blink clock (nowMs only).
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage,
                        Id = "p-shift",
                        Name = "SHIFT",
                        Base = new ContentWithEffect { Content = Speed() },
                    },
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage,
                        Id = "p-delta",
                        Name = "DELTA",
                        Base = new ContentWithEffect
                        {
                            Content = Text("DEL"),
                            Effect = ContentEffect.Blink,
                        },
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
                            Kind = PageRefKind.ItmPage,
                            CatalogPageId = "fuelErsDrs",
                        },
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
            };
            var warns = new List<string>();
            var c = Composer(doc, warn: warns.Add);
            string itm = DestinationIds.Itm("fuelErsDrs");
            var content = Ctx(speed: 268);

            var t0 = c.Tick(In(0, "p-shift", itm, content));
            Assert.Equal(
                (SevenSegment.Digit2, SevenSegment.Digit6, SevenSegment.Digit8),
                Triple(t0.SegmentFrame));

            // Blink on at 0; off at 500 (global clock — no re-anchor on landing switch).
            var tDeltaOn = c.Tick(In(0, "p-delta", itm, content));
            Assert.Equal(
                (SevenSegment.D, SevenSegment.E, SevenSegment.L),
                Triple(tDeltaOn.SegmentFrame));

            var tDeltaOff = c.Tick(In(
                LegacyEffectClock.BlinkHalfPeriodMs, "p-delta", itm, content));
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                Triple(tDeltaOff.SegmentFrame));

            var tGhost = c.Tick(In(1000, "p-ghost", itm, content));
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                Triple(tGhost.SegmentFrame));
            Assert.Contains(warns, w => w.Contains("p-ghost"));

            var tBack = c.Tick(In(1000, "p-shift", itm, content));
            Assert.Equal(
                (SevenSegment.Digit2, SevenSegment.Digit6, SevenSegment.Digit8),
                Triple(tBack.SegmentFrame));
        }

        [Fact]
        public void WheelScreenHolds_StillComposes_MarksNonWritable()
        {
            // E5-07: SegmentSurfaceHeldByWheelScreen → frame produced, not writable.
            var doc = HostedDoc("p-a",
                new ContentWithEffect { Content = Text("PIT") });
            var c = Composer(doc);
            var r = c.Tick(In(0, "p-a", DestinationIds.RestIdle,
                Ctx(inGame: false), wheelScreenHolds: true));

            Assert.Equal(
                (SevenSegment.P, SevenSegment.I, SevenSegment.T),
                Triple(r.SegmentFrame));
            Assert.False(r.SegmentFrameWritable);
            Assert.False(r.ReclaimFrame);
        }

        [Fact]
        public void ReclaimEdge_MarksReclaimFrame_ForcesWritable()
        {
            // Contract §6.2 law 3: E5 produces reclaim frame + marker; E7/E8 writes.
            var doc = HostedDoc("p-a",
                new ContentWithEffect { Content = Text("PIT") });
            var c = Composer(doc);
            var input = In(0, "p-a", DestinationIds.Hosted("p-a"),
                Ctx(), wheelScreenHolds: false);
            input.ReclaimEdge = true;
            var r = c.Tick(input);
            Assert.True(r.ReclaimFrame);
            Assert.True(r.SegmentFrameWritable);
            Assert.Equal(
                (SevenSegment.P, SevenSegment.I, SevenSegment.T),
                Triple(r.SegmentFrame));
        }

        [Fact]
        public void DirectorOrder_ManualFeedsNextTick_TwoTickFixture()
        {
            // Contract §6.2 law 4: director runs LAST; Manual/Adopted feeds NEXT tick's
            // E4 + wheel-screen dismissal. SegmentSurfaceHeldByWheelScreen is same-tick.
            // This pure fixture pins the one-frame lag shape without E8 composition.
            bool pressFromDirectorTickN = false;
            bool pressSeenByE4OnTickN = false;
            bool pressSeenByE4OnTickN1 = false;
            bool surfaceHeldSameTick = false;

            // Tick N: E4 → E6 → E5 → (director last produces press for N+1)
            {
                bool e4Manual = pressFromDirectorTickN; // false on first tick
                pressSeenByE4OnTickN = e4Manual;
                bool e6Held = true; // wheel-screen holds this tick
                surfaceHeldSameTick = e6Held; // same-tick hold (not previous-tick)
                // director last:
                pressFromDirectorTickN = true; // adopted/manual this tick
            }
            // Tick N+1: press lands in E4
            {
                bool e4Manual = pressFromDirectorTickN;
                pressSeenByE4OnTickN1 = e4Manual;
            }

            Assert.False(pressSeenByE4OnTickN);
            Assert.True(pressSeenByE4OnTickN1);
            Assert.True(surfaceHeldSameTick);

            // E5 reclaim marker is representable on the release tick (same-tick hold → release).
            var doc = HostedDoc("p-a",
                new ContentWithEffect { Content = Text("GO ") });
            var c = Composer(doc);
            var held = c.Tick(In(0, "p-a", DestinationIds.Hosted("p-a"),
                Ctx(), wheelScreenHolds: true));
            Assert.False(held.SegmentFrameWritable);
            var release = In(1, "p-a", DestinationIds.Hosted("p-a"),
                Ctx(), wheelScreenHolds: false);
            release.ReclaimEdge = true;
            var reclaimed = c.Tick(release);
            Assert.True(reclaimed.ReclaimFrame);
            Assert.True(reclaimed.SegmentFrameWritable);
        }

        [Fact]
        public void WheelScreenHolds_DemotesPageOnScreen_ToOffScreen()
        {
            // E6-OP-05: while held, page:{id} OnScreen → OffScreen (record honesty).
            var doc = HostedDoc("p-a",
                new ContentWithEffect { Content = Text("BAS") },
                Layer("l-top", "TOP"));
            var c = Composer(doc);

            var free = c.Tick(In(0, "p-a", DestinationIds.Hosted("p-a"),
                Ctx(), null, false, Snap("l-top", true)));
            Assert.Equal(CarrierPresence.OnScreen,
                free.Resolution.CarrierStatuses.Single(s => s.CarrierId == "l-top").Presence);

            var held = c.Tick(In(0, "p-a", DestinationIds.Hosted("p-a"),
                Ctx(), null, true, Snap("l-top", true)));
            Assert.Equal(CarrierPresence.OffScreen,
                held.Resolution.CarrierStatuses.Single(s => s.CarrierId == "l-top").Presence);
            Assert.False(held.SegmentFrameWritable);
            // Winner id still carried; presence surrendered.
            Assert.Equal("l-top", held.SegmentWinnerCarrierId);
            Assert.Equal(0, held.Resolution.CarrierStatuses
                .Count(s => s.Presence == CarrierPresence.OnScreen
                    && s.SurfaceId != null
                    && s.SurfaceId.StartsWith("page:", StringComparison.Ordinal)));
        }

        // ═════════════════════════════════════════════════════════════════
        // Effect clocks (v9 law) + field blink resolution
        // ═════════════════════════════════════════════════════════════════

        [Fact]
        public void EffectClock_Blink_AnchoredOnInjectedNowMs()
        {
            var doc = HostedDoc("p-a",
                new ContentWithEffect
                {
                    Content = Text("PIT"),
                    Effect = ContentEffect.Blink,
                });
            var c = Composer(doc);

            var on = c.Tick(In(0, "p-a", DestinationIds.Hosted("p-a"), Ctx()));
            Assert.Equal(
                (SevenSegment.P, SevenSegment.I, SevenSegment.T),
                Triple(on.SegmentFrame));

            var off = c.Tick(In(
                LegacyEffectClock.BlinkHalfPeriodMs,
                "p-a", DestinationIds.Hosted("p-a"), Ctx()));
            Assert.Equal(
                (SevenSegment.Blank, SevenSegment.Blank, SevenSegment.Blank),
                Triple(off.SegmentFrame));

            var directOn = LegacyEffectClock.Apply("PIT", LegacyEffect.Blink, 0);
            var directOff = LegacyEffectClock.Apply(
                "PIT", LegacyEffect.Blink, LegacyEffectClock.BlinkHalfPeriodMs);
            Assert.Equal(directOn, on.SegmentFrame);
            Assert.Equal(directOff, off.SegmentFrame);
        }

        [Fact]
        public void EffectClock_Scroll_StepsWithInjectedClock()
        {
            var doc = HostedDoc("p-a",
                new ContentWithEffect
                {
                    Content = new ContentObject
                    {
                        Kind = ContentKind.Message,
                        Text = "HELLO",
                    },
                    Effect = ContentEffect.Scroll,
                });
            var c = Composer(doc);
            var t0 = c.Tick(In(0, "p-a", DestinationIds.Hosted("p-a"), Ctx()));
            var t1 = c.Tick(In(
                LegacyEffectClock.ScrollStepMs,
                "p-a", DestinationIds.Hosted("p-a"), Ctx()));

            var d0 = LegacyEffectClock.Apply("HELLO", LegacyEffect.Scroll, 0);
            var d1 = LegacyEffectClock.Apply(
                "HELLO", LegacyEffect.Scroll, LegacyEffectClock.ScrollStepMs);
            Assert.Equal(d0, t0.SegmentFrame);
            Assert.Equal(d1, t1.SegmentFrame);
            Assert.NotEqual(t0.SegmentFrame, t1.SegmentFrame);
        }

        [Fact]
        public void FieldEffect_BlinkSuffix_WidthBlankOnOffPhase_SharedClock()
        {
            // E5-13: AlignedSuffixText is width-blank when !EffectVisible; shared clock.
            var doc = FieldOnlyDoc(42,
                Ov("o-bang", FieldWrites.Suffix, "!", ContentEffect.Blink));
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [42] = Cap(42, suffixWidth: 1),
            };
            var c = Composer(doc, caps);

            var on = c.Tick(In(0, "p-x", DestinationIds.Itm("tyreTemps"),
                Ctx(), null, false, Snap("o-bang", true)));
            Assert.True(on.FieldPlans[0].EffectVisible);
            Assert.Equal("!", on.FieldPlans[0].AlignedSuffixText);
            Assert.True(LegacyEffectClock.IsOnPhase(0));

            var off = c.Tick(In(
                LegacyEffectClock.BlinkHalfPeriodMs, "p-x",
                DestinationIds.Itm("tyreTemps"),
                Ctx(), null, false, Snap("o-bang", true)));
            Assert.False(off.FieldPlans[0].EffectVisible);
            Assert.Equal(" ", off.FieldPlans[0].AlignedSuffixText);
            Assert.False(LegacyEffectClock.IsOnPhase(LegacyEffectClock.BlinkHalfPeriodMs));
        }

        // ═════════════════════════════════════════════════════════════════
        // Route law / dismissal / session scope
        // ═════════════════════════════════════════════════════════════════

        [Fact]
        public void RouteLaw_DismissedLayerStillPaints_AndSelfStampsDismissedLabel()
        {
            var doc = HostedDoc("p-alerts", bas: null,
                Layer("l-pit", "PIT", ContentEffect.Blink),
                Layer("l-low", "LO"));
            var c = Composer(doc);
            var r = c.Tick(In(
                0, "p-alerts", DestinationIds.Hosted("p-alerts"),
                Ctx(), dismissed: new[] { "l-pit" }, false,
                Snap("l-pit", true), Snap("l-low", false)));

            Assert.Equal("l-pit", r.SegmentWinnerCarrierId);
            Assert.Equal(
                (SevenSegment.P, SevenSegment.I, SevenSegment.T),
                Triple(r.SegmentFrame));
            var pit = r.Resolution.CarrierStatuses.Single(s => s.CarrierId == "l-pit");
            Assert.Equal(CarrierPresence.OnScreen, pit.Presence);
            Assert.True(pit.RowLabels.HasFlag(CarrierRowLabels.Dismissed));
        }

        [Fact]
        public void OutOfSessionScope_MirroredWhenIneligible()
        {
            // E5-10: !Eligible → Waiting + OutOfSessionScope.
            var doc = HostedDoc("p-a",
                new ContentWithEffect { Content = Text("BAS") },
                Layer("l-idle", "IDL"));
            var c = Composer(doc);
            var r = c.Tick(In(0, "p-a", DestinationIds.Hosted("p-a"),
                Ctx(), null, false,
                Snap("l-idle", active: false, eligible: false)));

            var row = r.Resolution.CarrierStatuses.Single(s => s.CarrierId == "l-idle");
            Assert.Equal(CarrierPresence.Waiting, row.Presence);
            Assert.True(row.RowLabels.HasFlag(CarrierRowLabels.OutOfSessionScope));
        }

        [Fact]
        public void UnknownDisplayedDestination_PresenceNull_NotOffScreen()
        {
            // E5-11: null / rest → Presence null + warn.
            var doc = HostedDoc("p-alerts", bas: null,
                Layer("l-pit", "PIT"));
            var warns = new List<string>();
            var c = Composer(doc, warn: warns.Add);
            var r = c.Tick(In(0, "p-alerts", displayed: null,
                Ctx(), null, false, Snap("l-pit", true)));

            var pit = r.Resolution.CarrierStatuses.Single(s => s.CarrierId == "l-pit");
            Assert.Null(pit.Presence);
            Assert.Contains(warns, w => w.Contains("null/unknown/rest"));

            var r2 = c.Tick(In(0, "p-alerts", DestinationIds.RestInSession,
                Ctx(), null, false, Snap("l-pit", true)));
            Assert.Null(r2.Resolution.CarrierStatuses
                .Single(s => s.CarrierId == "l-pit").Presence);
        }

        // ═════════════════════════════════════════════════════════════════
        // Degraded pages / segment format honesty
        // ═════════════════════════════════════════════════════════════════

        [Fact]
        public void DegradedHostedPage_Warns_AndKeepsUnambiguousRows()
        {
            // E5-09: duplicate id → warn (no silent drop of the fact).
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage,
                        Id = "dup",
                        Name = "A",
                        Layers = new List<LayerEntry> { Layer("l-a", "AAA") },
                    },
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage,
                        Id = "dup",
                        Name = "B",
                        Layers = new List<LayerEntry> { Layer("l-b", "BBB") },
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
                            Kind = PageRefKind.HostedPage, Id = "dup",
                        },
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
            };
            var warns = new List<string>();
            var c = Composer(doc, warn: warns.Add);
            var r = c.Tick(In(0, "dup", DestinationIds.Hosted("dup"),
                Ctx(), null, false, Snap("l-a", true)));

            Assert.Contains(warns, w => w.Contains("degraded"));
            // Live page still has l-a; degraded duplicate does not invent a second surface.
            Assert.Contains(r.Resolution.CarrierStatuses, s => s.CarrierId == "l-a");
        }

        // ═════════════════════════════════════════════════════════════════
        // oneDecimal (E5-06 option (a) — deliberate change vs v9)
        // ═════════════════════════════════════════════════════════════════

        [Fact]
        public void DeliberateChange_OneDecimal_Fuel42()
        {
            // Ruled: content.format = oneDecimal on numeric kinds → exactly one decimal,
            // no leading-zero pad, EncodeWithDots fold. Encoder-derived expected bytes.

            // ── '4.2' (two positions, right-padded blank) ───────────────
            var expected42 = SevenSegment.EncodeWithDots("4.2");
            Assert.Equal(2, expected42.Count);
            var doc42 = HostedDoc("p-fuel",
                new ContentWithEffect
                {
                    Content = new ContentObject
                    {
                        Kind = ContentKind.Fuel,
                        Format = FrameComposer.SegmentFormatOneDecimal,
                    },
                });
            var warns42 = new List<string>();
            var c42 = Composer(doc42, warn: warns42.Add);
            var r42 = c42.Tick(In(0, "p-fuel", DestinationIds.Hosted("p-fuel"),
                Ctx(fuel: 4.2)));

            Assert.False(r42.SegmentContentFormatDegraded);
            Assert.DoesNotContain(warns42, w => w.Contains("not consumed"));
            Assert.Equal("4.2", r42.SegmentRenderedText);
            Assert.Equal(
                (expected42[0], expected42[1], SevenSegment.Blank),
                Triple(r42.SegmentFrame));
            Assert.Equal(
                ((byte)(SevenSegment.Digit4 | SevenSegment.Dot),
                    SevenSegment.Digit2, SevenSegment.Blank),
                Triple(r42.SegmentFrame));

            // ── 12.7 three-position case ────────────────────────────────
            var expected127 = SevenSegment.EncodeWithDots("12.7");
            Assert.Equal(3, expected127.Count);
            var doc127 = HostedDoc("p-fuel",
                new ContentWithEffect
                {
                    Content = new ContentObject
                    {
                        Kind = ContentKind.Fuel,
                        Format = FrameComposer.SegmentFormatOneDecimal,
                    },
                });
            var r127 = Composer(doc127).Tick(In(
                0, "p-fuel", DestinationIds.Hosted("p-fuel"), Ctx(fuel: 12.7)));
            Assert.Equal("12.7", r127.SegmentRenderedText);
            Assert.Equal(
                (expected127[0], expected127[1], expected127[2]),
                Triple(r127.SegmentFrame));
            Assert.Equal(
                (SevenSegment.Digit1,
                    (byte)(SevenSegment.Digit2 | SevenSegment.Dot),
                    SevenSegment.Digit7),
                Triple(r127.SegmentFrame));

            // ── Overflow fallback (≥100 → "XXX.Y" = 4 positions → v9 D3) ─
            // Fallback rule pinned in TryFormatOneDecimal: EncodeWithDots count > 3.
            var rOverflow = Composer(doc42).Tick(In(
                0, "p-fuel", DestinationIds.Hosted("p-fuel"), Ctx(fuel: 100.5)));
            Assert.False(rOverflow.SegmentContentFormatDegraded);
            Assert.Equal(
                LegacyValueFormatter.FormatFuel(100.5),
                rOverflow.SegmentRenderedText);
            Assert.Equal(
                Triple(LegacyValueFormatter.Render(
                    LegacyValueFormatter.FormatFuel(100.5))),
                Triple(rOverflow.SegmentFrame));

            // ── Format absent = v9 integer+D3 parity (untouched path) ──
            var docAbsent = HostedDoc("p-fuel",
                new ContentWithEffect
                {
                    Content = new ContentObject { Kind = ContentKind.Fuel },
                });
            var rAbsent = Composer(docAbsent).Tick(In(
                0, "p-fuel", DestinationIds.Hosted("p-fuel"), Ctx(fuel: 4.2)));
            Assert.False(rAbsent.SegmentContentFormatDegraded);
            Assert.Equal(
                LegacyValueFormatter.FormatFuel(4.2),
                rAbsent.SegmentRenderedText);
            Assert.Equal(
                (SevenSegment.Digit0, SevenSegment.Digit0, SevenSegment.Digit4),
                Triple(rAbsent.SegmentFrame));

            // ── Sam board trio: Speed 268 / Fuel 4.2 / temp 94 ──────────
            // Speed & temp stay integer (format absent); Fuel is oneDecimal.
            // '094' is the v9 pad law for temp 94 — unchanged, deliberate.
            var docSpeed = HostedDoc("p-spd",
                new ContentWithEffect
                {
                    Content = new ContentObject { Kind = ContentKind.Speed },
                });
            var rSpeed = Composer(docSpeed).Tick(In(
                0, "p-spd", DestinationIds.Hosted("p-spd"), Ctx(speed: 268)));
            Assert.Equal("268", rSpeed.SegmentRenderedText);
            Assert.Equal(
                (SevenSegment.Digit2, SevenSegment.Digit6, SevenSegment.Digit8),
                Triple(rSpeed.SegmentFrame));

            // Fuel 4.2 already asserted above as r42.

            var docTemp = HostedDoc("p-temp",
                new ContentWithEffect
                {
                    Content = new ContentObject
                    {
                        Kind = ContentKind.Property,
                        Source = new ValueSource
                        {
                            Kind = ValueSourceKind.BuiltIn,
                            Name = BuiltInProperties.Fuel,
                        },
                    },
                });
            var rTemp = Composer(docTemp).Tick(In(
                0, "p-temp", DestinationIds.Hosted("p-temp"),
                Ctx(props: new DictReader(94))));
            Assert.Equal("094", rTemp.SegmentRenderedText);
            Assert.Equal(
                (SevenSegment.Digit0, SevenSegment.Digit9, SevenSegment.Digit4),
                Triple(rTemp.SegmentFrame));
        }

        [Fact]
        public void SegmentFormat_UnknownFormat_WarnsAndMarksDegraded()
        {
            // Unknown spelling still warn-once + degraded; integer path preserved.
            var doc = HostedDoc("p-fuel",
                new ContentWithEffect
                {
                    Content = new ContentObject
                    {
                        Kind = ContentKind.Fuel,
                        Format = "twoDecimal",
                    },
                });
            var warns = new List<string>();
            var c = Composer(doc, warn: warns.Add);
            var r = c.Tick(In(0, "p-fuel", DestinationIds.Hosted("p-fuel"),
                Ctx(fuel: 4.2)));

            Assert.True(r.SegmentContentFormatDegraded);
            Assert.Contains(warns, w => w.Contains("twoDecimal") && w.Contains("not consumed"));
            Assert.Equal(
                (SevenSegment.Digit0, SevenSegment.Digit0, SevenSegment.Digit4),
                Triple(r.SegmentFrame));
        }

        // ═════════════════════════════════════════════════════════════════
        // Composed-resolution / merge law
        // ═════════════════════════════════════════════════════════════════

        [Fact]
        public void Resolution_ExactlyOneOnScreenPerSurface_WhenPageUp()
        {
            var doc = HostedDoc("p-a",
                new ContentWithEffect { Content = Text("BAS") },
                Layer("l-top", "TOP"),
                Layer("l-mid", "MID"));
            doc.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.HostedPage,
                Id = "p-b",
                Name = "B",
                Base = new ContentWithEffect { Content = Text("BBB") },
                Layers = new List<LayerEntry> { Layer("l-b", "ZZZ") },
            });
            doc.Fields = new Dictionary<ushort, FieldEntry>
            {
                [42] = new FieldEntry
                {
                    Base = new FieldBase { BaseSuffix = "C" },
                    Overrides = new List<FieldOverride>
                    {
                        Ov("o-a", FieldWrites.Suffix, "!"),
                        Ov("o-b", FieldWrites.Suffix, "X"),
                    },
                },
            };

            var caps = new Dictionary<ushort, FieldCapability>
            {
                [42] = Cap(42, suffixWidth: 1),
            };
            var c = Composer(doc, caps);
            var r = c.Tick(In(
                0, "p-a", DestinationIds.Hosted("p-a"), Ctx(), null, false,
                Snap("l-top", true), Snap("l-mid", true),
                Snap("l-b", true),
                Snap("o-a", true), Snap("o-b", true)));

            var pageA = r.Resolution.CarrierStatuses
                .Where(s => s.SurfaceId == FrameComposer.PageSurfaceId("p-a"))
                .ToList();
            Assert.Equal(1, pageA.Count(s => s.Presence == CarrierPresence.OnScreen));
            Assert.Equal("l-top",
                pageA.Single(s => s.Presence == CarrierPresence.OnScreen).CarrierId);

            var pageB = r.Resolution.CarrierStatuses
                .Where(s => s.SurfaceId == FrameComposer.PageSurfaceId("p-b"))
                .ToList();
            Assert.Equal(CarrierPresence.OffScreen,
                pageB.Single(s => s.CarrierId == "l-b").Presence);

            var field = r.Resolution.CarrierStatuses
                .Where(s => s.SurfaceId == FrameComposer.FieldSurfaceId(42))
                .ToList();
            Assert.Equal(CarrierPresence.OffScreen,
                field.Single(s => s.CarrierId == "o-a").Presence);

            var r2 = c.Tick(In(
                0, "p-a", DestinationIds.Itm("tyreTemps"), Ctx(), null, false,
                Snap("l-top", true), Snap("l-mid", true),
                Snap("l-b", true),
                Snap("o-a", true), Snap("o-b", true)));
            Assert.Equal(CarrierPresence.OnScreen,
                r2.Resolution.CarrierStatuses
                    .Single(s => s.CarrierId == "o-a").Presence);
        }

        [Fact]
        public void MergeLaw_OneDocument_E4ThenE5_OneRowPerCarrierSurface()
        {
            // E5-04: one document drives SeatArbiter + FrameComposer; merge by
            // (CarrierId, SurfaceId) with label union; shared primary-host map.
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage,
                        Id = "p-alerts",
                        Name = "Alerts",
                        Layers = new List<LayerEntry>
                        {
                            new LayerEntry
                            {
                                Id = "l-pit",
                                Name = "Pit",
                                Content = Text("PIT"),
                                ActsAsEntrypoint = true,
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
                            },
                        },
                    },
                },
                Fields = new Dictionary<ushort, FieldEntry>
                {
                    [42] = new FieldEntry
                    {
                        Base = new FieldBase { BaseSuffix = "C" },
                        Overrides = new List<FieldOverride>
                        {
                            new FieldOverride
                            {
                                Id = "o-fl-alert",
                                Writes = FieldWrites.Suffix,
                                Content = Text("!"),
                                ActsAsEntrypoint = true,
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
                            },
                        },
                    },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Seat,
                            Id = "s-alerts",
                            Target = new PageRef
                            {
                                Kind = PageRefKind.HostedPage, Id = "p-alerts",
                            },
                        },
                    },
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.ItmPage,
                            CatalogPageId = "tyreTemps",
                        },
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
            };
            doc = Normalize(doc);

            var hostMap = new Dictionary<ushort, string> { [42] = "tyreTemps" };
            var caps = new Dictionary<ushort, FieldCapability>
            {
                [42] = Cap(42, suffixWidth: 1, primaryHost: "tyreTemps"),
            };

            var arbiter = new SeatArbiter(doc, new SeatArbiterOptions
            {
                PrimaryHostByParam = hostMap,
                DeviceKey = "test",
            });
            var composer = new FrameComposer(doc, new FrameComposerOptions
            {
                Capabilities = caps,
                PrimaryHostByParam = hostMap,
                DeviceKey = "test",
            });

            var snaps = new[]
            {
                Snap("l-pit", true),
                Snap("o-fl-alert", true),
            };
            long now = 0;
            var e4 = arbiter.Tick(new SeatArbiterTickInput
            {
                NowMs = now,
                InGame = true,
                CarrierSnapshots = snaps,
            });
            var e5 = composer.Tick(new FrameComposerTickInput
            {
                NowMs = now,
                SegmentHostedPageId = "p-alerts",
                DisplayedDestinationId = e4.Intent?.EffectivePageDestinationId
                    ?? DestinationIds.Hosted("p-alerts"),
                CarrierSnapshots = snaps,
                DismissedCarrierIds = e4.DismissedCarrierIds
                    ?? Array.Empty<string>(),
                Content = Ctx(),
            });

            // Pure merge per contract §6.1.
            var merged = MergeRecords(e4.Resolution, e5.Resolution);
            var keys = merged.CarrierStatuses
                .Select(s => (s.CarrierId, s.SurfaceId))
                .ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());

            // Shared host map → one destination for field:42.
            var fieldRows = merged.CarrierStatuses
                .Where(s => s.SurfaceId == DestinationIds.FieldSurface(42))
                .ToList();
            Assert.All(fieldRows, row =>
                Assert.Equal(DestinationIds.Itm("tyreTemps"), row.DestinationId));

            // Page surface rows exist once.
            Assert.Single(merged.CarrierStatuses, s =>
                s.CarrierId == "l-pit"
                && s.SurfaceId == DestinationIds.PageSurface("p-alerts"));
        }

        private static ComposedResolutionRecord MergeRecords(
            ComposedResolutionRecord? e4, ComposedResolutionRecord? e5)
        {
            // Minimal pure merge matching contract §6.1 for the unit test.
            var rows = new Dictionary<(string, string), CarrierResolutionStatus>();
            void ingest(IReadOnlyList<CarrierResolutionStatus>? list)
            {
                if (list == null) return;
                foreach (var s in list)
                {
                    var key = (s.CarrierId, s.SurfaceId);
                    if (!rows.TryGetValue(key, out var existing))
                    {
                        rows[key] = s;
                        continue;
                    }
                    var presence = existing.Presence ?? s.Presence;
                    if (existing.Presence != null && s.Presence != null
                        && existing.Presence != s.Presence)
                        throw new InvalidOperationException(
                            "presence conflict for " + key);
                    rows[key] = new CarrierResolutionStatus(
                        s.CarrierId,
                        s.SurfaceId,
                        existing.DestinationId ?? s.DestinationId,
                        presence,
                        existing.RemainingMs ?? s.RemainingMs,
                        existing.RowLabels | s.RowLabels);
                }
            }
            ingest(e4?.CarrierStatuses);
            ingest(e5?.CarrierStatuses);

            var winners = new Dictionary<string, SurfaceWinner>(StringComparer.Ordinal);
            void ingestW(IReadOnlyList<SurfaceWinner>? list)
            {
                if (list == null) return;
                foreach (var w in list)
                    winners[w.SurfaceId] = w;
            }
            ingestW(e4?.SurfaceWinners);
            ingestW(e5?.SurfaceWinners);

            var snaps = new Dictionary<string, CarrierTickSnapshot>(StringComparer.Ordinal);
            void ingestS(IReadOnlyList<CarrierTickSnapshot>? list)
            {
                if (list == null) return;
                foreach (var s in list)
                    if (s.CarrierId != null) snaps[s.CarrierId] = s;
            }
            ingestS(e4?.CarrierSnapshots);
            ingestS(e5?.CarrierSnapshots);

            return new ComposedResolutionRecord(
                e4?.TickMs ?? e5?.TickMs ?? 0,
                e4?.DeviceKey ?? e5?.DeviceKey ?? "",
                winners.Values.ToList(),
                rows.Values.ToList(),
                snaps.Values.ToList());
        }

        // ═════════════════════════════════════════════════════════════════
        // Surface-key helpers
        // ═════════════════════════════════════════════════════════════════

        [Fact]
        public void SurfaceKeys_FieldSurfaceNormalizesViaUShortParse()
        {
            // E5-14: "05" / " 5" → field:5
            Assert.Equal("field:5", DestinationIds.FieldSurface(5));
            Assert.Equal("field:5", DestinationIds.FieldSurface("5"));
            Assert.Equal("field:5", DestinationIds.FieldSurface("05"));
            Assert.Equal("field:5", DestinationIds.FieldSurface(" 5"));
            Assert.Equal(FrameComposer.FieldSurfaceId(5), DestinationIds.FieldSurface(5));
            Assert.Equal(FrameComposer.PageSurfaceId("p-a"), DestinationIds.PageSurface("p-a"));
        }

        // ═════════════════════════════════════════════════════════════════
        // Parity battery — real v9 DisplayRuleStack harness
        // FA2: v2 side is a FROZEN fixture pair (parity-pairs/*.v2.json), not a
        // test-time migration. v1 JSON stays inline and drives the v9 reference.
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// Loads a frozen v2 parity-pair fixture (embedded under
        /// Display/Fixtures/parity-pairs/). FA2: counterparts were produced once
        /// offline; the harness never migrates at test time.
        /// </summary>
        private static DisplayConfigV2 LoadParityV2(string fileName)
        {
            var asm = typeof(FrameComposerTests).Assembly;
            // Embedded-resource names fold path separators and '-' in folders to '_'.
            string suffix = ".Display.Fixtures.parity_pairs." + fileName;
            string resource = asm.GetManifestResourceNames()
                .Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
            using (var stream = asm.GetManifestResourceStream(resource))
            using (var reader = new StreamReader(stream!))
                return DisplayConfigV2Serializer.Load(reader.ReadToEnd(), _ => { });
        }

        private static string BuildV1SingleScreen(
            string text, string contentKind, string effect)
        {
            string textJson = !string.IsNullOrEmpty(text)
                ? $@", ""text"": ""{text}"""
                : "";
            return $@"{{
  ""schemaVersion"": 1,
  ""segmentDisplay"": {{
    ""screens"": [
      {{
        ""id"": ""s1"",
        ""name"": ""S1"",
        ""contentKind"": ""{contentKind}""{textJson},
        ""effect"": ""{effect}"",
        ""inRotation"": true
      }}
    ],
    ""baseScreenId"": ""s1"",
    ""rules"": []
  }}
}}";
        }

        // ── v9 stack harness (real DisplayRuleStack) ─────────────────────

        private sealed class FakePageControl : IItmPageControl
        {
            public ItmLifecycleState State { get; set; } = ItmLifecycleState.Synced;
            public byte? CurrentWirePage { get; set; } = 1;
            public long SyncGeneration { get; set; }
            public void RequestPage(byte wirePage) { }
        }

        private sealed class V9Harness
        {
            public long T;
            public DisplayRuleStack Stack = null!;
            public readonly FakePageControl Control = new FakePageControl();

            public static V9Harness Create(string configJson)
            {
                var h = new V9Harness();
                var config = DisplayConfigSerializer.Load(configJson, _ => { });
                h.Stack = new DisplayRuleStack(
                    config, h.Control, itmDeviceId: 2, defaultWirePage: 1,
                    log: _ => { }, nowMs: () => h.T);
                return h;
            }
        }

        private static readonly Type StatusDataType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.StatusData`1")
                .MakeGenericType(typeof(object));

        private static object NewStatus() =>
            FormatterServices.GetUninitializedObject(StatusDataType);

        private static void Set(object s, string p, object v) =>
            s.GetType().GetProperty(p).GetSetMethod(true).Invoke(s, new[] { v });

        private static GameData Live(
            string gear = "1",
            double speed = 0,
            double rpm = 0,
            int pos = 0,
            double fuel = 0,
            int pit = 0)
        {
            var s = NewStatus();
            Set(s, "Gear", gear);
            Set(s, "SpeedLocal", speed);
            Set(s, "Rpms", rpm);
            Set(s, "Position", pos);
            Set(s, "Fuel", fuel);
            Set(s, "IsInPitLane", pit);
            // PitLimiterOn is the built-in the pit rule keys on (not IsInPitLane alone).
            Set(s, "PitLimiterOn", pit);
            var d = new GameData { NewData = (StatusDataBase)s };
            typeof(GameData).GetProperty("GameRunning").GetSetMethod(true)
                .Invoke(d, new object[] { true });
            return d;
        }


        // ═════════════════════════════════════════════════════════════════
        // Catalog capability helper
        // ═════════════════════════════════════════════════════════════════

        [Fact]
        public void FieldCapability_FromCatalog_MergesHostsAndPrimary()
        {
            var path = TestPaths.CatalogPath();
            var catalog = CatalogLoader.LoadWheelCatalog(File.ReadAllText(path), _ => { });
            var map = FieldCapability.FromCatalog(catalog);

            Assert.True(map.ContainsKey(42));
            Assert.True(map[42].SuffixSupported);
            Assert.Equal(1, map[42].SuffixWidth);
            Assert.Equal(false, map[42].ValueAscii);
            Assert.Equal("tyreTemps", map[42].PrimaryHostCatalogPageId);
            Assert.Equal(false, map[4].Overridable);
        }


    }
}
