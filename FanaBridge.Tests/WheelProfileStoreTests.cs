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

        // ── FindByWheelType null/empty safety ────────────────────────────
        //
        // An attached but unrecognized wheel (a wire byte not in the decode
        // tables, e.g. EXT_INFO / future hardware) resolves to a null code.
        // Lookup must return null rather than throw — otherwise identity
        // polling crashes on exactly the new hardware this path exists for.

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void FindByWheelType_ReturnsNull_ForNullOrEmptyWheel(string wheelType)
        {
            Assert.Null(WheelProfileStore.FindByWheelType(wheelType));
        }

        [Fact]
        public void FindByWheelType_ReturnsNull_ForNullWheelWithModule()
        {
            Assert.Null(WheelProfileStore.FindByWheelType(null, "PBMR"));
        }
    }
}
