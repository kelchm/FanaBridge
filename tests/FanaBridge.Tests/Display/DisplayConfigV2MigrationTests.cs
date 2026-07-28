using System;
using System.Text;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using Newtonsoft.Json;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Phase E2: v1→v2 migration reader (DisplayConfigV2Migration). Loads v1 via the
    /// existing serializer/validator, converts, and byte-pins the v2 document via
    /// DisplayConfigV2Serializer. Covers every §9 key map row and structural law.
    /// </summary>
    public class DisplayConfigV2MigrationTests
    {
        private static string Migrate(string v1Json)
        {
            var v1 = DisplayConfigSerializer.Load(v1Json, _ => { });
            var v2 = DisplayConfigV2Migration.Convert(v1);
            return DisplayConfigV2Serializer.Save(v2);
        }

        /// <summary>
        /// Deserialize without Normalize — used for missing/duplicate-id fixtures so the
        /// migration's own occurrence allocator is under test (Normalize assigns random GUIDs).
        /// </summary>
        private static DisplayCustomizationConfig DeserializeV1Raw(string v1Json)
            => JsonConvert.DeserializeObject<DisplayCustomizationConfig>(v1Json)
               ?? new DisplayCustomizationConfig();

        private static string MigrateRaw(string v1Json)
        {
            var v1 = DeserializeV1Raw(v1Json);
            var v2 = DisplayConfigV2Migration.Convert(v1);
            return DisplayConfigV2Serializer.Save(v2);
        }

        private static void AssertGolden(string v1Json, string expectedV2)
        {
            string actual = Migrate(v1Json);
            // Normalize newlines / surrounding whitespace so the pin is OS-stable.
            Assert.Equal(Norm(expectedV2), Norm(actual));
        }

        private static void AssertGoldenRaw(string v1Json, string expectedV2)
        {
            string actual = MigrateRaw(v1Json);
            Assert.Equal(Norm(expectedV2), Norm(actual));
        }

        private static string Norm(string s)
            => (s ?? "").Replace("\r\n", "\n").Trim();


        [Fact]
        public void MainBattery_EveryV1Shape()
        {
            AssertGolden(V1_MainBattery_EveryV1Shape, Golden_MainBattery_EveryV1Shape);
        }

        private const string V1_MainBattery_EveryV1Shape = @"
{
  ""schemaVersion"": 1,
  ""profileId"": ""mig-main"",
  ""itm"": {
    ""rules"": [
      {
        ""id"": ""r-page"",
        ""name"": ""Low fuel"",
        ""when"": {
          ""kind"": ""lessThan"",
          ""source"": { ""kind"": ""builtIn"", ""name"": ""FuelPercent"" },
          ""value"": 10.0,
          ""hysteresis"": 2.0
        },
        ""show"": { ""kind"": ""page"", ""page"": ""fuelErsDrs"" },
        ""hold"": { ""kind"": ""forDuration"" },
        ""runs"": ""inGame""
      },
      {
        ""id"": ""r-cycle"",
        ""when"": {
          ""kind"": ""isTrue"",
          ""source"": { ""kind"": ""builtIn"", ""name"": ""DrsEnabled"" }
        },
        ""show"": {
          ""kind"": ""cycle"",
          ""pages"": [ ""lapInfo"", ""legacy"" ],
          ""periodMs"": 4000
        },
        ""hold"": { ""kind"": ""whileActive"" },
        ""runs"": ""always""
      },
      {
        ""id"": ""r-screen"",
        ""enabled"": false,
        ""when"": {
          ""kind"": ""changes"",
          ""source"": { ""kind"": ""builtIn"", ""name"": ""BrakeBias"" }
        },
        ""show"": { ""kind"": ""segmentScreen"", ""screenId"": ""s1"" },
        ""hold"": { ""kind"": ""untilDismissed"" },
        ""runs"": ""idle""
      },
      {
        ""id"": ""r-special"",
        ""when"": {
          ""kind"": ""actionTriggered"",
          ""source"": { ""kind"": ""fanaBridgeAction"", ""name"": ""ShowLogo"" }
        },
        ""show"": { ""kind"": ""special"", ""command"": ""logo"" },
        ""hold"": { ""kind"": ""forDuration"", ""durationMs"": 2000 }
      },
      {
        ""id"": ""r-page-legacy"",
        ""when"": {
          ""kind"": ""isTrue"",
          ""source"": { ""kind"": ""builtIn"", ""name"": ""PitLimiterOn"" }
        },
        ""show"": { ""kind"": ""page"", ""page"": ""legacy"" },
        ""hold"": { ""kind"": ""whileActive"" }
      },
      {
        ""id"": ""r-increases"",
        ""when"": {
          ""kind"": ""increases"",
          ""source"": { ""kind"": ""builtIn"", ""name"": ""TcLevel"" }
        },
        ""show"": { ""kind"": ""page"", ""page"": ""carSettings"" },
        ""hold"": { ""kind"": ""forDuration"", ""durationMs"": 3000 }
      },
      {
        ""id"": ""r-decreases"",
        ""when"": {
          ""kind"": ""decreases"",
          ""source"": { ""kind"": ""builtIn"", ""name"": ""AbsLevel"" }
        },
        ""show"": { ""kind"": ""page"", ""page"": ""carSettings"" },
        ""hold"": { ""kind"": ""untilDismissed"" }
      },
      {
        ""id"": ""r-special2"",
        ""when"": {
          ""kind"": ""isTrue"",
          ""source"": { ""kind"": ""builtIn"", ""name"": ""IsInPitLane"" }
        },
        ""show"": { ""kind"": ""special"", ""command"": ""blank"" },
        ""hold"": { ""kind"": ""forDuration"", ""durationMs"": 1000 },
        ""runs"": ""idle""
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
          ""source"": { ""kind"": ""builtIn"", ""name"": ""DrsAvailable"" }
        },
        ""show"": { ""kind"": ""segmentScreen"", ""screenId"": ""s2"" },
        ""hold"": { ""kind"": ""whileActive"" }
      },
      {
        ""id"": ""l-special"",
        ""when"": {
          ""kind"": ""isTrue"",
          ""source"": { ""kind"": ""builtIn"", ""name"": ""RedlineReached"" }
        },
        ""show"": { ""kind"": ""special"", ""command"": ""white"" },
        ""hold"": { ""kind"": ""forDuration"", ""durationMs"": 500 }
      }
    ],
    ""baseScreenId"": ""s1"",
    ""screens"": [
      { ""id"": ""s1"", ""name"": ""Speed"", ""contentKind"": ""speed"" },
      { ""id"": ""s2"", ""name"": ""Pit"", ""text"": ""PIT"", ""effect"": ""blink"", ""inRotation"": false },
      {
        ""id"": ""s3"",
        ""name"": ""Prop"",
        ""contentKind"": ""property"",
        ""source"": { ""kind"": ""simHubProperty"", ""name"": ""DataCorePlugin.GameData.SpeedKmh"" }
      },
      {
        ""id"": ""s4"",
        ""name"": ""Gear"",
        ""contentKind"": ""gear"",
        ""effect"": ""scroll"",
        ""inRotation"": true
      }
    ]
  },
  ""fieldMappings"": {
    ""5"": {
      ""source"": { ""kind"": ""simHubProperty"", ""name"": ""DataCorePlugin.Computed.Fuel_RemainingLaps"" },
      ""format"": ""bare""
    }
  }
}
";

        private const string Golden_MainBattery_EveryV1Shape = @"
{
  ""schemaVersion"": 2,
  ""profileId"": ""mig-main"",
  ""pages"": [
    {
      ""kind"": ""hostedPage"",
      ""id"": ""s1"",
      ""name"": ""Speed"",
      ""base"": {
        ""content"": {
          ""kind"": ""speed""
        }
      }
    },
    {
      ""kind"": ""hostedPage"",
      ""id"": ""s2"",
      ""name"": ""Pit"",
      ""base"": {
        ""content"": {
          ""kind"": ""text"",
          ""text"": ""PIT""
        },
        ""effect"": ""blink""
      }
    },
    {
      ""kind"": ""hostedPage"",
      ""id"": ""s3"",
      ""name"": ""Prop"",
      ""base"": {
        ""content"": {
          ""kind"": ""property"",
          ""source"": {
            ""kind"": ""simHubProperty"",
            ""name"": ""DataCorePlugin.GameData.SpeedKmh""
          }
        }
      }
    },
    {
      ""kind"": ""hostedPage"",
      ""id"": ""s4"",
      ""name"": ""Gear"",
      ""base"": {
        ""content"": {
          ""kind"": ""gear""
        },
        ""effect"": ""scroll""
      }
    }
  ],
  ""cycles"": [
    {
      ""id"": ""c-r-cycle"",
      ""members"": [
        {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""lapInfo""
        },
        {
          ""kind"": ""hostedPage"",
          ""id"": ""s1""
        }
      ],
      ""periodMs"": 4000
    }
  ],
  ""priority"": {
    ""rows"": [
      {
        ""kind"": ""seat"",
        ""id"": ""s-r-page"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""fuelErsDrs""
        },
        ""summons"": [
          {
            ""id"": ""r-page"",
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
            }
          }
        ]
      },
      {
        ""kind"": ""seat"",
        ""id"": ""s-r-cycle"",
        ""target"": {
          ""kind"": ""cycle"",
          ""id"": ""c-r-cycle""
        },
        ""summons"": [
          {
            ""id"": ""r-cycle"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""DrsEnabled""
              },
              ""operator"": ""isTrue""
            },
            ""lifetime"": {
              ""kind"": ""whileTrue""
            },
            ""runs"": ""always""
          }
        ]
      },
      {
        ""kind"": ""seat"",
        ""id"": ""s-r-screen"",
        ""target"": {
          ""kind"": ""hostedPage"",
          ""id"": ""s1""
        },
        ""summons"": [
          {
            ""id"": ""r-screen"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""BrakeBias""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""then"": ""untilDismissed""
            },
            ""runs"": ""idle"",
            ""enabled"": false
          },
          {
            ""id"": ""r-page-legacy"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""PitLimiterOn""
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
        ""kind"": ""seat"",
        ""id"": ""s-r-increases"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""carSettings""
        },
        ""summons"": [
          {
            ""id"": ""r-increases"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""TcLevel""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""durationMs"": 3000,
              ""direction"": ""up""
            }
          },
          {
            ""id"": ""r-decreases"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""AbsLevel""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""direction"": ""down"",
              ""then"": ""untilDismissed""
            }
          }
        ]
      },
      {
        ""kind"": ""seat"",
        ""id"": ""s-l-screen"",
        ""target"": {
          ""kind"": ""hostedPage"",
          ""id"": ""s2""
        },
        ""summons"": [
          {
            ""id"": ""l-screen"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""DrsAvailable""
              },
              ""operator"": ""isTrue""
            },
            ""lifetime"": {
              ""kind"": ""whileTrue""
            }
          }
        ]
      }
    ],
    ""rest"": {
      ""inSessionPage"": {
        ""kind"": ""itmPage"",
        ""catalogPageId"": ""lapInfo""
      },
      ""landingPage"": {
        ""kind"": ""hostedPage"",
        ""id"": ""s1""
      },
      ""idle"": {
        ""kind"": ""blank""
      }
    }
  },
  ""pageOrder"": [
    {
      ""kind"": ""hostedPage"",
      ""id"": ""s1""
    },
    {
      ""kind"": ""hostedPage"",
      ""id"": ""s3""
    },
    {
      ""kind"": ""hostedPage"",
      ""id"": ""s4""
    }
  ],
  ""fields"": {
    ""5"": {
      ""base"": {
        ""source"": {
          ""kind"": ""simHubProperty"",
          ""name"": ""DataCorePlugin.Computed.Fuel_RemainingLaps""
        },
        ""format"": ""bare""
      },
      ""overrides"": []
    }
  },
  ""wheelScreen"": {
    ""rules"": [
      {
        ""id"": ""r-special"",
        ""screen"": ""logo"",
        ""condition"": {
          ""source"": {
            ""kind"": ""action"",
            ""name"": ""ShowLogo""
          }
        },
        ""lifetime"": {
          ""kind"": ""onChange"",
          ""durationMs"": 2000
        }
      },
      {
        ""id"": ""r-special2"",
        ""screen"": ""blank"",
        ""condition"": {
          ""source"": {
            ""kind"": ""builtIn"",
            ""name"": ""IsInPitLane""
          },
          ""operator"": ""isTrue""
        },
        ""lifetime"": {
          ""kind"": ""forDuration"",
          ""durationMs"": 1000
        },
        ""runs"": ""idle""
      },
      {
        ""id"": ""l-special"",
        ""screen"": ""white"",
        ""condition"": {
          ""source"": {
            ""kind"": ""builtIn"",
            ""name"": ""RedlineReached""
          },
          ""operator"": ""isTrue""
        },
        ""lifetime"": {
          ""kind"": ""forDuration"",
          ""durationMs"": 500
        }
      }
    ]
  },
  ""settings"": {}
}
";

        [Fact]
        public void InterleavedRanks_HomeAndSatelliteOrder()
        {
            AssertGolden(V1_InterleavedRanks_HomeAndSatelliteOrder, Golden_InterleavedRanks_HomeAndSatelliteOrder);
        }

        private const string V1_InterleavedRanks_HomeAndSatelliteOrder = @"
{
  ""schemaVersion"": 1,
  ""itm"": {
    ""rules"": [
      {
        ""id"": ""a1"",
        ""when"": { ""kind"": ""isTrue"", ""source"": { ""kind"": ""builtIn"", ""name"": ""DrsEnabled"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""fuelErsDrs"" },
        ""hold"": { ""kind"": ""whileActive"" }
      },
      {
        ""id"": ""a2"",
        ""when"": { ""kind"": ""isTrue"", ""source"": { ""kind"": ""builtIn"", ""name"": ""DrsAvailable"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""fuelErsDrs"" },
        ""hold"": { ""kind"": ""whileActive"" }
      },
      {
        ""id"": ""b1"",
        ""when"": { ""kind"": ""isTrue"", ""source"": { ""kind"": ""builtIn"", ""name"": ""PitLimiterOn"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""lapInfo"" },
        ""hold"": { ""kind"": ""whileActive"" }
      },
      {
        ""id"": ""a3"",
        ""when"": { ""kind"": ""isTrue"", ""source"": { ""kind"": ""builtIn"", ""name"": ""IsInPitLane"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""fuelErsDrs"" },
        ""hold"": { ""kind"": ""whileActive"" }
      },
      {
        ""id"": ""a4"",
        ""when"": { ""kind"": ""lessThan"", ""source"": { ""kind"": ""builtIn"", ""name"": ""FuelPercent"" }, ""value"": 5 },
        ""show"": { ""kind"": ""page"", ""page"": ""fuelErsDrs"" },
        ""hold"": { ""kind"": ""forDuration"", ""durationMs"": 2000 }
      },
      {
        ""id"": ""c1"",
        ""when"": { ""kind"": ""isTrue"", ""source"": { ""kind"": ""builtIn"", ""name"": ""RedlineReached"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""lapTimes"" },
        ""hold"": { ""kind"": ""whileActive"" }
      }
    ],
    ""basePage"": ""lapInfo""
  },
  ""segmentDisplay"": {
    ""rules"": [
      {
        ""id"": ""seg-a"",
        ""when"": { ""kind"": ""isTrue"", ""source"": { ""kind"": ""builtIn"", ""name"": ""Gear"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""fuelErsDrs"" },
        ""hold"": { ""kind"": ""whileActive"" }
      }
    ],
    ""screens"": []
  }
}
";

        private const string Golden_InterleavedRanks_HomeAndSatelliteOrder = @"
{
  ""schemaVersion"": 2,
  ""pages"": [],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [
      {
        ""kind"": ""seat"",
        ""id"": ""s-a1"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""fuelErsDrs""
        },
        ""summons"": [
          {
            ""id"": ""a1"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""DrsEnabled""
              },
              ""operator"": ""isTrue""
            },
            ""lifetime"": {
              ""kind"": ""whileTrue""
            }
          },
          {
            ""id"": ""a2"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""DrsAvailable""
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
        ""kind"": ""seat"",
        ""id"": ""s-b1"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""lapInfo""
        },
        ""summons"": [
          {
            ""id"": ""b1"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""PitLimiterOn""
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
        ""id"": ""sat-a3"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""fuelErsDrs""
        },
        ""summons"": [
          {
            ""id"": ""a3"",
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
          },
          {
            ""id"": ""a4"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""FuelPercent""
              },
              ""operator"": ""lessThan"",
              ""value"": 5.0
            },
            ""lifetime"": {
              ""kind"": ""forDuration"",
              ""durationMs"": 2000
            }
          }
        ]
      },
      {
        ""kind"": ""seat"",
        ""id"": ""s-c1"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""lapTimes""
        },
        ""summons"": [
          {
            ""id"": ""c1"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""RedlineReached""
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
        ""id"": ""sat-seg-a"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""fuelErsDrs""
        },
        ""summons"": [
          {
            ""id"": ""seg-a"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""Gear""
              },
              ""operator"": ""isTrue""
            },
            ""lifetime"": {
              ""kind"": ""whileTrue""
            }
          }
        ]
      }
    ],
    ""rest"": {
      ""inSessionPage"": {
        ""kind"": ""itmPage"",
        ""catalogPageId"": ""lapInfo""
      },
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
}
";

        [Fact]
        public void BasePageLegacy_InSessionIsHostedFromBaseScreenId()
        {
            AssertGolden(V1_BasePageLegacy_InSessionIsHostedFromBaseScreenId, Golden_BasePageLegacy_InSessionIsHostedFromBaseScreenId);
        }

        private const string V1_BasePageLegacy_InSessionIsHostedFromBaseScreenId = @"
{
  ""schemaVersion"": 1,
  ""itm"": {
    ""basePage"": ""legacy"",
    ""rules"": []
  },
  ""segmentDisplay"": {
    ""baseScreenId"": ""s-base"",
    ""screens"": [
      { ""id"": ""s-base"", ""name"": ""Base"", ""contentKind"": ""speed"" }
    ]
  }
}
";

        private const string Golden_BasePageLegacy_InSessionIsHostedFromBaseScreenId = @"
{
  ""schemaVersion"": 2,
  ""pages"": [
    {
      ""kind"": ""hostedPage"",
      ""id"": ""s-base"",
      ""name"": ""Base"",
      ""base"": {
        ""content"": {
          ""kind"": ""speed""
        }
      }
    }
  ],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [],
    ""rest"": {
      ""inSessionPage"": {
        ""kind"": ""hostedPage"",
        ""id"": ""s-base""
      },
      ""landingPage"": {
        ""kind"": ""hostedPage"",
        ""id"": ""s-base""
      },
      ""idle"": {
        ""kind"": ""blank""
      }
    }
  },
  ""pageOrder"": [
    {
      ""kind"": ""hostedPage"",
      ""id"": ""s-base""
    }
  ],
  ""fields"": {},
  ""wheelScreen"": {
    ""rules"": []
  },
  ""settings"": {}
}
";

        [Fact]
        public void EdgeAndActionTriggered_OnChangeLifetimes()
        {
            AssertGolden(V1_EdgeAndActionTriggered_OnChangeLifetimes, Golden_EdgeAndActionTriggered_OnChangeLifetimes);
        }

        private const string V1_EdgeAndActionTriggered_OnChangeLifetimes = @"
{
  ""schemaVersion"": 1,
  ""itm"": {
    ""rules"": [
      {
        ""id"": ""e-chg"",
        ""when"": { ""kind"": ""changes"", ""source"": { ""kind"": ""builtIn"", ""name"": ""Gear"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""lapInfo"" },
        ""hold"": { ""kind"": ""forDuration"", ""durationMs"": 2500 }
      },
      {
        ""id"": ""e-up"",
        ""when"": { ""kind"": ""increases"", ""source"": { ""kind"": ""builtIn"", ""name"": ""TcLevel"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""carSettings"" },
        ""hold"": { ""kind"": ""forDuration"" }
      },
      {
        ""id"": ""e-dn"",
        ""when"": { ""kind"": ""decreases"", ""source"": { ""kind"": ""builtIn"", ""name"": ""AbsLevel"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""carSettings"" },
        ""hold"": { ""kind"": ""untilDismissed"" }
      },
      {
        ""id"": ""e-act"",
        ""when"": {
          ""kind"": ""actionTriggered"",
          ""source"": { ""kind"": ""fanaBridgeAction"", ""name"": ""NextPage"" }
        },
        ""show"": { ""kind"": ""page"", ""page"": ""tyreTemps"" },
        ""hold"": { ""kind"": ""forDuration"", ""durationMs"": 1500 }
      },
      {
        ""id"": ""e-act-latch"",
        ""when"": {
          ""kind"": ""actionTriggered"",
          ""source"": { ""kind"": ""fanaBridgeAction"", ""name"": ""ShowTyres"" }
        },
        ""show"": { ""kind"": ""page"", ""page"": ""tyreTemps"" },
        ""hold"": { ""kind"": ""untilDismissed"" }
      }
    ]
  }
}
";

        private const string Golden_EdgeAndActionTriggered_OnChangeLifetimes = @"
{
  ""schemaVersion"": 2,
  ""pages"": [],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [
      {
        ""kind"": ""seat"",
        ""id"": ""s-e-chg"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""lapInfo""
        },
        ""summons"": [
          {
            ""id"": ""e-chg"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""Gear""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""durationMs"": 2500
            }
          }
        ]
      },
      {
        ""kind"": ""seat"",
        ""id"": ""s-e-up"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""carSettings""
        },
        ""summons"": [
          {
            ""id"": ""e-up"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""TcLevel""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""direction"": ""up""
            }
          },
          {
            ""id"": ""e-dn"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""AbsLevel""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""direction"": ""down"",
              ""then"": ""untilDismissed""
            }
          }
        ]
      },
      {
        ""kind"": ""seat"",
        ""id"": ""s-e-act"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""tyreTemps""
        },
        ""summons"": [
          {
            ""id"": ""e-act"",
            ""condition"": {
              ""source"": {
                ""kind"": ""action"",
                ""name"": ""NextPage""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""durationMs"": 1500
            }
          },
          {
            ""id"": ""e-act-latch"",
            ""condition"": {
              ""source"": {
                ""kind"": ""action"",
                ""name"": ""ShowTyres""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""then"": ""untilDismissed""
            }
          }
        ]
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
}
";

        [Fact]
        public void ExtensionData_RelocatedPerStructuralLaw()
        {
            AssertGolden(V1_ExtensionData_RelocatedPerStructuralLaw, Golden_ExtensionData_RelocatedPerStructuralLaw);
        }

        private const string V1_ExtensionData_RelocatedPerStructuralLaw = @"
{
  ""schemaVersion"": 1,
  ""profileId"": ""ext"",
  ""v1Top"": { ""nested"": true },
  ""itm"": {
    ""v1Itm"": 42,
    ""basePage"": ""lapInfo"",
    ""rules"": [
      {
        ""id"": ""r-ext"",
        ""v1Rule"": ""rule-x"",
        ""when"": {
          ""kind"": ""lessThan"",
          ""source"": { ""kind"": ""builtIn"", ""name"": ""Fuel"", ""v1Source"": 1 },
          ""value"": 10,
          ""v1When"": ""w""
        },
        ""show"": { ""kind"": ""page"", ""page"": ""fuelErsDrs"", ""v1Show"": 7 },
        ""hold"": { ""kind"": ""forDuration"", ""durationMs"": 4000, ""v1Hold"": ""h"" },
        ""runs"": ""always""
      }
    ]
  },
  ""segmentDisplay"": {
    ""v1Leg"": ""L"",
    ""baseScreenId"": ""sx"",
    ""screens"": [
      { ""id"": ""sx"", ""name"": ""X"", ""contentKind"": ""speed"", ""v1Screen"": true }
    ],
    ""rules"": []
  },
  ""fieldMappings"": {
    ""5"": {
      ""source"": { ""kind"": ""builtIn"", ""name"": ""Fuel"" },
      ""format"": ""bare"",
      ""v1Map"": ""m""
    }
  }
}
";

        private const string Golden_ExtensionData_RelocatedPerStructuralLaw = @"
{
  ""schemaVersion"": 2,
  ""profileId"": ""ext"",
  ""pages"": [
    {
      ""kind"": ""hostedPage"",
      ""id"": ""sx"",
      ""name"": ""X"",
      ""base"": {
        ""content"": {
          ""kind"": ""speed""
        }
      },
      ""v1Screen"": true
    }
  ],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [
      {
        ""kind"": ""seat"",
        ""id"": ""s-r-ext"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""fuelErsDrs""
        },
        ""summons"": [
          {
            ""id"": ""r-ext"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""Fuel"",
                ""v1Source"": 1
              },
              ""operator"": ""lessThan"",
              ""value"": 10.0,
              ""v1When"": ""w""
            },
            ""lifetime"": {
              ""kind"": ""forDuration"",
              ""durationMs"": 4000,
              ""v1Hold"": ""h""
            },
            ""runs"": ""always"",
            ""v1Rule"": ""rule-x""
          }
        ],
        ""v1Show"": 7
      }
    ],
    ""rest"": {
      ""inSessionPage"": {
        ""kind"": ""itmPage"",
        ""catalogPageId"": ""lapInfo""
      },
      ""landingPage"": {
        ""kind"": ""hostedPage"",
        ""id"": ""sx""
      },
      ""idle"": {
        ""kind"": ""blank""
      }
    }
  },
  ""pageOrder"": [
    {
      ""kind"": ""hostedPage"",
      ""id"": ""sx""
    }
  ],
  ""fields"": {
    ""5"": {
      ""base"": {
        ""source"": {
          ""kind"": ""builtIn"",
          ""name"": ""Fuel""
        },
        ""format"": ""bare"",
        ""v1Map"": ""m""
      },
      ""overrides"": []
    }
  },
  ""wheelScreen"": {
    ""rules"": []
  },
  ""settings"": {},
  ""v1Top"": {
    ""nested"": true
  },
  ""v1Itm"": 42,
  ""v1Leg"": ""L""
}
";

        [Fact]
        public void DegradedUnknownKinds_RawSpellingsSurvive()
        {
            AssertGolden(V1_DegradedUnknownKinds_RawSpellingsSurvive, Golden_DegradedUnknownKinds_RawSpellingsSurvive);
        }

        private const string V1_DegradedUnknownKinds_RawSpellingsSurvive = @"
{
  ""schemaVersion"": 1,
  ""itm"": {
    ""rules"": [
      {
        ""id"": ""r-unk"",
        ""enabled"": false,
        ""when"": {
          ""kind"": ""sparkles"",
          ""source"": { ""kind"": ""builtIn"", ""name"": ""Fuel"" },
          ""value"": 1
        },
        ""show"": { ""kind"": ""hologram"", ""page"": ""tyreTemps"" },
        ""hold"": { ""kind"": ""untilTomorrow"", ""durationMs"": 3000 },
        ""runs"": ""whenever""
      },
      {
        ""id"": ""r-ok"",
        ""when"": {
          ""kind"": ""isTrue"",
          ""source"": { ""kind"": ""builtIn"", ""name"": ""DrsEnabled"" }
        },
        ""show"": { ""kind"": ""page"", ""page"": ""fuelErsDrs"" },
        ""hold"": { ""kind"": ""whileActive"" }
      }
    ]
  }
}
";

        private const string Golden_DegradedUnknownKinds_RawSpellingsSurvive = @"
{
  ""schemaVersion"": 2,
  ""pages"": [],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [
      {
        ""kind"": ""seat"",
        ""id"": ""s-r-unk"",
        ""target"": {
          ""kind"": ""hologram"",
          ""catalogPageId"": ""tyreTemps""
        },
        ""summons"": [
          {
            ""id"": ""r-unk"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""Fuel""
              },
              ""operator"": ""sparkles"",
              ""value"": 1.0
            },
            ""lifetime"": {
              ""kind"": ""untilTomorrow"",
              ""durationMs"": 3000
            },
            ""runs"": ""whenever"",
            ""enabled"": false
          }
        ],
        ""v1Show"": {
          ""kind"": ""hologram"",
          ""page"": ""tyreTemps"",
          ""periodMs"": 3000
        }
      },
      {
        ""kind"": ""seat"",
        ""id"": ""s-r-ok"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""fuelErsDrs""
        },
        ""summons"": [
          {
            ""id"": ""r-ok"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""DrsEnabled""
              },
              ""operator"": ""isTrue""
            },
            ""lifetime"": {
              ""kind"": ""whileTrue""
            }
          }
        ]
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
}
";

        [Fact]
        public void EmptyDocument_BlankIdleDefault()
        {
            AssertGolden(V1_EmptyDocument_BlankIdleDefault, Golden_EmptyDocument_BlankIdleDefault);
        }

        private const string V1_EmptyDocument_BlankIdleDefault = @"
{}
";

        private const string Golden_EmptyDocument_BlankIdleDefault = @"
{
  ""schemaVersion"": 2,
  ""pages"": [],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [],
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
}
";

        [Fact]
        public void SegmentOnly_BaseScreenIdIsInSessionPage()
        {
            AssertGolden(V1_SegmentOnly_BaseScreenIdIsInSessionPage, Golden_SegmentOnly_BaseScreenIdIsInSessionPage);
        }

        private const string V1_SegmentOnly_BaseScreenIdIsInSessionPage = @"
{
  ""schemaVersion"": 1,
  ""segmentDisplay"": {
    ""baseScreenId"": ""only"",
    ""screens"": [
      { ""id"": ""only"", ""name"": ""Only"", ""contentKind"": ""gear"" }
    ],
    ""rules"": [
      {
        ""id"": ""seg-r"",
        ""when"": { ""kind"": ""isTrue"", ""source"": { ""kind"": ""builtIn"", ""name"": ""DrsEnabled"" } },
        ""show"": { ""kind"": ""segmentScreen"", ""screenId"": ""only"" },
        ""hold"": { ""kind"": ""whileActive"" }
      }
    ]
  }
}
";

        private const string Golden_SegmentOnly_BaseScreenIdIsInSessionPage = @"
{
  ""schemaVersion"": 2,
  ""pages"": [
    {
      ""kind"": ""hostedPage"",
      ""id"": ""only"",
      ""name"": ""Only"",
      ""base"": {
        ""content"": {
          ""kind"": ""gear""
        }
      }
    }
  ],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [
      {
        ""kind"": ""seat"",
        ""id"": ""s-seg-r"",
        ""target"": {
          ""kind"": ""hostedPage"",
          ""id"": ""only""
        },
        ""summons"": [
          {
            ""id"": ""seg-r"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""DrsEnabled""
              },
              ""operator"": ""isTrue""
            },
            ""lifetime"": {
              ""kind"": ""whileTrue""
            }
          }
        ]
      }
    ],
    ""rest"": {
      ""inSessionPage"": {
        ""kind"": ""hostedPage"",
        ""id"": ""only""
      },
      ""idle"": {
        ""kind"": ""blank""
      }
    }
  },
  ""pageOrder"": [
    {
      ""kind"": ""hostedPage"",
      ""id"": ""only""
    }
  ],
  ""fields"": {},
  ""wheelScreen"": {
    ""rules"": []
  },
  ""settings"": {}
}
";

        [Fact]
        public void Determinism_SameDocTwice_ByteIdentical()
        {
            string a = Migrate(V1_MainBattery_EveryV1Shape);
            string b = Migrate(V1_MainBattery_EveryV1Shape);
            Assert.Equal(a, b);
        }

        [Fact]
        public void NullInput_YieldsDefaultBlankV2()
        {
            string actual = DisplayConfigV2Serializer.Save(DisplayConfigV2Migration.Convert(null));
            Assert.Equal(Norm(Golden_EmptyDocument_BlankIdleDefault), Norm(actual));
        }

        // ── MIG-001 / MIG-007: edge+hold matrix, unknown holds, whileActive coercions ──

        [Fact]
        public void EdgeHoldMatrix_OnChangeMappingsAndCoercions()
        {
            AssertGolden(V1_EdgeHoldMatrix_OnChangeMappingsAndCoercions,
                Golden_EdgeHoldMatrix_OnChangeMappingsAndCoercions);
        }

        private const string V1_EdgeHoldMatrix_OnChangeMappingsAndCoercions = @"
{
  ""schemaVersion"": 1,
  ""itm"": {
    ""rules"": [
      {
        ""id"": ""chg-until"",
        ""when"": { ""kind"": ""changes"", ""source"": { ""kind"": ""builtIn"", ""name"": ""Gear"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""lapInfo"" },
        ""hold"": { ""kind"": ""untilDismissed"" }
      },
      {
        ""id"": ""chg-wa"",
        ""when"": { ""kind"": ""changes"", ""source"": { ""kind"": ""builtIn"", ""name"": ""Gear"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""lapTimes"" },
        ""hold"": { ""kind"": ""whileActive"" }
      },
      {
        ""id"": ""up-until"",
        ""when"": { ""kind"": ""increases"", ""source"": { ""kind"": ""builtIn"", ""name"": ""TcLevel"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""carSettings"" },
        ""hold"": { ""kind"": ""untilDismissed"" }
      },
      {
        ""id"": ""up-wa"",
        ""when"": { ""kind"": ""increases"", ""source"": { ""kind"": ""builtIn"", ""name"": ""TcLevel"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""fuelErsDrs"" },
        ""hold"": { ""kind"": ""whileActive"" }
      },
      {
        ""id"": ""dn-dur"",
        ""when"": { ""kind"": ""decreases"", ""source"": { ""kind"": ""builtIn"", ""name"": ""AbsLevel"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""carSettings"" },
        ""hold"": { ""kind"": ""forDuration"", ""durationMs"": 2500 }
      },
      {
        ""id"": ""dn-wa"",
        ""when"": { ""kind"": ""decreases"", ""source"": { ""kind"": ""builtIn"", ""name"": ""AbsLevel"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""tyreTemps"" },
        ""hold"": { ""kind"": ""whileActive"" }
      },
      {
        ""id"": ""act-wa"",
        ""when"": {
          ""kind"": ""actionTriggered"",
          ""source"": { ""kind"": ""fanaBridgeAction"", ""name"": ""ShowLogo"" }
        },
        ""show"": { ""kind"": ""page"", ""page"": ""lapInfo"" },
        ""hold"": { ""kind"": ""whileActive"" }
      },
      {
        ""id"": ""chg-unk"",
        ""when"": { ""kind"": ""changes"", ""source"": { ""kind"": ""builtIn"", ""name"": ""Gear"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""lapInfo"" },
        ""hold"": { ""kind"": ""futureHold"", ""durationMs"": 3333 }
      },
      {
        ""id"": ""act-unk"",
        ""when"": {
          ""kind"": ""actionTriggered"",
          ""source"": { ""kind"": ""fanaBridgeAction"", ""name"": ""NextPage"" }
        },
        ""show"": { ""kind"": ""page"", ""page"": ""tyreTemps"" },
        ""hold"": { ""kind"": ""tomorrowHold"", ""durationMs"": 1111 }
      }
    ]
  }
}
";

        private const string Golden_EdgeHoldMatrix_OnChangeMappingsAndCoercions = @"
{
  ""schemaVersion"": 2,
  ""pages"": [],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [
      {
        ""kind"": ""seat"",
        ""id"": ""s-chg-until"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""lapInfo""
        },
        ""summons"": [
          {
            ""id"": ""chg-until"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""Gear""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""then"": ""untilDismissed""
            }
          }
        ]
      },
      {
        ""kind"": ""seat"",
        ""id"": ""s-chg-wa"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""lapTimes""
        },
        ""summons"": [
          {
            ""id"": ""chg-wa"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""Gear""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange""
            }
          }
        ]
      },
      {
        ""kind"": ""seat"",
        ""id"": ""s-up-until"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""carSettings""
        },
        ""summons"": [
          {
            ""id"": ""up-until"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""TcLevel""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""direction"": ""up"",
              ""then"": ""untilDismissed""
            }
          }
        ]
      },
      {
        ""kind"": ""seat"",
        ""id"": ""s-up-wa"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""fuelErsDrs""
        },
        ""summons"": [
          {
            ""id"": ""up-wa"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""TcLevel""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""direction"": ""up""
            }
          }
        ]
      },
      {
        ""kind"": ""satellite"",
        ""id"": ""sat-dn-dur"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""carSettings""
        },
        ""summons"": [
          {
            ""id"": ""dn-dur"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""AbsLevel""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""durationMs"": 2500,
              ""direction"": ""down""
            }
          }
        ]
      },
      {
        ""kind"": ""seat"",
        ""id"": ""s-dn-wa"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""tyreTemps""
        },
        ""summons"": [
          {
            ""id"": ""dn-wa"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""AbsLevel""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""direction"": ""down""
            }
          }
        ]
      },
      {
        ""kind"": ""satellite"",
        ""id"": ""sat-act-wa"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""lapInfo""
        },
        ""summons"": [
          {
            ""id"": ""act-wa"",
            ""condition"": {
              ""source"": {
                ""kind"": ""action"",
                ""name"": ""ShowLogo""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange""
            }
          },
          {
            ""id"": ""chg-unk"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""Gear""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""durationMs"": 3333,
              ""v1HoldKind"": ""futureHold""
            }
          }
        ]
      },
      {
        ""kind"": ""satellite"",
        ""id"": ""sat-act-unk"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""tyreTemps""
        },
        ""summons"": [
          {
            ""id"": ""act-unk"",
            ""condition"": {
              ""source"": {
                ""kind"": ""action"",
                ""name"": ""NextPage""
              }
            },
            ""lifetime"": {
              ""kind"": ""onChange"",
              ""durationMs"": 1111,
              ""v1HoldKind"": ""tomorrowHold""
            }
          }
        ]
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
}
";

        // ── MIG-002: bare Legacy without baseScreenId in all three contexts ──

        [Fact]
        public void BareLegacy_NoBaseScreenId_AllThreeContexts()
        {
            AssertGolden(V1_BareLegacy_NoBaseScreenId_AllThreeContexts,
                Golden_BareLegacy_NoBaseScreenId_AllThreeContexts);
        }

        private const string V1_BareLegacy_NoBaseScreenId_AllThreeContexts = @"
{
  ""schemaVersion"": 1,
  ""itm"": {
    ""basePage"": ""legacy"",
    ""rules"": [
      {
        ""id"": ""r-page-leg"",
        ""when"": { ""kind"": ""isTrue"", ""source"": { ""kind"": ""builtIn"", ""name"": ""PitLimiterOn"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""legacy"" },
        ""hold"": { ""kind"": ""whileActive"" }
      },
      {
        ""id"": ""r-cycle-leg"",
        ""when"": { ""kind"": ""isTrue"", ""source"": { ""kind"": ""builtIn"", ""name"": ""DrsEnabled"" } },
        ""show"": {
          ""kind"": ""cycle"",
          ""pages"": [ ""lapInfo"", ""legacy"" ],
          ""periodMs"": 4000
        },
        ""hold"": { ""kind"": ""whileActive"" }
      }
    ]
  }
}
";

        private const string Golden_BareLegacy_NoBaseScreenId_AllThreeContexts = @"
{
  ""schemaVersion"": 2,
  ""pages"": [],
  ""cycles"": [
    {
      ""id"": ""c-r-cycle-leg"",
      ""members"": [
        {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""lapInfo""
        },
        {
          ""kind"": ""hostedPage"",
          ""id"": ""p-v1-legacy""
        }
      ],
      ""periodMs"": 4000
    }
  ],
  ""priority"": {
    ""rows"": [
      {
        ""kind"": ""seat"",
        ""id"": ""s-r-page-leg"",
        ""target"": {
          ""kind"": ""hostedPage"",
          ""id"": ""p-v1-legacy""
        },
        ""summons"": [
          {
            ""id"": ""r-page-leg"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""PitLimiterOn""
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
        ""kind"": ""seat"",
        ""id"": ""s-r-cycle-leg"",
        ""target"": {
          ""kind"": ""cycle"",
          ""id"": ""c-r-cycle-leg""
        },
        ""summons"": [
          {
            ""id"": ""r-cycle-leg"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""DrsEnabled""
              },
              ""operator"": ""isTrue""
            },
            ""lifetime"": {
              ""kind"": ""whileTrue""
            }
          }
        ]
      }
    ],
    ""rest"": {
      ""inSessionPage"": {
        ""kind"": ""hostedPage"",
        ""id"": ""p-v1-legacy""
      },
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
}
";

        // ── MIG-003: special rule name carries onto WheelScreenRule ──

        [Fact]
        public void SpecialRule_NameCarriesOntoWheelScreenRule()
        {
            AssertGolden(V1_SpecialRule_NameCarriesOntoWheelScreenRule,
                Golden_SpecialRule_NameCarriesOntoWheelScreenRule);
        }

        private const string V1_SpecialRule_NameCarriesOntoWheelScreenRule = @"
{
  ""schemaVersion"": 1,
  ""itm"": {
    ""rules"": [
      {
        ""id"": ""logo"",
        ""name"": ""Qualifying logo"",
        ""when"": {
          ""kind"": ""isTrue"",
          ""source"": { ""kind"": ""builtIn"", ""name"": ""IsInPitLane"" }
        },
        ""show"": { ""kind"": ""special"", ""command"": ""logo"" },
        ""hold"": { ""kind"": ""forDuration"", ""durationMs"": 2000 }
      }
    ]
  }
}
";

        private const string Golden_SpecialRule_NameCarriesOntoWheelScreenRule = @"
{
  ""schemaVersion"": 2,
  ""pages"": [],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [],
    ""rest"": {
      ""idle"": {
        ""kind"": ""blank""
      }
    }
  },
  ""fields"": {},
  ""wheelScreen"": {
    ""rules"": [
      {
        ""id"": ""logo"",
        ""name"": ""Qualifying logo"",
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
          ""durationMs"": 2000
        }
      }
    ]
  },
  ""settings"": {}
}
";

        // ── MIG-004: complete unknown-show payload under v1Show ──

        [Fact]
        public void UnknownShow_CompletePayloadInV1Show()
        {
            AssertGolden(V1_UnknownShow_CompletePayloadInV1Show,
                Golden_UnknownShow_CompletePayloadInV1Show);
        }

        private const string V1_UnknownShow_CompletePayloadInV1Show = @"
{
  ""schemaVersion"": 1,
  ""itm"": {
    ""rules"": [
      {
        ""id"": ""r-holo"",
        ""when"": {
          ""kind"": ""isTrue"",
          ""source"": { ""kind"": ""builtIn"", ""name"": ""DrsEnabled"" }
        },
        ""show"": {
          ""kind"": ""hologram"",
          ""page"": ""tyreTemps"",
          ""screenId"": ""s1"",
          ""pages"": [ ""lapInfo"", ""legacy"" ],
          ""periodMs"": 4000,
          ""command"": ""logo"",
          ""vendorFlag"": true
        },
        ""hold"": { ""kind"": ""whileActive"" }
      }
    ]
  }
}
";

        private const string Golden_UnknownShow_CompletePayloadInV1Show = @"
{
  ""schemaVersion"": 2,
  ""pages"": [],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [
      {
        ""kind"": ""seat"",
        ""id"": ""s-r-holo"",
        ""target"": {
          ""kind"": ""hologram"",
          ""catalogPageId"": ""tyreTemps""
        },
        ""summons"": [
          {
            ""id"": ""r-holo"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""DrsEnabled""
              },
              ""operator"": ""isTrue""
            },
            ""lifetime"": {
              ""kind"": ""whileTrue""
            }
          }
        ],
        ""vendorFlag"": true,
        ""v1Show"": {
          ""kind"": ""hologram"",
          ""page"": ""tyreTemps"",
          ""screenId"": ""s1"",
          ""pages"": [
            ""lapInfo"",
            ""legacy""
          ],
          ""periodMs"": 4000,
          ""command"": ""logo"",
          ""vendorFlag"": true
        }
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
}
";

        // ── MIG-005: extension collisions (root/container + adjacent show) ──

        [Fact]
        public void ExtensionCollisions_RootContainerAndAdjacentShow()
        {
            AssertGolden(V1_ExtensionCollisions_RootContainerAndAdjacentShow,
                Golden_ExtensionCollisions_RootContainerAndAdjacentShow);
        }

        private const string V1_ExtensionCollisions_RootContainerAndAdjacentShow = @"
{
  ""schemaVersion"": 1,
  ""vendor"": ""root"",
  ""itm"": {
    ""vendor"": ""itm"",
    ""basePage"": ""lapInfo"",
    ""rules"": [
      {
        ""id"": ""a1"",
        ""when"": { ""kind"": ""isTrue"", ""source"": { ""kind"": ""builtIn"", ""name"": ""DrsEnabled"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""fuelErsDrs"", ""vendor"": ""show-a"" },
        ""hold"": { ""kind"": ""whileActive"" }
      },
      {
        ""id"": ""a2"",
        ""when"": { ""kind"": ""isTrue"", ""source"": { ""kind"": ""builtIn"", ""name"": ""DrsAvailable"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""fuelErsDrs"", ""vendor"": ""show-b"" },
        ""hold"": { ""kind"": ""whileActive"" }
      }
    ]
  },
  ""segmentDisplay"": {
    ""vendor"": ""seg"",
    ""screens"": [],
    ""rules"": []
  }
}
";

        private const string Golden_ExtensionCollisions_RootContainerAndAdjacentShow = @"
{
  ""schemaVersion"": 2,
  ""pages"": [],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [
      {
        ""kind"": ""seat"",
        ""id"": ""s-a1"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""fuelErsDrs""
        },
        ""summons"": [
          {
            ""id"": ""a1"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""DrsEnabled""
              },
              ""operator"": ""isTrue""
            },
            ""lifetime"": {
              ""kind"": ""whileTrue""
            }
          },
          {
            ""id"": ""a2"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""DrsAvailable""
              },
              ""operator"": ""isTrue""
            },
            ""lifetime"": {
              ""kind"": ""whileTrue""
            }
          }
        ],
        ""vendor"": ""show-a"",
        ""show.vendor"": ""show-b""
      }
    ],
    ""rest"": {
      ""inSessionPage"": {
        ""kind"": ""itmPage"",
        ""catalogPageId"": ""lapInfo""
      },
      ""idle"": {
        ""kind"": ""blank""
      }
    }
  },
  ""fields"": {},
  ""wheelScreen"": {
    ""rules"": []
  },
  ""settings"": {},
  ""vendor"": ""root"",
  ""itm.vendor"": ""itm"",
  ""segmentDisplay.vendor"": ""seg""
}
";

        // ── MIG-006: missing/duplicate ids — deterministic occurrence allocator ──

        [Fact]
        public void MissingAndDuplicateRuleIds_DeterministicAllocator()
        {
            AssertGoldenRaw(V1_MissingAndDuplicateRuleIds_DeterministicAllocator,
                Golden_MissingAndDuplicateRuleIds_DeterministicAllocator);

            // Identical source bytes convert identically twice (no random GUIDs).
            string a = MigrateRaw(V1_MissingAndDuplicateRuleIds_DeterministicAllocator);
            string b = MigrateRaw(V1_MissingAndDuplicateRuleIds_DeterministicAllocator);
            Assert.Equal(a, b);
        }

        private const string V1_MissingAndDuplicateRuleIds_DeterministicAllocator = @"
{
  ""schemaVersion"": 1,
  ""itm"": {
    ""rules"": [
      {
        ""id"": ""dup"",
        ""when"": { ""kind"": ""isTrue"", ""source"": { ""kind"": ""builtIn"", ""name"": ""DrsEnabled"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""lapInfo"" },
        ""hold"": { ""kind"": ""whileActive"" }
      },
      {
        ""id"": ""dup"",
        ""when"": { ""kind"": ""isTrue"", ""source"": { ""kind"": ""builtIn"", ""name"": ""DrsAvailable"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""fuelErsDrs"" },
        ""hold"": { ""kind"": ""whileActive"" }
      },
      {
        ""when"": { ""kind"": ""isTrue"", ""source"": { ""kind"": ""builtIn"", ""name"": ""PitLimiterOn"" } },
        ""show"": { ""kind"": ""page"", ""page"": ""tyreTemps"" },
        ""hold"": { ""kind"": ""whileActive"" }
      },
      {
        ""id"": ""ok"",
        ""when"": { ""kind"": ""isTrue"", ""source"": { ""kind"": ""builtIn"", ""name"": ""IsInPitLane"" } },
        ""show"": { ""kind"": ""special"", ""command"": ""logo"" },
        ""hold"": { ""kind"": ""forDuration"", ""durationMs"": 1000 }
      }
    ]
  }
}
";

        private const string Golden_MissingAndDuplicateRuleIds_DeterministicAllocator = @"
{
  ""schemaVersion"": 2,
  ""pages"": [],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [
      {
        ""kind"": ""seat"",
        ""id"": ""s-dup"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""lapInfo""
        },
        ""summons"": [
          {
            ""id"": ""dup"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""DrsEnabled""
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
        ""kind"": ""seat"",
        ""id"": ""s-r-v1-2"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""fuelErsDrs""
        },
        ""summons"": [
          {
            ""id"": ""r-v1-2"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""DrsAvailable""
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
        ""kind"": ""seat"",
        ""id"": ""s-r-v1-3"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""tyreTemps""
        },
        ""summons"": [
          {
            ""id"": ""r-v1-3"",
            ""condition"": {
              ""source"": {
                ""kind"": ""builtIn"",
                ""name"": ""PitLimiterOn""
              },
              ""operator"": ""isTrue""
            },
            ""lifetime"": {
              ""kind"": ""whileTrue""
            }
          }
        ]
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
    ""rules"": [
      {
        ""id"": ""ok"",
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
          ""durationMs"": 1000
        }
      }
    ]
  },
  ""settings"": {}
}
";

        // ── FZ-003: pageOrder absent vs explicit [] ──

        [Fact]
        public void PageOrder_ScreensExistNoneInRotation_EmitsEmptyArray()
        {
            AssertGolden(V1_ScreensExist_NoneInRotation, Golden_ScreensExist_NoneInRotation);
        }

        private const string V1_ScreensExist_NoneInRotation = @"
{
  ""schemaVersion"": 1,
  ""segmentDisplay"": {
    ""screens"": [
      { ""id"": ""s1"", ""name"": ""A"", ""contentKind"": ""speed"", ""inRotation"": false },
      { ""id"": ""s2"", ""name"": ""B"", ""text"": ""PIT"", ""inRotation"": false }
    ]
  }
}
";

        private const string Golden_ScreensExist_NoneInRotation = @"
{
  ""schemaVersion"": 2,
  ""pages"": [
    {
      ""kind"": ""hostedPage"",
      ""id"": ""s1"",
      ""name"": ""A"",
      ""base"": {
        ""content"": {
          ""kind"": ""speed""
        }
      }
    },
    {
      ""kind"": ""hostedPage"",
      ""id"": ""s2"",
      ""name"": ""B"",
      ""base"": {
        ""content"": {
          ""kind"": ""text"",
          ""text"": ""PIT""
        }
      }
    }
  ],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [],
    ""rest"": {
      ""idle"": {
        ""kind"": ""blank""
      }
    }
  },
  ""pageOrder"": [],
  ""fields"": {},
  ""wheelScreen"": {
    ""rules"": []
  },
  ""settings"": {}
}
";

        [Fact]
        public void PageOrder_NoScreens_Absent()
        {
            AssertGolden(V1_NoScreens_PageOrderAbsent, Golden_NoScreens_PageOrderAbsent);
        }

        private const string V1_NoScreens_PageOrderAbsent = @"
{
  ""schemaVersion"": 1,
  ""itm"": {
    ""basePage"": ""lapInfo""
  }
}
";

        private const string Golden_NoScreens_PageOrderAbsent = @"
{
  ""schemaVersion"": 2,
  ""pages"": [],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [],
    ""rest"": {
      ""inSessionPage"": {
        ""kind"": ""itmPage"",
        ""catalogPageId"": ""lapInfo""
      },
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
}
";

        // ── FZ-007: reserved p-v1- namespace escape ──

        [Fact]
        public void ReservedHostedPageId_UserPageNamedPv1Legacy_IsNamespaceEscaped()
        {
            AssertGolden(V1_UserPageNamedPv1Legacy, Golden_UserPageNamedPv1Legacy);
        }

        private const string V1_UserPageNamedPv1Legacy = @"
{
  ""schemaVersion"": 1,
  ""segmentDisplay"": {
    ""baseScreenId"": ""p-v1-legacy"",
    ""screens"": [
      { ""id"": ""p-v1-legacy"", ""name"": ""Legacy-looking"", ""text"": ""LEG"", ""inRotation"": true }
    ]
  }
}
";

        private const string Golden_UserPageNamedPv1Legacy = @"
{
  ""schemaVersion"": 2,
  ""pages"": [
    {
      ""kind"": ""hostedPage"",
      ""id"": ""u-p-v1-legacy"",
      ""name"": ""Legacy-looking"",
      ""base"": {
        ""content"": {
          ""kind"": ""text"",
          ""text"": ""LEG""
        }
      }
    }
  ],
  ""cycles"": [],
  ""priority"": {
    ""rows"": [],
    ""rest"": {
      ""inSessionPage"": {
        ""kind"": ""hostedPage"",
        ""id"": ""u-p-v1-legacy""
      },
      ""idle"": {
        ""kind"": ""blank""
      }
    }
  },
  ""pageOrder"": [
    {
      ""kind"": ""hostedPage"",
      ""id"": ""u-p-v1-legacy""
    }
  ],
  ""fields"": {},
  ""wheelScreen"": {
    ""rules"": []
  },
  ""settings"": {}
}
";

        // ── MIG-007: input immutability ──

        [Fact]
        public void Convert_DoesNotMutateV1Input()
        {
            var v1 = DisplayConfigSerializer.Load(V1_MainBattery_EveryV1Shape, _ => { });
            string before = DisplayConfigSerializer.Save(v1);
            DisplayConfigV2Migration.Convert(v1);
            string after = DisplayConfigSerializer.Save(v1);
            Assert.Equal(
                Encoding.UTF8.GetBytes(before.Replace("\r\n", "\n")),
                Encoding.UTF8.GetBytes(after.Replace("\r\n", "\n")));
        }

        [Fact]
        public void Convert_DoesNotMutateV1Input_RawDuplicateIds()
        {
            var v1 = DeserializeV1Raw(V1_MissingAndDuplicateRuleIds_DeterministicAllocator);
            string before = DisplayConfigSerializer.Save(v1);
            DisplayConfigV2Migration.Convert(v1);
            string after = DisplayConfigSerializer.Save(v1);
            Assert.Equal(
                Encoding.UTF8.GetBytes(before.Replace("\r\n", "\n")),
                Encoding.UTF8.GetBytes(after.Replace("\r\n", "\n")));
        }
    }
}


