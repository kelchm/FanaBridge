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

namespace FanaBridge.Tests.UI
{
    /// <summary>
    /// The Display tab's Overview render model — the snapshot→row mapping the XAML
    /// code-behind draws from (<see cref="DisplayOverviewRender"/>). Plain functions,
    /// no UI thread: chip text per rule status, ordering, the pinned base row, the
    /// empty state, activity ordering/cap, relative ages, and the current-page caption.
    /// </summary>
    public class DisplayOverviewRenderTests
    {
        private static DisplayRuleSnapshot Snapshot(
            IReadOnlyList<DisplayRuleRow>? itmRules = null,
            IReadOnlyList<DisplayActivityEvent>? activity = null,
            long composedAtMs = 0,
            string? basePageName = null,
            DateTime composedAtUtc = default)
            => new DisplayRuleSnapshot("Lap Info", basePageName,
                itmRules ?? new DisplayRuleRow[0],
                new DisplayRuleRow[0],
                activity ?? new DisplayActivityEvent[0],
                activityVersion: 0,
                composedAtMs: composedAtMs,
                composedAtUtc: composedAtUtc);

        // ── State chip: the shared live-state helper ─────────────────────
        //    (StateChip is the one live producer of chip text / countdown / accent / muted,
        //    consumed by DisplayTriggersEditModel.Rows for both Workbench and Monitor.)

        [Fact]
        public void StateChip_MapsEveryStatus()
        {
            // On screen: chip + accent + countdown (3.2 s reads as 4s, ceiling).
            var onScreen = DisplayOverviewRender.StateChip(RuleStatus.OnScreen, 3200);
            Assert.Equal("on screen", onScreen.Chip);
            Assert.True(onScreen.OnScreen);
            Assert.Equal("4s", onScreen.Seconds);
            Assert.False(onScreen.Muted);

            var waiting = DisplayOverviewRender.StateChip(RuleStatus.Waiting, null);
            Assert.Equal("waiting", waiting.Chip);
            Assert.False(waiting.OnScreen);
            Assert.Null(waiting.Seconds);

            // Armed: blank chip, default styling.
            var armed = DisplayOverviewRender.StateChip(RuleStatus.Armed, null);
            Assert.Equal("", armed.Chip);
            Assert.False(armed.Muted);
            Assert.False(armed.OnScreen);

            var unavailable = DisplayOverviewRender.StateChip(RuleStatus.Unavailable, null);
            Assert.Equal("n/a on this wheel", unavailable.Chip);
            Assert.False(unavailable.Muted);

            // Disabled and Ineligible: no chip, muted row.
            var disabled = DisplayOverviewRender.StateChip(RuleStatus.Disabled, null);
            Assert.Equal("", disabled.Chip);
            Assert.True(disabled.Muted);
            var ineligible = DisplayOverviewRender.StateChip(RuleStatus.Ineligible, null);
            Assert.Equal("", ineligible.Chip);
            Assert.True(ineligible.Muted);
        }

        [Fact]
        public void StateChip_CountdownEdges()
        {
            Assert.Equal("0s", DisplayOverviewRender.StateChip(RuleStatus.OnScreen, 0).Seconds);
            // On screen with no timer (indefinite hold) → no countdown.
            Assert.Null(DisplayOverviewRender.StateChip(RuleStatus.OnScreen, null).Seconds);
            Assert.Equal("5s", DisplayOverviewRender.StateChip(RuleStatus.OnScreen, 5000).Seconds);
        }

        // ── Monitor rows (the v9 converged Overview list — the LIVE Overview projection) ──

