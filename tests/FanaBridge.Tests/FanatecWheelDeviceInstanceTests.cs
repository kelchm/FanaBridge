using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge;
using FanaBridge.Adapters;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Host;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;
using SimHub.Plugins.Devices;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// The issue-#37 transition matrix: how a DeviceInstance's state and driver
    /// generation respond as the plugin singleton appears, matches, mismatches,
    /// and is replaced. The original bug (game change → in-process plugin
    /// restart → instances writing through a disposed transport) was hardware-
    /// diagnosed and shipped with no regression tests; this pins the guard.
    /// Runs against the PluginResolver seam plus a wheelbase built on the
    /// injected-transport seam — no SimHub host, no hardware.
    /// </summary>
    public class FanatecWheelDeviceInstanceTests
    {
        // ── Wheelbase harness (same fakes as FanatecWheelbaseTests) ───────

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
            public IReadOnlyList<HidDeviceInfo> GetDevices(ushort vendorId) =>
                new[] { new HidDeviceInfo(0x0020, 64, 64, "Base") };
        }

        private sealed class Clock { public long T; public long Now() => T; }

        private static byte WheelWire(string code) =>
            FanatecDeviceTables.Wheels.First(kv => kv.Value == code).Key;

        private static byte[] Ff08(byte baseType, byte wire)
        {
            var b = new byte[64];
            b[0] = 0xFF; b[1] = 0x08;
            b[FanatecIdentity.OffBaseType] = baseType;
            b[FanatecIdentity.OffWireCode] = wire;
            return b;
        }

        // A plugin generation whose core is a wheelbase with a committed,
        // settled identity for the given wheel code (or none when null).
        private static FanatecPlugin PluginWithWheel(string? wheelCode, out FanatecWheelbase wheelbase)
        {
            var t = new FakeTransport();
            var clock = new Clock();
            wheelbase = new FanatecWheelbase(t, new FakeBus(), clock.Now);
            Assert.True(wheelbase.AutoConnect());

            if (wheelCode != null)
            {
                t.Identity.Enqueue(Ff08(0x0C, WheelWire(wheelCode)));
                clock.T += 10;
                wheelbase.UpdateIdentity();
                clock.T += 250;
                Assert.True(wheelbase.UpdateIdentity());
            }

            var plugin = new FanatecPlugin();
            plugin.InstallWheelbaseForTest(wheelbase);
            return plugin;
        }

        private static FanatecWheelDeviceInstance InstanceFor(string wheelCode)
        {
            var profile = WheelProfileStore.FindByWheelType(wheelCode);
            Assert.NotNull(profile);
            var config = new DeviceConfig
            {
                Profile = profile,
                Capabilities = new WheelCapabilities(profile),
            };
            return new FanatecWheelDeviceInstance(config);
        }

        // ── GetDeviceState rows ────────────────────────────────────────────

        [Fact]
        public void NoPlugin_ReportsDisabled()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            Assert.Equal(DeviceState.Disabled, inst.GetDeviceState());
        }

        [Fact]
        public void PluginWithoutCore_ReportsScanning()
        {
            var inst = InstanceFor("PSWBMW");
            var bare = new FanatecPlugin();   // Init never ran — no wheelbase
            inst.PluginResolver = () => bare;

            Assert.Equal(DeviceState.Scanning, inst.GetDeviceState());
        }

        [Fact]
        public void MatchingSettledIdentity_ReportsConnected()
        {
            var inst = InstanceFor("PSWBMW");
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;

            Assert.Equal(DeviceState.Connected, inst.GetDeviceState());
        }

        [Fact]
        public void MismatchedIdentity_ReportsScanning()
        {
            var inst = InstanceFor("PSWBMW");
            // Any other recognized wheel — resolved from the table, not guessed.
            string other = FanatecDeviceTables.Wheels.Values.First(v => v != "PSWBMW");
            var plugin = PluginWithWheel(other, out _);
            inst.PluginResolver = () => plugin;

            Assert.Equal(DeviceState.Scanning, inst.GetDeviceState());
        }

        [Fact]
        public void SettlingIdentity_ReportsScanning_UntilCommitted()
        {
            // Mid-transition output suppression: while a changed reading is still
            // settling the device must NOT present as Connected, or LED/display
            // writes would land on a half-(re)connected wheel.
            var t = new FakeTransport();
            var clock = new Clock();
            var wheelbase = new FanatecWheelbase(t, new FakeBus(), clock.Now);
            Assert.True(wheelbase.AutoConnect());
            var plugin = new FanatecPlugin();
            plugin.InstallWheelbaseForTest(wheelbase);

            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => plugin;

            t.Identity.Enqueue(Ff08(0x0C, WheelWire("PSWBMW")));
            clock.T += 10;
            wheelbase.UpdateIdentity();              // offered — still settling
            Assert.Equal(DeviceState.Scanning, inst.GetDeviceState());

            clock.T += 250;
            wheelbase.UpdateIdentity();              // committed
            Assert.Equal(DeviceState.Connected, inst.GetDeviceState());
        }

        // ── Generation guard (DataUpdate) ──────────────────────────────────

        [Fact]
        public void FirstDataUpdate_BindsTheCurrentGeneration()
        {
            var inst = InstanceFor("PSWBMW");
            var pluginA = PluginWithWheel(null, out _);   // no wheel → Scanning path
            inst.PluginResolver = () => pluginA;

            var data = new GameData();
            inst.DataUpdate(null, ref data);

            Assert.Same(pluginA, inst.BoundPluginForTest);
        }

        [Fact]
        public void PluginReplaced_RebindsToTheNewGeneration()
        {
            // The issue-#37 core case: SimHub keeps the DeviceInstance alive while
            // the plugin is replaced; cached drivers bound to the old (disposed)
            // core must be dropped and the instance re-bound to the new one.
            var inst = InstanceFor("PSWBMW");
            var pluginA = PluginWithWheel(null, out _);
            var pluginB = PluginWithWheel(null, out _);

            var data = new GameData();
            inst.PluginResolver = () => pluginA;
            inst.DataUpdate(null, ref data);
            Assert.Same(pluginA, inst.BoundPluginForTest);

            inst.PluginResolver = () => pluginB;
            inst.DataUpdate(null, ref data);
            Assert.Same(pluginB, inst.BoundPluginForTest);
        }

        [Fact]
        public void PluginGoneThenBack_KeepsLastBinding_WhileGone()
        {
            // The A → null → A sequence (plugin torn down mid-restart): with no
            // current generation there is nothing to rebind against — the guard
            // must neither rebind nor clear, and the state must report Disabled.
            var inst = InstanceFor("PSWBMW");
            var pluginA = PluginWithWheel(null, out _);

            var data = new GameData();
            inst.PluginResolver = () => pluginA;
            inst.DataUpdate(null, ref data);

            inst.PluginResolver = () => null;
            inst.DataUpdate(null, ref data);
            Assert.Same(pluginA, inst.BoundPluginForTest);   // unchanged while gone
            Assert.Equal(DeviceState.Disabled, inst.GetDeviceState());

            inst.PluginResolver = () => pluginA;
            inst.DataUpdate(null, ref data);
            Assert.Same(pluginA, inst.BoundPluginForTest);   // same generation — no rebind
        }

        [Fact]
        public void SameGeneration_RepeatedUpdates_DoNotRebind()
        {
            var inst = InstanceFor("PSWBMW");
            var pluginA = PluginWithWheel(null, out _);
            inst.PluginResolver = () => pluginA;

            var data = new GameData();
            inst.DataUpdate(null, ref data);
            inst.DataUpdate(null, ref data);
            inst.DataUpdate(null, ref data);

            Assert.Same(pluginA, inst.BoundPluginForTest);
        }
    }
}
