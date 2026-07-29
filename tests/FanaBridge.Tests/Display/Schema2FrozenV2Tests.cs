using System.Collections.Generic;
using System.Text;
using FanaBridge.Display.Schema2;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Schema-v2 golden pin (E1). Fully-populated document covering every §1–§8 member
    /// and every discriminated alternative that can coexist in one document: both page
    /// kinds, nameOverride/removed, base.effect, every row kind, cycle pageRef target,
    /// summons with name/enabled, manual returnToRestAfterMs, field baseSuffix, override
    /// effect/enabled, all three writes values, wheel-rule enabled, settings.mode, and
    /// idle screen. Idle blank/page shapes live in a second golden (one idle per doc).
    /// Changing a golden means a schema change — additive members belong at the end of
    /// the object they extend. Default-suppression assertions stay separate.
    /// </summary>
    public class Schema2FrozenV2Tests
    {
        // Formatting matches DisplayConfigV2Serializer (indented, null/default suppressed).
        private static readonly string SchemaV2Golden = @"{
  ""schemaVersion"": 2,
  ""profileId"": ""schema-v2-pin"",
  ""pages"": [
    {
      ""kind"": ""itmPage"",
      ""catalogPageId"": ""fuelErsDrs"",
      ""nameOverride"": ""Fuel pack"",
      ""removed"": true
    },
    {
      ""kind"": ""hostedPage"",
      ""id"": ""p-speed"",
      ""name"": ""SPEED"",
      ""base"": {
        ""content"": {
          ""kind"": ""speed""
        },
        ""effect"": ""blink""
      },
      ""layers"": [
        {
          ""id"": ""l-pit"",
          ""name"": ""PIT"",
          ""content"": {
            ""kind"": ""text"",
            ""text"": ""PIT""
          },
          ""effect"": ""blink"",
          ""condition"": {
            ""source"": {
              ""kind"": ""builtIn"",
              ""name"": ""PitLimiterOn""
            },
            ""operator"": ""isTrue""
          },
          ""lifetime"": {
            ""kind"": ""whileTrue""
          },
          ""actsAsEntrypoint"": true
        }
      ]
    },
    {
      ""kind"": ""hostedPage"",
      ""id"": ""p-alert"",
      ""name"": ""ALERT""
    }
  ],
  ""cycles"": [
    {
      ""id"": ""c-pit"",
      ""name"": ""Pit box"",
      ""members"": [
        {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""tyreTemps""
        },
        {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""fuelErsDrs""
        }
      ],
      ""periodMs"": 5000
    }
  ],
  ""priority"": {
    ""rows"": [
      {
        ""kind"": ""seat"",
        ""id"": ""s-gaps"",
        ""target"": {
          ""kind"": ""cycle"",
          ""id"": ""c-pit""
        },
        ""summons"": [
          {
            ""id"": ""e-lowfuel"",
            ""name"": ""Low fuel"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""FuelPercent""
              },
              ""operator"": ""lessThan"",
              ""value"": 10.0,
              ""hysteresis"": 2.0
            },
            ""lifetime"": {
              ""kind"": ""forDuration""
            },
            ""enabled"": false
          },
          {
            ""id"": ""e-edge"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""BrakeBias""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""durationMs"": 3000,
              ""direction"": ""up""
            },
            ""runs"": ""always""
          },
          {
            ""id"": ""e-latch"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""DrsEnabled""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""then"": ""untilDismissed""
            },
            ""runs"": ""idle""
          },
          {
            ""id"": ""e-hold"",
            ""condition"": {
              ""source"": {
                ""kind"": ""simHubProperty"",
                ""name"": ""DataCorePlugin.GameData.PitLimiterOn""
              },
              ""operator"": ""isTrue""
            },
            ""lifetime"": {
              ""kind"": ""untilDismissed""
            }
          }
        ],
        ""bringUpLifetime"": {
          ""kind"": ""whileTrue""
        }
      },
      {
        ""kind"": ""satellite"",
        ""id"": ""s-sat1"",
        ""target"": {
          ""kind"": ""hostedPage"",
          ""id"": ""p-speed""
        },
        ""summons"": [
          {
            ""id"": ""e-sat"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""IsInPitLane""
              },
              ""operator"": ""isTrue""
            },
            ""lifetime"": {
              ""kind"": ""whileTrue""
            }
          }
        ]
      },
      {
        ""kind"": ""satellite"",
        ""id"": ""s-sat2"",
        ""childRef"": {
          ""field"": ""42"",
          ""overrideId"": ""o-fl""
        },
        ""lifetime"": {
          ""kind"": ""forDuration"",
          ""durationMs"": 2000
        }
      },
      {
        ""kind"": ""manual"",
        ""returnToRestAfterMs"": 15000
      }
    ],
    ""rest"": {
      ""inSessionPage"": {
        ""kind"": ""hostedPage"",
        ""id"": ""p-speed""
      },
      ""idle"": {
        ""kind"": ""screen"",
        ""screen"": ""logo""
      }
    }
  },
  ""pageOrder"": [
    {
      ""kind"": ""itmPage"",
      ""catalogPageId"": ""lapInfo""
    },
    {
      ""kind"": ""hostedPage"",
      ""id"": ""p-speed""
    }
  ],
  ""fields"": {
    ""5"": {
      ""base"": {
        ""source"": {
          ""kind"": ""simHubProperty"",
          ""name"": ""DataCorePlugin.Computed.Fuel_RemainingLaps""
        },
        ""format"": ""bare"",
        ""baseSuffix"": ""L""
      },
      ""overrides"": [
        {
          ""id"": ""o-fn1"",
          ""writes"": ""suffix"",
          ""content"": {
            ""kind"": ""text"",
            ""text"": ""FN1""
          },
          ""effect"": ""scroll"",
          ""condition"": {
            ""source"": {
              ""kind"": ""itmField"",
              ""name"": ""self""
            },
            ""operator"": ""lessThan"",
            ""value"": 5.0
          },
          ""lifetime"": {
            ""kind"": ""whileTrue""
          },
          ""runs"": ""always"",
          ""enabled"": false,
          ""actsAsEntrypoint"": true
        },
        {
          ""id"": ""o-val"",
          ""writes"": ""value"",
          ""content"": {
            ""kind"": ""text"",
            ""text"": ""LO""
          },
          ""alignment"": ""right"",
          ""condition"": {
            ""source"": {
              ""kind"": ""builtIn"",
              ""name"": ""FuelPercent""
            },
            ""operator"": ""lessThan"",
            ""value"": 3.0
          },
          ""lifetime"": {
            ""kind"": ""forDuration"",
            ""durationMs"": 4000
          }
        },
        {
          ""id"": ""o-both"",
          ""writes"": ""both"",
          ""content"": {
            ""kind"": ""text"",
            ""text"": ""!!""
          },
          ""condition"": {
            ""source"": {
              ""kind"": ""builtIn"",
              ""name"": ""FuelPercent""
            },
            ""operator"": ""lessThan"",
            ""value"": 1.0
          },
          ""lifetime"": {
            ""kind"": ""untilDismissed""
          },
          ""actsAsEntrypoint"": true
        }
      ]
    }
  },
  ""wheelScreen"": {
    ""rules"": [
      {
        ""id"": ""w-logo60"",
        ""screen"": ""logo"",
        ""condition"": {
          ""source"": {
            ""kind"": ""builtIn"",
            ""name"": ""IsInPitLane""
          },
          ""operator"": ""isTrue""
        },
        ""lifetime"": {
          ""kind"": ""forDuration"",
          ""durationMs"": 60000
        },
        ""runs"": ""idle"",
        ""enabled"": false
      }
    ]
  },
  ""settings"": {
    ""rejectUncommandedChanges"": true,
    ""mode"": ""legacyOnly""
  }
}";

        /// <summary>Second golden family: idle blank and idle page shapes (one idle
        /// per document; empty collection members are part of the wire form).</summary>
        private static readonly string SchemaV2IdleBlankGolden = @"{
  ""schemaVersion"": 2,
  ""pages"": [],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [
      {
        ""kind"": ""manual""
      }
    ],
    ""rest"": {
      ""idle"": {
        ""kind"": ""blank""
      }
    }
  },
  ""fields"": {},
  ""wheelScreen"": {
    ""rules"": []
  },
  ""settings"": {}
}";

        private static readonly string SchemaV2IdlePageGolden = @"{
  ""schemaVersion"": 2,
  ""pages"": [],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [
      {
        ""kind"": ""manual""
      }
    ],
    ""rest"": {
      ""idle"": {
        ""kind"": ""page"",
        ""page"": {
          ""kind"": ""hostedPage"",
          ""id"": ""p-speed""
        }
      }
    }
  },
  ""fields"": {},
  ""wheelScreen"": {
    ""rules"": []
  },
  ""settings"": {}
}";

        [Fact]
        public void FullyPopulatedDocument_MatchesSchemaV2Golden()
        {
            var cfg = BuildFullyPopulated();

            // Save → Load → Save must be byte-identical to the golden (newline folding
            // is the ONE sanctioned normalization, same as S1).
            string actual = DisplayConfigV2Serializer.Save(
                DisplayConfigV2Serializer.Load(DisplayConfigV2Serializer.Save(cfg), _ => { }));

            Assert.Equal(
                Encoding.UTF8.GetBytes(SchemaV2Golden.Replace("\r\n", "\n")),
                Encoding.UTF8.GetBytes(actual.Replace("\r\n", "\n")));
        }

        [Fact]
        public void IdleBlankDocument_MatchesGolden()
        {
            var cfg = new DisplayConfigV2
            {
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        Idle = new IdleSpec { Kind = IdleKind.Blank },
                    },
                },
            };

            string actual = DisplayConfigV2Serializer.Save(
                DisplayConfigV2Serializer.Load(DisplayConfigV2Serializer.Save(cfg), _ => { }));

            Assert.Equal(
                Encoding.UTF8.GetBytes(SchemaV2IdleBlankGolden.Replace("\r\n", "\n")),
                Encoding.UTF8.GetBytes(actual.Replace("\r\n", "\n")));
        }

        [Fact]
        public void IdlePageDocument_MatchesGolden()
        {
            var cfg = new DisplayConfigV2
            {
                Priority = new PriorityLadder
                {
                    Rest = new RestBlock
                    {
                        Idle = new IdleSpec
                        {
                            Kind = IdleKind.Page,
                            Page = new PageRef { Kind = PageRefKind.HostedPage, Id = "p-speed" },
                        },
                    },
                },
            };

            string actual = DisplayConfigV2Serializer.Save(
                DisplayConfigV2Serializer.Load(DisplayConfigV2Serializer.Save(cfg), _ => { }));

            Assert.Equal(
                Encoding.UTF8.GetBytes(SchemaV2IdlePageGolden.Replace("\r\n", "\n")),
                Encoding.UTF8.GetBytes(actual.Replace("\r\n", "\n")));
        }

        /// <summary>Builds the object graph that produces <see cref="SchemaV2Golden"/>.</summary>
        internal static DisplayConfigV2 BuildFullyPopulated()
        {
            var cfg = new DisplayConfigV2
            {
                SchemaVersion = 2,
                ProfileId = "schema-v2-pin",
            };
            cfg.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.ItmPage,
                CatalogPageId = "fuelErsDrs",
                NameOverride = "Fuel pack",
                Removed = true,
            });
            cfg.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.HostedPage,
                Id = "p-speed",
                Name = "SPEED",
                Base = new ContentWithEffect
                {
                    Content = new ContentObject { Kind = ContentKind.Speed },
                    Effect = ContentEffect.Blink,
                },
                Layers = new List<LayerEntry>
                {
                    new LayerEntry
                    {
                        Id = "l-pit",
                        Name = "PIT",
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "PIT" },
                        Effect = ContentEffect.Blink,
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.BuiltIn,
                                Name = "PitLimiterOn",
                            },
                            Operator = ConditionOperator.IsTrue,
                        },
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                        ActsAsEntrypoint = true,
                    },
                },
            });
            // Blank base (absent ≡ null) — alert-style page.
            cfg.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.HostedPage,
                Id = "p-alert",
                Name = "ALERT",
            });
            cfg.Cycles.Add(new CycleEntry
            {
                Id = "c-pit",
                Name = "Pit box",
                Members = new List<PageRef>
                {
                    new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "tyreTemps" },
                    new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "fuelErsDrs" },
                },
                PeriodMs = 5000,
            });
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Seat,
                Id = "s-gaps",
                Target = new PageRef { Kind = PageRefKind.Cycle, Id = "c-pit" },
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "e-lowfuel",
                        Name = "Low fuel",
                        Enabled = false,
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.BuiltIn,
                                Name = "FuelPercent",
                            },
                            Operator = ConditionOperator.LessThan,
                            Value = 10.0,
                            Hysteresis = 2.0,
                        },
                        // DurationMs omitted → runtime default 5000 (absent, not authored).
                        Lifetime = new Lifetime
                        {
                            Kind = LifetimeKind.ForDuration,
                        },
                    },
                    new Summon
                    {
                        Id = "e-edge",
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
                            Direction = ChangeDirection.Up,
                            DurationMs = 3000,
                        },
                        Runs = RunsWhen.Always,
                    },
                    new Summon
                    {
                        Id = "e-latch",
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.BuiltIn,
                                Name = "DrsEnabled",
                            },
                        },
                        Lifetime = new Lifetime
                        {
                            Kind = LifetimeKind.OnChange,
                            Then = LifetimeThen.UntilDismissed,
                        },
                        Runs = RunsWhen.Idle,
                    },
                    new Summon
                    {
                        Id = "e-hold",
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.SimHubProperty,
                                Name = "DataCorePlugin.GameData.PitLimiterOn",
                            },
                            Operator = ConditionOperator.IsTrue,
                        },
                        Lifetime = new Lifetime { Kind = LifetimeKind.UntilDismissed },
                    },
                },
                BringUpLifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
            });
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Satellite,
                Id = "s-sat1",
                Target = new PageRef { Kind = PageRefKind.HostedPage, Id = "p-speed" },
                Summons = new List<Summon>
                {
                    new Summon
                    {
                        Id = "e-sat",
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.BuiltIn,
                                Name = "IsInPitLane",
                            },
                            Operator = ConditionOperator.IsTrue,
                        },
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                    },
                },
            });
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Satellite,
                Id = "s-sat2",
                ChildRef = new ChildRef { Field = "42", OverrideId = "o-fl" },
                Lifetime = new Lifetime { Kind = LifetimeKind.ForDuration, DurationMs = 2000 },
            });
            cfg.Priority.Rows.Add(new PriorityRow
            {
                Kind = PriorityRowKind.Manual,
                ReturnToRestAfterMs = 15000,
            });
            cfg.Priority.Rest.InSessionPage =
                new PageRef { Kind = PageRefKind.HostedPage, Id = "p-speed" };
            cfg.Priority.Rest.Idle = new IdleSpec
            {
                Kind = IdleKind.Screen,
                Screen = WheelScreenCommand.Logo,
            };
            cfg.PageOrder = new List<PageRef>
            {
                new PageRef { Kind = PageRefKind.ItmPage, CatalogPageId = "lapInfo" },
                new PageRef { Kind = PageRefKind.HostedPage, Id = "p-speed" },
            };
            cfg.Fields[5] = new FieldEntry
            {
                Base = new FieldBase
                {
                    Source = new ValueSource
                    {
                        Kind = ValueSourceKind.SimHubProperty,
                        Name = "DataCorePlugin.Computed.Fuel_RemainingLaps",
                    },
                    Format = "bare",
                    BaseSuffix = "L",
                },
                Overrides = new List<FieldOverride>
                {
                    new FieldOverride
                    {
                        Id = "o-fn1",
                        Writes = FieldWrites.Suffix,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "FN1" },
                        Effect = ContentEffect.Scroll,
                        Enabled = false,
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.ItmField,
                                Name = "self",
                            },
                            Operator = ConditionOperator.LessThan,
                            Value = 5.0,
                        },
                        Lifetime = new Lifetime { Kind = LifetimeKind.WhileTrue },
                        Runs = RunsWhen.Always,
                        ActsAsEntrypoint = true,
                    },
                    new FieldOverride
                    {
                        Id = "o-val",
                        Writes = FieldWrites.Value,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "LO" },
                        Alignment = FieldAlignment.Right,
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.BuiltIn,
                                Name = "FuelPercent",
                            },
                            Operator = ConditionOperator.LessThan,
                            Value = 3.0,
                        },
                        Lifetime = new Lifetime
                        {
                            Kind = LifetimeKind.ForDuration,
                            DurationMs = 4000,
                        },
                    },
                    new FieldOverride
                    {
                        Id = "o-both",
                        Writes = FieldWrites.Both,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "!!" },
                        Condition = new Condition
                        {
                            Source = new ValueSource
                            {
                                Kind = ValueSourceKind.BuiltIn,
                                Name = "FuelPercent",
                            },
                            Operator = ConditionOperator.LessThan,
                            Value = 1.0,
                        },
                        Lifetime = new Lifetime { Kind = LifetimeKind.UntilDismissed },
                        ActsAsEntrypoint = true,
                    },
                },
            };
            cfg.WheelScreen.Rules.Add(new WheelScreenRule
            {
                Id = "w-logo60",
                Screen = WheelScreenCommand.Logo,
                Enabled = false,
                Condition = new Condition
                {
                    Source = new ValueSource
                    {
                        Kind = ValueSourceKind.BuiltIn,
                        Name = "IsInPitLane",
                    },
                    Operator = ConditionOperator.IsTrue,
                },
                Lifetime = new Lifetime { Kind = LifetimeKind.ForDuration, DurationMs = 60000 },
                Runs = RunsWhen.Idle,
            });
            cfg.Settings.RejectUncommandedChanges = true;
            cfg.Settings.Mode = SettingsMode.LegacyOnly;
            return cfg;
        }
    }
}
