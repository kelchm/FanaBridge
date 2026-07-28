using System;
using FanaBridge.Display.Schema2;

namespace FanaBridge.Display.Arbitration
{
    /// <summary>
    /// Engine seed for the Legacy plane's remembered page (spec §14 two-step law after
    /// FREEZE AMENDMENT 3 / FA3). The remembered page itself is runtime state and is not
    /// resolved here. This type produces only the SEED: the first non-degraded hosted page
    /// in the compiled walk (<see cref="WalkCompiler"/> is the single ordering authority —
    /// strip / <c>pageOrder</c> semantics, not a duplicated walk). <c>rest.inSessionPage</c>
    /// plays no role. Terminal null = zero hosted pages in the walk → col01 silence at
    /// in-game bring-up (E8 lead ruling; rest.idle Blank is separate).
    /// Note: empty <c>pageOrder: []</c> yields null even when hosted pages exist (empty
    /// walk has no first member) — a widening of the lead "zero hosted pages" ruling that
    /// lives here and in <c>EmptyPageOrder_ReturnsNull_EvenWithHostedPages</c>.
    /// </summary>
    public static class LegacySeedResolver
    {
        /// <summary>
        /// Resolve the seed destination id (<c>hosted:…</c>) or null when nothing
        /// resolves. Delegates entirely to <see cref="WalkCompiler.Compile"/> with a null
        /// catalog (null catalog contributes no ITM members; hosted membership/order stays
        /// the compiler's sole authority, including OrdinalIgnoreCase id comparison).
        /// </summary>
        public static string ResolveSeedDestination(DisplayConfigV2 config)
        {
            if (config == null)
                return null;

            // Single ordering authority: first hosted member of the compiled walk.
            var walk = WalkCompiler.Compile(config, catalog: null);
            for (int i = 0; i < walk.DestinationIds.Count; i++)
            {
                string dest = walk.DestinationIds[i];
                if (dest != null
                    && dest.StartsWith("hosted:", StringComparison.Ordinal))
                    return dest;
            }

            return null;
        }

        /// <summary>
        /// Hosted page id (no <c>hosted:</c> prefix) for the segment-plane seed, or
        /// null when the chain terminates empty.
        /// </summary>
        public static string ResolveSeedHostedPageId(DisplayConfigV2 config)
        {
            string dest = ResolveSeedDestination(config);
            if (dest == null
                || !dest.StartsWith("hosted:", StringComparison.Ordinal)
                || dest.Length <= "hosted:".Length)
                return null;
            return dest.Substring("hosted:".Length);
        }
    }
}
