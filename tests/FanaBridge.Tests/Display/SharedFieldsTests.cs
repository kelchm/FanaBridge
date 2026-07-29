using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Composition;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Protocol;
using FanaBridge.UI.Display;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Task #23 — shared fields: schema closure, one-ladder resolution, FieldFormats
    /// growth, exclusion deletion, reach derivation.
    /// </summary>
    public class SharedFieldsTests
    {
        private static WheelCatalog Pbme()
        {
            Assert.True(CatalogLoader.TryResolve("pbme", out var c, _ => { }));
            return c!;
        }

        // ── Schema round-trip ────────────────────────────────────────────

        [Fact]
        public void SharedFields_RoundTrip_PreservesUnknownMembers()
        {
            var cfg = new DisplayConfigV2
            {
                SharedFields = new Dictionary<string, FieldEntry>
                {
                    ["speed"] = new FieldEntry
                    {
                        Base = new FieldBase { Format = FieldFormats.Whole },
                        ExtensionData = new Dictionary<string, JToken>
                        {
                            ["v3Shared"] = JToken.FromObject(true),
                        },
                    },
                    ["gear"] = new FieldEntry
                    {
                        Base = new FieldBase { Format = FieldFormats.Neutral },
                    },
                },
            };

            string json = DisplayConfigV2Serializer.Save(cfg);
            Assert.Contains("\"sharedFields\"", json);
            Assert.Contains("\"speed\"", json);
            Assert.Contains("\"v3Shared\"", json);

            var loaded = DisplayConfigV2Serializer.Load(json, _ => { });
            Assert.NotNull(loaded.SharedFields);
            Assert.True(loaded.SharedFields!.ContainsKey("speed"));
            Assert.True(loaded.SharedFields.ContainsKey("gear"));
            Assert.Equal(FieldFormats.Whole, loaded.SharedFields["speed"].Base?.Format);
            Assert.NotNull(loaded.SharedFields["speed"].ExtensionData);
            Assert.True(loaded.SharedFields["speed"].ExtensionData!.ContainsKey("v3Shared"));
        }

        [Fact]
        public void SharedFields_AbsentWhenEmpty_NotEmitted()
        {
            var cfg = new DisplayConfigV2();
            string json = DisplayConfigV2Serializer.Save(cfg);
            Assert.DoesNotContain("sharedFields", json);
        }

        // ── Validator matrix ─────────────────────────────────────────────

        [Fact]
        public void Validator_UnknownLogicalId_InertWarnOnce()
        {
            var warns = new List<string>();
            var cfg = new DisplayConfigV2
            {
                SharedFields = new Dictionary<string, FieldEntry>
                {
                    ["noSuchField"] = new FieldEntry
                    {
                        Base = new FieldBase { Format = FieldFormats.Whole },
                    },
                },
            };
            DisplayConfigV2Validator.Normalize(cfg, warns.Add, Pbme());
            Assert.True(cfg.SharedFields!["noSuchField"].DegradedAtLoad);
            Assert.Contains(warns, w => w.Contains("unknown logical id") && w.Contains("noSuchField"));
        }

        [Fact]
        public void Validator_NoCatalog_SharedFieldsInert_WarnOnce()
        {
            var warns = new List<string>();
            var cfg = new DisplayConfigV2
            {
                SharedFields = new Dictionary<string, FieldEntry>
                {
                    ["speed"] = new FieldEntry(),
                    ["gear"] = new FieldEntry(),
                },
            };
            DisplayConfigV2Validator.Normalize(cfg, warns.Add, catalog: null);
            Assert.True(cfg.SharedFields!["speed"].DegradedAtLoad);
            Assert.True(cfg.SharedFields["gear"].DegradedAtLoad);
            Assert.Equal(1, warns.Count(w => w.Contains("no catalog")));
        }

        [Fact]
        public void Validator_Collision_SharedWins_FieldsNamedInert()
        {
            var warns = new List<string>();
            var cfg = new DisplayConfigV2
            {
                Fields = new Dictionary<ushort, FieldEntry>
                {
                    [1] = new FieldEntry
                    {
                        Base = new FieldBase { Format = "from-fields" },
                    },
                },
                SharedFields = new Dictionary<string, FieldEntry>
                {
                    ["speed"] = new FieldEntry
                    {
                        Base = new FieldBase { Format = FieldFormats.Whole },
                    },
                },
            };
            DisplayConfigV2Validator.Normalize(cfg, warns.Add, Pbme());
            Assert.True(cfg.Fields[1].DegradedAtLoad);
            Assert.Contains("addressed by shared field 'speed'", cfg.Fields[1].DegradeReason);
            Assert.False(cfg.SharedFields!["speed"].DegradedAtLoad);
            Assert.Contains(warns, w => w.Contains("sharedFields wins"));
        }

        private static FieldOverride OkOverride(string id, bool entrypoint = false)
            => new FieldOverride
            {
                Id = id,
                Writes = FieldWrites.Value,
                Content = new ContentObject
                {
                    Kind = ContentKind.Property,
                    Source = new ValueSource
                    {
                        Kind = ValueSourceKind.BuiltIn,
                        Name = BuiltInProperties.FuelPercent,
                    },
                },
                Condition = new Condition
                {
                    Source = new ValueSource
                    {
                        Kind = ValueSourceKind.BuiltIn,
                        Name = BuiltInProperties.FuelPercent,
                    },
                    Operator = ConditionOperator.LessThan,
                    Value = 10,
                },
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                ActsAsEntrypoint = entrypoint,
            };

        /// <summary>
        /// BLOCKER pin: shared ladder registers first. Same override id on the
        /// page-scoped side degrades as the duplicate; shared child stays live.
        /// Both declaration orders (shared-then-fields / fields-then-shared in the
        /// document object) hold — authority is normalize order, not JSON order.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Validator_SameIdChild_SharedKeepsId_FieldsDuplicate(bool fieldsFirstInDict)
        {
            var warns = new List<string>();
            var fields = new Dictionary<ushort, FieldEntry>
            {
                [1] = new FieldEntry
                {
                    Overrides = new List<FieldOverride> { OkOverride("ov-shared-id") },
                },
            };
            var shared = new Dictionary<string, FieldEntry>
            {
                ["speed"] = new FieldEntry
                {
                    Overrides = new List<FieldOverride> { OkOverride("ov-shared-id", entrypoint: true) },
                },
            };

            var cfg = fieldsFirstInDict
                ? new DisplayConfigV2 { Fields = fields, SharedFields = shared }
                : new DisplayConfigV2 { SharedFields = shared, Fields = fields };

            DisplayConfigV2Validator.Normalize(cfg, warns.Add, Pbme());

            Assert.False(cfg.SharedFields!["speed"].Overrides![0].DegradedAtLoad);
            Assert.True(cfg.Fields[1].DegradedAtLoad); // collision inert
            Assert.True(cfg.Fields[1].Overrides![0].DegradedAtLoad); // duplicate id
            Assert.Contains(warns, w => w.Contains("duplicate override id"));
            Assert.Contains(warns, w => w.Contains("sharedFields wins"));
        }

        [Fact]
        public void ChildRef_ToSharedOverride_Resolves()
        {
            var warns = new List<string>();
            var cfg = new DisplayConfigV2
            {
                SharedFields = new Dictionary<string, FieldEntry>
                {
                    ["speed"] = new FieldEntry
                    {
                        Overrides = new List<FieldOverride> { OkOverride("ov-spd", entrypoint: true) },
                    },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow
                        {
                            Id = "sat-spd",
                            Kind = PriorityRowKind.Satellite,
                            ChildRef = new ChildRef { Field = "1", OverrideId = "ov-spd" },
                        },
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                },
            };
            DisplayConfigV2Validator.Normalize(cfg, warns.Add, Pbme());
            var sat = cfg.Priority!.Rows![0];
            Assert.False(sat.DegradedAtLoad);
            Assert.True(FieldLadderMap.TryFindOverride(
                cfg, Pbme(), 1, "ov-spd", out var ov));
            Assert.NotNull(ov);
            Assert.True(ov!.ActsAsEntrypoint);
        }

        [Fact]
        public void FieldLadderMap_PreservesDocumentInsertionOrder()
        {
            var catalog = Pbme();
            var cfg = new DisplayConfigV2
            {
                SharedFields = new Dictionary<string, FieldEntry>
                {
                    ["gear"] = new FieldEntry { Base = new FieldBase { Format = FieldFormats.Neutral } },
                    ["speed"] = new FieldEntry { Base = new FieldBase { Format = FieldFormats.Whole } },
                },
                Fields = new Dictionary<ushort, FieldEntry>
                {
                    [5] = new FieldEntry { Base = new FieldBase { Format = FieldFormats.WithTotal } },
                    [505] = new FieldEntry { Base = new FieldBase { Format = FieldFormats.WithTotal } },
                },
            };
            DisplayConfigV2Validator.Normalize(cfg, _ => { }, catalog);
            var map = FieldLadderMap.Build(cfg, catalog);
            Assert.Equal(new ushort[] { 4, 1, 5, 505 }, map.Select(kv => kv.Key).ToArray());
        }

        [Fact]
        public void FieldEnvelope_PbmeGear_LockedAndAnnouncedFormats()
        {
            var pbme = Pbme();
            Assert.True(FieldEnvelope.IsLocked(pbme, ItmParam.Gear));
            Assert.False(FieldEnvelope.IsLocked(pbme, ItmParam.Speed));
            var gear = FieldEnvelope.OfferedFormats(pbme, ItmParam.Gear);
            Assert.Equal(new[] { FieldFormats.Neutral, FieldFormats.Blank }, gear.ToArray());
            // No-catalog fallback = family tables.
            Assert.Equal(
                FieldFormats.AllowedFor(ItmParam.Gear).ToArray(),
                FieldEnvelope.OfferedFormats(null, ItmParam.Gear).ToArray());
            Assert.False(FieldEnvelope.IsLocked(null, ItmParam.Gear));
        }

        // ── One ladder ───────────────────────────────────────────────────

        [Fact]
        public void OneLadder_SharedWins_InertSideExcludedFromMap()
        {
            var catalog = Pbme();
            var cfg = new DisplayConfigV2
            {
                Fields = new Dictionary<ushort, FieldEntry>
                {
                    [1] = new FieldEntry
                    {
                        Base = new FieldBase { Format = "from-fields" },
                    },
                },
                SharedFields = new Dictionary<string, FieldEntry>
                {
                    ["speed"] = new FieldEntry
                    {
                        Base = new FieldBase { Format = FieldFormats.Whole },
                    },
                },
            };
            DisplayConfigV2Validator.Normalize(cfg, _ => { }, catalog);

            var map = FieldLadderMap.Build(cfg, catalog);
            Assert.Single(map);
            Assert.Equal(1, map[0].Key);
            Assert.Equal(FieldFormats.Whole, map[0].Value.Base?.Format);
        }

        [Fact]
        public void OneLadder_FrameComposer_UsesSharedLadder()
        {
            var catalog = Pbme();
            var caps = FieldCapability.FromCatalog(catalog);
            var cfg = new DisplayConfigV2
            {
                SharedFields = new Dictionary<string, FieldEntry>
                {
                    ["speed"] = new FieldEntry
                    {
                        Base = new FieldBase { Format = FieldFormats.Whole },
                    },
                },
            };
            DisplayConfigV2Validator.Normalize(cfg, _ => { }, catalog);

            var composer = new FrameComposer(cfg, new FrameComposerOptions
            {
                Capabilities = caps,
                PrimaryHostByParam = FieldCapability.PrimaryHostMapFromCapabilities(caps),
                Catalog = catalog,
            });
            var r = composer.Tick(new FrameComposerTickInput
            {
                NowMs = 0,
                SegmentHostedPageId = null,
                DisplayedDestinationId = DestinationIds.Itm("lapInfo"),
                Content = new SegmentContentContext(),
            });
            // Speed ladder is present (one plan for param 1).
            Assert.Contains(r.FieldPlans, p => p.ParamId == 1);
        }

        [Fact]
        public void Reach_DerivedFromPlacements_SpeedOnAllPbmePages()
        {
            var catalog = Pbme();
            Assert.True(CatalogFields.TryGetReach(catalog, "speed", out int placed, out int total));
            Assert.Equal(5, placed);
            Assert.Equal(5, total);
            Assert.Equal(
                "Shared · appears on every ITM page",
                DisplayCopy.ReachLine(placed, total));
        }

        // ── FieldFormats families ────────────────────────────────────────

        [Fact]
        public void FieldFormats_GearAndSpeed_FamiliesPresent()
        {
            var gear = FieldFormats.AllowedFor(ItmParam.Gear);
            Assert.Equal(new[] { FieldFormats.Neutral, FieldFormats.Blank }, gear.ToArray());

            var speed = FieldFormats.AllowedFor(ItmParam.Speed);
            Assert.Equal(new[] { FieldFormats.Whole, FieldFormats.OneDecimal }, speed.ToArray());

            Assert.True(FieldFormats.IsAllowed(ItmParam.Gear, FieldFormats.Neutral));
            Assert.True(FieldFormats.IsAllowed(ItmParam.Speed, FieldFormats.Whole));
            Assert.Equal(FieldFormats.Neutral,
                FieldFormats.EffectiveFormat(ItmParam.Gear, null, false, true, true));
            Assert.Equal(FieldFormats.Whole,
                FieldFormats.EffectiveFormat(ItmParam.Speed, null, false, true, true));
        }

        /// <summary>
        /// Standing law (amendment §3a, owner 2026-07-28): "There are no arbitrary
        /// exclusions. Users get exactly what the hardware supports" — capability facts
        /// live in catalog/envelope DATA, never as per-field code law.
        /// IsOverrideExcluded is deleted; gear offers exactly what FieldFormats +
        /// the envelope announce (format family present; overridable comes from catalog).
        /// </summary>
        [Fact]
        public void ExclusionDeletion_GearOffersEnvelopeFormats_NotCodeLock()
        {
            // Format family is real (8c blank-vs-neutral) — not an empty AllowedFor.
            Assert.NotEmpty(FieldFormats.AllowedFor(ItmParam.Gear));
            Assert.NotEmpty(FieldFormats.AllowedFor(ItmParam.Speed));

            // No IsOverrideExcluded member remains on FieldFormats.
            Assert.Null(typeof(FieldFormats).GetMethod("IsOverrideExcluded"));

            // Envelope DATA still encodes overridable:false on PBME gear (catalog lock).
            var caps = FieldCapability.FromCatalog(Pbme());
            Assert.Equal(false, caps[ItmParam.Gear].Overridable);
            // Speed is not code-locked — overridable absent/true in catalog.
            Assert.NotEqual(false, caps[ItmParam.Speed].Overridable);
        }

        [Fact]
        public void AnnouncedFormats_SeedsIncludeGearAndSpeed()
        {
            var pbme = Pbme();
            Assert.True(pbme.AnnouncedFormats!.ByParam!.ContainsKey("1"));
            Assert.True(pbme.AnnouncedFormats.ByParam.ContainsKey("4"));
            Assert.Contains(FieldFormats.Whole, pbme.AnnouncedFormats.ByParam["1"]);
            Assert.Contains(FieldFormats.Neutral, pbme.AnnouncedFormats.ByParam["4"]);
        }
    }
}
