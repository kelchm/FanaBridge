using FanaBridge.Diagnostics;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Guards the top-level Usage Page/Usage parser used in the diagnostic report. HID
    /// item parsing (1/2/4-byte data sizes, global vs local items, stopping at the first
    /// Main Collection) is easy to get subtly wrong, so it is pinned to known descriptors.
    /// </summary>
    public class HidUsageTests
    {
        [Fact]
        public void ParsesGenericDesktopJoystick()
        {
            // Usage Page (Generic Desktop, 0x01); Usage (Joystick, 0x04); Collection (App)
            var rd = new byte[] { 0x05, 0x01, 0x09, 0x04, 0xA1, 0x01, 0xC0 };
            Assert.Equal("0001/0004", DiagnosticsReport.TopLevelUsage(rd));
        }

        [Fact]
        public void ParsesTwoByteVendorPage()
        {
            // Usage Page (0xFF00, 2-byte data); Usage (0x01); Collection
            var rd = new byte[] { 0x06, 0x00, 0xFF, 0x09, 0x01, 0xA1, 0x01, 0xC0 };
            Assert.Equal("FF00/0001", DiagnosticsReport.TopLevelUsage(rd));
        }

        [Fact]
        public void StopsAtFirstCollection_IgnoringNestedUsages()
        {
            // Top-level Usage 0x04; a nested Usage 0x30 after the Collection must be ignored.
            var rd = new byte[] { 0x05, 0x01, 0x09, 0x04, 0xA1, 0x01, 0x09, 0x30, 0xC0 };
            Assert.Equal("0001/0004", DiagnosticsReport.TopLevelUsage(rd));
        }

        [Fact]
        public void ReturnsNullForNullOrEmpty()
        {
            Assert.Null(DiagnosticsReport.TopLevelUsage(null));
            Assert.Null(DiagnosticsReport.TopLevelUsage(new byte[0]));
        }
    }
}
