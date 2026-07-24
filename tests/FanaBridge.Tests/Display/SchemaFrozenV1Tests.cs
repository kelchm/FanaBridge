using System.Collections.Generic;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Ship-v1 freeze guard (S4 / spec-v9-s4-rename-freeze). This IS ship-v1; changing
    /// this golden means a schema change — additive members belong at the end of the
    /// object they extend, renames are prohibited without a version bump + migration.
    /// </summary>
    public class SchemaFrozenV1Tests
    {
        // Fully-populated ship-v1 document: every target kind, hold kind, eligibility,
        // both rule sets, screens (incl. inRotation false), field mapping, special + cycle.
        // Formatting matches DisplayConfigSerializer (indented, null/default suppressed).
        private static readonly string ShipV1Golden = @"{
  ""schemaVersion"": 1,
  ""profileId"": ""ship-v1-pin"",
  ""itm"": {
    ""rules"": [
      {
        ""id"": ""r-page"",
        ""name"": ""Low fuel"",
        ""when"": {
          ""kind"": ""lessThan"",
          ""source"": {
            ""kind"": ""builtIn"",
            ""name"": ""FuelPercent""
          },
          ""value"": 10.0,
          ""hysteresis"": 2.0
        },
        ""show"": {
          ""kind"": ""page"",
          ""page"": ""fuelErsDrs""
        },
        ""hold"": {
          ""kind"": ""forDuration""
        },
        ""runs"": ""inGame""
      },
      {
        ""id"": ""r-cycle"",
        ""when"": {
          ""kind"": ""isTrue"",
          ""source"": {
            ""kind"": ""builtIn"",
            ""name"": ""DrsEnabled""
          }
        },
        ""show"": {
          ""kind"": ""cycle"",
          ""pages"": [
            ""lapInfo"",
            ""legacy""
          ],
          ""periodMs"": 4000
        },
        ""hold"": {
          ""kind"": ""whileActive""
        },
        ""runs"": ""always""
      },
      {
        ""id"": ""r-screen"",
        ""enabled"": false,
        ""when"": {
          ""kind"": ""changes"",
          ""source"": {
            ""kind"": ""builtIn"",
            ""name"": ""BrakeBias""
          }
        },
        ""show"": {
          ""kind"": ""segmentScreen"",
          ""screenId"": ""s1""
        },
        ""hold"": {
          ""kind"": ""untilDismissed""
        },
        ""runs"": ""idle""
      },
      {
        ""id"": ""r-special"",
        ""when"": {
          ""kind"": ""actionTriggered"",
          ""source"": {
            ""kind"": ""fanaBridgeAction"",
            ""name"": ""ShowLogo""
          }
        },
        ""show"": {
          ""kind"": ""special"",
          ""command"": ""logo""
        },
        ""hold"": {
          ""kind"": ""forDuration"",
          ""durationMs"": 2000
        }
      }
    ],
    ""basePage"": ""lapInfo""
  },
  ""segmentDisplay"": {
    ""rules"": [
      {
        ""id"": ""l-screen"",
        ""when"": {
          ""kind"": ""isTrue"",
          ""source"": {
            ""kind"": ""builtIn"",
            ""name"": ""DrsAvailable""
          }
        },
        ""show"": {
          ""kind"": ""segmentScreen"",
          ""screenId"": ""s2""
        },
        ""hold"": {
          ""kind"": ""whileActive""
        }
      }
    ],
    ""baseScreenId"": ""s1"",
    ""screens"": [
      {
        ""id"": ""s1"",
        ""name"": ""Speed"",
        ""contentKind"": ""speed""
      },
      {
        ""id"": ""s2"",
        ""name"": ""Pit"",
        ""text"": ""PIT"",
        ""effect"": ""blink"",
        ""inRotation"": false
      },
      {
        ""id"": ""s3"",
        ""name"": ""Prop"",
        ""contentKind"": ""property"",
        ""source"": {
          ""kind"": ""simHubProperty"",
          ""name"": ""DataCorePlugin.GameData.SpeedKmh""
        }
      }
    ]
  },
  ""fieldMappings"": {
    ""5"": {
      ""source"": {
        ""kind"": ""simHubProperty"",
        ""name"": ""DataCorePlugin.Computed.Fuel_RemainingLaps""
      },
      ""format"": ""bare""
    }
  }
}";

        [Fact]
        public void FullyPopulatedDocument_MatchesShipV1Golden()
        {
            var config = new DisplayCustomizationConfig
            {
                SchemaVersion = 1,
                ProfileId = "ship-v1-pin",
            };
            config.Itm.BasePage = ItmPage.LapInfo;
            config.Itm.Rules.Add(new DisplayRule
            {
                Id = "r-page",
                Name = "Low fuel",
                When = new RuleCondition
                {
                    Kind = ConditionKind.LessThan,
                    Source = new PropertySpec
                    {
                        Kind = PropertyKind.BuiltIn,
                        Name = BuiltInProperties.FuelPercent,
                    },
                    Value = 10.0,
                    Hysteresis = 2.0,
                },
                Show = new RuleTarget { Kind = TargetKind.Page, Page = ItmPage.FuelErsDrs },
                Hold = new HoldSpec { Kind = HoldKind.ForDuration, DurationMs = 5000 },
                Eligible = RuleEligibility.InGame,
            });
            config.Itm.Rules.Add(new DisplayRule
            {
                Id = "r-cycle",
                When = new RuleCondition
                {
                    Kind = ConditionKind.IsTrue,
                    Source = new PropertySpec
                    {
                        Kind = PropertyKind.BuiltIn,
                        Name = BuiltInProperties.DrsEnabled,
                    },
                },
                Show = new RuleTarget
                {
                    // Page 6's "legacy" spelling (the wheel's own label) + a
                    // NON-default period so periodMs is visible to the freeze.
                    Kind = TargetKind.Cycle,
                    PagesRaw = new List<string> { "lapInfo", "legacy" },
                    PeriodMs = 4000,
                },
                Hold = new HoldSpec { Kind = HoldKind.WhileActive },
                Eligible = RuleEligibility.Always,
            });
            config.Itm.Rules.Add(new DisplayRule
            {
                Id = "r-screen",
                Enabled = false,
                When = new RuleCondition
                {
                    Kind = ConditionKind.Changes,
                    Source = new PropertySpec
                    {
                        Kind = PropertyKind.BuiltIn,
                        Name = BuiltInProperties.BrakeBias,
                    },
                },
                Show = new RuleTarget { Kind = TargetKind.SegmentScreen, ScreenId = "s1" },
                Hold = new HoldSpec { Kind = HoldKind.UntilDismissed },
                Eligible = RuleEligibility.Idle,
            });
            config.Itm.Rules.Add(new DisplayRule
            {
                Id = "r-special",
                When = new RuleCondition
                {
                    Kind = ConditionKind.ActionTriggered,
                    Source = new PropertySpec
                    {
                        Kind = PropertyKind.FanaBridgeAction,
                        Name = "ShowLogo",
                    },
                },
                Show = new RuleTarget
                {
                    Kind = TargetKind.Special,
                    Command = SpecialCommand.LogoScreen,
                },
                Hold = new HoldSpec { Kind = HoldKind.ForDuration, DurationMs = 2000 },
            });

            config.Legacy.BaseScreenId = "s1";
            config.Legacy.Screens.Add(new LegacyScreen
            {
                Id = "s1",
                Name = "Speed",
                ContentKind = LegacyContentKind.Speed,
            });
            config.Legacy.Screens.Add(new LegacyScreen
            {
                Id = "s2",
                Name = "Pit",
                Text = "PIT",
                Effect = LegacyEffect.Blink,
                InRotation = false,
            });
            config.Legacy.Screens.Add(new LegacyScreen
            {
                Id = "s3",
                Name = "Prop",
                ContentKind = LegacyContentKind.Property,
                Source = new PropertySpec
                {
                    Kind = PropertyKind.SimHubProperty,
                    Name = "DataCorePlugin.GameData.SpeedKmh",
                },
            });
            config.Legacy.Rules.Add(new DisplayRule
            {
                Id = "l-screen",
                When = new RuleCondition
                {
                    Kind = ConditionKind.IsTrue,
                    Source = new PropertySpec
                    {
                        Kind = PropertyKind.BuiltIn,
                        Name = BuiltInProperties.DrsAvailable,
                    },
                },
                Show = new RuleTarget { Kind = TargetKind.SegmentScreen, ScreenId = "s2" },
                Hold = new HoldSpec { Kind = HoldKind.WhileActive },
            });

            config.FieldMappings[ItmParam.Fuel] = new FieldMapping
            {
                Source = new PropertySpec
                {
                    Kind = PropertyKind.SimHubProperty,
                    Name = "DataCorePlugin.Computed.Fuel_RemainingLaps",
                },
                Format = FieldFormats.Bare,
            };

            // Normalize so the golden matches a real load/save path (ids/invariants).
            var normalized = DisplayConfigSerializer.Load(
                DisplayConfigSerializer.Save(config), _ => { });
            string actual = DisplayConfigSerializer.Save(normalized);

            // Newline folding is the ONE sanctioned normalization (git autocrlf would
            // otherwise flap this pin); every other byte is exact. Default-suppressed
            // members (durationMs 5000, periodMs 3000, enabled true, inRotation true)
            // cannot appear by design — each is pinned via a non-default instance above.
            Assert.Equal(
                System.Text.Encoding.UTF8.GetBytes(ShipV1Golden.Replace("\r\n", "\n")),
                System.Text.Encoding.UTF8.GetBytes(actual.Replace("\r\n", "\n")));
        }
    }
}
