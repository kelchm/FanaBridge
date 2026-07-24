using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Adapters;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using FanaBridge.UI.Display;
using FanaBridge.UI.Display.Shared;
using Xunit;

namespace FanaBridge.Tests.Display
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
            draft.Hold = HoldKind.UntilDismissed;

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
            Assert.Equal(HoldKind.UntilDismissed, rule.Hold.Kind);
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
            Assert.Equal(RuleEligibility.Always, rule.Eligible);
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
                + "\"runs\": \"whenTheStarsAlign\" }, "
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
                + "\"show\": { \"kind\": \"segmentScreen\", \"screenId\": \"fn1\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } } ] }, "
                + "\"segmentDisplay\": { \"screens\": [ "
                + "{ \"id\": \"fn1\", \"name\": \"FN1\", \"text\": \"FN1\" } ] } }");

        [Fact]
        public void ToDraft_CarriesTheScreenId_OfALegacyScreenTarget()
        {
            var cfg = LegacyScreenTargetedRule();
            var rule = cfg.Itm.Rules[0];
            Assert.False(rule.DegradedAtLoad);             // a valid, editable rule

            var draft = DisplayTriggersEditModel.ToDraft(rule);

            Assert.Equal(TargetKind.SegmentScreen, draft.TargetKind);
            Assert.Equal("fn1", draft.ScreenId);
        }

        [Fact]
        public void UpdateRule_LegacyScreenTarget_KeepsScreenId_WhenAnotherFieldChanges()
        {
            var cfg0 = LegacyScreenTargetedRule();
            var model = new DisplayTriggersEditModel(cfg0, Device3);

            // Edit an unrelated field (eligibility) — the SHOW target must be untouched.
            var draft = DisplayTriggersEditModel.ToDraft(cfg0.Itm.Rules[0]);
            draft.Eligibility = RuleEligibility.Always;
            var cfg = model.UpdateRule(draft);

            var rule = Assert.Single(cfg.Itm.Rules);
            Assert.Equal(TargetKind.SegmentScreen, rule.Show.Kind);
            Assert.Equal("fn1", rule.Show.ScreenId);       // NOT dropped to null
            Assert.Equal(RuleEligibility.Always, rule.Eligible);
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
            Assert.Equal(new[] { HoldKind.WhileActive, HoldKind.ForDuration, HoldKind.UntilDismissed },
                DisplayTriggersEditModel.Holds.ToArray());
            Assert.Equal("While active", DisplayTriggersEditModel.HoldLabel(HoldKind.WhileActive));
            Assert.Equal("For duration", DisplayTriggersEditModel.HoldLabel(HoldKind.ForDuration));
            Assert.Equal("Until dismissed", DisplayTriggersEditModel.HoldLabel(HoldKind.UntilDismissed));

            Assert.Equal(new[] { RuleEligibility.InGame, RuleEligibility.Idle, RuleEligibility.Always },
                DisplayTriggersEditModel.Eligibilities.ToArray());
            Assert.Equal("In-game", DisplayTriggersEditModel.EligibilityLabel(RuleEligibility.InGame));
            Assert.Equal("Idle", DisplayTriggersEditModel.EligibilityLabel(RuleEligibility.Idle));
            Assert.Equal("Any time", DisplayTriggersEditModel.EligibilityLabel(RuleEligibility.Always));
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
        public void PageOptions_IncludesTheFullDeviceSet_LegacyPageToo()
        {
            // Page 6 is a real page: a rule may target it (its CONTENT is still authored
            // as virtual pages on the legacy rule set).
            var model = new DisplayTriggersEditModel(null, Device3);
            var pages = model.PageOptions();
            Assert.Contains(ItmPage.Legacy, pages);
            Assert.Contains(ItmPage.CarSettings, pages);   // device 3 has it
            Assert.Equal(ItmPage.Legacy, pages[pages.Count - 1]);   // catalog order: last
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
            // Structured v9 WHEN: a built-in shows a bare leaf; operator/value/target split out.
            Assert.Equal("Fuel", rows[0].PropertyName);
            Assert.Equal(PropertyDisplayKind.BuiltIn, rows[0].DisplayKind);
            Assert.Equal(">", rows[0].Operator);
            Assert.Equal("10", rows[0].ValueText);
            Assert.Equal("Fuel / ERS / DRS", rows[0].TargetText);

            // Row 1: mapped control — SimHub property, boolean operator (no value).
            Assert.Equal("InputStatus.ControlMapperPlugin.A", rows[1].PropertyName);
            Assert.Equal(PropertyDisplayKind.SimHubProperty, rows[1].DisplayKind);
            Assert.Equal("is on", rows[1].Operator);
            Assert.Equal("", rows[1].ValueText);
            Assert.Equal("waiting", rows[1].Chip);

            // Base row pinned last: device 3 default wire 1 = Lap Info.
            var baseRow = rows[2];
            Assert.True(baseRow.IsBase);
            Assert.Equal("★", baseRow.Rank);
            Assert.Equal("Always → Lap Info", baseRow.Label);
            Assert.Equal("base", baseRow.Chip);
            Assert.False(baseRow.Draggable);
            Assert.False(baseRow.Expandable);
            Assert.Null(baseRow.PropertyName);              // base row renders its Label
        }

        [Fact]
        public void OperatorChoices_AreTheOptionsForTheDraft_WithSelection_AndLabels()
        {
            var model = new DisplayTriggersEditModel(OneNormalRule(), Device3);
            var draft = DisplayTriggersEditModel.ToDraft(model.Rules[0]);   // greaterThan

            var choices = DisplayTriggersEditModel.OperatorChoices(draft);
            Assert.Equal("GreaterThan", choices.SelectedId);
            Assert.Equal("greater than", choices.Selected.Label);
            Assert.Equal(DisplayTriggersEditModel.Operators.Count, choices.Items.Count);
            Assert.Equal("less than", choices.Items[0].Label);
            Assert.Equal("LessThan", choices.Items[0].Id);

            // A loaded unlisted-but-valid kind is prepended and selected (not mislabeled).
            draft.Operator = ConditionKind.ActionTriggered;
            var withEvent = DisplayTriggersEditModel.OperatorChoices(draft);
            Assert.Equal("ActionTriggered", withEvent.SelectedId);
            Assert.Equal("action triggered", withEvent.Selected.Label);
            Assert.Equal(DisplayTriggersEditModel.Operators.Count + 1, withEvent.Items.Count);
            Assert.Equal("ActionTriggered", withEvent.Items[0].Id);
        }

        [Fact]
        public void Rows_DegradedRule_IsMutedNonExpandable_ButStillDraggable()
        {
            var model = new DisplayTriggersEditModel(WithDegradedRule(), Device3);

            // Degraded rows live in the Workbench editor stack; the Monitor "what's in play"
            // list drops them (they can never compete), so their reorder/edit affordances are
            // a Workbench projection.
            var rows = model.Rows(null, defaultWirePage: 1, TriggerTableMode.Workbench);

            var degradedRow = rows[0];
            Assert.Equal("future", degradedRow.RuleId);
            Assert.True(degradedRow.Degraded);
            Assert.True(degradedRow.Muted);
            Assert.False(degradedRow.Expandable);          // not editable
            Assert.True(degradedRow.Draggable);            // but reorderable
            Assert.Equal("", degradedRow.Eligibility);     // no eligibility chip
            Assert.Null(degradedRow.PropertyName);         // no structured grammar — uses Label

            // The dense-grid columns stay at their empty defaults: a degraded rule skips
            // ApplyWorkbenchColumns (spec 2b §1 — don't fabricate presentations the editor
            // can't honor). Route a degraded row through ApplyWorkbenchColumns and these would
            // read "While active" / "In game" / "waiting" for an unhonorable rule — pinned here.
            Assert.Equal("", degradedRow.Timeout);
            Assert.Equal("", degradedRow.RunGlyph);
            Assert.Equal("", degradedRow.RunLabel);
            Assert.Equal("", degradedRow.StateText);
        }

        [Fact]
        public void Rows_Monitor_DropsDegradedRule_EvenWhenEnabledWithNoLiveState()
        {
            // Monitor is "what's in play": a rule degraded at load can never fire, so it must
            // not leak into the Overview list. The degraded "future" rule is Enabled (default)
            // and, with a null snapshot, has no live status (Armed) — so ONLY the
            // `rule.DegradedAtLoad ||` drop-arm keeps it out. Remove that clause and "future"
            // survives as rank 1, failing this test.
            var model = new DisplayTriggersEditModel(WithDegradedRule(), Device3);

            var rows = model.Rows(null, defaultWirePage: 1, TriggerTableMode.Monitor);

            Assert.DoesNotContain(rows, r => r.RuleId == "future");   // degraded → dropped
            Assert.Equal(2, rows.Count);                              // only r1 + the base row
            Assert.Equal("r1", rows[0].RuleId);
            Assert.Equal("1", rows[0].Rank);                          // renumbered contiguously
            Assert.True(rows[1].IsBase);
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

        [Fact]
        public void Rows_UserNamedRule_KeepsItsLabel_NoStructuredGrammar()
        {
            // Deliberate deviation #1: a user-named rule (e.g. imported with a friendly name)
            // must keep its Label, not have the name replaced by the "prop op value" grammar.
            // Guarded by the !IsNullOrWhiteSpace(rule.Name) clause in ApplyStructuredWhen;
            // remove that clause and PropertyName populates — this test fails.
            var cfg = Load("{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"name\": \"My Fuel Rule\", \"when\": { \"kind\": \"greaterThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" } } ] } }");
            var model = new DisplayTriggersEditModel(cfg, Device3);

            var row = model.Rows(null, defaultWirePage: 1)[0];

            Assert.False(row.Degraded);                 // a valid rule, just named
            Assert.Null(row.PropertyName);              // no structured grammar
            Assert.Equal("My Fuel Rule", row.Label);    // its name survives
        }

        [Fact]
        public void Rows_ActionTriggeredRule_FallsBackToQuotedLabel_NoStructuredGrammar()
        {
            // ActionTriggered's label carries a distinct quoted framing ("'ShowTyres' triggered")
            // the property/operator grammar would drop and re-namespace; such (imported-only)
            // rules must fall back to Label. Without the ActionTriggered guard in
            // ApplyStructuredWhen the structured path populates PropertyName — this test fails.
            var cfg = Load("{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"actionTriggered\", "
                + "\"source\": { \"kind\": \"simHubProperty\", \"name\": \"ShowTyres\" } }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" } } ] } }");
            var model = new DisplayTriggersEditModel(cfg, Device3);

            var row = model.Rows(null, defaultWirePage: 1)[0];

            Assert.False(row.Degraded);                 // event condition loads valid
            Assert.Null(row.PropertyName);              // fell back to Label, no grammar
            Assert.Contains("'ShowTyres' triggered", row.Label);
        }

        // ── v9 Workbench: columns, no base row in the stack ───────────────

        private static DisplayCustomizationConfig TwoRulesWithEligibility()
            => Load("{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"greaterThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" }, "
                + "\"hold\": { \"kind\": \"forDuration\", \"durationMs\": 5000 }, \"runs\": \"idle\" }, "
                + "{ \"id\": \"r2\", \"enabled\": false, \"when\": { \"kind\": \"isTrue\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"DrsEnabled\" } }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"tyreTemps\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" }, \"runs\": \"always\" } ] } }");

        [Fact]
        public void Rows_Workbench_DropsTheBaseRow_AndFillsTheDenseColumns()
        {
            var model = new DisplayTriggersEditModel(TwoRulesWithEligibility(), Device3);

            var rows = model.Rows(null, defaultWirePage: 1, TriggerTableMode.Workbench);

            Assert.Equal(2, rows.Count);                       // no base row in the workbench stack
            Assert.DoesNotContain(rows, r => r.IsBase);

            // r1: idle, enabled, ForDuration 5 s, page target → "Page 2 · Fuel / ERS / DRS".
            Assert.Equal("Page 2 · Fuel / ERS / DRS", rows[0].ShowText);
            Assert.Equal("5 s", rows[0].Timeout);
            Assert.Equal("☾", rows[0].RunGlyph);
            Assert.Equal("Idle", rows[0].RunLabel);
            Assert.Equal("", rows[0].StateText);               // armed (no snapshot)

            // r2: disabled → Runs "⊘ Disabled", State "off", dimmed; WhileActive timeout.
            Assert.Equal("While active", rows[1].Timeout);
            Assert.Equal("⊘", rows[1].RunGlyph);
            Assert.Equal("Disabled", rows[1].RunLabel);
            Assert.Equal("off", rows[1].StateText);
            Assert.True(rows[1].Muted);                        // disabled dims even with no snapshot
            Assert.False(rows[0].Muted);
        }

        [Fact]
        public void Rows_Monitor_StillEmitsTheBaseRowLast()
        {
            // The default (Monitor) projection is unchanged — the base row is pinned last.
            var model = new DisplayTriggersEditModel(OneNormalRule(), Device3);
            var rows = model.Rows(null, defaultWirePage: 1, TriggerTableMode.Monitor);
            Assert.True(rows[rows.Count - 1].IsBase);
        }

        [Fact]
        public void Rows_Monitor_DropsDisabled_RenumbersAndCarriesTheNowValue()
        {
            // r1 idle+enabled (live on screen), r2 disabled → r2 drops from the Monitor list,
            // r1 renumbers to rank 1, and its live "Now" value + the base ShowText carry through.
            var model = new DisplayTriggersEditModel(TwoRulesWithEligibility(), Device3);
            var ids = model.Rules.Select(r => r.Id).ToArray();
            var snapshot = Snapshot(
                new DisplayRuleRow(ids[0], "x", RuleStatus.OnScreen, null, "42"),
                new DisplayRuleRow(ids[1], "x", RuleStatus.Disabled, null, "off"));

            var rows = model.Rows(snapshot, defaultWirePage: 1, TriggerTableMode.Monitor);

            Assert.Equal(2, rows.Count);                    // r1 + base (r2 disabled dropped)
            Assert.Equal("1", rows[0].Rank);
            Assert.Equal(ids[0], rows[0].RuleId);
            Assert.Equal("42", rows[0].NowText);
            Assert.True(rows[0].OnScreen);
            Assert.True(rows[1].IsBase);
            Assert.Equal("Lap Info", rows[1].ShowText);     // device 3 default wire 1
        }

        [Fact]
        public void Rows_Workbench_CycleTarget_ShowsShortPair()
        {
            var cfg = Load("{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"greaterThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"cycle\", \"pages\": [ \"fuelErsDrs\", \"tyreTemps\" ], "
                + "\"periodMs\": 3000 }, \"hold\": { \"kind\": \"whileActive\" } } ] } }");
            var model = new DisplayTriggersEditModel(cfg, Device3);

            var row = model.Rows(null, defaultWirePage: 1, TriggerTableMode.Workbench)[0];
            // Device 3: Fuel/ERS/DRS wire 2, Tyre Temps wire 5.
            Assert.Equal("P2 ⇄ P5", row.ShowText);
        }

        // ── Cycle target (S4: Alternate purged — degrades-preserved; Cycle is the write path)

        private static DisplayCustomizationConfig TwoCycleRules()
            => Load("{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"greaterThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"cycle\", \"pages\": [ \"fuelErsDrs\", \"tyreTemps\" ], "
                + "\"periodMs\": 4000 }, \"hold\": { \"kind\": \"whileActive\" } }, "
                + "{ \"id\": \"r2\", \"when\": { \"kind\": \"lessThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 5 }, "
                + "\"show\": { \"kind\": \"cycle\", \"pages\": [ \"lapInfo\", \"lapTimes\" ], "
                + "\"periodMs\": 2500 }, \"hold\": { \"kind\": \"whileActive\" } } ] } }");

        [Fact]
        public void ToDraft_CycleRule_CarriesPagesAndPeriod()
        {
            var rule = TwoCycleRules().Itm.Rules[0];
            Assert.Equal(TargetKind.Cycle, rule.Show.Kind);

            var draft = DisplayTriggersEditModel.ToDraft(rule);

            Assert.Equal(TargetKind.Cycle, draft.TargetKind);
            Assert.Equal(new[] { ItmPage.FuelErsDrs, ItmPage.TyreTemps }, draft.CyclePages.ToArray());
            Assert.Equal(4000, draft.CyclePeriodMs);
        }

        [Fact]
        public void ToDraft_AlternateRule_Degrades_DoesNotMapToCycle()
        {
            // S4 (spec-v9-s4-rename-freeze): Alternate→Cycle mapping deleted with the purge.
            // Old alternate documents load as Unknown and stay degraded (not drafted as Cycle).
            var cfg = Load("{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"greaterThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"alternate\", \"pageA\": \"fuelErsDrs\", \"pageB\": \"tyreTemps\", "
                + "\"periodMs\": 4000 }, \"hold\": { \"kind\": \"whileActive\" } } ] } }");
            var rule = Assert.Single(cfg.Itm.Rules);
            Assert.True(rule.DegradedAtLoad);
            Assert.Equal(TargetKind.Unknown, rule.Show.Kind);
            Assert.Equal("alternate", rule.Show.KindRaw);
        }

        [Fact]
        public void BuildRule_Cycle_WritesKindPagesAndPeriod_CamelCase()
        {
            var model = new DisplayTriggersEditModel(null, Device3);
            var draft = model.NewTelemetryDraft();
            draft.SourceKind = PropertyKind.BuiltIn;
            draft.SourceName = BuiltInProperties.Fuel;
            draft.Operator = ConditionKind.GreaterThan;
            draft.Value = 10;
            draft.TargetKind = TargetKind.Cycle;
            draft.CyclePages = new List<ItmPage>
            {
                ItmPage.FuelErsDrs, ItmPage.TyreTemps, ItmPage.CarSettings,
            };
            draft.CyclePeriodMs = 2500;

            var cfg = model.AddRule(draft);

            var show = Assert.Single(cfg.Itm.Rules).Show;
            Assert.Equal(TargetKind.Cycle, show.Kind);
            Assert.Equal("cycle", show.KindRaw);
            Assert.Equal(new[] { "fuelErsDrs", "tyreTemps", "carSettings" }, show.PagesRaw);
            Assert.Equal(2500, show.PeriodMs);
        }

        [Fact]
        public void UpdateRule_EditedCycle_ResavesAsCycle_UntouchedSiblingSurvivesByReference()
        {
            var start = TwoCycleRules();
            string r2JsonBefore = DisplayConfigSerializer.Save(
                new DisplayCustomizationConfig
                {
                    SchemaVersion = 1,
                    Itm = new ItmRuleSet { Rules = new List<DisplayRule> { start.Itm.Rules[1] } },
                });
            var model = new DisplayTriggersEditModel(start, Device3);
            var untouched = start.Itm.Rules[1];

            var draft = DisplayTriggersEditModel.ToDraft(start.Itm.Rules[0]);
            Assert.Equal(TargetKind.Cycle, draft.TargetKind);
            draft.CyclePages.Add(ItmPage.CarSettings);     // user edit: grow the cycle
            draft.CyclePeriodMs = 5000;
            var cfg = model.UpdateRule(draft);

            var edited = cfg.Itm.Rules[0];
            Assert.Equal(TargetKind.Cycle, edited.Show.Kind);
            Assert.Equal("cycle", edited.Show.KindRaw);
            Assert.Equal(new[] { "fuelErsDrs", "tyreTemps", "carSettings" }, edited.Show.PagesRaw);
            Assert.Equal(5000, edited.Show.PeriodMs);

            // Untouched sibling: same instance (edit-path guarantee) and still Cycle text.
            Assert.Same(untouched, cfg.Itm.Rules[1]);
            Assert.Equal(TargetKind.Cycle, cfg.Itm.Rules[1].Show.Kind);
            Assert.Equal("cycle", cfg.Itm.Rules[1].Show.KindRaw);
            string r2JsonAfter = DisplayConfigSerializer.Save(
                new DisplayCustomizationConfig
                {
                    SchemaVersion = 1,
                    Itm = new ItmRuleSet { Rules = new List<DisplayRule> { cfg.Itm.Rules[1] } },
                });
            Assert.Equal(r2JsonBefore, r2JsonAfter);
        }

        [Fact]
        public void CloneRuleWithRun_PreservesPagesRaw_OnDuplicateAndDisable()
        {
            var start = Load("{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"greaterThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"cycle\", \"pages\": [ \"fuelErsDrs\", \"tyreTemps\", \"carSettings\" ], "
                + "\"periodMs\": 3000 }, \"hold\": { \"kind\": \"whileActive\" } } ] } }");
            var model = new DisplayTriggersEditModel(start, Device3);
            string[] expected = { "fuelErsDrs", "tyreTemps", "carSettings" };

            var duplicated = model.DuplicateRule("r1", out _);
            Assert.Equal(expected, duplicated.Itm.Rules[0].Show.PagesRaw);
            Assert.Equal(expected, duplicated.Itm.Rules[1].Show.PagesRaw);
            // Fresh list — mutating the copy must not reach the source.
            duplicated.Itm.Rules[1].Show.PagesRaw.Add("lapInfo");
            Assert.Equal(expected, duplicated.Itm.Rules[0].Show.PagesRaw);

            var disabled = model.SetRuleEnabled("r1", false);
            Assert.False(disabled.Itm.Rules[0].Enabled);
            Assert.Equal(expected, disabled.Itm.Rules[0].Show.PagesRaw);
            Assert.Equal("cycle", disabled.Itm.Rules[0].Show.KindRaw);
        }

        [Fact]
        public void ShowTextFor_ThreePageCycle_JoinsShortLabels()
        {
            var model = new DisplayTriggersEditModel(null, Device3);
            var show = new RuleTarget
            {
                Kind = TargetKind.Cycle,
                PagesRaw = new List<string> { "fuelErsDrs", "tyreTemps", "carSettings" },
                PeriodMs = 3000,
            };
            // Device 3: Fuel/ERS/DRS wire 2, Tyre Temps wire 5, Car Settings wire 3.
            Assert.Equal("P2 ⇄ P5 ⇄ P3", model.ShowTextFor(show));
        }

        // ── Special command (TargetKind.Special draft + Show text) ────────

        [Fact]
        public void ShowTextFor_Special_DiamondPrefixAndLabel()
        {
            var model = new DisplayTriggersEditModel(null, Device3);
            var show = new RuleTarget
            {
                Kind = TargetKind.Special,
                Command = SpecialCommand.LogoScreen,
            };
            Assert.Equal("\u25C7 Fanatec logo", model.ShowTextFor(show));
            show.Command = SpecialCommand.LogoInvertedScreen;
            Assert.Equal("\u25C7 Fanatec logo (inverted)", model.ShowTextFor(show));
            show.Command = SpecialCommand.WhiteScreen;
            Assert.Equal("\u25C7 White screen", model.ShowTextFor(show));
            show.Command = SpecialCommand.BlankScreen;
            Assert.Equal("\u25C7 Blank screen", model.ShowTextFor(show));
        }

        [Fact]
        public void ToDraft_And_BuildRule_Special_RoundTripsCommand()
        {
            var start = Load("{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"isTrue\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"DrsEnabled\" } }, "
                + "\"show\": { \"kind\": \"special\", \"command\": \"logoInverted\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } } ] } }");
            var draft = DisplayTriggersEditModel.ToDraft(start.Itm.Rules[0]);
            Assert.Equal(TargetKind.Special, draft.TargetKind);
            Assert.Equal(SpecialCommand.LogoInvertedScreen, draft.Command);

            draft.Command = SpecialCommand.WhiteScreen;
            var model = new DisplayTriggersEditModel(start, Device3);
            var cfg = model.UpdateRule(draft);
            var show = Assert.Single(cfg.Itm.Rules).Show;
            Assert.Equal(TargetKind.Special, show.Kind);
            Assert.Equal("special", show.KindRaw);
            Assert.Equal(SpecialCommand.WhiteScreen, show.Command);
            Assert.Equal("white", show.CommandRaw);
        }

        [Fact]
        public void SpecialCommandChoices_FourScreens_Selected()
        {
            var choices = DisplayTriggersEditModel.SpecialCommandChoices(SpecialCommand.LogoScreen);
            Assert.Equal(new[] { "logo", "logoInverted", "white", "blankScreen" },
                choices.Items.Select(i => i.Id).ToArray());
            Assert.Equal("Fanatec logo", choices.Items[0].Label);
            Assert.Equal("logo", choices.SelectedId);
        }

        [Fact]
        public void AddRule_Special_DefaultsLogoWhenCommandUnset()
        {
            var model = new DisplayTriggersEditModel(null, Device3);
            var draft = model.NewTelemetryDraft();
            draft.SourceKind = PropertyKind.BuiltIn;
            draft.SourceName = BuiltInProperties.Fuel;
            draft.Operator = ConditionKind.IsTrue;
            draft.TargetKind = TargetKind.Special;
            // Command left Unknown — BuildRule seeds LogoScreen.
            var cfg = model.AddRule(draft);
            Assert.Equal(SpecialCommand.LogoScreen, cfg.Itm.Rules[0].Show.Command);
            Assert.Equal("logo", cfg.Itm.Rules[0].Show.CommandRaw);
        }

        [Fact]
        public void CloneRuleWithRun_PreservesCommandRaw_OnDuplicateAndDisable()
        {
            var start = Load("{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"isTrue\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"DrsEnabled\" } }, "
                + "\"show\": { \"kind\": \"special\", \"command\": \"white\" }, "
                + "\"hold\": { \"kind\": \"whileActive\" } } ] } }");
            var model = new DisplayTriggersEditModel(start, Device3);

            var duplicated = model.DuplicateRule("r1", out _);
            Assert.Equal("white", duplicated.Itm.Rules[0].Show.CommandRaw);
            Assert.Equal("white", duplicated.Itm.Rules[1].Show.CommandRaw);

            var disabled = model.SetRuleEnabled("r1", false);
            Assert.False(disabled.Itm.Rules[0].Enabled);
            Assert.Equal("white", disabled.Itm.Rules[0].Show.CommandRaw);
            Assert.Equal("special", disabled.Itm.Rules[0].Show.KindRaw);
        }

        // ── Runs mapping (enable × eligibility fold, plan B6) ─────────────

        [Fact]
        public void RunsChoices_HasTheFourRuns_WithGlyphs_AndTheDraftsSelection()
        {
            var choices = DisplayTriggersEditModel.RunsChoices(
                new RuleEdit { Enabled = true, Eligibility = RuleEligibility.Always });

            Assert.Equal(new[] { "in", "idle", "any", "disabled" },
                choices.Items.Select(i => i.Id).ToArray());
            Assert.Equal("In game", choices.Items[0].Label);
            Assert.Equal("⚑", choices.Items[0].Glyph);
            Assert.Equal("Idle", choices.Items[1].Label);
            Assert.Equal("☾", choices.Items[1].Glyph);
            Assert.Equal("Always", choices.Items[2].Label);
            Assert.Equal("∞", choices.Items[2].Glyph);
            Assert.Equal("Disabled", choices.Items[3].Label);
            Assert.Equal("⊘", choices.Items[3].Glyph);
            Assert.Equal("any", choices.SelectedId);

            // A disabled draft selects Disabled regardless of its stored eligibility.
            Assert.Equal("disabled", DisplayTriggersEditModel.RunsChoices(
                new RuleEdit { Enabled = false, Eligibility = RuleEligibility.Idle }).SelectedId);
        }

        [Fact]
        public void SetRun_Disabled_TurnsOff_ButKeepsEligibleRawVerbatim()
        {
            var model = new DisplayTriggersEditModel(TwoRulesWithEligibility(), Device3);

            var cfg = model.SetRun("r1", DisplayTriggersEditModel.RunDisabled);

            var r1 = cfg.Itm.Rules[0];
            Assert.False(r1.Enabled);
            Assert.Equal("idle", r1.EligibleRaw);        // untouched — re-enabling restores it
            Assert.Equal(RuleEligibility.Idle, r1.Eligible);
            // The untouched sibling is carried through by reference (byte-identical).
            Assert.Same(model.Config.Itm.Rules[1], cfg.Itm.Rules[1]);
        }

        [Fact]
        public void SetRun_Scope_TurnsOn_AndSetsEligibility()
        {
            var model = new DisplayTriggersEditModel(TwoRulesWithEligibility(), Device3);

            var cfg = model.SetRun("r2", DisplayTriggersEditModel.RunInGame);   // r2 was disabled/any

            var r2 = cfg.Itm.Rules[1];
            Assert.True(r2.Enabled);
            Assert.Equal(RuleEligibility.InGame, r2.Eligible);
        }

        [Fact]
        public void SetRun_DisableThenReEnableToPriorScope_RestoresIt_ByteIdentical()
        {
            var start = TwoRulesWithEligibility();
            string before = DisplayConfigSerializer.Save(start);
            var model = new DisplayTriggersEditModel(start, Device3);

            model.SetRun("r1", DisplayTriggersEditModel.RunDisabled);    // off, eligibility kept
            var restored = model.SetRun("r1", DisplayTriggersEditModel.RunIdle);   // r1's prior scope

            Assert.True(restored.Itm.Rules[0].Enabled);
            Assert.Equal(RuleEligibility.Idle, restored.Itm.Rules[0].Eligible);
            // The whole document round-trips byte-for-byte back to the start.
            Assert.Equal(before, DisplayConfigSerializer.Save(restored));
        }

        // ── Base page ─────────────────────────────────────────────────────

        [Fact]
        public void SetBasePage_EditsBasePage_CarriesRulesThrough()
        {
            var model = new DisplayTriggersEditModel(OneNormalRule(), Device3);

            var cfg = model.SetBasePage(ItmPage.TyreTemps);

            Assert.Equal(ItmPage.TyreTemps, cfg.Itm.BasePage);
            var rule = Assert.Single(cfg.Itm.Rules);
            Assert.Equal("r1", rule.Id);
            Assert.Same(cfg, model.Config);
        }

        [Fact]
        public void EffectiveBasePage_UsesConfigBase_ThenDefaultWire()
        {
            var configured = new DisplayTriggersEditModel(
                Load("{ \"schemaVersion\": 1, \"itm\": { \"basePage\": \"lapTimes\", \"rules\": [] } }"), Device3);
            Assert.Equal(ItmPage.LapTimes, configured.EffectiveBasePage(defaultWirePage: 1));

            var fallback = new DisplayTriggersEditModel(OneNormalRule(), Device3);
            Assert.Equal(ItmPage.FuelErsDrs, fallback.EffectiveBasePage(defaultWirePage: 2));  // wire 2
        }

        // ── Duplicate / insert-at-top (the ⋯ menu + add flow) ─────────────

        [Fact]
        public void DuplicateRule_InsertsACopyBelow_WithAFreshId_NoSuffixForUnnamed()
        {
            var model = new DisplayTriggersEditModel(OneNormalRule(), Device3);

            var cfg = model.DuplicateRule("r1", out string newId);

            Assert.Equal(2, cfg.Itm.Rules.Count);
            Assert.Equal("r1", cfg.Itm.Rules[0].Id);
            Assert.Equal(newId, cfg.Itm.Rules[1].Id);
            Assert.NotEqual("r1", newId);
            Assert.Equal(32, newId.Length);                    // fresh GUID "N"
            Assert.Null(cfg.Itm.Rules[1].Name);                // unnamed source → no "(copy)"
            Assert.Equal("Fuel", cfg.Itm.Rules[1].When.Source.Name);   // same condition
        }

        [Fact]
        public void DuplicateRule_NamedRule_SuffixesTheCopyName()
        {
            var cfg0 = Load("{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"name\": \"My Fuel Rule\", \"when\": { \"kind\": \"greaterThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" } } ] } }");
            var model = new DisplayTriggersEditModel(cfg0, Device3);

            var cfg = model.DuplicateRule("r1", out _);

            Assert.Equal("My Fuel Rule (copy)", cfg.Itm.Rules[1].Name);
        }

        [Fact]
        public void DuplicateRule_UnknownId_IsNoOp()
        {
            var model = new DisplayTriggersEditModel(OneNormalRule(), Device3);
            var same = model.DuplicateRule("nope", out string newId);
            Assert.Same(model.Config, same);
            Assert.Null(newId);
        }

        [Fact]
        public void InsertRuleAtTop_PutsTheDraftFirst()
        {
            var model = new DisplayTriggersEditModel(OneNormalRule(), Device3);
            var draft = model.NewTelemetryDraft();
            draft.SourceName = "P";
            draft.Operator = ConditionKind.GreaterThan;
            draft.Value = 3;

            var cfg = model.InsertRuleAtTop(draft, out string newId);

            Assert.Equal(2, cfg.Itm.Rules.Count);
            Assert.Equal(newId, cfg.Itm.Rules[0].Id);          // draft at the TOP
            Assert.Equal("r1", cfg.Itm.Rules[1].Id);
        }

        // ── Property-pick shaping ─────────────────────────────────────────

        [Fact]
        public void AdoptPickedProperty_MappedControl_TakesTheMappedShape()
        {
            var model = new DisplayTriggersEditModel(null, Device3);
            var draft = model.NewTelemetryDraft();          // greaterThan telemetry default

            DisplayTriggersEditModel.AdoptPickedProperty(draft,
                DisplayTriggersEditModel.MappedControlPropertyName("Up Shift"),
                PropertyKind.SimHubProperty);

            var reference = model.NewMappedControlDraft("Up Shift");
            Assert.Equal(reference.SourceName, draft.SourceName);
            Assert.Equal(ConditionKind.IsTrue, draft.Operator);
            Assert.Equal(HoldKind.WhileActive, draft.Hold);
            Assert.Equal(RuleEligibility.Always, draft.Eligibility);
            Assert.Null(draft.Value);
            Assert.True(DisplayTriggersEditModel.IsMappedControlProperty(draft.SourceName));
        }

        [Fact]
        public void AdoptPickedProperty_Telemetry_LeavesTheOperatorAlone()
        {
            var model = new DisplayTriggersEditModel(null, Device3);
            var draft = model.NewTelemetryDraft();
            draft.Operator = ConditionKind.LessThan;

            DisplayTriggersEditModel.AdoptPickedProperty(draft, "GameData.Fuel", PropertyKind.SimHubProperty);

            Assert.Equal("GameData.Fuel", draft.SourceName);
            Assert.Equal(ConditionKind.LessThan, draft.Operator);   // untouched
            Assert.False(DisplayTriggersEditModel.IsMappedControlProperty(draft.SourceName));
        }
    }
}
