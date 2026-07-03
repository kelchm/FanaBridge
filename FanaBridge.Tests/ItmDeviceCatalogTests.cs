using System.Linq;
using FanaBridge.Adapters;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests
{
    public class ItmDeviceCatalogTests
    {
        [Fact]
        public void Standard_HasSixPagesInOrder()
        {
            var pages = ItmDeviceCatalog.PagesFor((byte)ItmDevice.BmeOrGtswx);

            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, pages.Select(p => p.Number).ToArray());
            Assert.Equal("Lap Info", pages[0].Name);
            Assert.Equal("Car Settings", pages[2].Name);
            Assert.True(pages[5].IsLegacy);   // page 6 = Legacy, no params
        }

        [Fact]
        public void BaseAndWheelOled_ShareTheStandardSet()
        {
            Assert.Same(ItmDeviceCatalog.PagesFor((byte)ItmDevice.BmeOrGtswx),
                        ItmDeviceCatalog.PagesFor((byte)ItmDevice.Base));
        }

        [Fact]
        public void Bentley_HasFivePages_NoCarSettings_Renumbered()
        {
            var pages = ItmDeviceCatalog.PagesFor((byte)ItmDevice.Bentley);

            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, pages.Select(p => p.Number).ToArray());
            Assert.DoesNotContain(pages, p => p.Name == "Car Settings");
            Assert.Equal("Lap Times", pages[2].Name);   // page 3 is Lap Times, not Car Settings
            Assert.True(pages[4].IsLegacy);              // page 5 = Legacy
        }

        [Fact]
        public void UnknownDeviceId_FallsBackToStandard()
        {
            Assert.Same(ItmDeviceCatalog.PagesFor((byte)ItmDevice.BmeOrGtswx),
                        ItmDeviceCatalog.PagesFor(99));
        }

        [Fact]
        public void EveryCatalogParam_HasAMapperEncoder()
        {
            // Every param any device can show must be encodable (generalizes the Phase-1 guard
            // across all device page sets). All device sets are subsets of BME's, so this holds.
            byte[] devices = { (byte)ItmDevice.Base, (byte)ItmDevice.BmeOrGtswx, (byte)ItmDevice.Bentley };
            foreach (var dev in devices)
                foreach (var page in ItmDeviceCatalog.PagesFor(dev))
                    foreach (var id in page.Params)
                        Assert.True(ItmTelemetryMapper.HasEncoder(id),
                            $"paramId {id} on device {dev} page {page.Number} ({page.Name}) has no mapper encoder");
        }
    }
}
