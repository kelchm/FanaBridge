using System.Globalization;
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

        /// <summary>The SHOW target text ("Fuel / ERS / DRS"), or "". The v9 dense-grid
        /// Show column uses <see cref="ShowText"/> instead; this stays for the inline
        /// "→ target" the old collapsed row appended.</summary>
        public string TargetText = "";

        // ── v9 dense-grid columns (Workbench). Populated by the row producer; the
        //    Monitor/old-look renderers ignore them. ──

        /// <summary>The Show column: "Page N · Name", "P2 ⇄ P5", or "screen 'X'".</summary>
        public string ShowText = "";

        /// <summary>The Timeout column: "While active" / "&lt;n&gt; s" / "Until replaced".</summary>
        public string Timeout = "";

        /// <summary>The Runs column glyph (⚑ / ☾ / ∞ / ⊘), or "".</summary>
        public string RunGlyph = "";

        /// <summary>The Runs column label ("In game" / "Idle" / "Always" / "Disabled"), or "".</summary>
        public string RunLabel = "";

        /// <summary>The State column text for the non-on-screen case ("waiting" / "off" /
        /// "n/a on this wheel" / ""); the on-screen row shows the green dot + "on screen"
        /// + <see cref="Seconds"/> ring instead.</summary>
        public string StateText = "";

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

    /// <summary>
    /// The pure v9 dense-grid column projections (Timeout / State) — no WPF, no config
    /// mutation — so the workbench columns' wording is unit-pinned and single-sourced for
    /// both the Triggers editor rows and (in Monitor mode) the Overview. The Runs mapping
    /// lives with the edit model (<c>DisplayTriggersEditModel.RunGlyph</c>/<c>RunLabel</c>),
    /// which owns the enable/eligibility semantics behind that column.
    /// </summary>
    internal static class TriggerTableModel
    {
        /// <summary>The Timeout column text for a hold: <see cref="HoldKind.WhileActive"/> →
        /// "While active", <see cref="HoldKind.Indefinite"/> → "Until replaced" (serialization
        /// untouched — the display word only), <see cref="HoldKind.ForDuration"/> →
        /// "&lt;seconds&gt; s". An unset/unknown hold reads as "While active" (the level default).</summary>
        public static string TimeoutText(HoldKind kind, int durationMs)
        {
            switch (kind)
            {
                case HoldKind.Indefinite:
                    return "Until replaced";
                case HoldKind.ForDuration:
                    return SecondsText(durationMs) + " s";
                case HoldKind.WhileActive:
                default:
                    return "While active";
            }
        }

        /// <summary>The State column text for the non-on-screen presentation: a disabled rule
        /// reads "off"; otherwise the live status maps to "waiting" / "n/a on this wheel" / ""
        /// (armed and the muted states are blank). The on-screen row draws the green dot +
        /// "on screen" + countdown instead, so this returns "on screen" there for completeness
        /// but the renderer supplies the dot and ring.</summary>
        public static string StateText(RuleStatus status, bool enabled)
        {
            if (!enabled)
                return "off";
            switch (status)
            {
                case RuleStatus.OnScreen: return "on screen";
                case RuleStatus.Waiting: return "waiting";
                case RuleStatus.Unavailable: return "n/a on this wheel";
                default: return "";
            }
        }

        private static string SecondsText(int durationMs)
            => (durationMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
    }
}
