using System;
using System.Collections.Generic;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Schema2;

namespace FanaBridge.Display.Arbitration
{
    /// <summary>
    /// Pure walk compilation (phase E7a). Clock-free; no session state.
    ///
    /// From a NORMALIZED <see cref="DisplayConfigV2"/> plus the device
    /// <see cref="WheelCatalog"/>, produces the ordered destination-id list that
    /// the seat surface consumes as <see cref="SeatArbiterTickInput.CompiledWalk"/>
    /// (E4 seam: StepWalk applies the list; manual row remembers the resolved target;
    /// director adopt/reject outputs feed the <b>next</b> tick per contract §6.2).
    ///
    /// <b>§5b compile laws</b> (membership = presence; order = array order; wraps):
    /// <list type="bullet">
    /// <item>Explicit <c>pageOrder</c>: validated itmPage/hostedPage refs only.
    /// Degraded / unresolved / cycle refs are SKIPPED; duplicates keep the first;
    /// removed ITM pages are excluded even when still listed.</item>
    /// <item>ABSENT <c>pageOrder</c> (<c>null</c>): catalog pages in catalog-index
    /// order excluding <c>removed: true</c>, then hosted pages in <c>pages[]</c>
    /// order.</item>
    /// <item>EMPTY <c>pageOrder</c> (<c>[]</c>): empty walk (distinct from absent).</item>
    /// </list>
    ///
    /// <b>Recompile contract</b>: recompile on <b>config edges</b> and
    /// <b>capability-envelope edges</b> only (a catalog swap is a capability edge).
    /// Same logical inputs always yield the same ordered destination list —
    /// list-instance equality is irrelevant; content equality is the contract.
    /// Do not recompile on session, press, clock, or tick edges.
    ///
    /// <b>Step-from-outside rule</b> (pinned): when
    /// <paramref name="currentDestinationId"/> is not a walk member, re-entry is
    /// <b>direction-aware</b> and <b>nearest-index</b> (landing <b>is</b> the step —
    /// direction is not applied a second time):
    /// <list type="bullet">
    /// <item>ITM current with a known catalog index, NEXT: the walk ITM member with
    /// the <b>minimum</b> catalog index strictly greater than current; when none
    /// greater, wrap to the walk's <b>lowest-index</b> ITM member, else
    /// <c>walk[0]</c>.</item>
    /// <item>PREV is symmetric: <b>maximum</b> catalog index strictly lesser; wrap
    /// to the walk's <b>highest-index</b> ITM member, else <c>walk[last]</c>.</item>
    /// <item>Equal catalog indexes: authored walk order is the deterministic
    /// tie-breaker (earlier member wins).</item>
    /// <item>Hosted / unknown / null-catalog current: <c>walk[0]</c> (NEXT) /
    /// <c>walk[last]</c> (PREV).</item>
    /// </list>
    /// An EMPTY walk steps nowhere (<c>null</c> + reason).
    /// </summary>
    public static class WalkCompiler
    {
        public const string EmptyWalkReason = "empty walk";

        /// <summary>
        /// Compile the walk per §5b. <paramref name="config"/> must already be
        /// normalized (<see cref="DisplayConfigV2Validator.Normalize"/>); the compiler
        /// still enforces membership laws so stale/degraded entries never join.
        /// </summary>
        public static CompiledWalk Compile(DisplayConfigV2 config, WheelCatalog catalog)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            var removedItm = CollectRemovedItmIds(config);
            var hostedIds = CollectHostedIds(config);
            var catalogIndexById = BuildCatalogIndexMap(catalog);

            if (config.PageOrder == null)
            {
                return new CompiledWalk(
                    BuildDefaultWalk(config, catalog, removedItm, catalogIndexById),
                    WalkCompileSource.Default);
            }

            if (config.PageOrder.Count == 0)
                return new CompiledWalk(Array.Empty<string>(), WalkCompileSource.Empty);

            return new CompiledWalk(
                BuildExplicitWalk(config.PageOrder, removedItm, hostedIds, catalogIndexById, catalog),
                WalkCompileSource.Explicit);
        }

