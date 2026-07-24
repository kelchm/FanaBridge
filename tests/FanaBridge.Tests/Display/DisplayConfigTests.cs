using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Exercises the display customization config model: JSON round-trip of every enum
    /// kind, the lenient-load contract (unknown enum strings and fields degrade per rule
    /// with a warning, never a throw), legacy screen charset validation, hold coercion
    /// for edge/event conditions, and the normalizer's engine-facing invariants.
    /// </summary>
    public class DisplayConfigTests
    {
        // ── Helpers ──────────────────────────────────────────────────────

        private static DisplayCustomizationConfig Load(string? json, out List<string> warnings)
        {
            var w = new List<string>();
            var config = DisplayConfigSerializer.Load(json, w.Add);
            warnings = w;
            return config;
        }

        private static DisplayRule Rule(string id, RuleCondition when, RuleTarget show,
            HoldSpec? hold = null, RuleEligibility eligible = RuleEligibility.InGame)
            => new DisplayRule { Id = id, When = when, Show = show, Hold = hold, Eligible = eligible };

        private static RuleCondition Level(ConditionKind kind, string builtIn, double? value = null,
            double? hysteresis = null)
            => new RuleCondition
            {
                Kind = kind,
                Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = builtIn },
                Value = value,
                Hysteresis = hysteresis,
            };

        private static RuleTarget Page(ItmPage page)
            => new RuleTarget { Kind = TargetKind.Page, Page = page };

        // A minimal valid ITM rule wrapped in a full document, for JSON-literal tests.
        private static string DocWithItmRule(string ruleJson)
            => "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ " + ruleJson + " ] } }";

        private const string ValidWhen =
            "\"when\": { \"kind\": \"lessThan\", \"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }";
        private const string ValidShow = "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" }";

        // ── Round-trip ───────────────────────────────────────────────────

        [Fact]
        public void RoundTrip_PreservesEveryConditionKind()
        {
            var kinds = Enum.GetValues(typeof(ConditionKind)).Cast<ConditionKind>()
                .Where(k => k != ConditionKind.Unknown).ToList();

            var config = new DisplayCustomizationConfig();
            foreach (var kind in kinds)
            {
                var source = kind.IsEvent()
                    ? new PropertySpec { Kind = PropertyKind.FanaBridgeAction, Name = "NextPage" }
                    : new PropertySpec { Kind = PropertyKind.BuiltIn, Name = BuiltInProperties.Fuel };
                config.Itm.Rules.Add(Rule(
                    "r-" + kind,
                    new RuleCondition
                    {
                        Kind = kind,
                        Source = source,
                        Value = kind.RequiresValue() ? 10.0 : (double?)null,
                    },
                    Page(ItmPage.FuelErsDrs),
                    new HoldSpec
                    {
                        Kind = kind.IsLevel() ? HoldKind.WhileActive : HoldKind.ForDuration,
                        DurationMs = 2000,
                    }));
            }

            var loaded = Load(DisplayConfigSerializer.Save(config), out var warnings);

            Assert.Empty(warnings);
            Assert.Equal(kinds, loaded.Itm.Rules.Select(r => r.When.Kind).ToList());
            Assert.All(loaded.Itm.Rules, r => Assert.True(r.Enabled));
        }

        [Fact]
        public void RoundTrip_PreservesEveryTargetHoldAndEligibility()
        {
            var config = new DisplayCustomizationConfig();
            config.Legacy.Screens.Add(new LegacyScreen { Id = "fn1", Name = "FN1", Text = "FN1" });
            config.Itm.BasePage = ItmPage.LapTimes;

            config.Itm.Rules.Add(Rule("page",
                Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Page(ItmPage.TyreTemps),
                new HoldSpec { Kind = HoldKind.UntilDismissed },
                RuleEligibility.Always));
            config.Itm.Rules.Add(Rule("screen",
                Level(ConditionKind.IsTrue, BuiltInProperties.DrsEnabled),
                new RuleTarget { Kind = TargetKind.Screen, ScreenId = "fn1" },
                new HoldSpec { Kind = HoldKind.WhileActive },
                RuleEligibility.Idle));
            config.Itm.Rules.Add(Rule("cycle",
                Level(ConditionKind.GreaterOrEqual, BuiltInProperties.Position, 3),
                new RuleTarget
                {
                    Kind = TargetKind.Cycle,
                    PagesRaw = new System.Collections.Generic.List<string>
                    {
                        "fuelErsDrs", "tyreTemps",
                    },
                    PeriodMs = 4000,
                },
                new HoldSpec { Kind = HoldKind.ForDuration, DurationMs = 8000 }));

            var loaded = Load(DisplayConfigSerializer.Save(config), out var warnings);

            Assert.Empty(warnings);
            Assert.Equal(ItmPage.LapTimes, loaded.Itm.BasePage);

            var page = loaded.Itm.Rules[0];
            Assert.Equal(TargetKind.Page, page.Show.Kind);
            Assert.Equal(ItmPage.TyreTemps, page.Show.Page);
            Assert.Equal(HoldKind.UntilDismissed, page.Hold.Kind);
            Assert.Equal(RuleEligibility.Always, page.Eligible);

            var screen = loaded.Itm.Rules[1];
            Assert.Equal(TargetKind.Screen, screen.Show.Kind);
            Assert.Equal("fn1", screen.Show.ScreenId);
            Assert.Equal(HoldKind.WhileActive, screen.Hold.Kind);
            Assert.Equal(RuleEligibility.Idle, screen.Eligible);

            var cycle = loaded.Itm.Rules[2];
            Assert.Equal(TargetKind.Cycle, cycle.Show.Kind);
            Assert.Equal(new ItmPage?[] { ItmPage.FuelErsDrs, ItmPage.TyreTemps },
                cycle.Show.CyclePages);
            Assert.Equal(4000, cycle.Show.PeriodMs);
            Assert.Equal(HoldKind.ForDuration, cycle.Hold.Kind);
            Assert.Equal(8000, cycle.Hold.DurationMs);
        }

        [Fact]
        public void RoundTrip_LegacySetAndFieldMappings()
        {
            var config = new DisplayCustomizationConfig { ProfileId = "iracing-gt3" };
            config.Legacy.Screens.Add(new LegacyScreen { Id = "pit", Name = "Pit", Text = "PIT" });
            config.Legacy.BaseScreenId = "pit";
            config.Legacy.Rules.Add(Rule("l1",
                Level(ConditionKind.IsTrue, BuiltInProperties.DrsAvailable),
                new RuleTarget { Kind = TargetKind.Screen, ScreenId = "pit" },
                new HoldSpec { Kind = HoldKind.WhileActive }));
            config.FieldMappings[ItmParam.Fuel] = new FieldMapping
            {
                Source = new PropertySpec { Kind = PropertyKind.SimHubProperty, Name = "DataCorePlugin.Computed.Fuel_RemainingLaps" },
                Format = FieldFormats.Bare,
            };

            var loaded = Load(DisplayConfigSerializer.Save(config), out var warnings);

            Assert.Empty(warnings);
            Assert.Equal("iracing-gt3", loaded.ProfileId);
            Assert.Equal("pit", loaded.Legacy.BaseScreenId);
            Assert.Single(loaded.Legacy.Rules);
            Assert.True(loaded.Legacy.Rules[0].Enabled);
            var mapping = loaded.FieldMappings[ItmParam.Fuel];
            Assert.Equal(PropertyKind.SimHubProperty, mapping.Source.Kind);
            Assert.Equal("DataCorePlugin.Computed.Fuel_RemainingLaps", mapping.Source.Name);
            Assert.Equal(FieldFormats.Bare, mapping.Format);
        }

        [Fact]
        public void RoundTrip_DisabledRuleStaysDisabled()
        {
            var config = new DisplayCustomizationConfig();
            var rule = Rule("r1", Level(ConditionKind.LessThan, BuiltInProperties.Fuel, 10),
                Page(ItmPage.FuelErsDrs), new HoldSpec { Kind = HoldKind.WhileActive });
            rule.Enabled = false;
            config.Itm.Rules.Add(rule);

            string json = DisplayConfigSerializer.Save(config);
            Assert.Contains("\"enabled\": false", json);

            var loaded = Load(json, out var warnings);
            Assert.Empty(warnings);
            Assert.False(loaded.Itm.Rules[0].Enabled);
        }

        [Fact]
        public void Save_WritesSchemaVersionAndCamelCaseEnumStrings()
        {
            var config = new DisplayCustomizationConfig();
            config.Itm.BasePage = ItmPage.LapInfo;
            config.Itm.Rules.Add(Rule("r1",
                Level(ConditionKind.LessOrEqual, BuiltInProperties.GapAhead, 0.5),
                Page(ItmPage.LapTimes),
                new HoldSpec { Kind = HoldKind.ForDuration, DurationMs = 2000 },
                RuleEligibility.Always));

            string json = DisplayConfigSerializer.Save(config);

            Assert.Contains("\"schemaVersion\": 1", json);
            Assert.Contains("\"lessOrEqual\"", json);
            Assert.Contains("\"builtIn\"", json);
            Assert.Contains("\"page\"", json);
            Assert.Contains("\"lapTimes\"", json);
            Assert.Contains("\"forDuration\"", json);
            Assert.Contains("\"always\"", json);
            Assert.Contains("\"runs\"", json);
            Assert.Contains("\"lapInfo\"", json);
        }

        // ── Lenient loading ──────────────────────────────────────────────

        [Fact]
        public void Load_UnknownConditionKind_DisablesRuleAndWarns()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", \"when\": { \"kind\": \"sparkles\", \"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 1 }, "
                + ValidShow + " }"), out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.True(rule.DegradedAtLoad);
            Assert.False(rule.EffectivelyEnabled);
            Assert.True(rule.Enabled);   // the user's own switch is untouched
            Assert.Equal(ConditionKind.Unknown, rule.When.Kind);
            Assert.Contains(warnings, w => w.Contains("sparkles"));
            Assert.Contains(warnings, w => w.Contains("disabled"));
        }

        [Fact]
        public void Load_UnknownTargetKind_DisablesRuleAndWarns()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen + ", \"show\": { \"kind\": \"hologram\" } }"),
                out var warnings);

            Assert.True(config.Itm.Rules[0].DegradedAtLoad);
            Assert.Contains(warnings, w => w.Contains("hologram"));
            Assert.Contains(warnings, w => w.Contains("disabled"));
        }

        // ── Special commands ─────────────────────────────────────────────

        [Fact]
        public void RoundTrip_SpecialCommand_KnownAndUnknownSurvival()
        {
            var config = new DisplayCustomizationConfig();
            config.Itm.Rules.Add(Rule("logo",
                Level(ConditionKind.IsTrue, BuiltInProperties.DrsEnabled),
                new RuleTarget { Kind = TargetKind.Special, Command = SpecialCommand.LogoScreen },
                new HoldSpec { Kind = HoldKind.WhileActive }));
            config.Itm.Rules.Add(Rule("inv",
                Level(ConditionKind.IsTrue, BuiltInProperties.DrsAvailable),
                new RuleTarget { Kind = TargetKind.Special, Command = SpecialCommand.LogoInvertedScreen },
                new HoldSpec { Kind = HoldKind.WhileActive }));
            config.Itm.Rules.Add(Rule("white",
                Level(ConditionKind.IsTrue, BuiltInProperties.DrsEnabled),
                new RuleTarget { Kind = TargetKind.Special, Command = SpecialCommand.WhiteScreen },
                new HoldSpec { Kind = HoldKind.ForDuration, DurationMs = 3000 }));
            config.Itm.Rules.Add(Rule("blank",
                Level(ConditionKind.IsTrue, BuiltInProperties.DrsAvailable),
                new RuleTarget { Kind = TargetKind.Special, Command = SpecialCommand.BlankScreen },
                new HoldSpec { Kind = HoldKind.WhileActive }));

            var loaded = Load(DisplayConfigSerializer.Save(config), out var warnings);
            Assert.Empty(warnings);
            Assert.Equal(SpecialCommand.LogoScreen, loaded.Itm.Rules[0].Show.Command);
            Assert.Equal("logo", loaded.Itm.Rules[0].Show.CommandRaw);
            Assert.Equal(SpecialCommand.LogoInvertedScreen, loaded.Itm.Rules[1].Show.Command);
            Assert.Equal("logoInverted", loaded.Itm.Rules[1].Show.CommandRaw);
            Assert.Equal(SpecialCommand.WhiteScreen, loaded.Itm.Rules[2].Show.Command);
            Assert.Equal("white", loaded.Itm.Rules[2].Show.CommandRaw);
            Assert.Equal(SpecialCommand.BlankScreen, loaded.Itm.Rules[3].Show.Command);
            Assert.Equal("blankScreen", loaded.Itm.Rules[3].Show.CommandRaw);

            // Unknown command text survives byte-for-byte through degrade + save/load.
            var unknown = Load(DocWithItmRule(
                "{ \"id\": \"u1\", " + ValidWhen
                + ", \"show\": { \"kind\": \"special\", \"command\": \"futureScreen\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } }"), out var uWarn);
            Assert.True(unknown.Itm.Rules[0].DegradedAtLoad);
            Assert.Equal("futureScreen", unknown.Itm.Rules[0].Show.CommandRaw);
            Assert.Equal(SpecialCommand.Unknown, unknown.Itm.Rules[0].Show.Command);
            Assert.Contains(uWarn, w => w.Contains("futureScreen"));

            var resaved = Load(DisplayConfigSerializer.Save(unknown), out _);
            Assert.Equal("futureScreen", resaved.Itm.Rules[0].Show.CommandRaw);
            Assert.True(resaved.Itm.Rules[0].DegradedAtLoad);
        }

        [Fact]
        public void Load_Special_UnknownOrAbsentCommand_Degrades()
        {
            var absent = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen
                + ", \"show\": { \"kind\": \"special\" }, \"hold\": { \"kind\": \"whileActive\" } }"),
                out var w1);
            Assert.True(absent.Itm.Rules[0].DegradedAtLoad);
            Assert.Contains(w1, w => w.Contains("disabled"));

            var bad = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen
                + ", \"show\": { \"kind\": \"special\", \"command\": \"nope\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } }"), out var w2);
            Assert.True(bad.Itm.Rules[0].DegradedAtLoad);
            Assert.Equal("nope", bad.Itm.Rules[0].Show.CommandRaw);
            Assert.Contains(w2, w => w.Contains("nope"));
        }

        [Fact]
        public void Load_Special_AllowedInBothRuleSets()
        {
            var config = Load(
                "{ \"schemaVersion\": 1, "
                + "\"itm\": { \"rules\": [ { \"id\": \"i1\", " + ValidWhen
                + ", \"show\": { \"kind\": \"special\", \"command\": \"logo\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } } ] }, "
                + "\"segmentDisplay\": { \"rules\": [ { \"id\": \"l1\", "
                + "\"when\": { \"kind\": \"isTrue\", \"source\": { \"kind\": \"builtIn\", \"name\": \"DrsEnabled\" } }, "
                + "\"show\": { \"kind\": \"special\", \"command\": \"white\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } } ] } }",
                out var warnings);
            Assert.Empty(warnings);
            Assert.False(config.Itm.Rules[0].DegradedAtLoad);
            Assert.False(config.Legacy.Rules[0].DegradedAtLoad);
            Assert.Equal(SpecialCommand.LogoScreen, config.Itm.Rules[0].Show.Command);
            Assert.Equal(SpecialCommand.WhiteScreen, config.Legacy.Rules[0].Show.Command);
        }

        [Fact]
        public void Load_Cycle_DropsSpecialCommandPages_WithWarn()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen
                + ", \"show\": { \"kind\": \"cycle\", \"pages\": [ \"fuelErsDrs\", \"logo\", \"tyreTemps\" ], "
                + "\"periodMs\": 3000 }, \"hold\": { \"kind\": \"whileActive\" } }"),
                out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.False(rule.DegradedAtLoad);
            Assert.Equal(new[] { "fuelErsDrs", "tyreTemps" }, rule.Show.PagesRaw);
            Assert.Contains(warnings, w => w.Contains("logo") && w.Contains("dropped"));
        }

        [Fact]
        public void Load_UnknownPageName_DisablesRuleAndWarns()
        {
            // A page this build doesn't know must not silently retarget to LapInfo.
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen + ", \"show\": { \"kind\": \"page\", \"page\": \"ersDetail\" } }"),
                out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.True(rule.DegradedAtLoad);
            Assert.Null(rule.Show.Page);
            Assert.Contains(warnings, w => w.Contains("ersDetail"));
        }

        [Fact]
        public void Load_UnknownEligibility_CoercesToInGameAndWarns()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen + ", " + ValidShow + ", \"runs\": \"weekends\" }"),
                out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.True(rule.Enabled);   // eligibility degrades, the rule survives
            Assert.Equal(RuleEligibility.InGame, rule.Eligible);
            Assert.Contains(warnings, w => w.Contains("weekends"));
        }

        [Fact]
        public void Load_UnknownHoldKind_CoercedToFamilyDefaultAndWarns()
        {
            // Coerced at runtime to the condition family's default; the rule survives.
            var config = Load(
                "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", " + ValidWhen + ", " + ValidShow
                + ", \"hold\": { \"kind\": \"shimmer\" } }, "
                + "{ \"id\": \"r2\", \"when\": { \"kind\": \"changes\", \"source\": { \"kind\": \"builtIn\", \"name\": \"BrakeBias\" } }, "
                + ValidShow + ", \"hold\": { \"kind\": \"shimmer\" } } "
                + "] } }", out var warnings);

            Assert.Equal(HoldKind.WhileActive, config.Itm.Rules[0].Hold.Kind);   // level
            Assert.Equal(HoldKind.ForDuration, config.Itm.Rules[1].Hold.Kind);   // edge
            Assert.All(config.Itm.Rules, r => Assert.False(r.DegradedAtLoad));
            Assert.Equal(2, warnings.Count(w => w.Contains("shimmer")));
        }

        [Fact]
        public void Load_UnknownSourceKind_DisablesRuleAndWarns()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", \"when\": { \"kind\": \"lessThan\", \"source\": { \"kind\": \"telepathy\", \"name\": \"Fuel\" }, \"value\": 1 }, "
                + ValidShow + " }"), out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.True(rule.DegradedAtLoad);
            Assert.Equal(PropertyKind.Unknown, rule.When.Source.Kind);
            Assert.Contains(warnings, w => w.Contains("telepathy"));
            Assert.Contains(warnings, w => w.Contains("disabled"));
        }

        [Fact]
        public void SaveAfterLoad_UnknownEnumValues_SurviveVerbatim()
        {
            // A future version's document passes through this build (load, then the
            // routine settings save) without losing what this build does not understand
            // — and without baking the load-time degradation into the document.
            string original =
                "{ \"schemaVersion\": 2, \"itm\": { \"basePage\": \"pitDetail\", \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"sparkles\", \"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 1 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"ersDetail\" }, "
                + "\"hold\": { \"kind\": \"shimmer\" }, \"runs\": \"weekends\" } "
                + "] } }";

            var config = Load(original, out _);
            var rule = Assert.Single(config.Itm.Rules);
            Assert.True(rule.DegradedAtLoad);
            Assert.True(rule.Enabled);   // the user's own switch is untouched

            string saved = DisplayConfigSerializer.Save(config);
            Assert.Contains("\"sparkles\"", saved);
            Assert.Contains("\"ersDetail\"", saved);
            Assert.Contains("\"shimmer\"", saved);
            Assert.Contains("\"weekends\"", saved);
            Assert.Contains("\"pitDetail\"", saved);
            Assert.DoesNotContain("\"enabled\": false", saved);

            // A second pass degrades identically — nothing was lost the first time.
            var reloaded = Load(saved, out _);
            Assert.True(reloaded.Itm.Rules[0].DegradedAtLoad);
            Assert.Equal(saved, DisplayConfigSerializer.Save(reloaded));
        }

        [Fact]
        public void Load_UnknownBasePage_FallsBackToLapInfoAndWarns()
        {
            var config = Load("{ \"schemaVersion\": 1, \"itm\": { \"basePage\": \"pitDetail\" } }",
                out var warnings);

            Assert.Equal(ItmPage.LapInfo, config.Itm.BasePage);
            Assert.Equal("pitDetail", config.Itm.BasePageRaw);   // preserved for the round-trip
            Assert.Contains(warnings, w => w.Contains("pitDetail"));
        }

        [Fact]
        public void Load_UnknownJsonFields_AreIgnored()
        {
            var config = Load(
                "{ \"schemaVersion\": 1, \"futureTopLevel\": { \"x\": 1 }, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"futureRuleField\": true, " + ValidWhen + ", " + ValidShow + " } "
                + "] } }", out var warnings);

            Assert.Empty(warnings);
            var rule = Assert.Single(config.Itm.Rules);
            Assert.True(rule.Enabled);
            Assert.Equal(ConditionKind.LessThan, rule.When.Kind);
        }

        [Fact]
        public void Load_NewerSchemaVersion_WarnsButLoads()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen + ", " + ValidShow + " }")
                .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99"), out var warnings);

            Assert.Equal(99, config.SchemaVersion);
            Assert.True(config.Itm.Rules[0].Enabled);
            Assert.Contains(warnings, w => w.Contains("newer schema version"));
        }

        [Fact]
        public void Load_MalformedJson_ReturnsDefaultsAndWarns()
        {
            var config = Load("{ this is not json", out var warnings);

            Assert.NotNull(config);
            Assert.Equal(DisplayCustomizationConfig.CurrentSchemaVersion, config.SchemaVersion);
            Assert.Empty(config.Itm.Rules);
            Assert.Contains(warnings, w => w.Contains("could not parse"));
        }

        [Fact]
        public void Load_NullOrBlank_ReturnsDefaultsSilently()
        {
            foreach (var json in new[] { null, "", "   " })
            {
                var config = Load(json, out var warnings);
                Assert.Empty(warnings);
                Assert.Equal(ItmPage.LapInfo, config.Itm.BasePage);
                Assert.Empty(config.Itm.Rules);
                Assert.Empty(config.Legacy.Rules);
                Assert.Null(config.Legacy.BaseScreenId);
                Assert.Empty(config.FieldMappings);
            }
        }

        [Fact]
        public void Load_OmittedRuleFields_GetDefaults()
        {
            // No enabled/eligible/hold: enabled true, InGame, and the condition family's
            // natural hold (WhileActive for a level condition).
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen + ", " + ValidShow + " }"), out var warnings);

            Assert.Empty(warnings);
            var rule = Assert.Single(config.Itm.Rules);
            Assert.True(rule.Enabled);
            Assert.Equal(RuleEligibility.InGame, rule.Eligible);
            Assert.Equal(HoldKind.WhileActive, rule.Hold.Kind);
        }

        // ── Normalization: conditions and holds ──────────────────────────

        [Fact]
        public void EdgeCondition_WhileActiveHold_CoercedToForDuration()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", \"when\": { \"kind\": \"changes\", \"source\": { \"kind\": \"builtIn\", \"name\": \"BrakeBias\" } }, "
                + ValidShow + ", \"hold\": { \"kind\": \"whileActive\" } }"), out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.True(rule.Enabled);   // coerced, not disabled
            Assert.Equal(HoldKind.ForDuration, rule.Hold.Kind);
            Assert.Equal(HoldSpec.DefaultDurationMs, rule.Hold.DurationMs);
            Assert.Contains(warnings, w => w.Contains("WhileActive"));
        }

        [Fact]
        public void EventCondition_WhileActiveHold_CoercedToForDuration()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", \"when\": { \"kind\": \"actionTriggered\", \"source\": { \"kind\": \"fanaBridgeAction\", \"name\": \"ShowTyres\" } }, "
                + ValidShow + ", \"hold\": { \"kind\": \"whileActive\" } }"), out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.True(rule.Enabled);
            Assert.Equal(HoldKind.ForDuration, rule.Hold.Kind);
            Assert.Equal(HoldSpec.DefaultDurationMs, rule.Hold.DurationMs);
        }

        [Fact]
        public void EdgeCondition_OmittedHold_DefaultsToForDurationSilently()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", \"when\": { \"kind\": \"increases\", \"source\": { \"kind\": \"builtIn\", \"name\": \"CurrentLap\" } }, "
                + ValidShow + " }"), out var warnings);

            Assert.Empty(warnings);
            var rule = Assert.Single(config.Itm.Rules);
            Assert.Equal(HoldKind.ForDuration, rule.Hold.Kind);
            Assert.Equal(HoldSpec.DefaultDurationMs, rule.Hold.DurationMs);
        }

        [Fact]
        public void ThresholdCondition_MissingValue_DisablesRule()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", \"when\": { \"kind\": \"lessThan\", \"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" } }, "
                + ValidShow + " }"), out var warnings);

            Assert.True(config.Itm.Rules[0].DegradedAtLoad);
            Assert.Contains(warnings, w => w.Contains("no comparison value"));
        }

        [Fact]
        public void UnknownBuiltInName_DisablesRule()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", \"when\": { \"kind\": \"lessThan\", \"source\": { \"kind\": \"builtIn\", \"name\": \"FluxCapacitor\" }, \"value\": 1 }, "
                + ValidShow + " }"), out var warnings);

            Assert.True(config.Itm.Rules[0].DegradedAtLoad);
            Assert.Contains(warnings, w => w.Contains("FluxCapacitor"));
        }

        [Fact]
        public void ThresholdCondition_NonFiniteValue_DisablesRule()
        {
            // NaN never satisfies a comparison — the rule would be silently inert.
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", \"when\": { \"kind\": \"lessThan\", \"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": NaN }, "
                + ValidShow + " }"), out var warnings);

            Assert.True(config.Itm.Rules[0].DegradedAtLoad);
            Assert.Contains(warnings, w => w.Contains("finite"));
        }

        [Fact]
        public void Hysteresis_NonFinite_ClampedToZero()
        {
            // NaN hysteresis makes every release comparison false: an active level rule
            // would flap (and re-fire) on alternating ticks.
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", \"when\": { \"kind\": \"lessThan\", \"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10, \"hysteresis\": NaN }, "
                + ValidShow + " }"), out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.False(rule.DegradedAtLoad);
            Assert.Equal(0.0, rule.When.Hysteresis);
            Assert.Contains(warnings, w => w.Contains("hysteresis"));
        }

        [Fact]
        public void Hysteresis_NegativeClampedToZero_AndRemovedFromEdgeKinds()
        {
            var config = Load(
                "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"lessThan\", \"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10, \"hysteresis\": -2 }, " + ValidShow + " }, "
                + "{ \"id\": \"r2\", \"when\": { \"kind\": \"changes\", \"source\": { \"kind\": \"builtIn\", \"name\": \"BrakeBias\" }, \"hysteresis\": 1 }, " + ValidShow + " } "
                + "] } }", out var warnings);

            Assert.Equal(0.0, config.Itm.Rules[0].When.Hysteresis);
            Assert.Contains(warnings, w => w.Contains("hysteresis"));
            Assert.Null(config.Itm.Rules[1].When.Hysteresis);   // level-only concept
        }

        [Fact]
        public void Alternate_PeriodBelowFloor_DegradesUnknown_NoClamp()
        {
            // S4 (spec-v9-s4-rename-freeze): Alternate purged — old spelling degrades as
            // Unknown (rule kept, disabled). Period is not clamped (not a Cycle branch).
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen + ", "
                + "\"show\": { \"kind\": \"alternate\", \"pageA\": \"fuelErsDrs\", \"pageB\": \"tyreTemps\", \"periodMs\": 200 } }"),
                out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.True(rule.DegradedAtLoad);
            Assert.Equal(TargetKind.Unknown, rule.Show.Kind);
            Assert.Equal("alternate", rule.Show.KindRaw);
            Assert.Equal(200, rule.Show.PeriodMs);
            Assert.Contains(warnings, w => w.Contains("alternate"));
        }

        [Fact]
        public void Alternate_OmittedPeriod_DegradesUnknown_DefaultPeriodStillApplies()
        {
            // S4: alternate is Unknown; PeriodMs still defaults on the property itself.
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen + ", "
                + "\"show\": { \"kind\": \"alternate\", \"pageA\": \"fuelErsDrs\", \"pageB\": \"tyreTemps\" } }"),
                out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.True(rule.DegradedAtLoad);
            Assert.Equal(TargetKind.Unknown, rule.Show.Kind);
            Assert.Equal(RuleTarget.DefaultCyclePeriodMs, rule.Show.PeriodMs);
            Assert.Contains(warnings, w => w.Contains("alternate"));
        }

        [Fact]
        public void RoundTrip_CycleRule_PreservesPagesIncludingUnknown()
        {
            // A cycle with three pages (one unknown to this build) serializes camelCase
            // "pages", degrades at load, and survives a save/load round-trip verbatim —
            // the raw page name is what a future version needs.
            string original = DocWithItmRule(
                "{ \"id\": \"c1\", " + ValidWhen + ", "
                + "\"show\": { \"kind\": \"cycle\", \"pages\": [ \"fuelErsDrs\", \"tyreTemps\", \"ersDetail\" ], "
                + "\"periodMs\": 2500 }, \"hold\": { \"kind\": \"whileActive\" } }");

            var config = Load(original, out var warnings);
            var rule = Assert.Single(config.Itm.Rules);
            Assert.Equal(TargetKind.Cycle, rule.Show.Kind);
            Assert.Equal(new[] { "fuelErsDrs", "tyreTemps", "ersDetail" }, rule.Show.PagesRaw);
            Assert.Equal(2500, rule.Show.PeriodMs);
            Assert.True(rule.DegradedAtLoad);
            Assert.Contains(warnings, w => w.Contains("ersDetail"));

            string saved = DisplayConfigSerializer.Save(config);
            Assert.Contains("\"cycle\"", saved);
            Assert.Contains("\"pages\"", saved);
            Assert.Contains("\"ersDetail\"", saved);
            Assert.Contains("\"fuelErsDrs\"", saved);
            Assert.Contains("\"tyreTemps\"", saved);
            Assert.Contains("\"periodMs\": 2500", saved);

            var reloaded = Load(saved, out _);
            Assert.Equal(rule.Show.PagesRaw, reloaded.Itm.Rules[0].Show.PagesRaw);
            Assert.Equal(saved, DisplayConfigSerializer.Save(reloaded));
        }

        [Fact]
        public void RoundTrip_UntouchedAlternate_DegradesPreserved()
        {
            // S4 (spec-v9-s4-rename-freeze): P4 Alternate-alias pin re-anchored — alternate
            // is no longer a known kind. Rule degrades (kept, disabled); kind + pageA/pageB
            // round-trip via KindRaw / ExtensionData. Never crash, never rewrite.
            string original = DocWithItmRule(
                "{ \"id\": \"a1\", " + ValidWhen + ", "
                + "\"show\": { \"kind\": \"alternate\", \"pageA\": \"fuelErsDrs\", \"pageB\": \"tyreTemps\", "
                + "\"periodMs\": 4000 }, \"hold\": { \"kind\": \"whileActive\" } }");

            var config = Load(original, out var warnings);
            var rule = Assert.Single(config.Itm.Rules);
            Assert.True(rule.DegradedAtLoad);
            Assert.Equal(TargetKind.Unknown, rule.Show.Kind);
            Assert.Equal("alternate", rule.Show.KindRaw);
            Assert.Null(rule.Show.PagesRaw);
            Assert.Null(rule.Show.CyclePages);
            Assert.Contains(warnings, w => w.Contains("alternate"));

            string saved = DisplayConfigSerializer.Save(config);
            Assert.Contains("\"alternate\"", saved);
            Assert.Contains("\"pageA\"", saved);
            Assert.Contains("\"pageB\"", saved);
            Assert.Contains("\"periodMs\": 4000", saved);

            Assert.Equal(saved, DisplayConfigSerializer.Save(Load(saved, out _)));
        }

        [Fact]
        public void Cycle_ZeroOrOnePage_DisablesRule()
        {
            var empty = Load(DocWithItmRule(
                "{ \"id\": \"c0\", " + ValidWhen + ", "
                + "\"show\": { \"kind\": \"cycle\", \"pages\": [] } }"), out var w0);
            Assert.True(empty.Itm.Rules[0].DegradedAtLoad);
            Assert.Contains(w0, w => w.Contains("at least two pages"));

            var one = Load(DocWithItmRule(
                "{ \"id\": \"c1\", " + ValidWhen + ", "
                + "\"show\": { \"kind\": \"cycle\", \"pages\": [ \"fuelErsDrs\" ] } }"), out var w1);
            Assert.True(one.Itm.Rules[0].DegradedAtLoad);
            Assert.Contains(w1, w => w.Contains("at least two pages"));

            var missing = Load(DocWithItmRule(
                "{ \"id\": \"c2\", " + ValidWhen + ", "
                + "\"show\": { \"kind\": \"cycle\" } }"), out var w2);
            Assert.True(missing.Itm.Rules[0].DegradedAtLoad);
            Assert.Contains(w2, w => w.Contains("at least two pages"));
        }

        [Fact]
        public void Cycle_UnknownPage_DisablesAndPreservesRaw()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"c1\", " + ValidWhen + ", "
                + "\"show\": { \"kind\": \"cycle\", \"pages\": [ \"fuelErsDrs\", \"ersDetail\" ] } }"),
                out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.True(rule.DegradedAtLoad);
            Assert.Equal(new[] { "fuelErsDrs", "ersDetail" }, rule.Show.PagesRaw);
            Assert.Contains(warnings, w => w.Contains("ersDetail"));
            Assert.Contains("\"ersDetail\"", DisplayConfigSerializer.Save(config));
        }

        [Fact]
        public void Cycle_InLegacySet_Disables()
        {
            var config = Load(
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"rules\": [ "
                + "{ \"id\": \"l1\", " + ValidWhen + ", "
                + "\"show\": { \"kind\": \"cycle\", \"pages\": [ \"fuelErsDrs\", \"tyreTemps\" ] } } "
                + "] } }", out var warnings);

            Assert.True(config.Legacy.Rules[0].DegradedAtLoad);
            Assert.Contains(warnings, w => w.Contains("legacy screens"));
        }

        [Fact]
        public void Cycle_PeriodBelowFloor_Clamped()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"c1\", " + ValidWhen + ", "
                + "\"show\": { \"kind\": \"cycle\", \"pages\": [ \"fuelErsDrs\", \"tyreTemps\" ], "
                + "\"periodMs\": 500 } }"), out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.False(rule.DegradedAtLoad);
            Assert.Equal(RuleTarget.MinCyclePeriodMs, rule.Show.PeriodMs);
            Assert.Contains(warnings, w => w.Contains("clamped"));
        }

        [Fact]
        public void Cycle_ValidThreePages_LoadsEnabled()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"c1\", " + ValidWhen + ", "
                + "\"show\": { \"kind\": \"cycle\", \"pages\": [ \"fuelErsDrs\", \"tyreTemps\", \"carSettings\" ], "
                + "\"periodMs\": 2000 } }"), out var warnings);

            Assert.Empty(warnings);
            var rule = Assert.Single(config.Itm.Rules);
            Assert.False(rule.DegradedAtLoad);
            Assert.Equal(TargetKind.Cycle, rule.Show.Kind);
            Assert.Equal(new ItmPage?[] { ItmPage.FuelErsDrs, ItmPage.TyreTemps, ItmPage.CarSettings },
                rule.Show.CyclePages);
            Assert.Equal(2000, rule.Show.PeriodMs);
        }

        // ── Normalization: ids, targets, sets ────────────────────────────

        [Fact]
        public void MissingAndDuplicateRuleIds_AreAssigned()
        {
            var config = Load(
                "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ " + ValidWhen + ", " + ValidShow + " }, "
                + "{ \"id\": \"dup\", " + ValidWhen + ", " + ValidShow + " }, "
                + "{ \"id\": \"dup\", " + ValidWhen + ", " + ValidShow + " } "
                + "] } }", out var warnings);

            var ids = config.Itm.Rules.Select(r => r.Id).ToList();
            Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
            Assert.Equal(3, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Contains(warnings, w => w.Contains("no id"));
            Assert.Contains(warnings, w => w.Contains("duplicates id"));
        }

        [Fact]
        public void LegacyRule_TargetingItmPage_Disabled()
        {
            var config = Load(
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"rules\": [ "
                + "{ \"id\": \"l1\", " + ValidWhen + ", " + ValidShow + " } "
                + "] } }", out var warnings);

            Assert.True(config.Legacy.Rules[0].DegradedAtLoad);
            Assert.Contains(warnings, w => w.Contains("legacy screens"));
        }

        [Fact]
        public void ScreenTarget_UnknownScreenId_DisablesRule()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen + ", \"show\": { \"kind\": \"screen\", \"screenId\": \"ghost\" } }"),
                out var warnings);

            Assert.True(config.Itm.Rules[0].DegradedAtLoad);
            Assert.Contains(warnings, w => w.Contains("ghost"));
        }

        // ── Legacy screens ───────────────────────────────────────────────

        [Fact]
        public void LegacyScreens_UnrenderableOrOversizedText_SkippedWithWarning()
        {
            var config = Load(
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"baseScreenId\": \"logo\", \"screens\": [ "
                + "{ \"id\": \"fn1\", \"text\": \"FN1\" }, "
                + "{ \"id\": \"logo\", \"text\": \"\\u25c6F\\u25c6\" }, "
                + "{ \"id\": \"long\", \"text\": \"WAIT\" }, "
                + "{ \"id\": \"empty\", \"text\": \"\" } "
                + "] } }", out var warnings);

            var kept = Assert.Single(config.Legacy.Screens);
            Assert.Equal("fn1", kept.Id);
            Assert.Contains(warnings, w => w.Contains("'logo'"));
            Assert.Contains(warnings, w => w.Contains("'long'"));
            Assert.Contains(warnings, w => w.Contains("'empty'"));
            // The base screen pointed at the skipped screen — cleared, with a warning.
            Assert.Null(config.Legacy.BaseScreenId);
            Assert.Contains(warnings, w => w.Contains("base screen"));
        }

        [Fact]
        public void LegacyScreens_DuplicateId_KeepsFirst()
        {
            var config = Load(
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"pit\", \"text\": \"PIT\" }, "
                + "{ \"id\": \"pit\", \"text\": \"P2T\" } "
                + "] } }", out var warnings);

            var kept = Assert.Single(config.Legacy.Screens);
            Assert.Equal("PIT", kept.Text);
            Assert.Contains(warnings, w => w.Contains("duplicate"));
        }

        [Theory]
        [InlineData("FN1", true)]
        [InlineData("PIT", true)]
        [InlineData("-1.5", true)]     // dots fold onto the previous character
        [InlineData("P 1", true)]      // space is a deliberate blank
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("WAIT", false)]    // too long
        [InlineData("◆F◆", false)]   // no segment coverage
        public void LegacyScreen_IsRenderableText(string? text, bool expected)
            => Assert.Equal(expected, LegacyScreen.IsRenderableText(text));

        [Theory]
        [InlineData("PIT", true)]
        [InlineData("HELLO", true)]    // Message allows any length ≥ 1
        [InlineData("-1.5", true)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("◆F◆", false)]
        public void LegacyScreen_IsRenderableMessage(string? text, bool expected)
            => Assert.Equal(expected, LegacyScreen.IsRenderableMessage(text));

        // ── Legacy screen contentKind / effect growth (Phase 7a) ─────────

        [Fact]
        public void LegacyScreen_OmittedContentKind_DefaultsToText()
        {
            // Today's static screens omit contentKind — must stay Text with no warn.
            var config = Load(
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"pit\", \"text\": \"PIT\" } ] } }", out var warnings);

            var screen = Assert.Single(config.Legacy.Screens);
            Assert.Equal(LegacyContentKind.Text, screen.ContentKind);
            Assert.Null(screen.ContentKindRaw);
            Assert.Equal(LegacyEffect.None, screen.Effect);
            Assert.Empty(warnings);
        }

        [Fact]
        public void RoundTrip_LegacyScreen_PreservesEveryContentKindAndEffect()
        {
            // Spec P10a: GearAndSpeed removed from the closed set (degrades as Unknown).
            var kinds = new[]
            {
                LegacyContentKind.Text, LegacyContentKind.Speed, LegacyContentKind.Gear,
                LegacyContentKind.GearBrackets,
                LegacyContentKind.Rpm, LegacyContentKind.Position, LegacyContentKind.Fuel,
                LegacyContentKind.Message, LegacyContentKind.Property,
            };
            var effects = new[]
            {
                LegacyEffect.None, LegacyEffect.Scroll, LegacyEffect.Blink,
            };

            var config = new DisplayCustomizationConfig();
            int i = 0;
            foreach (var kind in kinds)
            {
                var screen = new LegacyScreen
                {
                    Id = "s" + i,
                    Name = kind.ToString(),
                    ContentKind = kind,
                    Effect = effects[i % effects.Length],
                };
                if (kind == LegacyContentKind.Text)
                    screen.Text = "T" + (i % 10);
                else if (kind == LegacyContentKind.Message)
                    screen.Text = "HELLO";
                else if (kind == LegacyContentKind.Property)
                    screen.Source = new PropertySpec
                    {
                        Kind = PropertyKind.BuiltIn,
                        Name = BuiltInProperties.Fuel,
                    };
                config.Legacy.Screens.Add(screen);
                i++;
            }

            var loaded = Load(DisplayConfigSerializer.Save(config), out var warnings);

            Assert.Empty(warnings);
            Assert.Equal(kinds.Length, loaded.Legacy.Screens.Count);
            for (int k = 0; k < kinds.Length; k++)
            {
                Assert.Equal(kinds[k], loaded.Legacy.Screens[k].ContentKind);
                Assert.Equal(effects[k % effects.Length], loaded.Legacy.Screens[k].Effect);
            }
        }

        [Fact]
        public void SaveAfterLoad_UnknownContentKindAndEffect_SurviveVerbatim()
        {
            // A future version's contentKind/effect must pass through this build
            // byte-for-byte — the screen is kept (not dropped) but is not a survivor.
            string original =
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"x1\", \"text\": \"PIT\", \"contentKind\": \"hologram\", \"effect\": \"shimmer\" }, "
                + "{ \"id\": \"ok\", \"text\": \"FN1\" } "
                + "], \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"isTrue\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"DrsEnabled\" } }, "
                + "\"show\": { \"kind\": \"screen\", \"screenId\": \"x1\" } } "
                + "] } }";

            var config = Load(original, out var warnings);
            Assert.Equal(2, config.Legacy.Screens.Count);
            var future = config.Legacy.Screens[0];
            Assert.Equal("hologram", future.ContentKindRaw);
            Assert.Equal(LegacyContentKind.Unknown, future.ContentKind);
            Assert.Equal("shimmer", future.EffectRaw);
            Assert.Equal(LegacyEffect.Unknown, future.Effect);
            Assert.Contains(warnings, w => w.Contains("hologram"));

            // Rules targeting the unknown-kind screen degrade like a missing screen.
            Assert.True(config.Legacy.Rules[0].DegradedAtLoad);
            Assert.Contains(warnings, w => w.Contains("x1") && w.Contains("does not exist"));

            string saved = DisplayConfigSerializer.Save(config);
            Assert.Contains("\"hologram\"", saved);
            Assert.Contains("\"shimmer\"", saved);

            var reloaded = Load(saved, out _);
            Assert.Equal("hologram", reloaded.Legacy.Screens[0].ContentKindRaw);
            Assert.Equal("shimmer", reloaded.Legacy.Screens[0].EffectRaw);
            Assert.Equal(saved, DisplayConfigSerializer.Save(reloaded));
        }

        // Spec P10a: removed GearAndSpeed spelling degrades like any unknown kind —
        // screen kept, excluded from survivors, targeting rule degraded; raw survives.
        [Fact]
        public void GearAndSpeed_Spelling_DegradesAsUnknown_ByteIdenticalRoundTrip()
        {
            string original =
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"gs\", \"name\": \"Gear + Speed\", \"contentKind\": \"gearAndSpeed\" }, "
                + "{ \"id\": \"ok\", \"text\": \"FN1\" } "
                + "], \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"isTrue\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"DrsEnabled\" } }, "
                + "\"show\": { \"kind\": \"screen\", \"screenId\": \"gs\" } } "
                + "] } }";

            var config = Load(original, out var warnings);
            Assert.Equal(2, config.Legacy.Screens.Count);
            var composite = config.Legacy.Screens[0];
            Assert.Equal("gearAndSpeed", composite.ContentKindRaw);
            Assert.Equal(LegacyContentKind.Unknown, composite.ContentKind);
            Assert.Contains(warnings, w => w.Contains("gearAndSpeed"));
            Assert.True(config.Legacy.Rules[0].DegradedAtLoad);
            Assert.Contains(warnings, w => w.Contains("gs") && w.Contains("does not exist"));

            string saved = DisplayConfigSerializer.Save(config);
            Assert.Contains("\"gearAndSpeed\"", saved);
            var reloaded = Load(saved, out _);
            Assert.Equal("gearAndSpeed", reloaded.Legacy.Screens[0].ContentKindRaw);
            Assert.Equal(saved, DisplayConfigSerializer.Save(reloaded));
        }

        // Spec P10a: inRotation schema — absent → true; false serializes; true suppressed.
        [Fact]
        public void InRotation_RoundTrip_AbsentTrue_FalseSerializes_TrueSuppressed()
        {
            var absent = Load(
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"a\", \"text\": \"AAA\" } ] } }", out _);
            Assert.True(Assert.Single(absent.Legacy.Screens).InRotation);

            var explicitFalse = Load(
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"b\", \"text\": \"BBB\", \"inRotation\": false } ] } }", out _);
            Assert.False(Assert.Single(explicitFalse.Legacy.Screens).InRotation);
            string savedFalse = DisplayConfigSerializer.Save(explicitFalse);
            Assert.Contains("\"inRotation\": false", savedFalse);

            var explicitTrue = new DisplayCustomizationConfig();
            explicitTrue.Legacy.Screens.Add(new LegacyScreen
            {
                Id = "c",
                Text = "CCC",
                ContentKind = LegacyContentKind.Text,
                InRotation = true,
            });
            string savedTrue = DisplayConfigSerializer.Save(explicitTrue);
            Assert.DoesNotContain("inRotation", savedTrue);

            // Unknown members on a screen with inRotation survive (S1 discipline).
            string withFuture =
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"d\", \"text\": \"DDD\", \"inRotation\": false, "
                + "\"futureWidget\": 7 } ] } }";
            var withExt = Load(withFuture, out _);
            Assert.False(withExt.Legacy.Screens[0].InRotation);
            string savedExt = DisplayConfigSerializer.Save(withExt);
            Assert.Contains("\"inRotation\": false", savedExt);
            Assert.Contains("\"futureWidget\": 7", savedExt);
            Assert.Equal(savedExt, DisplayConfigSerializer.Save(Load(savedExt, out _)));
        }

        [Fact]
        public void BaseScreenId_UnknownContentKind_KeptForRoundTrip_NotCleared()
        {
            // Future-version base pointing at a kept-but-unusable screen must NOT clear
            // BaseScreenId (a persisted mutation) — only warn that this build blanks it.
            // Contrast: a base id with no kept screen at all still clears (existing behaviour).
            string original =
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"baseScreenId\": \"x1\", \"screens\": [ "
                + "{ \"id\": \"x1\", \"text\": \"PIT\", \"contentKind\": \"hologram\" }, "
                + "{ \"id\": \"ok\", \"text\": \"FN1\" } "
                + "] } }";

            var config = Load(original, out var warnings);
            Assert.Equal("x1", config.Legacy.BaseScreenId);
            Assert.Equal(2, config.Legacy.Screens.Count);
            Assert.Contains(warnings, w => w.Contains("base screen") && w.Contains("not usable"));
            Assert.DoesNotContain(warnings, w => w.Contains("does not exist"));

            string saved = DisplayConfigSerializer.Save(config);
            Assert.Contains("\"baseScreenId\": \"x1\"", saved);
            Assert.Contains("\"hologram\"", saved);

            var reloaded = Load(saved, out _);
            Assert.Equal("x1", reloaded.Legacy.BaseScreenId);
            Assert.Equal(saved, DisplayConfigSerializer.Save(reloaded));
        }

        [Fact]
        public void LegacyScreen_MessageKind_AllowsLongRenderableText()
        {
            var config = Load(
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"msg\", \"contentKind\": \"message\", \"text\": \"HELLO\" } "
                + "] } }", out var warnings);

            var screen = Assert.Single(config.Legacy.Screens);
            Assert.Equal(LegacyContentKind.Message, screen.ContentKind);
            Assert.Equal("HELLO", screen.Text);
            Assert.Empty(warnings);
        }

        [Fact]
        public void LegacyScreen_MessageKind_Unrenderable_Skipped()
        {
            var config = Load(
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"msg\", \"contentKind\": \"message\", \"text\": \"\\u25c6F\\u25c6\" } "
                + "] } }", out var warnings);

            Assert.Empty(config.Legacy.Screens);
            Assert.Contains(warnings, w => w.Contains("'msg'"));
        }

        [Fact]
        public void LegacyScreen_DynamicKind_IgnoresText()
        {
            // Dynamic kinds do not require Text — even oversized/empty is fine.
            var config = Load(
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"spd\", \"contentKind\": \"speed\", \"text\": \"TOOLONG\" }, "
                + "{ \"id\": \"gr\", \"contentKind\": \"gear\" } "
                + "] } }", out var warnings);

            Assert.Equal(2, config.Legacy.Screens.Count);
            Assert.Empty(warnings);
            Assert.Equal(LegacyContentKind.Speed, config.Legacy.Screens[0].ContentKind);
            Assert.Equal(LegacyContentKind.Gear, config.Legacy.Screens[1].ContentKind);
        }

        [Fact]
        public void LegacyScreen_PropertyWithoutSource_Skipped()
        {
            var config = Load(
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"p1\", \"contentKind\": \"property\" }, "
                + "{ \"id\": \"p2\", \"contentKind\": \"property\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"FluxCapacitor\" } }, "
                + "{ \"id\": \"p3\", \"contentKind\": \"property\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" } } "
                + "] } }", out var warnings);

            var kept = Assert.Single(config.Legacy.Screens);
            Assert.Equal("p3", kept.Id);
            Assert.Equal(2, warnings.Count(w => w.Contains("skipped")));
        }

        [Fact]
        public void LegacyScreen_FlashEffect_CoercesToBlink_RawSurvives()
        {
            var config = Load(
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"pit\", \"text\": \"PIT\", \"effect\": \"flash\" } "
                + "] } }", out var warnings);

            var screen = Assert.Single(config.Legacy.Screens);
            Assert.Equal(LegacyEffect.Blink, screen.Effect);   // runtime view
            Assert.Equal("flash", screen.EffectRaw);           // document preserved
            Assert.Contains(warnings, w => w.Contains("flash") && w.Contains("blink"));

            string saved = DisplayConfigSerializer.Save(config);
            Assert.Contains("\"flash\"", saved);
            Assert.DoesNotContain("\"blink\"", saved);
        }

        [Fact]
        public void LegacyScreen_UnknownFormat_ClearedWithWarning()
        {
            var config = Load(
                "{ \"schemaVersion\": 1, \"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"pit\", \"text\": \"PIT\", \"format\": \"fancy\" } "
                + "] } }", out var warnings);

            var screen = Assert.Single(config.Legacy.Screens);
            Assert.Null(screen.Format);
            Assert.Contains(warnings, w => w.Contains("fancy"));
        }

        [Fact]
        public void RoundTrip_LegacyScreen_PropertySourcePreserved()
        {
            var config = new DisplayCustomizationConfig();
            config.Legacy.Screens.Add(new LegacyScreen
            {
                Id = "fuel",
                ContentKind = LegacyContentKind.Property,
                Source = new PropertySpec
                {
                    Kind = PropertyKind.SimHubProperty,
                    Name = "DataCorePlugin.GameData.Fuel",
                },
                Effect = LegacyEffect.Scroll,
            });

            var loaded = Load(DisplayConfigSerializer.Save(config), out var warnings);
            Assert.Empty(warnings);
            var screen = Assert.Single(loaded.Legacy.Screens);
            Assert.Equal(LegacyContentKind.Property, screen.ContentKind);
            Assert.Equal(PropertyKind.SimHubProperty, screen.Source.Kind);
            Assert.Equal("DataCorePlugin.GameData.Fuel", screen.Source.Name);
            Assert.Equal(LegacyEffect.Scroll, screen.Effect);
        }

        // ── Field mappings ───────────────────────────────────────────────

        [Fact]
        public void FieldMappings_InvalidSources_Dropped()
        {
            var config = Load(
                "{ \"schemaVersion\": 1, \"fieldMappings\": { "
                + "\"5\": { \"source\": { \"kind\": \"simHubProperty\", \"name\": \"X.Y\" } }, "
                + "\"20\": { \"source\": { \"kind\": \"builtIn\", \"name\": \"FluxCapacitor\" } }, "
                + "\"25\": { \"source\": { \"kind\": \"fanaBridgeAction\", \"name\": \"Go\" } }, "
                + "\"33\": { \"format\": \"celsius\" } "
                + "} }", out var warnings);

            Assert.Single(config.FieldMappings);
            Assert.True(config.FieldMappings.ContainsKey(ItmParam.Fuel));
            Assert.Equal(3, warnings.Count(w => w.Contains("field mapping")));
        }

        [Fact]
        public void FieldMappings_GearAndEngineMapping_Dropped()
        {
            var config = Load(
                "{ \"schemaVersion\": 1, \"fieldMappings\": { "
                + "\"4\": { \"source\": { \"kind\": \"builtIn\", \"name\": \"Gear\" } }, "
                + "\"26\": { \"source\": { \"kind\": \"builtIn\", \"name\": \"EngineMap\" } }, "
                + "\"5\": { \"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" } } "
                + "} }", out var warnings);

            Assert.Single(config.FieldMappings);
            Assert.True(config.FieldMappings.ContainsKey(ItmParam.Fuel));
            Assert.False(config.FieldMappings.ContainsKey(ItmParam.Gear));
            Assert.False(config.FieldMappings.ContainsKey(ItmParam.EngineMapping));
            Assert.Equal(2, warnings.Count(w => w.Contains("cannot be remapped")));
        }

        [Theory]
        [InlineData("5", "withTotal", true)]   // Fuel total family
        [InlineData("5", "bare", true)]
        [InlineData("5", "unit", false)]       // unit is temp-only
        [InlineData("5", "fuel-laps", false)]  // unknown
        [InlineData("505", "withTotal", true)] // Lap
        [InlineData("501", "bare", true)]      // Position
        [InlineData("33", "unit", true)]       // OilTemp
        [InlineData("33", "bare", true)]
        [InlineData("33", "withTotal", false)]
        [InlineData("1", "bare", false)]       // Speed — no format options
        public void FieldMappings_FormatVocabulary_PerFamily(string paramKey, string format, bool kept)
        {
            var config = Load(
                "{ \"schemaVersion\": 1, \"fieldMappings\": { "
                + "\"" + paramKey + "\": { \"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, "
                + "\"format\": \"" + format + "\" } } }", out var warnings);

            Assert.Single(config.FieldMappings);
            var mapping = config.FieldMappings[ushort.Parse(paramKey)];
            if (kept)
            {
                Assert.Equal(format, mapping.Format);
                Assert.DoesNotContain(warnings, w => w.Contains("unrecognized format"));
            }
            else
            {
                Assert.Null(mapping.Format);
                Assert.Contains(warnings, w => w.Contains("unrecognized format"));
            }
        }

        // ── Determinism ──────────────────────────────────────────────────

        [Fact]
        public void Load_SameDocumentTwice_ProducesSameNormalization()
        {
            string json = DocWithItmRule(
                "{ \"id\": \"r1\", \"when\": { \"kind\": \"changes\", \"source\": { \"kind\": \"builtIn\", \"name\": \"BrakeBias\" } }, "
                + ValidShow + ", \"hold\": { \"kind\": \"whileActive\" } }");

            var a = Load(json, out var warningsA);
            var b = Load(json, out var warningsB);

            Assert.Equal(warningsA, warningsB);
            Assert.Equal(DisplayConfigSerializer.Save(a), DisplayConfigSerializer.Save(b));
        }

        // ── S4 old-spelling degrade pins (spec-v9-s4-rename-freeze §3) ───
        // Old spellings must degrade safely: never crash, never rewrite.

        [Fact]
        public void OldSpelling_LegacySection_IsUnknownMember_SegmentWorldEmpty()
        {
            // A "legacy" section is now an unknown top-level member → ExtensionData;
            // the segment world (Legacy property / segmentDisplay) reads empty.
            var config = Load(
                "{ \"schemaVersion\": 1, \"legacy\": { \"baseScreenId\": \"s1\", "
                + "\"screens\": [ { \"id\": \"s1\", \"text\": \"PIT\" } ], \"rules\": [] } }",
                out var warnings);

            Assert.Empty(config.Legacy.Screens);
            Assert.Null(config.Legacy.BaseScreenId);
            Assert.NotNull(config.ExtensionData);

            // The COMPLETE raw section survives as the extension token, byte-faithful.
            var expected = Newtonsoft.Json.Linq.JToken.Parse(
                "{ \"baseScreenId\": \"s1\", "
                + "\"screens\": [ { \"id\": \"s1\", \"text\": \"PIT\" } ], \"rules\": [] }");
            Assert.True(config.ExtensionData.TryGetValue("legacy", out var legacyBag));
            Assert.True(Newtonsoft.Json.Linq.JToken.DeepEquals(expected, legacyBag),
                "raw legacy section mutated: " + legacyBag);

            // Save → load → save is stable (same discipline as the other five pins).
            string saved = DisplayConfigSerializer.Save(config);
            Assert.Contains("\"legacy\"", saved);
            Assert.Equal(saved, DisplayConfigSerializer.Save(Load(saved, out _)));
            Assert.Empty(warnings);
        }

        [Fact]
        public void OldSpelling_KindLegacyScreen_DegradesUnknown_RawPreserved()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen + ", "
                + "\"show\": { \"kind\": \"legacyScreen\", \"screenId\": \"fn1\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } }"), out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.True(rule.DegradedAtLoad);
            Assert.Equal(TargetKind.Unknown, rule.Show.Kind);
            Assert.Equal("legacyScreen", rule.Show.KindRaw);
            Assert.Equal("fn1", rule.Show.ScreenId);
            Assert.Contains(warnings, w => w.Contains("legacyScreen"));

            string saved = DisplayConfigSerializer.Save(config);
            Assert.Contains("\"legacyScreen\"", saved);
            Assert.Contains("\"screenId\": \"fn1\"", saved);
            Assert.Equal(saved, DisplayConfigSerializer.Save(Load(saved, out _)));
        }

        [Fact]
        public void OldSpelling_KindAlternate_DegradesUnknown_RawPreserved()
        {
            // Also covered by RoundTrip_UntouchedAlternate_DegradesPreserved; one-case pin.
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen + ", "
                + "\"show\": { \"kind\": \"alternate\", \"pageA\": \"fuelErsDrs\", \"pageB\": \"tyreTemps\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } }"), out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.True(rule.DegradedAtLoad);
            Assert.Equal(TargetKind.Unknown, rule.Show.Kind);
            Assert.Equal("alternate", rule.Show.KindRaw);
            Assert.Contains(warnings, w => w.Contains("alternate"));

            string saved = DisplayConfigSerializer.Save(config);
            Assert.Contains("\"alternate\"", saved);
            Assert.Contains("\"pageA\"", saved);
            Assert.Contains("\"pageB\"", saved);
            Assert.Equal(saved, DisplayConfigSerializer.Save(Load(saved, out _)));
        }

        [Fact]
        public void OldSpelling_EligibleMember_LandsInExtensionData_RawPreserved()
        {
            // "eligible" is no longer a known member → ExtensionData on the rule;
            // Eligible defaults (InGame) when runs is absent.
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen + ", " + ValidShow + ", "
                + "\"eligible\": \"idle\", \"hold\": { \"kind\": \"whileActive\" } }"),
                out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.Equal(RuleEligibility.InGame, rule.Eligible);   // default — runs absent
            Assert.Null(rule.EligibleRaw);
            Assert.NotNull(rule.ExtensionData);
            Assert.Equal("idle", (string)rule.ExtensionData["eligible"]);
            Assert.Empty(warnings);

            string saved = DisplayConfigSerializer.Save(config);
            Assert.Contains("\"eligible\": \"idle\"", saved);
            Assert.DoesNotContain("\"runs\"", saved);
            Assert.Equal(saved, DisplayConfigSerializer.Save(Load(saved, out _)));
        }

        [Fact]
        public void OldSpelling_HoldIndefinite_DegradesUnknown_RawPreserved()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen + ", " + ValidShow + ", "
                + "\"hold\": { \"kind\": \"indefinite\" } }"), out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            // Runtime coerce to level family default; KindRaw keeps indefinite.
            Assert.Equal(HoldKind.WhileActive, rule.Hold.Kind);
            Assert.Equal("indefinite", rule.Hold.KindRaw);
            Assert.False(rule.DegradedAtLoad);   // hold-unknown coerces, does not disable
            Assert.Contains(warnings, w => w.Contains("indefinite"));

            string saved = DisplayConfigSerializer.Save(config);
            Assert.Contains("\"indefinite\"", saved);
            Assert.Equal(saved, DisplayConfigSerializer.Save(Load(saved, out _)));
        }

        [Fact]
        public void PageLegacy_IsTheFrozenPage6Spelling_ParsesValid()
        {
            // Ship-v1 decision (kelchm 2026-07-23): the page-6 identity keeps the
            // hardware-truthful spelling "legacy" — the wheel's own on-screen label —
            // while the surface/world it hosts is named segmentDisplay. NOT a degrade
            // case: this spelling is frozen-valid.
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen + ", "
                + "\"show\": { \"kind\": \"page\", \"page\": \"legacy\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } }"), out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.False(rule.DegradedAtLoad);
            Assert.Equal(ItmPage.Legacy, rule.Show.Page);
            Assert.DoesNotContain(warnings, w => w.Contains("legacy"));

            string saved = DisplayConfigSerializer.Save(config);
            Assert.Contains("\"page\": \"legacy\"", saved);
            Assert.Equal(saved, DisplayConfigSerializer.Save(Load(saved, out _)));
        }
    }
}

