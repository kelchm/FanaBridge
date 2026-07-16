using System;
using System.Collections.Generic;
using FanaBridge.Adapters;
using FanaBridge.Customization;
using FanaBridge.Protocol;

namespace FanaBridge.UI
{
    /// <summary>One priority-list row, ready to draw: rank, label, and state styling.</summary>
    internal sealed class PriorityRowModel
    {
        /// <summary>"1".."n" for rules, "★" for the pinned base row.</summary>
        public string Rank { get; set; }

        /// <summary>The rule's display text, or "Always → &lt;base page&gt;" for the base row.</summary>
        public string Label { get; set; }

        /// <summary>State chip text: "on screen", "waiting", "n/a on this wheel", "base",
        /// or "" (armed, and the chip-less muted states).</summary>
        public string Chip { get; set; } = "";

        /// <summary>Hold countdown ("4s"), only while on screen with a timed hold.</summary>
        public string Seconds { get; set; }

        /// <summary>The winning rule — green accent and left bar.</summary>
        public bool OnScreen { get; set; }

        /// <summary>Disabled or ineligible — the row renders dimmed.</summary>
        public bool Muted { get; set; }

        /// <summary>The pinned "Always" row — dashed border, always last.</summary>
        public bool IsBase { get; set; }
    }

    /// <summary>One recent-activity row: relative age plus the event's pre-built text.</summary>
    internal sealed class ActivityRowModel
    {
        /// <summary>Age at render time, "mm:ss" (or "h:mm:ss" past an hour).</summary>
        public string Age { get; set; }

        public string Text { get; set; }
    }

    /// <summary>
    /// Maps the volatile engine snapshots into the Overview view's row models — all of the
    /// Display tab's rendering decisions that are not literally WPF live here, so they are
    /// unit-testable with no UI thread. Pure functions of their inputs; relative ages use
    /// the engine-clock "now" estimated by <see cref="EstimatedNowMs"/> from the snapshot's
    /// own compose stamps (engine clock + wall clock).
    /// </summary>
    internal static class DisplayOverviewRender
    {
        /// <summary>The activity card shows at most this many events, newest first.</summary>
        public const int ActivityCap = 10;

        /// <summary>
        /// The base ("Always") page's display name. A live snapshot carries the rule
        /// stack's own resolution (<see cref="DisplayRuleSnapshot.BasePageName"/>) and that
        /// wins — the stack is the one base-page authority while it runs, including its
        /// build-time ItmDefaultPage capture and its unavailable-page fallback. With no
        /// snapshot (no stack live) the same precedence is derived here: the config's base
        /// page when set AND offered by this device, else the default-page setting's wire
        /// number through the device's page table, else Lap Info (the stack's fallback).
        /// </summary>
        public static string BasePageName(DisplayRuleSnapshot snapshot,
            DisplayCustomizationConfig config, byte itmDeviceId, byte defaultWirePage)
        {
            if (snapshot?.BasePageName != null)
                return snapshot.BasePageName;
            var pages = ItmDeviceCatalog.PagesFor(itmDeviceId);
            if (config?.Itm != null && config.Itm.BasePageRaw != null)
            {
                var basePage = config.Itm.BasePage;
                foreach (var page in pages)
                    if (page.Page == basePage)
                        return page.Name;
                // Pinned page this device doesn't have: the stack silently keeps the
                // default wire (PageToWire fallback) — mirror it rather than claim a
                // page the display can never rest on.
            }
            foreach (var page in pages)
                if (page.Number == defaultWirePage)
                    return page.Name;
            return ItmTelemetry.NameOf(ItmPage.LapInfo);
        }

        /// <summary>
        /// The engine clock's estimated current value: the snapshot's compose clock plus
        /// the wall time elapsed since composition (the snapshot stamps both). Correct
        /// however late the snapshot is first observed — a panel-side "first seen" anchor
        /// would understate every age when the dialog opens after a quiet period, because
        /// composition is change-gated and the latest snapshot can be minutes old.
        /// </summary>
        public static long EstimatedNowMs(DisplayRuleSnapshot snapshot, DateTime utcNow)
            => snapshot.ComposedAtMs
                + (long)(utcNow - snapshot.ComposedAtUtc).TotalMilliseconds;

        /// <summary>True when the config has ITM trigger rules — gates the priority list's
        /// "No triggers configured yet." empty state.</summary>
        public static bool HasConfiguredTriggers(DisplayCustomizationConfig config)
            => config?.Itm?.Rules != null && config.Itm.Rules.Count > 0;

