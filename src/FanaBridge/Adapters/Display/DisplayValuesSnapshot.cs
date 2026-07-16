using System;
using System.Collections.Generic;
using FanaBridge.Protocol;

namespace FanaBridge.Adapters
{
    /// <summary>One rendered field in a display-values snapshot: the parameter, its
    /// label (when the field carries its own — dual TC/ABS style), and the display
    /// string as the hardware renders it (value + suffix, or the dash placeholder).</summary>
    public sealed class DisplayValueField
    {
        internal DisplayValueField(ushort paramId, string label, string value)
        {
            ParamId = paramId;
            Label = label;
            Value = value;
        }

        public ushort ParamId { get; }

        /// <summary>The field's own label, or null when <see cref="DisplayValueSlot.Label"/>
        /// covers it.</summary>
        public string Label { get; }

        /// <summary>The rendered display string (<see cref="ItmValueRenderer"/>) for the
        /// value last sent on the wire — or the field's placeholder when nothing has
        /// been sent since the last reset/sync.</summary>
        public string Value { get; }
    }

    /// <summary>One of the four field slots, rendered: the slot label (null when each
    /// field is labeled individually) and its one or two fields.</summary>
    public sealed class DisplayValueSlot
    {
        internal DisplayValueSlot(string label, IReadOnlyList<DisplayValueField> fields)
        {
            Label = label;
            Fields = fields;
        }

        public string Label { get; }

        public IReadOnlyList<DisplayValueField> Fields { get; }
    }

    /// <summary>
    /// An immutable cross-thread snapshot of what the ITM display is showing — composed
    /// by <see cref="ItmDisplayDriver"/> from the values it last put on the wire (never
    /// from a separate telemetry read, so it cannot drift from the hardware), published
    /// through a volatile field and polled by the UI's display mirror. Recomposed only
    /// when a sent value, suffix, page, or lifecycle state actually changed, at a
    /// bounded cadence — the same hand-off pattern as <see cref="DisplayRuleSnapshot"/>,
    /// kept separate from it (this one exists for every ITM user, rules or not).
    /// </summary>
    public sealed class DisplayValuesSnapshot
    {
        internal DisplayValuesSnapshot(ItmPage? page, byte wirePage, string pageName,
            ItmLifecycleState state, bool showingPlaceholders,
            DisplayValueSlot leftTop, DisplayValueSlot leftBottom,
            DisplayValueSlot rightTop, DisplayValueSlot rightBottom,
            string gearText, string speedText, long composedAtMs, DateTime composedAtUtc)
        {
            Page = page;
            WirePage = wirePage;
            PageName = pageName;
            State = state;
            ShowingPlaceholders = showingPlaceholders;
            LeftTop = leftTop;
            LeftBottom = leftBottom;
            RightTop = rightTop;
            RightBottom = rightBottom;
            GearText = gearText;
            SpeedText = speedText;
            ComposedAtMs = composedAtMs;
            ComposedAtUtc = composedAtUtc;
        }

        /// <summary>The content identity of the page the display is on, or null while no
        /// page is known (not yet synced, or a page outside the catalog).</summary>
        public ItmPage? Page { get; }

        /// <summary>The wire page number on this device (0 = unknown).</summary>
        public byte WirePage { get; }

        /// <summary>The page's display name, or "Page N" for an uncataloged wire page;
        /// null while no page is known.</summary>
        public string PageName { get; }

        /// <summary>The ITM lifecycle state at composition — the UI derives its state
        /// caption (off / bringing up / recovering / synced) from this.</summary>
        public ItmLifecycleState State { get; }

        /// <summary>True while the fields show dash placeholders rather than values —
        /// nothing sent since the last sync, or the fields were cleared by the game-exit
        /// display reset.</summary>
        public bool ShowingPlaceholders { get; }

        /// <summary>The four rendered field slots; all null when the current page has no
        /// slots (legacy page, or no page known).</summary>
        public DisplayValueSlot LeftTop { get; }
        public DisplayValueSlot LeftBottom { get; }
        public DisplayValueSlot RightTop { get; }
        public DisplayValueSlot RightBottom { get; }

        /// <summary>The center-zone gear string ("N", "R", "4"), or its placeholder;
        /// null when the page has no telemetry fields.</summary>
        public string GearText { get; }

        /// <summary>The center-zone speed string ("268"), or its placeholder; null when
        /// the page has no telemetry fields.</summary>
        public string SpeedText { get; }

        /// <summary>The driver clock's value at composition (same pattern as
        /// <see cref="DisplayRuleSnapshot.ComposedAtMs"/>).</summary>
        public long ComposedAtMs { get; }

        /// <summary>Wall-clock UTC at composition, paired with <see cref="ComposedAtMs"/>
        /// so an arbitrarily-late observer can estimate the snapshot's current age.</summary>
        public DateTime ComposedAtUtc { get; }
    }
}