        /// <summary>
        /// Step one place along <paramref name="walk"/> with wrap.
        /// See type doc comment for the step-from-outside rule and empty-walk law.
        /// </summary>
        /// <param name="walk">Compiled destination ids (may be null/empty).</param>
        /// <param name="currentDestinationId">Current page destination (may be off-walk).</param>
        /// <param name="direction">+1 next, −1 previous (sign only; magnitude ignored).</param>
        /// <param name="catalog">
        /// Optional catalog for the ITM catalog-index half of the outside rule.
        /// Null catalog → hosted/unknown path: <c>walk[0]</c> (NEXT) /
        /// <c>walk[last]</c> (PREV).
        /// </param>
        public static WalkStepResult Step(
            IReadOnlyList<string> walk,
            string currentDestinationId,
            int direction,
            WheelCatalog catalog = null)
        {
            if (walk == null || walk.Count == 0)
                return WalkStepResult.Nowhere(EmptyWalkReason);

            int idx = IndexOf(walk, currentDestinationId);
            if (idx >= 0)
            {
                int step = direction < 0 ? -1 : 1;
                if (direction == 0)
                    return WalkStepResult.Landed(walk[idx]);
                int next = ((idx + step) % walk.Count + walk.Count) % walk.Count;
                return WalkStepResult.Landed(walk[next]);
            }

            // Outside the walk: direction-aware nearest-index re-entry (type doc).
            string entry = DirectionAwareReentry(
                walk, currentDestinationId, direction, catalog);
            return WalkStepResult.Landed(entry);
        }

        // ── Compile paths ────────────────────────────────────────────────

