using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Tests.Display.TestSupport;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Phase E4: pure SeatArbiter tick-trace tests. Maps to display-model laws,
    /// replan E4 ownership, the evaluated-carrier contract, and panel probe traces.
    /// </summary>
    public class SeatArbiterTests
    {
        // ── Snapshot helpers ─────────────────────────────────────────────

        private static CarrierTickSnapshot Snap(
            string id, bool active, bool fired = false, bool fresh = false,
            bool eligible = true, int? remaining = null,
            bool legacySupersededV9 = false)
            => new CarrierTickSnapshot(
                id, conditionSatisfied: active, active, fresh, fired,
                legacySupersededV9, eligible, expiresAtMs: 0, remaining);

        private static SeatArbiterTickInput In(
            long now, params CarrierTickSnapshot[] snaps)
            => new SeatArbiterTickInput
            {
                NowMs = now,
                InGame = true,
                CarrierSnapshots = snaps,
            };

        private static Condition LevelTrue(string builtIn)
            => new Condition
            {
                Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = builtIn },
                Operator = ConditionOperator.IsTrue,
            };

        private static Summon MakeSummon(string id, string builtIn = null)
            => new Summon
            {
                Id = id,
                Condition = LevelTrue(builtIn ?? BuiltInProperties.PitLimiterOn),
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
            };

        private static DisplayConfigV2 Normalize(DisplayConfigV2 doc)
            => DisplayConfigV2Validator.Normalize(doc, _ => { });

        private static DisplayConfigV2 LoadPersona(string fileName)
        {
            var path = Path.Combine(
                TestPaths.RepoRoot(), "scratch", "plans", "display-customization",
                "examples", fileName);
            var json = File.ReadAllText(path);
            return DisplayConfigV2Serializer.Load(json, _ => { });
        }



        private static DisplayConfigV2 MinimalLadder(
            params (PriorityRowKind kind, string id, string destKind, string destId,
                string[] summons, Lifetime? bringUp)[] rows)
        {
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-a", Name = "A",
                        Layers = new List<LayerEntry>(),
                    },
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-b", Name = "B",
                        Layers = new List<LayerEntry>(),
                    },
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-c", Name = "C",
                        Layers = new List<LayerEntry>(),
                    },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>(),
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-a",
                        },
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
            };

            foreach (var r in rows)
            {
                var row = new PriorityRow
                {
                    Kind = r.kind,
                    Id = r.id,
                    Summons = new List<Summon>(),
                    BringUpLifetime = r.bringUp,
                };
                if (r.kind == PriorityRowKind.Manual)
                {
                    doc.Priority.Rows.Add(row);
                    continue;
                }
                if (r.destKind == "hosted")
                    row.Target = new PageRef { Kind = PageRefKind.HostedPage, Id = r.destId };
                else if (r.destKind == "itm")
                    row.Target = new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = r.destId };
                else if (r.destKind == "cycle")
                    row.Target = new PageRef { Kind = PageRefKind.Cycle, Id = r.destId };

                if (r.summons != null)
                {
                    foreach (var sid in r.summons)
                    {
                        row.Summons.Add(new Summon
                        {
                            Id = sid,
                            Condition = LevelTrue(BuiltInProperties.PitLimiterOn),
                            Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                        });
                    }
                }
                doc.Priority.Rows.Add(row);
            }

            // Ensure a manual row exists for Normalize.
            if (!doc.Priority.Rows.Any(x => x.Kind == PriorityRowKind.Manual))
                doc.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });

            return Normalize(doc);
        }

        private static int CountDisplayOnScreen(SeatArbiterTickResult r)
            => r.Resolution.CarrierStatuses.Count(s =>
                s.SurfaceId == SeatArbiter.DisplaySurfaceId
                && s.Presence == CarrierPresence.OnScreen);

        private static void AssertExactlyOneDisplayOnScreenWhenNonRest(SeatArbiterTickResult r)
        {
            if (r.Intent.WinnerCarrierId == SeatArbiter.RestCarrierId)
                return;
            Assert.Equal(1, CountDisplayOnScreen(r));
            var on = r.Resolution.CarrierStatuses.First(s =>
                s.SurfaceId == SeatArbiter.DisplaySurfaceId
                && s.Presence == CarrierPresence.OnScreen);
            Assert.Equal(r.Intent.WinnerCarrierId, on.CarrierId);
        }

        private static Dictionary<ushort, string> AlexHosts()
            => new Dictionary<ushort, string>
            {
                [42] = "tyreTemps",
                [45] = "tyreTemps",
                [48] = "tyreTemps",
                [51] = "tyreTemps",
                [5] = "fuelErsDrs",
            };

        private static DisplayConfigV2 VisitAggregateDoc()
        {
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-t", Name = "T",
                        Layers = new List<LayerEntry>
                        {
                            new LayerEntry
                            {
                                Id = "c-a", ActsAsEntrypoint = true,
                                Condition = LevelTrue(BuiltInProperties.PitLimiterOn),
                                Lifetime = new Lifetime
                                {
                                    Kind = LifetimeKind.ForDuration, DurationMs = 10000,
                                },
                            },
                            new LayerEntry
                            {
                                Id = "c-b", ActsAsEntrypoint = true,
                                Condition = LevelTrue(BuiltInProperties.IsInPitLane),
                                Lifetime = new Lifetime
                                {
                                    Kind = LifetimeKind.ForDuration, DurationMs = 10000,
                                },
                            },
                        },
                    },
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-rest", Name = "REST",
                    },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Seat, Id = "s-t",
                            Target = new PageRef { Kind = PageRefKind.HostedPage, Id = "p-t" },
                            BringUpLifetime = new Lifetime
                            {
                                Kind = LifetimeKind.ForDuration, DurationMs = 4000,
                            },
                        },
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-rest",
                        },
                    },
                },
            };
            return Normalize(doc);
        }

        // ── Rest / one-winner ────────────────────────────────────────────

        [Fact]
        public void Rest_InSession_WhenNoClaim()
        {
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s-hi", "hosted", "p-b", new[] { "e-hi" }, null)));
            var r = arb.Tick(In(0));
            Assert.Equal(SeatArbiter.RestCarrierId, r.Intent.WinnerCarrierId);
            Assert.Equal(DestinationIds.Hosted("p-a"), r.Intent.DestinationId);
            Assert.False(r.Intent.DestinationChanged);
            Assert.False(r.Manual.HasRememberedTarget);
            Assert.Null(r.Manual.RememberedDestinationId);
        }

        [Fact]
        public void Rest_Idle_EmitsSemanticChoice()
        {
            var doc = MinimalLadder(
                (PriorityRowKind.Seat, "s-hi", "hosted", "p-b", new[] { "e-hi" }, null));
            doc.Priority.Rest.Idle = new IdleSpec
            {
                Kind = IdleKind.Screen,
                Screen = WheelScreenCommand.Logo,
            };
            var arb = new SeatArbiter(Normalize(doc));
            var input = In(0);
            input.InGame = false;
            var r = arb.Tick(input);
            Assert.Equal(DestinationIds.RestIdle, r.Intent.DestinationId);
            Assert.Equal(IdleKind.Screen, r.Intent.IdleKind);
            Assert.Equal(WheelScreenCommand.Logo, r.Intent.IdleScreen);
        }

        [Fact]
        public void Idle_SemanticPublishedOnEveryOutOfSessionTick()
        {
            // E4-15: idle semantic even when an idle-eligible claim owns the plane.
            var doc = MinimalLadder(
                (PriorityRowKind.Seat, "s-hi", "hosted", "p-b", new[] { "e-hi" }, null));
            doc.Priority.Rest.Idle = new IdleSpec
            {
                Kind = IdleKind.Screen,
                Screen = WheelScreenCommand.Logo,
            };
            var arb = new SeatArbiter(Normalize(doc));
            var input = In(0, Snap("e-hi", true, eligible: true));
            input.InGame = false;
            var r = arb.Tick(input);
            // Claim may win display; idle floor semantic still published.
            Assert.Equal(IdleKind.Screen, r.Intent.IdleKind);
            Assert.Equal(WheelScreenCommand.Logo, r.Intent.IdleScreen);
        }

        [Fact]
        public void Idle_ParkOnLegacyForBlank_PublishedFromHelper()
        {
            // E7-007: SeatArbiter asserts ParkOnLegacyForBlank from IdleCompile.
            var doc = MinimalLadder(
                (PriorityRowKind.Seat, "s-hi", "hosted", "p-b", new[] { "e-hi" }, null));
            doc.Priority.Rest.Idle = new IdleSpec { Kind = IdleKind.Blank };
            doc.Priority.Rest.Idle.ParkOnLegacyForBlank = true;
            var arb = new SeatArbiter(Normalize(doc));
            var input = In(0);
            input.InGame = false;
            var r = arb.Tick(input);
            Assert.Equal(IdleKind.Blank, r.Intent.IdleKind);
            Assert.True(r.Intent.ParkOnLegacyForBlank);
        }

        [Fact]
        public void AdoptedUnknownPage_RestWithNoIntent()
        {
            // E7-OPUS-06: uncataloged adopt → ManualRowState.AdoptedUnknownPage, no remembered dest.
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s-hi", "hosted", "p-b", new[] { "e-hi" }, null)));
            // First establish a remembered target.
            var r = arb.Tick(new SeatArbiterTickInput
            {
                NowMs = 0,
                InGame = true,
                Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-b")),
            });
            Assert.True(r.Manual.HasRememberedTarget);

            // Uncataloged adopt clears remembered page (rest-with-no-intent).
            r = arb.Tick(new SeatArbiterTickInput
            {
                NowMs = 1,
                InGame = true,
                Manual = SeatManualInput.NavigateUnknownPage(),
            });
            Assert.True(r.Manual.AdoptedUnknownPage);
            Assert.False(r.Manual.HasRememberedTarget);
            Assert.Null(r.Manual.RememberedDestinationId);
        }

        [Fact]
        public void OneWinner_TopRankBeatsLower()
        {
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s-hi", "hosted", "p-b", new[] { "e-hi" }, null),
                (PriorityRowKind.Seat, "s-lo", "hosted", "p-c", new[] { "e-lo" }, null)));
            var r = arb.Tick(In(0, Snap("e-hi", true), Snap("e-lo", true)));
            Assert.Equal("e-hi", r.Intent.WinnerCarrierId);
            Assert.Equal(DestinationIds.Hosted("p-b"), r.Intent.DestinationId);

            var hi = r.Resolution.CarrierStatuses.First(s => s.CarrierId == "e-hi");
            var lo = r.Resolution.CarrierStatuses.First(s => s.CarrierId == "e-lo");
            Assert.Equal(CarrierPresence.OnScreen, hi.Presence);
            Assert.Equal(CarrierPresence.Outranked, lo.Presence);
            AssertExactlyOneDisplayOnScreenWhenNonRest(r);
        }

        // ── Supersede retirement (deliberate change) ─────────────────────

        [Fact]
        public void SupersedeRetirement_DisplacedUntilDismissed_Resumes()
        {
            // Deliberate-change vs v9: untilDismissed outranked then reclaims — RESUMES.
            // SA-007 adversarial: LegacySupersededV9=true must NOT kill the claim.
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s-hi", "hosted", "p-b", new[] { "e-hi" }, null),
                (PriorityRowKind.Seat, "s-fuel", "hosted", "p-c", new[] { "e-fuel" }, null)));

            var r = arb.Tick(In(0, Snap("e-fuel", true, legacySupersededV9: true)));
            Assert.Equal("e-fuel", r.Intent.WinnerCarrierId);

            r = arb.Tick(In(SeatArbiter.MinDwellMs,
                Snap("e-hi", true),
                Snap("e-fuel", true, legacySupersededV9: true)));
            Assert.Equal("e-hi", r.Intent.WinnerCarrierId);
            var fuelStatus = r.Resolution.CarrierStatuses.First(s => s.CarrierId == "e-fuel");
            Assert.Equal(CarrierPresence.Outranked, fuelStatus.Presence);
            Assert.Equal(CarrierRowLabels.None, fuelStatus.RowLabels & CarrierRowLabels.Dismissed);

            r = arb.Tick(In(SeatArbiter.MinDwellMs * 2,
                Snap("e-fuel", true, legacySupersededV9: true)));
            Assert.Equal("e-fuel", r.Intent.WinnerCarrierId);
            Assert.Equal(DestinationIds.Hosted("p-c"), r.Intent.DestinationId);
        }

        // ── D8 dismissal ─────────────────────────────────────────────────

        [Fact]
        public void Dismiss_LatchesDestinationCarriers_FallsToManual()
        {
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s-pit", "hosted", "p-b", new[] { "e-pit" }, null)));

            // Park on p-c first so dismiss-and-return has a remembered page.
            var park = In(0);
            park.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-c"));
            var r = arb.Tick(park);
            Assert.Equal(DestinationIds.Hosted("p-c"), r.Manual.RememberedDestinationId);

            r = arb.Tick(In(SeatArbiter.MinDwellMs, Snap("e-pit", true)));
            Assert.Equal("e-pit", r.Intent.WinnerCarrierId);

            // Dismissing press is consumed — no adopt/walk; returns to remembered p-c.
            var input = In(SeatArbiter.MinDwellMs + 100, Snap("e-pit", true));
            input.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-a"));
            r = arb.Tick(input);

            Assert.Equal(SeatArbiter.ManualCarrierId, r.Intent.WinnerCarrierId);
            Assert.Equal(DestinationIds.Hosted("p-c"), r.Intent.DestinationId);
            Assert.Equal(DestinationIds.Hosted("p-c"), r.Manual.RememberedDestinationId);
            Assert.True(r.PressConsumedByDismissal);
            Assert.Contains("e-pit", r.DismissedCarrierIds);

            var pit = r.Resolution.CarrierStatuses.First(s => s.CarrierId == "e-pit");
            Assert.Equal(CarrierRowLabels.Dismissed, pit.RowLabels & CarrierRowLabels.Dismissed);
            // REALIGNMENT #1: latched + Active+Eligible → Dismissed (first-class; not Waiting).
            Assert.Equal(CarrierPresence.Dismissed, pit.Presence);

            r = arb.Tick(In(SeatArbiter.MinDwellMs + 200, Snap("e-pit", true)));
            Assert.Equal(SeatArbiter.ManualCarrierId, r.Intent.WinnerCarrierId);
            Assert.Contains("e-pit", r.DismissedCarrierIds);
        }

        [Fact]
        public void Dismiss_RearmOnFreshFire_AllowsResummon()
        {
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s-pit", "hosted", "p-b", new[] { "e-pit" }, null)));

            arb.Tick(In(0, Snap("e-pit", true)));
            var dismiss = In(100, Snap("e-pit", true));
            dismiss.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-a"));
            arb.Tick(dismiss);

            arb.Tick(In(200)); // pit inactive
            var r = arb.Tick(In(100 + SeatArbiter.PreemptFloorMs,
                Snap("e-pit", active: true, fired: true, fresh: true)));
            Assert.Equal("e-pit", r.Intent.WinnerCarrierId);
        }

        [Fact]
        public void Dismissed_IsFirstClassPresence_DisplayPlane()
        {
            // REALIGNMENT #1: display-plane latched Active+Eligible is Presence=Dismissed
            // (+ RowLabels.Dismissed), not Outranked. Nothing above the row won.
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s-pit", "hosted", "p-b", new[] { "e-pit" }, null)));

            arb.Tick(In(0, Snap("e-pit", true)));
            var dismiss = In(100, Snap("e-pit", true));
            dismiss.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-a"));
            var r = arb.Tick(dismiss);

            var pit = r.Resolution.CarrierStatuses.First(s => s.CarrierId == "e-pit");
            Assert.Equal(SeatArbiter.DisplaySurfaceId, pit.SurfaceId);
            Assert.Equal(CarrierPresence.Dismissed, pit.Presence);
            Assert.Equal(CarrierRowLabels.Dismissed, pit.RowLabels & CarrierRowLabels.Dismissed);
            Assert.NotEqual("e-pit", r.Intent.WinnerCarrierId);
            Assert.Equal(1, CountDisplayOnScreen(r));
        }

        [Fact]
        public void MidWindowRefire_DoesNotRearm_D8Letter()
        {
            // RULED (owner, 2026-07-28): D8's letter. A re-fire INSIDE an unexpired
            // window (FiredThisTick && !FreshFire — a window restart) does NOT re-arm
            // a dismissal latch; only a genuine inactive→active edge does. The policy
            // lives in SeatArbiter.ShouldRearmDismissalLatch — if the ruling is ever
            // reversed (re-arm on any fire), THIS fixture changes with it.
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s-pit", "hosted", "p-b", new[] { "e-pit" }, null)));

            arb.Tick(In(0, Snap("e-pit", active: true, fired: true, fresh: true)));
            var dismiss = In(100, Snap("e-pit", true));
            dismiss.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-a"));
            arb.Tick(dismiss);

            // Mid-window re-fire: still active, fired again, NOT a fresh edge.
            var mid = arb.Tick(In(100 + SeatArbiter.PreemptFloorMs,
                Snap("e-pit", active: true, fired: true, fresh: false)));
            Assert.NotEqual("e-pit", mid.Intent.WinnerCarrierId);
            var midPit = mid.Resolution.CarrierStatuses.First(s => s.CarrierId == "e-pit");
            Assert.Equal(CarrierPresence.Dismissed, midPit.Presence);
            Assert.Equal(CarrierRowLabels.Dismissed, midPit.RowLabels & CarrierRowLabels.Dismissed);

            // The law's other half: a genuine inactive→active edge re-summons.
            arb.Tick(In(400)); // pit inactive — activation truly ends
            var fresh = arb.Tick(In(400 + SeatArbiter.PreemptFloorMs,
                Snap("e-pit", active: true, fired: true, fresh: true)));
            Assert.Equal("e-pit", fresh.Intent.WinnerCarrierId);
        }

        // ── D9 same-destination handoff ──────────────────────────────────

        [Fact]
        public void D9_SameDestinationHandoff_NoPageChangeIntent()
        {
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-fuel", Name = "FUEL" },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Satellite, Id = "s-sat",
                            Target = new PageRef { Kind = PageRefKind.HostedPage, Id = "p-fuel" },
                            Summons = new List<Summon>
                            {
                                MakeSummon("e-sat", BuiltInProperties.PitLimiterOn),
                            },
                        },
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Seat, Id = "s-home",
                            Target = new PageRef { Kind = PageRefKind.HostedPage, Id = "p-fuel" },
                            Summons = new List<Summon>
                            {
                                MakeSummon("e-home", BuiltInProperties.IsInPitLane),
                            },
                        },
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef { Kind = PageRefKind.HostedPage, Id = "p-fuel" },
                    },
                },
            };
            var arb = new SeatArbiter(Normalize(doc));

            var r = arb.Tick(In(0, Snap("e-home", true)));
            Assert.Equal(DestinationIds.Hosted("p-fuel"), r.Intent.DestinationId);
            Assert.Equal("e-home", r.Intent.WinnerCarrierId);

            // Satellite (higher rank) takes over same destination — D9, no dwell wait.
            r = arb.Tick(In(1, Snap("e-sat", true), Snap("e-home", true)));
            Assert.Equal("e-sat", r.Intent.WinnerCarrierId);
            Assert.Equal(DestinationIds.Hosted("p-fuel"), r.Intent.DestinationId);
            Assert.False(r.Intent.DestinationChanged);
            Assert.False(r.Intent.DwellHeld);
        }

        [Fact]
        public void D9_SubFloorHandoff_ZeroRepaintAndPageCounters()
        {
            // SA-007: sub-floor D9 handoff with observable repaint/page counters.
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s", "hosted", "p-b", new[] { "e1", "e2" }, null)));

            // Land directly on e1 as first selection (no rest→page transition noise).
            var r = arb.Tick(In(0, Snap("e1", true)));
            Assert.Equal("e1", r.Intent.WinnerCarrierId);
            Assert.False(r.Intent.DestinationChanged); // first tick

            // t=1 (well under MinDwell): same dest e2 — immediate handoff, no page change.
            r = arb.Tick(In(1, Snap("e2", true)));
            Assert.Equal("e2", r.Intent.WinnerCarrierId);
            Assert.False(r.Intent.DwellHeld);
            Assert.False(r.Intent.DestinationChanged);
            Assert.Equal(DestinationIds.Hosted("p-b"), r.Intent.EffectivePageDestinationId);
        }

        // ── Cycle free-run RESUME ────────────────────────────────────────

        [Fact]
        public void Cycle_FreeRun_ResumeKeepsCursor()
        {
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-a", Name = "A" },
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-b", Name = "B" },
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-c", Name = "C" },
                },
                Cycles = new List<CycleEntry>
                {
                    new CycleEntry
                    {
                        Id = "c1", PeriodMs = 1000,
                        Members = new List<PageRef>
                        {
                            new PageRef { Kind = PageRefKind.HostedPage, Id = "p-a" },
                            new PageRef { Kind = PageRefKind.HostedPage, Id = "p-b" },
                            new PageRef { Kind = PageRefKind.HostedPage, Id = "p-c" },
                        },
                    },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Seat, Id = "s-hi",
                            Target = new PageRef { Kind = PageRefKind.HostedPage, Id = "p-b" },
                            Summons = new List<Summon>
                            {
                                MakeSummon("e-hi", BuiltInProperties.PitLimiterOn),
                            },
                        },
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Seat, Id = "s-cycle",
                            Target = new PageRef { Kind = PageRefKind.Cycle, Id = "c1" },
                            Summons = new List<Summon>
                            {
                                MakeSummon("e-cycle", BuiltInProperties.IsInPitLane),
                            },
                        },
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef { Kind = PageRefKind.HostedPage, Id = "p-a" },
                    },
                },
            };
            var arb = new SeatArbiter(Normalize(doc));

            var r = arb.Tick(In(0, Snap("e-cycle", true)));
            Assert.Equal(DestinationIds.Cycle("c1"), r.Intent.DestinationId);
            Assert.Equal(0, r.Intent.CycleCursor);
            Assert.Equal(DestinationIds.Hosted("p-a"), r.Intent.EffectivePageDestinationId);

            r = arb.Tick(In(1500, Snap("e-cycle", true)));
            Assert.Equal(1, r.Intent.CycleCursor);
            Assert.Equal(DestinationIds.Hosted("p-b"), r.Intent.CycleMemberDestinationId);
            Assert.Equal(DestinationIds.Hosted("p-b"), r.Intent.EffectivePageDestinationId);
            // Cycle advance → DestinationChanged from effective page.
            Assert.True(r.Intent.DestinationChanged);

            r = arb.Tick(In(2000, Snap("e-hi", true), Snap("e-cycle", true)));
            Assert.Equal("e-hi", r.Intent.WinnerCarrierId);

            r = arb.Tick(In(4500, Snap("e-cycle", true)));
            Assert.Equal("e-cycle", r.Intent.WinnerCarrierId);
            Assert.Equal(1, r.Intent.CycleCursor);
            Assert.Equal(DestinationIds.Hosted("p-b"), r.Intent.CycleMemberDestinationId);
        }

        [Fact]
        public void Cycle_Advance_EmitsDestinationChangedFromEffectivePage()
        {
            // Opus probe fixture: cycle-advance intent case.
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-a", Name = "A" },
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-b", Name = "B" },
                },
                Cycles = new List<CycleEntry>
                {
                    new CycleEntry
                    {
                        Id = "c-pitbox", PeriodMs = 5000,
                        Members = new List<PageRef>
                        {
                            new PageRef { Kind = PageRefKind.HostedPage, Id = "p-a" },
                            new PageRef { Kind = PageRefKind.HostedPage, Id = "p-b" },
                        },
                    },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Seat, Id = "s-cycle",
                            Target = new PageRef { Kind = PageRefKind.Cycle, Id = "c-pitbox" },
                            Summons = new List<Summon> { MakeSummon("e-inpit") },
                        },
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef { Kind = PageRefKind.HostedPage, Id = "p-a" },
                    },
                },
            };
            var arb = new SeatArbiter(Normalize(doc));

            var r = arb.Tick(In(0, Snap("e-inpit", true)));
            Assert.Equal(DestinationIds.Cycle("c-pitbox"), r.Intent.DestinationId);
            Assert.Equal(DestinationIds.Hosted("p-a"), r.Intent.EffectivePageDestinationId);
            Assert.False(r.Intent.DestinationChanged); // first tick

            r = arb.Tick(In(5000, Snap("e-inpit", true)));
            Assert.Equal(DestinationIds.Hosted("p-b"), r.Intent.EffectivePageDestinationId);
            Assert.True(r.Intent.DestinationChanged);

            r = arb.Tick(In(10000, Snap("e-inpit", true)));
            Assert.Equal(DestinationIds.Hosted("p-a"), r.Intent.EffectivePageDestinationId);
            Assert.True(r.Intent.DestinationChanged);
        }

        // ── Dwell floors ─────────────────────────────────────────────────

        [Fact]
        public void Dwell_BlockedThenAllowed_AtExactMinDwellMs()
        {
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s-a", "hosted", "p-b", new[] { "e-a" }, null),
                (PriorityRowKind.Seat, "s-b", "hosted", "p-c", new[] { "e-b" }, null)));

            var r = arb.Tick(In(0)); // rest first
            r = arb.Tick(In(0, Snap("e-a", true)));
            Assert.Equal("e-a", r.Intent.WinnerCarrierId);
            AssertExactlyOneDisplayOnScreenWhenNonRest(r);

            // t=499: e-b active, lower rank — blocked; dwell-held winner stays OnScreen.
            r = arb.Tick(In(SeatArbiter.MinDwellMs - 1, Snap("e-b", true)));
            Assert.Equal("e-a", r.Intent.WinnerCarrierId);
            Assert.True(r.Intent.DwellHeld);
            AssertExactlyOneDisplayOnScreenWhenNonRest(r);
            var held = r.Resolution.CarrierStatuses.First(s => s.CarrierId == "e-a");
            Assert.Equal(CarrierPresence.OnScreen, held.Presence);

            r = arb.Tick(In(SeatArbiter.MinDwellMs, Snap("e-b", true)));
            Assert.Equal("e-b", r.Intent.WinnerCarrierId);
            Assert.False(r.Intent.DwellHeld);
            AssertExactlyOneDisplayOnScreenWhenNonRest(r);
        }

        [Fact]
        public void Dwell_HigherRank_PreemptsAfterPreemptFloor()
        {
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s-hi", "hosted", "p-b", new[] { "e-hi" }, null),
                (PriorityRowKind.Seat, "s-lo", "hosted", "p-c", new[] { "e-lo" }, null)));

            arb.Tick(In(0)); // rest
            var r = arb.Tick(In(0, Snap("e-lo", true)));
            Assert.Equal("e-lo", r.Intent.WinnerCarrierId);

            r = arb.Tick(In(SeatArbiter.PreemptFloorMs - 1, Snap("e-hi", true), Snap("e-lo", true)));
            Assert.Equal("e-lo", r.Intent.WinnerCarrierId);
            Assert.True(r.Intent.DwellHeld);
            AssertExactlyOneDisplayOnScreenWhenNonRest(r);

            r = arb.Tick(In(SeatArbiter.PreemptFloorMs, Snap("e-hi", true), Snap("e-lo", true)));
            Assert.Equal("e-hi", r.Intent.WinnerCarrierId);
            AssertExactlyOneDisplayOnScreenWhenNonRest(r);
        }

        [Fact]
        public void Dwell_SameRank_RequiresFullMinDwell()
        {
            // Same seat, DIFFERENT destinations would require full dwell; same destination
            // is D9 (immediate). This fixture uses two seats with different destinations
            // at the same "logical" peer level via sequential claims after rest stamp.
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s-a", "hosted", "p-b", new[] { "e1" }, null),
                (PriorityRowKind.Seat, "s-b", "hosted", "p-c", new[] { "e2" }, null)));

            arb.Tick(In(0));
            var r = arb.Tick(In(0, Snap("e1", true)));
            Assert.Equal("e1", r.Intent.WinnerCarrierId);

            // e1 ends, e2 wants different destination — dwell holds.
            r = arb.Tick(In(SeatArbiter.MinDwellMs - 1, Snap("e2", true)));
            Assert.Equal("e1", r.Intent.WinnerCarrierId);
            Assert.True(r.Intent.DwellHeld);
            AssertExactlyOneDisplayOnScreenWhenNonRest(r);

            r = arb.Tick(In(SeatArbiter.MinDwellMs, Snap("e2", true)));
            Assert.Equal("e2", r.Intent.WinnerCarrierId);
            Assert.True(r.Intent.DestinationChanged);
        }

        [Fact]
        public void Dwell_SameDestinationCarrierHandoff_BypassesDwell()
        {
            // Law fix for former Dwell_SameRank inversion: same dest e1→e2 is D9.
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s", "hosted", "p-b", new[] { "e1", "e2" }, null)));

            arb.Tick(In(0));
            var r = arb.Tick(In(0, Snap("e1", true)));
            Assert.Equal("e1", r.Intent.WinnerCarrierId);

            r = arb.Tick(In(SeatArbiter.MinDwellMs - 1, Snap("e2", true)));
            Assert.Equal("e2", r.Intent.WinnerCarrierId);
            Assert.False(r.Intent.DwellHeld);
            Assert.False(r.Intent.DestinationChanged);
            AssertExactlyOneDisplayOnScreenWhenNonRest(r);
        }

        [Fact]
        public void Dwell_HeldWinner_IsOnScreen()
        {
            // Opus probe fixture: dwell-held status case (E4-03).
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s", "hosted", "p-b", new[] { "e1", "e2" }, null),
                (PriorityRowKind.Seat, "s2", "hosted", "p-c", new[] { "e3" }, null)));

            arb.Tick(In(0));
            arb.Tick(In(0, Snap("e1", true)));
            // Different dest e3 under dwell floor.
            var r = arb.Tick(In(SeatArbiter.MinDwellMs - 1, Snap("e3", true)));
            Assert.Equal("e1", r.Intent.WinnerCarrierId);
            Assert.True(r.Intent.DwellHeld);
            var e1 = r.Resolution.CarrierStatuses.First(s => s.CarrierId == "e1");
            Assert.Equal(CarrierPresence.OnScreen, e1.Presence);
            AssertExactlyOneDisplayOnScreenWhenNonRest(r);
        }

        [Fact]
        public void Dwell_ManualBypasses()
        {
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s", "hosted", "p-b", new[] { "e1" }, null)));
            arb.Tick(In(0));
            arb.Tick(In(0, Snap("e1", true)));

            var input = In(50, Snap("e1", true));
            input.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-c"));
            var r = arb.Tick(input);
            Assert.Equal(SeatArbiter.ManualCarrierId, r.Intent.WinnerCarrierId);
            Assert.False(r.Intent.DwellHeld);
            Assert.True(r.PressConsumedByDismissal);
        }

        [Fact]
        public void Dwell_ImmediatePostFirstSelection_Preemption()
        {
            // SA-007: first selection unstamped — higher can take immediately.
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s-hi", "hosted", "p-b", new[] { "e-hi" }, null),
                (PriorityRowKind.Seat, "s-lo", "hosted", "p-c", new[] { "e-lo" }, null)));

            // First selection is e-lo (unstamped).
            var r = arb.Tick(In(0, Snap("e-lo", true)));
            Assert.Equal("e-lo", r.Intent.WinnerCarrierId);

            // Immediate higher preemption (held is huge from unstamped first).
            r = arb.Tick(In(1, Snap("e-hi", true), Snap("e-lo", true)));
            Assert.Equal("e-hi", r.Intent.WinnerCarrierId);
            Assert.False(r.Intent.DwellHeld);
        }

        // ── Manual / GameChanged / walk / returnToRest ───────────────────

        [Fact]
        public void Manual_NeverNavigated_WaitingNullDestination()
        {
            // Opus probe: never-navigated manual case (E4-04 / E4-10).
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s", "hosted", "p-b", new[] { "e1" }, null)));
            var r = arb.Tick(In(0));
            var manual = r.Resolution.CarrierStatuses.First(s => s.CarrierId == SeatArbiter.ManualCarrierId);
            Assert.Equal(CarrierPresence.Waiting, manual.Presence);
            Assert.Null(manual.DestinationId);
            Assert.False(r.Manual.HasRememberedTarget);
            Assert.Null(r.Manual.MsSinceLastPress);
        }

        [Fact]
        public void Manual_LandingDestinationExposed()
        {
            var doc = MinimalLadder();
            doc.Priority.Rest.LandingPage = new PageRef
            {
                Kind = PageRefKind.HostedPage, Id = "p-c",
            };
            var arb = new SeatArbiter(Normalize(doc));
            var r = arb.Tick(In(0));
            Assert.Equal(DestinationIds.Hosted("p-c"), r.Manual.LandingDestinationId);
            Assert.False(r.Manual.HasRememberedTarget);

            var nav = In(10);
            nav.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-b"));
            r = arb.Tick(nav);
            Assert.True(r.Manual.HasRememberedTarget);
            Assert.Equal(DestinationIds.Hosted("p-b"), r.Manual.RememberedDestinationId);
        }

        [Fact]
        public void Manual_ResetsOnGameChanged()
        {
            var arb = new SeatArbiter(MinimalLadder());
            var nav = In(0);
            nav.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-c"));
            var r = arb.Tick(nav);
            Assert.Equal(DestinationIds.Hosted("p-c"), r.Manual.RememberedDestinationId);

            var g = In(100);
            g.GameChanged = true;
            r = arb.Tick(g);
            Assert.Null(r.Manual.RememberedDestinationId);
            Assert.False(r.Manual.HasRememberedTarget);
            Assert.Equal(SeatArbiter.RestCarrierId, r.Intent.WinnerCarrierId);
        }

        [Fact]
        public void Manual_GameChanged_FromDwellStampedParkedState()
        {
            // SA-006: game-change from a dwell-stamped parked manual must apply immediately.
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s", "hosted", "p-b", new[] { "e1" }, null)));

            // Rest first (unstamped), then manual nav stamps dwell.
            arb.Tick(In(0));
            var nav = In(100);
            nav.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-c"));
            var r = arb.Tick(nav);
            Assert.Equal(SeatArbiter.ManualCarrierId, r.Intent.WinnerCarrierId);
            Assert.Equal(DestinationIds.Hosted("p-c"), r.Intent.DestinationId);

            // GameChanged at t=200 — must not leave stale manual until t=600.
            var g = In(200);
            g.GameChanged = true;
            r = arb.Tick(g);
            Assert.Equal(SeatArbiter.RestCarrierId, r.Intent.WinnerCarrierId);
            Assert.Equal(DestinationIds.Hosted("p-a"), r.Intent.DestinationId);
            Assert.Null(r.Manual.RememberedDestinationId);

            // Subsequent claim is dwell-constrained from the GameChanged stamp.
            r = arb.Tick(In(200 + SeatArbiter.PreemptFloorMs - 1, Snap("e1", true)));
            Assert.Equal(SeatArbiter.RestCarrierId, r.Intent.WinnerCarrierId);
            Assert.True(r.Intent.DwellHeld);

            r = arb.Tick(In(200 + SeatArbiter.PreemptFloorMs, Snap("e1", true)));
            Assert.Equal("e1", r.Intent.WinnerCarrierId);
        }

        [Fact]
        public void Manual_WalkStep_ExposesResolvedNext()
        {
            var arb = new SeatArbiter(MinimalLadder());
            var walk = new[]
            {
                DestinationIds.Hosted("p-a"),
                DestinationIds.Hosted("p-b"),
                DestinationIds.Hosted("p-c"),
            };
            var nav = In(0);
            nav.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-a"));
            nav.CompiledWalk = walk;
            arb.Tick(nav);

            var step = In(100);
            step.Manual = SeatManualInput.StepWalk(+1);
            step.CompiledWalk = walk;
            var r = arb.Tick(step);
            Assert.Equal(DestinationIds.Hosted("p-b"), r.WalkStepResolvedDestinationId);
            Assert.Equal(DestinationIds.Hosted("p-b"), r.Intent.DestinationId);
            Assert.False(r.PressConsumedByDismissal);
        }

        [Fact]
        public void Manual_Navigate_RejectsCycleDestination()
        {
            // E4-15 guard.
            var warnings = new List<string>();
            var arb = new SeatArbiter(
                MinimalLadder(
                    (PriorityRowKind.Seat, "s", "cycle", "c1", new[] { "e1" }, null)),
                new SeatArbiterOptions { Warn = warnings.Add });

            // Build a config that has a cycle for the reject path; navigate to cycle.
            var nav = In(0);
            nav.Manual = SeatManualInput.Navigate(DestinationIds.Cycle("c1"));
            var r = arb.Tick(nav);
            Assert.False(r.Manual.HasRememberedTarget);
            Assert.Null(r.Manual.RememberedDestinationId);
            Assert.Contains(warnings, w => w.IndexOf("cycle", StringComparison.OrdinalIgnoreCase) >= 0);

            // Second attempt: warn-once (no duplicate).
            int before = warnings.Count;
            nav = In(10);
            nav.Manual = SeatManualInput.Navigate(DestinationIds.Cycle("c1"));
            arb.Tick(nav);
            Assert.Equal(before, warnings.Count);
        }

        [Fact]
        public void RowsBelowManual_CannotInterruptWhileParked()
        {
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-a", Name = "A" },
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-b", Name = "B" },
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-c", Name = "C" },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Seat, Id = "s-above",
                            Target = new PageRef { Kind = PageRefKind.HostedPage, Id = "p-b" },
                            Summons = new List<Summon>
                            {
                                MakeSummon("e-above", BuiltInProperties.PitLimiterOn),
                            },
                        },
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Seat, Id = "s-below",
                            Target = new PageRef { Kind = PageRefKind.HostedPage, Id = "p-c" },
                            Summons = new List<Summon>
                            {
                                MakeSummon("e-below", BuiltInProperties.IsInPitLane),
                            },
                        },
                    },
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef { Kind = PageRefKind.HostedPage, Id = "p-a" },
                    },
                },
            };
            var arb = new SeatArbiter(Normalize(doc));

            var nav = In(0);
            nav.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-a"));
            arb.Tick(nav);

            var r = arb.Tick(In(100, Snap("e-below", true)));
            Assert.Equal(SeatArbiter.ManualCarrierId, r.Intent.WinnerCarrierId);

            r = arb.Tick(In(SeatArbiter.MinDwellMs + 100, Snap("e-above", true), Snap("e-below", true)));
            Assert.Equal("e-above", r.Intent.WinnerCarrierId);
        }

        [Fact]
        public void ReturnToRestAfterMs_ClearsPark()
        {
            // Law: X ms since LAST PRESS — no pause/restart on interruption.
            var doc = MinimalLadder();
            doc.Priority.Rows.First(r => r.Kind == PriorityRowKind.Manual)
                .ReturnToRestAfterMs = 1000;
            var arb = new SeatArbiter(Normalize(doc));

            var nav = In(0);
            nav.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-c"));
            var r = arb.Tick(nav);
            Assert.True(r.Manual.OwnsDisplay);

            r = arb.Tick(In(999));
            Assert.True(r.Manual.OwnsDisplay);
            Assert.False(r.Manual.ReturnedToRest);

            r = arb.Tick(In(1000));
            Assert.True(r.Manual.ReturnedToRest);
            Assert.Equal(SeatArbiter.RestCarrierId, r.Intent.WinnerCarrierId);
            Assert.Equal(DestinationIds.Hosted("p-a"), r.Intent.DestinationId);
        }

        [Fact]
        public void ReturnToRestAfterMs_SinceLastPress_NotPausedByInterruption()
        {
            // E4-12: interruption does not pause/restart; clock is since last press.
            var doc = MinimalLadder(
                (PriorityRowKind.Seat, "s-hi", "hosted", "p-b", new[] { "e-hi" }, null));
            doc.Priority.Rows.First(r => r.Kind == PriorityRowKind.Manual)
                .ReturnToRestAfterMs = 1000;
            var arb = new SeatArbiter(Normalize(doc));

            var nav = In(0);
            nav.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-c"));
            arb.Tick(nav);

            // Higher owns t=100..t=2000 — clock still runs from press at 0.
            arb.Tick(In(100, Snap("e-hi", true)));
            arb.Tick(In(SeatArbiter.MinDwellMs + 100, Snap("e-hi", true)));

            // At t=1001 higher ends: manual is already expired → does not reclaim.
            var r = arb.Tick(In(1001));
            Assert.NotEqual(SeatArbiter.ManualCarrierId, r.Intent.WinnerCarrierId);
            Assert.True(r.Manual.ReturnedToRest);
        }

        // ── Derived aggregate ────────────────────────────────────────────

        [Fact]
        public void Aggregate_NOfM_ExcludesSplitChild()
        {
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-alerts", Name = "ALERTS",
                        Layers = new List<LayerEntry>
                        {
                            new LayerEntry
                            {
                                Id = "l-pit", ActsAsEntrypoint = true,
                                Condition = LevelTrue(BuiltInProperties.PitLimiterOn),
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                            new LayerEntry
                            {
                                Id = "l-low", ActsAsEntrypoint = true,
                                Condition = LevelTrue(BuiltInProperties.IsInPitLane),
                                Lifetime = new Lifetime
                                {
                                    Kind = LifetimeKind.ForDuration, DurationMs = 5000,
                                },
                            },
                        },
                    },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Seat, Id = "s-alerts",
                            Target = new PageRef
                            {
                                Kind = PageRefKind.HostedPage, Id = "p-alerts",
                            },
                            Summons = new List<Summon>(),
                            BringUpLifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                        },
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Satellite, Id = "s-split",
                            ChildRef = new ChildRef { PageId = "p-alerts", LayerId = "l-low" },
                            Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                        },
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-alerts",
                        },
                    },
                },
            };
            var arb = new SeatArbiter(Normalize(doc));
            var r = arb.Tick(In(0, Snap("l-pit", true), Snap("l-low", true)));

            var agg = r.Aggregates.First(a => a.SeatId == "s-alerts");
            Assert.Equal(1, agg.TotalCount); // l-low excluded (split)
            Assert.Equal(1, agg.ActiveCount);
            Assert.Contains("l-pit", agg.MemberCarrierIds);
            Assert.DoesNotContain("l-low", agg.MemberCarrierIds);

            Assert.Equal(DestinationIds.Hosted("p-alerts"), r.Intent.DestinationId);
            // Split child is a one-member derived at satellite rank.
            Assert.Contains(r.Aggregates, a => a.SeatId == "s-split" && a.TotalCount == 1);
        }

        [Fact]
        public void Aggregate_BringUp_Pin_WhileMarkedChildActive()
        {
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-alerts", Name = "A",
                        Layers = new List<LayerEntry>
                        {
                            new LayerEntry
                            {
                                Id = "l1", ActsAsEntrypoint = true,
                                Condition = LevelTrue(BuiltInProperties.PitLimiterOn),
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                        },
                    },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Seat, Id = "s-a",
                            Target = new PageRef
                            {
                                Kind = PageRefKind.HostedPage, Id = "p-alerts",
                            },
                            BringUpLifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                        },
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-alerts",
                        },
                    },
                },
            };
            var arb = new SeatArbiter(Normalize(doc));

            var r = arb.Tick(In(0, Snap("l1", true)));
            Assert.Equal("bringUp:s-a", r.Intent.WinnerCarrierId);

            r = arb.Tick(In(100)); // child inactive
            Assert.NotEqual("bringUp:s-a", r.Intent.WinnerCarrierId);
        }

        [Fact]
        public void Aggregate_BringUp_Visit_WindowRestartsPerFiredThisTick()
        {
            var arb = new SeatArbiter(VisitAggregateDoc());

            var r = arb.Tick(In(0, Snap("c-a", active: true, fired: true, fresh: true)));
            Assert.Equal("bringUp:s-t", r.Intent.WinnerCarrierId);
            var bring = r.Resolution.CarrierSnapshots.First(s => s.CarrierId == "bringUp:s-t");
            Assert.Equal(4000, bring.RemainingMs);

            r = arb.Tick(In(1500,
                Snap("c-a", active: true),
                Snap("c-b", active: true, fired: true, fresh: true)));
            bring = r.Resolution.CarrierSnapshots.First(s => s.CarrierId == "bringUp:s-t");
            Assert.Equal(4000, bring.RemainingMs);
            Assert.Equal("bringUp:s-t", r.Intent.WinnerCarrierId);

            r = arb.Tick(In(5000, Snap("c-a", true), Snap("c-b", true)));
            Assert.Equal("bringUp:s-t", r.Intent.WinnerCarrierId);

            r = arb.Tick(In(5500, Snap("c-a", true), Snap("c-b", true)));
            Assert.NotEqual("bringUp:s-t", r.Intent.WinnerCarrierId);
        }

        [Fact]
        public void Aggregate_Visit_DismissalHoldsUntilFreshFire()
        {
            // Opus probe E4-01: visit-shaped aggregate is latched on press; page falls.
            var arb = new SeatArbiter(VisitAggregateDoc());

            var r = arb.Tick(In(0, Snap("c-a", active: true, fired: true, fresh: true)));
            Assert.Equal("bringUp:s-t", r.Intent.WinnerCarrierId);
            Assert.Equal(DestinationIds.Hosted("p-t"), r.Intent.DestinationId);

            // t=1000 press with c-a still active → derived + member latched, manual wins.
            var press = In(1000, Snap("c-a", active: true));
            press.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-rest"));
            r = arb.Tick(press);
            Assert.Equal(SeatArbiter.ManualCarrierId, r.Intent.WinnerCarrierId);
            Assert.Equal(DestinationIds.Hosted("p-rest"), r.Intent.DestinationId);
            Assert.True(r.PressConsumedByDismissal);
            Assert.Contains("bringUp:s-t", r.DismissedCarrierIds);
            Assert.Contains("c-a", r.DismissedCarrierIds);

            var ca = r.Resolution.CarrierStatuses.First(s => s.CarrierId == "c-a");
            Assert.Equal(CarrierRowLabels.Dismissed, ca.RowLabels & CarrierRowLabels.Dismissed);
            // Foreign surface (page:p-t): labels only; E5 fills presence (E4-07).
            Assert.Null(ca.Presence);
            Assert.Equal("page:p-t", ca.SurfaceId);
            var bring = r.Resolution.CarrierStatuses.First(s => s.CarrierId == "bringUp:s-t");
            Assert.Equal(CarrierRowLabels.Dismissed, bring.RowLabels & CarrierRowLabels.Dismissed);
            // Display contender latched while still Active → Dismissed (REALIGNMENT #1).
            Assert.Equal(CarrierPresence.Dismissed, bring.Presence);

            // Still held mid-window.
            r = arb.Tick(In(1100, Snap("c-a", true)));
            Assert.Equal(SeatArbiter.ManualCarrierId, r.Intent.WinnerCarrierId);
            Assert.Contains("bringUp:s-t", r.DismissedCarrierIds);

            // Mid-window member re-fire (!FreshFire) stays down (D8 letter via aggregate).
            r = arb.Tick(In(2000,
                Snap("c-a", active: true, fired: true, fresh: false)));
            Assert.Equal(SeatArbiter.ManualCarrierId, r.Intent.WinnerCarrierId);

            // After window expiry derived inactive; still latched until FreshFire.
            r = arb.Tick(In(5100, Snap("c-a", true)));
            Assert.NotEqual("bringUp:s-t", r.Intent.WinnerCarrierId);

            // Fresh fire re-summons after preemption floor from manual stamp.
            arb.Tick(In(5200)); // inactive edge
            r = arb.Tick(In(5200 + SeatArbiter.PreemptFloorMs,
                Snap("c-a", active: true, fired: true, fresh: true)));
            Assert.Equal("bringUp:s-t", r.Intent.WinnerCarrierId);
        }

        [Fact]
        public void Aggregate_Pin_ResummonOnSiblingFreshFire()
        {
            // Opus adversarial (a): pin/whileTrue — latched A, fresh B re-summons.
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-tyres", Name = "TYRES",
                        Layers = new List<LayerEntry>
                        {
                            new LayerEntry
                            {
                                Id = "o-fl", ActsAsEntrypoint = true,
                                Condition = LevelTrue(BuiltInProperties.PitLimiterOn),
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                            new LayerEntry
                            {
                                Id = "o-fr", ActsAsEntrypoint = true,
                                Condition = LevelTrue(BuiltInProperties.IsInPitLane),
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                        },
                    },
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-rest", Name = "REST",
                    },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Seat, Id = "s-tyres",
                            Target = new PageRef
                            {
                                Kind = PageRefKind.HostedPage, Id = "p-tyres",
                            },
                            BringUpLifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                        },
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-rest",
                        },
                    },
                },
            };
            var arb = new SeatArbiter(Normalize(doc));

            arb.Tick(In(0, Snap("o-fl", active: true, fired: true, fresh: true)));
            var press = In(600, Snap("o-fl", true));
            press.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-rest"));
            var r = arb.Tick(press);
            Assert.Equal(SeatArbiter.ManualCarrierId, r.Intent.WinnerCarrierId);
            Assert.Contains("o-fl", r.DismissedCarrierIds);

            // o-fr fresh at t=1200 while o-fl still latched → re-summon.
            r = arb.Tick(In(600 + SeatArbiter.PreemptFloorMs,
                Snap("o-fl", true),
                Snap("o-fr", active: true, fired: true, fresh: true)));
            Assert.Equal("bringUp:s-tyres", r.Intent.WinnerCarrierId);
            Assert.DoesNotContain("bringUp:s-tyres", r.DismissedCarrierIds);
        }

        [Fact]
        public void Aggregate_SplitChild_RearmIndependentOfHomeLatch()
        {
            // Opus adversarial (b): split childRef re-arms while home members latched.
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-alerts", Name = "ALERTS",
                        Layers = new List<LayerEntry>
                        {
                            new LayerEntry
                            {
                                Id = "l-pit", ActsAsEntrypoint = true,
                                Condition = LevelTrue(BuiltInProperties.PitLimiterOn),
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                            new LayerEntry
                            {
                                Id = "l-low", ActsAsEntrypoint = true,
                                Condition = LevelTrue(BuiltInProperties.IsInPitLane),
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                        },
                    },
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-rest", Name = "REST",
                    },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Satellite, Id = "s-split",
                            ChildRef = new ChildRef { PageId = "p-alerts", LayerId = "l-low" },
                            Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                        },
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Seat, Id = "s-alerts",
                            Target = new PageRef
                            {
                                Kind = PageRefKind.HostedPage, Id = "p-alerts",
                            },
                            BringUpLifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                        },
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-rest",
                        },
                    },
                },
            };
            var arb = new SeatArbiter(Normalize(doc));

            // Home aggregate from l-pit.
            var r = arb.Tick(In(0, Snap("l-pit", active: true, fired: true, fresh: true)));
            Assert.Equal("bringUp:s-alerts", r.Intent.WinnerCarrierId);

            var press = In(100, Snap("l-pit", true));
            press.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-rest"));
            r = arb.Tick(press);
            Assert.Equal(SeatArbiter.ManualCarrierId, r.Intent.WinnerCarrierId);
            Assert.Contains("l-pit", r.DismissedCarrierIds);

            // Split child fresh while home latched → satellite derived re-summons.
            r = arb.Tick(In(100 + SeatArbiter.PreemptFloorMs,
                Snap("l-pit", true),
                Snap("l-low", active: true, fired: true, fresh: true)));
            Assert.Equal("bringUp:s-split", r.Intent.WinnerCarrierId);
            Assert.Equal(DestinationIds.Hosted("p-alerts"), r.Intent.DestinationId);
        }

        [Fact]
        public void ChildRefSatellite_HonoursOwnVisitLifetime()
        {
            // E4-06 / SA-003: forDuration satellite holds after child inactive.
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-al", Name = "AL",
                        Layers = new List<LayerEntry>
                        {
                            new LayerEntry
                            {
                                Id = "l-low", ActsAsEntrypoint = true,
                                Condition = LevelTrue(BuiltInProperties.PitLimiterOn),
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                        },
                    },
                    new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage, Id = "p-rest", Name = "REST",
                    },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Satellite, Id = "s-sat",
                            ChildRef = new ChildRef { PageId = "p-al", LayerId = "l-low" },
                            Lifetime = new Lifetime
                            {
                                Kind = LifetimeKind.ForDuration, DurationMs = 5000,
                            },
                        },
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-rest",
                        },
                    },
                },
            };
            var arb = new SeatArbiter(Normalize(doc));

            var r = arb.Tick(In(0, Snap("l-low", active: true, fired: true, fresh: true)));
            Assert.Equal("bringUp:s-sat", r.Intent.WinnerCarrierId);
            Assert.Equal(DestinationIds.Hosted("p-al"), r.Intent.DestinationId);

            // Child inactive at t=600 — visit holds until 5000.
            r = arb.Tick(In(600));
            Assert.Equal("bringUp:s-sat", r.Intent.WinnerCarrierId);

            r = arb.Tick(In(5000));
            Assert.NotEqual("bringUp:s-sat", r.Intent.WinnerCarrierId);
        }

        // ── Cross-surface latch exposure (E4-11) ─────────────────────────

        [Fact]
        public void TwoSurface_LatchExposure_ActivationUntouched()
        {
            // Suppress-the-summon-only: latch set exposed; layer snap stays Active.
            var doc = LoadPersona("sam-pswbmw.v2.json");
            var arb = new SeatArbiter(doc);

            var r = arb.Tick(In(0, Snap("l-pit", active: true, fired: true, fresh: true)));
            Assert.Equal("bringUp:s-alerts", r.Intent.WinnerCarrierId);

            var press = In(100, Snap("l-pit", true));
            press.Manual = SeatManualInput.StepWalk(+1);
            press.CompiledWalk = new[]
            {
                DestinationIds.Hosted("p-speed"),
                DestinationIds.Hosted("p-fuel"),
                DestinationIds.Hosted("p-temp"),
            };
            // Need a remembered target first.
            // Re-seed: navigate fuel, let pit fire, then step.
            arb = new SeatArbiter(doc);
            var nav = In(0);
            nav.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-fuel"));
            arb.Tick(nav);
            arb.Tick(In(SeatArbiter.MinDwellMs,
                Snap("l-pit", active: true, fired: true, fresh: true)));
            press = In(SeatArbiter.MinDwellMs + 100, Snap("l-pit", true));
            press.Manual = SeatManualInput.StepWalk(+1);
            press.CompiledWalk = new[]
            {
                DestinationIds.Hosted("p-speed"),
                DestinationIds.Hosted("p-fuel"),
                DestinationIds.Hosted("p-temp"),
            };
            r = arb.Tick(press);

            Assert.True(r.PressConsumedByDismissal);
            Assert.Contains("l-pit", r.DismissedCarrierIds);
            // Activation untouched in the snapshot the caller supplied / composed.
            var pitSnap = r.Resolution.CarrierSnapshots.First(s => s.CarrierId == "l-pit");
            Assert.True(pitSnap.Active);
            // Foreign surface row: labels only, presence null.
            var pitStatus = r.Resolution.CarrierStatuses.First(s => s.CarrierId == "l-pit");
            Assert.Equal("page:p-alerts", pitStatus.SurfaceId);
            Assert.Null(pitStatus.Presence);
            Assert.Equal(CarrierRowLabels.Dismissed, pitStatus.RowLabels & CarrierRowLabels.Dismissed);
        }

        // ── Status / foreign surfaces / rest / runs ──────────────────────

        [Fact]
        public void Status_Rest_NeverOutranked()
        {
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s", "hosted", "p-b", new[] { "e1" }, null)));
            var r = arb.Tick(In(0, Snap("e1", true)));
            var rest = r.Resolution.CarrierStatuses.First(s => s.CarrierId == SeatArbiter.RestCarrierId);
            Assert.Equal(CarrierPresence.OffScreen, rest.Presence);

            r = arb.Tick(In(SeatArbiter.MinDwellMs));
            rest = r.Resolution.CarrierStatuses.First(s => s.CarrierId == SeatArbiter.RestCarrierId);
            Assert.Equal(CarrierPresence.OnScreen, rest.Presence);
        }

        [Fact]
        public void Status_RunsGated_WaitingOutOfSessionScope()
        {
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s", "hosted", "p-b", new[] { "e-idle" }, null)));
            var r = arb.Tick(In(0, Snap("e-idle", active: false, eligible: false)));
            var st = r.Resolution.CarrierStatuses.First(s => s.CarrierId == "e-idle");
            Assert.Equal(CarrierPresence.Waiting, st.Presence);
            Assert.Equal(
                CarrierRowLabels.OutOfSessionScope,
                st.RowLabels & CarrierRowLabels.OutOfSessionScope);
        }

        [Fact]
        public void Status_DisabledSummon_EmittedWithOffLabel()
        {
            var doc = MinimalLadder(
                (PriorityRowKind.Seat, "s", "hosted", "p-b", new[] { "e-on" }, null));
            // Inject a disabled summon on the seat.
            var seat = doc.Priority.EffectiveRows.First(r => r.Id == "s");
            seat.Summons.Add(new Summon
            {
                Id = "e-off",
                Enabled = false,
                Condition = LevelTrue(BuiltInProperties.PitLimiterOn),
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
            });
            // Re-normalize? Enabled false already; rebuild arbiter on current doc.
            var arb = new SeatArbiter(doc);
            // Force re-build with disabled via fresh normalize
            var raw = new DisplayConfigV2
            {
                Pages = doc.Pages,
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Seat, Id = "s",
                            Target = new PageRef { Kind = PageRefKind.HostedPage, Id = "p-b" },
                            Summons = new List<Summon>
                            {
                                MakeSummon("e-on"),
                                new Summon
                                {
                                    Id = "e-off",
                                    Enabled = false,
                                    Condition = LevelTrue(BuiltInProperties.PitLimiterOn),
                                    Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                                },
                            },
                        },
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = doc.Priority.Rest,
                },
            };
            arb = new SeatArbiter(Normalize(raw));
            var r = arb.Tick(In(0, Snap("e-on", true)));
            var off = r.Resolution.CarrierStatuses.First(s => s.CarrierId == "e-off");
            Assert.Equal(CarrierPresence.OffScreen, off.Presence);
            Assert.Equal(CarrierRowLabels.Off, off.RowLabels & CarrierRowLabels.Off);
        }

        [Fact]
        public void ForeignSurface_Fn1_NoDisplayPresence_FuelOutranked()
        {
            // E4-07 fixture: o-fn1 carries no display presence; fuel bringUp Outranked
            // when a higher seat owns.
            var doc = LoadPersona("alex-pbme.v2.json");
            var arb = new SeatArbiter(doc, new SeatArbiterOptions { PrimaryHostByParam = AlexHosts() });

            // Proximity owns; fuel child active.
            var r = arb.Tick(In(0,
                Snap("e-proximity", true),
                Snap("o-fn1", true)));
            Assert.Equal("e-proximity", r.Intent.WinnerCarrierId);

            var fuel = r.Resolution.CarrierStatuses.FirstOrDefault(s =>
                s.CarrierId == "bringUp:s-fuel");
            if (fuel.CarrierId != null)
                Assert.Equal(CarrierPresence.Outranked, fuel.Presence);

            var fn1 = r.Resolution.CarrierStatuses.First(s => s.CarrierId == "o-fn1");
            Assert.StartsWith("field:", fn1.SurfaceId);
            Assert.Null(fn1.Presence); // E5 fills
            Assert.NotEqual(SeatArbiter.DisplaySurfaceId, fn1.SurfaceId);
        }

        // ── PrimaryHostByParam degrade ───────────────────────────────────

        [Fact]
        public void PrimaryHostByParam_Missing_DegradeVisible()
        {
            var doc = LoadPersona("alex-pbme.v2.json");
            var warnings = new List<string>();
            // Empty host map — flagged ITM overrides must not vanish silently.
            var arb = new SeatArbiter(doc, new SeatArbiterOptions
            {
                PrimaryHostByParam = new Dictionary<ushort, string>(),
                Warn = warnings.Add,
            });
            var r = arb.Tick(In(0, Snap("o-fl-alert", true), Snap("o-fn1", true)));

            var fl = r.Resolution.CarrierStatuses.First(s => s.CarrierId == "o-fl-alert");
            Assert.Equal("field:42", fl.SurfaceId);
            Assert.Null(fl.Presence);
            Assert.NotEqual(CarrierRowLabels.None, fl.RowLabels & (
                CarrierRowLabels.KeptAsIs | CarrierRowLabels.CantRunHere));

            // Tyre aggregate membership empty / no silent contender.
            var tyre = r.Aggregates.FirstOrDefault(a => a.SeatId == "s-tyres");
            if (tyre != null)
                Assert.Equal(0, tyre.TotalCount);

            Assert.NotEmpty(warnings);
        }

        [Fact]
        public void PrimaryHostByParam_Partial_OnlyMappedJoinAggregate()
        {
            var doc = LoadPersona("alex-pbme.v2.json");
            var partial = new Dictionary<ushort, string>
            {
                [42] = "tyreTemps",
                // 45/48/51 missing
                [5] = "fuelErsDrs",
            };
            var arb = new SeatArbiter(doc, new SeatArbiterOptions { PrimaryHostByParam = partial });
            var r = arb.Tick(In(0, Snap("o-fl-alert", true)));
            var tyre = r.Aggregates.First(a => a.SeatId == "s-tyres");
            Assert.Equal(1, tyre.TotalCount);
            Assert.Contains("o-fl-alert", tyre.MemberCarrierIds);
            Assert.Equal("bringUp:s-tyres", r.Intent.WinnerCarrierId);

            // Unmapped param still has a foreign row.
            Assert.Contains(r.Resolution.CarrierStatuses, s => s.CarrierId == "o-fr-alert");
        }

        [Fact]
        public void PrimaryHostByParam_Complete_FullMembership()
        {
            var doc = LoadPersona("alex-pbme.v2.json");
            var arb = new SeatArbiter(doc, new SeatArbiterOptions { PrimaryHostByParam = AlexHosts() });
            var r = arb.Tick(In(0, Snap("o-fl-alert", true), Snap("o-fr-alert", true)));
            var tyre = r.Aggregates.First(a => a.SeatId == "s-tyres");
            Assert.Equal(4, tyre.TotalCount);
            Assert.Equal(2, tyre.ActiveCount);
        }

        // ── Press + fresh ordering (SA-002) ──────────────────────────────

        [Fact]
        public void PressPlusFresh_RearmsSameTick()
        {
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s-pit", "hosted", "p-b", new[] { "e-pit" }, null)));

            arb.Tick(In(0, Snap("e-pit", true)));
            var dismiss = In(100, Snap("e-pit", true));
            dismiss.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-a"));
            arb.Tick(dismiss);

            // After dismiss, manual is dwell-stamped; wait PreemptFloor then FreshFire re-arms.
            var r = arb.Tick(In(100 + SeatArbiter.PreemptFloorMs,
                Snap("e-pit", active: true, fired: true, fresh: true)));
            Assert.Equal("e-pit", r.Intent.WinnerCarrierId);
        }

        [Fact]
        public void PressPlusFresh_WhileEntrypointOwns_DismissThenRearm()
        {
            // Dismissal-before-evaluation: press latches old activation; FreshFire re-arms.
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s-pit", "hosted", "p-b", new[] { "e-pit" }, null)));

            arb.Tick(In(0, Snap("e-pit", true)));
            var press = In(SeatArbiter.MinDwellMs,
                Snap("e-pit", active: true, fired: true, fresh: true));
            press.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-a"));
            var r = arb.Tick(press);
            // Press latches, then FreshFire re-arms → pit may reclaim after dismiss consumed.
            // Manual bypass dwell; logical winner after re-arm is e-pit (live again).
            Assert.True(r.PressConsumedByDismissal);
            Assert.Equal("e-pit", r.Intent.WinnerCarrierId);
        }

        // ── Sam 5m dismiss-and-return restage ────────────────────────────

        [Fact]
        public void Sam_DismissAndReturn_Restage()
        {
            // Round-7b: BOTH presses are StepWalk (runtime never feeds adopt for next/prev).
            var doc = LoadPersona("sam-pswbmw.v2.json");
            var arb = new SeatArbiter(doc);
            var walk = new[]
            {
                DestinationIds.Hosted("p-speed"),
                DestinationIds.Hosted("p-fuel"),
                DestinationIds.Hosted("p-temp"),
            };

            // 1. Page to Fuel before limiter.
            var t = In(0);
            t.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-fuel"));
            t.CompiledWalk = walk;
            var r = arb.Tick(t);
            Assert.Equal(DestinationIds.Hosted("p-fuel"), r.Intent.DestinationId);
            Assert.Equal(SeatArbiter.ManualCarrierId, r.Intent.WinnerCarrierId);

            // 2. PIT layer fires → aggregate brings up alerts.
            r = arb.Tick(In(SeatArbiter.MinDwellMs,
                Snap("l-pit", active: true, fired: true, fresh: true)));
            Assert.Equal(DestinationIds.Hosted("p-alerts"), r.Intent.DestinationId);
            Assert.Equal("bringUp:s-alerts", r.Intent.WinnerCarrierId);

            // 3. First StepWalk press dismisses PIT → falls to manual Fuel (no walk step).
            var press = In(SeatArbiter.MinDwellMs + 100, Snap("l-pit", active: true));
            press.Manual = SeatManualInput.StepWalk(+1);
            press.CompiledWalk = walk;
            r = arb.Tick(press);
            Assert.True(r.PressConsumedByDismissal);
            Assert.Null(r.WalkStepResolvedDestinationId);
            Assert.Equal(DestinationIds.Hosted("p-fuel"), r.Intent.DestinationId);
            Assert.Equal(SeatArbiter.ManualCarrierId, r.Intent.WinnerCarrierId);
            Assert.Contains("l-pit", r.DismissedCarrierIds);
            Assert.Contains(CarrierRowLabels.Dismissed,
                r.Resolution.CarrierStatuses
                    .Where(s => (s.RowLabels & CarrierRowLabels.Dismissed) != 0)
                    .Select(s => s.RowLabels));

            r = arb.Tick(In(SeatArbiter.MinDwellMs + 200, Snap("l-pit", true)));
            Assert.Equal(SeatArbiter.ManualCarrierId, r.Intent.WinnerCarrierId);

            // 4. Second StepWalk actually steps Fuel → Temp.
            var step = In(SeatArbiter.MinDwellMs + 300);
            step.Manual = SeatManualInput.StepWalk(+1);
            step.CompiledWalk = walk;
            r = arb.Tick(step);
            Assert.False(r.PressConsumedByDismissal);
            Assert.Equal(DestinationIds.Hosted("p-temp"), r.WalkStepResolvedDestinationId);
            Assert.Equal(DestinationIds.Hosted("p-temp"), r.Intent.DestinationId);

            // 5. Fresh limiter fire re-interrupts.
            long tInactive = SeatArbiter.MinDwellMs + 400;
            arb.Tick(In(tInactive));
            long tRefire = tInactive + SeatArbiter.PreemptFloorMs;
            r = arb.Tick(In(tRefire,
                Snap("l-pit", active: true, fired: true, fresh: true)));
            Assert.Equal(DestinationIds.Hosted("p-alerts"), r.Intent.DestinationId);
        }

        [Fact]
        public void DwellVersusDismissal_PressWinsDuringDwell()
        {
            // SA-007: dwell-vs-dismissal — manual press bypasses dwell floor.
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s", "hosted", "p-b", new[] { "e1" }, null)));
            arb.Tick(In(0));
            arb.Tick(In(0, Snap("e1", true)));
            var press = In(10, Snap("e1", true));
            press.Manual = SeatManualInput.Navigate(DestinationIds.Hosted("p-c"));
            var r = arb.Tick(press);
            Assert.Equal(SeatArbiter.ManualCarrierId, r.Intent.WinnerCarrierId);
            Assert.False(r.Intent.DwellHeld);
            Assert.True(r.PressConsumedByDismissal);
        }

        // ── Alex persona tick trace ──────────────────────────────────────

        [Fact]
        public void Alex_Persona_TickTrace_GapsThenFuelThenRest()
        {
            var doc = LoadPersona("alex-pbme.v2.json");
            var arb = new SeatArbiter(doc, new SeatArbiterOptions { PrimaryHostByParam = AlexHosts() });

            var r = arb.Tick(In(0));
            Assert.Equal(DestinationIds.Itm("lapInfo"), r.Intent.DestinationId);

            r = arb.Tick(In(0, Snap("e-proximity", true)));
            Assert.Equal(DestinationIds.Itm("lapTimes"), r.Intent.DestinationId);
            Assert.Equal("e-proximity", r.Intent.WinnerCarrierId);

            r = arb.Tick(In(100, Snap("e-proximity", true), Snap("e-lowfuel", true)));
            Assert.Equal("e-proximity", r.Intent.WinnerCarrierId);
            var fuel = r.Resolution.CarrierStatuses.First(s => s.CarrierId == "e-lowfuel");
            Assert.Equal(CarrierPresence.Outranked, fuel.Presence);

            r = arb.Tick(In(SeatArbiter.MinDwellMs + 100, Snap("e-lowfuel", true)));
            Assert.Equal(DestinationIds.Itm("fuelErsDrs"), r.Intent.DestinationId);

            r = arb.Tick(In(SeatArbiter.MinDwellMs * 2 + 100));
            Assert.Equal(DestinationIds.Itm("lapInfo"), r.Intent.DestinationId);
        }

        [Fact]
        public void Alex_TyreAggregate_PinBringUp()
        {
            var doc = LoadPersona("alex-pbme.v2.json");
            var arb = new SeatArbiter(doc, new SeatArbiterOptions { PrimaryHostByParam = AlexHosts() });

            var r = arb.Tick(In(0,
                Snap("o-fl-alert", true),
                Snap("o-fr-alert", true)));
            var agg = r.Aggregates.First(a => a.SeatId == "s-tyres");
            Assert.Equal(4, agg.TotalCount);
            Assert.Equal(2, agg.ActiveCount);
            Assert.Equal(DestinationIds.Itm("tyreTemps"), r.Intent.DestinationId);
            Assert.Equal("bringUp:s-tyres", r.Intent.WinnerCarrierId);
        }

        // ── Two seats sharing a destination (SA-007) ─────────────────────

        [Fact]
        public void TwoSeats_SharingDestination_D9Handoff()
        {
            // SA-007: two contenders sharing a destination (satellite + home seat —
            // validator degrades duplicate home seats for the same target).
            var doc = new DisplayConfigV2
            {
                Pages = new List<PageEntry>
                {
                    new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-shared", Name = "S" },
                },
                Priority = new PriorityLadder
                {
                    Rows = new List<PriorityRow>
                    {
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Satellite, Id = "s-hi",
                            Target = new PageRef
                            {
                                Kind = PageRefKind.HostedPage, Id = "p-shared",
                            },
                            Summons = new List<Summon> { MakeSummon("e1") },
                        },
                        new PriorityRow
                        {
                            Kind = PriorityRowKind.Seat, Id = "s-home",
                            Target = new PageRef
                            {
                                Kind = PageRefKind.HostedPage, Id = "p-shared",
                            },
                            Summons = new List<Summon>
                            {
                                MakeSummon("e2", BuiltInProperties.IsInPitLane),
                            },
                        },
                        new PriorityRow { Kind = PriorityRowKind.Manual },
                    },
                    Rest = new RestBlock
                    {
                        InSessionPage = new PageRef
                        {
                            Kind = PageRefKind.HostedPage, Id = "p-shared",
                        },
                    },
                },
            };
            var arb = new SeatArbiter(Normalize(doc));
            var r = arb.Tick(In(0, Snap("e2", true)));
            Assert.Equal("e2", r.Intent.WinnerCarrierId);

            // Higher satellite takes same dest immediately (D9).
            r = arb.Tick(In(10, Snap("e1", true), Snap("e2", true)));
            Assert.Equal("e1", r.Intent.WinnerCarrierId);
            Assert.False(r.Intent.DestinationChanged);
            Assert.False(r.Intent.DwellHeld);
        }

        // ── Composed resolution shape ────────────────────────────────────

        [Fact]
        public void Resolution_DisplaySurfaceOnly_OtherSurfacesEmpty()
        {
            var arb = new SeatArbiter(MinimalLadder(
                (PriorityRowKind.Seat, "s", "hosted", "p-b", new[] { "e1" }, null)));
            var r = arb.Tick(In(42, Snap("e1", true)));
            Assert.Equal(42, r.Resolution.TickMs);
            Assert.Single(r.Resolution.SurfaceWinners);
            Assert.Equal(SeatArbiter.DisplaySurfaceId, r.Resolution.SurfaceWinners[0].SurfaceId);
            Assert.Equal("e1", r.Resolution.SurfaceWinners[0].WinnerCarrierId);
        }

        [Fact]
        public void Idle_SessionEdges()
        {
            // SA-007: idle/session edges.
            var doc = MinimalLadder(
                (PriorityRowKind.Seat, "s", "hosted", "p-b", new[] { "e1" }, null));
            doc.Priority.Rest.Idle = new IdleSpec { Kind = IdleKind.Blank };
            var arb = new SeatArbiter(Normalize(doc));

            var inGame = In(0, Snap("e1", true));
            inGame.InGame = true;
            var r = arb.Tick(inGame);
            Assert.Null(r.Intent.IdleKind);
            Assert.Equal("e1", r.Intent.WinnerCarrierId);

            var idle = In(SeatArbiter.MinDwellMs);
            idle.InGame = false;
            r = arb.Tick(idle);
            Assert.Equal(IdleKind.Blank, r.Intent.IdleKind);
            Assert.Equal(DestinationIds.RestIdle, r.Intent.DestinationId);
        }
    }
}
