using FanaBridge.Core.Devices.Profiles;
using Xunit;

namespace FanaBridge.Tests.Core.Devices.Profiles
{
    public class DeviceConfigTests
    {
        private static DeviceConfig ConfigFor(string? wheelType, string? moduleType = null) =>
            new DeviceConfig
            {
                Profile = new WheelProfile
                {
                    Id = "test",
                    Match = wheelType == null && moduleType == null
                        ? null
                        : new ProfileMatch { WheelType = wheelType, ModuleType = moduleType },
                },
            };

        [Theory]
        [InlineData("PSWBMW", null, true, "PSWBMW", null, true)]
        [InlineData("pswbmw", null, true, "PSWBMW", null, true)]
        [InlineData("PSWBMW", null, false, "PSWBMW", null, false)]
        [InlineData("PSWBMW", null, true, "CSSWRS", null, false)]
        [InlineData("PHUB", "PBME", true, "PHUB", "PBME", true)]
        [InlineData("PHUB", null, true, "PHUB", "PBME", false)]
        [InlineData("PHUB", "PBME", true, "PHUB", null, false)]
        public void MatchesExactly_OnWheelAndModule(
            string wheelType, string? moduleType,
            bool detected, string attachedWheel, string? attachedModule, bool expected)
        {
            Assert.Equal(expected,
                ConfigFor(wheelType, moduleType).MatchesAttachment(detected, attachedWheel, attachedModule));
        }

        // An unrecognized rim is detected with no code; a profile with no wheel
        // code must not claim it.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ConfigWithoutWheelCode_NeverMatchesAnUnrecognizedRim(string? wheelType)
        {
            var noMatchBlock = ConfigFor(null);
            var noWheelType = ConfigFor(wheelType, "PBME");

            Assert.False(noMatchBlock.MatchesAttachment(true, null, null));
            Assert.False(noMatchBlock.MatchesAttachment(true, "", null));
            Assert.False(noWheelType.MatchesAttachment(true, null, "PBME"));
            Assert.False(noWheelType.MatchesAttachment(true, "", "PBME"));
            Assert.False(noWheelType.MatchesAttachment(true, wheelType, "PBME"));
        }
    }
}
