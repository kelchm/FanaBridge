using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Schema2;
using Newtonsoft.Json;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Keeper laws (no scaffolding split): pure v2 validator §14 pins. All tests here
    /// survive E8b. Phase E1b: every §14 rule, plus preservation / idempotence / never-throws.
    /// </summary>
    public class Schema2ValidatorTests
    {
        private static DisplayConfigV2 Norm(DisplayConfigV2? cfg, List<string>? log = null,
            WheelCatalog? catalog = null)
            => DisplayConfigV2Validator.Normalize(cfg, log == null ? (_ => { }) : (Action<string>)log.Add,
                catalog);

        private static DisplayConfigV2 Load(string json, List<string>? log = null)
            => DisplayConfigV2Serializer.Load(json, log == null ? (_ => { }) : (Action<string>)log.Add);

        private static string Save(DisplayConfigV2 cfg)
            => DisplayConfigV2Serializer.Save(cfg);

        /// <summary>Deserialize without Normalize — baseline for preservation checks.</summary>
        private static DisplayConfigV2 DeserializeOnly(string json)
            => JsonConvert.DeserializeObject<DisplayConfigV2>(json, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore,
            })!;

        private static void AssertUtf8Equal(string expected, string actual)
            => Assert.Equal(
                Encoding.UTF8.GetBytes((expected ?? "").Replace("\r\n", "\n")),
                Encoding.UTF8.GetBytes((actual ?? "").Replace("\r\n", "\n")));

        private static PageRef Hosted(string id)
            => new PageRef { Kind = PageRefKind.HostedPage, Id = id };

        private static PageRef Itm(string catalogPageId)
            => new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = catalogPageId };

        private static PageRef CycleRef(string id)
            => new PageRef { Kind = PageRefKind.Cycle, Id = id };

        private static DisplayConfigV2 DocWithHosted(string id = "p-a")
        {
            var cfg = new DisplayConfigV2();
            cfg.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.HostedPage,
                Id = id,
                Name = id,
            });
            return cfg;
        }

        private static Condition LevelCondition(string builtIn = "FuelPercent")
            => new Condition
            {
                Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = builtIn },
                Operator = ConditionOperator.LessThan,
                Value = 10,
            };

        private static Summon OkSummon(string id)
            => new Summon
            {
                Id = id,
                Condition = LevelCondition(),
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
            };

        // ── Laws ──────────────────────────────────────────────────────────

        [Fact]
        public void Law_NeverDropsData_DuplicatePageKept()
        {
            var cfg = new DisplayConfigV2();
            cfg.Pages.Add(new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-a", Name = "A" });
            cfg.Pages.Add(new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-a", Name = "A2" });
            Norm(cfg);
            Assert.Equal(2, cfg.Pages.Count);
            Assert.False(cfg.Pages[0].DegradedAtLoad);
            Assert.True(cfg.Pages[1].DegradedAtLoad);
        }

        [Fact]
        public void Law_RuntimeOnlyCoercions_RawPreserved()
        {
            var life = new Lifetime { KindRaw = "onChange", ThenRaw = "somethingElse" };
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "s1",
                Target = Hosted("p-a"),
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "e1",
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.BuiltIn,
                                Name = "BrakeBias",
                            },
                        },
                        Lifetime = life,
                    },
                },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.Equal("somethingElse", life.ThenRaw);
            Assert.True(life.ThenIgnored);
            Assert.Equal("onChange", life.KindRaw);
        }

        [Fact]
        public void Law_DocumentNeverRewritten_SaveByteIdentical()
        {
            // Hand-authored invalid doc: unresolved target, illegal then, hysteresis on edge.
            string json = @"{
  ""schemaVersion"": 2,
  ""pages"": [ { ""kind"": ""hostedPage"", ""id"": ""p-a"", ""name"": ""A"" } ],
  ""priority"": {
    ""rows"": [
      {
        ""kind"": ""seat"",
        ""id"": ""s1"",
        ""target"": { ""kind"": ""hostedPage"", ""id"": ""missing"" },
        ""summons"": [ {
          ""id"": ""e1"",
          ""condition"": {
            ""source"": { ""kind"": ""builtIn"", ""name"": ""BrakeBias"" },
            ""operator"": ""lessThan"",
            ""value"": 1,
            ""hysteresis"": 2
          },
          ""lifetime"": { ""kind"": ""onChange"", ""direction"": ""sideways"", ""then"": ""untilDismissed"", ""durationMs"": 3000 }
        } ]
      },
      { ""kind"": ""manual"" }
    ]
  }
}";
            // save(load(json)) must match save(deserialize-only) — Normalize never rewrites
            // persisted members (degrade marks / runtime projection are JsonIgnore).
            string baseline = Save(DeserializeOnly(json));
            var cfg = Load(json);
            string after = Save(cfg);
            AssertUtf8Equal(baseline, after);

            // Second load/save still identical.
            AssertUtf8Equal(after, Save(Load(after)));

            // Degraded marks set, but target id still "missing" in document.
            Assert.True(cfg.Priority.Rows[0].DegradedAtLoad);
            Assert.Equal("missing", cfg.Priority.Rows[0].Target.Id);
            Assert.Equal("sideways", cfg.Priority.Rows[0].Summons[0].Lifetime.DirectionRaw);
            Assert.Equal(3000, cfg.Priority.Rows[0].Summons[0].Lifetime.DurationMs);
            Assert.Equal(2.0, cfg.Priority.Rows[0].Summons[0].Condition.Hysteresis);
        }

        [Fact]
        public void Law_DocumentNeverRewritten_ExplicitNullPriorityMembers_Preserved()
        {
            // Serializer NullValueHandling.Ignore treats JSON null as absent on load,
            // so exercise true null members the way a programmatic/authoring path can:
            // Normalize must not fill them in.
            var cases = new[]
            {
                new DisplayConfigV2 { SchemaVersion = 2, Priority = null },
                new DisplayConfigV2
                {
                    SchemaVersion = 2,
                    Priority = new PriorityLadder { Rows = null, Rest = new RestBlock() },
                },
                new DisplayConfigV2
                {
                    SchemaVersion = 2,
                    Priority = new PriorityLadder { Rows = new List<PriorityRow>(), Rest = null },
                },
                new DisplayConfigV2
                {
                    SchemaVersion = 2,
                    Priority = new PriorityLadder { Rows = null, Rest = null },
                },
            };

            foreach (var raw in cases)
            {
                string baseline = Save(raw);
                // Clone via JSON so Norm does not share the baseline instance.
                var clone = DeserializeOnly(Save(raw));
                // Re-apply the intentional nulls DeserializeOnly may have restored via
                // property initializers (Ignore skips JSON null; Save omitted them).
                if (raw.Priority == null)
                    clone.Priority = null;
                else
                {
                    if (raw.Priority.Rows == null)
                        clone.Priority.Rows = null;
                    if (raw.Priority.Rest == null)
                        clone.Priority.Rest = null;
                }

                Norm(clone);
                AssertUtf8Equal(baseline, Save(clone));

                if (raw.Priority == null)
                    Assert.Null(clone.Priority);
                else
                {
                    Assert.NotNull(clone.Priority);
                    if (raw.Priority.Rows == null)
                        Assert.Null(clone.Priority.Rows);
                    if (raw.Priority.Rest == null)
                        Assert.Null(clone.Priority.Rest);
                }
            }

            // JSON documents that write explicit nulls: save(load) == save(deserialize-only)
            // under the shared serializer settings (null ≡ absent on both paths).
            string[] jsons =
            {
                @"{""schemaVersion"":2,""priority"":null}",
                @"{""schemaVersion"":2,""priority"":{""rows"":null}}",
                @"{""schemaVersion"":2,""priority"":{""rest"":null}}",
                @"{""schemaVersion"":2,""priority"":{""rows"":null,""rest"":null}}",
            };
            foreach (var json in jsons)
                AssertUtf8Equal(Save(DeserializeOnly(json)), Save(Load(json)));
        }

        // ── Identity & cardinality ────────────────────────────────────────

        [Fact]
        public void Identity_DuplicatePageId_FirstWins()
        {
            var cfg = new DisplayConfigV2();
            cfg.Pages.Add(new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-x", Name = "1" });
            cfg.Pages.Add(new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-x", Name = "2" });
            Norm(cfg);
            Assert.False(cfg.Pages[0].DegradedAtLoad);
            Assert.True(cfg.Pages[1].DegradedAtLoad);
        }

        [Fact]
        public void Identity_DuplicateCycleId_FirstWins()
        {
            var cfg = DocWithHosted("p-a");
            cfg.Pages.Add(new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-b", Name = "B" });
            cfg.Cycles.Add(new CycleEntry
            {
                Id = "c1",
                Members = new List<PageRef> { Hosted("p-a"), Hosted("p-b") },
            });
            cfg.Cycles.Add(new CycleEntry
            {
                Id = "c1",
                Members = new List<PageRef> { Hosted("p-a"), Hosted("p-b") },
            });
            Norm(cfg);
            Assert.False(cfg.Cycles[0].DegradedAtLoad);
            Assert.True(cfg.Cycles[1].DegradedAtLoad);
        }

        [Fact]
        public void Identity_DuplicateRowId_FirstWins()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat, Id = "s1", Target = Hosted("p-a"),
            });
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat, Id = "s1", Target = Hosted("p-a"),
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.False(cfg.Priority.Rows[0].DegradedAtLoad);
            Assert.True(cfg.Priority.Rows[1].DegradedAtLoad);
        }

        [Fact]
        public void Identity_DuplicateSummonId_FirstWins()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "s1",
                Target = Hosted("p-a"),
                Summons = new List<Summon> { OkSummon("e1"), OkSummon("e1") },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.False(cfg.Priority.Rows[0].Summons[0].DegradedAtLoad);
            Assert.True(cfg.Priority.Rows[0].Summons[1].DegradedAtLoad);
        }

        [Fact]
        public void Identity_DuplicateOverrideId_FirstWins()
        {
            var cfg = new DisplayConfigV2();
            cfg.Fields[1] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "o1",
                        Writes = FieldWrites.Value,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "A" },
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                    new FieldOverride
                    {
                        Id = "o1",
                        Writes = FieldWrites.Value,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "B" },
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            };
            Norm(cfg);
            Assert.False(cfg.Fields[1].Overrides[0].DegradedAtLoad);
            Assert.True(cfg.Fields[1].Overrides[1].DegradedAtLoad);
        }

        [Fact]
        public void Identity_DuplicateLayerId_FirstWins()
        {
            var cfg = DocWithHosted();
            cfg.Pages[0].Layers = new List<LayerEntry>
            {
                new LayerEntry
                {
                    Id = "l1",
                    Content = new ContentObject { Kind = ContentKind.Text, Text = "A" },
                    Condition = LevelCondition(),
                    Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                },
                new LayerEntry
                {
                    Id = "l1",
                    Content = new ContentObject { Kind = ContentKind.Text, Text = "B" },
                    Condition = LevelCondition(),
                    Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                },
            };
            Norm(cfg);
            Assert.False(cfg.Pages[0].Layers[0].DegradedAtLoad);
            Assert.True(cfg.Pages[0].Layers[1].DegradedAtLoad);
        }

        [Fact]
        public void Identity_DuplicateWheelScreenRuleId_FirstWins()
        {
            var cfg = new DisplayConfigV2();
            cfg.WheelScreen.Rules.Add(new WheelScreenRule
            {
                Id = "w1",
                Screen = WheelScreenCommand.Logo,
                Condition = LevelCondition(),
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
            });
            cfg.WheelScreen.Rules.Add(new WheelScreenRule
            {
                Id = "w1",
                Screen = WheelScreenCommand.Blank,
                Condition = LevelCondition(),
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
            });
            Norm(cfg);
            Assert.False(cfg.WheelScreen.Rules[0].DegradedAtLoad);
            Assert.True(cfg.WheelScreen.Rules[1].DegradedAtLoad);
        }

        [Fact]
        public void Cardinality_DuplicateHomeSeats_FirstWins()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat, Id = "s1", Target = Hosted("p-a"),
            });
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat, Id = "s2", Target = Hosted("p-a"),
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.False(cfg.Priority.Rows[0].DegradedAtLoad);
            Assert.True(cfg.Priority.Rows[1].DegradedAtLoad);
        }

        [Fact]
        public void Cardinality_MissingManual_RestoredAboveRest_RuntimeOnly()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat, Id = "s1", Target = Hosted("p-a"),
            });
            // No manual in document.
            Norm(cfg);
            Assert.DoesNotContain(cfg.Priority.Rows, r => r.Kind == PriorityRowKind.Manual);
            var manuals = cfg.Priority.EffectiveRows.Where(r => r.Kind == PriorityRowKind.Manual).ToList();
            Assert.Single(manuals);
            Assert.True(manuals[0].MaterializedAtLoad);
            // Restored is last in runtime (above rest floor, bottom of rows).
            Assert.Equal(PriorityRowKind.Manual,
                cfg.Priority.EffectiveRows[cfg.Priority.EffectiveRows.Count - 1].Kind);
        }

        [Fact]
        public void Cardinality_MoreThanOneManual_FirstWins()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual, ReturnToRestAfterMs = 1 });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual, ReturnToRestAfterMs = 2 });
            Norm(cfg);
            Assert.False(cfg.Priority.Rows[0].DegradedAtLoad);
            Assert.True(cfg.Priority.Rows[1].DegradedAtLoad);
        }

        [Fact]
        public void Cardinality_MaterializeSeat_AboveManual_EncounterOrder()
        {
            var cfg = new DisplayConfigV2();
            // Two hosted pages with flagged layers; no seats.
            cfg.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.HostedPage,
                Id = "p-first",
                Layers = new List<LayerEntry>
                {
                    new LayerEntry
                    {
                        Id = "l1",
                        ActsAsEntrypoint = true,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "A" },
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            });
            cfg.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.HostedPage,
                Id = "p-second",
                Layers = new List<LayerEntry>
                {
                    new LayerEntry
                    {
                        Id = "l2",
                        ActsAsEntrypoint = true,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "B" },
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);

            // Document rows unchanged (still just manual).
            Assert.Single(cfg.Priority.Rows);
            Assert.Equal(PriorityRowKind.Manual, cfg.Priority.Rows[0].Kind);

            var rt = cfg.Priority.EffectiveRows;
            Assert.Equal(3, rt.Count); // 2 materialized seats + manual
            Assert.True(rt[0].MaterializedAtLoad);
            Assert.Equal("p-first", rt[0].Target.Id);
            Assert.True(rt[1].MaterializedAtLoad);
            Assert.Equal("p-second", rt[1].Target.Id);
            Assert.Equal(PriorityRowKind.Manual, rt[2].Kind);
        }

        // ── Reference carriers ────────────────────────────────────────────

        [Fact]
        public void Ref_SeatTarget_Unresolved_RowDegraded()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat, Id = "s1", Target = Hosted("nope"),
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.True(cfg.Priority.Rows[0].DegradedAtLoad);
            Assert.True(cfg.Priority.Rows[0].Target.DegradedAtLoad);
        }

        [Fact]
        public void Ref_SatelliteChildRef_Missing_Degraded()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Satellite,
                Id = "sat1",
                ChildRef = new ChildRef { Field = "9", OverrideId = "missing" },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.True(cfg.Priority.Rows[0].DegradedAtLoad);
        }

        [Fact]
        public void Ref_SatelliteChildRef_Unflagged_Degraded()
        {
            var cfg = DocWithHosted();
            cfg.Fields[9] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "o-unflagged",
                        ActsAsEntrypoint = false,
                        Writes = FieldWrites.Value,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            };
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Satellite,
                Id = "sat1",
                ChildRef = new ChildRef { Field = "9", OverrideId = "o-unflagged" },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.True(cfg.Priority.Rows[0].DegradedAtLoad);
        }

        [Fact]
        public void Ref_CycleMember_Unresolved_AndWholeCycle()
        {
            var cfg = DocWithHosted("p-a");
            cfg.Cycles.Add(new CycleEntry
            {
                Id = "c1",
                Members = new List<PageRef> { Hosted("p-a"), Hosted("missing") },
            });
            Norm(cfg);
            Assert.True(cfg.Cycles[0].Members[1].DegradedAtLoad);
            // Only 1 resolvable → whole cycle degrades.
            Assert.True(cfg.Cycles[0].DegradedAtLoad);
        }

        [Fact]
        public void Ref_Cycle_TwoResolvable_Ok()
        {
            var cfg = DocWithHosted("p-a");
            cfg.Pages.Add(new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-b", Name = "B" });
            cfg.Cycles.Add(new CycleEntry
            {
                Id = "c1",
                Members = new List<PageRef> { Hosted("p-a"), Hosted("p-b") },
            });
            Norm(cfg);
            Assert.False(cfg.Cycles[0].DegradedAtLoad);
        }

        [Fact]
        public void Ref_PageOrder_Unresolved_AndDuplicates()
        {
            var cfg = DocWithHosted("p-a");
            cfg.PageOrder = new List<PageRef>
            {
                Hosted("p-a"),
                Hosted("p-a"),
                Hosted("missing"),
                CycleRef("c-x"),
            };
            Norm(cfg);
            Assert.False(cfg.PageOrder[0].DegradedAtLoad);
            Assert.True(cfg.PageOrder[1].DegradedAtLoad); // duplicate
            Assert.True(cfg.PageOrder[2].DegradedAtLoad); // missing
            Assert.True(cfg.PageOrder[3].DegradedAtLoad); // cycle illegal
        }

        [Fact]
        public void Ref_RestInSessionPage_Unresolved_FallbackFlag()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rest.InSessionPage = Hosted("missing");
            Norm(cfg);
            Assert.True(cfg.Priority.Rest.InSessionPage.DegradedAtLoad);
            Assert.True(cfg.Priority.Rest.InSessionPageUseDefaultWalk);
        }

        [Fact]
        public void Ref_RestInSessionPage_Cycle_FallbackFlag()
        {
            var cfg = DocWithHosted();
            cfg.Cycles.Add(new CycleEntry
            {
                Id = "c1",
                Members = new List<PageRef> { Hosted("p-a"), Hosted("p-a") },
            });
            cfg.Priority.Rest.InSessionPage = CycleRef("c1");
            Norm(cfg);
            Assert.True(cfg.Priority.Rest.InSessionPage.DegradedAtLoad);
            Assert.True(cfg.Priority.Rest.InSessionPageUseDefaultWalk);
        }

        // FA3: rest.landingPage removed — no validator marks for that carrier.

        [Fact]
        public void Ref_RestIdlePage_Unresolved_FallbackBlank()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rest.Idle = new IdleSpec
            {
                Kind = IdleKind.Page,
                Page = Hosted("missing"),
            };
            Norm(cfg);
            Assert.True(cfg.Priority.Rest.Idle.DegradedAtLoad);
            Assert.True(cfg.Priority.Rest.Idle.Page.DegradedAtLoad);
        }

        [Fact]
        public void Ref_ConditionSource_UnknownBuiltIn_CarrierDegraded()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "s1",
                Target = Hosted("p-a"),
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "e1",
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.BuiltIn,
                                Name = "NotARealProperty",
                            },
                            Operator = ConditionOperator.IsTrue,
                        },
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.True(cfg.Priority.Rows[0].Summons[0].DegradedAtLoad);
            Assert.True(cfg.Priority.Rows[0].Summons[0].Condition.Source.DegradedAtLoad);
        }

        [Fact]
        public void Ref_ConditionSource_MalformedItmFieldParam_Degraded()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "s1",
                Target = Hosted("p-a"),
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "e1",
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.ItmField,
                                Name = "not-a-number",
                            },
                            Operator = ConditionOperator.LessThan,
                            Value = 1,
                        },
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.True(cfg.Priority.Rows[0].Summons[0].DegradedAtLoad);
        }

        [Fact]
        public void Ref_ItmFieldSelf_OutsideFieldOverride_Degraded()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "s1",
                Target = Hosted("p-a"),
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "e1",
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.ItmField,
                                Name = "self",
                            },
                            Operator = ConditionOperator.LessThan,
                            Value = 1,
                        },
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.True(cfg.Priority.Rows[0].Summons[0].DegradedAtLoad);
        }

        [Fact]
        public void Ref_ItmFieldSelf_OnFieldOverride_Ok()
        {
            var cfg = new DisplayConfigV2();
            cfg.Fields[5] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "o1",
                        Writes = FieldWrites.Value,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.ItmField,
                                Name = "self",
                            },
                            Operator = ConditionOperator.LessThan,
                            Value = 1,
                        },
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            };
            Norm(cfg);
            Assert.False(cfg.Fields[5].Overrides[0].DegradedAtLoad);
            Assert.False(cfg.Fields[5].Overrides[0].Condition.Source.DegradedAtLoad);
        }

        [Fact]
        public void Ref_ContentSource_PropertyUnusable_NoDataConvention()
        {
            var cfg = DocWithHosted();
            cfg.Pages[0].Base = new ContentWithEffect
            {
                Content = new ContentObject
                {
                    Kind = ContentKind.Property,
                    Source = new ValueSource
                    {
                        Kind = ValueSourceKind.BuiltIn,
                        Name = "Nope",
                    },
                },
            };
            Norm(cfg);
            Assert.True(cfg.Pages[0].Base.Content.DegradedAtLoad);
        }

        // ── Shape coercions ───────────────────────────────────────────────

        [Fact]
        public void Shape_Satellite_BothSummonsAndChildRef_ChildRefWins()
        {
            var cfg = DocWithHosted();
            cfg.Pages[0].Layers = new List<LayerEntry>
            {
                new LayerEntry
                {
                    Id = "l1",
                    ActsAsEntrypoint = true,
                    Content = new ContentObject { Kind = ContentKind.Text, Text = "A" },
                    Condition = LevelCondition(),
                    Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                },
            };
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Satellite,
                Id = "sat1",
                Target = Hosted("p-a"),
                Summons = new List<Summon> { OkSummon("e1") },
                ChildRef = new ChildRef { PageId = "p-a", LayerId = "l1" },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.True(cfg.Priority.Rows[0].SummonsIgnored);
            Assert.True(cfg.Priority.Rows[0].DegradedAtLoad); // both → degrade-visible
            // Child resolves (flagged) — not further degraded for missing child.
        }

        [Fact]
        public void Shape_Satellite_Neither_Inert()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Satellite,
                Id = "sat1",
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.True(cfg.Priority.Rows[0].DegradedAtLoad);
        }

        [Fact]
        public void Shape_WhileTrue_OperatorLess_Degraded()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "s1",
                Target = Hosted("p-a"),
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "e-bad",
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.BuiltIn,
                                Name = "FuelPercent",
                            },
                            // no operator
                        },
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.True(cfg.Priority.Rows[0].Summons[0].DegradedAtLoad);
        }

        /// <summary>
        /// FA2: authored "action" source kind is unknown and degrades (raw preserved).
        /// </summary>
        [Fact]
        public void Shape_ActionSourceKind_DegradesAsUnknown()
        {
            var cfg = DocWithHosted();
            var source = new ValueSource { KindRaw = "action", Name = "showPit" };
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "s1",
                Target = Hosted("p-a"),
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "e-action",
                        Condition = new Condition
                        {
                            Source = source,
                        },
                        Lifetime = new Lifetime { Kind = LifetimeKind.OnChange, DurationMs = 2000 },
                    },
                },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.Equal(ValueSourceKind.Unknown, source.Kind);
            Assert.Equal("action", source.KindRaw);
            Assert.True(source.DegradedAtLoad);
            Assert.True(cfg.Priority.Rows[0].Summons[0].DegradedAtLoad);
        }

        [Fact]
        public void Shape_OnChange_WithOperator_Degraded()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "s1",
                Target = Hosted("p-a"),
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "e1",
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.OnChange },
                    },
                },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.True(cfg.Priority.Rows[0].Summons[0].DegradedAtLoad);
        }

        [Fact]
        public void Shape_Hysteresis_OnNonLevel_Ignored()
        {
            var cfg = DocWithHosted();
            var c = new Condition
            {
                Source = new ValueSource { Kind = ValueSourceKind.BuiltIn, Name = "BrakeBias" },
                Hysteresis = 2,
            };
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "s1",
                Target = Hosted("p-a"),
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "e1",
                        Condition = c,
                        Lifetime = new Lifetime { Kind = LifetimeKind.OnChange },
                    },
                },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.True(c.HysteresisIgnored);
            Assert.Equal(2.0, c.Hysteresis); // document preserved
        }

        [Fact]
        public void Shape_Direction_OutsideDomain_CoercedToAny()
        {
            var life = new Lifetime { KindRaw = "onChange", DirectionRaw = "sideways" };
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "s1",
                Target = Hosted("p-a"),
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "e1",
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.BuiltIn,
                                Name = "BrakeBias",
                            },
                        },
                        Lifetime = life,
                    },
                },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.Equal(ChangeDirection.Unknown, life.Direction); // parsed fallback intact
            Assert.True(life.DirectionCoercedToAny);               // engine uses Any
            Assert.Equal("sideways", life.DirectionRaw);
        }

        [Fact]
        public void Shape_Then_OutsideDomain_Ignored()
        {
            var life = new Lifetime { KindRaw = "onChange", ThenRaw = "forAWhile" };
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "s1",
                Target = Hosted("p-a"),
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "e1",
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.BuiltIn,
                                Name = "BrakeBias",
                            },
                        },
                        Lifetime = life,
                    },
                },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.True(life.ThenIgnored);
            Assert.Equal(LifetimeThen.Unknown, life.Then); // parsed fallback intact
            Assert.Equal("forAWhile", life.ThenRaw);
        }

        [Fact]
        public void Shape_ThenPlusDurationMs_DurationIgnored()
        {
            var life = new Lifetime
            {
                Kind = LifetimeKind.OnChange,
                Then = LifetimeThen.UntilDismissed,
                DurationMs = 3000,
            };
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "s1",
                Target = Hosted("p-a"),
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "e1",
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.BuiltIn,
                                Name = "BrakeBias",
                            },
                        },
                        Lifetime = life,
                    },
                },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.True(life.DurationMsIgnored);
            Assert.Equal(3000, life.DurationMs);
            Assert.True(cfg.Priority.Rows[0].Summons[0].DegradedAtLoad);
        }

        [Fact]
        public void Shape_UntilDismissed_OnUnflaggedChild_CoercesToForDuration()
        {
            var cfg = new DisplayConfigV2();
            var life = new Lifetime { Kind = LifetimeKind.UntilDismissed };
            cfg.Fields[1] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "o1",
                        ActsAsEntrypoint = false,
                        Writes = FieldWrites.Value,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                        Condition = LevelCondition(),
                        Lifetime = life,
                    },
                },
            };
            Norm(cfg);
            Assert.Equal(LifetimeKind.ForDuration, life.Kind);
            Assert.Equal("untilDismissed", life.KindRaw); // raw preserved
            Assert.True(cfg.Fields[1].Overrides[0].DegradedAtLoad);
        }

        [Fact]
        public void Shape_UntilDismissed_OnFlaggedChild_Ok()
        {
            var cfg = new DisplayConfigV2();
            cfg.Fields[1] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "o1",
                        ActsAsEntrypoint = true,
                        Writes = FieldWrites.Value,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.UntilDismissed },
                    },
                },
            };
            Norm(cfg);
            Assert.False(cfg.Fields[1].Overrides[0].DegradedAtLoad);
            Assert.Equal(LifetimeKind.UntilDismissed, cfg.Fields[1].Overrides[0].Lifetime.Kind);
        }

        [Fact]
        public void Shape_BringUpLifetime_Domain_CoercesToWhileTrue()
        {
            var life = new Lifetime { Kind = LifetimeKind.OnChange };
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "s1",
                Target = Hosted("p-a"),
                BringUpLifetime = life,
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.Equal(LifetimeKind.WhileTrue, life.Kind);
            Assert.Equal("onChange", life.KindRaw);
            // FZ-011: bring-up lifetime coercion is degrade-visible on the owning row.
            Assert.True(cfg.Priority.Rows[0].DegradedAtLoad);
        }

        [Fact]
        public void Shape_ChildRefSatellite_LifetimeCoercion_MarksRowDegraded()
        {
            var cfg = DocWithHosted();
            cfg.Pages[0].Layers = new List<LayerEntry>
            {
                new LayerEntry
                {
                    Id = "l1",
                    ActsAsEntrypoint = true,
                    Content = new ContentObject { Kind = ContentKind.Text, Text = "A" },
                    Condition = LevelCondition(),
                    Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                },
            };
            var life = new Lifetime { Kind = LifetimeKind.OnChange };
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Satellite,
                Id = "sat1",
                ChildRef = new ChildRef { PageId = "p-a", LayerId = "l1" },
                Lifetime = life,
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.Equal(LifetimeKind.WhileTrue, life.Kind);
            Assert.Equal("onChange", life.KindRaw);
            Assert.True(cfg.Priority.Rows[0].DegradedAtLoad);
        }

        [Fact]
        public void Shape_OverLengthText_EffectiveClamp_DocumentPreserved()
        {
            var cfg = DocWithHosted();
            cfg.Pages[0].Base = new ContentWithEffect
            {
                Content = new ContentObject
                {
                    Kind = ContentKind.Text,
                    Text = "TOOLONG",
                },
            };
            Norm(cfg);
            Assert.Equal("TOOLONG", cfg.Pages[0].Base.Content.Text);
            Assert.Equal("TOO", cfg.Pages[0].Base.Content.EffectiveText);
            Assert.True(cfg.Pages[0].Base.Content.DegradedAtLoad);
        }

        // ── Capability matrix (optional catalog) ──────────────────────────

        private static WheelCatalog CatalogWithField(
            ushort paramId,
            bool? suffixSupported = true,
            bool? valueAscii = true,
            int primaryHostCount = 1,
            bool? logoSupported = true,
            bool? blankSupported = true)
        {
            string logicalId = "f" + paramId;
            var cat = new WheelCatalog
            {
                WheelId = "test",
                Itm = new ItmCatalogSection
                {
                    Fields = new List<CatalogFieldDefinition>
                    {
                        new CatalogFieldDefinition
                        {
                            Id = logicalId,
                            ParamId = paramId,
                            Suffix = new FieldSuffixCapability { Supported = suffixSupported },
                            Value = new FieldValueCapability { Ascii = valueAscii, Numeric = true },
                        },
                    },
                },
                ScreenCommands = new ScreenCommandsCapability
                {
                    Logo = logoSupported,
                    Blank = blankSupported,
                    White = true,
                    LogoInverted = true,
                },
            };

            // Spread primaryHost across pages if count > 1.
            for (int i = 0; i < Math.Max(1, primaryHostCount); i++)
            {
                var page = new CatalogPage
                {
                    Id = "page" + i,
                    Index = i,
                    Placements = new List<CatalogFieldPlacement>
                    {
                        new CatalogFieldPlacement
                        {
                            Field = logicalId,
                            PrimaryHost = primaryHostCount > 0 && i < primaryHostCount
                                ? true
                                : (bool?)null,
                        },
                    },
                };
                cat.Itm.Pages.Add(page);
            }

            if (primaryHostCount == 0)
            {
                // One page, field present, no primaryHost.
                cat.Itm.Pages.Clear();
                cat.Itm.Pages.Add(new CatalogPage
                {
                    Id = "page0",
                    Index = 0,
                    Placements = new List<CatalogFieldPlacement>
                    {
                        new CatalogFieldPlacement
                        {
                            Field = logicalId,
                            PrimaryHost = null,
                        },
                    },
                });
            }

            return cat;
        }

        [Fact]
        public void Capability_SkippedWhenNoCatalog()
        {
            var cfg = new DisplayConfigV2();
            cfg.Fields[1] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "o1",
                        Writes = FieldWrites.Suffix,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            };
            Norm(cfg, catalog: null);
            Assert.False(cfg.Fields[1].Overrides[0].DegradedAtLoad);
        }

        [Fact]
        public void Capability_SuffixOnNoSuffixField_Degraded()
        {
            var cat = CatalogWithField(1, suffixSupported: false);
            var cfg = new DisplayConfigV2();
            cfg.Fields[1] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "o1",
                        Writes = FieldWrites.Suffix,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            };
            Norm(cfg, catalog: cat);
            Assert.True(cfg.Fields[1].Overrides[0].DegradedAtLoad);
        }

        [Fact]
        public void Capability_TextInNonAsciiValueRegion_Degraded()
        {
            var cat = CatalogWithField(1, valueAscii: false);
            var cfg = new DisplayConfigV2();
            cfg.Fields[1] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "o1",
                        Writes = FieldWrites.Value,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "AB" },
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            };
            Norm(cfg, catalog: cat);
            Assert.True(cfg.Fields[1].Overrides[0].DegradedAtLoad);
        }

        [Fact]
        public void Capability_BringUp_ZeroPrimaryHost_FlagInert()
        {
            var cat = CatalogWithField(1, primaryHostCount: 0);
            var cfg = new DisplayConfigV2();
            cfg.Fields[1] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "o1",
                        ActsAsEntrypoint = true,
                        Writes = FieldWrites.Value,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            };
            Norm(cfg, catalog: cat);
            Assert.True(cfg.Fields[1].Overrides[0].ActsAsEntrypointIgnored);
            Assert.True(cfg.Fields[1].Overrides[0].ActsAsEntrypoint); // document preserved
        }

        [Fact]
        public void Capability_BringUp_MultiplePrimaryHost_FlagInert()
        {
            var cat = CatalogWithField(1, primaryHostCount: 2);
            var cfg = new DisplayConfigV2();
            cfg.Fields[1] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "o1",
                        ActsAsEntrypoint = true,
                        Writes = FieldWrites.Value,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            };
            Norm(cfg, catalog: cat);
            Assert.True(cfg.Fields[1].Overrides[0].ActsAsEntrypointIgnored);
        }

        [Fact]
        public void Capability_BringUp_RemovedHost_FlagInert()
        {
            var cat = CatalogWithField(1, primaryHostCount: 1);
            // Catalog primary host page id is "page0".
            var cfg = new DisplayConfigV2();
            cfg.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.ItmPage,
                CatalogPageId = "page0",
                Removed = true,
            });
            cfg.Fields[1] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "o1",
                        ActsAsEntrypoint = true,
                        Writes = FieldWrites.Value,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            };
            Norm(cfg, catalog: cat);
            Assert.True(cfg.Fields[1].Overrides[0].ActsAsEntrypointIgnored);
        }

        [Fact]
        public void Capability_WheelScreen_Unsupported_Degraded()
        {
            var cat = CatalogWithField(1, logoSupported: false);
            var cfg = new DisplayConfigV2();
            cfg.WheelScreen.Rules.Add(new WheelScreenRule
            {
                Id = "w1",
                Screen = WheelScreenCommand.Logo,
                Condition = LevelCondition(),
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
            });
            Norm(cfg, catalog: cat);
            Assert.True(cfg.WheelScreen.Rules[0].DegradedAtLoad);
        }

        [Fact]
        public void Capability_WheelScreen_NullCapability_WarnsButDoesNotGate()
        {
            var cat = CatalogWithField(1, logoSupported: null);
            var log = new List<string>();
            var cfg = new DisplayConfigV2();
            cfg.WheelScreen.Rules.Add(new WheelScreenRule
            {
                Id = "w1",
                Screen = WheelScreenCommand.Logo,
                Condition = LevelCondition(),
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
            });
            Norm(cfg, log, cat);
            Assert.False(cfg.WheelScreen.Rules[0].DegradedAtLoad);
            Assert.Contains(log, m => m.IndexOf("untested", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void Capability_BlankIdle_CommandLessItm_ParkOnLegacy()
        {
            var cat = CatalogWithField(1, blankSupported: false);
            var cfg = new DisplayConfigV2();
            cfg.Priority.Rest.Idle = new IdleSpec { Kind = IdleKind.Blank };
            Norm(cfg, catalog: cat);
            Assert.True(cfg.Priority.Rest.Idle.ParkOnLegacyForBlank);
        }

        [Fact]
        public void Capability_SubscriptionBudget_WarnOnly()
        {
            var cat = CatalogWithField(1);
            var log = new List<string>();
            var cfg = new DisplayConfigV2();
            for (ushort i = 0; i < 17; i++)
                cfg.Fields[i] = new FieldEntry();
            Norm(cfg, log, cat);
            Assert.Contains(log, m => m.IndexOf("subscription budget", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // ── Meta proofs ───────────────────────────────────────────────────

        [Fact]
        public void Meta_Idempotence_NormalizeTwice_SameRuntimeState()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat, Id = "s1", Target = Hosted("missing"),
            });
            // no manual
            Norm(cfg);
            bool deg1 = cfg.Priority.Rows[0].DegradedAtLoad;
            int rtCount1 = cfg.Priority.EffectiveRows.Count;
            bool manualMat1 = cfg.Priority.EffectiveRows.Any(r => r.MaterializedAtLoad
                && r.Kind == PriorityRowKind.Manual);

            Norm(cfg);
            Assert.Equal(deg1, cfg.Priority.Rows[0].DegradedAtLoad);
            Assert.Equal(rtCount1, cfg.Priority.EffectiveRows.Count);
            Assert.Equal(manualMat1, cfg.Priority.EffectiveRows.Any(r => r.MaterializedAtLoad
                && r.Kind == PriorityRowKind.Manual));
        }

        [Fact]
        public void Meta_NeverThrows_OnGarbage()
        {
            var ex = Record.Exception(() =>
            {
                Norm(null);
                Norm(new DisplayConfigV2());
                Load("{ not json at all");
                Load("null");
                Load("");
                var messy = new DisplayConfigV2
                {
                    Pages = new List<PageEntry> { null!, new PageEntry() },
                    Cycles = new List<CycleEntry> { null! },
                    Priority = new PriorityLadder
                    {
                        Rows = new List<PriorityRow> { null!, new PriorityRow() },
                    },
                    Fields = new Dictionary<ushort, FieldEntry>
                    {
                        [0] = null!,
                        [1] = new FieldEntry { Overrides = new List<FieldOverride> { null! } },
                    },
                    WheelScreen = new WheelScreenPlane
                    {
                        Rules = new List<WheelScreenRule> { null! },
                    },
                    PageOrder = new List<PageRef> { null! },
                };
                Norm(messy);
            });
            Assert.Null(ex);
        }

        [Fact]
        public void Meta_LoadAlwaysRunsNormalize()
        {
            string json = @"{
  ""schemaVersion"": 2,
  ""pages"": [ { ""kind"": ""hostedPage"", ""id"": ""p-a"" } ],
  ""priority"": {
    ""rows"": [
      { ""kind"": ""seat"", ""id"": ""s1"", ""target"": { ""kind"": ""hostedPage"", ""id"": ""nope"" } }
    ]
  }
}";
            var cfg = Load(json);
            Assert.True(cfg.Priority.Rows[0].DegradedAtLoad);
            Assert.Contains(cfg.Priority.EffectiveRows, r => r.Kind == PriorityRowKind.Manual);
        }

        [Fact]
        public void Meta_Preservation_DegradedDocument_SaveByteIdentical()
        {
            // Build via objects, save once to get canonical form, load+save must match.
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "s1",
                Target = Hosted("missing"),
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "e1",
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.BuiltIn,
                                Name = "NotReal",
                            },
                            Operator = ConditionOperator.IsTrue,
                        },
                        Lifetime = new Lifetime
                        {
                            Kind = LifetimeKind.OnChange,
                            DirectionRaw = "sideways",
                            ThenRaw = "nope",
                            DurationMs = 1234,
                        },
                    },
                },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });

            string canonical = Save(cfg);
            var loaded = Load(canonical);
            string resaved = Save(loaded);
            AssertUtf8Equal(canonical, resaved);
        }

        [Fact]
        public void Meta_FullyPopulatedGolden_StillRoundTrips()
        {
            var cfg = Schema2FrozenV2Tests.BuildFullyPopulated();
            string a = Save(cfg);
            string b = Save(Load(a));
            AssertUtf8Equal(a, b);
        }

        [Fact]
        public void EffectiveRows_FallsBackToStoredRows_BeforeNormalize()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Assert.Null(cfg.Priority.RuntimeRows);
            Assert.Same(cfg.Priority.Rows, cfg.Priority.EffectiveRows);
            Assert.Single(cfg.Priority.EffectiveRows);
        }

        // ── F2 / F6 ordering permutations ─────────────────────────────────

        private static LayerEntry FlaggedLayer(string id)
            => new LayerEntry
            {
                Id = id,
                ActsAsEntrypoint = true,
                Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                Condition = LevelCondition(),
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
            };

        private static FieldOverride FlaggedOverride(string id)
            => new FieldOverride
            {
                Id = id,
                ActsAsEntrypoint = true,
                Writes = FieldWrites.Value,
                Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                Condition = LevelCondition(),
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
            };

        [Fact]
        public void Cardinality_MaterializeSeat_ManualAtIndex0()
        {
            var cfg = new DisplayConfigV2();
            cfg.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.HostedPage, Id = "p-a",
                Layers = new List<LayerEntry> { FlaggedLayer("l1") },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            var rt = cfg.Priority.EffectiveRows;
            Assert.Equal(2, rt.Count);
            Assert.True(rt[0].MaterializedAtLoad);
            Assert.Equal("p-a", rt[0].Target.Id);
            Assert.Equal(PriorityRowKind.Manual, rt[1].Kind);
            Assert.False(rt[1].MaterializedAtLoad);
        }

        [Fact]
        public void Cardinality_MaterializeSeat_ManualAtMiddle()
        {
            var cfg = new DisplayConfigV2();
            cfg.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.HostedPage, Id = "p-flag",
                Layers = new List<LayerEntry> { FlaggedLayer("l1") },
            });
            cfg.Pages.Add(new PageEntry { Kind = PageEntryKind.HostedPage, Id = "p-seat" });
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat, Id = "s-top", Target = Hosted("p-seat"),
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat, Id = "s-bot", Target = Hosted("p-seat"),
            });
            // s-bot is duplicate home — degraded, but still occupies a document slot below manual.
            Norm(cfg);

            // Storage unchanged.
            Assert.Equal(3, cfg.Priority.Rows.Count);
            Assert.Equal(PriorityRowKind.Manual, cfg.Priority.Rows[1].Kind);

            var rt = cfg.Priority.EffectiveRows;
            // s-top, [materialized p-flag], manual, s-bot
            Assert.Equal(4, rt.Count);
            Assert.Equal("s-top", rt[0].Id);
            Assert.True(rt[1].MaterializedAtLoad);
            Assert.Equal("p-flag", rt[1].Target.Id);
            Assert.Equal(PriorityRowKind.Manual, rt[2].Kind);
            Assert.Equal("s-bot", rt[3].Id);
        }

        [Fact]
        public void Cardinality_RestoredManual_PlusMaterialization()
        {
            var cfg = new DisplayConfigV2();
            cfg.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.HostedPage, Id = "p-a",
                Layers = new List<LayerEntry> { FlaggedLayer("l1") },
            });
            // No manual in document.
            Norm(cfg);
            Assert.Empty(cfg.Priority.Rows);
            var rt = cfg.Priority.EffectiveRows;
            Assert.Equal(2, rt.Count);
            Assert.True(rt[0].MaterializedAtLoad);
            Assert.Equal("p-a", rt[0].Target.Id);
            Assert.Equal(PriorityRowKind.Manual, rt[1].Kind);
            Assert.True(rt[1].MaterializedAtLoad);
        }

        [Fact]
        public void Cardinality_FieldEncounterOrder_ReversedNumericKeys()
        {
            // Catalog with two primary hosts so field overrides materialize ITM seats.
            var cat = new WheelCatalog
            {
                WheelId = "test",
                Itm = new ItmCatalogSection
                {
                    Fields =
                    {
                        new CatalogFieldDefinition
                        {
                            Id = "f42", ParamId = 42,
                            Suffix = new FieldSuffixCapability { Supported = true },
                            Value = new FieldValueCapability { Ascii = true, Numeric = true },
                        },
                        new CatalogFieldDefinition
                        {
                            Id = "f5", ParamId = 5,
                            Suffix = new FieldSuffixCapability { Supported = true },
                            Value = new FieldValueCapability { Ascii = true, Numeric = true },
                        },
                    },
                    Pages =
                    {
                        new CatalogPage
                        {
                            Id = "host-42",
                            Index = 0,
                            Placements =
                            {
                                new CatalogFieldPlacement { Field = "f42", PrimaryHost = true },
                            },
                        },
                        new CatalogPage
                        {
                            Id = "host-5",
                            Index = 1,
                            Placements =
                            {
                                new CatalogFieldPlacement { Field = "f5", PrimaryHost = true },
                            },
                        },
                    },
                },
            };

            var cfg = new DisplayConfigV2();
            // Encounter order: 42 then 5 (reversed relative to numeric sort).
            cfg.Fields[42] = new FieldEntry
            {
                Overrides = new List<FieldOverride> { FlaggedOverride("o-42") },
            };
            cfg.Fields[5] = new FieldEntry
            {
                Overrides = new List<FieldOverride> { FlaggedOverride("o-5") },
            };
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });

            // Confirm dictionary enumeration is encounter order (not numeric).
            Assert.Equal(new ushort[] { 42, 5 }, cfg.Fields.Keys.ToArray());

            Norm(cfg, catalog: cat);
            var mats = cfg.Priority.EffectiveRows.Where(r => r.MaterializedAtLoad).ToList();
            Assert.Equal(2, mats.Count);
            Assert.Equal("host-42", mats[0].Target.CatalogPageId);
            Assert.Equal("host-5", mats[1].Target.CatalogPageId);
            // Numeric sort would have produced host-5 first — prove we did not.
            Assert.NotEqual("host-5", mats[0].Target.CatalogPageId);
        }

        [Fact]
        public void Cardinality_MixedPagesThenFields_EncounterOrder()
        {
            var cat = new WheelCatalog
            {
                WheelId = "test",
                Itm = new ItmCatalogSection
                {
                    Fields =
                    {
                        new CatalogFieldDefinition
                        {
                            Id = "f9", ParamId = 9,
                            Suffix = new FieldSuffixCapability { Supported = true },
                            Value = new FieldValueCapability { Ascii = true, Numeric = true },
                        },
                    },
                    Pages =
                    {
                        new CatalogPage
                        {
                            Id = "host-f",
                            Index = 0,
                            Placements =
                            {
                                new CatalogFieldPlacement { Field = "f9", PrimaryHost = true },
                            },
                        },
                    },
                },
            };

            var cfg = new DisplayConfigV2();
            cfg.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.HostedPage, Id = "p-page",
                Layers = new List<LayerEntry> { FlaggedLayer("l-page") },
            });
            cfg.Fields[9] = new FieldEntry
            {
                Overrides = new List<FieldOverride> { FlaggedOverride("o-f") },
            };
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg, catalog: cat);

            var mats = cfg.Priority.EffectiveRows.Where(r => r.MaterializedAtLoad).ToList();
            Assert.Equal(2, mats.Count);
            Assert.Equal("p-page", mats[0].Target.Id);
            Assert.Equal("host-f", mats[1].Target.CatalogPageId);
        }

        // ── F3 catalog-backed ITM resolution ──────────────────────────────

        private static WheelCatalog CatalogWithPages(params string[] pageIds)
        {
            var cat = new WheelCatalog
            {
                WheelId = "test",
                Itm = new ItmCatalogSection(),
                ScreenCommands = new ScreenCommandsCapability
                {
                    Logo = true, Blank = true, White = true, LogoInverted = true,
                },
            };
            for (int i = 0; i < pageIds.Length; i++)
            {
                cat.Itm.Pages.Add(new CatalogPage { Id = pageIds[i], Index = i });
            }
            return cat;
        }

        [Fact]
        public void Ref_Catalog_ItmOverlayCannotMintIdentity()
        {
            var cat = CatalogWithPages("lapInfo"); // no "future"
            var cfg = new DisplayConfigV2();
            cfg.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.ItmPage, CatalogPageId = "future",
            });
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat, Id = "s1", Target = Itm("future"),
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg, catalog: cat);
            Assert.True(cfg.Pages[0].DegradedAtLoad); // overlay unknown
            Assert.True(cfg.Priority.Rows[0].DegradedAtLoad); // seat target unresolved
            Assert.True(cfg.Priority.Rows[0].Target.DegradedAtLoad);
        }

        [Fact]
        public void Ref_Catalog_SeatTarget_UnresolvedItm()
        {
            var cat = CatalogWithPages("lapInfo");
            var cfg = new DisplayConfigV2();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat, Id = "s1", Target = Itm("nope"),
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg, catalog: cat);
            Assert.True(cfg.Priority.Rows[0].DegradedAtLoad);
            Assert.True(cfg.Priority.Rows[0].Target.DegradedAtLoad);
        }

        [Fact]
        public void Ref_Catalog_SummonSatelliteTarget_UnresolvedItm()
        {
            var cat = CatalogWithPages("lapInfo");
            var cfg = new DisplayConfigV2();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Satellite,
                Id = "sat1",
                Target = Itm("nope"),
                Summons = new List<Summon> { OkSummon("e1") },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg, catalog: cat);
            Assert.True(cfg.Priority.Rows[0].DegradedAtLoad);
            Assert.True(cfg.Priority.Rows[0].Target.DegradedAtLoad);
        }

        [Fact]
        public void Ref_Catalog_CycleMember_UnresolvedItm()
        {
            var cat = CatalogWithPages("lapInfo");
            var cfg = new DisplayConfigV2();
            cfg.Cycles.Add(new CycleEntry
            {
                Id = "c1",
                Members = new List<PageRef> { Itm("lapInfo"), Itm("nope") },
            });
            Norm(cfg, catalog: cat);
            Assert.True(cfg.Cycles[0].Members[1].DegradedAtLoad);
            Assert.True(cfg.Cycles[0].DegradedAtLoad); // <2 resolvable
        }

        [Fact]
        public void Ref_Catalog_PageOrder_UnresolvedItm()
        {
            var cat = CatalogWithPages("lapInfo");
            var cfg = new DisplayConfigV2();
            cfg.PageOrder = new List<PageRef> { Itm("lapInfo"), Itm("nope") };
            Norm(cfg, catalog: cat);
            Assert.False(cfg.PageOrder[0].DegradedAtLoad);
            Assert.True(cfg.PageOrder[1].DegradedAtLoad);
        }

        [Fact]
        public void Ref_Catalog_RestInSessionPage_UnresolvedItm()
        {
            var cat = CatalogWithPages("lapInfo");
            var cfg = new DisplayConfigV2();
            cfg.Priority.Rest.InSessionPage = Itm("nope");
            Norm(cfg, catalog: cat);
            Assert.True(cfg.Priority.Rest.InSessionPage.DegradedAtLoad);
            Assert.True(cfg.Priority.Rest.InSessionPageUseDefaultWalk);
        }

        [Fact]
        public void Ref_Catalog_RestIdlePage_UnresolvedItm()
        {
            var cat = CatalogWithPages("lapInfo");
            var cfg = new DisplayConfigV2();
            cfg.Priority.Rest.Idle = new IdleSpec
            {
                Kind = IdleKind.Page,
                Page = Itm("nope"),
            };
            Norm(cfg, catalog: cat);
            Assert.True(cfg.Priority.Rest.Idle.DegradedAtLoad);
            Assert.True(cfg.Priority.Rest.Idle.Page.DegradedAtLoad);
        }

        [Fact]
        public void Ref_RestIdlePage_Cycle_FallbackBlank()
        {
            var cfg = DocWithHosted();
            cfg.Cycles.Add(new CycleEntry
            {
                Id = "c1",
                Members = new List<PageRef> { Hosted("p-a"), Hosted("p-a") },
            });
            cfg.Priority.Rest.Idle = new IdleSpec
            {
                Kind = IdleKind.Page,
                Page = CycleRef("c1"),
            };
            Norm(cfg);
            Assert.True(cfg.Priority.Rest.Idle.DegradedAtLoad);
            Assert.True(cfg.Priority.Rest.Idle.Page.DegradedAtLoad);
        }

        [Fact]
        public void Ref_SummonSatelliteTarget_UnresolvedHosted()
        {
            var cfg = DocWithHosted();
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Satellite,
                Id = "sat1",
                Target = Hosted("missing"),
                Summons = new List<Summon> { OkSummon("e1") },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.True(cfg.Priority.Rows[0].DegradedAtLoad);
            Assert.True(cfg.Priority.Rows[0].Target.DegradedAtLoad);
        }

        // ── F5 tri-state null capabilities ────────────────────────────────

        [Fact]
        public void Capability_Suffix_NullCapability_WarnsButDoesNotGate()
        {
            var cat = CatalogWithField(1, suffixSupported: null);
            var log = new List<string>();
            var cfg = new DisplayConfigV2();
            cfg.Fields[1] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "o1",
                        Writes = FieldWrites.Suffix,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            };
            Norm(cfg, log, cat);
            Assert.False(cfg.Fields[1].Overrides[0].DegradedAtLoad);
            Assert.Contains(log, m => m.IndexOf("untested", StringComparison.OrdinalIgnoreCase) >= 0
                && m.IndexOf("suffix", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void Capability_ValueAscii_NullCapability_WarnsButDoesNotGate()
        {
            var cat = CatalogWithField(1, valueAscii: null);
            var log = new List<string>();
            var cfg = new DisplayConfigV2();
            cfg.Fields[1] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "o1",
                        Writes = FieldWrites.Value,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "AB" },
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            };
            Norm(cfg, log, cat);
            Assert.False(cfg.Fields[1].Overrides[0].DegradedAtLoad);
            Assert.Contains(log, m => m.IndexOf("untested", StringComparison.OrdinalIgnoreCase) >= 0
                && m.IndexOf("ascii", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void Capability_IdleScreen_NullCapability_WarnsButDoesNotGate()
        {
            var cat = CatalogWithField(1, logoSupported: null);
            var log = new List<string>();
            var cfg = new DisplayConfigV2();
            cfg.Priority.Rest.Idle = new IdleSpec
            {
                Kind = IdleKind.Screen,
                Screen = WheelScreenCommand.Logo,
            };
            Norm(cfg, log, cat);
            Assert.False(cfg.Priority.Rest.Idle.DegradedAtLoad);
            Assert.Contains(log, m => m.IndexOf("untested", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void Capability_IdleScreen_Unsupported_Degraded()
        {
            var cat = CatalogWithField(1, logoSupported: false);
            var cfg = new DisplayConfigV2();
            cfg.Priority.Rest.Idle = new IdleSpec
            {
                Kind = IdleKind.Screen,
                Screen = WheelScreenCommand.Logo,
            };
            Norm(cfg, catalog: cat);
            Assert.True(cfg.Priority.Rest.Idle.DegradedAtLoad);
            Assert.True(cfg.Priority.Rest.Idle.ScreenIgnored);
        }

        // ── F6 catalog immutability ───────────────────────────────────────

        [Fact]
        public void Meta_Catalog_ImmutableAcrossNormalize()
        {
            var cat = CatalogWithField(1, suffixSupported: true, valueAscii: true);
            cat.CatalogVersion = 7;
            cat.DisplayName = "Probe";
            cat.Provisional = true;
            cat.Itm.LegacyPageIndex = 6;
            cat.Itm.Pages[0].Name = "Page0";
            cat.Itm.Pages[0].Provisional = true;
            cat.Itm.Fields[0].ShortCode = "SC";
            cat.Itm.Fields[0].DisplayLabel = "Label";
            cat.ScreenCommands.White = false;

            string before = JsonConvert.SerializeObject(cat, Formatting.None);

            var cfg = new DisplayConfigV2();
            cfg.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.ItmPage, CatalogPageId = "future-overlay",
            });
            cfg.Fields[1] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "o1",
                        Writes = FieldWrites.Suffix,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            };
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat, Id = "s1", Target = Itm("nope"),
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            cfg.Priority.Rest.Idle = new IdleSpec
            {
                Kind = IdleKind.Screen, Screen = WheelScreenCommand.Logo,
            };
            cfg.WheelScreen.Rules.Add(new WheelScreenRule
            {
                Id = "w1",
                Screen = WheelScreenCommand.White,
                Condition = LevelCondition(),
                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
            });

            Norm(cfg, catalog: cat);
            string after = JsonConvert.SerializeObject(cat, Formatting.None);
            Assert.Equal(before, after);
        }

        // ── Freeze-round fixes (FZ-002 … FZ-011) ─────────────────────────

        [Theory]
        [InlineData("summon")]
        [InlineData("override")]
        [InlineData("layer")]
        [InlineData("fieldBase")]
        [InlineData("content")]
        [InlineData("wheelRule")]
        public void FZ002_ScriptSource_ParsedButInert_OnEveryCarrier(string site)
        {
            var cfg = DocWithHosted();
            var script = new ValueSource { Kind = ValueSourceKind.Script, Name = "myScript" };

            switch (site)
            {
                case "summon":
                    cfg.Priority.Rows.Add(new PriorityRow
                    {
                        Kind = PriorityRowKind.Seat,
                        Id = "s1",
                        Target = Hosted("p-a"),
                        Summons = new List<Summon>
                        {
                            new Summon
                            {
                                Id = "e1",
                                Condition = new Condition
                                {
                                    Source = script,
                                    Operator = ConditionOperator.IsTrue,
                                },
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                        },
                    });
                    cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
                    Norm(cfg);
                    Assert.True(script.DegradedAtLoad);
                    Assert.True(cfg.Priority.Rows[0].Summons[0].DegradedAtLoad);
                    Assert.False(cfg.Priority.Rows[0].Summons[0].EffectivelyEnabled);
                    Assert.Equal("script", script.KindRaw);
                    break;

                case "override":
                    cfg.Fields[1] = new FieldEntry
                    {
                        Overrides = new List<FieldOverride>
                        {
                            new FieldOverride
                            {
                                Id = "o1",
                                Writes = FieldWrites.Value,
                                Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                                Condition = new Condition
                                {
                                    Source = script,
                                    Operator = ConditionOperator.IsTrue,
                                },
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                        },
                    };
                    Norm(cfg);
                    Assert.True(script.DegradedAtLoad);
                    Assert.True(cfg.Fields[1].Overrides[0].DegradedAtLoad);
                    Assert.Equal("script", script.KindRaw);
                    break;

                case "layer":
                    cfg.Pages[0].Layers = new List<LayerEntry>
                    {
                        new LayerEntry
                        {
                            Id = "l1",
                            Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                            Condition = new Condition
                            {
                                Source = script,
                                Operator = ConditionOperator.IsTrue,
                            },
                            Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                        },
                    };
                    Norm(cfg);
                    Assert.True(script.DegradedAtLoad);
                    Assert.True(cfg.Pages[0].Layers[0].DegradedAtLoad);
                    Assert.Equal("script", script.KindRaw);
                    break;

                case "fieldBase":
                    cfg.Fields[1] = new FieldEntry { Base = new FieldBase { Source = script } };
                    Norm(cfg);
                    Assert.True(script.DegradedAtLoad);
                    Assert.Equal("script", script.KindRaw);
                    break;

                case "content":
                    cfg.Pages[0].Base = new ContentWithEffect
                    {
                        Content = new ContentObject
                        {
                            Kind = ContentKind.Property,
                            Source = script,
                        },
                    };
                    Norm(cfg);
                    Assert.True(script.DegradedAtLoad);
                    Assert.True(cfg.Pages[0].Base.Content.DegradedAtLoad);
                    Assert.Equal("script", script.KindRaw);
                    break;

                case "wheelRule":
                    cfg.WheelScreen.Rules.Add(new WheelScreenRule
                    {
                        Id = "w1",
                        Screen = WheelScreenCommand.Logo,
                        Condition = new Condition
                        {
                            Source = script,
                            Operator = ConditionOperator.IsTrue,
                        },
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    });
                    Norm(cfg);
                    Assert.True(script.DegradedAtLoad);
                    Assert.True(cfg.WheelScreen.Rules[0].DegradedAtLoad);
                    Assert.Equal("script", script.KindRaw);
                    break;
            }
        }

        [Fact]
        public void FZ003_PageOrder_AbsentVsEmpty_AreDifferentStates()
        {
            // Absent → null on POCO; not emitted on save.
            var absent = Load(@"{ ""schemaVersion"": 2 }");
            Assert.Null(absent.PageOrder);
            string absentSaved = Save(absent);
            Assert.DoesNotContain("pageOrder", absentSaved);

            // Explicit [] → empty list; emitted on save; round-trips.
            var empty = Load(@"{ ""schemaVersion"": 2, ""pageOrder"": [] }");
            Assert.NotNull(empty.PageOrder);
            Assert.Empty(empty.PageOrder);
            string emptySaved = Save(empty);
            Assert.Contains("\"pageOrder\": []", emptySaved.Replace("\r\n", "\n"));
            var empty2 = Load(emptySaved);
            Assert.NotNull(empty2.PageOrder);
            Assert.Empty(empty2.PageOrder);
        }

        [Fact]
        public void FZ005_ThenPlusDurationMs5000_DegradesAndPreserves()
        {
            string json = @"{
  ""schemaVersion"": 2,
  ""pages"": [ { ""kind"": ""hostedPage"", ""id"": ""p-a"", ""name"": ""A"" } ],
  ""priority"": {
    ""rows"": [
      {
        ""kind"": ""seat"",
        ""id"": ""s1"",
        ""target"": { ""kind"": ""hostedPage"", ""id"": ""p-a"" },
        ""summons"": [ {
          ""id"": ""e1"",
          ""condition"": {
            ""source"": { ""kind"": ""builtIn"", ""name"": ""BrakeBias"" }
          },
          ""lifetime"": {
            ""kind"": ""onChange"",
            ""then"": ""untilDismissed"",
            ""durationMs"": 5000
          }
        } ]
      },
      { ""kind"": ""manual"" }
    ]
  }
}";
            var cfg = Load(json);
            var life = cfg.Priority.Rows[0].Summons[0].Lifetime;
            Assert.True(life.DurationMsPresent);
            Assert.Equal(5000, life.DurationMs);
            Assert.True(life.DurationMsIgnored);
            Assert.True(cfg.Priority.Rows[0].Summons[0].DegradedAtLoad);

            // Authored durationMs preserved on save (never rewritten / suppressed).
            string saved = Save(cfg);
            Assert.Contains("\"durationMs\": 5000", saved);
            var again = Load(saved);
            Assert.True(again.Priority.Rows[0].Summons[0].Lifetime.DurationMsPresent);
            Assert.Equal(5000, again.Priority.Rows[0].Summons[0].Lifetime.DurationMs);
        }

        // FA3: FZ006 rest.landingPage rule DELETED with the member (FREEZE AMENDMENT 3).

        // FA2: FZ007 reserved hosted-page-prefix rule DELETED with the v1→v2 converter.

        public static IEnumerable<object[]> UnknownEnumCarrierCases()
        {
            // One unknown-spelling case per enum-carrier pair (FZ-008).
            yield return new object[] { "runs.summon", "runs", "futureMode" };
            yield return new object[] { "runs.override", "runs", "futureMode" };
            yield return new object[] { "runs.layer", "runs", "futureMode" };
            yield return new object[] { "runs.wheelRule", "runs", "futureMode" };
            yield return new object[] { "content.kind", "contentKind", "contentKindX" };
            yield return new object[] { "effect.base", "effect", "sparkle" };
            yield return new object[] { "effect.override", "effect", "sparkle" };
            yield return new object[] { "effect.layer", "effect", "sparkle" };
            yield return new object[] { "writes", "writes", "writesX" };
            yield return new object[] { "alignment", "alignment", "leftish" };
            yield return new object[] { "settings.mode", "mode", "telepathy" };
            yield return new object[] { "idle.kind", "idleKind", "idleKindX" };
            yield return new object[] { "idle.screen", "idleScreen", "holodeck" };
            yield return new object[] { "direction", "direction", "sideways" };
            yield return new object[] { "then", "then", "forAWhile" };
            yield return new object[] { "row.kind", "rowKind", "rowKindX" };
            yield return new object[] { "pageRef.kind", "pageRefKind", "pageRefX" };
            yield return new object[] { "source.kind", "sourceKind", "sourceKindX" };
            yield return new object[] { "operator", "operator", "sparkles" };
            yield return new object[] { "page.kind", "pageKind", "pageKindX" };
            yield return new object[] { "lifetime.kind", "lifetimeKind", "untilTomorrow" };
            yield return new object[] { "wheel.screen", "wheelScreen", "holodeck" };
        }

        [Theory]
        [MemberData(nameof(UnknownEnumCarrierCases))]
        public void FZ008_UnknownEnumSpelling_DegradesCarrier(string caseId, string family, string spelling)
        {
            _ = family; // documented grouping only
            DisplayConfigV2 cfg;
            Action assertDegraded;

            switch (caseId)
            {
                case "runs.summon":
                    cfg = DocWithHosted();
                    cfg.Priority.Rows.Add(new PriorityRow
                    {
                        Kind = PriorityRowKind.Seat,
                        Id = "s1",
                        Target = Hosted("p-a"),
                        Summons = new List<Summon>
                        {
                            new Summon
                            {
                                Id = "e1",
                                RunsRaw = spelling,
                                Condition = LevelCondition(),
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                        },
                    });
                    cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Priority.Rows[0].Summons[0].DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Priority.Rows[0].Summons[0].RunsRaw);
                        Assert.Equal(RunsWhen.Unknown, cfg.Priority.Rows[0].Summons[0].Runs);
                    };
                    break;

                case "runs.override":
                    cfg = new DisplayConfigV2();
                    cfg.Fields[1] = new FieldEntry
                    {
                        Overrides = new List<FieldOverride>
                        {
                            new FieldOverride
                            {
                                Id = "o1",
                                Writes = FieldWrites.Value,
                                RunsRaw = spelling,
                                Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                                Condition = LevelCondition(),
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                        },
                    };
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Fields[1].Overrides[0].DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Fields[1].Overrides[0].RunsRaw);
                    };
                    break;

                case "runs.layer":
                    cfg = DocWithHosted();
                    cfg.Pages[0].Layers = new List<LayerEntry>
                    {
                        new LayerEntry
                        {
                            Id = "l1",
                            RunsRaw = spelling,
                            Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                            Condition = LevelCondition(),
                            Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                        },
                    };
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Pages[0].Layers[0].DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Pages[0].Layers[0].RunsRaw);
                    };
                    break;

                case "runs.wheelRule":
                    cfg = new DisplayConfigV2();
                    cfg.WheelScreen.Rules.Add(new WheelScreenRule
                    {
                        Id = "w1",
                        Screen = WheelScreenCommand.Logo,
                        RunsRaw = spelling,
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    });
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.WheelScreen.Rules[0].DegradedAtLoad);
                        Assert.Equal(spelling, cfg.WheelScreen.Rules[0].RunsRaw);
                    };
                    break;

                case "content.kind":
                    cfg = DocWithHosted();
                    cfg.Pages[0].Base = new ContentWithEffect
                    {
                        Content = new ContentObject { KindRaw = spelling },
                    };
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Pages[0].Base.Content.DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Pages[0].Base.Content.KindRaw);
                        Assert.Equal(ContentKind.Unknown, cfg.Pages[0].Base.Content.Kind);
                    };
                    break;

                case "effect.base":
                    cfg = DocWithHosted();
                    cfg.Pages[0].Base = new ContentWithEffect
                    {
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                        EffectRaw = spelling,
                    };
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Pages[0].Base.DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Pages[0].Base.EffectRaw);
                        Assert.Equal(ContentEffect.Unknown, cfg.Pages[0].Base.Effect);
                    };
                    break;

                case "effect.override":
                    cfg = new DisplayConfigV2();
                    cfg.Fields[1] = new FieldEntry
                    {
                        Overrides = new List<FieldOverride>
                        {
                            new FieldOverride
                            {
                                Id = "o1",
                                Writes = FieldWrites.Value,
                                EffectRaw = spelling,
                                Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                                Condition = LevelCondition(),
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                        },
                    };
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Fields[1].Overrides[0].DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Fields[1].Overrides[0].EffectRaw);
                    };
                    break;

                case "effect.layer":
                    cfg = DocWithHosted();
                    cfg.Pages[0].Layers = new List<LayerEntry>
                    {
                        new LayerEntry
                        {
                            Id = "l1",
                            EffectRaw = spelling,
                            Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                            Condition = LevelCondition(),
                            Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                        },
                    };
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Pages[0].Layers[0].DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Pages[0].Layers[0].EffectRaw);
                    };
                    break;

                case "writes":
                    cfg = new DisplayConfigV2();
                    cfg.Fields[1] = new FieldEntry
                    {
                        Overrides = new List<FieldOverride>
                        {
                            new FieldOverride
                            {
                                Id = "o1",
                                WritesRaw = spelling,
                                Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                                Condition = LevelCondition(),
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                        },
                    };
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Fields[1].Overrides[0].DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Fields[1].Overrides[0].WritesRaw);
                        Assert.Equal(FieldWrites.Unknown, cfg.Fields[1].Overrides[0].Writes);
                    };
                    break;

                case "alignment":
                    cfg = new DisplayConfigV2();
                    cfg.Fields[1] = new FieldEntry
                    {
                        Overrides = new List<FieldOverride>
                        {
                            new FieldOverride
                            {
                                Id = "o1",
                                Writes = FieldWrites.Value,
                                AlignmentRaw = spelling,
                                Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                                Condition = LevelCondition(),
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                        },
                    };
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Fields[1].Overrides[0].DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Fields[1].Overrides[0].AlignmentRaw);
                        Assert.Equal(FieldAlignment.Unknown, cfg.Fields[1].Overrides[0].Alignment);
                    };
                    break;

                case "settings.mode":
                    cfg = new DisplayConfigV2 { Settings = new SettingsBlock { ModeRaw = spelling } };
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Settings.DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Settings.ModeRaw);
                        Assert.Equal(SettingsMode.Unknown, cfg.Settings.Mode);
                    };
                    break;

                case "idle.kind":
                    cfg = new DisplayConfigV2
                    {
                        Priority = new PriorityLadder
                        {
                            Rest = new RestBlock { Idle = new IdleSpec { KindRaw = spelling } },
                        },
                    };
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Priority.Rest.Idle.DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Priority.Rest.Idle.KindRaw);
                    };
                    break;

                case "idle.screen":
                    cfg = new DisplayConfigV2
                    {
                        Priority = new PriorityLadder
                        {
                            Rest = new RestBlock
                            {
                                Idle = new IdleSpec
                                {
                                    Kind = IdleKind.Screen,
                                    ScreenRaw = spelling,
                                },
                            },
                        },
                    };
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Priority.Rest.Idle.DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Priority.Rest.Idle.ScreenRaw);
                    };
                    break;

                case "direction":
                    cfg = DocWithHosted();
                    cfg.Priority.Rows.Add(new PriorityRow
                    {
                        Kind = PriorityRowKind.Seat,
                        Id = "s1",
                        Target = Hosted("p-a"),
                        Summons = new List<Summon>
                        {
                            new Summon
                            {
                                Id = "e1",
                                Condition = new Condition
                                {
                                    Source = new ValueSource
                                    {
                                        Kind = ValueSourceKind.BuiltIn,
                                        Name = "BrakeBias",
                                    },
                                },
                                Lifetime = new Lifetime
                                {
                                    Kind = LifetimeKind.OnChange,
                                    DirectionRaw = spelling,
                                },
                            },
                        },
                    });
                    cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Priority.Rows[0].Summons[0].DegradedAtLoad);
                        Assert.True(cfg.Priority.Rows[0].Summons[0].Lifetime.DirectionCoercedToAny);
                        Assert.Equal(spelling, cfg.Priority.Rows[0].Summons[0].Lifetime.DirectionRaw);
                    };
                    break;

                case "then":
                    cfg = DocWithHosted();
                    cfg.Priority.Rows.Add(new PriorityRow
                    {
                        Kind = PriorityRowKind.Seat,
                        Id = "s1",
                        Target = Hosted("p-a"),
                        Summons = new List<Summon>
                        {
                            new Summon
                            {
                                Id = "e1",
                                Condition = new Condition
                                {
                                    Source = new ValueSource
                                    {
                                        Kind = ValueSourceKind.BuiltIn,
                                        Name = "BrakeBias",
                                    },
                                },
                                Lifetime = new Lifetime
                                {
                                    Kind = LifetimeKind.OnChange,
                                    ThenRaw = spelling,
                                },
                            },
                        },
                    });
                    cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Priority.Rows[0].Summons[0].DegradedAtLoad);
                        Assert.True(cfg.Priority.Rows[0].Summons[0].Lifetime.ThenIgnored);
                        Assert.Equal(spelling, cfg.Priority.Rows[0].Summons[0].Lifetime.ThenRaw);
                    };
                    break;

                case "row.kind":
                    cfg = new DisplayConfigV2();
                    cfg.Priority.Rows.Add(new PriorityRow { KindRaw = spelling, Id = "x" });
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Priority.Rows[0].DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Priority.Rows[0].KindRaw);
                    };
                    break;

                case "pageRef.kind":
                    cfg = DocWithHosted();
                    cfg.PageOrder = new List<PageRef>
                    {
                        new PageRef { KindRaw = spelling, Id = "x" },
                    };
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.PageOrder[0].DegradedAtLoad);
                        Assert.Equal(spelling, cfg.PageOrder[0].KindRaw);
                    };
                    break;

                case "source.kind":
                    cfg = DocWithHosted();
                    cfg.Priority.Rows.Add(new PriorityRow
                    {
                        Kind = PriorityRowKind.Seat,
                        Id = "s1",
                        Target = Hosted("p-a"),
                        Summons = new List<Summon>
                        {
                            new Summon
                            {
                                Id = "e1",
                                Condition = new Condition
                                {
                                    Source = new ValueSource { KindRaw = spelling, Name = "n" },
                                    Operator = ConditionOperator.IsTrue,
                                },
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                        },
                    });
                    cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Priority.Rows[0].Summons[0].DegradedAtLoad);
                        Assert.True(cfg.Priority.Rows[0].Summons[0].Condition.Source.DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Priority.Rows[0].Summons[0].Condition.Source.KindRaw);
                    };
                    break;

                case "operator":
                    cfg = DocWithHosted();
                    cfg.Priority.Rows.Add(new PriorityRow
                    {
                        Kind = PriorityRowKind.Seat,
                        Id = "s1",
                        Target = Hosted("p-a"),
                        Summons = new List<Summon>
                        {
                            new Summon
                            {
                                Id = "e1",
                                Condition = new Condition
                                {
                                    Source = new ValueSource
                                    {
                                        Kind = ValueSourceKind.BuiltIn,
                                        Name = "FuelPercent",
                                    },
                                    OperatorRaw = spelling,
                                    Value = 1,
                                },
                                Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                            },
                        },
                    });
                    cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Priority.Rows[0].Summons[0].DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Priority.Rows[0].Summons[0].Condition.OperatorRaw);
                    };
                    break;

                case "page.kind":
                    cfg = new DisplayConfigV2();
                    cfg.Pages.Add(new PageEntry { KindRaw = spelling, Id = "x" });
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Pages[0].DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Pages[0].KindRaw);
                    };
                    break;

                case "lifetime.kind":
                    cfg = DocWithHosted();
                    cfg.Priority.Rows.Add(new PriorityRow
                    {
                        Kind = PriorityRowKind.Seat,
                        Id = "s1",
                        Target = Hosted("p-a"),
                        Summons = new List<Summon>
                        {
                            new Summon
                            {
                                Id = "e1",
                                Condition = LevelCondition(),
                                Lifetime = new Lifetime { KindRaw = spelling },
                            },
                        },
                    });
                    cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.Priority.Rows[0].Summons[0].DegradedAtLoad);
                        Assert.Equal(spelling, cfg.Priority.Rows[0].Summons[0].Lifetime.KindRaw);
                    };
                    break;

                case "wheel.screen":
                    cfg = new DisplayConfigV2();
                    cfg.WheelScreen.Rules.Add(new WheelScreenRule
                    {
                        Id = "w1",
                        ScreenRaw = spelling,
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    });
                    assertDegraded = () =>
                    {
                        Assert.True(cfg.WheelScreen.Rules[0].DegradedAtLoad);
                        Assert.Equal(spelling, cfg.WheelScreen.Rules[0].ScreenRaw);
                    };
                    break;

                default:
                    throw new InvalidOperationException("unknown case " + caseId);
            }

            Norm(cfg);
            assertDegraded();
        }

        [Fact]
        public void FZ009_ChildRef_StoredTarget_IgnoredAndDegraded()
        {
            var cfg = DocWithHosted();
            cfg.Pages[0].Layers = new List<LayerEntry>
            {
                new LayerEntry
                {
                    Id = "l1",
                    ActsAsEntrypoint = true,
                    Content = new ContentObject { Kind = ContentKind.Text, Text = "A" },
                    Condition = LevelCondition(),
                    Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                },
            };
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Satellite,
                Id = "sat1",
                Target = Hosted("p-a"),
                ChildRef = new ChildRef { PageId = "p-a", LayerId = "l1" },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.True(cfg.Priority.Rows[0].TargetIgnored);
            Assert.True(cfg.Priority.Rows[0].DegradedAtLoad);
            Assert.Equal("p-a", cfg.Priority.Rows[0].Target.Id); // document preserved
        }

        [Fact]
        public void FZ009_ChildRef_BothFieldAndLayerShapes_DegradedNoSilentPreference()
        {
            var cfg = DocWithHosted();
            cfg.Fields[9] = new FieldEntry
            {
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "o1",
                        ActsAsEntrypoint = true,
                        Writes = FieldWrites.Value,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                        Condition = LevelCondition(),
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            };
            cfg.Pages[0].Layers = new List<LayerEntry>
            {
                new LayerEntry
                {
                    Id = "l1",
                    ActsAsEntrypoint = true,
                    Content = new ContentObject { Kind = ContentKind.Text, Text = "A" },
                    Condition = LevelCondition(),
                    Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                },
            };
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Satellite,
                Id = "sat1",
                ChildRef = new ChildRef
                {
                    Field = "9",
                    OverrideId = "o1",
                    PageId = "p-a",
                    LayerId = "l1",
                },
            });
            cfg.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });
            Norm(cfg);
            Assert.True(cfg.Priority.Rows[0].ChildRefAmbiguous);
            Assert.True(cfg.Priority.Rows[0].DegradedAtLoad);
        }

        [Fact]
        public void FZ010_IdleScreenBlank_Degraded()
        {
            var cfg = new DisplayConfigV2();
            cfg.Priority.Rest.Idle = new IdleSpec
            {
                Kind = IdleKind.Screen,
                Screen = WheelScreenCommand.Blank,
            };
            Norm(cfg);
            Assert.True(cfg.Priority.Rest.Idle.DegradedAtLoad);
            Assert.True(cfg.Priority.Rest.Idle.ScreenIgnored);
            Assert.Equal("blank", cfg.Priority.Rest.Idle.ScreenRaw); // raw preserved
        }
    }
}
