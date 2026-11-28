using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.ControlMapper;
using FanaBridge.Core.Devices;
using FanaBridge.Core.Devices.Identity;
using FanaBridge.Core.Devices.Profiles;
using FanaBridge.Core.Transport;
using FanaBridge.Tests.TestDoubles;
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

        [Fact]
        public void ResolveForSnapshot_ReadsConnectedPidAndVariantFromOneSnapshot()
        {
            var connected = new WheelbaseSnapshot(
                connected: true,
                productId: 0x0020,
                productName: "ClubSport DD",
                connectionEpoch: 1,
                attachmentEpoch: 2,
                wheelChangeCount: 1,
                identityStable: true,
                baseType: 0x0C,
                baseCode: "CSDDPlus",
                wheelDetected: true,
                wheelCode: "PHUB",
                wheelWireCode: 0x09,
                isHub: true,
                moduleCode: "PBMR",
                moduleWireCode: 0x02,
                isSrmConverter: false,
                srmKitFirmware: null,
                capabilities: WheelCapabilities.None);

            // Matching VID + PID from that snapshot emits the snapshot's variant.
            Assert.Equal(
                "FS_WHEEL_SWTYPE_PHUB_PBMR",
                FanaBridgeVariantProvider.ResolveForSnapshot(
                    connected, FanaBridgeVariantProvider.FanatecVendorId, 0x0020));

            // Different PID is never claimed, even though the snapshot has a wheel.
            Assert.Null(FanaBridgeVariantProvider.ResolveForSnapshot(
                connected, FanaBridgeVariantProvider.FanatecVendorId, 0x0005));

            // Disconnected snapshot never claims, even if wheel fields were populated.
            var disconnected = new WheelbaseSnapshot(
                connected: false,
                productId: 0x0020,
                productName: "ClubSport DD",
                connectionEpoch: 2,
                attachmentEpoch: 3,
                wheelChangeCount: 1,
                identityStable: true,
                baseType: 0x0C,
                baseCode: "CSDDPlus",
                wheelDetected: true,
                wheelCode: "PHUB",
                wheelWireCode: 0x09,
                isHub: true,
                moduleCode: "PBMR",
                moduleWireCode: 0x02,
                isSrmConverter: false,
                srmKitFirmware: null,
                capabilities: WheelCapabilities.None);

            Assert.Null(FanaBridgeVariantProvider.ResolveForSnapshot(
                disconnected, FanaBridgeVariantProvider.FanatecVendorId, 0x0020));
        }

        private sealed class FakeTransport : IConnectableTransport
        {
            public bool Connected;
            public FakeReportStream Identity { get; } = new FakeReportStream();
            public bool Connect(int productId) { Connected = true; return true; }
            public void Disconnect() => Connected = false;
            public void Dispose() => Disconnect();
            public bool IsConnected => Connected;
            public bool IsDevicePresent => Connected;
            public FanatecTransport.TransportConnectStatus LastConnectStatus =>
                FanatecTransport.TransportConnectStatus.Connected;
            public bool SendCol03(byte[] data) => true;
            public bool SendCol01(byte[] data) => true;
            public IReportStream IdentityReports => Identity;
            public IReportStream ItmReports => FakeReportStream.Empty;
            public IReportStream SrmReports => FakeReportStream.Empty;
            public IReportStream TuningReports => FakeReportStream.Empty;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public int Col03MaxInputReportLength => 64;
            public int Col01MaxInputReportLength => 34;
            public IDisposable BeginBatch() => new NoOp();
            private sealed class NoOp : IDisposable { public void Dispose() { } }
        }

        private sealed class FakeBus : IHidBusEnumerator
        {
            private readonly int _pid;
            public FakeBus(int pid) => _pid = pid;
            public IReadOnlyList<HidDeviceInfo> GetDevices(ushort vendorId) =>
                new[] { new HidDeviceInfo(_pid, 64, 64, "ClubSport DD") };
        }

        private static byte[] HubFrame(string hub, string module)
        {
            var b = new byte[64];
            b[0] = 0xFF; b[1] = 0x08;
            b[FanatecIdentity.OffBaseType] = 0x0C;
            b[FanatecIdentity.OffWireCode] = FanatecDeviceTables.Hubs.First(kv => kv.Value == hub).Key;
            b[FanatecIdentity.OffModule] = FanatecDeviceTables.Modules.First(kv => kv.Value == module).Key;
            return b;
        }

        [Fact]
        public void GetVariant_PublicPath_UsesSingleCapturedViewForPidGateAndVariant()
        {
            var transport = new FakeTransport();
            long now = 0;
            var wheelbase = new FanatecWheelbase(transport, new FakeBus(0x0020), () => now);
            Assert.True(wheelbase.AutoConnect());
            transport.Identity.Enqueue(HubFrame("PHUB", "PBMR"));
            now += 10;
            wheelbase.UpdateIdentity();
            now += 250;
            Assert.True(wheelbase.UpdateIdentity());

            var plugin = new FanatecPlugin();
            plugin.InstallWheelbaseForTest(wheelbase, withDisplayEncoder: false);

            var provider = new FanaBridgeVariantProvider(() => plugin);

            Assert.Equal(
                "FS_WHEEL_SWTYPE_PHUB_PBMR",
                provider.GetVariant(FanaBridgeVariantProvider.FanatecVendorId, 0x0020));
            Assert.Null(provider.GetVariant(FanaBridgeVariantProvider.FanatecVendorId, 0x0005));
            Assert.Null(provider.GetVariant(0x1234, 0x0020));

            provider.IsActive = false;
            Assert.Null(provider.GetVariant(FanaBridgeVariantProvider.FanatecVendorId, 0x0020));
            provider.IsActive = true;

            wheelbase.Disconnect();
            Assert.Null(provider.GetVariant(FanaBridgeVariantProvider.FanatecVendorId, 0x0020));
        }
    }
}
