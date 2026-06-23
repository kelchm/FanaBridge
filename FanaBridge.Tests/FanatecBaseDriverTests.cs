using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge.Devices;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Characterization tests for <see cref="FanatecBaseDriver"/> — the FF 08 identity
    /// logic split out of the old FanatecWheelbase. They assert the produced
    /// <see cref="DeviceSnapshot"/> reproduces the prior CommitIdentity decode exactly
    /// (wire 0x18 → wheel/hub, module 0x1F only on a hub, BaseType 0x02 → base), plus
    /// the settle/commit gating. Decode expectations are derived from
    /// <see cref="FanatecIdentity"/> so the tests stay correct if the tables change.
    /// </summary>
    public class FanatecBaseDriverTests
    {
        private const int SettleMs = 200;

        // A few representative bytes (a plain wheel, a hub, a module, a base).
        private const byte WheelWire = 0x0F;  // PSWBMW (not a hub)
        private const byte HubWire = 0x0C;    // PHUB (a hub)
        private const byte ModuleByte = 0x02; // PBMR
        private const byte BaseByte = 12;     // CSDDPlus

        // ── Fake transport: replays queued FF 08 frames, then drains empty ──
        private sealed class FakeTransport : IDeviceTransport
        {
            private readonly Queue<byte[]> _frames = new Queue<byte[]>();
            public bool IsConnected { get; set; } = true;
            public int Col03MaxInputReportLength => 64;

            public void Enqueue(byte[] frame) => _frames.Enqueue(frame);

            public bool SendCol03(byte[] data) => true;
            public bool SendCol01(byte[] data) => true;

            public int ReadCol03(byte[] buffer, int timeoutMs)
            {
                if (_frames.Count == 0) return 0; // empty → non-blocking drain
                var f = _frames.Dequeue();
                Array.Clear(buffer, 0, buffer.Length);
                int n = Math.Min(f.Length, buffer.Length);
                Array.Copy(f, buffer, n);
                return n;
            }

            public IDisposable BeginBatch() => new NoOp();
            private sealed class NoOp : IDisposable { public void Dispose() { } }
        }

        private static byte[] Frame(byte baseType, byte wire, byte module)
        {
            var f = new byte[64];
            f[0] = 0xFF;
            f[1] = 0x08;
            f[FanatecIdentity.OffBaseType] = baseType;
            f[FanatecIdentity.OffWireCode] = wire;
            f[FanatecIdentity.OffModule] = module;
            return f;
        }

        // Build a driver seeded with one initial frame, advancing past the settle
        // window so the reading commits. Returns the driver and an event counter.
        private static (FanatecBaseDriver driver, Func<int> changes) Commit(byte baseType, byte wire, byte module)
        {
            long now = 0;
            var io = new FakeTransport();
            io.Enqueue(Frame(baseType, wire, module));

            var driver = new FanatecBaseDriver(io, () => now);
            int changes = 0;
            driver.SnapshotChanged += _ => changes++;

            driver.Initialize();      // reads the frame at now=0 → offers the settler (deadline 200)
            now = SettleMs + 50;      // past the settle window
            driver.Service();         // drains empty, ticks → commits

            return (driver, () => changes);
        }

        // ── Decode mapping ───────────────────────────────────────────────

        [Fact]
        public void PlainWheel_ProducesOneWheelAttachment_NoModule()
        {
            var (driver, _) = Commit(BaseByte, WheelWire, 0x00);
            var snap = driver.Snapshot;

            Assert.Equal(DeviceClass.Base, snap.Class);
            Assert.Equal(FanatecIdentity.DecodeBaseCode(BaseByte), snap.Code);
            Assert.Equal(BaseByte, snap.BaseTypeByte);
            Assert.True(snap.HasIdentity);
            Assert.True(snap.Stable);

            var wheel = Assert.Single(snap.Attachments);
            Assert.Equal(PeripheralKind.Wheel, wheel.Kind);
            Assert.Equal(FanatecIdentity.DecodeCode(WheelWire), wheel.Code);
            Assert.Equal(WheelWire, wheel.WireCode);
            Assert.DoesNotContain(snap.Attachments, a => a.Kind == PeripheralKind.Module);
        }

        [Fact]
        public void Hub_WithModule_ProducesHubAndModuleAttachments()
        {
            var (driver, _) = Commit(BaseByte, HubWire, ModuleByte);
            var snap = driver.Snapshot;

            var hub = snap.Attachments.Single(a => a.Kind == PeripheralKind.Hub);
            Assert.Equal(FanatecIdentity.DecodeCode(HubWire), hub.Code);
            Assert.Equal(HubWire, hub.WireCode);

            var module = snap.Attachments.Single(a => a.Kind == PeripheralKind.Module);
            Assert.Equal(FanatecIdentity.DecodeModule(ModuleByte), module.Code);
            Assert.Equal(ModuleByte, module.WireCode);
        }

        [Fact]
        public void Hub_WithoutModule_HasModuleSlotWithZeroWire()
        {
            // 0x1F is only meaningful on a hub; today's ModuleWireCode is the raw byte
            // (0 here), ModuleCode null. The snapshot carries a Module attachment so an
            // unmapped module is still reportable.
            var (driver, _) = Commit(BaseByte, HubWire, 0x00);
            var module = driver.Snapshot.Attachments.Single(a => a.Kind == PeripheralKind.Module);

            Assert.Equal(0, module.WireCode);
            Assert.Null(module.Code);
        }

        [Fact]
        public void NoWheel_StillReportsBaseIdentity()
        {
            var (driver, _) = Commit(BaseByte, 0x00, 0x00);
            var snap = driver.Snapshot;

            Assert.Equal(BaseByte, snap.BaseTypeByte);
            Assert.True(snap.HasIdentity);
            Assert.Empty(snap.Attachments);
        }

        // ── Settle / commit gating ────────────────────────────────────────

        [Fact]
        public void Service_DoesNotCommitBeforeSettleWindow()
        {
            long now = 0;
            var io = new FakeTransport();
            io.Enqueue(Frame(BaseByte, WheelWire, 0x00));
            var driver = new FanatecBaseDriver(io, () => now);
            int changes = 0;
            driver.SnapshotChanged += _ => changes++;

            driver.Initialize();   // offer at now=0, deadline 200
            now = 100;
            bool committed = driver.Service();

            Assert.False(committed);
            Assert.Equal(0, changes);
            Assert.False(driver.Snapshot.HasIdentity); // not committed yet
            Assert.False(driver.Snapshot.Stable);      // mid-settle
        }

        [Fact]
        public void Service_CommitsOnceSettled_AndRaisesSnapshotChanged()
        {
            var (driver, changes) = Commit(BaseByte, WheelWire, 0x00);
            Assert.Equal(1, changes());
            Assert.True(driver.Snapshot.Stable);
        }

        [Fact]
        public void LastRawReport_RetainedAfterInitialize_BeforeAnyCommit()
        {
            long now = 0;
            var io = new FakeTransport();
            var raw = Frame(BaseByte, WheelWire, 0x00);
            io.Enqueue(raw);
            var driver = new FanatecBaseDriver(io, () => now);

            driver.Initialize();   // no Service yet → not committed

            var snap = driver.Snapshot;
            Assert.False(snap.HasIdentity);                 // no settled commit
            Assert.NotNull(snap.LastRawReport);             // raw frame still captured
            Assert.Equal(0xFF, snap.LastRawReport[0]);
            Assert.Equal(BaseByte, snap.LastRawReport[FanatecIdentity.OffBaseType]);
        }

        [Fact]
        public void Service_FalseWhenNotConnected()
        {
            var io = new FakeTransport { IsConnected = false };
            var driver = new FanatecBaseDriver(io, () => 0);
            Assert.False(driver.Service());
        }
    }
}
