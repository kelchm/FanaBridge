using System.Collections.Generic;
using System.Linq;
using FanaBridge.Display.Host;
using FanaBridge.Display.Legacy;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests.Display.Host
{
    /// <summary>
    /// Phase 9a / P10a pure migration step: frozen DisplayMode → pure screens + overlays.
    /// </summary>
    public class LegacyModeMigrationTests
    {
        [Theory]
        [InlineData("Gear", LegacyContentKind.Gear, "Gear")]
        [InlineData("Speed", LegacyContentKind.Speed, "Speed")]
        [InlineData("TotallyBogus", LegacyContentKind.Gear, "Gear")]
        public void SynthesizesScreen_Shape_IdNameKindBase(
            string mode, LegacyContentKind kind, string label)
        {
            var settings = new DisplaySettings { DisplayMode = mode };
            var config = LegacyModeMigration.Apply(settings, null);

            Assert.True(settings.LegacyModeMigrated);
            Assert.NotNull(config);
            var screen = Assert.Single(config!.Legacy.Screens);
            Assert.False(string.IsNullOrEmpty(screen.Id));
            Assert.Equal(32, screen.Id.Length); // Guid "N"
            Assert.Equal(label, screen.Name);
            Assert.Equal(kind, screen.ContentKind);
            Assert.True(screen.InRotation);
            Assert.Equal(screen.Id, config.Legacy.BaseScreenId);
            Assert.Empty(config.Legacy.Rules);
        }

        // Spec P10a: composite modes synthesize base + overlay (inRotation false) + rule.
        [Fact]
        public void GearAndSpeed_SynthesizesTrio_BaseSpeed_OverlayGear_ChangesRule()
        {
            var settings = new DisplaySettings { DisplayMode = "GearAndSpeed" };
            var config = LegacyModeMigration.Apply(settings, null);

            Assert.True(settings.LegacyModeMigrated);
            Assert.Equal(2, config.Legacy.Screens.Count);
            var speed = config.Legacy.Screens[0];
            var gear = config.Legacy.Screens[1];
            Assert.Equal("Speed", speed.Name);
            Assert.Equal(LegacyContentKind.Speed, speed.ContentKind);
            Assert.True(speed.InRotation);
            Assert.Equal("Gear", gear.Name);
            Assert.Equal(LegacyContentKind.Gear, gear.ContentKind);
            Assert.False(gear.InRotation);
            Assert.Equal(speed.Id, config.Legacy.BaseScreenId);

            var rule = Assert.Single(config.Legacy.Rules);
            Assert.Equal(32, rule.Id.Length);
            Assert.Equal("Gear change", rule.Name);
            Assert.Equal(ConditionKind.Changes, rule.When.Kind);
            Assert.Equal(PropertyKind.BuiltIn, rule.When.Source.Kind);
            Assert.Equal(BuiltInProperties.Gear, rule.When.Source.Name);
            Assert.Equal(TargetKind.LegacyScreen, rule.Show.Kind);
            Assert.Equal(gear.Id, rule.Show.ScreenId);
            Assert.Equal(HoldKind.ForDuration, rule.Hold.Kind);
            Assert.Equal(LegacyValueFormatter.GearOverlayMs, rule.Hold.DurationMs);
            // Eligibility omitted → default InGame (telemetry-gated, matches driver).
            Assert.True(string.IsNullOrEmpty(rule.EligibleRaw));
            Assert.Equal(RuleEligibility.InGame, rule.Eligible);
        }

        [Fact]
        public void GearUpshiftBrackets_SynthesizesTrio_BaseGear_OverlayBrackets_RedlineRule()
        {
            var settings = new DisplaySettings { DisplayMode = "GearUpshiftBrackets" };
            var config = LegacyModeMigration.Apply(settings, null);

            Assert.True(settings.LegacyModeMigrated);
            Assert.Equal(2, config.Legacy.Screens.Count);
            var gear = config.Legacy.Screens[0];
            var brackets = config.Legacy.Screens[1];
            Assert.Equal("Gear", gear.Name);
            Assert.Equal(LegacyContentKind.Gear, gear.ContentKind);
            Assert.True(gear.InRotation);
            Assert.Equal("Gear (brackets)", brackets.Name);
            Assert.Equal(LegacyContentKind.GearBrackets, brackets.ContentKind);
            Assert.False(brackets.InRotation);
            Assert.Equal(gear.Id, config.Legacy.BaseScreenId);

            var rule = Assert.Single(config.Legacy.Rules);
            Assert.Equal(32, rule.Id.Length);
            Assert.Equal("Redline", rule.Name);
            Assert.Equal(ConditionKind.IsTrue, rule.When.Kind);
            Assert.Equal(PropertyKind.BuiltIn, rule.When.Source.Kind);
            Assert.Equal(BuiltInProperties.RedlineReached, rule.When.Source.Name);
            Assert.Equal(TargetKind.LegacyScreen, rule.Show.Kind);
            Assert.Equal(brackets.Id, rule.Show.ScreenId);
            Assert.Equal(HoldKind.WhileActive, rule.Hold.Kind);
            Assert.True(string.IsNullOrEmpty(rule.EligibleRaw));
            Assert.Equal(RuleEligibility.InGame, rule.Eligible);
        }

        [Fact]
        public void ModeNone_NoSynthesis_StillBakesMarker()
        {
            var settings = new DisplaySettings { DisplayMode = DisplaySettings.ModeNone };
            var config = LegacyModeMigration.Apply(settings, null);

            Assert.True(settings.LegacyModeMigrated);
            Assert.Null(config);
        }

        [Fact]
        public void BakeOnSight_AuthoredWorld_NoExtraScreen()
        {
            var authored = DisplayConfigSerializer.Load(
                "{ \"legacy\": { \"baseScreenId\": \"pit\", "
                + "\"screens\": [ { \"id\": \"pit\", \"name\": \"Pit\", \"text\": \"PIT\" } ] } }",
                _ => { });
            var settings = new DisplaySettings { DisplayMode = "Speed" };

            var result = LegacyModeMigration.Apply(settings, authored);

            Assert.True(settings.LegacyModeMigrated);
            Assert.Same(authored, result);
            Assert.Single(result.Legacy.Screens);
            Assert.Equal("pit", result.Legacy.Screens[0].Id);
            Assert.Equal(LegacyContentKind.Text, result.Legacy.Screens[0].ContentKind);
        }

        [Fact]
        public void Idempotent_SecondApply_NoDuplicate()
        {
            var settings = new DisplaySettings { DisplayMode = "Gear" };
            var first = LegacyModeMigration.Apply(settings, null);
            Assert.Single(first.Legacy.Screens);

            var second = LegacyModeMigration.Apply(settings, first);
            Assert.Same(first, second);
            Assert.Single(second.Legacy.Screens);
        }

        [Fact]
        public void Graft_PreservesItmAndFieldMappings()
        {
            var withItm = DisplayConfigSerializer.Load(
                "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"greaterThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } } ], \"basePage\": \"tyreTemps\" }, "
                + "\"fieldMappings\": { \"505\": { \"source\": { \"kind\": \"builtIn\", \"name\": \"Position\" } } } }",
                _ => { });
            Assert.False(DisplayRuleStackHasLegacy(withItm));
            var settings = new DisplaySettings { DisplayMode = "Speed" };

            var result = LegacyModeMigration.Apply(settings, withItm);

            Assert.Same(withItm, result);
            Assert.Single(result.Itm.Rules);
            Assert.Equal("r1", result.Itm.Rules[0].Id);
            Assert.Equal(ItmPage.TyreTemps, result.Itm.BasePage);
            Assert.NotNull(result.FieldMappings);
            Assert.True(result.FieldMappings.ContainsKey(505));
            Assert.Single(result.Legacy.Screens);
            Assert.Equal(LegacyContentKind.Speed, result.Legacy.Screens[0].ContentKind);
            Assert.Equal(result.Legacy.Screens[0].Id, result.Legacy.BaseScreenId);
        }

        [Fact]
        public void RawJObject_Untouched_ByCodecReadAndMigration()
        {
            // Codec Read never rewrites the source; migration only mutates the parsed
            // graph + settings object — never a JObject.
            var source = new JObject
            {
                ["displayMode"] = "Gear",
                ["displayControl"] = DisplaySettings.ControlLegacy,
                ["unrelated"] = 42,
            };
            var original = (JObject)source.DeepClone();

            var settings = DisplaySettingsCodec.Read(source, itmCapable: false);
            Assert.True(JToken.DeepEquals(original, source));

            LegacyModeMigration.Apply(settings, null);
            Assert.True(JToken.DeepEquals(original, source));
            Assert.True(settings.LegacyModeMigrated);
        }

        [Fact]
        public void DeletionAfterBake_DoesNotResurrect()
        {
            // Marker true + empty world + frozen Gear must NOT re-seed a page.
            var settings = new DisplaySettings
            {
                DisplayMode = "Gear",
                LegacyModeMigrated = true,
            };
            var empty = DisplayConfigSerializer.Load("{ \"schemaVersion\": 1 }", _ => { });

            var result = LegacyModeMigration.Apply(settings, empty);

            Assert.True(settings.LegacyModeMigrated);
            Assert.Same(empty, result);
            Assert.True(result.IsEmpty);
            Assert.Empty(result.Legacy.Screens);
        }

        [Fact]
        public void MarkerFalse_EmptyWorld_RepeatedApplyWithoutMarkerReset_OneScreen()
        {
            // Simulates two SetSettings entries that both see marker false only if the
            // bag is not baked — with in-memory marker true after first apply, second
            // is a no-op (idempotence for the load-path short-circuit).
            var settings = new DisplaySettings { DisplayMode = "Gear" };
            var a = LegacyModeMigration.Apply(settings, null);
            var b = LegacyModeMigration.Apply(settings, a);
            Assert.Single(a.Legacy.Screens);
            Assert.Single(b.Legacy.Screens);
            Assert.Equal(a.Legacy.Screens[0].Id, b.Legacy.Screens[0].Id);
        }

        [Fact]
        public void TrySynthesize_NoneIsFalse_SimpleModesGraftSingleBase()
        {
            Assert.False(LegacyModeMigration.TrySynthesize(DisplaySettings.ModeNone, out _));

            var leg = new LegacyRuleSet();
            Assert.True(LegacyModeMigration.TrySynthesize("Speed", out var graft));
            graft(leg);
            Assert.Single(leg.Screens);
            Assert.Equal(LegacyContentKind.Speed, leg.Screens[0].ContentKind);
            Assert.True(leg.Screens[0].InRotation);
            Assert.Equal(leg.Screens[0].Id, leg.BaseScreenId);
            Assert.Empty(leg.Rules);

            // Unknown mode → Gear single base (driver unknown-mode fallback).
            var unknown = new LegacyRuleSet();
            Assert.True(LegacyModeMigration.TrySynthesize("someFutureMode", out var g2));
            g2(unknown);
            Assert.Single(unknown.Screens);
            Assert.Equal(LegacyContentKind.Gear, unknown.Screens[0].ContentKind);
        }

        // Local mirror of the runtime gate (Host tests don't need DisplayRuleStack).
        private static bool DisplayRuleStackHasLegacy(DisplayCustomizationConfig config)
        {
            var leg = config?.Legacy;
            if (leg == null)
                return false;
            return (leg.Rules != null && leg.Rules.Count > 0)
                || (leg.Screens != null && leg.Screens.Count > 0);
        }
    }
}
