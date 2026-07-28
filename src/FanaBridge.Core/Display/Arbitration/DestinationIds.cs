using System;
using FanaBridge.Display.Schema2;

namespace FanaBridge.Display.Arbitration
{
    /// <summary>
    /// Stable destination identity strings for the seat surface. E4 owns destination
    /// identity (same-destination handoff, cycle anchor, dismissal scope).
    /// </summary>
    public static class DestinationIds
    {
        public const string RestInSession = "rest:inSession";
        public const string RestIdle = "rest:idle";
        public const string ManualPrefix = "manual:";

        public static string Itm(string catalogPageId)
            => "itm:" + (catalogPageId ?? "");

        public static string Hosted(string pageId)
            => "hosted:" + (pageId ?? "");

        public static string Cycle(string cycleId)
            => "cycle:" + (cycleId ?? "");

        /// <summary>Build from a schema <see cref="PageRef"/> (itm / hosted / cycle).</summary>
        public static string FromPageRef(PageRef pageRef)
        {
            if (pageRef == null)
                return null;
            switch (pageRef.Kind)
            {
                case PageRefKind.ItmPage:
                    return string.IsNullOrEmpty(pageRef.CatalogPageId) ? null : Itm(pageRef.CatalogPageId);
                case PageRefKind.HostedPage:
                    return string.IsNullOrEmpty(pageRef.Id) ? null : Hosted(pageRef.Id);
                case PageRefKind.Cycle:
                    return string.IsNullOrEmpty(pageRef.Id) ? null : Cycle(pageRef.Id);
                default:
                    return null;
            }
        }

        public static bool IsCycle(string destinationId)
            => destinationId != null
               && destinationId.StartsWith("cycle:", StringComparison.Ordinal);

        public static string CycleId(string destinationId)
            => IsCycle(destinationId) ? destinationId.Substring("cycle:".Length) : null;

        public static bool IsRest(string destinationId)
            => destinationId == RestInSession || destinationId == RestIdle;
    }
}
