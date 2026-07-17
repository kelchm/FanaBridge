using System;
using System.Collections.Generic;
using FanaBridge.Display.Rules;

namespace FanaBridge.Display.Runtime
{
    /// <summary>One rule's row in the UI snapshot: identity, display label, live status.</summary>
    public struct DisplayRuleRow
    {
        public DisplayRuleRow(string ruleId, string label, RuleStatus status, int? remainingMs)
        {
            RuleId = ruleId;
            Label = label;
            Status = status;
            RemainingMs = remainingMs;
        }

        public string RuleId { get; }

        /// <summary>Display text (<see cref="DisplayRuleFormatter.Label"/>).</summary>
        public string Label { get; }

        public RuleStatus Status { get; }

        /// <summary>Hold countdown at composition time (OnScreen + ForDuration only).</summary>
        public int? RemainingMs { get; }
    }

    /// <summary>
    /// An immutable cross-thread snapshot of the rule stack's live state, published
    /// through a volatile field on the device instance and polled by the (future) UI —
    /// the same hand-off pattern as the ITM status snapshot, kept separate from it.
    /// Recomposed only when something visible changed (activity version, a rule status,
    /// the intent, or — at a bounded 250 ms cadence — a timed hold's countdown), so idle
    /// frames publish nothing new.
    /// </summary>
    public sealed class DisplayRuleSnapshot
    {
        internal DisplayRuleSnapshot(string intentDescription, string basePageName,
            IReadOnlyList<DisplayRuleRow> itmRules, IReadOnlyList<DisplayRuleRow> legacyRules,
            IReadOnlyList<DisplayActivityEvent> activity, long activityVersion,
            long composedAtMs, DateTime composedAtUtc)
        {
            IntentDescription = intentDescription;
            BasePageName = basePageName;
            ItmRules = itmRules;
            LegacyRules = legacyRules;
            Activity = activity;
            ActivityVersion = activityVersion;
            ComposedAtMs = composedAtMs;
            ComposedAtUtc = composedAtUtc;
        }

        /// <summary>What the ITM surface should be showing, in row language
        /// (page name, or "screen 'X'").</summary>
        public string IntentDescription { get; }

        /// <summary>
        /// The display name of the page the stack actually rests on — the stack's own
        /// resolution (<see cref="DisplayRuleStack.BaseWirePage"/>): the config's base page
        /// when set AND offered by this device, else the page at the device's default wire.
        /// The UI's "Always →" row must show THIS while a stack is live, not re-derive it
        /// from settings the stack captured at build time.
        /// </summary>
        public string BasePageName { get; }

        /// <summary>ITM rules in priority order.</summary>
        public IReadOnlyList<DisplayRuleRow> ItmRules { get; }

        /// <summary>Legacy rules in priority order.</summary>
        public IReadOnlyList<DisplayRuleRow> LegacyRules { get; }

        /// <summary>Recent activity, oldest first (both engines merged by time).</summary>
        public IReadOnlyList<DisplayActivityEvent> Activity { get; }

        /// <summary>Combined engine activity version — a cheap "anything new?" check.</summary>
        public long ActivityVersion { get; }

        /// <summary>
        /// The engine clock's value when this snapshot was composed — the same clock
        /// <see cref="DisplayActivityEvent.AtMs"/> is stamped with, so the UI can render
        /// relative ages ("12s ago") as <c>ComposedAtMs - AtMs</c> without ever holding a
        /// clock of its own.
        /// </summary>
        public long ComposedAtMs { get; }

        /// <summary>
        /// Wall-clock UTC at composition, paired with <see cref="ComposedAtMs"/> so a UI
        /// observing this snapshot arbitrarily late can estimate the engine clock's CURRENT
        /// value (<c>ComposedAtMs + (UtcNow - ComposedAtUtc)</c>). Composition is
        /// change-gated, so the latest snapshot can be minutes old when a settings dialog
        /// opens — anchoring ages to first observation instead would understate them all.
        /// </summary>
        public DateTime ComposedAtUtc { get; }
    }
}
