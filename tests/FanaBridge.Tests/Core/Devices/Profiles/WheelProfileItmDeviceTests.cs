using System.Linq;
using FanaBridge.Core.Devices.Profiles;
using FanaBridge.Core.Display.Protocol;
using Xunit;

namespace FanaBridge.Tests.Core.Devices.Profiles
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
        public void GetRestartReason_DisplayChanges_RequireRestart()
        {
            // Output follows these live, but SimHub builds the Screen tab once per
            // device from its registration capabilities: whether it exists, its
            // basic or ITM controls, and the ITM page catalog for the device id.
            var dev3 = new WheelCapabilities(new WheelProfile { Display = "itm", ItmDeviceIdRaw = 3 });
            var dev4 = new WheelCapabilities(new WheelProfile { Display = "itm", ItmDeviceIdRaw = 4 });
            var basic = new WheelCapabilities(new WheelProfile { Display = "basic" });
            var none = new WheelCapabilities(new WheelProfile());

            Assert.Equal("ITM display changed (device 3 → 4)", dev4.GetRestartReason(dev3));
            Assert.Equal("Display changed (basic → ITM)", dev3.GetRestartReason(basic));
            Assert.Equal("Display changed (ITM → basic)", basic.GetRestartReason(dev3));
            Assert.Equal("Display changed (none → basic)", basic.GetRestartReason(none));
            Assert.Equal("Display changed (basic → none)", none.GetRestartReason(basic));
            Assert.Null(dev4.GetRestartReason(dev4));
        }

        [Fact]
        public void GetRestartReason_IgnoresTheItmDeviceId_WithoutAnItmDisplay()
        {
            var basic3 = new WheelCapabilities(new WheelProfile { Display = "basic", ItmDeviceIdRaw = 3 });
            var basic4 = new WheelCapabilities(new WheelProfile { Display = "basic", ItmDeviceIdRaw = 4 });

            Assert.Null(basic4.GetRestartReason(basic3));
        }

        [Fact]
        public void GetRestartReason_EncoderChanges_RequireRestart()
        {
            // Same LED counts, so only the Tuning tab's presence differs.
            WheelCapabilities WithButtonLed(LedRole role) => new WheelCapabilities(new WheelProfile
            {
                Leds = new System.Collections.Generic.List<LedDefinition>
                {
                    new LedDefinition { Channel = LedChannel.ButtonRgb, HwIndex = 0, Role = role },
                },
            });
            var plain = WithButtonLed(LedRole.Button);
            var encoder = WithButtonLed(LedRole.Encoder);

            Assert.Equal("Encoders added", encoder.GetRestartReason(plain));
            Assert.Equal("Encoders removed", plain.GetRestartReason(encoder));
            Assert.Null(encoder.GetRestartReason(encoder));
        }

        [Fact]
        public void GetRestartReason_WithoutARegisteredDevice_IsNull()
        {
            var basic = new WheelCapabilities(new WheelProfile { Display = "basic" });

            Assert.Null(basic.GetRestartReason(null));
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

            Assert.Equal("LED count changed (0 → 9)", nineRev.GetRestartReason(noLeds));
        }

        [Fact]
        public void GetRestartReason_SameTotalInDifferentGroups_IsALayoutChange()
        {
            // SimHub sizes the LED editor by group (rev/flag and buttons), so
            // moving an LED between groups needs a restart even when the total
            // stays the same, and "12 → 12" would read as a false alarm.
            WheelCapabilities Split(int rev, int buttons) => new WheelCapabilities(new WheelProfile
            {
                Leds = System.Linq.Enumerable.Range(0, rev)
                    .Select(i => new LedDefinition { Channel = LedChannel.RevRgb, HwIndex = i })
                    .Concat(System.Linq.Enumerable.Range(0, buttons)
                        .Select(i => new LedDefinition { Channel = LedChannel.ButtonRgb, HwIndex = i, Role = LedRole.Button }))
                    .ToList(),
            });

            Assert.Equal("LED layout changed", Split(12, 0).GetRestartReason(Split(9, 3)));
        }
    }
}
