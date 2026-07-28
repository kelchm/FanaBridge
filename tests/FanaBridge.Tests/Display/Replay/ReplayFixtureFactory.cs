using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Schema2;
using FanaBridge.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Tests.Display.Replay
{
    /// <summary>
    /// Builds paired v1/v2 documents for each matrix cell and materializes them under
    /// <c>Display/Replay/Fixtures/</c>. Generation is deterministic (InvariantCulture).
    /// </summary>
    internal static class ReplayFixtureFactory
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            Culture = CultureInfo.InvariantCulture,
        };

        public static string FixturesDirectory()
        {
            // Prefer source tree (when tests run from bin/… they walk up to repo root).
            string root = TestSupport.TestPaths.RepoRoot();
            return Path.Combine(root, "tests", "FanaBridge.Tests", "Display", "Replay", "Fixtures");
        }

        public static (string v1Json, string v2Json) BuildPair(ReplayCell cell)
        {
            string v1 = BuildV1(cell);
            string v2 = BuildV2(cell);
            return (v1, v2);
        }

        public static void MaterializeAll(IEnumerable<ReplayCell> cells)
        {
            string dir = FixturesDirectory();
            Directory.CreateDirectory(dir);
            foreach (var cell in cells)
            {
                if (!cell.IsRepresentable)
                    continue;
                var (v1, v2) = BuildPair(cell);
                File.WriteAllText(Path.Combine(dir, cell.Id + ".v1.json"), v1, Encoding.UTF8);
                File.WriteAllText(Path.Combine(dir, cell.Id + ".v2.json"), v2, Encoding.UTF8);
            }
        }

        public static string LoadOrBuildV1(ReplayCell cell)
        {
            string path = Path.Combine(FixturesDirectory(), cell.Id + ".v1.json");
            if (File.Exists(path))
                return File.ReadAllText(path);
            return BuildV1(cell);
        }

        public static string LoadOrBuildV2(ReplayCell cell)
        {
            string path = Path.Combine(FixturesDirectory(), cell.Id + ".v2.json");
            if (File.Exists(path))
                return File.ReadAllText(path);
            return BuildV2(cell);
        }

        // ── v1 document ──────────────────────────────────────────────────

        private static string BuildV1(ReplayCell cell)
        {
            var when = BuildV1When(cell);
            var show = BuildV1Show(cell);
            var hold = BuildV1Hold(cell);
            string runs = EnumText.Write(ReplayMatrix.ToV1Runs(cell.Runs));

            // Segment world only when the cell actually exercises col01 (segment/special
            // targets or segment-only device). Pure ITM page/cycle cells omit it so the
            // v9 legacy base-screen paint does not invent a col01 stream v2 has no twin for.
            bool needsSegmentWorld = cell.Device == ReplayDevice.SegmentOnly
                || cell.Target == ReplayTarget.SegmentScreen
                || cell.Target == ReplayTarget.Special
                || (cell.KeptBehaviorName != null
                    && (cell.KeptBehaviorName.Contains("wheel-screen")
                        || cell.KeptBehaviorName.Contains("blank-compile")
                        || cell.KeptBehaviorName.Contains("special")));

            var screens = new JArray();
            if (needsSegmentWorld)
            {
                screens.Add(new JObject
                {
                    ["id"] = "spd",
                    ["name"] = "Speed",
                    ["contentKind"] = "speed",
                    ["inRotation"] = true,
                });
                screens.Add(new JObject
                {
                    ["id"] = "pit",
                    ["name"] = "Pit",
                    ["contentKind"] = "text",
                    ["text"] = "PIT",
                    ["inRotation"] = false,
                });
                screens.Add(new JObject
                {
                    ["id"] = "gear",
                    ["name"] = "Gear",
                    ["contentKind"] = "gear",
                    ["inRotation"] = false,
                });
            }

            var legacyRules = new JArray();
            var itmRules = new JArray();

            var rule = new JObject
            {
                ["id"] = "r1",
                ["name"] = "replay-r1",
                ["when"] = when,
                ["show"] = show,
                ["hold"] = hold,
                ["runs"] = runs,
            };

            if (cell.Target == ReplayTarget.SegmentScreen || cell.Target == ReplayTarget.Special)
                legacyRules.Add(rule);
            else
                itmRules.Add(rule);

            // Dual-rule kept cells: supersede / special outrank extras.
            if (cell.KeptBehaviorName == "supersede-retired-untilDismissed-resumes")
            {
                itmRules.Clear();
                itmRules.Add(new JObject
                {
                    ["id"] = "r-low",
                    ["when"] = JObject.FromObject(when),
                    ["show"] = new JObject { ["kind"] = "page", ["page"] = "fuelErsDrs" },
                    ["hold"] = new JObject { ["kind"] = "untilDismissed" },
                    ["runs"] = "inGame",
                });
                itmRules.Add(new JObject
                {
                    ["id"] = "r-high",
                    ["when"] = new JObject
                    {
                        ["kind"] = "isTrue",
                        ["source"] = new JObject { ["kind"] = "builtIn", ["name"] = "PitLimiterOn" },
                    },
                    ["show"] = new JObject { ["kind"] = "page", ["page"] = "tyreTemps" },
                    ["hold"] = new JObject { ["kind"] = "whileActive" },
                    ["runs"] = "inGame",
                });
            }

            if (cell.KeptBehaviorName == "itm-special-outranks-legacy-special")
            {
                itmRules.Clear();
                legacyRules.Clear();
                itmRules.Add(new JObject
                {
                    ["id"] = "r-itm-special",
                    ["when"] = new JObject
                    {
                        ["kind"] = "isTrue",
                        ["source"] = new JObject { ["kind"] = "builtIn", ["name"] = "IsInPitLane" },
                    },
                    ["show"] = new JObject { ["kind"] = "special", ["command"] = "raceFlag" },
                    ["hold"] = new JObject { ["kind"] = "whileActive" },
                    ["runs"] = "inGame",
                });
                legacyRules.Add(new JObject
                {
                    ["id"] = "r-leg-special",
                    ["when"] = new JObject
                    {
                        ["kind"] = "isTrue",
                        ["source"] = new JObject { ["kind"] = "builtIn", ["name"] = "IsInPitLane" },
                    },
                    ["show"] = new JObject { ["kind"] = "special", ["command"] = "drs" },
                    ["hold"] = new JObject { ["kind"] = "whileActive" },
                    ["runs"] = "inGame",
                });
            }

            var root = new JObject
            {
                ["schemaVersion"] = 1,
                ["itm"] = new JObject
                {
                    ["basePage"] = "lapInfo",
                    ["rules"] = itmRules,
                },
            };

            if (needsSegmentWorld)
            {
                root["segmentDisplay"] = new JObject
                {
                    ["baseScreenId"] = "spd",
                    ["screens"] = screens,
                    ["rules"] = legacyRules,
                };
            }
            else if (legacyRules.Count > 0)
            {
                root["segmentDisplay"] = new JObject
                {
                    ["screens"] = screens,
                    ["rules"] = legacyRules,
                };
            }

            // Field mappings for param-budget kept cells.
            if (cell.KeptBehaviorName == "param-budget-at-16"
                || cell.KeptBehaviorName == "param-budget-at-17")
            {
                int n = cell.KeptBehaviorName.EndsWith("17", StringComparison.Ordinal) ? 17 : 16;
                var maps = new JObject();
                for (int i = 1; i <= n; i++)
                {
                    maps[i.ToString(CultureInfo.InvariantCulture)] = new JObject
                    {
                        ["source"] = new JObject
                        {
                            ["kind"] = "builtIn",
                            ["name"] = "Fuel",
                        },
                    };
                }
                root["fieldMappings"] = maps;
            }

            return root.ToString(Formatting.Indented);
        }

        private static JObject BuildV1When(ReplayCell cell)
        {
            var kind = ReplayMatrix.ToV1Condition(cell.Condition);
            string kindText = EnumText.Write(kind);
            var srcName = ReplayMatrix.IsBoolLevel(cell.Condition)
                ? "IsInPitLane"
                : ReplayMatrix.IsEdge(cell.Condition)
                    ? "Gear"
                    : "Fuel";

            var when = new JObject
            {
                ["kind"] = kindText,
                ["source"] = new JObject
                {
                    ["kind"] = "builtIn",
                    ["name"] = srcName,
                },
            };

            if (!ReplayMatrix.IsBoolLevel(cell.Condition) && !ReplayMatrix.IsEdge(cell.Condition))
                when["value"] = 10;

            if (cell.Hysteresis.HasValue)
                when["hysteresis"] = cell.Hysteresis.Value;

            return when;
        }

        private static JObject BuildV1Show(ReplayCell cell)
        {
            switch (cell.Target)
            {
                case ReplayTarget.Page:
                    return new JObject { ["kind"] = "page", ["page"] = "fuelErsDrs" };
                case ReplayTarget.SegmentScreen:
                    return new JObject { ["kind"] = "segmentScreen", ["screenId"] = "pit" };
                case ReplayTarget.Cycle:
                    return new JObject
                    {
                        ["kind"] = "cycle",
                        ["pages"] = new JArray { "fuelErsDrs", "tyreTemps" },
                        ["periodMs"] = 3000,
                    };
                case ReplayTarget.Special:
                    return new JObject { ["kind"] = "special", ["command"] = "raceFlag" };
                default:
                    return new JObject { ["kind"] = "page", ["page"] = "fuelErsDrs" };
            }
        }

        private static JObject BuildV1Hold(ReplayCell cell)
        {
            var kind = ReplayMatrix.ToV1Hold(cell.Hold);
            // Edge conditions cannot use WhileActive — coerce to ForDuration in the document.
            if (ReplayMatrix.IsEdge(cell.Condition) && kind == HoldKind.WhileActive)
                kind = HoldKind.ForDuration;

            var hold = new JObject { ["kind"] = EnumText.Write(kind) };
            if (kind == HoldKind.ForDuration)
                hold["durationMs"] = 5000;
            return hold;
        }

        // ── v2 document ──────────────────────────────────────────────────

        private static string BuildV2(ReplayCell cell)
        {
            var pages = new JArray
            {
                new JObject
                {
                    ["kind"] = "hostedPage",
                    ["id"] = "spd",
                    ["name"] = "Speed",
                    ["base"] = new JObject
                    {
                        ["content"] = new JObject { ["kind"] = "speed" },
                    },
                },
                new JObject
                {
                    ["kind"] = "hostedPage",
                    ["id"] = "pit",
                    ["name"] = "Pit",
                    ["base"] = new JObject
                    {
                        ["content"] = new JObject
                        {
                            ["kind"] = "text",
                            ["text"] = "PIT",
                        },
                    },
                },
                new JObject
                {
                    ["kind"] = "hostedPage",
                    ["id"] = "gear",
                    ["name"] = "Gear",
                    ["base"] = new JObject
                    {
                        ["content"] = new JObject { ["kind"] = "gear" },
                    },
                },
            };

            // Cycle definition when needed.
            var cycles = new JArray();
            if (cell.Target == ReplayTarget.Cycle
                || (cell.KeptBehaviorName != null
                    && cell.KeptBehaviorName.StartsWith("cycle", StringComparison.Ordinal)))
            {
                cycles.Add(new JObject
                {
                    ["id"] = "c1",
                    ["name"] = "Fuel/Tyres",
                    ["periodMs"] = 3000,
                    ["members"] = new JArray
                    {
                        new JObject { ["kind"] = "itmPage", ["catalogPageId"] = "fuelErsDrs" },
                        new JObject { ["kind"] = "itmPage", ["catalogPageId"] = "tyreTemps" },
                    },
                });
            }

            var rows = new JArray();

            // Seat row for the primary rule (when it maps to an ITM/hosted destination).
            if (cell.Target != ReplayTarget.Special || cell.Device != ReplayDevice.SegmentOnly)
                rows.Add(BuildV2Seat(cell));

            rows.Add(new JObject { ["kind"] = "manual" });

            // Rest floor: document rest is the sole base-page producer (no DefaultWirePage).
            JObject restInSession;
            if (cell.Device == ReplayDevice.SegmentOnly
                || cell.Target == ReplayTarget.SegmentScreen)
            {
                restInSession = new JObject
                {
                    ["kind"] = "hostedPage",
                    ["id"] = "spd",
                };
            }
            else
            {
                restInSession = new JObject
                {
                    ["kind"] = "itmPage",
                    ["catalogPageId"] = "lapInfo",
                };
            }

            var priority = new JObject
            {
                ["rows"] = rows,
                ["rest"] = new JObject
                {
                    ["inSessionPage"] = restInSession,
                    ["landingPage"] = restInSession.DeepClone(),
                    ["idle"] = new JObject { ["kind"] = "blank" },
                },
            };

            var wheelScreen = new JObject { ["rules"] = new JArray() };
            if (cell.Target == ReplayTarget.Special
                || (cell.KeptBehaviorName != null
                    && cell.KeptBehaviorName.Contains("wheel-screen")))
            {
                // Special via wheel-screen rule (v2 surface for firmware specials).
                ((JArray)wheelScreen["rules"]!).Add(new JObject
                {
                    ["id"] = "ws-special",
                    ["command"] = "raceFlag",
                    ["condition"] = BuildV2Condition(cell),
                    ["lifetime"] = BuildV2Lifetime(cell),
                });
            }

            if (cell.KeptBehaviorName == "itm-special-outranks-legacy-special")
            {
                ((JArray)wheelScreen["rules"]!).Clear();
                ((JArray)wheelScreen["rules"]!).Add(new JObject
                {
                    ["id"] = "ws-itm",
                    ["command"] = "raceFlag",
                    ["condition"] = new JObject
                    {
                        ["source"] = new JObject
                        {
                            ["kind"] = "builtIn",
                            ["name"] = "IsInPitLane",
                        },
                        ["operator"] = "isTrue",
                    },
                    ["lifetime"] = new JObject { ["kind"] = "whileTrue" },
                });
            }

            string mode = cell.Device == ReplayDevice.SegmentOnly ? "legacyOnly" : "on";
            var settings = new JObject { ["mode"] = mode };
            if (cell.Press == ReplayPress.RejectOnRevert
                || (cell.KeptBehaviorName != null
                    && cell.KeptBehaviorName.StartsWith("reject-uncommanded", StringComparison.Ordinal)))
            {
                settings["rejectUncommanded"] = true;
            }

            var root = new JObject
            {
                ["schemaVersion"] = 2,
                ["pages"] = pages,
                ["cycles"] = cycles,
                ["priority"] = priority,
                ["pageOrder"] = new JArray
                {
                    new JObject { ["kind"] = "hostedPage", ["id"] = "spd" },
                    new JObject { ["kind"] = "hostedPage", ["id"] = "pit" },
                    new JObject { ["kind"] = "hostedPage", ["id"] = "gear" },
                },
                ["fields"] = new JObject(),
                ["wheelScreen"] = wheelScreen,
                ["settings"] = settings,
            };

            // Suffix-blink named new-behavior: field override with blink effect.
            if (cell.KeptBehaviorName == "suffix-blink-v2-only")
            {
                root["fields"] = new JObject
                {
                    ["1"] = new JObject
                    {
                        ["base"] = new JObject
                        {
                            ["source"] = new JObject
                            {
                                ["kind"] = "builtIn",
                                ["name"] = "Fuel",
                            },
                        },
                        ["overrides"] = new JArray
                        {
                            new JObject
                            {
                                ["id"] = "ov-blink",
                                ["condition"] = new JObject
                                {
                                    ["source"] = new JObject
                                    {
                                        ["kind"] = "builtIn",
                                        ["name"] = "IsInPitLane",
                                    },
                                    ["operator"] = "isTrue",
                                },
                                ["lifetime"] = new JObject { ["kind"] = "whileTrue" },
                                ["suffix"] = new JObject
                                {
                                    ["text"] = "L",
                                    ["effect"] = "blink",
                                },
                            },
                        },
                    },
                };
            }

            if (cell.KeptBehaviorName == "param-budget-at-16"
                || cell.KeptBehaviorName == "param-budget-at-17")
            {
                int n = cell.KeptBehaviorName.EndsWith("17", StringComparison.Ordinal) ? 17 : 16;
                var fields = new JObject();
                for (int i = 1; i <= n; i++)
                {
                    fields[i.ToString(CultureInfo.InvariantCulture)] = new JObject
                    {
                        ["base"] = new JObject
                        {
                            ["source"] = new JObject
                            {
                                ["kind"] = "builtIn",
                                ["name"] = "Fuel",
                            },
                        },
                    };
                }
                root["fields"] = fields;
            }

            return root.ToString(Formatting.Indented);
        }

        private static JObject BuildV2Seat(ReplayCell cell)
        {
            JObject target;
            switch (cell.Target)
            {
                case ReplayTarget.SegmentScreen:
                    target = new JObject { ["kind"] = "hostedPage", ["id"] = "pit" };
                    break;
                case ReplayTarget.Cycle:
                    target = new JObject { ["kind"] = "cycle", ["id"] = "c1" };
                    break;
                case ReplayTarget.Special:
                    // Special is on wheelScreen; seat points at rest ITM page for parity skeleton.
                    target = new JObject { ["kind"] = "itmPage", ["catalogPageId"] = "fuelErsDrs" };
                    break;
                default:
                    target = new JObject { ["kind"] = "itmPage", ["catalogPageId"] = "fuelErsDrs" };
                    break;
            }

            if (cell.Device == ReplayDevice.SegmentOnly
                && (cell.Target == ReplayTarget.Page || cell.Target == ReplayTarget.Cycle))
            {
                target = new JObject { ["kind"] = "hostedPage", ["id"] = "pit" };
            }

            var summons = new JArray
            {
                new JObject
                {
                    ["id"] = "s1",
                    ["condition"] = BuildV2Condition(cell),
                    ["lifetime"] = BuildV2Lifetime(cell),
                },
            };

            if (cell.KeptBehaviorName == "supersede-retired-untilDismissed-resumes")
            {
                summons = new JArray
                {
                    new JObject
                    {
                        ["id"] = "s-low",
                        ["condition"] = BuildV2Condition(cell),
                        ["lifetime"] = new JObject { ["kind"] = "untilDismissed" },
                    },
                };
                // Higher seat row for pit limiter.
                return new JObject
                {
                    ["kind"] = "seat",
                    ["id"] = "r-low",
                    ["target"] = target,
                    ["summons"] = summons,
                };
            }

            return new JObject
            {
                ["kind"] = "seat",
                ["id"] = "r1",
                ["target"] = target,
                ["summons"] = summons,
            };
        }

        private static JObject BuildV2Condition(ReplayCell cell)
        {
            // Edge conditions: no operator; direction lives on lifetime onChange.
            if (ReplayMatrix.IsEdge(cell.Condition))
            {
                return new JObject
                {
                    ["source"] = new JObject
                    {
                        ["kind"] = "builtIn",
                        ["name"] = "Gear",
                    },
                };
            }

            string op = EnumText.Write(ReplayMatrix.ToV1Condition(cell.Condition));
            // Map v1 ConditionKind spelling to ConditionOperator spelling (same camelCase).
            string srcName = ReplayMatrix.IsBoolLevel(cell.Condition) ? "IsInPitLane" : "Fuel";

            var cond = new JObject
            {
                ["source"] = new JObject
                {
                    ["kind"] = "builtIn",
                    ["name"] = srcName,
                },
                ["operator"] = op,
            };

            if (!ReplayMatrix.IsBoolLevel(cell.Condition))
                cond["value"] = 10;

            if (cell.Hysteresis.HasValue)
                cond["hysteresis"] = cell.Hysteresis.Value;

            return cond;
        }

        private static JObject BuildV2Lifetime(ReplayCell cell)
        {
            if (ReplayMatrix.IsEdge(cell.Condition))
            {
                string direction = cell.Condition switch
                {
                    ReplayCondition.Increases => "up",
                    ReplayCondition.Decreases => "down",
                    _ => "any",
                };
                var life = new JObject
                {
                    ["kind"] = "onChange",
                    ["direction"] = direction,
                    ["durationMs"] = 5000,
                };
                if (cell.Hold == ReplayHold.UntilDismissed)
                {
                    life["then"] = "untilDismissed";
                    life.Remove("durationMs");
                }
                return life;
            }

            string kind = cell.Hold switch
            {
                ReplayHold.WhileActive => "whileTrue",
                ReplayHold.ForDuration => "forDuration",
                ReplayHold.UntilDismissed => "untilDismissed",
                _ => "forDuration",
            };

            var lifetime = new JObject { ["kind"] = kind };
            if (cell.Hold == ReplayHold.ForDuration)
                lifetime["durationMs"] = 5000;
            return lifetime;
        }
    }
}
