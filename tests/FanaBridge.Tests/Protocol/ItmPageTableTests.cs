using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests.Protocol
{
    /// <summary>
    /// The one page-mapping table (<see cref="ItmPageTable"/>): identity ↔ wire round-trips,
    /// wire → name (with the off-table fallback), the per-device legacy wire, and the
    /// effective-base resolution the "Always →" row and the driver hand-off share — across
    /// the standard six-page set (device 3) and the Bentley's renumbered five (device 4).
    /// </summary>
    public class ItmPageTableTests
    {
        private static ItmPageTable Standard => ItmPageTable.ForDevice(3);
        private static ItmPageTable Bentley => ItmPageTable.ForDevice(4);

        // ── identity ↔ wire ──────────────────────────────────────────────

        [Fact]
        public void IdentityWire_RoundTrips_ForEverySlot_Standard()
        {
            var t = Standard;
            foreach (var info in t.Pages)
            {
                Assert.True(t.TryGetWire(info.Page, out var wire));
                Assert.Equal(info.Number, wire);
                Assert.True(t.TryGetPage(info.Number, out var page));
                Assert.Equal(info.Page, page);
            }

            // The well-known standard slots (Car Settings at wire 3, Tyre Temps at 5).
            Assert.True(t.TryGetWire(ItmPage.CarSettings, out var cs));
            Assert.Equal(3, cs);
            Assert.Equal(5, t.WireFor(ItmPage.TyreTemps, fallback: 0));
        }

        [Fact]
        public void Bentley_Renumbers_AndDropsCarSettings()
        {
            var t = Bentley;

            Assert.False(t.Offers(ItmPage.CarSettings));
            Assert.False(t.TryGetWire(ItmPage.CarSettings, out var missing));
            Assert.Equal(0, missing);                 // out is 0 on a miss
            Assert.Equal(9, t.WireFor(ItmPage.CarSettings, fallback: 9));

            // The remaining pages renumber to a contiguous 1–5: Lap Times is wire 3,
            // Tyre Temps wire 4 (they are 4 and 5 on the standard set).
            Assert.Equal(3, t.WireFor(ItmPage.LapTimes, fallback: 0));
            Assert.Equal(4, t.WireFor(ItmPage.TyreTemps, fallback: 0));
        }

        [Fact]
        public void Offers_TracksTheDeviceSet()
        {
            Assert.True(Standard.Offers(ItmPage.CarSettings));
            Assert.False(Bentley.Offers(ItmPage.CarSettings));
            Assert.True(Bentley.Offers(ItmPage.LapInfo));
        }

        [Fact]
        public void UnknownDevice_FallsBackToStandard()
        {
            var t = ItmPageTable.ForDevice(99);
            Assert.True(t.Offers(ItmPage.CarSettings));
            Assert.Equal(6, t.LegacyWire);
        }

        // ── wire → page / name, off-table fallbacks ──────────────────────

        [Fact]
        public void PageAtWire_And_NameAtWire_Standard()
        {
            var t = Standard;
            Assert.Equal(ItmPage.FuelErsDrs, t.PageAtWire(2));
            Assert.Equal("Fuel / ERS / DRS", t.NameAtWire(2));
            Assert.Equal("Tire Temps", t.NameAtWire(5));
        }

        [Fact]
        public void OffTableWire_PageFallsToLapInfo_NameReadsByNumber()
        {
            var t = Standard;
            Assert.False(t.TryGetPage(99, out _));
            Assert.Equal(ItmPage.LapInfo, t.PageAtWire(99));   // WireToPage fallback
            Assert.Equal("Page 99", t.NameAtWire(99));         // named honestly by number
        }

        [Fact]
        public void LegacyWire_IsPerDevice()
        {
            Assert.Equal(6, Standard.LegacyWire);   // standard six-page: legacy is wire 6
            Assert.Equal(5, Bentley.LegacyWire);    // Bentley: legacy renumbers to wire 5
        }

        // ── effective base resolution ────────────────────────────────────

        [Fact]
        public void ResolveBase_ConfiguredBaseOffered_WinsWithItsWireAndName()
        {
            var r = Standard.ResolveBase(ItmPage.TyreTemps, defaultWirePage: 1);
            Assert.Equal(ItmPage.TyreTemps, r.Identity);
            Assert.Equal(5, r.Wire);
            Assert.Equal("Tire Temps", r.Name);
        }

        [Fact]
        public void ResolveBase_NoConfiguredBase_UsesTheDefaultWiresIdentity()
        {
            var r = Standard.ResolveBase(null, defaultWirePage: 2);
            Assert.Equal(ItmPage.FuelErsDrs, r.Identity);
            Assert.Equal(2, r.Wire);
            Assert.Equal("Fuel / ERS / DRS", r.Name);
        }

        [Fact]
        public void ResolveBase_UnavailableBase_FallsToTheDefaultWiresIdentity()
        {
            // The Bentley set has no Car Settings: a config pinning it keeps the default
            // wire, and the effective base is whatever identity sits at that wire.
            var r = Bentley.ResolveBase(ItmPage.CarSettings, defaultWirePage: 1);
            Assert.Equal(ItmPage.LapInfo, r.Identity);   // Bentley wire 1 = Lap Info
            Assert.Equal(1, r.Wire);
            Assert.Equal("Lap Info", r.Name);

            // A different (available) default wire on the Bentley: wire 4 is Tyre Temps.
            var r2 = Bentley.ResolveBase(ItmPage.CarSettings, defaultWirePage: 4);
            Assert.Equal(ItmPage.TyreTemps, r2.Identity);
            Assert.Equal(4, r2.Wire);
            Assert.Equal("Tire Temps", r2.Name);
        }

        [Fact]
        public void ResolveBase_OffTableDefaultWire_NoConfig_FallsToLapInfo()
        {
            // A corrupted default-page setting (off-table) with no config base: the
            // requested identity falls to Lap Info and resolves to its own wire — exactly
            // the running stack's fallback, so the "Always →" row can't read "Page 99".
            var r = Standard.ResolveBase(null, defaultWirePage: 99);
            Assert.Equal(ItmPage.LapInfo, r.Identity);
            Assert.Equal(1, r.Wire);              // Lap Info's wire on the standard set
            Assert.Equal("Lap Info", r.Name);
        }

        [Fact]
        public void ResolveBase_UnavailableBase_AndOffTableDefaultWire_StaysCoherent()
        {
            // The compound corner: a Bentley pinned to Car Settings (absent) AND an off-table
            // default wire — wire 6 is a standard six-page wheel's legacy wire, realistic
            // because ItmDefaultPage is an unvalidated byte that can carry across wheels. The
            // (identity, wire, name) triple must still agree: the driver hand-off (wire) and
            // the "Always →" row (name) must not name a page the display can never rest on
            // while the engine rests on another.
            var r = Bentley.ResolveBase(ItmPage.CarSettings, defaultWirePage: 6);
            Assert.Equal(ItmPage.LapInfo, r.Identity);   // resolves to a real page…
            Assert.Equal(1, r.Wire);                     // …anchored to ITS wire, not 6…
            Assert.Equal("Lap Info", r.Name);            // …named for that wire, coherently
        }
    }
}
