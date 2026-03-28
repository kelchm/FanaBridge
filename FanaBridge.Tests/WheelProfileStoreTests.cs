using FanaBridge.Profiles;
using Xunit;

namespace FanaBridge.Tests
{
    public class WheelProfileStoreTests
    {
        // ── NormalizeWheelType tests ─────────────────────────────────────
        //
        // The SimHub managed DLL (SimHub.FanatecManaged.dll) uses different
        // enum names than our profile IDs for two wheels. NormalizeWheelType
        // bridges this gap so profile lookup succeeds regardless of which
        // name the SDK reports.

        [Theory]
        [InlineData("BENTLEY", "PSWBENT")]
        public void NormalizeWheelType_MapsSimHubDllNames_ToProfileIds(string dllName, string expectedProfileId)
        {
            Assert.Equal(expectedProfileId, WheelProfileStore.NormalizeWheelType(dllName));
        }

        [Theory]
        [InlineData("PSWBMW")]
        [InlineData("CSSWBMWV2")]
        [InlineData("GTSWPRO")]
        [InlineData("PHUB")]
        [InlineData("CSSWFORMV2")]
        public void NormalizeWheelType_PassesThrough_WhenNoAliasNeeded(string wheelType)
        {
            Assert.Equal(wheelType, WheelProfileStore.NormalizeWheelType(wheelType));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("SOME_FUTURE_WHEEL")]
        public void NormalizeWheelType_PassesThrough_UnknownValues(string wheelType)
        {
            Assert.Equal(wheelType, WheelProfileStore.NormalizeWheelType(wheelType));
        }
    }
}
