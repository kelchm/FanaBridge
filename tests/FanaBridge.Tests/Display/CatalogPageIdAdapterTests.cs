using System.IO;
using System.Linq;
using System.Reflection;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Catalog;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>E7/E8: ItmPage → catalogPageId adapter (PBME + Bentley coverage).</summary>
    public class CatalogPageIdAdapterTests
    {
        private static string LoadFixture(string fileName)
        {
            var asm = typeof(CatalogPageIdAdapterTests).Assembly;
            string suffix = ".Display.Fixtures." + fileName;
            string resource = asm.GetManifestResourceNames()
                .Single(n => n.EndsWith(suffix, System.StringComparison.Ordinal));
            using (var stream = asm.GetManifestResourceStream(resource))
            using (var reader = new StreamReader(stream!))
                return reader.ReadToEnd();
        }

        [Fact]
        public void EveryItmPage_InPbmeTable_MapsToCatalog()
        {
            var catalog = CatalogLoader.LoadWheelCatalog(
                LoadFixture("pbme-catalog-draft.json"), _ => { });
            var table = ItmPageTable.ForDevice(3); // PBME
            // Legacy is the segment host — adapter spelling exists, but catalogs do not
            // list it as an ITM telemetry page entry. Filter to telemetry pages.
            var missing = CatalogPageIdAdapter.MissingFromCatalog(table, catalog)
                .Where(p => p != ItmPage.Legacy)
                .ToList();
            Assert.Empty(missing);
            foreach (var info in table.Pages)
            {
                string id = CatalogPageIdAdapter.FromItmPage(info.Page);
                Assert.False(string.IsNullOrEmpty(id), info.Page.ToString());
                Assert.StartsWith("itm:", CatalogPageIdAdapter.ToDestinationId(info.Page));
            }
        }

        [Fact]
        public void EveryItmPage_InBentleyTable_MapsToCatalog()
        {
            var catalog = CatalogLoader.LoadWheelCatalog(
                LoadFixture("bentley-catalog-draft.json"), _ => { });
            var table = ItmPageTable.ForDevice(4); // Bentley — no Car Settings
            var missing = CatalogPageIdAdapter.MissingFromCatalog(table, catalog)
                .Where(p => p != ItmPage.Legacy)
                .ToList();
            Assert.Empty(missing);
            // Bentley does not offer CarSettings; remaining pages still map.
            Assert.False(table.Offers(ItmPage.CarSettings));
            Assert.Equal("lapInfo", CatalogPageIdAdapter.FromItmPage(ItmPage.LapInfo));
            Assert.Equal("tyreTemps", CatalogPageIdAdapter.FromItmPage(ItmPage.TyreTemps));
        }

        [Fact]
        public void Spelling_MatchesCatalogEnumText()
        {
            Assert.Equal("lapInfo", CatalogPageIdAdapter.FromItmPage(ItmPage.LapInfo));
            Assert.Equal("fuelErsDrs", CatalogPageIdAdapter.FromItmPage(ItmPage.FuelErsDrs));
            Assert.Equal("carSettings", CatalogPageIdAdapter.FromItmPage(ItmPage.CarSettings));
            Assert.Equal("lapTimes", CatalogPageIdAdapter.FromItmPage(ItmPage.LapTimes));
            Assert.Equal("tyreTemps", CatalogPageIdAdapter.FromItmPage(ItmPage.TyreTemps));
            Assert.Equal("legacy", CatalogPageIdAdapter.FromItmPage(ItmPage.Legacy));
        }
    }
}
