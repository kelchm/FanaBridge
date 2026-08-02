using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge;
using FanaBridge.Adapters;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
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
        static FanatecWheelDeviceInstanceTests()
        {
            // SimHub's Profile static initializer wants a JavascriptExtensions
            // directory next to the binary (present in every SimHub install,
            // absent in the test bin) — and a failed type initializer is sticky
            // for the whole process.
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "JavascriptExtensions"));
        }

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
        private static FanatecPlugin PluginWithWheel(
            string? wheelCode, out FanatecWheelbase wheelbase, string? overrideProfileId = null)
        {
            var t = new FakeTransport();
            var clock = new Clock();
            wheelbase = new FanatecWheelbase(t, new FakeBus(), clock.Now);
            if (overrideProfileId != null)
                wheelbase.ProfileOverrideResolver = _ => overrideProfileId;
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
        public void ResolveCurrentCapabilities_PluginReplacedBeforeDataUpdate_UsesNewGeneration()
        {
            var inst = InstanceFor("PSWBMW");
            var pluginA = PluginWithWheel(null, out _);
            var pluginB = PluginWithWheel("PSWBMW", out _, "CSLESWP1X");

            var data = new GameData();
            inst.PluginResolver = () => pluginA;
            inst.DataUpdate(null, ref data);
            Assert.Same(pluginA, inst.BoundPluginForTest);

            // SimHub can build the LEDs tab after the singleton changes but before
            // its next DataUpdate. The notice must resolve through the new singleton,
            // not through the generation that still owns the cached drivers.
            inst.PluginResolver = () => pluginB;

            var caps = inst.ResolveCurrentCapabilities();
            Assert.Equal("CSLESWP1X", caps.Profile?.Id);
            Assert.True(caps.HasLegacyRevStripe);
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

        // ── Settings persistence (document-first) ──────────────────────────
        //
        // SimHub loads the DLL and creates DeviceInstances even when the
        // plugin is deactivated in its plugin list, then rewrites each
        // device's settings file from GetSettings on every save-all. The
        // canonical document must round-trip losslessly in every state where
        // the LED module can't be built — one save-all writing a stub is
        // permanent loss of the stored LED profiles.

        private static JObject LedSettingsPayload() => new JObject
        {
            ["ledModuleSettings"] = new JObject { ["_LEDsBrightness"] = 80.0 },
            ["leds"] = new JObject { ["activeProfileId"] = "3795069f-fb17-46f8-a89e-b432d812283d" },
            ["buttons"] = new JObject(),
            ["wheelType"] = "PSWBMW",
            ["itmDefaultPage"] = 3,
        };

        [Fact]
        public void PluginUnavailable_GetSettings_RoundTripsTheDocument()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            inst.SetSettings(LedSettingsPayload(), false);
            var result = (JObject)inst.GetSettings(false, false);

            Assert.Equal(80.0, (double)result["ledModuleSettings"]!["_LEDsBrightness"]!);
            Assert.Equal("3795069f-fb17-46f8-a89e-b432d812283d", (string?)result["leds"]!["activeProfileId"]);
            Assert.NotNull(result["buttons"]);
            Assert.Equal(3, (int)result["itmDefaultPage"]!);
        }

        [Fact]
        public void PluginUnavailable_SpecialFlavors_StillReturnTheFullDocument()
        {
            // Interim policy pending SimHub-contract verification: with no
            // module to do the template/defaults filtering, returning the
            // complete document is the only answer that can't wipe the
            // per-device file if SimHub writes this output there.
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;
            inst.SetSettings(LedSettingsPayload(), false);

            foreach (var (forTemplate, forDefaults) in new[] { (true, false), (false, true), (true, true) })
            {
                var result = (JObject)inst.GetSettings(forTemplate, forDefaults);
                Assert.NotNull(result["leds"]);
                Assert.Equal(3, (int)result["itmDefaultPage"]!);
            }
        }

        [Fact]
        public void PluginArrivesLater_DocumentHydratesTheBuiltModule()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;
            inst.SetSettings(LedSettingsPayload(), false);

            var plugin = PluginWithWheel(null, out _);
            inst.PluginResolver = () => plugin;
            inst.GetDynamicButtonActions();   // any EnsureLedModuleInitialized site

            var result = (JObject)inst.GetSettings(false, false);
            Assert.Equal(80.0, (double)result["ledModuleSettings"]!["_LEDsBrightness"]!);
        }

        [Fact]
        public void UnknownCustomKeys_SurviveTheReloadRoundTrip()
        {
            // The tuning panel writes keys (e.g. encoderMode) this class doesn't
            // enumerate; a whitelist would silently drop them on the next load.
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            var payload = LedSettingsPayload();
            payload["encoderMode"] = "fine";
            inst.SetSettings(payload, false);

            var result = (JObject)inst.GetSettings(false, false);
            Assert.Equal("fine", (string?)result["encoderMode"]);
        }

        [Fact]
        public void SetSettingsTwice_TheLaterPayloadWins()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            inst.SetSettings(LedSettingsPayload(), false);
            var second = LedSettingsPayload();
            second["ledModuleSettings"] = new JObject { ["_LEDsBrightness"] = 55.0 };
            inst.SetSettings(second, false);

            var result = (JObject)inst.GetSettings(false, false);
            Assert.Equal(55.0, (double)result["ledModuleSettings"]!["_LEDsBrightness"]!);
        }

        [Fact]
        public void LoadDefaults_WithoutModule_DefersTheLedReset()
        {
            // With no module to define LED defaults, the stored LED subtrees
            // must survive (deferred reset) while custom keys reset now —
            // destroying profiles to fake a reset is the wipe again.
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;
            inst.SetSettings(LedSettingsPayload(), false);

            inst.LoadDefaultSettings();
            var result = (JObject)inst.GetSettings(false, false);

            Assert.Equal(80.0, (double)result["ledModuleSettings"]!["_LEDsBrightness"]!);   // retained
            Assert.Equal(
                (int)DisplaySettings.DefaultItmDefaultPage, (int)result["itmDefaultPage"]!); // reset
        }

        [Fact]
        public void DeferredLedReset_ModuleBuildAttempt_NeverProducesAStub()
        {
            // Headless, LoadDefaults on the fresh module fails inside SimHub
            // internals (app context is absent), taking the hydration-failure
            // path; in-app it succeeds and materializes the reset. Either way
            // the invariant this pins is the same: the LED subtrees survive —
            // a save after the build attempt must never emit a stub.
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;
            inst.SetSettings(LedSettingsPayload(), false);
            inst.LoadDefaultSettings();

            var plugin = PluginWithWheel(null, out _);
            inst.PluginResolver = () => plugin;
            inst.GetDynamicButtonActions();   // builds the module → LoadDefaults path

            var result = (JObject)inst.GetSettings(false, false);
            Assert.NotNull(result["ledModuleSettings"]);
            Assert.NotNull(result["leds"]);
        }
    }
}
