using FanaBridge.Protocol;

namespace FanaBridge.Display.Rules
{
    /// <summary>
    /// Honest current-page knowledge from the director's sync bookkeeping.
    /// The runtime never invents a page: connect-before-first-announcement is
    /// <see cref="Unknown"/> until a Synced observation lands.
    /// </summary>
    public readonly struct CurrentPageKnowledge
    {
        private CurrentPageKnowledge(bool isKnown, byte? wirePage, ItmPage? page)
        {
            IsKnown = isKnown;
            WirePage = wirePage;
            Page = page;
        }

        /// <summary>True when the lifecycle has reported a known wire page since cold.</summary>
        public bool IsKnown { get; }

        /// <summary>Wire page when known and the controller reported one; null when
        /// known-as-uncataloged (Synced with null page) or unknown.</summary>
        public byte? WirePage { get; }

        /// <summary>Catalog identity when the wire page maps; null for uncataloged /
        /// unknown.</summary>
        public ItmPage? Page { get; }

        /// <summary>No baseline yet (cold, connect, pre-first-announcement).</summary>
        public static CurrentPageKnowledge Unknown { get; } =
            new CurrentPageKnowledge(false, null, null);

        /// <summary>Known wire page, optionally resolved to a catalog identity.</summary>
        public static CurrentPageKnowledge Known(byte wirePage, ItmPage? page)
            => new CurrentPageKnowledge(true, wirePage, page);

        /// <summary>Synced on an uncataloged parameter set (page identity unknown).</summary>
        public static CurrentPageKnowledge KnownUncataloged { get; } =
            new CurrentPageKnowledge(true, null, null);
    }
}
