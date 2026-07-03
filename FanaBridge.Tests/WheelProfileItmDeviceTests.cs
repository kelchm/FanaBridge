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
    }
}
