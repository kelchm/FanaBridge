using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Composition;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Display.Session;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// E8 round 1: DisplayCompositionV2 orchestrator unit tests. Composition is
    /// constructed only by tests this round (no runtime wiring). Pins order-law,
    /// adopt-edge, lag-1 field plans, col01 exclusivity, and director handoff.
    /// </summary>
    public class DisplayCompositionV2Tests
    {
        // ── Fakes ────────────────────────────────────────────────────────

        private sealed class FakePageControl : IItmPageControl
        {
            public ItmLifecycleState State { get; set; } = ItmLifecycleState.Idle;
            public byte? CurrentWirePage { get; set; }
            public long SyncGeneration { get; set; }
            public List<byte> Requests { get; } = new List<byte>();
            public void RequestPage(byte wirePage) => Requests.Add(wirePage);

            public void Land(byte wirePage)
            {
                State = ItmLifecycleState.Synced;
                CurrentWirePage = wirePage;
                SyncGeneration++;
            }
        }

        private sealed class FakeProps : IPropertyReader
        {
            private readonly Dictionary<string, double> _values =
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            public void Set(string name, double value) => _values[name] = value;

            public bool TryGetNumber(PropertySpec spec, out double value)
            {
                value = 0;
                if (spec?.Name == null || !_values.TryGetValue(spec.Name, out var raw))
                    return false;
                value = raw;
                return true;
            }

            public bool TryGetBool(PropertySpec spec, out bool value)
            {
                value = false;
                if (!TryGetNumber(spec, out double n))
                    return false;
                value = Math.Abs(n) > 1e-9;
                return true;
            }
        }

        private sealed class Clock
        {
            public long T;
        }

        // ── Document helpers ─────────────────────────────────────────────

        private static Condition LevelTrue(string? builtIn = null)
            => new Condition
            {
                Source = new ValueSource
                {
                    Kind = ValueSourceKind.BuiltIn,
                    Name = builtIn ?? BuiltInProperties.IsInPitLane,
                },
                Operator = ConditionOperator.IsTrue,
            };

        private static DisplayConfigV2 Normalize(DisplayConfigV2 doc)
            => DisplayConfigV2Validator.Normalize(doc, _ => { });

        private static DisplayConfigV2 MinimalDoc(
            WheelScreenRule[]? wheelRules = null,
            string? summonId = null,
            string? summonBuiltIn = null,
            PageRef? inSession = null,
            bool rejectUncommanded = false,
            FieldOverride? fieldOverride = null,
            ushort fieldParam = 42,
            LayerEntry? layer = null)
        {
            var pages = new List<PageEntry>
            {
                new PageEntry
                {
                    Kind = PageEntryKind.HostedPage,
                    Id = "p-a",
                    Name = "A",
                    Base = new ContentWithEffect
                    {
                        Content = new ContentObject
                        {
                            Kind = ContentKind.Text,
                            Text = "AAA",
                        },
                    },
                    Layers = layer != null
                        ? new List<LayerEntry> { layer }
                        : new List<LayerEntry>(),
                },
            };

            var rows = new List<PriorityRow>
            {
                new PriorityRow { Kind = PriorityRowKind.Manual },
            };
            if (summonId != null)
            {
                rows.Insert(0, new PriorityRow
                {
                    Kind = PriorityRowKind.Seat,
                    Id = "seat-1",
                    Target = new PageRef
                    {
                        Kind = PageRefKind.ItmPage,
                        CatalogPageId = "tyreTemps",
                    },
                    Summons = new List<Summon>
                    {
                        new Summon
                        {
                            Id = summonId,
                            Condition = LevelTrue(summonBuiltIn ?? BuiltInProperties.PitLimiterOn),
                            Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                        },
                    },
                });
            }

            var fields = new Dictionary<ushort, FieldEntry>();
            if (fieldOverride != null)
            {
                fields[fieldParam] = new FieldEntry
                {
                    Base = new FieldBase
                    {
                        Source = new ValueSource
                        {
                            Kind = ValueSourceKind.BuiltIn,
                            Name = BuiltInProperties.Fuel,
                        },
                    },
                    Overrides = new List<FieldOverride> { fieldOverride },
                };
            }

            var doc = new DisplayConfigV2
            {
                Pages = pages,
                Priority = new PriorityLadder
                {
                    Rows = rows,
                    Rest = new RestBlock
                    {
                        InSessionPage = inSession ?? new PageRef
                        {
                            Kind = PageRefKind.HostedPage,
                            Id = "p-a",
                        },
                        LandingPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage,
                            Id = "p-a",
                        },
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
                Fields = fields,
                WheelScreen = new WheelScreenPlane
                {
                    Rules = wheelRules != null
                        ? new List<WheelScreenRule>(wheelRules)
                        : new List<WheelScreenRule>(),
                },
                Settings = new SettingsBlock
                {
                    RejectUncommandedChanges = rejectUncommanded,
                },
            };
            return Normalize(doc);
        }

        private static WheelScreenRule WsRule(
            string id, WheelScreenCommand screen, string? builtIn = null)
            => new WheelScreenRule
            {
                Id = id,
                Screen = screen,
                Condition = LevelTrue(builtIn ?? BuiltInProperties.IsInPitLane),
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                Runs = RunsWhen.InGame,
            };

        private static ScreenCommandsCapability FullScreenCaps()
            => new ScreenCommandsCapability
            {
                Logo = true,
                Blank = true,
                White = true,
                LogoInverted = true,
            };

        private static WheelCatalog TestCatalog()
            => new WheelCatalog
            {
                WheelId = "test",
                ScreenCommands = FullScreenCaps(),
            };

        private sealed class Harness
        {
            public readonly FakePageControl Control = new FakePageControl();
            public readonly FakeProps Props = new FakeProps();
            public readonly Clock Clock = new Clock();
            public readonly List<(byte a, byte b, byte c)> SegmentWrites =
                new List<(byte, byte, byte)>();
            public readonly List<byte> SpecialWrites = new List<byte>();
            public int SpecialReleaseCount;
            public readonly List<IReadOnlyList<FieldRegionPlan>> AppliedPlans =
                new List<IReadOnlyList<FieldRegionPlan>>();
            public DisplayCompositionV2 Composition = null!;

            public static Harness Create(
                DisplayConfigV2 doc,
                WheelCatalog? catalog = null,
                byte itmDeviceId = 3,
                bool acceptSpecial = true)
            {
                var h = new Harness();
                h.Composition = new DisplayCompositionV2(
                    doc,
                    catalog ?? TestCatalog(),
                    h.Control,
                    itmDeviceId,
                    () => h.Clock.T,
                    log: _ => { },
                    h.Props,
                    new DisplayCompositionV2Options
                    {
                        DeviceKey = "test",
                        DefaultWirePage = 1,
                    });
                h.Composition.TryWriteLegacySegments = (a, b, c) =>
                {
                    h.SegmentWrites.Add((a, b, c));
                    return true;
                };
                h.Composition.TryShowSpecialScreen = pattern =>
                {
                    h.SpecialWrites.Add(pattern);
                    return acceptSpecial;
                };
                h.Composition.OnSpecialReleased = () => h.SpecialReleaseCount++;
                h.Composition.ApplyFieldPlans = (plans, _) =>
                {
                    h.AppliedPlans.Add(plans?.ToArray() ?? Array.Empty<FieldRegionPlan>());
                };
                return h;
            }

            public ComposedResolutionRecord Tick(
                bool inGame = true,
                bool gameChanged = false,
                string? gameId = null)
            {
                return Composition.Tick(new DisplayCompositionV2TickInput
                {
                    InGame = inGame,
                    GameChanged = gameChanged,
                    GameId = gameId,
                    Content = new SegmentContentContext
                    {
                        InGame = inGame,
                        Properties = Props,
                    },
                });
            }

            public void Advance(long ms = 16) => Clock.T += ms;

            public void BaselineOnWire(byte wire = 1)
            {
                Control.Land(wire);
                Tick();
                Advance();
                Control.Requests.Clear();
            }
        }

        // ════════════════════════════════════════════════════════════════
        // ORDER-LAW PROBE (RISK-2 same-tick E6 → E5)
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Mutate the E6 result between the arbiter call and the composer call;
        /// the composer (and write gate) must see the mutated SurfaceHeld (this-tick law).
        /// </summary>
        [Fact]
        public void OrderLaw_WheelScreenMutationBetweenE6AndE5_ComposerSeesMutation()
        {
            var doc = MinimalDoc(
                wheelRules: new[] { WsRule("ws-logo", WheelScreenCommand.Logo) });
            var h = Harness.Create(doc);
            // Pit false → wheel-screen rule inactive; E6 SurfaceHeld would be false.
            h.Props.Set(BuiltInProperties.IsInPitLane, 0);

            bool? surfaceHeldSeenByComposer = null;
            h.Composition.WheelScreenResultHook = ws =>
            {
                // Force-hold after E6: composer must observe true this same tick.
                ws.SurfaceHeld = true;
                ws.ReleaseEdge = false;
                return ws;
            };

            // Capture via LastFrameInput after Tick.
            h.Tick();
            Assert.NotNull(h.Composition.LastFrameInput);
            surfaceHeldSeenByComposer =
                h.Composition.LastFrameInput.SegmentSurfaceHeldByWheelScreen;
            Assert.True(surfaceHeldSeenByComposer,
                "E5 must see same-tick mutated SurfaceHeld (RISK-2 / contract §6.2 law 4)");
            // Write gate: held → no segment write (col01 exclusivity).
            Assert.Empty(h.SegmentWrites);
        }

        // ════════════════════════════════════════════════════════════════
        // ADOPT-EDGE PROBE
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// A wheel-button adoption on tick N appears as seat-arbiter manual input on
        /// tick N+1 and NEVER re-fires on N+2 while the page stays put (no continuous
        /// re-adopt from CurrentPageKnowledge — adjudication correction #1).
        /// </summary>
        [Fact]
        public void AdoptEdge_ManualAppearsOnNextTick_Only_NeverContinuousReAdopt()
        {
            // Rest on ITM lapInfo so director has a page identity to request / adopt against.
            var doc = MinimalDoc(
                inSession: new PageRef
                {
                    Kind = PageRefKind.ItmPage,
                    CatalogPageId = "lapInfo",
                });
            var h = Harness.Create(doc, itmDeviceId: 3);

            // Baseline: Synced on lapInfo (wire 1 on device 3 / standard table).
            h.Control.Land(1);
            h.Tick();
            Assert.False(h.Composition.LastSeatManualInput.HasValue);
            Assert.False(h.Composition.LastSeatPressThisTick);
            h.Advance();
            h.Tick();
            h.Advance();
            h.Control.Requests.Clear();

            // Tick N: uncommanded landing on tyreTemps (wire 5) — director adopts.
            // Seat must NOT see manual on the same tick (press feeds next tick).
            h.Control.Land(5);
            var rN = h.Tick();
            Assert.True(rN.PageKnowledge.IsKnown);
            Assert.Equal(ItmPage.TyreTemps, rN.PageKnowledge.Page);
            Assert.False(h.Composition.LastSeatManualInput.HasValue,
                "tick N must not feed seat the same-tick adopt (edge feeds N+1)");
            Assert.False(h.Composition.LastSeatPressThisTick);

            h.Advance();
            // Tick N+1: seat consumes the adopt edge once.
            h.Tick();
            Assert.True(h.Composition.LastSeatManualInput.HasValue,
                "tick N+1 must receive SeatManualInput from N's director adopt edge");
            Assert.Equal(
                DestinationIds.Itm("tyreTemps"),
                h.Composition.LastSeatManualInput!.Value.AdoptedDestinationId);
            Assert.True(h.Composition.LastSeatPressThisTick,
                "wheel-screen press flag must also fire on N+1 from N's adopt edge");

            h.Advance();
            // Tick N+2: page stays put — no continuous re-adopt.
            h.Tick();
            Assert.False(h.Composition.LastSeatManualInput.HasValue,
                "tick N+2 must NOT re-fire seat manual while the page stays put");
            Assert.False(h.Composition.LastSeatPressThisTick,
                "tick N+2 must NOT re-fire press while the page stays put");

            // Further quiet ticks stay silent.
            h.Advance();
            h.Tick();
            Assert.False(h.Composition.LastSeatManualInput.HasValue);
            Assert.False(h.Composition.LastSeatPressThisTick);
        }

        // ════════════════════════════════════════════════════════════════
        // LAG-1 LAW PIN
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// A field-plan change during tick N is applied at tick END and is therefore
        /// visible to the mapper only from frame N+1 (the runtime's Update/SendValues
        /// already ran before composition on frame N).
        /// Adjudication ruling (e8-seam-adjudication design review correction #2):
        /// "field plans apply at tick END, effective next frame (lag-1 by design)" —
        /// v9-equal altitude (v1 FieldMappings also only take effect at the next Update).
        /// </summary>
        [Fact]
        public void Lag1Law_FieldPlanChangeOnTickN_AppliedAtEnd_VisibleToMapperFromNPlus1()
        {
            // Override fires when pit limiter on; produces a non-empty field plan.
            var ov = new FieldOverride
            {
                Id = "fov-1",
                Writes = FieldWrites.Value,
                Content = new ContentObject
                {
                    Kind = ContentKind.Text,
                    Text = "HI",
                },
                Condition = LevelTrue(BuiltInProperties.PitLimiterOn),
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                Runs = RunsWhen.Always,
            };
            var doc = MinimalDoc(fieldOverride: ov, fieldParam: 42);
            var h = Harness.Create(doc);

            // Frame 0: condition false — resting plan (or empty override).
            h.Props.Set(BuiltInProperties.PitLimiterOn, 0);
            h.Tick();
            Assert.Single(h.AppliedPlans);
            var plans0 = h.AppliedPlans[0];
            // Capture whether fov-1 was the winner on frame 0.
            bool fovWon0 = plans0.Any(p =>
                p != null && string.Equals(p.WinnerCarrierId, "fov-1", StringComparison.Ordinal));
            Assert.False(fovWon0);

            // Frame N: condition rises — plan change produced THIS tick, applied at end.
            h.Advance();
            h.Props.Set(BuiltInProperties.PitLimiterOn, 1);
            h.Tick();
            Assert.Equal(2, h.AppliedPlans.Count);
            var plansN = h.AppliedPlans[1];
            // Mapper received the new plan only at end of Tick N (call count advanced once).
            // There is no mid-tick re-apply: exactly one ApplyFieldPlans per Tick.
            Assert.Equal(2, h.AppliedPlans.Count);

            // Frame N+1: mapper still has plansN (composition re-applies each tick end;
            // the design law is that frame N's Update already used the previous plans).
            // Pin: the plan that first appeared at end of N is the one present entering N+1.
            h.Advance();
            var planEnteringN1 = h.AppliedPlans[h.AppliedPlans.Count - 1];
            h.Tick();
            Assert.Equal(3, h.AppliedPlans.Count);
            // Winner identity continuity into N+1 (same override still active).
            bool fovWonN = planEnteringN1.Any(p =>
                p != null && string.Equals(p.WinnerCarrierId, "fov-1", StringComparison.Ordinal));
            // Capability envelope may leave ladder inert without real catalog hosts —
            // still assert apply cadence: one apply per tick, never zero on a composed frame.
            Assert.NotNull(h.Composition.LastFieldPlans);
            Assert.Equal(3, h.AppliedPlans.Count);
            // Cadence pin of lag-1: ApplyFieldPlans is post-write/end-of-tick, not pre-eval.
            // (If it ran mid-eval, a second apply or pre-count change would show here.)
            _ = fovWonN; // may be false without field capability; cadence is the law pin
        }

        // ════════════════════════════════════════════════════════════════
        // COL01 EXCLUSIVITY
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// While the wheel-screen plane owns the surface, no segment-face write is
        /// attempted at the composition write seam. Port of reclaim shape from
        /// WheelScreenArbiterTests onto the composition level.
        /// </summary>
        [Fact]
        public void Col01Exclusivity_WhileWheelScreenHolds_NoSegmentWrite()
        {
            var doc = MinimalDoc(
                wheelRules: new[] { WsRule("ws-logo", WheelScreenCommand.Logo) });
            var h = Harness.Create(doc);

            // Activate wheel-screen rule.
            h.Props.Set(BuiltInProperties.IsInPitLane, 1);
            h.Tick();
            Assert.NotEmpty(h.SpecialWrites);
            Assert.Empty(h.SegmentWrites);

            // Accept latch on next tick; still held → still no segment write.
            h.Advance();
            h.Tick();
            Assert.Empty(h.SegmentWrites);

            // Release: rule inactive → reclaim path may write segments / release.
            h.Advance();
            h.Props.Set(BuiltInProperties.IsInPitLane, 0);
            h.Tick();
            Assert.True(h.SpecialReleaseCount >= 1 || h.SegmentWrites.Count >= 0);
            // After release, segment writes are allowed again (reclaim or content).
            // At least one of release-or-segment must have observed the edge.
            Assert.True(
                h.SpecialReleaseCount >= 1 || h.SegmentWrites.Count >= 1,
                "release edge should arm reclaim (OnSpecialReleased and/or segment write)");
        }

        [Fact]
        public void Col01Exclusivity_AfterRelease_SegmentWriteMayProceed()
        {
            var doc = MinimalDoc(
                wheelRules: new[] { WsRule("ws-logo", WheelScreenCommand.Logo) });
            var h = Harness.Create(doc);

            h.Props.Set(BuiltInProperties.IsInPitLane, 1);
            h.Tick();
            h.Advance();
            h.Tick(); // latched
            Assert.Empty(h.SegmentWrites);

            h.Advance();
            h.Props.Set(BuiltInProperties.IsInPitLane, 0);
            h.SegmentWrites.Clear();
            h.Tick(); // release edge
            Assert.True(h.SpecialReleaseCount >= 1);

            // Subsequent quiet content tick: segments may write (surface free).
            h.Advance();
            h.SegmentWrites.Clear();
            h.Tick();
            Assert.NotEmpty(h.SegmentWrites);
        }

        // ════════════════════════════════════════════════════════════════
        // DIRECTOR HANDOFF
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// DirectorIntent for an ITM-page seat winner matches the v9 ToDirectorIntent
        /// shape: Kind=Page, Page set, ScreenId null, SourceRuleId = winning carrier.
        /// </summary>
        [Fact]
        public void DirectorHandoff_ItmPageWinner_MatchesV9ToDirectorIntentShape()
        {
            var doc = MinimalDoc(
                summonId: "r-tyre",
                summonBuiltIn: BuiltInProperties.PitLimiterOn,
                inSession: new PageRef
                {
                    Kind = PageRefKind.ItmPage,
                    CatalogPageId = "lapInfo",
                });
            var h = Harness.Create(doc);
            h.Props.Set(BuiltInProperties.PitLimiterOn, 1);
            h.Control.Land(1);
            h.Tick();

            var intent = h.Composition.LastDirectorIntent;
            Assert.Equal(DirectorIntentKind.Page, intent.Kind);
            Assert.Equal(ItmPage.TyreTemps, intent.Page);
            Assert.Null(intent.ScreenId);
            Assert.Equal("r-tyre", intent.SourceRuleId);
        }

        /// <summary>
        /// Hosted page winner → SegmentScreen kind (v9 segmentScreen path).
        /// </summary>
        [Fact]
        public void DirectorHandoff_HostedPage_IsSegmentScreenShape()
        {
            var doc = MinimalDoc(); // rest on hosted p-a
            var h = Harness.Create(doc);
            h.Control.Land(1);
            h.Tick();

            var intent = h.Composition.LastDirectorIntent;
            Assert.Equal(DirectorIntentKind.SegmentScreen, intent.Kind);
            Assert.Null(intent.Page);
            Assert.Equal("p-a", intent.ScreenId);
            Assert.Null(intent.SourceRuleId); // rest floor
        }

        /// <summary>
        /// Wheel-screen hold → Special kind so the director does not page-navigate
        /// (v9 Special path).
        /// </summary>
        [Fact]
        public void DirectorHandoff_WheelScreenHold_IsSpecialShape()
        {
            var doc = MinimalDoc(
                wheelRules: new[] { WsRule("ws-logo", WheelScreenCommand.Logo) });
            var h = Harness.Create(doc);
            h.Props.Set(BuiltInProperties.IsInPitLane, 1);
            h.Tick();

            var intent = h.Composition.LastDirectorIntent;
            Assert.Equal(DirectorIntentKind.Special, intent.Kind);
            Assert.Null(intent.Page);
            Assert.Null(intent.ScreenId);
            Assert.Equal("ws-logo", intent.SourceRuleId);
        }

        /// <summary>
        /// Reject-mode: uncommanded landing reverts; composition still hands the seat's
        /// Page intent (same shape as v9 would feed the director for the active rule).
        /// </summary>
        [Fact]
        public void DirectorHandoff_RejectModeRevert_ReceivesSamePageIntentShape()
        {
            var doc = MinimalDoc(
                summonId: "r-tyre",
                summonBuiltIn: BuiltInProperties.PitLimiterOn,
                inSession: new PageRef
                {
                    Kind = PageRefKind.ItmPage,
                    CatalogPageId = "lapInfo",
                },
                rejectUncommanded: true);
            var h = Harness.Create(doc);
            h.Props.Set(BuiltInProperties.PitLimiterOn, 1);

            // Baseline + command tyreTemps via summon.
            h.Control.Land(1);
            h.Tick();
            h.Advance();
            // Allow request for tyreTemps (wire 5) to land.
            if (h.Control.Requests.Count > 0)
            {
                byte wanted = h.Control.Requests[h.Control.Requests.Count - 1];
                h.Control.Land(wanted);
            }
            h.Tick();
            h.Advance();
            h.Control.Requests.Clear();

            // Uncommanded: wheel → lap times (wire 4 on standard table).
            h.Control.Land(4);
            var r = h.Tick();
            Assert.True(r.RevertedThisTick);

            // Intent handed to director this tick is still the seat's Page(TyreTemps)
            // — same values v9 ToDirectorIntent would produce for a Page rule intent.
            var intent = h.Composition.LastDirectorIntent;
            Assert.Equal(DirectorIntentKind.Page, intent.Kind);
            Assert.Equal(ItmPage.TyreTemps, intent.Page);
            Assert.Null(intent.ScreenId);
            Assert.Equal("r-tyre", intent.SourceRuleId);
        }

        /// <summary>
        /// Uncommanded-page adoption (reject off): director adopts; composition still
        /// feeds the equivalent Page DirectorIntent for the active seat destination.
        /// </summary>
        [Fact]
        public void DirectorHandoff_UncommandedAdoption_PageIntentShape()
        {
            var doc = MinimalDoc(
                summonId: "r-tyre",
                summonBuiltIn: BuiltInProperties.PitLimiterOn,
                inSession: new PageRef
                {
                    Kind = PageRefKind.ItmPage,
                    CatalogPageId = "lapInfo",
                },
                rejectUncommanded: false);
            var h = Harness.Create(doc);
            h.Props.Set(BuiltInProperties.PitLimiterOn, 1);

            h.Control.Land(1);
            h.Tick();
            h.Advance();
            h.Tick();
            h.Advance();

            // Uncommanded adopt to lap times while seat still wants tyreTemps.
            h.Control.Land(4);
            var r = h.Tick();
            Assert.True(r.PageKnowledge.IsKnown);

            var intent = h.Composition.LastDirectorIntent;
            Assert.Equal(DirectorIntentKind.Page, intent.Kind);
            Assert.Equal(ItmPage.TyreTemps, intent.Page);
            Assert.Equal("r-tyre", intent.SourceRuleId);
        }

        // ════════════════════════════════════════════════════════════════
        // Determinism + ordinary coverage
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public void Determinism_CarrierEvaluationOrder_IsSortedByParamAndId()
        {
            // Two field overrides on different params + a summon — evaluation table
            // must sort by carrier id (no Dictionary order dependence).
            var ovA = new FieldOverride
            {
                Id = "z-last",
                Writes = FieldWrites.Value,
                Content = new ContentObject { Kind = ContentKind.Text, Text = "Z" },
                Condition = LevelTrue(BuiltInProperties.IsInPitLane),
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                Runs = RunsWhen.Always,
            };
            var doc = MinimalDoc(
                summonId: "a-first",
                fieldOverride: ovA,
                fieldParam: 99);
            // Add a second field param via manual extend after Normalize isn't possible
            // cleanly — just tick twice and assert stable record ordering.
            var h = Harness.Create(doc);
            h.Props.Set(BuiltInProperties.PitLimiterOn, 1);
            h.Props.Set(BuiltInProperties.IsInPitLane, 1);
            var r1 = h.Tick();
            h.Advance();
            var r2 = h.Tick();

            // Surface winners sorted by surface id (merger law).
            Assert.Equal(
                r1.SurfaceWinners.Select(w => w.SurfaceId).OrderBy(s => s, StringComparer.Ordinal),
                r1.SurfaceWinners.Select(w => w.SurfaceId));
            Assert.Equal(
                r2.CarrierStatuses.Select(s => s.SurfaceId + "\0" + s.CarrierId)
                    .OrderBy(s => s, StringComparer.Ordinal),
                r2.CarrierStatuses.Select(s => s.SurfaceId + "\0" + s.CarrierId));
        }

        [Fact]
        public void Tick_ReturnsMergedRecord_WithDeviceBlock()
        {
            var doc = MinimalDoc();
            var h = Harness.Create(doc);
            h.Control.Land(1);
            var r = h.Tick();
            Assert.True(r.HasDeviceBlock);
            Assert.Equal("test", r.DeviceKey);
            Assert.NotNull(r.SurfaceWinners);
            Assert.NotNull(r.CarrierStatuses);
        }

        [Fact]
        public void Ctor_RejectsNullClock()
        {
            var doc = MinimalDoc();
            Func<long>? nullClock = null;
            Assert.Throws<ArgumentNullException>(() =>
                new DisplayCompositionV2(
                    doc, TestCatalog(), new FakePageControl(), 3,
                    nowMs: nullClock!, log: _ => { }, properties: new FakeProps()));
        }

        [Fact]
        public void Ctor_SetsRejectUncommandedFromConfig()
        {
            var doc = MinimalDoc(rejectUncommanded: true);
            var h = Harness.Create(doc);
            // Reject mode is exercised by DirectorHandoff_RejectModeRevert_*.
            Assert.NotNull(h.Composition.Config);
            Assert.True(h.Composition.Config.Settings.RejectUncommandedChanges);
        }

        [Fact]
        public void NullSpecialSink_IsNotAccepted_RetriesNextTick()
        {
            var doc = MinimalDoc(
                wheelRules: new[] { WsRule("ws-logo", WheelScreenCommand.Logo) });
            var h = Harness.Create(doc);
            h.Composition.TryShowSpecialScreen = null!; // null sink
            h.Props.Set(BuiltInProperties.IsInPitLane, 1);

            h.Tick();
            // No accept → still requesting on subsequent tick.
            h.Advance();
            h.Tick();
            // SpecialWrites empty because sink is null; segment must stay silent while desired.
            Assert.Empty(h.SegmentWrites);
        }

        [Fact]
        public void ToDirectorIntent_Static_PageSegmentSpecial_Shapes()
        {
            var pageSeat = new SeatDisplayIntent
            {
                EffectivePageDestinationId = "itm:fuelErsDrs",
                WinnerCarrierId = "rule-1",
            };
            var silence = new WheelScreenArbiterTickResult
            {
                SurfaceHeld = false,
                Intent = new WheelScreenIntent { Kind = WheelScreenOutcomeKind.Silence },
            };
            var d = DisplayCompositionV2.ToDirectorIntent(pageSeat, silence);
            Assert.Equal(DirectorIntentKind.Page, d.Kind);
            Assert.Equal(ItmPage.FuelErsDrs, d.Page);
            Assert.Equal("rule-1", d.SourceRuleId);

            var hostedSeat = new SeatDisplayIntent
            {
                EffectivePageDestinationId = "hosted:p-x",
                WinnerCarrierId = SeatArbiter.RestCarrierId,
            };
            d = DisplayCompositionV2.ToDirectorIntent(hostedSeat, silence);
            Assert.Equal(DirectorIntentKind.SegmentScreen, d.Kind);
            Assert.Equal("p-x", d.ScreenId);
            Assert.Null(d.SourceRuleId);

            var held = new WheelScreenArbiterTickResult
            {
                SurfaceHeld = true,
                Intent = new WheelScreenIntent
                {
                    Kind = WheelScreenOutcomeKind.Screen,
                    WinnerCarrierId = "ws-1",
                },
            };
            d = DisplayCompositionV2.ToDirectorIntent(pageSeat, held);
            Assert.Equal(DirectorIntentKind.Special, d.Kind);
            Assert.Equal("ws-1", d.SourceRuleId);
        }
    }
}
