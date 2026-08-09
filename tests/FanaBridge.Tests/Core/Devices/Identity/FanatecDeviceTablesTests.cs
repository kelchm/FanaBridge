using System.Collections.Generic;
using System.Linq;
using FanaBridge.Core.Devices.Identity;
using Xunit;

namespace FanaBridge.Tests.Core.Devices.Identity
{
    /// <summary>
    /// Guards the friendly-name tables against drift. They are a second set of
    /// tables keyed by the same device codes as the decode tables; if a device is
    /// added to a decode table but its friendly name is forgotten, the Device
    /// Status chain silently falls back to the raw code. These tests fail loudly
    /// instead, in both directions (every code named; no orphan names).
    /// </summary>
    public class FanatecDeviceTablesTests
    {
        [Fact]
        public void WheelbaseNames_NameEveryWheelbaseCode()
            => AssertCovers(FanatecDeviceTables.Wheelbases.Values, FanatecDeviceTables.WheelbaseNames);

        [Fact]
        public void AttachmentNames_NameEveryWheelAndHubCode()
            => AssertCovers(
                FanatecDeviceTables.Wheels.Values.Concat(FanatecDeviceTables.Hubs.Values),
                FanatecDeviceTables.AttachmentNames);

        [Fact]
        public void ModuleNames_NameEveryModuleCode()
            => AssertCovers(FanatecDeviceTables.Modules.Values, FanatecDeviceTables.ModuleNames);

        [Fact]
        public void FriendlyNames_HaveNoOrphans()
        {
            AssertNoOrphans(FanatecDeviceTables.Wheelbases.Values, FanatecDeviceTables.WheelbaseNames);
            AssertNoOrphans(
                FanatecDeviceTables.Wheels.Values.Concat(FanatecDeviceTables.Hubs.Values),
                FanatecDeviceTables.AttachmentNames);
            AssertNoOrphans(FanatecDeviceTables.Modules.Values, FanatecDeviceTables.ModuleNames);
        }

        [Fact]
        public void FriendlyLookups_MapKnownCodes_AndAreNullSafe()
        {
            Assert.Equal("ClubSport DD+", FanatecIdentity.FriendlyBase("CSDDPlus"));
            Assert.Equal("Podium Hub", FanatecIdentity.FriendlyAttachment("PHUB"));
            Assert.Equal("Button Module Rally", FanatecIdentity.FriendlyModule("PBMR"));

            // Unmapped / null codes return null (the UI then falls back to the code).
            Assert.Null(FanatecIdentity.FriendlyBase(null));
            Assert.Null(FanatecIdentity.FriendlyAttachment("NOT_A_CODE"));
            Assert.Null(FanatecIdentity.FriendlyModule(null));
        }

        // Every code the decode tables can produce must have a non-empty friendly name.
        private static void AssertCovers(
            IEnumerable<string> codes, IReadOnlyDictionary<string, string> names)
        {
            foreach (var code in codes.Distinct())
            {
                Assert.True(names.ContainsKey(code), $"Missing friendly name for code '{code}'");
                Assert.False(string.IsNullOrWhiteSpace(names[code]), $"Empty friendly name for code '{code}'");
            }
        }

        // No friendly entry should name a code the decode tables never produce.
        private static void AssertNoOrphans(
            IEnumerable<string> codes, IReadOnlyDictionary<string, string> names)
        {
            var valid = new HashSet<string>(codes);
            foreach (var key in names.Keys)
                Assert.True(valid.Contains(key), $"Friendly name '{key}' has no matching device code");
        }
    }
}
