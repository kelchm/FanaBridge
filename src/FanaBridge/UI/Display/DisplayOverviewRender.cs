using System;
using System.Collections.Generic;
using FanaBridge.Adapters;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Session;
using FanaBridge.Protocol;
using FanaBridge.UI.Display.Shared;

namespace FanaBridge.UI.Display
{
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
        /// The Overview's Monitor rows (the v9 converged "what's in play" list) for the shared
        /// <see cref="Shared.TriggerTableControl"/>: the config's rules projected through
        /// <see cref="DisplayTriggersEditModel.Rows"/> in <see cref="TriggerTableMode.Monitor"/>,
        /// so the row language (structured When, live chip, "Now" value, winning emphasis) is
        /// single-sourced with the Triggers editor. Disabled/degraded and session-ineligible
        /// rules drop, the survivors renumber 1..n, and the base row is pinned last — the same
        /// filter the mock applies. A null config yields just the base row.
        /// </summary>
        public static IReadOnlyList<Shared.TriggerTableRow> MonitorRows(DisplayRuleSnapshot snapshot,
            DisplayCustomizationConfig config, byte itmDeviceId, byte defaultWirePage)
            => new DisplayTriggersEditModel(config, itmDeviceId, defaultWirePage)
                .Rows(snapshot, defaultWirePage, TriggerTableMode.Monitor);

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
