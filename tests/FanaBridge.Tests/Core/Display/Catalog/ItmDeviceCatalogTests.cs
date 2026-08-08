using System.Linq;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Protocol;
using FanaBridge.Plugin.Display.Drivers;
using Xunit;

namespace FanaBridge.Tests.Core.Display.Catalog
{
    public class ItmDeviceCatalogTests
    {
        [Fact]
        public void Standard_HasSixPagesInOrder()
        {
            var pages = ItmDeviceCatalog.PagesFor((byte)3);

            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, pages.Select(p => p.Number).ToArray());
            Assert.Equal(ItmPage.LapInfo, pages[0].Page);
            Assert.Equal(ItmPage.CarSettings, pages[2].Page);
            Assert.True(pages[5].IsLegacy);   // page 6 = Legacy, no params
        }

        [Fact]
        public void BaseAndWheelOled_ShareTheStandardSet()
        {
            Assert.Same(ItmDeviceCatalog.PagesFor((byte)3),
                        ItmDeviceCatalog.PagesFor((byte)1));
        }

        [Fact]
        public void Bentley_HasFivePages_NoCarSettings_Renumbered()
        {
            var pages = ItmDeviceCatalog.PagesFor((byte)4);

            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, pages.Select(p => p.Number).ToArray());
            Assert.DoesNotContain(pages, p => p.Page == ItmPage.CarSettings);
            Assert.Equal(ItmPage.LapTimes, pages[2].Page);   // page 3 is Lap Times, not Car Settings
            Assert.True(pages[4].IsLegacy);              // page 5 = Legacy
        }

        [Fact]
        public void UnknownDeviceId_FallsBackToStandard()
        {
            Assert.Same(ItmDeviceCatalog.PagesFor((byte)3),
                        ItmDeviceCatalog.PagesFor(99));
        }

        [Fact]
        public void EveryCatalogParam_HasAMapperEncoder()
        {
            // Every param any device can show must be encodable (generalizes the Phase-1 guard
            // across all device page sets). All device sets are subsets of BME's, so this holds.
            byte[] devices = { (byte)1, (byte)3, (byte)4 };
            foreach (var dev in devices)
                foreach (var page in ItmDeviceCatalog.PagesFor(dev))
                    foreach (var id in page.Params)
                        Assert.True(ItmTelemetryMapper.HasEncoder(id),
                            $"paramId {id} on device {dev} page {page.Number} ({page.Page}) has no mapper encoder");
        }
    }
}
