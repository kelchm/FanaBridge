using System.Collections.Generic;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// A base-page resolution: the identity the display effectively rests on, that
    /// identity's on-wire page number for this device, and its display name. The three
    /// travel together so a caller cannot pair a name with a wire the display can never
    /// rest on (see <see cref="ItmPageTable.ResolveBase"/>).
    /// </summary>
    public readonly struct ItmBaseResolution
    {
        public ItmBaseResolution(ItmPage identity, byte wire, string name)
        {
            Identity = identity;
            Wire = wire;
            Name = name;
        }

        /// <summary>The page content the display rests on: the configured base when this
        /// device offers it, else the identity sitting at the device's default wire.</summary>
        public ItmPage Identity { get; }

        /// <summary>That identity's on-wire page number on this device — the effective
        /// default page fed to the driver while a rule stack is live.</summary>
        public byte Wire { get; }

        /// <summary>The effective base page's display name (the "Always →" row text).</summary>
        public string Name { get; }
    }

    /// <summary>
    /// The one place identity ↔ wire ↔ name is resolved for a single ITM display's page set
    /// (from <see cref="ItmDeviceCatalog.PagesFor"/>). Both rule engines, the page director,
    /// and the Display tab's render models read every page mapping — a content identity's
    /// wire number, a wire number's identity, a wire's display name, and the effective base
    /// page — through this table, so a Bentley's renumbered set (no Car Settings) and a
    /// standard six-page set behave identically without each caller re-deriving the maps.
    ///
    /// Reference data only, no SimHub: the table is a per-device view over the catalog and
    /// holds no live state, so building one is cheap and callers may hold or rebuild it
    /// freely (a stack builds one at construction; a UI render builds one per draw).
    /// </summary>
    public sealed class ItmPageTable
    {
        private readonly IReadOnlyList<ItmPageInfo> _pages;
        private readonly Dictionary<ItmPage, byte> _wireByPage = new Dictionary<ItmPage, byte>();
        private readonly Dictionary<byte, ItmPageInfo> _infoByWire = new Dictionary<byte, ItmPageInfo>();
        private readonly byte _legacyWire;

        /// <summary>Wraps a device's page set. The list is the catalog's shared/immutable
        /// one — do not mutate it.</summary>
        public ItmPageTable(IReadOnlyList<ItmPageInfo> pages)
        {
            _pages = pages;
            foreach (var info in pages)
            {
                _wireByPage[info.Page] = info.Number;
                _infoByWire[info.Number] = info;
                if (info.IsLegacy)
                    _legacyWire = info.Number;
            }
        }

        /// <summary>The table for a wire device id (unknown ids fall back to the standard
        /// set, per <see cref="ItmDeviceCatalog.PagesFor"/>).</summary>
        public static ItmPageTable ForDevice(byte deviceId)
            => new ItmPageTable(ItmDeviceCatalog.PagesFor(deviceId));

        /// <summary>The device's pages, in wire order (the catalog's shared list).</summary>
        public IReadOnlyList<ItmPageInfo> Pages => _pages;

        /// <summary>The legacy page's wire number, or 0 when this display has none.</summary>
        public byte LegacyWire => _legacyWire;

        /// <summary>True when this device offers the given page content.</summary>
        public bool Offers(ItmPage page) => _wireByPage.ContainsKey(page);

        /// <summary>The wire number a page identity sits at on this device (found ⇒ true).</summary>
        public bool TryGetWire(ItmPage page, out byte wire) => _wireByPage.TryGetValue(page, out wire);

        /// <summary>The wire number a page identity sits at on this device, or
        /// <paramref name="fallback"/> when the device doesn't have the page.</summary>
        public byte WireFor(ItmPage page, byte fallback)
            => _wireByPage.TryGetValue(page, out var wire) ? wire : fallback;

        /// <summary>The identity sitting at a wire page number (found ⇒ true). A wire this
        /// device doesn't have leaves <paramref name="page"/> at its default.</summary>
        public bool TryGetPage(byte wire, out ItmPage page)
        {
            if (_infoByWire.TryGetValue(wire, out var info))
            {
                page = info.Page;
                return true;
            }
            page = default;
            return false;
        }

        /// <summary>The identity at a wire page number, or <see cref="ItmPage.LapInfo"/>
        /// for an out-of-table wire (a misconfigured default-page setting).</summary>
        public ItmPage PageAtWire(byte wire)
            => _infoByWire.TryGetValue(wire, out var info) ? info.Page : ItmPage.LapInfo;

        /// <summary>The display name at a wire page number. A wire outside the table (a
        /// pinned page is unavailable AND the default wire is itself off-table) is named
        /// honestly by number.</summary>
        public string NameAtWire(byte wire)
            => _infoByWire.TryGetValue(wire, out var info) ? info.Name : "Page " + wire;

        /// <summary>
        /// The effective base page: the configured base identity when this device offers it,
        /// else the identity sitting at <paramref name="defaultWirePage"/> — resolved to one
        /// coherent (identity, wire, name) triple so the "Always →" row, the driver hand-off,
        /// and the engine's resting target all speak of the same page. Pass a null
        /// <paramref name="configuredBase"/> when the config pins none (the default wire's
        /// identity is used); a base page this device lacks keeps the default wire.
        /// </summary>
        public ItmBaseResolution ResolveBase(ItmPage? configuredBase, byte defaultWirePage)
        {
            // The requested base: the config's own when set, else the identity at the
            // device's default wire (an off-table default wire falls to Lap Info).
            ItmPage requested = configuredBase ?? PageAtWire(defaultWirePage);
            // Its wire, or the default wire when this device doesn't have the requested page.
            byte wire = WireFor(requested, defaultWirePage);
            // Re-anchor to the identity actually sitting at that wire, and to THAT identity's
            // own wire: in the compound corner — a configured base this device lacks AND an
            // off-table default wire (e.g. a page number carried over from a different wheel)
            // — the wire above is the off-table number, but the resolved identity is Lap Info,
            // whose real wire and name are what the driver hand-off and the "Always →" row must
            // agree on. Anchoring keeps (identity, wire, name) one coherent page.
            ItmPage identity = PageAtWire(wire);
            byte anchored = WireFor(identity, wire);
            return new ItmBaseResolution(identity, anchored, NameAtWire(anchored));
        }
    }
}
