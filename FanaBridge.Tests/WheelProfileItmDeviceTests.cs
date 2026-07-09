using System.Linq;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using Xunit;

namespace FanaBridge.Tests
{
    public class WheelProfileItmDeviceTests
    {
        [Fact]
        public void ItmDeviceId_DefaultsToDevice3_WhenOmitted()
        {
            // No "itmDeviceId" in the JSON -> device 3 (DefaultDeviceId), correct for PBME/GTSWX.
            Assert.Equal(ItmEncoder.DefaultDeviceId, new WheelProfile().ItmDeviceId);
            Assert.Equal((byte)3, new WheelProfile().ItmDeviceId);
        }

        [Theory]
        [InlineData((byte)1)]   // base display
        [InlineData((byte)3)]   // wheel OLED (PBME / GTSWX)
        [InlineData((byte)4)]   // Bentley
        public void ItmDeviceId_PassesThroughRawWireId(byte id)
        {
            Assert.Equal(id, new WheelProfile { ItmDeviceIdRaw = id }.ItmDeviceId);
        }

        [Fact]
        public void GetRestartReason_DisplayChanges_SwitchLive_NoRestart()
        {
            // Display type and the ITM device id are resolved override-aware each
            // frame (the ITM driver hot-swaps on an id change), so neither is a
            // restart reason anymore — only LED-layout changes are, because
            // SimHub sizes the LED editor from registration-time counts.
            var dev3 = new WheelCapabilities(new WheelProfile { Display = "itm", ItmDeviceIdRaw = 3 });
            var dev4 = new WheelCapabilities(new WheelProfile { Display = "itm", ItmDeviceIdRaw = 4 });
            var basic = new WheelCapabilities(new WheelProfile { Display = "basic" });

            Assert.Null(dev4.GetRestartReason(dev3));      // ITM id change → live hot-swap
            Assert.Null(basic.GetRestartReason(dev3));     // display type change → live
            Assert.Null(dev4.GetRestartReason(dev4));      // identical caps → no restart
        }

        [Fact]
        public void GetRestartReason_LedLayoutChange_StillRequiresRestart()
        {
            var noLeds = new WheelCapabilities(new WheelProfile { Display = "itm" });
            var nineRev = new WheelCapabilities(new WheelProfile
            {
                Display = "itm",
                Leds = System.Linq.Enumerable.Range(0, 9)
                    .Select(i => new LedDefinition { Channel = LedChannel.RevRgb, HwIndex = i })
                    .ToList(),
            });

            Assert.NotNull(nineRev.GetRestartReason(noLeds));
        }
    }
}
