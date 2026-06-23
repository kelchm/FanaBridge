using System.Linq;
using FanaBridge.Devices;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Tests the peripheral merge (<see cref="DeviceManager.BuildPeripherals"/>) — the
    /// new Phase 2 logic that turns a driver's <see cref="DeviceSnapshot"/> into the
    /// flat peripheral set SimHub adapters bind to. The connection state machine is
    /// covered by <c>ConnectionMonitorTests</c> (the monitor is composed unchanged).
    /// </summary>
    public class DeviceManagerTests
    {
        private static DeviceSnapshot Snapshot(byte baseType, params Attachment[] attachments)
            => new DeviceSnapshot
            {
                Class = DeviceClass.Base,
                Code = "CSDDPlus",
                BaseTypeByte = baseType,
                HasIdentity = baseType != 0,
                Stable = true,
                Attachments = attachments,
            };

        private static Attachment Att(PeripheralKind kind, string code, byte wire)
            => new Attachment { Kind = kind, Code = code, WireCode = wire };

        [Fact]
        public void Base_WithWheel_ProducesBaseAndWheelPeripherals()
        {
            var peripherals = DeviceManager.BuildPeripherals(
                Snapshot(12, Att(PeripheralKind.Wheel, "PSWBMW", 0x0F)));

            Assert.Equal(2, peripherals.Count);
            var bas = peripherals.Single(p => p.Kind == PeripheralKind.Base);
            Assert.Equal("CSDDPlus", bas.Code);
            Assert.Equal(12, bas.WireCode);

            var wheel = peripherals.Single(p => p.Kind == PeripheralKind.Wheel);
            Assert.Equal("PSWBMW", wheel.Code);
            Assert.Equal(0x0F, wheel.WireCode);
        }

        [Fact]
        public void Hub_WithModule_ProducesBaseHubAndModule()
        {
            var peripherals = DeviceManager.BuildPeripherals(Snapshot(12,
                Att(PeripheralKind.Hub, "PHUB", 0x0C),
                Att(PeripheralKind.Module, "PBMR", 0x02)));

            Assert.Equal(3, peripherals.Count);
            Assert.Single(peripherals, p => p.Kind == PeripheralKind.Hub && p.Code == "PHUB");
            Assert.Single(peripherals, p => p.Kind == PeripheralKind.Module && p.Code == "PBMR");
        }

        [Fact]
        public void Hub_WithEmptyModuleSlot_DropsPhantomModule()
        {
            // A hub with no module carries a Module attachment with wire 0 / null code
            // (so an unmapped module is still reportable); the merge must NOT surface it
            // as a phantom Module peripheral.
            var peripherals = DeviceManager.BuildPeripherals(Snapshot(12,
                Att(PeripheralKind.Hub, "PHUB", 0x0C),
                Att(PeripheralKind.Module, null, 0x00)));

            Assert.DoesNotContain(peripherals, p => p.Kind == PeripheralKind.Module);
            Assert.Single(peripherals, p => p.Kind == PeripheralKind.Hub);
        }

        [Fact]
        public void Hub_WithUnmappedModule_KeepsModulePeripheral()
        {
            // A present-but-unrecognized module (wire != 0, code null) IS surfaced so it
            // can be reported.
            var peripherals = DeviceManager.BuildPeripherals(Snapshot(12,
                Att(PeripheralKind.Hub, "PHUB", 0x0C),
                Att(PeripheralKind.Module, null, 0x09)));

            var module = peripherals.Single(p => p.Kind == PeripheralKind.Module);
            Assert.Null(module.Code);
            Assert.Equal(0x09, module.WireCode);
        }

        [Fact]
        public void NoIdentity_ProducesNoPeripherals()
        {
            var peripherals = DeviceManager.BuildPeripherals(new DeviceSnapshot { Stable = true });
            Assert.Empty(peripherals);
        }

        [Fact]
        public void NoWheel_StillProducesBasePeripheral()
        {
            var peripherals = DeviceManager.BuildPeripherals(Snapshot(12));
            var bas = Assert.Single(peripherals);
            Assert.Equal(PeripheralKind.Base, bas.Kind);
        }

        // ── Secondary device selection (the multi-device collection logic) ──

        [Fact]
        public void SecondaryPids_ExcludesPrimaryPid()
        {
            // Two distinct base PIDs present; the primary already owns 0x0020, so only the
            // other becomes a secondary slot.
            var secondary = DeviceManager.SecondaryPidsFrom(new[] { 0x0020, 0x0E03 }, primaryPid: 0x0020);
            Assert.Equal(new[] { 0x0E03 }, secondary);
        }

        [Fact]
        public void SecondaryPids_Empty_WhenOnlyPrimaryPresent()
        {
            // The single-device case: the only base-like PID is the primary's → no secondaries.
            Assert.Empty(DeviceManager.SecondaryPidsFrom(new[] { 0x0020 }, primaryPid: 0x0020));
        }

        [Fact]
        public void SecondaryPids_Deduplicates()
        {
            var secondary = DeviceManager.SecondaryPidsFrom(new[] { 0x0E03, 0x0E03, 0x0020 }, primaryPid: 0x0020);
            Assert.Equal(new[] { 0x0E03 }, secondary);
        }

        [Fact]
        public void SecondaryPids_AllDistinct_WhenPrimaryNotAmongThem()
        {
            // Primary owns a PID not in the discovered base-like set (e.g. override) →
            // every discovered base-like PID is a secondary.
            var secondary = DeviceManager.SecondaryPidsFrom(new[] { 0x0001, 0x0002 }, primaryPid: 0x0020);
            Assert.Equal(new[] { 0x0001, 0x0002 }, secondary);
        }
    }
}
