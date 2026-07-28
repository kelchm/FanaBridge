using FanaBridge.Protocol;

namespace FanaBridge.Display.Arbitration
{
    /// <summary>
    /// Director input kind — content-plane identity the page director resolves to a wire
    /// page. Mirrors the v9 RuleIntent kind subset the director actually branches on
    /// (Page / SegmentScreen / Special); scaffolding through E8b still constructs this
    /// from RuleIntent at the composition edge.
    /// </summary>
    public enum DirectorIntentKind
    {
        /// <summary>An ITM page (or any non-segment/non-special intent).</summary>
        Page = 0,
        /// <summary>A named segment-display screen on the device's legacy page.</summary>
        SegmentScreen,
        /// <summary>A firmware special command — director does not page-navigate.</summary>
        Special,
    }

    /// <summary>
    /// v2-owned director input: content identities the page director turns into lifecycle
    /// requests. Decision logic is identical to the former RuleIntent parameter bundle.
    /// </summary>
    public readonly struct DirectorIntent
    {
        public DirectorIntent(DirectorIntentKind kind, ItmPage? page, string screenId,
            string sourceRuleId)
        {
            Kind = kind;
            Page = page;
            ScreenId = screenId;
            SourceRuleId = sourceRuleId;
        }

        /// <summary><see cref="DirectorIntentKind.Page"/>, <see cref="DirectorIntentKind.SegmentScreen"/>,
        /// or <see cref="DirectorIntentKind.Special"/>.</summary>
        public DirectorIntentKind Kind { get; }

        /// <summary>The page to show, for <see cref="DirectorIntentKind.Page"/>.</summary>
        public ItmPage? Page { get; }

        /// <summary>The screen to show, for <see cref="DirectorIntentKind.SegmentScreen"/>.</summary>
        public string ScreenId { get; }

        /// <summary>The rule whose target this is, or null for the resting/base target.</summary>
        public string SourceRuleId { get; }
    }
}
