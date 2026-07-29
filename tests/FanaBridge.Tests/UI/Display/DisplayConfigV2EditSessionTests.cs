using System;
using System.Collections.Generic;
using System.Linq;
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
            public DisplayCustomizationConfig GetDisplayConfig() => null!;
            public void ApplyDisplayConfig(DisplayCustomizationConfig config) { }
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
            public void NotifySettingsChanged() { }
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
            public DisplayCustomizationConfig GetDisplayConfig() => null!;
            public void ApplyDisplayConfig(DisplayCustomizationConfig config) { }
            public DisplayConfigV2 GetDisplayConfigV2() => _runtime.CurrentConfigV2;
            public void ApplyDisplayConfigV2(DisplayConfigV2 config)
                => _runtime.ApplyDisplayConfigV2(config);
            public bool TryApplyDisplayConfigV2(DisplayConfigV2 expected, DisplayConfigV2 config)
                => _runtime.TryApplyDisplayConfigV2(expected, config);
            public DisplayPanelSnapshot Snapshot => null!;
            public void NotifySettingsChanged() { }
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
        [InlineData("SetReturnToRestAfterMs")]
        [InlineData("SetIdle")]
        [InlineData("SetActsAsEntrypoint")]
        public void Mutation_PreservesUnknownMembersAndKeyOrder(string which)
        {
            var live = SeedLive();
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
                case "SetReturnToRestAfterMs":
                    session.SetReturnToRestAfterMs(9000);
                    break;
                case "SetIdle":
                    session.SetIdle(new IdleSpec { Kind = IdleKind.Blank });
                    break;
                case "SetActsAsEntrypoint":
                    session.SetActsAsEntrypoint(ActsAsEntrypointTarget.Field, "5", "ov-1", true);
                    break;
                default:
                    throw new InvalidOperationException(which);
            }

            var afterJson = DisplayConfigV2Serializer.Save(session.Document);
            AssertUnknownMembersSurvived(beforeJson, afterJson);
            // Original live document identity content unchanged (session clone only).
            Assert.Equal(beforeJson, DisplayConfigV2Serializer.Save(live));
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
            Assert.Equal(
                (string)before["fields"]!["5"]!["overrides"]![0]!["v3Override"]!,
                (string)after["fields"]!["5"]!["overrides"]![0]!["v3Override"]!);

            // Extension-member relative key order on the root (serializer clone path).
            AssertRelativeKeyOrder(before, after, "v3Top", "v3TopFlag");
            AssertRelativeKeyOrder(
                (JObject)before["priority"]!,
                (JObject)after["priority"]!,
                "rows", "rest", "v3Priority");
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
