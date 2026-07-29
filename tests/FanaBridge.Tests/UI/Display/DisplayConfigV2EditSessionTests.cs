using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Schema2;
using FanaBridge.Profiles;
using FanaBridge.UI.Display;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests.UI.Display
{
    /// <summary>
    /// Q14: v2 UI write seam — <see cref="DisplayConfigV2EditSession"/> fresh-document
    /// discipline, generation/conflict apply (CAS), Priority mutation helpers,
    /// byte-preservation, and poll re-projection isolation.
    /// </summary>
    public class DisplayConfigV2EditSessionTests
    {
        // ── Fake host (document identity swap only; no engine) ───────────

        private sealed class FakeHost : IDisplayPanelHost
        {
            public DisplayConfigV2 Live = null!;

            public DisplaySettings DisplaySettings { get; } = new DisplaySettings();
            public DisplayType DisplayType => DisplayType.Itm;
            public byte ItmDeviceId => 3;
            public string WheelCode { get; set; } = "pbme";
            public string ModuleCode { get; set; } = null!;
            public DisplayConfigV2 GetDisplayConfigV2() => Live;
            public void ApplyDisplayConfigV2(DisplayConfigV2 config)
            {
                // Mirror runtime: structural clone identity swap (no catalog).
                Live = config == null
                    ? null!
                    : DisplayConfigV2Validator.Normalize(
                        DisplayConfigV2Serializer.Clone(config), _ => { });
            }

            public bool TryApplyDisplayConfigV2(DisplayConfigV2 expected, DisplayConfigV2 config)
            {
                // Mirror runtime CAS: publish only when live is still the expected identity.
                if (!ReferenceEquals(Live, expected))
                    return false;
                ApplyDisplayConfigV2(config);
                return true;
            }

            public DisplayPanelSnapshot Snapshot => null!;
        }

        // ── Fixtures ─────────────────────────────────────────────────────

        private static readonly string DocWithUnknowns = @"{
  ""schemaVersion"": 2,
  ""v3Top"": { ""nested"": true },
  ""v3TopFlag"": ""keep-me"",
  ""priority"": {
    ""v3Priority"": true,
    ""rows"": [
      {
        ""kind"": ""seat"",
        ""id"": ""seat-1"",
        ""v3Row"": ""row-x"",
        ""target"": { ""kind"": ""itmPage"", ""catalogPageId"": ""lapInfo"" },
        ""summons"": [
          {
            ""id"": ""sum-1"",
            ""enabled"": true,
            ""v3Summon"": ""s"",
            ""condition"": {
              ""source"": { ""kind"": ""builtIn"", ""name"": ""Fuel"" },
              ""operator"": ""greaterThan"",
              ""value"": 10
            },
            ""lifetime"": { ""kind"": ""whileTrue"" }
          },
          {
            ""id"": ""sum-2"",
            ""enabled"": true,
            ""condition"": {
              ""source"": { ""kind"": ""builtIn"", ""name"": ""Speed"" },
              ""operator"": ""greaterThan"",
              ""value"": 100
            },
            ""lifetime"": { ""kind"": ""forDuration"", ""durationMs"": 2000 }
          }
        ]
      },
      {
        ""kind"": ""seat"",
        ""id"": ""seat-2"",
        ""target"": { ""kind"": ""itmPage"", ""catalogPageId"": ""lapTimes"" },
        ""summons"": []
      },
      {
        ""kind"": ""manual"",
        ""returnToRestAfterMs"": 15000,
        ""v3Manual"": ""m""
      }
    ],
    ""rest"": {
      ""v3Rest"": 1,
      ""idle"": { ""kind"": ""screen"", ""screen"": ""logo"", ""v3Idle"": ""i"" }
    }
  },
  ""fields"": {
    ""5"": {
      ""v3Field"": { ""precision"": 1 },
      ""overrides"": [
        {
          ""id"": ""ov-1"",
          ""writes"": ""suffix"",
          ""content"": { ""kind"": ""text"", ""text"": ""X"" },
          ""actsAsEntrypoint"": false,
          ""v3Override"": ""o""
        }
      ]
    }
  },
  ""pages"": [
    {
      ""kind"": ""hostedPage"",
      ""id"": ""p-x"",
      ""name"": ""X"",
      ""layers"": [
        {
          ""id"": ""l-1"",
          ""content"": { ""kind"": ""text"", ""text"": ""A"" },
          ""actsAsEntrypoint"": false,
          ""v3Layer"": true
        }
      ]
    }
  ],
  ""settings"": { ""mode"": ""on"", ""v3Settings"": 42 }
}";

        private static DisplayConfigV2 SeedLive()
        {
            // Live documents in the host are Normalize'd instances (runtime path).
            return DisplayConfigV2Serializer.Load(DocWithUnknowns, _ => { });
        }

        // ── Open / generation / conflict ─────────────────────────────────

        [Fact]
        public void Open_HoldsIndependentClone_OriginalUntouched()
        {
            var live = SeedLive();
            var session = DisplayConfigV2EditSession.Open(live);

            Assert.NotSame(live, session.Document);
            Assert.Same(live, session.OpenedAgainst);
            Assert.Equal(0, session.Generation);

            // Mutating the session does not touch live.
            session.MoveRow(0, 1);
            Assert.Equal("seat-1", live.Priority.Rows[0].Id);
            Assert.Equal("seat-2", session.Document.Priority.Rows[0].Id);
            Assert.Equal(1, session.Generation);
        }

        [Fact]
        public void TryApply_Succeeds_WhenLiveIdentityUnchanged()
        {
            var host = new FakeHost { Live = SeedLive() };
            var session = DisplayConfigV2EditSession.Open(host.Live);
            session.SetReturnToRestAfterMs(8000);

            var result = session.TryApply(host);
            Assert.True(result.Succeeded);
            Assert.False(result.IsConflict);
            Assert.Null(result.Message);
            Assert.NotNull(result.Applied);
            Assert.Same(host.Live, result.Applied);
            Assert.Equal(8000, host.Live.Priority.Rows
                .First(r => r.Kind == PriorityRowKind.Manual).ReturnToRestAfterMs);
        }

        [Fact]
        public void TryApply_Conflicts_WhenLiveIdentityDrifted()
        {
            var host = new FakeHost { Live = SeedLive() };
            var session = DisplayConfigV2EditSession.Open(host.Live);
            session.MoveRow(0, 1);

            // Another writer publishes a new document identity (poll cannot do this;
            // a concurrent Apply / profile load can).
            host.ApplyDisplayConfigV2(DisplayConfigV2Serializer.Clone(host.Live));

            var result = session.TryApply(host);
            Assert.False(result.Succeeded);
            Assert.True(result.IsConflict);
            Assert.Null(result.Applied);
            Assert.Equal(DisplayCopy.ConfigEditConflict, result.Message);
        }

        [Fact]
        public void TryApply_ConflictMessage_IsRuledDisplayCopy_NotTautology()
        {
            var host = new FakeHost { Live = SeedLive() };
            var session = DisplayConfigV2EditSession.Open(host.Live);
            host.ApplyDisplayConfigV2(DisplayConfigV2Serializer.Clone(host.Live));

            var result = session.TryApply(host);
            Assert.True(result.IsConflict);
            // Real assertion: the typed result carries the ruled constant (not X==X).
            Assert.Equal(
                "This document changed while you were editing. Your changes were not applied.",
                result.Message);
            Assert.Same(DisplayCopy.ConfigEditConflict, result.Message);
        }

        [Fact]
        public void PollReprojection_WhileSessionOpen_LeavesSessionCloneUntouched()
        {
            var host = new FakeHost { Live = SeedLive() };
            var session = DisplayConfigV2EditSession.Open(host.Live);
            var workingBefore = session.Document;
            session.SetIdle(new IdleSpec { Kind = IdleKind.Blank });
            var afterEdit = session.Document;
            Assert.NotSame(workingBefore, afterEdit);
            Assert.Equal(1, session.Generation);

            // Simulate poll re-projection: views re-read host; they must NOT feed the
            // host document back into an open session.
            var polled = host.GetDisplayConfigV2();
            var overview = DisplayOverviewV2Model.Project(
                polled, DisplayResolutionSnapshotModel.Empty, null, DisplayType.Itm);
            Assert.NotNull(overview);

            // Session clone is still the post-edit document; host is still the open identity.
            Assert.Same(afterEdit, session.Document);
            Assert.Same(host.Live, session.OpenedAgainst);
            Assert.Equal(IdleKind.Blank, session.Document.Priority.Rest.Idle.Kind);
            // Host was never applied — still the original idle (logo screen).
            Assert.Equal(IdleKind.Screen, host.Live.Priority.Rest.Idle.Kind);
        }

        // ── CAS atomicity (runtime seam) ─────────────────────────────────

        [Fact]
        public void TryApply_RuntimeCas_CompetingPublish_YieldsConflict_NeverOverwrite()
        {
            var runtime = new DeviceDisplayRuntime(
                new DeviceConfig
                {
                    Profile = WheelProfileStore.FindByWheelType("PSWBMW"),
                    Capabilities = new WheelCapabilities(
                        WheelProfileStore.FindByWheelType("PSWBMW")!),
                },
                itmClock: () => null,
                log: _ => { });

            var original = SeedLive();
            runtime.SetConfigV2(original);

            var host = new RuntimeHost(runtime);
            var session = DisplayConfigV2EditSession.Open(host.GetDisplayConfigV2());
            session.SetReturnToRestAfterMs(4242);

            // Competing publish between session-open and apply (SetSettings / bake shape).
            var competitor = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(original), _ => { });
            competitor.Priority.Rows
                .First(r => r.Kind == PriorityRowKind.Manual).ReturnToRestAfterMs = 1111;
            runtime.SetConfigV2(competitor);

            var result = session.TryApply(host);
            Assert.False(result.Succeeded);
            Assert.True(result.IsConflict);
            Assert.Equal(DisplayCopy.ConfigEditConflict, result.Message);

            // Live is still the competitor — session edit never overwrote.
            Assert.Same(competitor, runtime.CurrentConfigV2);
            Assert.Equal(1111, runtime.CurrentConfigV2.Priority.Rows
                .First(r => r.Kind == PriorityRowKind.Manual).ReturnToRestAfterMs);
            Assert.NotEqual(4242, runtime.CurrentConfigV2.Priority.Rows
                .First(r => r.Kind == PriorityRowKind.Manual).ReturnToRestAfterMs);
        }

        private sealed class RuntimeHost : IDisplayPanelHost
        {
            private readonly DeviceDisplayRuntime _runtime;

            public RuntimeHost(DeviceDisplayRuntime runtime) => _runtime = runtime;

            public DisplaySettings DisplaySettings { get; } = new DisplaySettings();
            public DisplayType DisplayType => DisplayType.Itm;
            public byte ItmDeviceId => 3;
            public string WheelCode => "pbme";
            public string ModuleCode => null;
            public DisplayConfigV2 GetDisplayConfigV2() => _runtime.CurrentConfigV2;
            public void ApplyDisplayConfigV2(DisplayConfigV2 config)
                => _runtime.ApplyDisplayConfigV2(config);
            public bool TryApplyDisplayConfigV2(DisplayConfigV2 expected, DisplayConfigV2 config)
                => _runtime.TryApplyDisplayConfigV2(expected, config);
            public DisplayPanelSnapshot Snapshot => null!;
        }

        // ── Mutation shapes ──────────────────────────────────────────────

        [Fact]
        public void MoveRow_ReordersAuthoredRows()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            // [seat-1, seat-2, manual] → move index 0 to 2 → [seat-2, manual, seat-1]
            var next = session.MoveRow(0, 2);
            Assert.Same(session.Document, next);

            var rows = session.Document.Priority.Rows;
            Assert.Equal(3, rows.Count);
            Assert.Equal("seat-2", rows[0].Id);
            Assert.Equal(PriorityRowKind.Manual, rows[1].Kind);
            Assert.Equal("seat-1", rows[2].Id);
        }

        [Fact]
        public void AddSummon_AppendsWithGeneratedId()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            var next = session.AddSummon("seat-2", new Summon
            {
                Condition = new Condition
                {
                    Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = "Fuel" },
                    Operator = ConditionOperator.IsTrue,
                },
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
            });

            var summons = next.Priority.Rows.First(r => r.Id == "seat-2").Summons;
            Assert.Single(summons);
            Assert.False(string.IsNullOrWhiteSpace(summons[0].Id));
        }

        [Fact]
        public void AddSummon_ClonesCallerOwned_GeneratedIdDoesNotMutateCaller()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            var caller = new Summon
            {
                // Blank id → session generates one on its clone only.
                Condition = new Condition
                {
                    Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = "Fuel" },
                    Operator = ConditionOperator.IsTrue,
                },
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
            };

            session.AddSummon("seat-2", caller);

            Assert.True(string.IsNullOrWhiteSpace(caller.Id),
                "caller's summon must not receive the generated id");
            var attached = session.Document.Priority.Rows
                .First(r => r.Id == "seat-2").Summons.Single();
            Assert.NotSame(caller, attached);
            Assert.False(string.IsNullOrWhiteSpace(attached.Id));

            // Later caller mutation must not alter the session document.
            caller.Name = "mutated-after-attach";
            Assert.NotEqual("mutated-after-attach", attached.Name);
            Assert.Null(attached.Name);
        }

        [Fact]
        public void RemoveSummon_DropsSummon_KeepsSeat()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.RemoveSummon("seat-1", "sum-1");
            var row = session.Document.Priority.Rows.First(r => r.Id == "seat-1");
            Assert.Single(row.Summons);
            Assert.Equal("sum-2", row.Summons[0].Id);
            Assert.Contains(session.Document.Priority.Rows, r => r.Id == "seat-1");
        }

        [Fact]
        public void RemoveSummon_LastOnSatellite_RemovesRow()
        {
            var live = SeedLive();
            // Seed a one-summon satellite.
            live.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Satellite,
                Id = "sat-1",
                Target = new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
                Summons = new List<Summon>
                {
                    new Summon { Id = "only", Enabled = true },
                },
            });
            // Re-identity as a normalized live doc.
            live = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(live), _ => { });

            var session = DisplayConfigV2EditSession.Open(live);
            session.RemoveSummon("sat-1", "only");
            Assert.DoesNotContain(session.Document.Priority.Rows, r => r.Id == "sat-1");
        }

        [Fact]
        public void RemoveSummon_FutureShapeChildRef_SurvivesLastSummonRemoval()
        {
            var live = SeedLive();
            // Extension-data-only ChildRef (no Field/PageId) — future/malformed shape.
            var futureChild = new ChildRef
            {
                ExtensionData = new Dictionary<string, JToken>
                {
                    ["v3ChildShape"] = JToken.FromObject(new { kind = "future", refId = "x" }),
                },
            };
            live.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Satellite,
                Id = "sat-future",
                ChildRef = futureChild,
                Summons = new List<Summon>
                {
                    new Summon { Id = "only", Enabled = true },
                },
            });
            live = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(live), _ => { });

            var session = DisplayConfigV2EditSession.Open(live);
            session.RemoveSummon("sat-future", "only");

            var sat = Assert.Single(
                session.Document.Priority.Rows, r => r.Id == "sat-future");
            Assert.NotNull(sat.ChildRef);
            Assert.True(sat.Summons == null || sat.Summons.Count == 0);
            Assert.NotNull(sat.ChildRef.ExtensionData);
            Assert.True(sat.ChildRef.ExtensionData.ContainsKey("v3ChildShape"));
        }

        [Fact]
        public void SetSummonEnabled_TogglesFlag()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.SetSummonEnabled("seat-1", "sum-1", enabled: false);
            var s = session.Document.Priority.Rows
                .First(r => r.Id == "seat-1").Summons
                .First(x => x.Id == "sum-1");
            Assert.False(s.Enabled);
        }

        [Fact]
        public void SplitSatellite_FromSummon_MovesToNewSatellite()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.SplitSatellite("seat-1", "sum-2");

            var rows = session.Document.Priority.Rows;
            var seat = rows.First(r => r.Id == "seat-1");
            Assert.Single(seat.Summons);
            Assert.Equal("sum-1", seat.Summons[0].Id);

            int seatIndex = rows.FindIndex(r => r.Id == "seat-1");
            var sat = rows[seatIndex + 1];
            Assert.Equal(PriorityRowKind.Satellite, sat.Kind);
            Assert.Single(sat.Summons);
            Assert.Equal("sum-2", sat.Summons[0].Id);
            Assert.Equal(PageRefKind.ItmPage, sat.Target.Kind);
            Assert.Equal("lapInfo", sat.Target.CatalogPageId);
            Assert.NotNull(sat.SplitOrigin);
            Assert.Equal("seat-1", sat.SplitOrigin.RowId);
            Assert.Equal(1, sat.SplitOrigin.SummonIndex);
            Assert.True(sat.ExtensionData == null
                || !sat.ExtensionData.Keys.Any(k => k.StartsWith(
                    "__fanaBridgeSplit", StringComparison.Ordinal)));
        }

        [Fact]
        public void SplitSatellite_ChildRef_InsertsAfterRow()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            var caller = new ChildRef
            {
                Field = "5",
                OverrideId = "ov-1",
            };
            session.SplitSatellite("seat-1", caller);

            var rows = session.Document.Priority.Rows;
            int seatIndex = rows.FindIndex(r => r.Id == "seat-1");
            var sat = rows[seatIndex + 1];
            Assert.Equal(PriorityRowKind.Satellite, sat.Kind);
            Assert.Equal("5", sat.ChildRef.Field);
            Assert.Equal("ov-1", sat.ChildRef.OverrideId);
            Assert.Null(sat.Summons);
            Assert.NotSame(caller, sat.ChildRef);

            caller.Field = "99";
            Assert.Equal("5", sat.ChildRef.Field);
        }

        // OWNER-WAIVED FIDELITY (Surface C): rejoin inverse of split.
        [Fact]
        public void MergeSatellite_Summon_RoundTrip_RestoresHomeAndDeletesSat()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.SplitSatellite("seat-1", "sum-2");
            var sat = session.Document.Priority.Rows
                .First(r => r.Kind == PriorityRowKind.Satellite);
            string satId = sat.Id;

            string persisted = DisplayConfigV2Serializer.Save(session.Document);
            var reopened = DisplayConfigV2EditSession.Open(
                DisplayConfigV2Serializer.Load(persisted, _ => { }));
            reopened.MergeSatellite(satId);

            Assert.DoesNotContain(
                reopened.Document.Priority.Rows, r => r.Id == satId);
            var seat = reopened.Document.Priority.Rows.First(r => r.Id == "seat-1");
            Assert.Equal(2, seat.Summons.Count);
            Assert.Equal(new[] { "sum-1", "sum-2" },
                seat.Summons.Select(s => s.Id).ToArray());
            Assert.DoesNotContain("__fanaBridgeSplit", persisted);
        }

        [Fact]
        public void MergeSatellite_WithoutHome_PromotesAndClearsSplitOrigin()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.SplitSatellite("seat-1", "sum-2");
            var sat = session.Document.Priority.Rows
                .First(r => r.Kind == PriorityRowKind.Satellite);
            string satId = sat.Id;

            var withoutHome = DisplayConfigV2Serializer.Clone(session.Document);
            withoutHome.Priority.Rows.RemoveAll(r => r.Id == "seat-1");
            var orphanSession = DisplayConfigV2EditSession.Open(withoutHome);
            orphanSession.MergeSatellite(satId);

            var promoted = Assert.Single(
                orphanSession.Document.Priority.Rows, r => r.Id == satId);
            Assert.Equal(PriorityRowKind.Seat, promoted.Kind);
            Assert.Null(promoted.SplitOrigin);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RemoveTargetRows_ClearsDeletedSatelliteSplitOrigin(bool removeContent)
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.SplitSatellite("seat-1", "sum-2");
            var mutationClone = DisplayConfigV2Serializer.Clone(session.Document);
            var deletedSatellite = mutationClone.Priority.Rows
                .First(r => r.Kind == PriorityRowKind.Satellite);
            Assert.NotNull(deletedSatellite.SplitOrigin);

            try
            {
                DisplayConfigV2Serializer.CloneHookForTest = _ => mutationClone;
                var target = new PageRef
                {
                    Kind = PageRefKind.ItmPage,
                    CatalogPageId = "lapInfo",
                };
                if (removeContent)
                {
                    Assert.True(CatalogLoader.TryResolve(
                        "pbme", out var catalog, _ => { }));
                    session.RemovePageContent(target, catalog);
                }
                else
                {
                    session.RemoveRowsForTarget(target);
                }
            }
            finally
            {
                DisplayConfigV2Serializer.CloneHookForTest = null!;
            }

            Assert.Null(deletedSatellite.SplitOrigin);
            Assert.DoesNotContain(
                session.Document.Priority.Rows,
                r => r.Kind == PriorityRowKind.Satellite);
        }

        [Fact]
        public void MergeSatellite_ChildRef_DeletesSatelliteOnly()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.SplitSatellite("seat-1", new ChildRef
            {
                Field = "5",
                OverrideId = "ov-1",
            });
            var satId = session.Document.Priority.Rows
                .First(r => r.Kind == PriorityRowKind.Satellite && r.ChildRef != null).Id;

            // Override remains on the field ladder (split is insert-only for ChildRef).
            Assert.Contains(
                session.Document.Fields[5].Overrides, o => o.Id == "ov-1");

            session.MergeSatellite(satId);
            Assert.DoesNotContain(
                session.Document.Priority.Rows, r => r.Id == satId);
            Assert.Contains(
                session.Document.Fields[5].Overrides, o => o.Id == "ov-1");
        }

        [Fact]
        public void MergeSatellite_TryApply_Succeeds()
        {
            var host = new FakeHost { Live = SeedLive() };
            var session = DisplayConfigV2EditSession.Open(host.Live);
            session.SplitSatellite("seat-1", "sum-2");
            var satId = session.Document.Priority.Rows
                .First(r => r.Kind == PriorityRowKind.Satellite).Id;
            session.MergeSatellite(satId);
            var result = session.TryApply(host);
            Assert.True(result.Succeeded);
            Assert.DoesNotContain(
                host.Live.Priority.Rows, r => r.Kind == PriorityRowKind.Satellite);
        }

        // Surface B: AddPage
        [Fact]
        public void AddPage_Hosted_AppendsPageAndSeatAtTop()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.AddPage(new PageEntry
            {
                Kind = PageEntryKind.HostedPage,
                Name = "Alerts",
            }, addToRotation: false, ensurePrioritySeat: true);

            var hosted = Assert.Single(
                session.Document.Pages.Where(p => p.Kind == PageEntryKind.HostedPage
                    && p.Name == "Alerts"));
            Assert.False(string.IsNullOrEmpty(hosted.Id));
            var top = session.Document.Priority.Rows[0];
            Assert.Equal(PriorityRowKind.Seat, top.Kind);
            Assert.Equal(PageRefKind.HostedPage, top.Target.Kind);
            Assert.Equal(hosted.Id, top.Target.Id);
        }

        [Fact]
        public void AddPage_Itm_RestoresRemoved_AndRotation()
        {
            var live = SeedLive();
            live.Pages = new List<PageEntry>
            {
                new PageEntry
                {
                    Kind = PageEntryKind.ItmPage,
                    CatalogPageId = "tyreTemps",
                    Removed = true,
                },
            };
            live = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(live), _ => { });

            var session = DisplayConfigV2EditSession.Open(live);
            session.AddPage(new PageEntry
            {
                Kind = PageEntryKind.ItmPage,
                CatalogPageId = "tyreTemps",
                NameOverride = "Tire Temps",
            }, addToRotation: true, ensurePrioritySeat: true);

            var pe = session.Document.Pages
                .First(p => p.CatalogPageId == "tyreTemps");
            Assert.False(pe.Removed);
            Assert.Equal("Tire Temps", pe.NameOverride);
            Assert.Contains(
                session.Document.PageOrder,
                r => r.Kind == PageRefKind.ItmPage && r.CatalogPageId == "tyreTemps");
        }

        [Fact]
        public void AddPage_TryApply_DocumentHasPageAndSeat()
        {
            var host = new FakeHost { Live = SeedLive() };
            var session = DisplayConfigV2EditSession.Open(host.Live);
            session.AddPage(new PageEntry
            {
                Kind = PageEntryKind.HostedPage,
                Name = "Pit",
            });
            var result = session.TryApply(host);
            Assert.True(result.Succeeded);
            Assert.Contains(
                host.Live.Pages, p => p.Kind == PageEntryKind.HostedPage && p.Name == "Pit");
            Assert.Equal(PriorityRowKind.Seat, host.Live.Priority.Rows[0].Kind);
        }

        [Fact]
        public void SetReturnToRestAfterMs_WritesManualRow()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.SetReturnToRestAfterMs(null);
            var manual = session.Document.Priority.Rows
                .First(r => r.Kind == PriorityRowKind.Manual);
            Assert.Null(manual.ReturnToRestAfterMs);

            session.SetReturnToRestAfterMs(12000);
            Assert.Equal(12000, session.Document.Priority.Rows
                .First(r => r.Kind == PriorityRowKind.Manual).ReturnToRestAfterMs);
        }

        [Fact]
        public void SetReturnToRestAfterMs_MaterializesManualWhenMissing()
        {
            var live = SeedLive();
            live.Priority.Rows.RemoveAll(r => r.Kind == PriorityRowKind.Manual);
            live = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(live), _ => { });

            var session = DisplayConfigV2EditSession.Open(live);
            Assert.DoesNotContain(session.Document.Priority.Rows,
                r => r.Kind == PriorityRowKind.Manual);

            session.SetReturnToRestAfterMs(5000);
            var manual = Assert.Single(
                session.Document.Priority.Rows, r => r.Kind == PriorityRowKind.Manual);
            Assert.Equal(5000, manual.ReturnToRestAfterMs);
        }

        [Fact]
        public void SetIdle_ReplacesRestIdle()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            var caller = new IdleSpec
            {
                Kind = IdleKind.Page,
                Page = new PageRef { Kind = PageRefKind.HostedPage, Id = "p-x" },
            };
            session.SetIdle(caller);
            Assert.Equal(IdleKind.Page, session.Document.Priority.Rest.Idle.Kind);
            Assert.Equal("p-x", session.Document.Priority.Rest.Idle.Page.Id);
            Assert.NotSame(caller, session.Document.Priority.Rest.Idle);

            caller.Kind = IdleKind.Blank;
            Assert.Equal(IdleKind.Page, session.Document.Priority.Rest.Idle.Kind);
        }

        [Fact]
        public void SetActsAsEntrypoint_Field_WritesOverride()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.SetActsAsEntrypoint(ActsAsEntrypointTarget.Field, "5", "ov-1", true);
            Assert.True(session.Document.Fields[5].Overrides
                .First(o => o.Id == "ov-1").ActsAsEntrypoint);
        }

        [Fact]
        public void SetActsAsEntrypoint_Layer_WritesLayer()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.SetActsAsEntrypoint(ActsAsEntrypointTarget.Layer, "p-x", "l-1", true);
            var page = session.Document.Pages.First(p => p.Id == "p-x");
            Assert.True(page.Layers.First(l => l.Id == "l-1").ActsAsEntrypoint);
        }

        [Fact]
        public void SetActsAsEntrypoint_IdComparisons_AreCaseInsensitive()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.SetActsAsEntrypoint(ActsAsEntrypointTarget.Field, "5", "OV-1", true);
            Assert.True(session.Document.Fields[5].Overrides
                .First(o => o.Id == "ov-1").ActsAsEntrypoint);

            session.SetActsAsEntrypoint(ActsAsEntrypointTarget.Layer, "P-X", "L-1", true);
            var page = session.Document.Pages.First(p => p.Id == "p-x");
            Assert.True(page.Layers.First(l => l.Id == "l-1").ActsAsEntrypoint);
        }

        // ── Removal variants (owner ruling) ──────────────────────────────

        [Fact]
        public void RemoveRowsForTarget_DropsMatchingRows_PageAndOverridesSurvive()
        {
            var live = SeedLive();
            // Two rows targeting lapTimes + one override on field 5.
            live.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "seat-lap-b",
                Target = new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapTimes" },
                Summons = new List<Summon>(),
            });
            // seat-2 already targets lapTimes in SeedLive.
            live = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(live), _ => { });

            Assert.Equal(1, live.Fields[5].Overrides.Count);
            Assert.Contains(live.Priority.Rows, r => r.Id == "seat-2");

            var session = DisplayConfigV2EditSession.Open(live);
            session.RemoveRowsForTarget(new PageRef
            {
                Kind = PageRefKind.ItmPage,
                CatalogPageId = "lapTimes",
            });

            Assert.DoesNotContain(session.Document.Priority.Rows, r => r.Id == "seat-2");
            Assert.DoesNotContain(session.Document.Priority.Rows, r => r.Id == "seat-lap-b");
            // PageEntry untouched; overrides survive.
            Assert.Equal(1, session.Document.Fields[5].Overrides.Count);
            Assert.Contains(session.Document.Pages, p => p.Id == "p-x");
            Assert.False(session.Document.Pages.First(p => p.Id == "p-x").Removed);
        }

        [Fact]
        public void RemovePageContent_Hosted_ClearsLayers_PageEntrySurvives()
        {
            var live = SeedLive();
            live.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "seat-hosted",
                Target = new PageRef { Kind = PageRefKind.HostedPage, Id = "p-x" },
                Summons = new List<Summon>(),
            });
            live = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(live), _ => { });

            Assert.Single(live.Pages.First(p => p.Id == "p-x").Layers);

            var session = DisplayConfigV2EditSession.Open(live);
            session.RemovePageContent(new PageRef
            {
                Kind = PageRefKind.HostedPage,
                Id = "p-x",
            });

            Assert.DoesNotContain(session.Document.Priority.Rows, r => r.Id == "seat-hosted");
            var page = Assert.Single(session.Document.Pages, p => p.Id == "p-x");
            Assert.False(page.Removed);
            Assert.Empty(page.Layers ?? new List<LayerEntry>());
        }

        [Fact]
        public void RemovePageContent_Itm_WithCatalog_DeletesOverridesOnPage()
        {
            var live = SeedLive();
            // Attribute field 5 to lapInfo via a tiny catalog primary placement.
            var catalog = new WheelCatalog
            {
                Itm = new ItmCatalogSection
                {
                    Fields = new List<CatalogFieldDefinition>
                    {
                        new CatalogFieldDefinition { Id = "fuel", ParamId = 5, ShortCode = "FUEL" },
                    },
                    Pages = new List<CatalogPage>
                    {
                        new CatalogPage
                        {
                            Id = "lapInfo",
                            Index = 1,
                            Name = "Lap Info",
                            Placements = new List<CatalogFieldPlacement>
                            {
                                new CatalogFieldPlacement { Field = "fuel" },
                            },
                        },
                    },
                },
            };
            // seat-1 targets lapInfo.
            Assert.Equal("lapInfo", live.Priority.Rows.First(r => r.Id == "seat-1").Target.CatalogPageId);
            Assert.Single(live.Fields[5].Overrides);

            var session = DisplayConfigV2EditSession.Open(live);
            session.RemovePageContent(
                new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
                catalog);

            Assert.DoesNotContain(session.Document.Priority.Rows, r => r.Id == "seat-1");
            Assert.Empty(session.Document.Fields[5].Overrides);
        }

        [Fact]
        public void SetInSessionPage_WritesRestInSessionPage()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            var page = new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "tyreTemps" };
            session.SetInSessionPage(page);
            Assert.Equal(PageRefKind.ItmPage, session.Document.Priority.Rest.InSessionPage.Kind);
            Assert.Equal("tyreTemps", session.Document.Priority.Rest.InSessionPage.CatalogPageId);
            Assert.NotSame(page, session.Document.Priority.Rest.InSessionPage);
        }

        [Fact]
        public void UpdateSummon_ReplacesAuthoredFields()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.UpdateSummon("seat-1", "sum-1", new Summon
            {
                Name = "renamed",
                Enabled = false,
                Lifetime = new Lifetime { Kind = LifetimeKind.UntilDismissed },
            });
            var s = session.Document.Priority.Rows
                .First(r => r.Id == "seat-1").Summons
                .First(x => x.Id == "sum-1");
            Assert.Equal("renamed", s.Name);
            Assert.False(s.Enabled);
            Assert.Equal(LifetimeKind.UntilDismissed, s.Lifetime.Kind);
        }

        [Fact]
        public void UpdateSummon_PreservesExtensionData_AndHysteresis()
        {
            var live = SeedLive();
            var existing = live.Priority.Rows.First(r => r.Id == "seat-1")
                .Summons.First(x => x.Id == "sum-1");
            existing.Name = "keep-name";
            existing.Runs = RunsWhen.Always;
            existing.Condition.Hysteresis = 1.5;
            existing.ExtensionData = new Dictionary<string, JToken>
            {
                ["v3SummonExtra"] = JToken.FromObject("preserve-me"),
            };

            var session = DisplayConfigV2EditSession.Open(live);
            // Sparse form write: condition source/op/value + lifetime only.
            session.UpdateSummon("seat-1", "sum-1", new Summon
            {
                Enabled = true,
                Condition = new Condition
                {
                    Source = new ValueSource
                    {
                        Kind = ValueSourceKind.SimHubProperty,
                        Name = "DataCorePlugin.GameData.Fuel",
                    },
                    Operator = ConditionOperator.LessThan,
                    Value = 4,
                },
                Lifetime = new Lifetime { Kind = LifetimeKind.ForDuration, DurationMs = 3000 },
            });

            var s = session.Document.Priority.Rows
                .First(r => r.Id == "seat-1").Summons
                .First(x => x.Id == "sum-1");
            Assert.Equal("keep-name", s.Name);
            Assert.Equal(RunsWhen.Always, s.Runs);
            Assert.Equal(1.5, s.Condition.Hysteresis);
            Assert.Equal(4.0, s.Condition.Value);
            Assert.Equal("DataCorePlugin.GameData.Fuel", s.Condition.Source.Name);
            Assert.Equal(LifetimeKind.ForDuration, s.Lifetime.Kind);
            Assert.NotNull(s.ExtensionData);
            Assert.Equal("preserve-me", (string)s.ExtensionData["v3SummonExtra"]);
        }

        [Fact]
        public void UpdateSummon_DeepMerge_SourceAndLifetime_UnauthoredMembersSurvive()
        {
            // Fixture carries ALL unauthored members: source extension data; lifetime
            // direction, then-state, extension data. Form edit touches none of them.
            var live = SeedLive();
            var existing = live.Priority.Rows.First(r => r.Id == "seat-1")
                .Summons.First(x => x.Id == "sum-1");
            existing.Condition.Source = new ValueSource
            {
                Kind = ValueSourceKind.BuiltIn,
                Name = "Fuel",
                ExtensionData = new Dictionary<string, JToken>
                {
                    ["v3SourceExtra"] = JToken.FromObject("src-keep"),
                },
            };
            existing.Lifetime = new Lifetime
            {
                Kind = LifetimeKind.OnChange,
                Direction = ChangeDirection.Up,
                Then = LifetimeThen.UntilDismissed,
                ExtensionData = new Dictionary<string, JToken>
                {
                    ["v3LifeExtra"] = JToken.FromObject(7),
                },
            };

            var session = DisplayConfigV2EditSession.Open(live);
            session.UpdateSummon("seat-1", "sum-1", new Summon
            {
                Enabled = true,
                Condition = new Condition
                {
                    Source = new ValueSource
                    {
                        Kind = ValueSourceKind.SimHubProperty,
                        Name = "DataCorePlugin.GameData.Fuel",
                    },
                    Operator = ConditionOperator.LessThan,
                    Value = 4,
                },
                Lifetime = new Lifetime
                {
                    Kind = LifetimeKind.ForDuration,
                    DurationMs = 3000,
                },
            });

            var s = session.Document.Priority.Rows
                .First(r => r.Id == "seat-1").Summons
                .First(x => x.Id == "sum-1");
            Assert.Equal(ValueSourceKind.SimHubProperty, s.Condition.Source.Kind);
            Assert.Equal("DataCorePlugin.GameData.Fuel", s.Condition.Source.Name);
            Assert.NotNull(s.Condition.Source.ExtensionData);
            Assert.Equal("src-keep", (string)s.Condition.Source.ExtensionData["v3SourceExtra"]);
            Assert.Equal(LifetimeKind.ForDuration, s.Lifetime.Kind);
            Assert.Equal(3000, s.Lifetime.DurationMs);
            Assert.Equal(ChangeDirection.Up, s.Lifetime.Direction);
            Assert.Equal(LifetimeThen.UntilDismissed, s.Lifetime.Then);
            Assert.NotNull(s.Lifetime.ExtensionData);
            Assert.Equal(7, (int)s.Lifetime.ExtensionData["v3LifeExtra"]);
        }

        [Fact]
        public void RemovePageContent_PlanApply_OneSet_EndToEnd()
        {
            // Confirm path: one session plans the exclusive set; apply uses THAT set.
            Assert.True(CatalogLoader.TryResolve("pbme", out var catalog, _ => { }));
            var live = SeedLive();
            live.Fields[505] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride { Id = "ov-exclusive" },
                },
            };
            live = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(live), _ => { }, catalog);

            var host = new FakeHost { Live = live };
            var session = DisplayConfigV2EditSession.Open(host.GetDisplayConfigV2());
            Assert.True(session.TryPlanRemovePageContent(
                new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
                catalog,
                out var plan));
            Assert.True(plan.RankCount >= 1);
            Assert.True(plan.ContentCount >= 1);
            Assert.Contains((ushort)505, plan.ExclusiveParams);

            session.ApplyPageContentRemoval(plan);
            var result = session.TryApply(host);
            Assert.True(result.Succeeded);
            Assert.DoesNotContain(host.Live.Priority.Rows, r => r.Id == "seat-1");
            Assert.Empty(host.Live.Fields[505].Overrides);
        }

        [Fact]
        public void CanRemovePageContent_ResolvesTargetPage_NotAnyNonempty()
        {
            // Any-nonempty catalog is NOT resolution — target page must exist.
            var catalog = new WheelCatalog
            {
                Itm = new ItmCatalogSection
                {
                    Pages = new List<CatalogPage>
                    {
                        new CatalogPage { Id = "otherPage", Index = 0, Name = "Other" },
                    },
                },
            };
            Assert.False(DisplayConfigV2EditSession.CanRemovePageContent(
                new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
                catalog));
            Assert.True(DisplayConfigV2EditSession.CanRemovePageContent(
                new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "otherPage" },
                catalog));
        }

        [Fact]
        public void EnsureAuthoredRow_InsertsMissingMaterializedSeat()
        {
            var live = SeedLive();
            // No seat with this id yet.
            var session = DisplayConfigV2EditSession.Open(live);
            session.EnsureAuthoredRow(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "materialized-itm:x",
                Target = new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "x" },
            });
            Assert.Contains(session.Document.Priority.Rows, r => r.Id == "materialized-itm:x");
            // Second call is a no-op (generation unchanged after first).
            int gen = session.Generation;
            session.EnsureAuthoredRow(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "materialized-itm:x",
                Target = new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "x" },
            });
            Assert.Equal(gen, session.Generation);
        }

        [Fact]
        public void EnsureAuthoredRow_FullClone_PreservesSeedFields()
        {
            var seed = new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "mat-full",
                Target = new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
                BringUpLifetime = new Lifetime { Kind = LifetimeKind.UntilDismissed },
                Lifetime = new Lifetime { Kind = LifetimeKind.ForDuration, DurationMs = 2000 },
                ReturnToRestAfterMs = 9000,
                ChildRef = new ChildRef { Field = "5", OverrideId = "ov-1" },
                Summons = new List<Summon>
                {
                    new Summon { Id = "s", Name = "seeded", Enabled = true },
                },
                ExtensionData = new Dictionary<string, JToken>
                {
                    ["v3Seed"] = JToken.FromObject(42),
                },
            };

            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.EnsureAuthoredRow(seed);

            var row = session.Document.Priority.Rows.First(r => r.Id == "mat-full");
            Assert.Equal(LifetimeKind.UntilDismissed, row.BringUpLifetime.Kind);
            Assert.Equal(LifetimeKind.ForDuration, row.Lifetime.Kind);
            Assert.Equal(9000, row.ReturnToRestAfterMs);
            Assert.Equal("5", row.ChildRef.Field);
            Assert.Equal("ov-1", row.ChildRef.OverrideId);
            Assert.Equal("seeded", row.Summons.Single().Name);
            Assert.Equal(42, (int)row.ExtensionData["v3Seed"]);
            Assert.NotSame(seed, row);
            Assert.NotSame(seed.Target, row.Target);
        }

        [Fact]
        public void SetInSessionPage_RejectsCycleRef_WithValidationNote()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            var before = session.Document.Priority.Rest?.InSessionPage;
            int gen = session.Generation;
            session.SetInSessionPage(new PageRef
            {
                Kind = PageRefKind.Cycle,
                Id = "c1",
            });
            Assert.Equal(gen, session.Generation);
            Assert.Equal(before?.CatalogPageId, session.Document.Priority.Rest?.InSessionPage?.CatalogPageId);
            Assert.Contains(
                DisplayCopy.InSessionPageMustBeItmOrHosted,
                session.ValidationNotes);
        }

        [Fact]
        public void RemovePageContent_SharedMultiPageLadder_SurvivesSinglePageRemoveAll()
        {
            // Real pbme catalog: params 1 and 4 are placed on all five ITM pages.
            Assert.True(CatalogLoader.TryResolve("pbme", out var catalog, _ => { }));
            var live = SeedLive();
            live.Fields[1] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride { Id = "ov-speed", ActsAsEntrypoint = true },
                },
            };
            live.Fields[4] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride { Id = "ov-gear" },
                },
            };
            // Exclusive-to-lapInfo param (505 appears only on lapInfo in pbme).
            live.Fields[505] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride { Id = "ov-laps-exclusive" },
                },
            };
            live = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(live), _ => { }, catalog);

            var session = DisplayConfigV2EditSession.Open(live);
            session.RemovePageContent(
                new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
                catalog);

            // Shared ladders survive.
            Assert.Single(session.Document.Fields[1].Overrides);
            Assert.Single(session.Document.Fields[4].Overrides);
            // Exclusive ladder deleted.
            Assert.Empty(session.Document.Fields[505].Overrides);
            // Seat for lapInfo removed.
            Assert.DoesNotContain(session.Document.Priority.Rows, r => r.Id == "seat-1");
        }

        [Fact]
        public void RemovePageContent_PageExclusiveSharedField_IsDeleted()
        {
            // Exclusivity law spans BOTH collections: a page-exclusive param stored
            // under sharedFields is still cleared on remove-all for that page.
            Assert.True(CatalogLoader.TryResolve("pbme", out var catalog, _ => { }));
            var live = SeedLive();
            live.SharedFields = new Dictionary<string, FieldEntry>
            {
                // lap (505) is exclusive to lapInfo on PBME.
                ["lap"] = new FieldEntry
                {
                    Overrides = new List<FieldOverride>
                    {
                        new FieldOverride { Id = "ov-lap-shared-exclusive" },
                    },
                },
                // speed is multi-page — must survive.
                ["speed"] = new FieldEntry
                {
                    Overrides = new List<FieldOverride>
                    {
                        new FieldOverride { Id = "ov-speed-shared" },
                    },
                },
            };
            live = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(live), _ => { }, catalog);

            var session = DisplayConfigV2EditSession.Open(live);
            session.RemovePageContent(
                new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
                catalog);

            Assert.Empty(session.Document.SharedFields!["lap"].Overrides);
            Assert.Single(session.Document.SharedFields["speed"].Overrides);
        }

        [Fact]
        public void RemovePageContent_NoCatalog_IsNoOp_FailClosed()
        {
            var live = SeedLive();
            Assert.Single(live.Fields[5].Overrides);
            var session = DisplayConfigV2EditSession.Open(live);
            int gen = session.Generation;
            session.RemovePageContent(
                new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
                catalog: null);
            Assert.Equal(gen, session.Generation);
            Assert.Single(session.Document.Fields[5].Overrides);
            Assert.Contains(session.Document.Priority.Rows, r => r.Id == "seat-1");
            Assert.False(DisplayConfigV2EditSession.CanRemovePageContent(
                new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
                catalog: null));
        }

        [Fact]
        public void RemoveRowsForTarget_PreservesUnknownMembers()
        {
            var live = SeedLive();
            var beforeJson = DisplayConfigV2Serializer.Save(live);
            var session = DisplayConfigV2EditSession.Open(live);
            session.RemoveRowsForTarget(new PageRef
            {
                Kind = PageRefKind.ItmPage,
                CatalogPageId = "lapTimes",
            });
            var afterJson = DisplayConfigV2Serializer.Save(session.Document);
            AssertUnknownMembersSurvived(beforeJson, afterJson);
            Assert.Equal(beforeJson, DisplayConfigV2Serializer.Save(live));
        }

        [Fact]
        public void RemovePageContent_PreservesUnknownMembers()
        {
            var live = SeedLive();
            var beforeJson = DisplayConfigV2Serializer.Save(live);
            var session = DisplayConfigV2EditSession.Open(live);
            session.RemovePageContent(new PageRef
            {
                Kind = PageRefKind.HostedPage,
                Id = "p-x",
            });
            var afterJson = DisplayConfigV2Serializer.Save(session.Document);
            AssertUnknownMembersSurvived(beforeJson, afterJson);
            Assert.Equal(beforeJson, DisplayConfigV2Serializer.Save(live));
        }

        // ── Clone fail-closed ────────────────────────────────────────────

        [Fact]
        public void CloneFailure_DoesNotYieldEmptyDocumentPublish()
        {
            var host = new FakeHost { Live = SeedLive() };
            var session = DisplayConfigV2EditSession.Open(host.Live);
            var before = session.Document;
            Assert.NotEmpty(before.Priority.Rows);

            try
            {
                DisplayConfigV2Serializer.CloneHookForTest = _ =>
                    throw new InvalidOperationException("simulated clone corruption");

                var ex = Assert.Throws<InvalidOperationException>(() => session.MoveRow(0, 1));
                Assert.Contains("simulated clone corruption", ex.Message);

                // Working document unchanged — not wiped to empty default.
                Assert.Same(before, session.Document);
                Assert.NotEmpty(session.Document.Priority.Rows);
                Assert.Equal(0, session.Generation);

                // Publish still carries the non-empty working document, never an empty default.
                var apply = session.TryApply(host);
                Assert.True(apply.Succeeded);
                Assert.NotNull(host.Live.Priority);
                Assert.NotEmpty(host.Live.Priority.Rows);
                Assert.Contains(host.Live.Priority.Rows, r => r.Id == "seat-1");
            }
            finally
            {
                DisplayConfigV2Serializer.CloneHookForTest = null!;
            }
        }

        [Fact]
        public void SerializerClone_NullDeserializePath_ThrowsNotDefault()
        {
            try
            {
                DisplayConfigV2Serializer.CloneHookForTest = _ => null!;
                Assert.Throws<InvalidOperationException>(
                    () => DisplayConfigV2Serializer.Clone(SeedLive()));
            }
            finally
            {
                // Hook is one-shot and already cleared by Clone; clear defensively.
                DisplayConfigV2Serializer.CloneHookForTest = null!;
            }
        }

        // ── Byte-preservation ────────────────────────────────────────────

        [Theory]
        [InlineData("MoveRow")]
        [InlineData("AddSummon")]
        [InlineData("RemoveSummon")]
        [InlineData("SetSummonEnabled")]
        [InlineData("SplitSatelliteSummon")]
        [InlineData("SplitSatelliteChildRef")]
        [InlineData("MergeSatellite")]
        [InlineData("AddPage")]
        [InlineData("SetReturnToRestAfterMs")]
        [InlineData("SetIdle")]
        [InlineData("SetActsAsEntrypoint")]
        [InlineData("AddOverride")]
        [InlineData("UpdateOverride")]
        [InlineData("RemoveOverride")]
        [InlineData("MoveOverride")]
        [InlineData("SetFieldBase")]
        [InlineData("SetPageOrder")]
        [InlineData("MovePageOrder")]
        [InlineData("SetBringUpLifetime")]
        public void Mutation_PreservesUnknownMembersAndKeyOrder(string which)
        {
            var live = SeedLive();
            // Seed pageOrder + bring-up home for new helpers.
            if (live.PageOrder == null)
                live.PageOrder = new List<PageRef>();
            if (live.PageOrder.Count == 0)
            {
                live.PageOrder.Add(new PageRef
                {
                    Kind = PageRefKind.ItmPage,
                    CatalogPageId = "lapInfo",
                });
                live.PageOrder.Add(new PageRef
                {
                    Kind = PageRefKind.ItmPage,
                    CatalogPageId = "lapTimes",
                });
            }
            // Re-normalize so live is a Normalize'd host identity (runtime path).
            live = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(live), _ => { });

            var beforeJson = DisplayConfigV2Serializer.Save(live);
            var session = DisplayConfigV2EditSession.Open(live);

            switch (which)
            {
                case "MoveRow":
                    session.MoveRow(0, 1);
                    break;
                case "AddSummon":
                    session.AddSummon("seat-2", new Summon { Id = "sum-new" });
                    break;
                case "RemoveSummon":
                    session.RemoveSummon("seat-1", "sum-2");
                    break;
                case "SetSummonEnabled":
                    session.SetSummonEnabled("seat-1", "sum-1", false);
                    break;
                case "SplitSatelliteSummon":
                    session.SplitSatellite("seat-1", "sum-2");
                    break;
                case "SplitSatelliteChildRef":
                    session.SplitSatellite("seat-2", new ChildRef { Field = "5", OverrideId = "ov-1" });
                    break;
                case "MergeSatellite":
                {
                    session.SplitSatellite("seat-1", "sum-2");
                    var sat = session.Document.Priority.Rows
                        .First(r => r.Kind == PriorityRowKind.Satellite);
                    session.MergeSatellite(sat.Id);
                    break;
                }
                case "AddPage":
                    session.AddPage(new PageEntry
                    {
                        Kind = PageEntryKind.HostedPage,
                        Name = "BytePreserve",
                    });
                    break;
                case "SetReturnToRestAfterMs":
                    session.SetReturnToRestAfterMs(9000);
                    break;
                case "SetIdle":
                    session.SetIdle(new IdleSpec { Kind = IdleKind.Blank });
                    break;
                case "SetActsAsEntrypoint":
                    session.SetActsAsEntrypoint(ActsAsEntrypointTarget.Field, "5", "ov-1", true);
                    break;
                case "AddOverride":
                    session.AddOverride(5, new FieldOverride
                    {
                        Id = "ov-new",
                        Writes = FieldWrites.Suffix,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "!" },
                    });
                    break;
                case "UpdateOverride":
                    session.UpdateOverride(5, "ov-1", new FieldOverride
                    {
                        Writes = FieldWrites.Both,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "Y" },
                        Enabled = true,
                    });
                    break;
                case "RemoveOverride":
                    // Add a second so remove still leaves the extension-data ladder home.
                    session.AddOverride(5, new FieldOverride { Id = "ov-tmp", Writes = FieldWrites.Value });
                    session.RemoveOverride(5, "ov-tmp");
                    break;
                case "MoveOverride":
                    session.AddOverride(5, new FieldOverride { Id = "ov-2", Writes = FieldWrites.Value });
                    session.MoveOverride(5, 0, 1);
                    break;
                case "SetFieldBase":
                    session.SetFieldBase(5, new FieldBase
                    {
                        Source = new ValueSource
                        {
                            Kind = ValueSourceKind.BuiltIn,
                            Name = "Fuel",
                        },
                        Format = "withTotal",
                        BaseSuffix = "%",
                    });
                    break;
                case "SetPageOrder":
                    session.SetPageOrder(new List<PageRef>
                    {
                        new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapTimes" },
                        new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
                    });
                    break;
                case "MovePageOrder":
                    session.MovePageOrder(0, 1);
                    break;
                case "SetBringUpLifetime":
                    session.SetBringUpLifetime("seat-1", new Lifetime
                    {
                        Kind = LifetimeKind.ForDuration,
                        DurationMs = 3000,
                    });
                    break;
                default:
                    throw new InvalidOperationException(which);
            }

            var afterJson = DisplayConfigV2Serializer.Save(session.Document);
            AssertUnknownMembersSurvived(beforeJson, afterJson);
            // Original live document identity content unchanged (session clone only).
            Assert.Equal(beforeJson, DisplayConfigV2Serializer.Save(live));
        }

        // ── Pages & Fields helpers (shapes) ──────────────────────────────

        [Fact]
        public void AddOverride_AppendsToFieldsLadder_AssignsId()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.AddOverride(5, new FieldOverride
            {
                Writes = FieldWrites.Suffix,
                Content = new ContentObject { Kind = ContentKind.Text, Text = "!" },
            });
            var entry = session.Document.Fields[5];
            Assert.Equal(2, entry.Overrides.Count);
            Assert.False(string.IsNullOrEmpty(entry.Overrides[1].Id));
            Assert.Equal("!", entry.Overrides[1].Content.Text);
        }

        [Fact]
        public void UpdateOverride_PreservesExtensionData_AndUneditedMembers()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.UpdateOverride(5, "ov-1", new FieldOverride
            {
                Content = new ContentObject { Kind = ContentKind.Text, Text = "Z" },
                Enabled = true,
                ActsAsEntrypoint = true,
            });
            var ov = session.Document.Fields[5].Overrides[0];
            Assert.Equal("Z", ov.Content.Text);
            Assert.True(ov.ActsAsEntrypoint);
            Assert.NotNull(ov.ExtensionData);
            Assert.Equal("o", (string)ov.ExtensionData["v3Override"]);
        }

        [Fact]
        public void UpdateOverride_ContentMergesMemberWise_NestedExtensionSurvives()
        {
            var live = SeedLive();
            live.Fields[5].Overrides[0].Content = new ContentObject
            {
                Kind = ContentKind.Text,
                Text = "X",
                ExtensionData = new Dictionary<string, JToken>
                {
                    ["v3Content"] = "keep-me",
                },
            };
            live = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(live), _ => { });
            var session = DisplayConfigV2EditSession.Open(live);
            session.UpdateOverride(5, "ov-1", new FieldOverride
            {
                Content = new ContentObject { Kind = ContentKind.Text, Text = "Y" },
                Enabled = true,
            });
            var content = session.Document.Fields[5].Overrides[0].Content;
            Assert.Equal("Y", content.Text);
            Assert.NotNull(content.ExtensionData);
            Assert.Equal("keep-me", (string)content.ExtensionData["v3Content"]);
        }

        [Fact]
        public void SetPageOrder_TriState_AbsentEmptyExplicit()
        {
            var live = SeedLive();
            Assert.Null(live.PageOrder); // seed: absent

            // Absent → explicit list
            var session = DisplayConfigV2EditSession.Open(live);
            session.SetPageOrder(new List<PageRef>
            {
                new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
            });
            Assert.NotNull(session.Document.PageOrder);
            Assert.Single(session.Document.PageOrder);

            // Explicit → empty (distinct from absent)
            session.SetPageOrder(new List<PageRef>());
            Assert.NotNull(session.Document.PageOrder);
            Assert.Empty(session.Document.PageOrder);

            // Empty → absent
            session.SetPageOrder(null);
            Assert.Null(session.Document.PageOrder);
        }

        [Fact]
        public void SetBringUpLifetime_CloneMergesExistingLifetime()
        {
            var live = SeedLive();
            var seat = live.Priority.Rows.First(r => r.Id == "seat-1");
            seat.BringUpLifetime = new Lifetime
            {
                Kind = LifetimeKind.WhileTrue,
                ExtensionData = new Dictionary<string, JToken>
                {
                    ["v3Life"] = "preserve",
                },
            };
            live = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(live), _ => { });

            var session = DisplayConfigV2EditSession.Open(live);
            session.SetBringUpLifetime("seat-1", new Lifetime
            {
                Kind = LifetimeKind.ForDuration,
                DurationMs = 2500,
            });
            var life = session.Document.Priority.Rows.First(r => r.Id == "seat-1")
                .BringUpLifetime;
            Assert.Equal(LifetimeKind.ForDuration, life.Kind);
            Assert.Equal(2500, life.DurationMs);
            Assert.NotNull(life.ExtensionData);
            Assert.Equal("preserve", (string)life.ExtensionData["v3Life"]);
        }

        [Fact]
        public void RemoveOverride_DropsById()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.RemoveOverride(5, "ov-1");
            Assert.Empty(session.Document.Fields[5].Overrides);
        }

        [Fact]
        public void MoveOverride_ReordersRank()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.AddOverride(5, new FieldOverride { Id = "ov-2", Writes = FieldWrites.Value });
            session.MoveOverride(5, 0, 1);
            Assert.Equal("ov-2", session.Document.Fields[5].Overrides[0].Id);
            Assert.Equal("ov-1", session.Document.Fields[5].Overrides[1].Id);
        }

        [Fact]
        public void SetFieldBase_PreservesFieldExtensionData()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.SetFieldBase(5, new FieldBase
            {
                Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = "Fuel" },
                Format = "bare",
                BaseSuffix = string.Empty,
            });
            var entry = session.Document.Fields[5];
            Assert.Equal("Fuel", entry.Base.Source.Name);
            Assert.NotNull(entry.ExtensionData);
            Assert.True(entry.ExtensionData.ContainsKey("v3Field"));
        }

        [Fact]
        public void SetPageOrder_ReplacesOrder_RejectsCycle()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.SetPageOrder(new List<PageRef>
            {
                new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
            });
            Assert.Single(session.Document.PageOrder);
            Assert.Equal("lapInfo", session.Document.PageOrder[0].CatalogPageId);

            int gen = session.Generation;
            var blocked = session.SetPageOrder(new List<PageRef>
            {
                new PageRef { Kind = PageRefKind.Cycle, Id = "c1" },
            });
            Assert.Equal(gen, session.Generation); // no mutation
            Assert.Contains(session.ValidationNotes, n => n.Contains("cycle") || n.Contains("Cycle")
                || n == DisplayCopy.PageOrderMustNotContainCycle);
        }

        [Fact]
        public void MovePageOrder_SwapsSteps()
        {
            var live = SeedLive();
            live.PageOrder = new List<PageRef>
            {
                new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "a" },
                new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "b" },
            };
            live = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(live), _ => { });
            var session = DisplayConfigV2EditSession.Open(live);
            session.MovePageOrder(0, 1);
            Assert.Equal("b", session.Document.PageOrder[0].CatalogPageId);
            Assert.Equal("a", session.Document.PageOrder[1].CatalogPageId);
        }

        [Fact]
        public void SetBringUpLifetime_SetsSeatLifetime()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            session.SetBringUpLifetime("seat-1", new Lifetime
            {
                Kind = LifetimeKind.ForDuration,
                DurationMs = 2500,
            });
            var seat = session.Document.Priority.Rows.First(r => r.Id == "seat-1");
            Assert.Equal(LifetimeKind.ForDuration, seat.BringUpLifetime.Kind);
            Assert.Equal(2500, seat.BringUpLifetime.DurationMs);
        }

        [Fact]
        public void SharedOverride_UpdateWithoutCatalog_ByOverrideIdScan()
        {
            var live = SeedLive();
            live.SharedFields = new Dictionary<string, FieldEntry>
            {
                ["speed"] = new FieldEntry
                {
                    Overrides = new List<FieldOverride>
                    {
                        new FieldOverride
                        {
                            Id = "sov-1",
                            Writes = FieldWrites.Suffix,
                            Content = new ContentObject { Kind = ContentKind.Text, Text = "K" },
                            ExtensionData = new Dictionary<string, JToken>
                            {
                                ["v3SharedOv"] = "keep",
                            },
                        },
                    },
                },
            };
            live = DisplayConfigV2Validator.Normalize(
                DisplayConfigV2Serializer.Clone(live), _ => { });
            var session = DisplayConfigV2EditSession.Open(live);
            // param id ignored for shared scan fallback when catalog is null.
            session.UpdateOverride(4, "sov-1", new FieldOverride
            {
                Content = new ContentObject { Kind = ContentKind.Text, Text = "M" },
                Enabled = true,
            }, catalog: null);
            var ov = session.Document.SharedFields["speed"].Overrides[0];
            Assert.Equal("M", ov.Content.Text);
            Assert.Equal("keep", (string)ov.ExtensionData["v3SharedOv"]);
        }

        [Fact]
        public void OverrideHelpers_EndToEnd_TryApply()
        {
            var host = new FakeHost { Live = SeedLive() };
            var session = DisplayConfigV2EditSession.Open(host.Live);
            session.AddOverride(5, new FieldOverride
            {
                Id = "ov-e2e",
                Writes = FieldWrites.Suffix,
                Content = new ContentObject { Kind = ContentKind.Text, Text = "!" },
            });
            session.SetFieldBase(5, new FieldBase
            {
                Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = "Fuel" },
                Format = "bare",
            });
            session.SetPageOrder(new List<PageRef>
            {
                new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
            });
            var result = session.TryApply(host);
            Assert.True(result.Succeeded);
            Assert.Contains(host.Live.Fields[5].Overrides, o => o.Id == "ov-e2e");
            Assert.Equal("Fuel", host.Live.Fields[5].Base.Source.Name);
            Assert.Single(host.Live.PageOrder);
        }

        private static void AssertUnknownMembersSurvived(string beforeJson, string afterJson)
        {
            var before = JObject.Parse(beforeJson);
            var after = JObject.Parse(afterJson);

            // Top-level extension data + relative key order of known top keys around them.
            Assert.True(JToken.DeepEquals(before["v3Top"], after["v3Top"]));
            Assert.Equal((string)before["v3TopFlag"]!, (string)after["v3TopFlag"]!);
            Assert.Equal((int)before["settings"]!["v3Settings"]!, (int)after["settings"]!["v3Settings"]!);

            Assert.True(JToken.DeepEquals(before["priority"]!["v3Priority"], after["priority"]!["v3Priority"]));
            Assert.True(JToken.DeepEquals(before["priority"]!["rest"]!["v3Rest"], after["priority"]!["rest"]!["v3Rest"]));
            Assert.True(JToken.DeepEquals(before["fields"]!["5"]!["v3Field"], after["fields"]!["5"]!["v3Field"]));
            // Find ov-1 by id (MoveOverride may reorder the ladder).
            Assert.Equal(
                FindOverrideExt(before, "5", "ov-1", "v3Override"),
                FindOverrideExt(after, "5", "ov-1", "v3Override"));

            // Extension-member relative key order on the root (serializer clone path).
            AssertRelativeKeyOrder(before, after, "v3Top", "v3TopFlag");
            AssertRelativeKeyOrder(
                (JObject)before["priority"]!,
                (JObject)after["priority"]!,
                "rows", "rest", "v3Priority");
        }

        private static string FindOverrideExt(
            JObject root, string paramKey, string overrideId, string extKey)
        {
            var overrides = root["fields"]?[paramKey]?["overrides"] as JArray;
            if (overrides == null)
                return null!;
            foreach (var o in overrides)
            {
                if (o == null) continue;
                if (string.Equals((string)o["id"]!, overrideId, StringComparison.OrdinalIgnoreCase))
                    return (string)o[extKey]!;
            }
            return null!;
        }

        private static void AssertRelativeKeyOrder(
            JObject before, JObject after, params string[] keys)
        {
            var b = before.Properties().Select(p => p.Name).ToList();
            var a = after.Properties().Select(p => p.Name).ToList();
            int lastB = -1;
            int lastA = -1;
            foreach (var key in keys)
            {
                int ib = b.IndexOf(key);
                int ia = a.IndexOf(key);
                Assert.True(ib >= 0, "before missing " + key);
                Assert.True(ia >= 0, "after missing " + key);
                Assert.True(ib > lastB, "before order broke at " + key);
                Assert.True(ia > lastA, "after order broke at " + key);
                lastB = ib;
                lastA = ia;
            }
        }

        // ── Degraded-legal ───────────────────────────────────────────────

        [Fact]
        public void DegradedMutation_IsLegal_SurfacesNotes_DoesNotBlock()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            // ChildRef satellite that also keeps summons on the same row after a
            // follow-up AddSummon produces SummonsIgnored (degraded, legal).
            session.SplitSatellite("seat-1", new ChildRef
            {
                Field = "5",
                OverrideId = "ov-1",
            });
            var satId = session.Document.Priority.Rows
                .First(r => r.Kind == PriorityRowKind.Satellite && r.ChildRef != null).Id;
            session.AddSummon(satId, new Summon
            {
                Id = "extra",
                Condition = new Condition
                {
                    Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = "Fuel" },
                    Operator = ConditionOperator.IsTrue,
                },
            });

            // Mutation was not blocked — both shapes still present on the working doc.
            var sat = session.Document.Priority.Rows.First(r => r.Id == satId);
            Assert.NotNull(sat.ChildRef);
            Assert.Contains(sat.Summons, s => s.Id == "extra");

            // Notes collected (survivors); document not rewritten to drop either shape.
            Assert.NotEmpty(session.ValidationNotes);

            var host = new FakeHost { Live = session.OpenedAgainst };
            // Re-open identity: host still holds the original live; rebind host to
            // the opened-against identity for a clean apply path.
            host.Live = session.OpenedAgainst;
            var apply = session.TryApply(host);
            Assert.True(apply.Succeeded);

            // Applied (normalized) keeps both shapes on the stored row — survivors.
            var appliedSat = host.Live.Priority.Rows.First(r => r.Id == satId);
            Assert.NotNull(appliedSat.ChildRef);
            Assert.Contains(appliedSat.Summons, s => s.Id == "extra");
        }

        [Fact]
        public void FreshDocument_EachMutation_NeverMutatesPriorDocumentInPlace()
        {
            var session = DisplayConfigV2EditSession.Open(SeedLive());
            var d0 = session.Document;
            var d1 = session.MoveRow(0, 1);
            var d2 = session.SetSummonEnabled("seat-1", "sum-1", false);

            Assert.NotSame(d0, d1);
            Assert.NotSame(d1, d2);
            Assert.Same(session.Document, d2);
            // Prior snapshots stay frozen at their generation.
            Assert.Equal("seat-1", d0.Priority.Rows[0].Id);
            Assert.True(d1.Priority.Rows
                .First(r => r.Id == "seat-1").Summons
                .First(s => s.Id == "sum-1").Enabled);
            Assert.False(d2.Priority.Rows
                .First(r => r.Id == "seat-1").Summons
                .First(s => s.Id == "sum-1").Enabled);
        }
    }
}
