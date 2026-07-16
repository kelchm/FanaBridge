using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests
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
                new HoldSpec { Kind = HoldKind.Indefinite },
                RuleEligibility.Any));
            config.Itm.Rules.Add(Rule("screen",
                Level(ConditionKind.IsTrue, BuiltInProperties.DrsEnabled),
                new RuleTarget { Kind = TargetKind.LegacyScreen, ScreenId = "fn1" },
                new HoldSpec { Kind = HoldKind.WhileActive },
                RuleEligibility.Idle));
            config.Itm.Rules.Add(Rule("alt",
                Level(ConditionKind.GreaterOrEqual, BuiltInProperties.Position, 3),
                new RuleTarget
                {
                    Kind = TargetKind.Alternate,
                    PageA = ItmPage.FuelErsDrs,
                    PageB = ItmPage.TyreTemps,
                    PeriodMs = 4000,
                },
                new HoldSpec { Kind = HoldKind.ForDuration, DurationMs = 8000 }));

            var loaded = Load(DisplayConfigSerializer.Save(config), out var warnings);

            Assert.Empty(warnings);
            Assert.Equal(ItmPage.LapTimes, loaded.Itm.BasePage);

            var page = loaded.Itm.Rules[0];
            Assert.Equal(TargetKind.Page, page.Show.Kind);
            Assert.Equal(ItmPage.TyreTemps, page.Show.Page);
            Assert.Equal(HoldKind.Indefinite, page.Hold.Kind);
            Assert.Equal(RuleEligibility.Any, page.Eligible);

            var screen = loaded.Itm.Rules[1];
            Assert.Equal(TargetKind.LegacyScreen, screen.Show.Kind);
            Assert.Equal("fn1", screen.Show.ScreenId);
            Assert.Equal(HoldKind.WhileActive, screen.Hold.Kind);
            Assert.Equal(RuleEligibility.Idle, screen.Eligible);

            var alt = loaded.Itm.Rules[2];
            Assert.Equal(TargetKind.Alternate, alt.Show.Kind);
            Assert.Equal(ItmPage.FuelErsDrs, alt.Show.PageA);
            Assert.Equal(ItmPage.TyreTemps, alt.Show.PageB);
            Assert.Equal(4000, alt.Show.PeriodMs);
            Assert.Equal(HoldKind.ForDuration, alt.Hold.Kind);
            Assert.Equal(8000, alt.Hold.DurationMs);
        }

        [Fact]
        public void RoundTrip_LegacySetAndFieldMappings()
        {
            var config = new DisplayCustomizationConfig { ProfileId = "iracing-gt3" };
            config.Legacy.Screens.Add(new LegacyScreen { Id = "pit", Name = "Pit", Text = "PIT" });
            config.Legacy.BaseScreenId = "pit";
            config.Legacy.Rules.Add(Rule("l1",
                Level(ConditionKind.IsTrue, BuiltInProperties.DrsAvailable),
                new RuleTarget { Kind = TargetKind.LegacyScreen, ScreenId = "pit" },
                new HoldSpec { Kind = HoldKind.WhileActive }));
            config.FieldMappings[ItmParam.Fuel] = new FieldMapping
            {
                Source = new PropertySpec { Kind = PropertyKind.SimHubProperty, Name = "DataCorePlugin.Computed.Fuel_RemainingLaps" },
                Format = "fuel-laps",
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
            Assert.Equal("fuel-laps", mapping.Format);
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
                RuleEligibility.Any));

            string json = DisplayConfigSerializer.Save(config);

            Assert.Contains("\"schemaVersion\": 1", json);
            Assert.Contains("\"lessOrEqual\"", json);
            Assert.Contains("\"builtIn\"", json);
            Assert.Contains("\"page\"", json);
            Assert.Contains("\"lapTimes\"", json);
            Assert.Contains("\"forDuration\"", json);
            Assert.Contains("\"any\"", json);
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
                "{ \"id\": \"r1\", " + ValidWhen + ", " + ValidShow + ", \"eligible\": \"weekends\" }"),
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
                + "\"hold\": { \"kind\": \"shimmer\" }, \"eligible\": \"weekends\" } "
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
        public void Alternate_PeriodBelowFloor_Clamped()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen + ", "
                + "\"show\": { \"kind\": \"alternate\", \"pageA\": \"fuelErsDrs\", \"pageB\": \"tyreTemps\", \"periodMs\": 200 } }"),
                out var warnings);

            var rule = Assert.Single(config.Itm.Rules);
            Assert.True(rule.Enabled);
            Assert.Equal(RuleTarget.MinAlternatePeriodMs, rule.Show.PeriodMs);
            Assert.Contains(warnings, w => w.Contains("clamped"));
        }

        [Fact]
        public void Alternate_OmittedPeriod_DefaultsTo3000()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen + ", "
                + "\"show\": { \"kind\": \"alternate\", \"pageA\": \"fuelErsDrs\", \"pageB\": \"tyreTemps\" } }"),
                out var warnings);

            Assert.Empty(warnings);
            Assert.Equal(RuleTarget.DefaultAlternatePeriodMs, config.Itm.Rules[0].Show.PeriodMs);
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
                "{ \"schemaVersion\": 1, \"legacy\": { \"rules\": [ "
                + "{ \"id\": \"l1\", " + ValidWhen + ", " + ValidShow + " } "
                + "] } }", out var warnings);

            Assert.True(config.Legacy.Rules[0].DegradedAtLoad);
            Assert.Contains(warnings, w => w.Contains("legacy screens"));
        }

        [Fact]
        public void ScreenTarget_UnknownScreenId_DisablesRule()
        {
            var config = Load(DocWithItmRule(
                "{ \"id\": \"r1\", " + ValidWhen + ", \"show\": { \"kind\": \"legacyScreen\", \"screenId\": \"ghost\" } }"),
                out var warnings);

            Assert.True(config.Itm.Rules[0].DegradedAtLoad);
            Assert.Contains(warnings, w => w.Contains("ghost"));
        }

        // ── Legacy screens ───────────────────────────────────────────────

        [Fact]
        public void LegacyScreens_UnrenderableOrOversizedText_SkippedWithWarning()
        {
            var config = Load(
                "{ \"schemaVersion\": 1, \"legacy\": { \"baseScreenId\": \"logo\", \"screens\": [ "
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
                "{ \"schemaVersion\": 1, \"legacy\": { \"screens\": [ "
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
    }
}
