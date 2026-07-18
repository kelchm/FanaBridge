using FanaBridge.Display.Rules;

namespace FanaBridge.UI.Display.Shared
{
    /// <summary>
    /// The two modes the shared trigger table serves. <see cref="Workbench"/> is the
    /// expand-to-edit editor (the Triggers screen); <see cref="Monitor"/> is the read-only
    /// "what's in play" list with the live Now column (the Overview). The enum exists from
    /// Phase 2 commit 2a so the control's surface is stable, but 2a only wires
    /// <see cref="Workbench"/> (the extracted Triggers editor); Monitor lands in 2c.
    /// </summary>
    internal enum TriggerTableMode
    {
        Monitor,
        Workbench,
    }

    /// <summary>One row of the shared trigger table, ready for
    /// <see cref="TriggerTableControl"/> to draw: the collapsed row language (rank, label,
    /// live-state chip + countdown, eligibility) plus the affordance flags
    /// (draggable / expandable) and the degraded / base markers. A pure projection — the
    /// producer (e.g. <see cref="DisplayTriggersEditModel"/>) fills it from a config +
    /// snapshot with no WPF involved.</summary>
    internal sealed class TriggerTableRow
    {
        /// <summary>The rule this row edits, or null for the pinned base row.</summary>
        public string RuleId;

        /// <summary>"1".."n" for rules, "★" for the base row.</summary>
        public string Rank;

        /// <summary>Row label (<see cref="DisplayRuleFormatter.Label"/>), or "Always → &lt;base&gt;".
        /// The v9 row header prefers the structured when-fields below; <see cref="Label"/> is the
        /// fallback for the base row, degraded rows, and user-named rules
        /// (<see cref="PropertyName"/> null in those cases).</summary>
        public string Label;

        // ── Structured WHEN (v9 property grammar). Populated only for a non-degraded,
        //    unnamed rule with a source property; null PropertyName means "use Label". ──

        /// <summary>The condition's source property, for <see cref="PropertyGrammar"/>;
        /// null on base / degraded / user-named rows (render <see cref="Label"/> then).</summary>
        public string PropertyName;

        /// <summary>How <see cref="PropertyName"/> namespaces for display.</summary>
        public PropertyDisplayKind DisplayKind;

        /// <summary>The operator glyph ("&gt;", "is on", "changes"), or "".</summary>
        public string Operator = "";

        /// <summary>The comparison value text, or "".</summary>
        public string ValueText = "";

        /// <summary>The SHOW target text ("Fuel / ERS / DRS"), or "".</summary>
        public string TargetText = "";

        /// <summary>Live-state chip ("on screen"/"waiting"/…/"base"), merged from the snapshot by id.</summary>
        public string Chip = "";

        /// <summary>Hold countdown ("4s"), only while on screen with a timed hold.</summary>
        public string Seconds;

        /// <summary>The winning rule — green accent.</summary>
        public bool OnScreen;

        /// <summary>Disabled, ineligible, or degraded — the row renders dimmed.</summary>
        public bool Muted;

        /// <summary>Loaded from a newer version this build can't honor: shown muted with a
        /// "created by a newer version" hint, reorderable and removable but not editable.</summary>
        public bool Degraded;

        /// <summary>The user's own enable toggle (independent of <see cref="Degraded"/>).</summary>
        public bool Enabled;

        /// <summary>Eligibility chip text ("In-game"/"Idle"/"Any time"); empty for base / degraded.</summary>
        public string Eligibility = "";

        /// <summary>The pinned "Always" row — dashed, last, not draggable, not expandable.</summary>
        public bool IsBase;

        /// <summary>Whether the drag handle reorders this row (every rule row; not the base).</summary>
        public bool Draggable;

        /// <summary>Whether the chevron opens an editor (every non-degraded rule row).</summary>
        public bool Expandable;
    }
}