        private static IReadOnlyList<string> BuildDefaultWalk(
            DisplayConfigV2 config,
            WheelCatalog catalog,
            HashSet<string> removedItm,
            Dictionary<string, int> catalogIndexById)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Catalog pages in index order (stable: index, then catalog list order).
            if (catalog?.Itm?.Pages != null)
            {
                var ordered = new List<CatalogPage>(catalog.Itm.Pages.Count);
                foreach (var p in catalog.Itm.Pages)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.Id))
                        continue;
                    ordered.Add(p);
                }
                ordered.Sort(CompareCatalogPage);

                foreach (var p in ordered)
                {
                    if (removedItm.Contains(p.Id))
                        continue;
                    string dest = DestinationIds.Itm(p.Id);
                    if (!seen.Add(dest))
                        continue;
                    result.Add(dest);
                }
            }
            else
            {
                // No catalog roster: nothing to add from ITM side.
                _ = catalogIndexById;
            }

            // Hosted pages in pages[] order (identity losers / blank ids skipped).
            if (config.Pages != null)
            {
                foreach (var page in config.Pages)
                {
                    if (page == null || page.Kind != PageEntryKind.HostedPage)
                        continue;
                    if (page.DegradedAtLoad || string.IsNullOrWhiteSpace(page.Id))
                        continue;
                    string dest = DestinationIds.Hosted(page.Id);
                    if (!seen.Add(dest))
                        continue;
                    result.Add(dest);
                }
            }

            return result;
        }

        private static IReadOnlyList<string> BuildExplicitWalk(
            List<PageRef> pageOrder,
            HashSet<string> removedItm,
            HashSet<string> hostedIds,
            Dictionary<string, int> catalogIndexById,
            WheelCatalog catalog)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in pageOrder)
            {
                if (entry == null || entry.DegradedAtLoad)
                    continue;

                // Cycles / unknown kinds are never walkable members.
                if (entry.Kind != PageRefKind.ItmPage && entry.Kind != PageRefKind.HostedPage)
                    continue;

                if (entry.Kind == PageRefKind.ItmPage)
                {
                    if (string.IsNullOrWhiteSpace(entry.CatalogPageId))
                        continue;
                    // removed:true ⇒ non-member even if still listed.
                    if (removedItm.Contains(entry.CatalogPageId))
                        continue;
                    // Unresolved: not on the catalog roster when a catalog is supplied.
                    if (catalog != null
                        && catalog.Itm?.Pages != null
                        && !catalogIndexById.ContainsKey(entry.CatalogPageId))
                        continue;
                }
                else // HostedPage
                {
                    if (string.IsNullOrWhiteSpace(entry.Id))
                        continue;
                    // Unresolved: not present in pages[].
                    if (!hostedIds.Contains(entry.Id))
                        continue;
                }

                string dest = DestinationIds.FromPageRef(entry);
                if (string.IsNullOrEmpty(dest))
                    continue;
                // Duplicates: first occurrence kept.
                if (!seen.Add(dest))
                    continue;
                result.Add(dest);
            }

            return result;
        }

        // ── Step helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Off-walk re-entry: NEXT = min catalog index &gt; current (wrap lowest ITM
        /// / walk[0]); PREV = max catalog index &lt; current (wrap highest ITM /
        /// walk[last]). Authored order breaks equal-index ties. Hosted/unknown →
        /// walk[0] / walk[last]. Landing consumes the direction (no second step).
        /// </summary>
        private static string DirectionAwareReentry(
            IReadOnlyList<string> walk,
            string currentDestinationId,
            int direction,
            WheelCatalog catalog)
        {
            bool goingNext = direction >= 0;

            if (TryGetItmCatalogPageId(currentDestinationId, out string currentItmId)
                && catalog?.Itm?.Pages != null)
            {
                var indexMap = BuildCatalogIndexMap(catalog);
                if (indexMap.TryGetValue(currentItmId, out int currentIndex))
                {
                    string bestDest = null;
                    int bestIndex = goingNext ? int.MaxValue : int.MinValue;
                    int bestAuthored = int.MaxValue;

                    string wrapDest = null;
                    int wrapIndex = goingNext ? int.MaxValue : int.MinValue;
                    int wrapAuthored = int.MaxValue;

                    for (int i = 0; i < walk.Count; i++)
                    {
                        if (!TryGetItmCatalogPageId(walk[i], out string memberItmId))
                            continue;
                        if (!indexMap.TryGetValue(memberItmId, out int memberIndex))
                            continue;

                        // Wrap candidate among all ITM walk members.
                        if (goingNext
                            ? memberIndex < wrapIndex
                              || (memberIndex == wrapIndex && i < wrapAuthored)
                            : memberIndex > wrapIndex
                              || (memberIndex == wrapIndex && i < wrapAuthored))
                        {
                            wrapIndex = memberIndex;
                            wrapDest = walk[i];
                            wrapAuthored = i;
                        }

                        // Eligible: strictly greater (NEXT) or strictly lesser (PREV).
                        if (goingNext)
                        {
                            if (memberIndex <= currentIndex)
                                continue;
                            if (memberIndex < bestIndex
                                || (memberIndex == bestIndex && i < bestAuthored))
                            {
                                bestIndex = memberIndex;
                                bestDest = walk[i];
                                bestAuthored = i;
                            }
                        }
                        else
                        {
                            if (memberIndex >= currentIndex)
                                continue;
                            if (memberIndex > bestIndex
                                || (memberIndex == bestIndex && i < bestAuthored))
                            {
                                bestIndex = memberIndex;
                                bestDest = walk[i];
                                bestAuthored = i;
                            }
                        }
                    }

                    if (bestDest != null)
                        return bestDest;
                    if (wrapDest != null)
                        return wrapDest;
                    // No ITM members in walk → fall through to hosted/unknown path.
                }
            }

            return goingNext ? walk[0] : walk[walk.Count - 1];
        }

        private static int IndexOf(IReadOnlyList<string> walk, string destinationId)
        {
            if (destinationId == null)
                return -1;
            for (int i = 0; i < walk.Count; i++)
            {
                if (string.Equals(walk[i], destinationId, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        private static bool TryGetItmCatalogPageId(string destinationId, out string catalogPageId)
        {
            catalogPageId = null;
            if (destinationId == null
                || !destinationId.StartsWith("itm:", StringComparison.Ordinal)
                || destinationId.Length <= 4)
                return false;
            catalogPageId = destinationId.Substring(4);
            return catalogPageId.Length > 0;
        }

        // ── Config / catalog indexes ─────────────────────────────────────

        private static HashSet<string> CollectRemovedItmIds(DisplayConfigV2 config)
        {
            // Case-insensitive match on catalog page ids (validator parity).
            // First-wins identity: degraded / later-duplicate overlays are ignored so
            // a removed:true loser cannot exclude a kept first-wins ITM page (E7A-004).
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (config.Pages == null)
                return set;
            foreach (var page in config.Pages)
            {
                if (page == null || page.Kind != PageEntryKind.ItmPage)
                    continue;
                if (page.DegradedAtLoad || string.IsNullOrWhiteSpace(page.CatalogPageId))
                    continue;
                if (!seen.Add(page.CatalogPageId))
                    continue;
                if (page.Removed)
                    set.Add(page.CatalogPageId);
            }
            return set;
        }

        private static HashSet<string> CollectHostedIds(DisplayConfigV2 config)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (config.Pages == null)
                return set;
            foreach (var page in config.Pages)
            {
                if (page == null || page.Kind != PageEntryKind.HostedPage)
                    continue;
                if (page.DegradedAtLoad || string.IsNullOrWhiteSpace(page.Id))
                    continue;
                set.Add(page.Id);
            }
            return set;
        }

        /// <summary>
        /// catalogPageId → on-wire index. First occurrence wins when the draft lists
        /// a page twice (should not happen; defensive).
        /// </summary>
        private static Dictionary<string, int> BuildCatalogIndexMap(WheelCatalog catalog)
        {
            // OrdinalIgnoreCase keys so explicit refs match the roster regardless of case;
            // DestinationIds still emit the ref/catalog spelling as written.
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (catalog?.Itm?.Pages == null)
                return map;
            foreach (var p in catalog.Itm.Pages)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Id))
                    continue;
                if (!map.ContainsKey(p.Id))
                    map[p.Id] = p.Index;
            }
            return map;
        }

        private static int CompareCatalogPage(CatalogPage a, CatalogPage b)
        {
            int c = a.Index.CompareTo(b.Index);
            if (c != 0)
                return c;
            // Stable secondary: id ordinal (deterministic when indexes collide).
            return string.CompareOrdinal(a.Id, b.Id);
        }
    }

    /// <summary>Where a compiled walk came from (§5b absent / empty / explicit).</summary>
    public enum WalkCompileSource
    {
        /// <summary><c>pageOrder</c> was absent — default catalog + hosted order.</summary>
        Default = 0,
        /// <summary><c>pageOrder</c> was an explicit (possibly filtered) list.</summary>
        Explicit,
        /// <summary><c>pageOrder</c> was <c>[]</c> — empty walk.</summary>
        Empty,
    }

    /// <summary>Immutable result of <see cref="WalkCompiler.Compile"/>.</summary>
    public sealed class CompiledWalk
    {
        public CompiledWalk(IReadOnlyList<string> destinationIds, WalkCompileSource source)
        {
            DestinationIds = destinationIds ?? Array.Empty<string>();
            Source = source;
        }

        /// <summary>Ordered walk members as destination ids (<c>itm:…</c> / <c>hosted:…</c>).</summary>
        public IReadOnlyList<string> DestinationIds { get; }

        /// <summary>Whether the walk was default / explicit / empty.</summary>
        public WalkCompileSource Source { get; }

        public bool IsEmpty => DestinationIds.Count == 0;

        public int Count => DestinationIds.Count;
    }

    /// <summary>Outcome of <see cref="WalkCompiler.Step"/>.</summary>
    public readonly struct WalkStepResult
    {
        private WalkStepResult(string destinationId, string emptyReason)
        {
            DestinationId = destinationId;
            EmptyReason = emptyReason;
        }

        /// <summary>Next destination, or null when the walk is empty.</summary>
        public string DestinationId { get; }

        /// <summary>Reason when <see cref="DestinationId"/> is null (empty walk).</summary>
        public string EmptyReason { get; }

        public bool Stepped => DestinationId != null;

        public static WalkStepResult Landed(string destinationId)
            => new WalkStepResult(destinationId, null);

        public static WalkStepResult Nowhere(string reason)
            => new WalkStepResult(null, reason ?? WalkCompiler.EmptyWalkReason);
    }
}