        [Fact]
        public void MonitorRows_StructuredWhenFields_DerivedFromConfig()
        {
            var snapshot = Snapshot(new[]
            {
                new DisplayRuleRow("r1", "Fuel > 10 → Fuel / ERS / DRS", RuleStatus.OnScreen, 3200),
                new DisplayRuleRow("r2", "Up Shift is on → Tire Temps", RuleStatus.Armed, null),
            });
            var config = DisplayConfigSerializer.Load(
                "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"greaterThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" } }, "
                + "{ \"id\": \"r2\", \"when\": { \"kind\": \"isTrue\", "
                + "\"source\": { \"kind\": \"simHubProperty\", "
                + "\"name\": \"InputStatus.ControlMapperPlugin.Up Shift\" } }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"tyreTemps\" } } ] } }", _ => { });

            // r1 on screen + r2 armed both survive the Monitor filter; base row pinned last.
            var rows = DisplayOverviewRender.MonitorRows(snapshot, config, itmDeviceId: 3, defaultWirePage: 1);

            Assert.Equal("Fuel", rows[0].PropertyName);
            Assert.Equal(PropertyDisplayKind.BuiltIn, rows[0].DisplayKind);
            Assert.Equal(">", rows[0].Operator);
            Assert.Equal("10", rows[0].ValueText);
            Assert.Contains("Fuel / ERS / DRS", rows[0].ShowText);

            Assert.Equal("InputStatus.ControlMapperPlugin.Up Shift", rows[1].PropertyName);
            Assert.Equal(PropertyDisplayKind.SimHubProperty, rows[1].DisplayKind);
            Assert.Equal("is on", rows[1].Operator);
            Assert.Equal("", rows[1].ValueText);
            Assert.Contains("Tire Temps", rows[1].ShowText);

            // The base row never carries structured fields.
            Assert.True(rows[2].IsBase);
            Assert.Null(rows[2].PropertyName);
        }

