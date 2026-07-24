using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FanaBridge.Adapters;
using FanaBridge.Display.Rules;
using FanaBridge.Profiles;
using FanaBridge.UI.Display;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Locks the schema-closure ExtensionData contract: unknown JSON members at every
    /// level survive load → save → editor commit → settings persistence, and the
    /// schema-version stamp is never rewritten down to CurrentSchemaVersion.
    /// </summary>
    public class SchemaExtensionDataTests
    {
        // Synthetic "v2-shaped" document: schemaVersion 2, unknown members at every
        // schema-closure level, unknown enum spellings, nested unknown objects/arrays.
        private static readonly string V2Document = @"
{
  ""schemaVersion"": 2,
  ""profileId"": ""v2-profile"",
  ""v2Top"": { ""nested"": true, ""arr"": [1, ""two"", { ""k"": 3 }] },
  ""v2TopFlag"": ""top"",
  ""itm"": {
    ""basePage"": ""lapInfo"",
    ""v2Itm"": 42,
    ""rules"": [
      {
        ""id"": ""r-a"",
        ""name"": ""Alpha"",
        ""enabled"": true,
        ""v2Rule"": ""alpha-only"",
        ""when"": {
          ""kind"": ""sparkles"",
          ""source"": {
            ""kind"": ""builtIn"",
            ""name"": ""Fuel"",
            ""v2Source"": { ""unit"": ""litres"" }
          },
          ""value"": 10,
          ""v2When"": [""x"", 1]
        },
        ""show"": {
          ""kind"": ""page"",
          ""page"": ""fuelErsDrs"",
          ""v2Show"": { ""fadeMs"": 250 }
        },
        ""hold"": {
          ""kind"": ""forDuration"",
          ""durationMs"": 5000,
          ""v2Hold"": ""linger""
        },
        ""runs"": ""always""
      },
      {
        ""id"": ""r-b"",
        ""name"": ""Bravo"",
        ""enabled"": true,
        ""v2Rule"": ""bravo-only"",
        ""when"": {
          ""kind"": ""greaterThan"",
          ""source"": { ""kind"": ""builtIn"", ""name"": ""Speed"" },
          ""value"": 100
        },
        ""show"": {
          ""kind"": ""hologram"",
          ""page"": ""tyreTemps""
        },
        ""hold"": {
          ""kind"": ""untilTomorrow"",
          ""durationMs"": 3000
        },
        ""runs"": ""whenever""
      },
      {
        ""id"": ""r-c"",
        ""name"": ""Charlie"",
        ""enabled"": true,
        ""v2Rule"": ""charlie-only"",
        ""when"": {
          ""kind"": ""lessThan"",
          ""source"": {
            ""kind"": ""builtIn"",
            ""name"": ""FuelPercent"",
            ""v2CSource"": ""cs""
          },
          ""value"": 12,
          ""v2CWhen"": true
        },
        ""show"": {
          ""kind"": ""page"",
          ""page"": ""fuelErsDrs"",
          ""v2CShow"": 7
        },
        ""hold"": {
          ""kind"": ""forDuration"",
          ""durationMs"": 4000,
          ""v2CHold"": [1, 2]
        },
        ""runs"": ""inGame""
      }
    ]
  },
  ""segmentDisplay"": {
    ""baseScreenId"": ""scr1"",
    ""v2Legacy"": { ""theme"": ""neon"" },
    ""screens"": [
      {
        ""id"": ""scr1"",
        ""name"": ""Pit"",
        ""text"": ""PIT"",
        ""contentKind"": ""text"",
        ""effect"": ""sparkle"",
        ""v2Screen"": { ""cols"": 3 },
        ""source"": {
          ""kind"": ""builtIn"",
          ""name"": ""Fuel"",
          ""v2ScreenSource"": true
        }
      },
      {
        ""id"": ""scr2"",
        ""name"": ""Wave"",
        ""contentKind"": ""wavelength"",
        ""v2Screen2"": ""wave""
      }
    ],
    ""rules"": []
  },
  ""fieldMappings"": {
    ""5"": {
      ""source"": {
        ""kind"": ""builtIn"",
        ""name"": ""Fuel"",
        ""v2MapSource"": ""keep""
      },
      ""format"": ""bare"",
      ""v2Mapping"": { ""precision"": 1 }
    }
  }
}";

        // Paths of unknown members that must survive every round-trip (exact JToken values).
        private static readonly (string path, string expectedJson)[] UnknownMembers =
        {
            ("v2Top", @"{""nested"":true,""arr"":[1,""two"",{""k"":3}]}"),
            ("v2TopFlag", @"""top"""),
            ("itm.v2Itm", @"42"),
            ("itm.rules[0].v2Rule", @"""alpha-only"""),
            ("itm.rules[0].when.v2When", @"[""x"",1]"),
            ("itm.rules[0].when.source.v2Source", @"{""unit"":""litres""}"),
            ("itm.rules[0].show.v2Show", @"{""fadeMs"":250}"),
            ("itm.rules[0].hold.v2Hold", @"""linger"""),
            ("itm.rules[1].v2Rule", @"""bravo-only"""),
            ("itm.rules[2].v2Rule", @"""charlie-only"""),
            ("itm.rules[2].when.v2CWhen", @"true"),
            ("itm.rules[2].when.source.v2CSource", @"""cs"""),
            ("itm.rules[2].show.v2CShow", @"7"),
            ("itm.rules[2].hold.v2CHold", @"[1,2]"),
            ("segmentDisplay.v2Legacy", @"{""theme"":""neon""}"),
            ("segmentDisplay.screens[0].v2Screen", @"{""cols"":3}"),
            ("segmentDisplay.screens[0].source.v2ScreenSource", @"true"),
            ("segmentDisplay.screens[1].v2Screen2", @"""wave"""),
            ("fieldMappings.5.v2Mapping", @"{""precision"":1}"),
            ("fieldMappings.5.source.v2MapSource", @"""keep"""),
        };

        private static DisplayCustomizationConfig Load(string json)
            => DisplayConfigSerializer.Load(json, _ => { });

        private static JObject ParseSaved(string saved)
            => JObject.Parse(saved);

        private static void AssertUnknownMembersSurvive(JObject root)
        {
            foreach (var (path, expectedJson) in UnknownMembers)
            {
                var token = Select(root, path);
                Assert.True(token != null && token.Type != JTokenType.Null,
                    "missing unknown member at " + path);
                var expected = JToken.Parse(expectedJson);
                Assert.True(JToken.DeepEquals(expected, token),
                    "value mismatch at " + path + ": expected " + expected
                    + ", got " + token);
            }
        }

        // Dot/bracket path into a JObject (rules[0], fieldMappings.5, etc.).
        private static JToken Select(JToken root, string path)
        {
            JToken cur = root;
            foreach (var part in path.Split('.'))
            {
                if (cur == null) return null;
                int bracket = part.IndexOf('[');
                if (bracket < 0)
                {
                    cur = cur[part];
                    continue;
                }
                string name = part.Substring(0, bracket);
                int close = part.IndexOf(']');
                int index = int.Parse(part.Substring(bracket + 1, close - bracket - 1));
                cur = cur[name];
                if (cur is JArray arr)
                    cur = index < arr.Count ? arr[index] : null;
                else
                    return null;
            }
            return cur;
        }

        [Fact]
        public void NormalizeThenIdempotent_ByteIdentical()
        {
            var first = DisplayConfigSerializer.Save(Load(V2Document));
            var second = DisplayConfigSerializer.Save(Load(first));
            Assert.Equal(first, second);
        }

        [Fact]
        public void LoadSave_PreservesEveryUnknownMember()
        {
            var saved = ParseSaved(DisplayConfigSerializer.Save(Load(V2Document)));
            AssertUnknownMembersSurvive(saved);

            // Unknown enum spellings (member values, not members) also survive.
            Assert.Equal("sparkles", (string)Select(saved, "itm.rules[0].when.kind"));
            Assert.Equal("hologram", (string)Select(saved, "itm.rules[1].show.kind"));
            Assert.Equal("untilTomorrow", (string)Select(saved, "itm.rules[1].hold.kind"));
            Assert.Equal("whenever", (string)Select(saved, "itm.rules[1].runs"));
            Assert.Equal("sparkle", (string)Select(saved, "segmentDisplay.screens[0].effect"));
        }

        [Fact]
        public void EditorNoOpCommit_PreservesUnknownMembers()
        {
            // Cheapest real commit that rebuilds the document: flip enable to the same
            // value (CloneRuleWithRun + Commit). Extension bags must ride through.
            var cfg = Load(V2Document);
            var model = new DisplayTriggersEditModel(cfg, itmDeviceId: 3);
            var after = model.SetRuleEnabled("r-a", enabled: true);

            var saved = ParseSaved(DisplayConfigSerializer.Save(after));
            AssertUnknownMembersSurvive(saved);
        }

        [Fact]
        public void EditorDrawerNoOpCommit_ToDraftUpdateRule_PreservesUnknownMembers()
        {
            // The drawer path: ToDraft → UpdateRule rebuilds the whole rule subtree from
            // the draft. r-c is a normally-editable rule (all-known enum values) with an
            // extension bag at every node — every bag must ride into the rebuilt rule.
            var cfg = Load(V2Document);
            var model = new DisplayTriggersEditModel(cfg, itmDeviceId: 3);
            var after = model.UpdateRule(DisplayTriggersEditModel.ToDraft(
                cfg.Itm.Rules.Single(r => r.Id == "r-c")));

            var saved = ParseSaved(DisplayConfigSerializer.Save(after));
            AssertUnknownMembersSurvive(saved);
        }

        [Fact]
        public void UnknownContentKindScreen_SurvivesRoundTrip()
        {
            // scr2's contentKind is a spelling this build does not know: the screen is
            // kept (excluded from survivors), the raw spelling and its unknown member
            // both round-trip.
            var saved = ParseSaved(DisplayConfigSerializer.Save(Load(V2Document)));
            Assert.Equal("wavelength", (string)Select(saved, "segmentDisplay.screens[1].contentKind"));
            Assert.Equal("wave", (string)Select(saved, "segmentDisplay.screens[1].v2Screen2"));
        }

        [Fact]
        public void PagesSetSource_PreservesSourceExtensionData()
        {
            var cfg = Load(V2Document);
            var model = new DisplayPagesEditModel(cfg, 3);
            var after = model.SetSource(5, PropertyKind.SimHubProperty, "Some.Property");

            var mapping = after.FieldMappings[5];
            Assert.Equal("Some.Property", mapping.Source.Name);
            Assert.Equal("keep", (string)mapping.Source.ExtensionData["v2MapSource"]);
        }

        [Fact]
        public void VirtualPagesSetSource_PreservesSourceExtensionData()
        {
            var cfg = Load(V2Document);
            var model = new DisplayVirtualPagesEditModel(cfg);
            var after = model.SetSource("scr1", PropertyKind.SimHubProperty, "Some.Property");

            var scr = after.Legacy.Screens.Single(s => s.Id == "scr1");
            Assert.Equal("Some.Property", scr.Source.Name);
            Assert.True((bool)scr.Source.ExtensionData["v2ScreenSource"]);
        }

        [Fact]
        public void VersionZero_NormalizesToCurrent_Intentionally()
        {
            // Accepted deviation from "round-trips as read": an explicit 0/negative
            // version is malformed input, normalized to current with a warning
            // (degrade visibly, refuse nothing). Higher versions are the protected case.
            var cfg = Load(@"{ ""schemaVersion"": 0 }");
            var saved = ParseSaved(DisplayConfigSerializer.Save(cfg));
            Assert.Equal(DisplayCustomizationConfig.CurrentSchemaVersion,
                (int)saved["schemaVersion"]);
        }

        [Fact]
        public void PersistencePath_SetSettingsGetSettings_PreservesUnknownMembers()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            inst.SetSettings(new JObject
            {
                ["wheelType"] = "PSWBMW",
                ["displayCustomization"] = JObject.Parse(V2Document),
            }, isDefault: false);

            var saved = (JObject)inst.GetSettings(false, false);
            var doc = saved["displayCustomization"] as JObject;
            Assert.NotNull(doc);
            AssertUnknownMembersSurvive(doc!);
        }

        [Fact]
        public void Isolation_EditingRuleA_DoesNotDisturbRuleB_ExtensionData()
        {
            var cfg = Load(V2Document);
            string ruleBBefore = JObject.FromObject(cfg.Itm.Rules.Single(r => r.Id == "r-b"))
                .ToString(Formatting.None);

            var model = new DisplayTriggersEditModel(cfg, itmDeviceId: 3);
            // Real edit of A (disable) — B must stay byte-identical including extension data.
            var after = model.SetRuleEnabled("r-a", enabled: false);

            var ruleB = after.Itm.Rules.Single(r => r.Id == "r-b");
            string ruleBAfter = JObject.FromObject(ruleB).ToString(Formatting.None);
            Assert.Equal(ruleBBefore, ruleBAfter);
            Assert.Equal("bravo-only", (string)ruleB.ExtensionData["v2Rule"]);
        }

        [Fact]
        public void VersionStamps_V2Keeps2_FreshIsCurrent()
        {
            var v2 = Load(V2Document);
            Assert.Equal(2, v2.SchemaVersion);
            var savedV2 = ParseSaved(DisplayConfigSerializer.Save(v2));
            Assert.Equal(2, (int)savedV2["schemaVersion"]);

            // Fresh document stamps CurrentSchemaVersion (1).
            var fresh = new DisplayCustomizationConfig();
            Assert.Equal(DisplayCustomizationConfig.CurrentSchemaVersion, fresh.SchemaVersion);
            var savedFresh = ParseSaved(DisplayConfigSerializer.Save(fresh));
            Assert.Equal(DisplayCustomizationConfig.CurrentSchemaVersion,
                (int)savedFresh["schemaVersion"]);

            // Editor commit preserves a higher SchemaVersion.
            var model = new DisplayTriggersEditModel(v2, itmDeviceId: 3);
            var after = model.SetRuleEnabled("r-a", true);
            Assert.Equal(2, after.SchemaVersion);
        }

        /// <summary>
        /// Reflection guard: every non-abstract class in the DisplayCustomizationConfig
        /// schema closure declares a [JsonExtensionData] member. List is explicit so a
        /// future class cannot silently opt out without updating this test.
        /// </summary>
        [Fact]
        public void SchemaClosure_EveryClassDeclaresJsonExtensionData()
        {
            // Explicit closure (see DisplayCustomizationConfig.cs / DisplayRule.cs /
            // LegacyScreen.cs / PropertySpec.cs). Add new schema types here when they land.
            Type[] closure =
            {
                typeof(DisplayCustomizationConfig),
                typeof(ItmRuleSet),
                typeof(LegacyRuleSet),
                typeof(FieldMapping),
                typeof(DisplayRule),
                typeof(RuleCondition),
                typeof(RuleTarget),
                typeof(HoldSpec),
                typeof(LegacyScreen),
                typeof(PropertySpec),
            };

            var missing = new List<string>();
            foreach (var type in closure)
            {
                var has = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Any(p => p.GetCustomAttribute<JsonExtensionDataAttribute>() != null);
                if (!has)
                    missing.Add(type.Name);
            }

            Assert.True(missing.Count == 0,
                "Schema-closure type(s) missing [JsonExtensionData]: "
                + string.Join(", ", missing));
        }

        private static FanatecWheelDeviceInstance InstanceFor(string wheelCode)
        {
            var profile = WheelProfileStore.FindByWheelType(wheelCode);
            Assert.NotNull(profile);
            var config = new DeviceConfig
            {
                Profile = profile,
                Capabilities = new WheelCapabilities(profile!),
            };
            return new FanatecWheelDeviceInstance(config);
        }
    }
}
