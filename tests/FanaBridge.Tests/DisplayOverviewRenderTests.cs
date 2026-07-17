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

        // ── Priority rows ────────────────────────────────────────────────

        [Fact]
        public void PriorityRows_MapsEveryStatus_InSnapshotOrder_BaseRowLast()
        {
            var snapshot = Snapshot(new[]
            {
                new DisplayRuleRow("r1", "Fuel < 10 → Fuel / ERS / DRS", RuleStatus.OnScreen, 3200),
                new DisplayRuleRow("r2", "Speed > 100 → Tire Temps", RuleStatus.Waiting, null),
                new DisplayRuleRow("r3", "Lap changes → Lap Info", RuleStatus.Armed, null),
                new DisplayRuleRow("r4", "Gap ≤ 0.5 → Car Settings", RuleStatus.Unavailable, null),
                new DisplayRuleRow("r5", "Oil > 120 → Car Settings", RuleStatus.Disabled, null),
                new DisplayRuleRow("r6", "Pit → Fuel / ERS / DRS", RuleStatus.Ineligible, null),
            });

            var rows = DisplayOverviewRender.PriorityRows(snapshot, "Lap Info");

            Assert.Equal(7, rows.Count);
            Assert.Equal(new[] { "1", "2", "3", "4", "5", "6", "★" },
                rows.Select(r => r.Rank).ToArray());

            // On screen: chip + accent + countdown (3.2 s reads as 4s, ceiling).
            Assert.Equal("on screen", rows[0].Chip);
            Assert.True(rows[0].OnScreen);
            Assert.Equal("4s", rows[0].Seconds);
            Assert.False(rows[0].Muted);

            Assert.Equal("waiting", rows[1].Chip);
            Assert.False(rows[1].OnScreen);
            Assert.Null(rows[1].Seconds);

            // Armed: blank chip, default styling.
            Assert.Equal("", rows[2].Chip);
            Assert.False(rows[2].Muted);

            Assert.Equal("n/a on this wheel", rows[3].Chip);
            Assert.False(rows[3].Muted);

            // Disabled and Ineligible: no chip, muted row.
            Assert.Equal("", rows[4].Chip);
            Assert.True(rows[4].Muted);
            Assert.Equal("", rows[5].Chip);
            Assert.True(rows[5].Muted);

            // The pinned base row.
            var baseRow = rows[6];
            Assert.True(baseRow.IsBase);
            Assert.Equal("Always → Lap Info", baseRow.Label);
            Assert.Equal("base", baseRow.Chip);
            Assert.False(baseRow.OnScreen);

            // Labels pass through the snapshot verbatim.
            Assert.Equal("Fuel < 10 → Fuel / ERS / DRS", rows[0].Label);
        }

        [Fact]
        public void PriorityRows_NullSnapshot_JustTheBaseRow()
        {
            var rows = DisplayOverviewRender.PriorityRows(null, "Tire Temps");

            var row = Assert.Single(rows);
            Assert.True(row.IsBase);
            Assert.Equal("★", row.Rank);
            Assert.Equal("Always → Tire Temps", row.Label);
        }

        [Fact]
        public void PriorityRows_CountdownEdges()
        {
            var snapshot = Snapshot(new[]
            {
                new DisplayRuleRow("a", "A", RuleStatus.OnScreen, 0),
                new DisplayRuleRow("b", "B", RuleStatus.OnScreen, null),   // indefinite hold
                new DisplayRuleRow("c", "C", RuleStatus.OnScreen, 5000),
            });

            var rows = DisplayOverviewRender.PriorityRows(snapshot, "Lap Info");

            Assert.Equal("0s", rows[0].Seconds);
            Assert.Null(rows[1].Seconds);      // on screen with no timer → no countdown
            Assert.Equal("5s", rows[2].Seconds);
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
