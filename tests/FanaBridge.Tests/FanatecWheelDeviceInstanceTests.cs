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
using FanaBridge.Tests.TestDoubles;
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
        public void NoPlugin_ReportsScanning_NotDisabled()
        {
            // Disabled is SimHub's word for "the user switched this device off",
            // and it overrides anything else claiming it: an enabled device
            // reporting Disabled is moved to Scanning every frame and asked
            // again, so the pair never settles and every flip is logged.
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            Assert.Equal(DeviceState.Scanning, inst.GetDeviceState());
        }

        [Fact]
        public void NoPlugin_ReportsTheSameStateEveryFrame()
        {
            // The flood was not the state itself but its instability: whatever
            // is reported has to survive SimHub re-asking after it has forced
            // the device to Scanning.
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            var first = inst.GetDeviceState();
            for (int frame = 0; frame < 5; frame++)
                Assert.Equal(first, inst.GetDeviceState());

            Assert.NotEqual(DeviceState.Disabled, first);
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
            Assert.Equal(DeviceState.Scanning, inst.GetDeviceState());

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


        // ── Settings persistence (device settings wipe) ────────────────────
        //
        // SimHub rewrites each device's settings file from GetSettings() on every
        // save, wholesale, and it does so even while the plugin is disabled.
        // These cover the device's part of that: it must be able to describe its
        // settings with no runtime at all, and must never hand SimHub a document
        // it knows is incomplete. The composition rules themselves are pinned in
        // FanatecDeviceSettingsTests.

        private static JObject FullDocument() => new JObject
        {
            ["ledModuleSettings"] = new JObject { ["Brightness"] = 80.0 },
            ["leds"] = new JObject { ["activeProfileId"] = "profile-abc" },
            ["buttons"] = new JObject { ["activeProfileId"] = "buttons-1" },
            ["encoders"] = JValue.CreateNull(),
            ["matrix"] = JValue.CreateNull(),
            ["raw"] = new JObject { ["activeProfileId"] = "raw-1" },
            ["wheelType"] = "PSWBMW",
            ["moduleType"] = "",
            ["displayMode"] = "Speed",
            ["itmEnabled"] = true,
            ["itmShowLapTotal"] = false,
            ["itmShowPositionTotal"] = true,
            ["itmDefaultPage"] = 3,
            ["futureExtension"] = new JObject { ["nested"] = "keep-me" },
        };

        private static FanatecWheelDeviceInstance InstanceWith(
            FakeLedModuleHost host, string wheelCode = "PSWBMW")
        {
            var profile = WheelProfileStore.FindByWheelType(wheelCode);
            Assert.NotNull(profile);
            var config = new DeviceConfig
            {
                Profile = profile,
                Capabilities = new WheelCapabilities(profile),
            };
            return new FanatecWheelDeviceInstance(config, null, host);
        }

        [Fact]
        public void PluginUnavailable_StillSerializesTheWholeDocument()
        {
            // The wipe: SimHub saved a device while FanaBridge was disabled and
            // got back a document with no LED data, which replaced the file.
            var inst = InstanceWith(new FakeLedModuleHost());
            inst.PluginResolver = () => null;
            var doc = FullDocument();

            inst.SetSettings(doc, isDefault: false);
            var saved = inst.GetSettings(false, false);

            Assert.True(JToken.DeepEquals(doc, saved),
                "saving with no runtime must reproduce the loaded document, got: " + saved);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void PluginUnavailable_EveryFlagCombination_IsComplete(
            bool forTemplate, bool forDefaultSettings)
        {
            var inst = InstanceWith(new FakeLedModuleHost());
            inst.PluginResolver = () => null;

            inst.SetSettings(FullDocument(), isDefault: false);
            var saved = (JObject)inst.GetSettings(forTemplate, forDefaultSettings)!;

            Assert.NotNull(saved["ledModuleSettings"]);
            Assert.Equal("keep-me", (string?)saved["futureExtension"]?["nested"]);
        }

        [Fact]
        public void PluginUnavailable_TheDeviceStillOwnsItsSettings()
        {
            // Editing what a device stores does not need its hardware. Hiding
            // the settings while disabled left users unable to see settings
            // that were still being saved.
            var inst = InstanceWith(new FakeLedModuleHost());
            inst.PluginResolver = () => null;

            Assert.NotNull(inst.SettingsForTest);
            Assert.Equal(DeviceState.Scanning, inst.GetDeviceState());
        }

        // A panel factory that yields placeholder controls, so tab composition
        // can be asserted without standing up the real WPF panels.
        private sealed class StubPanelFactory : IDevicePanelFactory
        {
            public System.Windows.Controls.Control CreateScreenPanel(
                DisplaySettings settings, DisplayType display, byte itmDeviceId, Action settingsChanged)
                => new System.Windows.Controls.ContentControl();

            public System.Windows.Controls.Control CreateTuningPanel(FanatecDeviceSettings settings)
                => new System.Windows.Controls.ContentControl();
        }

        [Fact]
        public void PluginUnavailable_StillOffersEveryTab()
        {
            // SimHub composes a device's settings pane once and caches it for
            // the instance's lifetime, with nothing to rebuild it. A pane built
            // while the plugin was away therefore used to stay empty for the
            // rest of the session -- re-enabling did not bring the tabs back,
            // only restarting SimHub did. Composition must not depend on the
            // plugin at all.
            //
            // Runs on its own STA thread: the tabs are real WPF controls, and
            // xUnit hands tests whichever pooled thread is free.
            var titles = OnStaThread(() =>
            {
                var host = new FakeLedModuleHost
                {
                    EditControlForTest = new System.Windows.Controls.ContentControl(),
                };
                var profile = WheelProfileStore.FindByWheelType("PSWBMW");
                var inst = new FanatecWheelDeviceInstance(
                    new DeviceConfig
                    {
                        Profile = profile,
                        Capabilities = new WheelCapabilities(profile),
                    },
                    new StubPanelFactory(),
                    host);
                inst.PluginResolver = () => null;

                return inst.GetSettingsControls().Select(c => c.Title).ToList();
            });

            Assert.Contains("LEDs", titles);
            Assert.Contains("Screen", titles);
        }

        /// <summary>Runs <paramref name="body"/> on a fresh STA thread, rethrowing anything it threw.</summary>
        private static T OnStaThread<T>(Func<T> body)
        {
            T result = default!;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;

            var thread = new System.Threading.Thread(() =>
            {
                try { result = body(); }
                catch (Exception ex)
                { failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();

            failure?.Throw();
            return result;
        }

        [Fact]
        public void RejectedSettings_BlockTheSave()
        {
            // A module that could not take its settings holds partial state, so
            // the stored file keeps the last complete copy instead.
            var inst = InstanceWith(new FakeLedModuleHost { AcceptSettings = false });
            inst.PluginResolver = () => null;

            Assert.Throws<InvalidOperationException>(
                () => inst.SetSettings(FullDocument(), isDefault: false));
            Assert.Throws<InvalidOperationException>(() => inst.GetSettings(false, false));
        }

        [Fact]
        public void RejectedSettings_AlsoPauseLedOutput()
        {
            // Driving LEDs from half-applied settings would show the user
            // something they never chose.
            var host = new FakeLedModuleHost { AcceptSettings = false };
            var inst = InstanceWith(host);
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;

            Assert.Throws<InvalidOperationException>(
                () => inst.SetSettings(FullDocument(), isDefault: false));

            var data = new GameData();
            inst.DataUpdate(null, ref data);

            Assert.True(inst.SettingsForTest.IsFaulted);
            Assert.Equal(0, host.DisplayCount);
        }

        [Fact]
        public void Defaults_AfterRejectedSettings_MakeTheDeviceSaveableAgain()
        {
            var host = new FakeLedModuleHost { AcceptSettings = false };
            var inst = InstanceWith(host);
            inst.PluginResolver = () => null;
            Assert.Throws<InvalidOperationException>(
                () => inst.SetSettings(FullDocument(), isDefault: false));

            host.AcceptSettings = true;
            inst.LoadDefaultSettings();

            Assert.NotNull(inst.GetSettings(false, false));
        }

        [Fact]
        public void DataUpdate_WithoutEncoders_DoesNotThrowIntoSimHubsFrameLoop()
        {
            // The base class asks for a driver outside its own try/catch, so a
            // driver that cannot be built must return nothing rather than throw.
            var inst = InstanceWith(new FakeLedModuleHost());
            var plugin = PluginWithWheel("PSWBMW", out _);   // connected, but no encoders
            inst.PluginResolver = () => plugin;

            inst.SetSettings(FullDocument(), isDefault: false);

            var data = new GameData();
            inst.DataUpdate(null, ref data);   // must not throw
        }

        [Fact]
        public void DisplayOnlyDevice_SerializesWithoutAnLedModule()
        {
            var profile = WheelProfileStore.FindByWheelType("CSLSWGT3");
            Assert.NotNull(profile);
            var inst = new FanatecWheelDeviceInstance(new DeviceConfig
            {
                Profile = profile,
                Capabilities = new WheelCapabilities(profile),
            });
            inst.PluginResolver = () => null;

            inst.SetSettings(new JObject
            {
                ["wheelType"] = "CSLSWGT3",
                ["displayMode"] = "Speed",
                ["itmDefaultPage"] = 2,
            }, isDefault: false);

            var saved = (JObject)inst.GetSettings(false, false)!;
            Assert.Equal("Speed", (string?)saved["displayMode"]);
            Assert.Equal(2, (int?)saved["itmDefaultPage"]);
        }

        [Fact]
        public void LoadedSettings_ReachTheLiveDisplayView()
        {
            // The display and ITM drivers read a live settings object every
            // frame; settings that only landed in the document would never
            // reach the hardware.
            var inst = InstanceWith(new FakeLedModuleHost());
            inst.PluginResolver = () => null;

            var doc = FullDocument();
            doc["displayMode"] = "Gear";
            doc["itmDefaultPage"] = 5;
            inst.SetSettings(doc, isDefault: false);

            var saved = (JObject)inst.GetSettings(false, false)!;
            Assert.Equal("Gear", (string?)saved["displayMode"]);
            Assert.Equal(5, (int?)saved["itmDefaultPage"]);
        }

        [Fact]
        public void SettingsRejectedBeforePublication_DisposeTheLedHost()
        {
            // SimHub abandons a device that throws on the way up without ever
            // calling End(), so the host's subscription to a static event would
            // outlive it -- once per failed attempt.
            var host = new FakeLedModuleHost { AcceptSettings = false };
            var inst = InstanceWith(host);
            inst.PluginResolver = () => null;

            Assert.Throws<InvalidOperationException>(
                () => inst.SetSettings(FullDocument(), isDefault: false));

            Assert.Equal(1, host.DisposeCount);
        }

        [Fact]
        public void SettingsRejectedAfterPublication_KeepTheLedHost()
        {
            // Once SimHub owns the device it stays in the list and will call
            // End() later, so a rejected reload must not tear its editor down --
            // it only stops the device saving.
            var host = new FakeLedModuleHost();
            var inst = InstanceWith(host);
            inst.PluginResolver = () => null;
            inst.SetSettings(FullDocument(), isDefault: false);
            inst.Init(null);

            host.AcceptSettings = false;
            Assert.Throws<InvalidOperationException>(
                () => inst.SetSettings(FullDocument(), isDefault: false));

            Assert.Equal(0, host.DisposeCount);
        }

        [Fact]
        public void End_DisposesTheLedHost()
        {
            // Only disposal removes the LED manager's subscription to a static
            // event, and SimHub never disposes it for us.
            var host = new FakeLedModuleHost();
            var inst = InstanceWith(host);
            inst.PluginResolver = () => null;

            inst.End();

            Assert.Equal(1, host.DisposeCount);
        }

        [Fact]
        public void End_DisposesTheLedHost_EvenIfEarlierCleanupFails()
        {
            var host = new FakeLedModuleHost();
            var inst = InstanceWith(host);
            inst.PluginResolver = () => throw new InvalidOperationException("boom");

            inst.End();

            Assert.Equal(1, host.DisposeCount);
        }
    }
}