        /// <summary>
        /// The priority list: one row per ITM rule in snapshot (priority) order, then the
        /// base row pinned last. A null snapshot (no customization active, or none composed
        /// yet) yields just the base row.
        /// </summary>
        public static List<PriorityRowModel> PriorityRows(DisplayRuleSnapshot snapshot,
            string basePageName)
        {
            var rows = new List<PriorityRowModel>();
            var rules = snapshot?.ItmRules;
            if (rules != null)
                for (int i = 0; i < rules.Count; i++)
                    rows.Add(RuleRow(i + 1, rules[i]));
            rows.Add(new PriorityRowModel
            {
                Rank = "★",
                Label = "Always → " + basePageName,
                Chip = "base",
                IsBase = true,
            });
            return rows;
        }

        private static PriorityRowModel RuleRow(int rank, DisplayRuleRow rule)
        {
            var row = new PriorityRowModel { Rank = rank.ToString(), Label = rule.Label };
            switch (rule.Status)
            {
                case RuleStatus.OnScreen:
                    row.Chip = "on screen";
                    row.OnScreen = true;
                    if (rule.RemainingMs != null)
                        // Ceiling, so a 3.2s hold reads "4s" and only hits "0s" at expiry.
                        row.Seconds = (rule.RemainingMs.Value + 999) / 1000 + "s";
                    break;
                case RuleStatus.Waiting:
                    row.Chip = "waiting";
                    break;
                case RuleStatus.Unavailable:
                    row.Chip = "n/a on this wheel";
                    break;
                case RuleStatus.Disabled:
                case RuleStatus.Ineligible:
                    row.Muted = true;
                    break;
                    // Armed: no chip, default styling.
            }
            return row;
        }

        /// <summary>
        /// The activity card's rows: newest first, capped at <see cref="ActivityCap"/>.
        /// <paramref name="nowMs"/> is the estimated current engine-clock value (events and
        /// <see cref="DisplayRuleSnapshot.ComposedAtMs"/> share that clock).
        /// </summary>
        public static List<ActivityRowModel> ActivityRows(DisplayRuleSnapshot snapshot, long nowMs)
        {
            var rows = new List<ActivityRowModel>();
            var events = snapshot?.Activity;
            if (events == null)
                return rows;
            for (int i = events.Count - 1; i >= 0 && rows.Count < ActivityCap; i--)
                rows.Add(new ActivityRowModel
                {
                    Age = FormatAge(nowMs - events[i].AtMs),
                    Text = events[i].Text,
                });
            return rows;
        }

        /// <summary>A relative age as "mm:ss", growing to "h:mm:ss" past an hour. Clock skew
        /// (an event newer than the estimate) clamps to zero rather than going negative.</summary>
        public static string FormatAge(long ageMs)
        {
            if (ageMs < 0)
                ageMs = 0;
            long totalSeconds = ageMs / 1000;
            long hours = totalSeconds / 3600;
            long minutes = totalSeconds % 3600 / 60;
            long seconds = totalSeconds % 60;
            return hours > 0
                ? hours + ":" + minutes.ToString("00") + ":" + seconds.ToString("00")
                : minutes.ToString("00") + ":" + seconds.ToString("00");
        }

        /// <summary>
        /// The current-page card's caption, from the lifecycle status line
        /// (<see cref="ItmLifecycleController.Describe"/> via the panel context). Synced
        /// becomes "Page N · &lt;name&gt;" (name from the device's page table); the
        /// off/bring-up/recovery states become short user words; transient switch states
        /// (already user-readable) pass through unchanged.
        /// </summary>
        public static string CurrentPageCaption(string itmStatus, byte itmDeviceId)
        {
            if (itmStatus == null || itmStatus == "Disabled")
                return "ITM off";
            if (itmStatus == "Idle")
                return "ITM idle";
            if (itmStatus == "BringUp")
                return "Bringing up…";
            if (itmStatus.StartsWith("Recovering", StringComparison.Ordinal))
                return "Recovering…";

            const string synced = "Synced — page ";
            if (itmStatus.StartsWith(synced, StringComparison.Ordinal))
            {
                int end = itmStatus.IndexOf(',', synced.Length);
                string token = end < 0
                    ? itmStatus.Substring(synced.Length)
                    : itmStatus.Substring(synced.Length, end - synced.Length);
                if (byte.TryParse(token, out byte wire))
                {
                    foreach (var page in ItmDeviceCatalog.PagesFor(itmDeviceId))
                        if (page.Number == wire)
                            return "Page " + wire + " · " + page.Name;
                    return "Page " + wire;
                }
                return "Synced";   // "page ?" — synced before a page number is adopted
            }

            return itmStatus;
        }
    }
}
