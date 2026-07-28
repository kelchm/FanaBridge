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

        // ── Surface keys (shared by E4/E5 merge — one spelling, one normalize) ─

        /// <summary>Surface key for a hosted page's layer ladder: <c>page:{id}</c>.</summary>
        public static string PageSurface(string hostedPageId)
            => "page:" + (hostedPageId ?? "");

        /// <summary>Surface key for a field override ladder: <c>field:{paramId}</c>.</summary>
        public static string FieldSurface(ushort paramId)
            => "field:" + paramId;

        /// <summary>
        /// Surface key from a raw field key / childRef.Field string. Normalizes via
        /// <see cref="ushort.TryParse"/> so <c>"05"</c> / <c>" 5"</c> collapse to
        /// <c>field:5</c> (same as the ushort overload) — merge-safe with E5.
        /// </summary>
        public static string FieldSurface(string fieldKey)
        {
            if (fieldKey != null
                && ushort.TryParse(fieldKey.Trim(), out var paramId))
                return FieldSurface(paramId);
            return "field:" + (fieldKey ?? "");
        }

        // ── Wheel-screen surface (E6) ────────────────────────────────────

        /// <summary>
        /// Surface key for the concurrent firmware-screen plane. One spelling for
        /// merge with E4/E5 (contract §6.1) — E6 is the sole presence owner.
        /// </summary>
        public const string WheelScreenSurfaceId = "wheelScreen";

        /// <summary>Surface key for the wheel-screen plane: <c>wheelScreen</c>.</summary>
        public static string WheelScreenSurface()
            => WheelScreenSurfaceId;

        /// <summary>Destination identity for a firmware screen command: <c>screen:{spelling}</c>.</summary>
        public static string Screen(string commandSpelling)
            => "screen:" + (commandSpelling ?? "");
    }
}
