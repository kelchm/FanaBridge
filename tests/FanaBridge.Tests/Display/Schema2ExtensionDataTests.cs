using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Schema2;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Schema-v2 ExtensionData contract (mirrors S1's SchemaExtensionDataTests): every
    /// class in the v2 closure declares [JsonExtensionData], unknown members at every
    /// nesting level survive load → save, and a higher schemaVersion is never rewritten
    /// down. Pure parse/serialize — no validator in this phase.
    /// </summary>
    public class Schema2ExtensionDataTests
    {
        // Synthetic higher-version document with unknown members at every schema-closure
        // nesting level, plus unknown enum spellings.
        private static readonly string FutureDocument = @"
{
  ""schemaVersion"": 3,
  ""profileId"": ""v3-profile"",
  ""v3Top"": { ""nested"": true, ""arr"": [1, ""two"", { ""k"": 3 }] },
  ""v3TopFlag"": ""top"",
  ""pages"": [
    {
      ""kind"": ""itmPage"",
      ""catalogPageId"": ""fuelErsDrs"",
      ""v3ItmPage"": 1
    },
    {
      ""kind"": ""hostedPage"",
      ""id"": ""p-x"",
      ""name"": ""X"",
      ""v3Hosted"": ""h"",
      ""base"": {
        ""content"": {
          ""kind"": ""speed"",
          ""v3Content"": true
        },
        ""effect"": ""sparkle"",
        ""v3Base"": 9
      },
      ""layers"": [
        {
          ""id"": ""l-1"",
          ""content"": { ""kind"": ""text"", ""text"": ""A"", ""v3LayerContent"": 1 },
          ""effect"": ""blink"",
          ""condition"": {
            ""source"": {
              ""kind"": ""builtIn"",
              ""name"": ""Fuel"",
              ""v3Source"": { ""unit"": ""litres"" }
            },
            ""operator"": ""sparkles"",
            ""value"": 10,
            ""v3When"": [""x"", 1]
          },
          ""lifetime"": {
            ""kind"": ""untilTomorrow"",
            ""durationMs"": 3000,
            ""v3Life"": ""linger""
          },
          ""runs"": ""whenever"",
          ""v3Layer"": true
        }
      ]
    }
  ],
  ""cycles"": [
    {
      ""id"": ""c-1"",
      ""name"": ""C"",
      ""members"": [
        { ""kind"": ""itmPage"", ""catalogPageId"": ""lapInfo"", ""v3Member"": 2 }
      ],
      ""periodMs"": 4000,
      ""v3Cycle"": ""c""
    }
  ],
  ""priority"": {
    ""v3Priority"": true,
    ""rows"": [
      {
        ""kind"": ""seat"",
        ""id"": ""s-1"",
        ""v3Row"": ""seat-x"",
        ""target"": {
          ""kind"": ""itmPage"",
          ""catalogPageId"": ""lapTimes"",
          ""v3Target"": 7
        },
        ""summons"": [
          {
            ""id"": ""e-1"",
            ""v3Summon"": ""s"",
            ""condition"": {
              ""source"": { ""kind"": ""builtIn"", ""name"": ""Speed"", ""v3SumSrc"": 1 },
              ""operator"": ""greaterThan"",
              ""value"": 100,
              ""v3SumCond"": true
            },
            ""lifetime"": {
              ""kind"": ""forDuration"",
              ""durationMs"": 2000,
              ""v3SumLife"": [1, 2]
            },
            ""runs"": ""always""
          }
        ],
        ""bringUpLifetime"": {
          ""kind"": ""whileTrue"",
          ""v3BringUp"": ""pin""
        }
      },
      {
        ""kind"": ""satellite"",
        ""id"": ""s-sat"",
        ""childRef"": {
          ""field"": ""5"",
          ""overrideId"": ""o-1"",
          ""v3Child"": true
        },
        ""lifetime"": { ""kind"": ""forDuration"", ""durationMs"": 1000, ""v3SatLife"": 3 }
      },
      {
        ""kind"": ""manual"",
        ""returnToRestAfterMs"": 15000,
        ""v3Manual"": ""m""
      }
    ],
    ""rest"": {
      ""v3Rest"": 1,
      ""inSessionPage"": {
        ""kind"": ""hostedPage"",
        ""id"": ""p-x"",
        ""v3InSession"": true
      },
      ""idle"": {
        ""kind"": ""screen"",
        ""screen"": ""logo"",
        ""v3Idle"": ""i""
      }
    }
  },
  ""pageOrder"": [
    { ""kind"": ""itmPage"", ""catalogPageId"": ""lapInfo"", ""v3Order"": 1 }
  ],
  ""fields"": {
    ""5"": {
      ""v3Field"": { ""precision"": 1 },
      ""base"": {
        ""source"": {
          ""kind"": ""simHubProperty"",
          ""name"": ""DataCorePlugin.GameData.Fuel"",
          ""v3FieldSrc"": ""keep""
        },
        ""format"": ""bare"",
        ""v3FieldBase"": true
      },
      ""overrides"": [
        {
          ""id"": ""o-1"",
          ""writes"": ""suffix"",
          ""content"": { ""kind"": ""text"", ""text"": ""X"", ""v3OvContent"": 1 },
          ""alignment"": ""leftish"",
          ""effect"": ""none"",
          ""condition"": {
            ""source"": { ""kind"": ""itmField"", ""name"": ""self"", ""v3OvSrc"": 2 },
            ""operator"": ""lessThan"",
            ""value"": 1,
            ""v3OvCond"": 3
          },
          ""lifetime"": {
            ""kind"": ""onChange"",
            ""direction"": ""sideways"",
            ""then"": ""untilDismissed"",
            ""v3OvLife"": 4
          },
          ""runs"": ""always"",
          ""actsAsEntrypoint"": true,
          ""v3Override"": ""o""
        }
      ]
    }
  },
  ""wheelScreen"": {
    ""v3Wheel"": true,
    ""rules"": [
      {
        ""id"": ""w-1"",
        ""screen"": ""holodeck"",
        ""condition"": {
          ""source"": { ""kind"": ""builtIn"", ""name"": ""Fuel"", ""v3WsSrc"": 1 },
          ""operator"": ""isTrue"",
          ""v3WsCond"": 2
        },
        ""lifetime"": { ""kind"": ""forDuration"", ""durationMs"": 60000, ""v3WsLife"": 3 },
        ""runs"": ""idle"",
        ""v3WsRule"": ""r""
      }
    ]
  },
  ""settings"": {
    ""rejectUncommandedChanges"": true,
    ""mode"": ""telepathy"",
    ""v3Settings"": 42
  }
}";

        private static readonly (string path, string expectedJson)[] UnknownMembers =
        {
            ("v3Top", @"{""nested"":true,""arr"":[1,""two"",{""k"":3}]}"),
            ("v3TopFlag", @"""top"""),
            ("pages[0].v3ItmPage", @"1"),
            ("pages[1].v3Hosted", @"""h"""),
            ("pages[1].base.v3Base", @"9"),
            ("pages[1].base.content.v3Content", @"true"),
            ("pages[1].layers[0].v3Layer", @"true"),
            ("pages[1].layers[0].content.v3LayerContent", @"1"),
            ("pages[1].layers[0].condition.v3When", @"[""x"",1]"),
            ("pages[1].layers[0].condition.source.v3Source", @"{""unit"":""litres""}"),
            ("pages[1].layers[0].lifetime.v3Life", @"""linger"""),
            ("cycles[0].v3Cycle", @"""c"""),
            ("cycles[0].members[0].v3Member", @"2"),
            ("priority.v3Priority", @"true"),
            ("priority.rows[0].v3Row", @"""seat-x"""),
            ("priority.rows[0].target.v3Target", @"7"),
            ("priority.rows[0].summons[0].v3Summon", @"""s"""),
            ("priority.rows[0].summons[0].condition.v3SumCond", @"true"),
            ("priority.rows[0].summons[0].condition.source.v3SumSrc", @"1"),
            ("priority.rows[0].summons[0].lifetime.v3SumLife", @"[1,2]"),
            ("priority.rows[0].bringUpLifetime.v3BringUp", @"""pin"""),
            ("priority.rows[1].childRef.v3Child", @"true"),
            ("priority.rows[1].lifetime.v3SatLife", @"3"),
            ("priority.rows[2].v3Manual", @"""m"""),
            ("priority.rest.v3Rest", @"1"),
            ("priority.rest.inSessionPage.v3InSession", @"true"),
            ("priority.rest.idle.v3Idle", @"""i"""),
            ("pageOrder[0].v3Order", @"1"),
            ("fields.5.v3Field", @"{""precision"":1}"),
            ("fields.5.base.v3FieldBase", @"true"),
            ("fields.5.base.source.v3FieldSrc", @"""keep"""),
            ("fields.5.overrides[0].v3Override", @"""o"""),
            ("fields.5.overrides[0].content.v3OvContent", @"1"),
            ("fields.5.overrides[0].condition.v3OvCond", @"3"),
            ("fields.5.overrides[0].condition.source.v3OvSrc", @"2"),
            ("fields.5.overrides[0].lifetime.v3OvLife", @"4"),
            ("wheelScreen.v3Wheel", @"true"),
            ("wheelScreen.rules[0].v3WsRule", @"""r"""),
            ("wheelScreen.rules[0].condition.v3WsCond", @"2"),
            ("wheelScreen.rules[0].condition.source.v3WsSrc", @"1"),
            ("wheelScreen.rules[0].lifetime.v3WsLife", @"3"),
            ("settings.v3Settings", @"42"),
        };

        /// <summary>
        /// Table of every Raw-backed discriminator: path in a minimal document, unknown
        /// spelling, and the expected parsed fallback enum name (for assertion).
        /// </summary>
        public static IEnumerable<object[]> UnknownSpellingCases()
        {
            // page / pageRef / row / content / source / writes / idle / then / direction / kind
            yield return new object[]
            {
                "page.kind",
                @"{ ""schemaVersion"": 2, ""pages"": [ { ""kind"": ""pageKindX"", ""id"": ""p"" } ] }",
                "pages[0].kind",
                "pageKindX",
                typeof(PageEntry),
                "Kind",
                PageEntryKind.Unknown,
            };
            yield return new object[]
            {
                "pageRef.kind",
                @"{ ""schemaVersion"": 2, ""pageOrder"": [ { ""kind"": ""pageRefX"", ""id"": ""c"" } ] }",
                "pageOrder[0].kind",
                "pageRefX",
                typeof(PageRef),
                "Kind",
                PageRefKind.Unknown,
            };
            yield return new object[]
            {
                "row.kind",
                @"{ ""schemaVersion"": 2, ""priority"": { ""rows"": [ { ""kind"": ""rowKindX"", ""id"": ""r"" } ] } }",
                "priority.rows[0].kind",
                "rowKindX",
                typeof(PriorityRow),
                "Kind",
                PriorityRowKind.Unknown,
            };
            yield return new object[]
            {
                "content.kind",
                @"{ ""schemaVersion"": 2, ""pages"": [ { ""kind"": ""hostedPage"", ""id"": ""p"", ""name"": ""P"", ""base"": { ""content"": { ""kind"": ""contentKindX"" } } } ] }",
                "pages[0].base.content.kind",
                "contentKindX",
                typeof(ContentObject),
                "Kind",
                ContentKind.Unknown,
            };
            yield return new object[]
            {
                "source.kind",
                @"{ ""schemaVersion"": 2, ""fields"": { ""1"": { ""base"": { ""source"": { ""kind"": ""sourceKindX"", ""name"": ""n"" } } } } }",
                "fields.1.base.source.kind",
                "sourceKindX",
                typeof(ValueSource),
                "Kind",
                ValueSourceKind.Unknown,
            };
            yield return new object[]
            {
                "writes",
                @"{ ""schemaVersion"": 2, ""fields"": { ""1"": { ""overrides"": [ { ""id"": ""o"", ""writes"": ""writesX"", ""content"": { ""kind"": ""text"", ""text"": ""t"" } } ] } } }",
                "fields.1.overrides[0].writes",
                "writesX",
                typeof(FieldOverride),
                "Writes",
                FieldWrites.Unknown,
            };
            yield return new object[]
            {
                "idle.kind",
                @"{ ""schemaVersion"": 2, ""priority"": { ""rest"": { ""idle"": { ""kind"": ""idleKindX"" } } } }",
                "priority.rest.idle.kind",
                "idleKindX",
                typeof(IdleSpec),
                "Kind",
                IdleKind.Unknown,
            };
            yield return new object[]
            {
                "then",
                @"{ ""schemaVersion"": 2, ""priority"": { ""rows"": [ { ""kind"": ""seat"", ""id"": ""s"", ""summons"": [ { ""id"": ""e"", ""lifetime"": { ""kind"": ""onChange"", ""then"": ""thenX"" } } ] } ] } }",
                "priority.rows[0].summons[0].lifetime.then",
                "thenX",
                typeof(Lifetime),
                "Then",
                LifetimeThen.Unknown,
            };
            yield return new object[]
            {
                "direction",
                @"{ ""schemaVersion"": 2, ""priority"": { ""rows"": [ { ""kind"": ""seat"", ""id"": ""s"", ""summons"": [ { ""id"": ""e"", ""lifetime"": { ""kind"": ""onChange"", ""direction"": ""directionX"" } } ] } ] } }",
                "priority.rows[0].summons[0].lifetime.direction",
                "directionX",
                typeof(Lifetime),
                "Direction",
                ChangeDirection.Unknown,
            };
            yield return new object[]
            {
                "alias.kind",
                null!, // handled specially via CatalogLoader (no schema JSON)
                "aliases[0].kind",
                "aliasKindX",
                typeof(AliasEntry),
                "Kind",
                AliasKind.Unknown,
            };
        }

        private static DisplayConfigV2 Load(string json)
            => DisplayConfigV2Serializer.Load(json, _ => { });

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

        private static JToken? Select(JToken root, string path)
        {
            JToken? cur = root;
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
        public void LoadSave_Idempotent_ByteIdentical()
        {
            var first = DisplayConfigV2Serializer.Save(Load(FutureDocument));
            var second = DisplayConfigV2Serializer.Save(Load(first));
            Assert.Equal(first, second);
        }

        [Fact]
        public void LoadSave_PreservesEveryUnknownMember()
        {
            var saved = ParseSaved(DisplayConfigV2Serializer.Save(Load(FutureDocument)));
            AssertUnknownMembersSurvive(saved);

            // Unknown enum spellings (member values, not members) also survive.
            Assert.Equal("sparkle", (string?)Select(saved, "pages[1].base.effect"));
            Assert.Equal("sparkles", (string?)Select(saved, "pages[1].layers[0].condition.operator"));
            Assert.Equal("untilTomorrow", (string?)Select(saved, "pages[1].layers[0].lifetime.kind"));
            Assert.Equal("whenever", (string?)Select(saved, "pages[1].layers[0].runs"));
            Assert.Equal("leftish", (string?)Select(saved, "fields.5.overrides[0].alignment"));
            Assert.Equal("sideways", (string?)Select(saved, "fields.5.overrides[0].lifetime.direction"));
            Assert.Equal("holodeck", (string?)Select(saved, "wheelScreen.rules[0].screen"));
            Assert.Equal("telepathy", (string?)Select(saved, "settings.mode"));
        }

        [Fact]
        public void SchemaVersion3_IsPreservedOnSave()
        {
            var cfg = Load(FutureDocument);
            Assert.Equal(3, cfg.SchemaVersion);
            var saved = ParseSaved(DisplayConfigV2Serializer.Save(cfg));
            var ver = saved["schemaVersion"];
            Assert.NotNull(ver);
            Assert.Equal(3, (int)ver!);
        }

        [Fact]
        public void FreshDocument_StampsCurrentSchemaVersion()
        {
            var fresh = new DisplayConfigV2();
            Assert.Equal(DisplayConfigV2.CurrentSchemaVersion, fresh.SchemaVersion);
            var saved = ParseSaved(DisplayConfigV2Serializer.Save(fresh));
            var ver = saved["schemaVersion"];
            Assert.NotNull(ver);
            Assert.Equal(DisplayConfigV2.CurrentSchemaVersion, (int)ver!);
        }

        [Fact]
        public void UnparseableInput_YieldsDefaults_NeverThrows()
        {
            var warnings = new List<string>();
            var cfg = DisplayConfigV2Serializer.Load("{ not json", warnings.Add);
            Assert.NotNull(cfg);
            Assert.Equal(DisplayConfigV2.CurrentSchemaVersion, cfg.SchemaVersion);
            Assert.NotEmpty(warnings);
        }

        [Fact]
        public void Load_NeverThrows_OnWrongType_ExplicitNull_ThrowingLogger()
        {
            // Wrong-typed root value / member that confuses the binder.
            var cfg = DisplayConfigV2Serializer.Load("{\"schemaVersion\":\"nope\"}", _ => { });
            Assert.NotNull(cfg);

            cfg = DisplayConfigV2Serializer.Load("null", _ => { });
            Assert.NotNull(cfg);

            cfg = DisplayConfigV2Serializer.Load(null, _ => { });
            Assert.NotNull(cfg);

            // Throwing logger must not break the never-throws contract.
            cfg = DisplayConfigV2Serializer.Load("{ not json",
                _ => throw new InvalidOperationException("logger boom"));
            Assert.NotNull(cfg);
            Assert.Equal(DisplayConfigV2.CurrentSchemaVersion, cfg.SchemaVersion);
        }

        [Fact]
        public void HostedPageBase_AbsentAndNull_AreSameState()
        {
            var absent = Load(@"{ ""schemaVersion"": 2, ""pages"": [
              { ""kind"": ""hostedPage"", ""id"": ""p1"", ""name"": ""A"" } ] }");
            Assert.Null(absent.Pages[0].Base);

            var explicitNull = Load(@"{ ""schemaVersion"": 2, ""pages"": [
              { ""kind"": ""hostedPage"", ""id"": ""p1"", ""name"": ""A"", ""base"": null } ] }");
            Assert.Null(explicitNull.Pages[0].Base);

            // Both serialize without a base member.
            var savedAbsent = ParseSaved(DisplayConfigV2Serializer.Save(absent));
            var savedNull = ParseSaved(DisplayConfigV2Serializer.Save(explicitNull));
            Assert.Null(Select(savedAbsent, "pages[0].base"));
            Assert.Null(Select(savedNull, "pages[0].base"));
        }

        [Fact]
        public void DefaultSuppression_EnabledRunsAlignmentActsAsEntrypoint()
        {
            // Build an override at all defaults that have behavioral weight, then assert
            // the suppressed keys are absent from the wire form.
            var cfg = new DisplayConfigV2();
            cfg.Fields[5] = new FieldEntry
            {
                Overrides =
                {
                    new FieldOverride
                    {
                        Id = "o-def",
                        Writes = FieldWrites.Suffix,
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "X" },
                        // defaults: alignment left, enabled true, runs inGame, actsAsEntrypoint false
                        Alignment = FieldAlignment.Left,
                        Enabled = true,
                        Runs = RunsWhen.InGame,
                        ActsAsEntrypoint = false,
                    },
                },
            };
            cfg.Pages.Add(new PageEntry
            {
                Kind = PageEntryKind.HostedPage,
                Id = "p1",
                Name = "P",
                Layers = new List<LayerEntry>
                {
                    new LayerEntry
                    {
                        Id = "l1",
                        Content = new ContentObject { Kind = ContentKind.Text, Text = "Y" },
                        Enabled = true,
                        Runs = RunsWhen.InGame,
                        ActsAsEntrypoint = false,
                    },
                },
            });

            var saved = ParseSaved(DisplayConfigV2Serializer.Save(cfg));
            var ov = Select(saved, "fields.5.overrides[0]") as JObject;
            Assert.NotNull(ov);
            Assert.Null(ov!["enabled"]);
            Assert.Null(ov["runs"]);
            Assert.Null(ov["alignment"]);
            Assert.Null(ov["actsAsEntrypoint"]);

            var layer = Select(saved, "pages[0].layers[0]") as JObject;
            Assert.NotNull(layer);
            Assert.Null(layer!["enabled"]);
            Assert.Null(layer["runs"]);
            Assert.Null(layer["actsAsEntrypoint"]);
        }

        [Theory]
        [MemberData(nameof(UnknownSpellingCases))]
        public void UnknownSpelling_RoundTrips_ParsedFallbackAndExactSpelling(
            string label,
            string? schemaJson,
            string jsonPath,
            string unknownSpelling,
            Type carrierType,
            string parsedPropertyName,
            object expectedFallback)
        {
            Assert.NotNull(label);

            if (carrierType == typeof(AliasEntry))
            {
                string aliasJson =
                    "{ \"aliasTableVersion\": 1, \"aliases\": [ { \"kind\": \""
                    + unknownSpelling
                    + "\", \"ref\": \"x\", \"alias\": \"X\" } ] }";
                var table = CatalogLoader.LoadAliasTable(aliasJson, _ => { });
                Assert.Single(table.Aliases);
                Assert.Equal(AliasKind.Unknown, table.Aliases[0].Kind);
                Assert.Equal(unknownSpelling, table.Aliases[0].KindRaw);

                var resaved = JObject.Parse(CatalogLoader.Save(table));
                Assert.Equal(unknownSpelling, (string?)Select(resaved, "aliases[0].kind"));
                return;
            }

            Assert.NotNull(schemaJson);
            var cfg = Load(schemaJson!);
            object carrier = ResolveCarrier(cfg, carrierType, jsonPath);
            Assert.NotNull(carrier);

            var prop = carrierType.GetProperty(parsedPropertyName,
                BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(prop);
            // Parsed accessor falls back to Unknown; raw spelling is preserved on save.
            Assert.Equal(expectedFallback, prop!.GetValue(carrier));

            var saved = ParseSaved(DisplayConfigV2Serializer.Save(cfg));
            Assert.Equal(unknownSpelling, (string?)Select(saved, jsonPath));
        }

        private static object ResolveCarrier(DisplayConfigV2 cfg, Type carrierType, string jsonPath)
        {
            if (carrierType == typeof(PageEntry))
                return cfg.Pages[0];
            if (carrierType == typeof(PageRef))
                return cfg.PageOrder![0];
            if (carrierType == typeof(PriorityRow))
                return cfg.Priority.Rows[0];
            if (carrierType == typeof(ContentObject))
                return cfg.Pages[0].Base!.Content!;
            if (carrierType == typeof(ValueSource))
                return cfg.Fields[1].Base!.Source!;
            if (carrierType == typeof(FieldOverride))
                return cfg.Fields[1].Overrides[0];
            if (carrierType == typeof(IdleSpec))
                return cfg.Priority.Rest!.Idle!;
            if (carrierType == typeof(Lifetime))
                return cfg.Priority.Rows[0].Summons![0].Lifetime!;
            throw new InvalidOperationException("unmapped carrier " + carrierType.Name
                + " for path " + jsonPath);
        }

        /// <summary>
        /// Reflection guard: every public non-abstract class in the Schema2 namespace
        /// declares a [JsonExtensionData] member. Discovered by namespace scan so a
        /// new type cannot silently opt out.
        /// </summary>
        [Fact]
        public void SchemaClosure_EveryClassDeclaresJsonExtensionData()
        {
            var closure = typeof(DisplayConfigV2).Assembly.GetTypes()
                .Where(t => t.IsClass
                    && t.IsPublic
                    && !t.IsAbstract
                    && t.Namespace == "FanaBridge.Display.Schema2")
                .OrderBy(t => t.Name)
                .ToList();

            Assert.NotEmpty(closure);

            var missing = new List<string>();
            foreach (var type in closure)
            {
                var has = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Any(p => p.GetCustomAttribute<JsonExtensionDataAttribute>() != null);
                if (!has)
                    missing.Add(type.Name);
            }

            Assert.True(missing.Count == 0,
                "Schema2 namespace type(s) missing [JsonExtensionData]: "
                + string.Join(", ", missing)
                + " (scanned: " + string.Join(", ", closure.Select(t => t.Name)) + ")");
        }
    }
}
