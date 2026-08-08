using FanaBridge.Plugin.ControlMapper;
using Xunit;

namespace FanaBridge.Tests.Plugin.ControlMapper
{
    /// <summary>
    /// Covers the pure variant-string logic of <see cref="FanaBridgeVariantProvider"/>:
    /// the stock-compatible id (<see cref="FanaBridgeVariantProvider.FormatStockVariant"/>),
    /// the friendly display name (<see cref="FanaBridgeVariantProvider.FormatFriendlyName"/>),
    /// and the claim gate (vendor id + connected-base product id) on
    /// <see cref="FanaBridgeVariantProvider.GetVariant"/>. No hardware / no live plugin required.
    /// </summary>
    public class FanaBridgeVariantProviderTests
    {
        [Fact]
        public void VendorId_MatchesFanatec()
            => Assert.Equal(0x0EB7, FanaBridgeVariantProvider.FanatecVendorId);

        // ── stock-compatible variant id (the match key) ──────────────────
        [Theory]
        [InlineData("PSWBMW", null, "FS_WHEEL_SWTYPE_PSWBMW")]
        [InlineData("PSWBMW", "", "FS_WHEEL_SWTYPE_PSWBMW")]
        [InlineData("PHUB", "PBME", "FS_WHEEL_SWTYPE_PHUB_PBME")]
        [InlineData("PHUB", "PBMR", "FS_WHEEL_SWTYPE_PHUB_PBMR")]
        [InlineData("CSSWFORMV3", null, "FS_WHEEL_SWTYPE_CSSWFORMV3")] // newer wheel stock never knew, emitted as-is
        public void FormatStockVariant_BuildsStockCompatibleId(string wheel, string? module, string expected)
            => Assert.Equal(expected, FanaBridgeVariantProvider.FormatStockVariant(wheel, module));

        [Fact]
        public void FormatStockVariant_MapsKnownCodeDivergence_PswbentToBentley()
            => Assert.Equal("FS_WHEEL_SWTYPE_BENTLEY",
                FanaBridgeVariantProvider.FormatStockVariant("PSWBENT", null));

        [Theory]
        [InlineData(null, null)]
        [InlineData("", "PBMR")]
        public void FormatStockVariant_NoWheelCode_ReturnsNull(string? wheel, string? module)
            => Assert.Null(FanaBridgeVariantProvider.FormatStockVariant(wheel, module));

        // ── friendly display name (applied as CustomName) ────────────────
        [Theory]
        [InlineData("PHUB", "PBMR", "Podium Hub + Button Module Rally")]
        [InlineData("PHUB", "PBME", "Podium Hub + Button Module Endurance")]
        [InlineData("PSWBMW", null, "Podium BMW M4 GT3")]
        public void FormatFriendlyName_UsesTableNames(string wheel, string? module, string expected)
            => Assert.Equal(expected, FanaBridgeVariantProvider.FormatFriendlyName(wheel, module));

        [Theory]
        [InlineData("ZZWHEEL", null, "ZZWHEEL")]
        [InlineData("ZZWHEEL", "ZZMOD", "ZZWHEEL + ZZMOD")]
        public void FormatFriendlyName_UnknownCodes_FallBackToRawCode(string wheel, string? module, string expected)
            => Assert.Equal(expected, FanaBridgeVariantProvider.FormatFriendlyName(wheel, module));

        [Fact]
        public void FormatFriendlyName_NoWheelCode_ReturnsNull()
            => Assert.Null(FanaBridgeVariantProvider.FormatFriendlyName(null, "PBMR"));

        // ── GetVariant claim gate (VID + connected-base PID) ─────────────
        [Fact]
        public void GetVariant_NonFanatecVendor_ReturnsNull()
            => Assert.Null(new FanaBridgeVariantProvider().GetVariant(0x1234, 0x0001));

        [Fact]
        public void GetVariant_FanatecVendor_NoLiveWheel_ReturnsNull()
            // No FanatecPlugin.Instance in a unit-test context, so the gate sees no
            // connected base and resolves to null without throwing.
            => Assert.Null(new FanaBridgeVariantProvider()
                .GetVariant(FanaBridgeVariantProvider.FanatecVendorId, 0x0020));

        [Fact]
        public void ShouldClaim_MatchingVendorAndConnectedPid_True()
            => Assert.True(FanaBridgeVariantProvider.ShouldClaim(
                FanaBridgeVariantProvider.FanatecVendorId, 0x0020, baseConnected: true, connectedProductId: 0x0020));

        [Fact]
        public void ShouldClaim_NonFanatecVendor_False()
            => Assert.False(FanaBridgeVariantProvider.ShouldClaim(
                0x1234, 0x0020, baseConnected: true, connectedProductId: 0x0020));

        [Fact] // standalone Fanatec pedals/handbrake, or a base we aren't driving
        public void ShouldClaim_DifferentFanatecPid_False()
            => Assert.False(FanaBridgeVariantProvider.ShouldClaim(
                FanaBridgeVariantProvider.FanatecVendorId, 0x0005, baseConnected: true, connectedProductId: 0x0020));

        [Fact]
        public void ShouldClaim_NoBaseConnected_False()
            => Assert.False(FanaBridgeVariantProvider.ShouldClaim(
                FanaBridgeVariantProvider.FanatecVendorId, 0x0020, baseConnected: false, connectedProductId: 0));

        [Fact]
        public void ShouldClaim_ConnectedPidZero_False()
            => Assert.False(FanaBridgeVariantProvider.ShouldClaim(
                FanaBridgeVariantProvider.FanatecVendorId, 0x0020, baseConnected: true, connectedProductId: 0));

        [Fact]
        public void Poll_WithNoLiveWheel_DoesNotThrow()
            => new FanaBridgeVariantProvider().Poll();
    }
}
