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

            // Applying LED settings reaches SimHub's profile subsystem, which
            // reads the running host (game name, car id) off PluginManager.
            // Tests have no host, so stand up a bare instance — the fields it
            // touches read as null/default, which is all these paths need.
            var field = typeof(SimHub.Plugins.PluginManager)
                .GetField("Instance", System.Reflection.BindingFlags.Static
                                    | System.Reflection.BindingFlags.NonPublic
                                    | System.Reflection.BindingFlags.Public);
            if (field != null && field.GetValue(null) == null)
            {
                field.SetValue(null, System.Runtime.Serialization.FormatterServices
                    .GetUninitializedObject(typeof(SimHub.Plugins.PluginManager)));
            }
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

        // ── Settings persistence (device settings wipe) ────────────────────
        //
        // SimHub rewrites each device's settings file from GetSettings() on every
        // save, with no merge — and it does so even while the plugin is disabled,
        // when the LED module cannot be built. These pin that such a save either
        // reproduces the loaded document or fails outright, never writing a
        // partial one. (Once the module owns the settings, roots this build does
        // not recognise are still dropped; carrying them across is PR 2's job.)

        /// <summary>A complete on-disk settings payload, shaped like a real one.</summary>
        private static JObject FullDocument() => new JObject
        {
            ["ledModuleSettings"] = new JObject
            {
                ["Brightness"] = 80.0,
                ["IndividualLEDsMode"] = false,
            },
            ["leds"] = new JObject
            {
                ["activeProfileId"] = "profile-abc",
                ["profiles"] = new JArray { new JObject { ["Id"] = "profile-abc" } },
            },
            ["buttons"] = new JObject { ["activeProfileId"] = "buttons-1" },
            // Channels SimHub emits as null when no driver exists for them.
            ["encoders"] = JValue.CreateNull(),
            ["matrix"] = JValue.CreateNull(),
            ["raw"] = new JObject { ["activeProfileId"] = "raw-1" },
            ["wheelType"] = "PSWBMW",
            ["moduleType"] = "",
            ["displayMode"] = "speed",
            ["itmEnabled"] = true,
            ["itmShowLapTotal"] = false,
            ["itmShowPositionTotal"] = true,
            ["itmDefaultPage"] = 3,
            // A root this build does not know about (a newer/older FanaBridge, or
            // an unmerged feature branch). It must survive a round trip.
            ["futureExtension"] = new JObject { ["nested"] = "keep-me" },
        };

        [Fact]
        public void PluginUnavailable_GetSettings_ReturnsTheWholeDocument()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;
            var doc = FullDocument();

            inst.SetSettings(doc, isDefault: false);
            var saved = inst.GetSettings(false, false);

            Assert.True(JToken.DeepEquals(doc, saved),
                "Saving with the plugin disabled must reproduce the loaded document, got: " + saved);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void PluginUnavailable_EveryFlagCombination_ReturnsTheWholeDocument(
            bool forTemplate, bool forDefaultSettings)
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;
            var doc = FullDocument();

            inst.SetSettings(doc, isDefault: false);
            var saved = inst.GetSettings(forTemplate, forDefaultSettings);

            Assert.True(JToken.DeepEquals(doc, saved),
                $"flags({forTemplate},{forDefaultSettings}) must not drop settings, got: " + saved);
        }

        [Fact]
        public void PluginUnavailable_UnknownRootsSurvive()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            inst.SetSettings(FullDocument(), isDefault: false);
            var saved = (JObject)inst.GetSettings(false, false)!;

            Assert.Equal("keep-me", (string?)saved["futureExtension"]?["nested"]);
        }

        [Fact]
        public void PluginUnavailable_NullChannelsAreNotDropped()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            inst.SetSettings(FullDocument(), isDefault: false);
            var saved = (JObject)inst.GetSettings(false, false)!;

            Assert.Equal(JTokenType.Null, saved["encoders"]?.Type);
            Assert.Equal(JTokenType.Null, saved["matrix"]?.Type);
        }

        [Fact]
        public void FailedHydration_RefusesToSave()
        {
            // A payload the module cannot consume leaves it partially populated;
            // saving it would overwrite a complete file with an incomplete one.
            var inst = InstanceFor("PSWBMW");
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;
            inst.LedApplyForTest = (_, __) => false;   // module refuses the payload

            inst.SetSettings(FullDocument(), isDefault: false);

            Assert.Throws<InvalidOperationException>(() => inst.GetSettings(false, false));
        }

        [Fact]
        public void FailedHydration_ThenDefaults_SavesAgain()
        {
            // An explicit reset makes the module authoritative again.
            var inst = InstanceFor("PSWBMW");
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;
            inst.LedApplyForTest = (_, __) => false;
            inst.LedDefaultsForTest = () => { };

            inst.SetSettings(FullDocument(), isDefault: false);
            Assert.Throws<InvalidOperationException>(() => inst.GetSettings(false, false));

            inst.LoadDefaultSettings();

            // No longer refuses; the reset cleared the unapplied payload.
            var saved = (JObject)inst.GetSettings(false, false)!;
            Assert.Equal("PSWBMW", (string?)saved["wheelType"]);
        }

        [Fact]
        public void SuccessfulHydration_DoesNotRefuseToSave()
        {
            var inst = InstanceFor("PSWBMW");
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;
            inst.LedApplyForTest = (_, __) => true;    // module accepts the payload

            inst.SetSettings(FullDocument(), isDefault: false);

            var saved = (JObject)inst.GetSettings(false, false)!;
            Assert.Equal(3, (int?)saved["itmDefaultPage"]);
        }

        [Fact]
        public void PluginArrivesAfterLoad_TheStashHydratesTheModule()
        {
            var inst = InstanceFor("PSWBMW");
            FanatecPlugin? plugin = null;
            inst.PluginResolver = () => plugin;
            JObject? applied = null;
            inst.LedApplyForTest = (doc, __) => { applied = doc; return true; };

            // Loaded while disabled — nothing to apply to yet …
            inst.SetSettings(FullDocument(), isDefault: false);
            Assert.Null(applied);

            // … then the plugin comes up and SimHub asks for the settings tab.
            plugin = PluginWithWheel("PSWBMW", out _);
            inst.GetDynamicButtonActions();   // any module-touching call builds it

            // The stashed document — including its unknown roots — reached the module.
            Assert.NotNull(applied);
            Assert.Equal("keep-me", (string?)applied?["futureExtension"]?["nested"]);
        }

        [Fact]
        public void FailedHydration_IsRetriedOnce_AndSavingRecovers()
        {
            // A transient refusal must not strand the device for the session.
            var inst = InstanceFor("PSWBMW");
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;

            var attempts = 0;
            inst.LedApplyForTest = (_, __) => ++attempts > 1;   // fails once, then succeeds

            inst.SetSettings(FullDocument(), isDefault: false);
            Assert.Throws<InvalidOperationException>(() => inst.GetSettings(false, false));

            inst.GetDynamicButtonActions();   // next touch retries the payload

            var saved = (JObject)inst.GetSettings(false, false)!;
            Assert.Equal(2, attempts);
            Assert.Equal(3, (int?)saved["itmDefaultPage"]);
        }

        [Fact]
        public void RepeatedlyRejectedPayload_IsNotRetriedEveryFrame()
        {
            // A permanently malformed payload must not be re-parsed per frame.
            var inst = InstanceFor("PSWBMW");
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;

            var attempts = 0;
            inst.LedApplyForTest = (_, __) => { attempts++; return false; };

            inst.SetSettings(FullDocument(), isDefault: false);
            for (int i = 0; i < 5; i++)
                inst.GetDynamicButtonActions();

            Assert.Equal(2, attempts);   // the initial apply plus one retry
            Assert.Throws<InvalidOperationException>(() => inst.GetSettings(false, false));
        }

        [Fact]
        public void NewPayloadAfterRejection_GetsAFreshRetry()
        {
            var inst = InstanceFor("PSWBMW");
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;

            var accept = false;
            inst.LedApplyForTest = (_, __) => accept;

            inst.SetSettings(FullDocument(), isDefault: false);
            inst.GetDynamicButtonActions();                     // retry budget spent
            Assert.Throws<InvalidOperationException>(() => inst.GetSettings(false, false));

            accept = true;
            inst.SetSettings(FullDocument(), isDefault: false); // a new load succeeds

            var saved = (JObject)inst.GetSettings(false, false)!;
            Assert.Equal(3, (int?)saved["itmDefaultPage"]);
        }

        [Fact]
        public void PluginArrivesAfterLoad_FailedHydrationKeepsTheStash()
        {
            var inst = InstanceFor("PSWBMW");
            FanatecPlugin? plugin = null;
            inst.PluginResolver = () => plugin;
            inst.LedApplyForTest = (_, __) => false;

            inst.SetSettings(FullDocument(), isDefault: false);
            plugin = PluginWithWheel("PSWBMW", out _);
            inst.GetDynamicButtonActions();   // any module-touching call builds it

            // Hydration failed, so the module must not become authoritative.
            Assert.Throws<InvalidOperationException>(() => inst.GetSettings(false, false));
        }

        [Fact]
        public void FreshDeviceWhileDisabled_SavesDefaultsWithoutThrowing()
        {
            // No prior document exists: emitting just the custom defaults is
            // correct, and throwing would leave the new device without a file.
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            inst.LoadDefaultSettings();
            var saved = (JObject)inst.GetSettings(false, false)!;

            Assert.Equal("PSWBMW", (string?)saved["wheelType"]);
            Assert.Null(saved["ledModuleSettings"]);
        }

        [Fact]
        public void ResetWhileDisabled_DropsTheStashedLedPayload()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            inst.SetSettings(FullDocument(), isDefault: false);
            inst.LoadDefaultSettings();

            var saved = (JObject)inst.GetSettings(false, false)!;
            Assert.Null(saved["leds"]);
            Assert.Null(saved["futureExtension"]);
        }

        [Fact]
        public void DisplayOnlyDevice_SerializesWithoutLedModule()
        {
            var inst = InstanceFor("CSLSWGT3");
            var plugin = PluginWithWheel("CSLSWGT3", out _);
            inst.PluginResolver = () => plugin;

            inst.SetSettings(new JObject
            {
                ["wheelType"] = "CSLSWGT3",
                ["displayMode"] = "speed",
                ["itmDefaultPage"] = 2,
            }, isDefault: false);

            var saved = (JObject)inst.GetSettings(false, false)!;
            Assert.Equal("speed", (string?)saved["displayMode"]);
            Assert.Equal(2, (int?)saved["itmDefaultPage"]);
        }
    }
}
