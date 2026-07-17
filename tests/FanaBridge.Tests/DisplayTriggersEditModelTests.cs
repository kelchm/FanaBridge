using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Adapters;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using FanaBridge.UI;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// The Triggers editor's edit model (<see cref="DisplayTriggersEditModel"/>): every
    /// mutation produces a fresh immutable document (ids stable, degraded rules carried
    /// through verbatim), the mapped-control mapping is pinned exactly, the UI option
    /// mappings hit the right schema enums, and the row model merges live state by id with
    /// the base and degraded rows. Plain functions — no SimHub, no UI thread.
    /// </summary>
    public class DisplayTriggersEditModelTests
    {
        private const byte Device3 = 3;   // standard six-page set

        private static DisplayCustomizationConfig Load(string json)
            => DisplayConfigSerializer.Load(json, _ => { });

        private static DisplayCustomizationConfig OneNormalRule()
            => Load("{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"greaterThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" }, "
                + "\"hold\": { \"kind\": \"forDuration\", \"durationMs\": 5000 } } ] } }");

        // ── Add ───────────────────────────────────────────────────────────

        [Fact]
        public void AddRule_FromNullConfig_CreatesTheDocument_WithAGuidId()
        {
            var model = new DisplayTriggersEditModel(null, Device3);
            var draft = model.NewTelemetryDraft();
            draft.SourceKind = PropertyKind.BuiltIn;
            draft.SourceName = BuiltInProperties.Fuel;
            draft.Operator = ConditionKind.LessThan;
            draft.Value = 10;
            draft.Page = ItmPage.FuelErsDrs;
            draft.Hold = HoldKind.Indefinite;

            var cfg = model.AddRule(draft);

            Assert.NotNull(cfg);
            var rule = Assert.Single(cfg.Itm.Rules);
            Assert.False(string.IsNullOrEmpty(rule.Id));
            Assert.Equal(32, rule.Id.Length);              // Guid "N" format
            Assert.Equal(ConditionKind.LessThan, rule.When.Kind);
            Assert.Equal(PropertyKind.BuiltIn, rule.When.Source.Kind);
            Assert.Equal("Fuel", rule.When.Source.Name);
            Assert.Equal(10, rule.When.Value);
            Assert.Equal(ItmPage.FuelErsDrs, rule.Show.Page);
            Assert.Equal(HoldKind.Indefinite, rule.Hold.Kind);
            Assert.Same(cfg, model.Config);
        }

        [Fact]
        public void NewDraft_DefaultTarget_AvoidsTheEffectiveBase_FromItmDefaultPage()
        {
            // No config base pinned, but ItmDefaultPage points at Fuel/ERS/DRS (wire 2 on the
            // standard set) — that IS the effective base the display rests on, so a new rule
            // must not default to it. Pre-fix DefaultTargetPage assumed Lap Info was the base
            // and returned Fuel/ERS/DRS (the base itself); the effective-base resolution fixes it.
            var model = new DisplayTriggersEditModel(null, Device3, defaultWirePage: 2);
            Assert.NotEqual(ItmPage.FuelErsDrs, model.NewTelemetryDraft().Page);
            Assert.NotEqual(ItmPage.FuelErsDrs, model.NewMappedControlDraft("Up Shift").Page);
        }

        [Fact]
        public void AddMappedControlRule_PinsTheRolePropertyExactly()
        {
            var model = new DisplayTriggersEditModel(null, Device3);

            var draft = model.NewMappedControlDraft("Up Shift");
            var cfg = model.AddRule(draft);

            var rule = Assert.Single(cfg.Itm.Rules);
            // The hardware-verified mapping: InputStatus.ControlMapperPlugin.<role>.
            Assert.Equal("InputStatus.ControlMapperPlugin.Up Shift", rule.When.Source.Name);
            Assert.Equal(PropertyKind.SimHubProperty, rule.When.Source.Kind);
            Assert.Equal(ConditionKind.IsTrue, rule.When.Kind);
            Assert.Equal(HoldKind.WhileActive, rule.Hold.Kind);
            Assert.Equal(RuleEligibility.Any, rule.Eligible);
            Assert.Null(rule.When.Value);                  // isTrue takes no comparison value
        }

        [Fact]
        public void MappedControlPropertyName_IsExact()
            => Assert.Equal("InputStatus.ControlMapperPlugin.Up Shift",
                DisplayTriggersEditModel.MappedControlPropertyName("Up Shift"));

        [Fact]
        public void AddRule_AppendsAtLowestPriority_KeepingExistingRules()
        {
            var model = new DisplayTriggersEditModel(OneNormalRule(), Device3);
            var draft = model.NewMappedControlDraft("Headlights");

            var cfg = model.AddRule(draft);

            Assert.Equal(2, cfg.Itm.Rules.Count);
            Assert.Equal("r1", cfg.Itm.Rules[0].Id);       // existing stays on top
            Assert.Equal("InputStatus.ControlMapperPlugin.Headlights",
                cfg.Itm.Rules[1].When.Source.Name);
        }

        // ── Update / Move / Remove / Enable — ids stable, fresh documents ─

        [Fact]
        public void UpdateRule_RebuildsInPlace_KeepsIdAndPosition()
        {
            var start = OneNormalRule();
            var model = new DisplayTriggersEditModel(start, Device3);

            var draft = DisplayTriggersEditModel.ToDraft(start.Itm.Rules[0]);
            draft.Operator = ConditionKind.LessThan;
            draft.Value = 5;
            draft.Page = ItmPage.TyreTemps;
            var cfg = model.UpdateRule(draft);

            var rule = Assert.Single(cfg.Itm.Rules);
            Assert.Equal("r1", rule.Id);                   // id stable
            Assert.Equal(ConditionKind.LessThan, rule.When.Kind);
            Assert.Equal(5, rule.When.Value);
            Assert.Equal(ItmPage.TyreTemps, rule.Show.Page);
            Assert.NotSame(start, cfg);                    // fresh document
            Assert.NotSame(start.Itm.Rules[0], rule);      // fresh rule (original untouched)
            Assert.Equal(ConditionKind.GreaterThan, start.Itm.Rules[0].When.Kind);
        }

        [Fact]
        public void MoveRule_ReordersPriority_IdsStable()
        {
            var cfg0 = OneNormalRule();
            var model = new DisplayTriggersEditModel(cfg0, Device3);
            model.AddRule(model.NewMappedControlDraft("A"));
            var cfg = model.AddRule(model.NewMappedControlDraft("B"));
            var ids = cfg.Itm.Rules.Select(r => r.Id).ToArray();
            Assert.Equal("r1", ids[0]);

            // Move the last rule two places up (to the top).
            var moved = model.MoveRule(ids[2], -2);

            Assert.Equal(new[] { ids[2], ids[0], ids[1] },
                moved.Itm.Rules.Select(r => r.Id).ToArray());
        }

        [Fact]
        public void MoveRule_ClampsAtEnds_AndUnknownIdIsNoOp()
        {
            var model = new DisplayTriggersEditModel(OneNormalRule(), Device3);
            model.AddRule(model.NewMappedControlDraft("A"));

            var top = model.MoveRule("r1", -5);            // already first → clamps, no move
            Assert.Equal("r1", top.Itm.Rules[0].Id);

            var same = model.MoveRule("does-not-exist", 1);
            Assert.Same(model.Config, same);
        }

        [Fact]
        public void RemoveRule_DropsById()
        {
            var model = new DisplayTriggersEditModel(OneNormalRule(), Device3);
            var cfg2 = model.AddRule(model.NewMappedControlDraft("A"));
            Assert.Equal(2, cfg2.Itm.Rules.Count);

            var cfg = model.RemoveRule("r1");

            var rule = Assert.Single(cfg.Itm.Rules);
            Assert.Equal("InputStatus.ControlMapperPlugin.A", rule.When.Source.Name);
        }

        [Fact]
        public void SetRuleEnabled_FlipsToggle_KeepsIdAndEverySerializedField()
        {
            var start = OneNormalRule();
            var model = new DisplayTriggersEditModel(start, Device3);

            var cfg = model.SetRuleEnabled("r1", false);

            var rule = Assert.Single(cfg.Itm.Rules);
            Assert.Equal("r1", rule.Id);
            Assert.False(rule.Enabled);
            // Fresh instance, original untouched.
            Assert.NotSame(start.Itm.Rules[0], rule);
            Assert.True(start.Itm.Rules[0].Enabled);
            // Every serialized field survived the clone.
            Assert.Equal(ConditionKind.GreaterThan, rule.When.Kind);
            Assert.Equal("Fuel", rule.When.Source.Name);
            Assert.Equal(10, rule.When.Value);
            Assert.Equal(ItmPage.FuelErsDrs, rule.Show.Page);
            Assert.Equal(HoldKind.ForDuration, rule.Hold.Kind);
            Assert.Equal(5000, rule.Hold.DurationMs);
        }

        // ── Degraded rules pass through verbatim ──────────────────────────

        private static DisplayCustomizationConfig WithDegradedRule()
            => Load("{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                // A rule using a condition kind (and eligibility) only a FUTURE build knows.
                + "{ \"id\": \"future\", \"when\": { \"kind\": \"orbitsMars\", "
                + "\"source\": { \"kind\": \"warpDrive\", \"name\": \"X\" } }, "
                + "\"show\": { \"kind\": \"hologram\", \"page\": \"deckTen\" }, "
                + "\"eligible\": \"whenTheStarsAlign\" }, "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"greaterThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" } } ] } }");

        [Fact]
        public void DegradedRule_SurvivesMutations_ByteForByte()
        {
            var start = WithDegradedRule();
            var degraded = start.Itm.Rules[0];
            Assert.True(degraded.DegradedAtLoad);
            var model = new DisplayTriggersEditModel(start, Device3);

            // Do an unrelated add, then reorder — the degraded rule must be untouched.
            model.AddRule(model.NewMappedControlDraft("A"));
            var cfg = model.MoveRule("r1", -1);

            var carried = cfg.Itm.Rules.Single(r => r.Id == "future");
            Assert.Same(degraded, carried);                 // same instance, never rebuilt
            Assert.True(carried.DegradedAtLoad);
            // The future-version text is preserved verbatim (EnumText round-trip).
            Assert.Equal("orbitsMars", carried.When.KindRaw);
            Assert.Equal("warpDrive", carried.When.Source.KindRaw);
            Assert.Equal("hologram", carried.Show.KindRaw);
            Assert.Equal("deckTen", carried.Show.PageRaw);
            Assert.Equal("whenTheStarsAlign", carried.EligibleRaw);

            // And it round-trips through the serializer unchanged.
            string json = DisplayConfigSerializer.Save(cfg);
            Assert.Contains("orbitsMars", json);
            Assert.Contains("whenTheStarsAlign", json);
            Assert.Contains("deckTen", json);
        }

        // ── Legacy-screen target survives an unrelated edit ───────────────

        private static DisplayCustomizationConfig LegacyScreenTargetedRule()
            => Load("{ \"schemaVersion\": 1, "
                + "\"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"isTrue\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"DrsEnabled\" } }, "
                + "\"show\": { \"kind\": \"legacyScreen\", \"screenId\": \"fn1\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } } ] }, "
                + "\"legacy\": { \"screens\": [ "
                + "{ \"id\": \"fn1\", \"name\": \"FN1\", \"text\": \"FN1\" } ] } }");

        [Fact]
        public void ToDraft_CarriesTheScreenId_OfALegacyScreenTarget()
        {
            var cfg = LegacyScreenTargetedRule();
            var rule = cfg.Itm.Rules[0];
            Assert.False(rule.DegradedAtLoad);             // a valid, editable rule

            var draft = DisplayTriggersEditModel.ToDraft(rule);

            Assert.Equal(TargetKind.LegacyScreen, draft.TargetKind);
            Assert.Equal("fn1", draft.ScreenId);
        }

        [Fact]
        public void UpdateRule_LegacyScreenTarget_KeepsScreenId_WhenAnotherFieldChanges()
        {
            var cfg0 = LegacyScreenTargetedRule();
            var model = new DisplayTriggersEditModel(cfg0, Device3);

            // Edit an unrelated field (eligibility) — the SHOW target must be untouched.
            var draft = DisplayTriggersEditModel.ToDraft(cfg0.Itm.Rules[0]);
            draft.Eligibility = RuleEligibility.Any;
            var cfg = model.UpdateRule(draft);

            var rule = Assert.Single(cfg.Itm.Rules);
            Assert.Equal(TargetKind.LegacyScreen, rule.Show.Kind);
            Assert.Equal("fn1", rule.Show.ScreenId);       // NOT dropped to null
            Assert.Equal(RuleEligibility.Any, rule.Eligible);
        }

        // ── Commit gating (no edit degrades a user's own valid rule) ──────

        [Fact]
        public void IsCommittable_RequiresAFiniteValueForComparisons_NotForBoolOrEdgeKinds()
        {
            var model = new DisplayTriggersEditModel(OneNormalRule(), Device3);
            var draft = DisplayTriggersEditModel.ToDraft(model.Rules[0]);   // greaterThan 10, source Fuel
            Assert.True(DisplayTriggersEditModel.IsCommittable(draft));

            // A momentarily-empty VALUE box on a comparison operator would degrade the rule.
            draft.Value = null;
            Assert.False(DisplayTriggersEditModel.IsCommittable(draft));

            // Bool and edge kinds need no value, so the same empty draft is committable.
            draft.Operator = ConditionKind.IsTrue;
            Assert.True(DisplayTriggersEditModel.IsCommittable(draft));
            draft.Operator = ConditionKind.Changes;
            Assert.True(DisplayTriggersEditModel.IsCommittable(draft));

            // A non-finite value would also degrade — reject it.
            draft.Operator = ConditionKind.GreaterThan;
            draft.Value = double.NaN;
            Assert.False(DisplayTriggersEditModel.IsCommittable(draft));
            draft.Value = 6000;
            Assert.True(DisplayTriggersEditModel.IsCommittable(draft));

            // No source is never committable.
            draft.SourceName = null;
            Assert.False(DisplayTriggersEditModel.IsCommittable(draft));
            Assert.False(DisplayTriggersEditModel.IsCommittable(null));
        }

        // ── UI option ↔ schema enum mappings ──────────────────────────────

        [Fact]
        public void Operators_AreTheElevenInMockOrder_WithLabels()
        {
            Assert.Equal(new[]
            {
                ConditionKind.LessThan, ConditionKind.LessOrEqual,
                ConditionKind.GreaterThan, ConditionKind.GreaterOrEqual,
                ConditionKind.Equals, ConditionKind.NotEquals,
                ConditionKind.IsTrue, ConditionKind.IsFalse,
                ConditionKind.Changes, ConditionKind.Increases, ConditionKind.Decreases,
            }, DisplayTriggersEditModel.Operators.ToArray());

            Assert.Equal("less than", DisplayTriggersEditModel.OperatorLabel(ConditionKind.LessThan));
            Assert.Equal("greater or equal", DisplayTriggersEditModel.OperatorLabel(ConditionKind.GreaterOrEqual));
            Assert.Equal("is true", DisplayTriggersEditModel.OperatorLabel(ConditionKind.IsTrue));
            Assert.Equal("changes", DisplayTriggersEditModel.OperatorLabel(ConditionKind.Changes));
            Assert.Equal("decreases", DisplayTriggersEditModel.OperatorLabel(ConditionKind.Decreases));
        }

        [Fact]
        public void OperatorOptionsFor_PrependsAValidUnlistedKind_SoAnEventRuleIsNotMislabeled()
        {
            // A loaded ActionTriggered rule is valid and editable but not a dropdown option;
            // its operator must be shown honestly, not fall back to the first item ("less than").
            var opts = DisplayTriggersEditModel.OperatorOptionsFor(ConditionKind.ActionTriggered);
            Assert.Equal(ConditionKind.ActionTriggered, opts[0]);
            Assert.Equal(DisplayTriggersEditModel.Operators.Count + 1, opts.Count);
            Assert.Equal("action triggered",
                DisplayTriggersEditModel.OperatorLabel(ConditionKind.ActionTriggered));

            // A standard kind gets the plain eleven, unchanged.
            var standard = DisplayTriggersEditModel.OperatorOptionsFor(ConditionKind.GreaterThan);
            Assert.Equal(DisplayTriggersEditModel.Operators.Count, standard.Count);
            Assert.DoesNotContain(ConditionKind.ActionTriggered, standard);
        }

        [Fact]
        public void HoldAndEligibility_MapToSchemaEnums()
        {
            Assert.Equal(new[] { HoldKind.WhileActive, HoldKind.ForDuration, HoldKind.Indefinite },
                DisplayTriggersEditModel.Holds.ToArray());
            Assert.Equal("While active", DisplayTriggersEditModel.HoldLabel(HoldKind.WhileActive));
            Assert.Equal("For duration", DisplayTriggersEditModel.HoldLabel(HoldKind.ForDuration));
            Assert.Equal("Indefinite", DisplayTriggersEditModel.HoldLabel(HoldKind.Indefinite));

            Assert.Equal(new[] { RuleEligibility.InGame, RuleEligibility.Idle, RuleEligibility.Any },
                DisplayTriggersEditModel.Eligibilities.ToArray());
            Assert.Equal("In-game", DisplayTriggersEditModel.EligibilityLabel(RuleEligibility.InGame));
            Assert.Equal("Idle", DisplayTriggersEditModel.EligibilityLabel(RuleEligibility.Idle));
            Assert.Equal("Any time", DisplayTriggersEditModel.EligibilityLabel(RuleEligibility.Any));
        }

        [Fact]
        public void BuildRule_LevelKindKeepsHysteresis_EdgeKindDropsValueAndHysteresis()
        {
            var model = new DisplayTriggersEditModel(null, Device3);

            var level = model.NewTelemetryDraft();
            level.Operator = ConditionKind.LessThan;
            level.SourceName = "P"; level.Value = 3; level.Hysteresis = 0.5;
            var levelRule = model.AddRule(level).Itm.Rules.Last();
            Assert.Equal(3, levelRule.When.Value);
            Assert.Equal(0.5, levelRule.When.Hysteresis);

            var edge = model.NewTelemetryDraft();
            edge.Operator = ConditionKind.Changes;
            edge.SourceName = "P"; edge.Value = 9; edge.Hysteresis = 0.5;   // both irrelevant
            var edgeRule = model.AddRule(edge).Itm.Rules.Last();
            Assert.Null(edgeRule.When.Value);
            Assert.Null(edgeRule.When.Hysteresis);
            // An edge kind with no explicit hold defaults to ForDuration (validator parity).
            Assert.Equal(HoldKind.ForDuration, edgeRule.Hold.Kind);
        }

        [Fact]
        public void PageOptions_ExcludesTheLegacyPage()
        {
            var model = new DisplayTriggersEditModel(null, Device3);
            var pages = model.PageOptions();
            Assert.DoesNotContain(ItmPage.Legacy, pages);
            Assert.Contains(ItmPage.CarSettings, pages);   // device 3 has it
        }

        // ── Row model ─────────────────────────────────────────────────────

        private static DisplayRuleSnapshot Snapshot(params DisplayRuleRow[] itmRules)
            => new DisplayRuleSnapshot("Fuel / ERS / DRS", null,
                itmRules, new DisplayRuleRow[0], new DisplayActivityEvent[0],
                activityVersion: 0, composedAtMs: 0, composedAtUtc: default);

        [Fact]
        public void Rows_MergeLiveStateById_ThenTheBaseRowLast()
        {
            var model = new DisplayTriggersEditModel(OneNormalRule(), Device3);
            model.AddRule(model.NewMappedControlDraft("A"));
            var ids = model.Rules.Select(r => r.Id).ToArray();

            var snapshot = Snapshot(
                new DisplayRuleRow(ids[0], "ignored label", RuleStatus.OnScreen, 3200),
                new DisplayRuleRow(ids[1], "ignored label", RuleStatus.Waiting, null));

            var rows = model.Rows(snapshot, defaultWirePage: 1);

            Assert.Equal(3, rows.Count);                    // 2 rules + base
            // Row 0: live "on screen" merged by id; label from the config rule, not the snapshot.
            Assert.Equal("1", rows[0].Rank);
            Assert.Equal("Fuel > 10 → Fuel / ERS / DRS", rows[0].Label);
            Assert.Equal("on screen", rows[0].Chip);
            Assert.True(rows[0].OnScreen);
            Assert.Equal("4s", rows[0].Seconds);           // ceiling, shared with the Overview
            Assert.True(rows[0].Draggable);
            Assert.True(rows[0].Expandable);
            Assert.Equal("In-game", rows[0].Eligibility);

            Assert.Equal("waiting", rows[1].Chip);

            // Base row pinned last: device 3 default wire 1 = Lap Info.
            var baseRow = rows[2];
            Assert.True(baseRow.IsBase);
            Assert.Equal("★", baseRow.Rank);
            Assert.Equal("Always → Lap Info", baseRow.Label);
            Assert.Equal("base", baseRow.Chip);
            Assert.False(baseRow.Draggable);
            Assert.False(baseRow.Expandable);
        }

        [Fact]
        public void Rows_DegradedRule_IsMutedNonExpandable_ButStillDraggable()
        {
            var model = new DisplayTriggersEditModel(WithDegradedRule(), Device3);

            var rows = model.Rows(null, defaultWirePage: 1);

            var degradedRow = rows[0];
            Assert.Equal("future", degradedRow.RuleId);
            Assert.True(degradedRow.Degraded);
            Assert.True(degradedRow.Muted);
            Assert.False(degradedRow.Expandable);          // not editable
            Assert.True(degradedRow.Draggable);            // but reorderable
            Assert.Equal("", degradedRow.Eligibility);     // no eligibility chip
        }

        [Fact]
        public void Rows_NoSnapshot_JustLabelsAndTheBaseRow()
        {
            var model = new DisplayTriggersEditModel(OneNormalRule(), Device3);

            var rows = model.Rows(null, defaultWirePage: 2);   // wire 2 = Fuel / ERS / DRS

            Assert.Equal(2, rows.Count);
            Assert.Equal("", rows[0].Chip);                // no live state merged
            Assert.Equal("Always → Fuel / ERS / DRS", rows[1].Label);
        }
    }
}
