namespace FanaBridge.Display
{
    /// <summary>What happened, for the activity log's row language.</summary>
    public enum ActivityKind
    {
        /// <summary>A rule's condition fired and created an activation.</summary>
        RuleFired,
        /// <summary>The emitted intent went back to the resting/base target.</summary>
        ReturnedToBase,
        /// <summary>A wheel-button page change was adopted (the engine stood down).</summary>
        ManualNavigation,
        /// <summary>The on-screen rule's activation ended (hold expiry or dismissal).</summary>
        RuleExpired,
    }

    /// <summary>
    /// One entry in the engine's bounded activity ring — the "Recent Activity" feed. Text
    /// is pre-built human-readable row language (see <see cref="DisplayRuleFormatter"/>) so
    /// the UI renders entries without re-resolving rules that may since have been edited away.
    /// </summary>
    public struct DisplayActivityEvent
    {
        public DisplayActivityEvent(long atMs, ActivityKind kind, string text, string ruleId)
        {
            AtMs = atMs;
            Kind = kind;
            Text = text;
            RuleId = ruleId;
        }

        /// <summary>Engine-clock timestamp of the event.</summary>
        public long AtMs { get; }

        public ActivityKind Kind { get; }

        /// <summary>Human-readable row text, e.g. "Fuel &lt; 10 → Fuel / ERS / DRS".</summary>
        public string Text { get; }

        /// <summary>The rule involved, or null for events with no rule (manual navigation,
        /// return to base).</summary>
        public string RuleId { get; }
    }
}
