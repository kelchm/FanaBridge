using FanaBridge.Devices;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Locks the wheel display-name formatting carried over from the old
    /// FanatecWheelbase.DisplayName, now shared by the settings chain and diagnostics.
    /// </summary>
    public class IdentityFormatterTests
    {
        private static string Name(bool detected, string wheelCode, byte wheelWire, bool isHub,
            string moduleCode, byte moduleWire, string capsName)
            => IdentityFormatter.DisplayName(detected, wheelCode, wheelWire, isHub, moduleCode, moduleWire, capsName);

        [Fact]
        public void NotDetected_SaysNoWheel()
            => Assert.Equal("No wheel attached", Name(false, null, 0, false, null, 0, null));

        [Fact]
        public void ProfileName_WinsWhenPresent()
            => Assert.Equal("Podium BMW M4 GT3", Name(true, "PSWBMW", 0x0F, false, null, 0, "Podium BMW M4 GT3"));

        [Fact]
        public void WheelCode_UsedWhenNoProfileName()
            => Assert.Equal("PSWBMW", Name(true, "PSWBMW", 0x0F, false, null, 0, null));

        [Fact]
        public void HubPlusModule_Concatenates()
            => Assert.Equal("PHUB + PBMR", Name(true, "PHUB", 0x0C, true, "PBMR", 0x02, null));

        [Fact]
        public void UnrecognizedExtInfo_Wheel()
            => Assert.StartsWith("EXT_INFO", Name(true, null, 0xFF, false, null, 0, null));

        [Fact]
        public void UnrecognizedWheel_ShowsRawByte()
            => Assert.Equal("Unknown (0x55)", Name(true, null, 0x55, false, null, 0, null));

        [Fact]
        public void HubWithUnmappedModule_ShowsRawModuleByte()
            => Assert.Equal("PHUB + Module 0x09 (please report)",
                Name(true, "PHUB", 0x0C, true, null, 0x09, null));
    }
}