        [Fact]
        public void MonitorRows_UserNamedRule_NoStructuredGrammar_UsesLabel()
        {
            // Mirror of the Triggers editor: a user-named rule keeps its Label (deviation #1),
            // never the "prop op value" grammar. Guarded by IsNullOrWhiteSpace(rule.Name) in
            // ApplyStructuredWhen — remove it and PropertyName populates, failing this test.
            var snapshot = Snapshot(new[]
            {
                new DisplayRuleRow("r1", "ignored snapshot label", RuleStatus.Armed, null),
            });
            var config = DisplayConfigSerializer.Load(
                "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"name\": \"My Fuel Rule\", \"when\": { \"kind\": \"greaterThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" } } ] } }", _ => { });

            var rows = DisplayOverviewRender.MonitorRows(snapshot, config, itmDeviceId: 3, defaultWirePage: 1);

            Assert.Null(rows[0].PropertyName);              // named → no grammar
            Assert.Equal("My Fuel Rule", rows[0].Label);    // the config name, via Label
        }

        [Fact]
        public void MonitorRows_ActionTriggeredRule_NoStructuredGrammar_UsesLabel()
        {
            // ActionTriggered is excluded from structured rendering (its quoted framing would
            // be dropped) — the row keeps its Label. Without the ActionTriggered guard in
            // ApplyStructuredWhen, PropertyName populates and this test fails.
            var snapshot = Snapshot(new[]
            {
                new DisplayRuleRow("r1", "ignored snapshot label", RuleStatus.Armed, null),
            });
            var config = DisplayConfigSerializer.Load(
                "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"actionTriggered\", "
                + "\"source\": { \"kind\": \"simHubProperty\", \"name\": \"ShowTyres\" } }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"tyreTemps\" } } ] } }", _ => { });

            var rows = DisplayOverviewRender.MonitorRows(snapshot, config, itmDeviceId: 3, defaultWirePage: 1);

            Assert.Null(rows[0].PropertyName);              // ActionTriggered → label fallback
            Assert.Contains("'ShowTyres' triggered", rows[0].Label);
        }

        [Fact]
        public void MonitorRows_NullSnapshotWithConfig_RulesRenderLabelOnly_BaseLast()
        {
            // No snapshot (customization not composed yet): rules render with no live chip,
            // structured When still derives from config, and the base row pins last.
            var config = DisplayConfigSerializer.Load(
                "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"greaterThan\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" } } ] } }", _ => { });

            var rows = DisplayOverviewRender.MonitorRows(null, config, itmDeviceId: 3, defaultWirePage: 2);

            Assert.Equal(2, rows.Count);
            Assert.Equal("r1", rows[0].RuleId);
            Assert.Equal("", rows[0].Chip);                 // no live state merged
            Assert.Equal("Fuel", rows[0].PropertyName);
            Assert.True(rows[1].IsBase);
            Assert.Equal("Fuel / ERS / DRS", rows[1].ShowText);   // wire 2 on device 3
        }

        // ── Monitor rows: the filter + renumber path ─────────────────────

        [Fact]
        public void MonitorRows_DropsDisabledAndIneligible_RenumbersContiguously_BaseLast()
        {
            var config = DisplayConfigSerializer.Load(
                "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"greaterThan\", \"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" }, \"hold\": { \"kind\": \"forDuration\", \"durationMs\": 3200 } }, "
                + "{ \"id\": \"r2\", \"enabled\": false, \"when\": { \"kind\": \"isTrue\", \"source\": { \"kind\": \"builtIn\", \"name\": \"DrsEnabled\" } }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"tyreTemps\" } }, "
                + "{ \"id\": \"r3\", \"when\": { \"kind\": \"isTrue\", \"source\": { \"kind\": \"builtIn\", \"name\": \"PitLimiterOn\" } }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"carSettings\" }, \"eligible\": \"idle\" }, "
                + "{ \"id\": \"r4\", \"when\": { \"kind\": \"greaterThan\", \"source\": { \"kind\": \"builtIn\", \"name\": \"Speed\" }, \"value\": 100 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"tyreTemps\" } } ] } }", _ => { });

            var snapshot = Snapshot(new[]
            {
                new DisplayRuleRow("r1", "x", RuleStatus.OnScreen, 3200, "8.4"),
                new DisplayRuleRow("r2", "x", RuleStatus.Disabled, null, "off"),
                new DisplayRuleRow("r3", "x", RuleStatus.Ineligible, null, "off"),
                new DisplayRuleRow("r4", "x", RuleStatus.Waiting, null, "150"),
            });

            var rows = DisplayOverviewRender.MonitorRows(snapshot, config, itmDeviceId: 3, defaultWirePage: 1);

            // r2 (disabled) and r3 (session-ineligible) drop; r1 + r4 survive, renumbered 1..2;
            // base row pinned last.
            Assert.Equal(3, rows.Count);
            Assert.Equal(new[] { "1", "2", "★" }, rows.Select(r => r.Rank).ToArray());
            Assert.Equal(new[] { "r1", "r4" },
                rows.Where(r => !r.IsBase).Select(r => r.RuleId).ToArray());

            // Winning emphasis flag + live "Now" value + countdown carry through.
            Assert.True(rows[0].OnScreen);
            Assert.Equal("8.4", rows[0].NowText);
            Assert.Equal("4s", rows[0].Seconds);       // 3.2 s ceils to 4s
            Assert.False(rows[1].OnScreen);
            Assert.Equal("150", rows[1].NowText);

            // Base footer row: device 3 default wire 1 = Lap Info; ShowText carries the bare name.
            Assert.True(rows[2].IsBase);
            Assert.Equal("Lap Info", rows[2].ShowText);
        }

        [Fact]
        public void MonitorRows_NullConfig_JustTheBaseRow()
        {
            var rows = DisplayOverviewRender.MonitorRows(null, null, itmDeviceId: 3, defaultWirePage: 2);
            var row = Assert.Single(rows);
            Assert.True(row.IsBase);
            Assert.Equal("Fuel / ERS / DRS", row.ShowText);   // wire 2 on device 3
        }

        // ── Empty state / base page name ─────────────────────────────────

        [Fact]
        public void HasConfiguredTriggers_OnlyForItmRules()
        {
            Assert.False(DisplayOverviewRender.HasConfiguredTriggers(null));
            Assert.False(DisplayOverviewRender.HasConfiguredTriggers(new DisplayCustomizationConfig()));

            // Legacy-only content is not an ITM trigger.
            var legacyOnly = new DisplayCustomizationConfig();
            legacyOnly.Legacy.Screens.Add(new LegacyScreen { Id = "pit", Text = "PIT" });
            Assert.False(DisplayOverviewRender.HasConfiguredTriggers(legacyOnly));

            var withRule = new DisplayCustomizationConfig();
            withRule.Itm.Rules.Add(new DisplayRule { Id = "r1" });
            Assert.True(DisplayOverviewRender.HasConfiguredTriggers(withRule));
        }

        [Fact]
        public void BasePageName_ConfigBasePageWins_ElseTheDefaultPageSetting()
        {
            // Config pins its own base page (and the device offers it).
            var config = new DisplayCustomizationConfig();
            config.Itm.BasePage = ItmPage.TyreTemps;
            Assert.Equal("Tire Temps",
                DisplayOverviewRender.BasePageName(null, config, itmDeviceId: 3, defaultWirePage: 1));

            // No config: the ItmDefaultPage wire number resolves through the catalog.
            Assert.Equal("Fuel / ERS / DRS",
                DisplayOverviewRender.BasePageName(null, null, itmDeviceId: 3, defaultWirePage: 2));

            // Config present but no explicit base page → still the setting.
            Assert.Equal("Fuel / ERS / DRS",
                DisplayOverviewRender.BasePageName(null, new DisplayCustomizationConfig(),
                    itmDeviceId: 3, defaultWirePage: 2));

            // Bentley (device 4) renumbers: wire 3 is Lap Times there.
            Assert.Equal("Lap Times",
                DisplayOverviewRender.BasePageName(null, null, itmDeviceId: 4, defaultWirePage: 3));

            // A wire number the device doesn't offer falls back to Lap Info (the
            // stack's own fallback).
            Assert.Equal("Lap Info",
                DisplayOverviewRender.BasePageName(null, null, itmDeviceId: 3, defaultWirePage: 99));
        }

        [Fact]
        public void BasePageName_LiveSnapshotWins_ItCarriesTheStacksOwnResolution()
        {
            // While a rule stack is live its snapshot carries the base page the engine
            // ACTUALLY uses (captured at stack build). The UI must not re-derive it from
            // live settings, or changing Starting Page would show a page the engine
            // won't use until the next stack rebuild.
            var snapshot = Snapshot(basePageName: "Lap Times");
            var config = new DisplayCustomizationConfig();
            config.Itm.BasePage = ItmPage.TyreTemps;

            Assert.Equal("Lap Times",
                DisplayOverviewRender.BasePageName(snapshot, config, itmDeviceId: 3, defaultWirePage: 1));
        }

        [Fact]
        public void BasePageName_PinnedPageTheDeviceLacks_FallsBackLikeTheStack()
        {
            // The Bentley set (device 4) has no Car Settings page: the running stack
            // silently keeps the default wire, so the "Always" row must not claim a
            // page the display can never rest on.
            var config = new DisplayCustomizationConfig();
            config.Itm.BasePage = ItmPage.CarSettings;

            Assert.Equal("Lap Info",
                DisplayOverviewRender.BasePageName(null, config, itmDeviceId: 4, defaultWirePage: 1));
        }

        // ── Activity rows ────────────────────────────────────────────────

        [Fact]
        public void ActivityRows_NewestFirst_CappedAtTen_WithWallClockTimes()
        {
            // 12 events, oldest first (the snapshot's order), 1 s apart, composed at
            // engine 12s = 12:00:00 UTC. Timestamps are ABSOLUTE local times (the design
            // shows "09:41:12"-style clock times, not ages).
            var composedUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var events = new DisplayActivityEvent[12];
            for (int i = 0; i < 12; i++)
                events[i] = new DisplayActivityEvent(
                    atMs: i * 1000, ActivityKind.RuleFired, "event " + i, "r" + i);
            var snapshot = Snapshot(activity: events, composedAtMs: 12_000,
                composedAtUtc: composedUtc);

            var rows = DisplayOverviewRender.ActivityRows(snapshot);

            Assert.Equal(DisplayOverviewRender.ActivityCap, rows.Count);
            Assert.Equal("event 11", rows[0].Text);     // newest first
            Assert.Equal("event 2", rows[9].Text);      // the two oldest fell off
            // Rendered times are local; assert through the same conversion the
            // renderer uses so the test is timezone-independent.
            Assert.Equal(composedUtc.AddSeconds(-1).ToLocalTime().ToString("HH:mm:ss"),
                rows[0].Time);   // event 11 fired 1 s before composition
            Assert.Equal(composedUtc.AddSeconds(-10).ToLocalTime().ToString("HH:mm:ss"),
                rows[9].Time);
        }

        [Fact]
        public void ActivityRows_NullSnapshot_Empty()
        {
            Assert.Empty(DisplayOverviewRender.ActivityRows(null));
        }

        [Fact]
        public void EventTimes_AreAbsolute_UnaffectedByHowStaleTheSnapshotIs()
        {
            // Composition is change-gated, so the latest snapshot can be minutes old
            // when the dialog opens. Absolute event times derive purely from the
            // snapshot's own dual compose stamps — a rule that fired 2 s before an
            // 12:00:00 compose reads 11:59:58 whether the dialog opens now or an hour
            // from now (the old relative-age rendering had exactly this staleness bug).
            var composedUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var snapshot = Snapshot(
                activity: new[] { new DisplayActivityEvent(
                    atMs: 58_000, ActivityKind.RuleFired, "rule fired", "r1") },
                composedAtMs: 60_000,
                composedAtUtc: composedUtc);

            Assert.Equal(new DateTime(2026, 1, 1, 11, 59, 58, DateTimeKind.Utc),
                DisplayOverviewRender.EventTimeUtc(snapshot, eventAtMs: 58_000));
            var row = Assert.Single(DisplayOverviewRender.ActivityRows(snapshot));
            Assert.Equal(composedUtc.AddSeconds(-2).ToLocalTime().ToString("HH:mm:ss"),
                row.Time);
        }

        // ── Current-page caption ─────────────────────────────────────────

        [Theory]
        [InlineData(null, "ITM off")]
        [InlineData("Disabled", "ITM off")]
        [InlineData("Idle", "ITM idle")]
        [InlineData("BringUp", "Bringing up…")]
        [InlineData("Recovering (gate cycle, target page 1)", "Recovering…")]
        [InlineData("Synced — page 5, 6 params", "Page 5 · Tire Temps")]
        [InlineData("Synced — page ?, 0 params", "Synced")]
        // Transient switch states are already user-readable and pass through.
        [InlineData("Switching to page 4", "Switching to page 4")]
        [InlineData("Waiting for page 4 confirmation", "Waiting for page 4 confirmation")]
        [InlineData("Unavailable — retry in 8 s", "Unavailable — retry in 8 s")]
        public void CurrentPageCaption_MapsTheLifecycleStatus(string? status, string expected)
        {
            Assert.Equal(expected, DisplayOverviewRender.CurrentPageCaption(status, itmDeviceId: 3));
        }

        [Fact]
        public void CurrentPageCaption_ResolvesPageNames_PerDevice()
        {
            // Device 3 (standard set): wire 4 is Lap Times; the Bentley (device 4)
            // renumbers so its wire 4 is Tire Temps.
            Assert.Equal("Page 4 · Lap Times",
                DisplayOverviewRender.CurrentPageCaption("Synced — page 4, 6 params", 3));
            Assert.Equal("Page 4 · Tire Temps",
                DisplayOverviewRender.CurrentPageCaption("Synced — page 4, 6 params", 4));

            // A wire number outside the device's table still reads honestly.
            Assert.Equal("Page 9",
                DisplayOverviewRender.CurrentPageCaption("Synced — page 9, 6 params", 3));
        }
    }
}
