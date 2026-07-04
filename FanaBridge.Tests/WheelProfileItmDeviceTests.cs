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
        public void GetRestartReason_ItmDeviceChange_RequiresRestart()
        {
            var dev3 = new WheelCapabilities(new WheelProfile { Display = "itm", ItmDeviceIdRaw = 3 });
            var dev4 = new WheelCapabilities(new WheelProfile { Display = "itm", ItmDeviceIdRaw = 4 });

            Assert.NotNull(dev4.GetRestartReason(dev3));   // ITM display device changed → restart
            Assert.Null(dev4.GetRestartReason(dev4));       // identical caps → no restart
        }
    }
}
