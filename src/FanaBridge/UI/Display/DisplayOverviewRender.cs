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

    /// <summary>The live-state styling for one rule row (see
    /// <see cref="DisplayOverviewRender.StateChip"/>) — shared by the Overview priority
    /// list and the Triggers editor so both speak the same row language.</summary>
    internal struct RuleStateChip
    {
        /// <summary>Chip text: "on screen", "waiting", "n/a on this wheel", or "" (armed).</summary>
        public string Chip;

        /// <summary>Hold countdown ("4s"), only while on screen with a timed hold.</summary>
        public string Seconds;

        /// <summary>The winning rule — green accent and left bar.</summary>
        public bool OnScreen;

        /// <summary>Disabled or ineligible — the row renders dimmed.</summary>
        public bool Muted;
    }

    /// <summary>One recent-activity row: relative age plus the event's pre-built text.</summary>
    internal sealed class ActivityRowModel
    {
        /// <summary>The event's absolute local wall-clock time, "HH:mm:ss".</summary>
        public string Time { get; set; }

        public string Text { get; set; }
    }

    /// <summary>
    /// Maps the volatile engine snapshots into the Overview view's row models — all of the
    /// Display tab's rendering decisions that are not literally WPF live here, so they are
    /// unit-testable with no UI thread. Pure functions of their inputs; activity
    /// timestamps are absolute wall-clock times derived via <see cref="EventTimeUtc"/>
    /// from the snapshot's dual compose stamps (engine clock + wall clock).
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
            // No stack live: re-derive the SAME base resolution the stack would, through
            // the one page table — the config's base page when this device offers it, else
            // the default-page setting's wire, else the off-table Lap Info fallback.
            ItmPage? configuredBase = config?.Itm != null && config.Itm.BasePageRaw != null
                ? config.Itm.BasePage
                : (ItmPage?)null;
            return ItmPageTable.ForDevice(itmDeviceId)
                .ResolveBase(configuredBase, defaultWirePage).Name;
        }

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
            var chip = StateChip(rule.Status, rule.RemainingMs);
            return new PriorityRowModel
            {
                Rank = rank.ToString(),
                Label = rule.Label,
                Chip = chip.Chip,
                Seconds = chip.Seconds,
                OnScreen = chip.OnScreen,
                Muted = chip.Muted,
            };
        }

        /// <summary>The live-state chip for one rule, shared by the Overview priority list
        /// and the Triggers editor rows so their row language cannot drift: chip text,
        /// countdown seconds (OnScreen + timed hold only), the on-screen accent, and the
        /// muted (disabled/ineligible) styling.</summary>
        internal static RuleStateChip StateChip(RuleStatus status, int? remainingMs)
        {
            var chip = new RuleStateChip { Chip = "" };
            switch (status)
            {
                case RuleStatus.OnScreen:
                    chip.Chip = "on screen";
                    chip.OnScreen = true;
                    if (remainingMs != null)
                        // Ceiling, so a 3.2s hold reads "4s" and only hits "0s" at expiry.
                        chip.Seconds = (remainingMs.Value + 999) / 1000 + "s";
                    break;
                case RuleStatus.Waiting:
                    chip.Chip = "waiting";
                    break;
                case RuleStatus.Unavailable:
                    chip.Chip = "n/a on this wheel";
                    break;
                case RuleStatus.Disabled:
                case RuleStatus.Ineligible:
                    chip.Muted = true;
                    break;
                    // Armed: no chip, default styling.
            }
            return chip;
        }

        /// <summary>
        /// The activity card's rows: newest first, capped at <see cref="ActivityCap"/>,
        /// stamped with the event's absolute LOCAL wall-clock time ("09:41:12", per the
        /// design). Events carry engine-clock ms; the snapshot stamps both clocks at
        /// composition, so an event's wall time is the compose wall time minus how far
        /// the event preceded composition on the engine clock — exact regardless of how
        /// stale the snapshot is when the panel first observes it.
        /// </summary>
        public static List<ActivityRowModel> ActivityRows(DisplayRuleSnapshot snapshot)
        {
            var rows = new List<ActivityRowModel>();
            var events = snapshot?.Activity;
            if (events == null)
                return rows;
            for (int i = events.Count - 1; i >= 0 && rows.Count < ActivityCap; i--)
                rows.Add(new ActivityRowModel
                {
                    Time = EventTimeUtc(snapshot, events[i].AtMs).ToLocalTime()
                        .ToString("HH:mm:ss"),
                    Text = events[i].Text,
                });
            return rows;
        }

        /// <summary>An event's absolute UTC wall time from the snapshot's dual compose
        /// stamps (engine ms + UTC taken together at composition).</summary>
        public static DateTime EventTimeUtc(DisplayRuleSnapshot snapshot, long eventAtMs)
            => snapshot.ComposedAtUtc
                - TimeSpan.FromMilliseconds(snapshot.ComposedAtMs - eventAtMs);

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
                    var table = ItmPageTable.ForDevice(itmDeviceId);
                    return table.TryGetPage(wire, out _)
                        ? "Page " + wire + " · " + table.NameAtWire(wire)
                        : "Page " + wire;
                }
                return "Synced";   // "page ?" — synced before a page number is adopted
            }

            return itmStatus;
        }
    }
}
