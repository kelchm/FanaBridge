using System.Linq;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using FanaBridge.UI.Display;
using FanaBridge.UI.Display.Shared;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// Triggers edit model in legacy mode: rule-set switch, virtual-page SHOW vocabulary
    /// ("◧ name"), survivors list, base screen footer, Runs reused as-is.
    /// </summary>
    public class DisplayTriggersLegacyEditModelTests
    {
        private static DisplayCustomizationConfig Load(string json)
            => DisplayConfigSerializer.Load(json, _ => { });

        private static DisplayCustomizationConfig LegacyWorld()
            => Load("{ \"schemaVersion\": 1, \"legacy\": { \"baseScreenId\": \"spd\", "
                + "\"screens\": [ "
                + "{ \"id\": \"spd\", \"name\": \"Speed\", \"contentKind\": \"speed\" }, "
                + "{ \"id\": \"pit\", \"name\": \"Pit\", \"text\": \"PIT\" } "
                + "], \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"isTrue\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"PitLimiterOn\" } }, "
                + "\"show\": { \"kind\": \"legacyScreen\", \"screenId\": \"pit\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } } "
                + "] } }");

        [Fact]
        public void LegacyMode_ReadsAndMutatesLegacyRules_Only()
        {
            var start = LegacyWorld();
            // Plant an ITM rule that must survive untouched by reference.
            start.Itm.Rules.Add(new DisplayRule
            {
                Id = "itm1",
                When = new RuleCondition
                {
                    Kind = ConditionKind.GreaterThan,
                    Source = new PropertySpec { Kind = PropertyKind.BuiltIn, Name = BuiltInProperties.Fuel },
                    Value = 10,
                },
                Show = new RuleTarget { Kind = TargetKind.Page, Page = ItmPage.FuelErsDrs },
            });

            var model = new DisplayTriggersEditModel(start, itmDeviceId: 0,
                ruleSet: TriggerRuleSet.Legacy);
            Assert.True(model.IsLegacyMode);
            Assert.Single(model.Rules);
            Assert.Equal("r1", model.Rules[0].Id);

            var draft = DisplayTriggersEditModel.ToDraft(model.Rules[0]);
            draft.ScreenId = "spd";
            var cfg = model.UpdateRule(draft);

            Assert.Equal("spd", cfg.Legacy.Rules[0].Show.ScreenId);
            Assert.Single(cfg.Itm.Rules);
            Assert.Equal("itm1", cfg.Itm.Rules[0].Id);
            Assert.Same(start.Itm.Rules[0], cfg.Itm.Rules[0]); // ITM list instance carried
        }

        [Fact]
        public void ShowTextFor_LegacyScreen_UsesHalfBlockAndDisplayName()
        {
            var model = new DisplayTriggersEditModel(LegacyWorld(), 0,
                ruleSet: TriggerRuleSet.Legacy);
            var text = model.ShowTextFor(new RuleTarget
            {
                Kind = TargetKind.LegacyScreen,
                ScreenId = "pit",
            });
            Assert.Equal(DisplayTriggersEditModel.LegacyShowGlyph + " Pit", text);
        }

        [Fact]
        public void NewTelemetryDraft_DefaultsToLegacyScreen_FirstSurvivor()
        {
            var model = new DisplayTriggersEditModel(LegacyWorld(), 0,
                ruleSet: TriggerRuleSet.Legacy);
            var draft = model.NewTelemetryDraft();
            Assert.Equal(TargetKind.LegacyScreen, draft.TargetKind);
            Assert.Equal("spd", draft.ScreenId); // first survivor in document order
            Assert.Null(draft.Page);
        }

        [Fact]
        public void ScreenOptions_AreSurvivors_UnknownExcluded()
        {
            var cfg = Load("{ \"schemaVersion\": 1, \"legacy\": { \"screens\": [ "
                + "{ \"id\": \"ok\", \"text\": \"PIT\" }, "
                + "{ \"id\": \"x\", \"text\": \"PIT\", \"contentKind\": \"hologram\" } "
                + "] } }");
            var model = new DisplayTriggersEditModel(cfg, 0, ruleSet: TriggerRuleSet.Legacy);
            var opts = model.ScreenOptions();
            Assert.Single(opts);
            Assert.Equal("ok", opts[0].Id);
        }

        [Fact]
        public void SetBaseScreenId_BlankAndScreen()
        {
            var model = new DisplayTriggersEditModel(LegacyWorld(), 0,
                ruleSet: TriggerRuleSet.Legacy);
            Assert.Equal("spd", model.EffectiveBaseScreenId);
            Assert.Equal("Speed", model.EffectiveBaseScreenName());

            var blank = model.SetBaseScreenId(DisplayVirtualPagesEditModel.BaseBlankId);
            Assert.Null(blank.Legacy.BaseScreenId);
            Assert.Equal("Blank", model.EffectiveBaseScreenName());

            var pit = model.SetBaseScreenId("pit");
            Assert.Equal("pit", pit.Legacy.BaseScreenId);
            // Rules list preserved.
            Assert.Single(pit.Legacy.Rules);
        }

        [Fact]
        public void Rows_Workbench_ShowTextUsesGlyph_NoBaseRow()
        {
            var model = new DisplayTriggersEditModel(LegacyWorld(), 0,
                ruleSet: TriggerRuleSet.Legacy);
            var rows = model.Rows(null, defaultWirePage: 1, TriggerTableMode.Workbench);
            Assert.Single(rows);
            Assert.Equal(DisplayTriggersEditModel.LegacyShowGlyph + " Pit", rows[0].ShowText);
            Assert.False(rows[0].IsBase);
            // Runs vocabulary reused.
            Assert.Equal(DisplayTriggersEditModel.RunGlyph(DisplayTriggersEditModel.RunInGame),
                rows[0].RunGlyph);
        }

        [Fact]
        public void Rows_Monitor_PinsBaseWithGlyph()
        {
            var model = new DisplayTriggersEditModel(LegacyWorld(), 0,
                ruleSet: TriggerRuleSet.Legacy);
            var rows = model.Rows(null, defaultWirePage: 1, TriggerTableMode.Monitor);
            Assert.Equal(2, rows.Count);
            Assert.True(rows[1].IsBase);
            Assert.Equal("Always → Speed", rows[1].Label);
            Assert.Equal(DisplayTriggersEditModel.LegacyShowGlyph + " Speed", rows[1].ShowText);
        }

        [Fact]
        public void AddRule_Legacy_WritesLegacyScreenTarget()
        {
            var model = new DisplayTriggersEditModel(LegacyWorld(), 0,
                ruleSet: TriggerRuleSet.Legacy);
            var draft = model.NewTelemetryDraft();
            draft.SourceName = "GameData.Flag_Yellow";
            draft.Operator = ConditionKind.IsTrue;
            draft.ScreenId = "pit";
            Assert.True(DisplayTriggersEditModel.IsCommittable(draft));

            var cfg = model.AddRule(draft);
            Assert.Equal(2, cfg.Legacy.Rules.Count);
            var added = cfg.Legacy.Rules[1];
            Assert.Equal(TargetKind.LegacyScreen, added.Show.Kind);
            Assert.Equal("pit", added.Show.ScreenId);
        }

        [Fact]
        public void ItmMode_SetBaseScreenId_IsNoOp()
        {
            var model = new DisplayTriggersEditModel(LegacyWorld(), 3,
                ruleSet: TriggerRuleSet.Itm);
            var before = model.Config;
            var after = model.SetBaseScreenId("pit");
            Assert.Same(before, after);
        }

        [Fact]
        public void RunsChoices_UnchangedInLegacyMode()
        {
            var draft = new RuleEdit { Eligibility = RuleEligibility.Idle, Enabled = true };
            var choices = DisplayTriggersEditModel.RunsChoices(draft);
            Assert.Equal(4, choices.Items.Count);
            Assert.Equal(DisplayTriggersEditModel.RunIdle, choices.SelectedId);
        }
    }
}
