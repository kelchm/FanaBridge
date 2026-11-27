using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FanaBridge.Core.Devices;
using FanaBridge.Core.Devices.Identity;
using FanaBridge.Core.Devices.Profiles;
using FanaBridge.Core.Display.Protocol;
using FanaBridge.Devices;
using FanaBridge.Leds;
using FanaBridge.Tests.TestDoubles;
using FanaBridge.UI.Devices;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using SimHub.Plugins.Devices;
using Xunit;

namespace FanaBridge.Tests.Plugin.Devices
{
    /// <summary>
    /// Issue #137: identity and capabilities are published as one attachment
    /// snapshot, a capability refresh for a superseded attachment is dropped,
    /// and a device whose attachment has lost its last profile stops its output
    /// (LEDs, 7-segment display) while the device itself stays.
    /// </summary>
    public partial class FanatecWheelDeviceInstanceTests
    {
        // ── Harness ───────────────────────────────────────────────────────

        // A plugin whose wheelbase has committed and settled one FF 08 frame,
        // exposing the clock so a test can commit another attachment later.
        private static FanatecPlugin AttachedPlugin(
            byte[] frame, out FanatecWheelbase wheelbase, out FakeTransport transport, out Clock clock,
            bool withDisplayEncoder = false, bool withLedEncoders = false)
        {
            transport = new FakeTransport();
            clock = new Clock();
            wheelbase = new FanatecWheelbase(transport, new FakeBus(), clock.Now);
            Assert.True(wheelbase.AutoConnect());
            Commit(wheelbase, transport, clock, frame);

            var plugin = new FanatecPlugin();
            plugin.InstallWheelbaseForTest(wheelbase, withDisplayEncoder, withLedEncoders);
            return plugin;
        }

        private static void Commit(FanatecWheelbase wheelbase, FakeTransport transport, Clock clock, byte[] frame)
        {
            transport.Identity.Enqueue(frame);
            clock.T += 10;
            wheelbase.UpdateIdentity();
            clock.T += 250;
            Assert.True(wheelbase.UpdateIdentity());
        }

        private static byte[] WheelFrame(string wheel) => Ff08(0x0C, WheelWire(wheel));

        private static byte[] BareHubFrame(string hub) =>
            Ff08(0x0C, FanatecDeviceTables.Hubs.First(kv => kv.Value == hub).Key);

        private static DeviceConfig RegisteredConfig(string wheelCode)
        {
            var profile = WheelProfileStore.FindByWheelType(wheelCode);
            Assert.NotNull(profile);
            return new DeviceConfig { Profile = profile, Capabilities = new WheelCapabilities(profile) };
        }

        // A user profile for the ClubSport Universal Hub, which has no built-in
        // profile, so deleting it leaves the attachment with none at all. It has
        // LEDs and a basic display, so the device has both to stop.
        private const string LastProfileId = "FB137_LAST";

        private static void WriteLastProfile(TempUserProfiles profiles) =>
            profiles.WriteJson("fb137-last-profile.json",
                "{ \"schemaVersion\": 2, \"id\": \"" + LastProfileId + "\", \"name\": \"FB137 hub\", " +
                "\"match\": { \"wheelType\": \"CSSWUH\" }, \"display\": \"basic\", \"leds\": [ " +
                "{ \"channel\": \"revRgb\", \"hwIndex\": 0, \"role\": \"rev\", \"label\": \"Rev 1\" }, " +
                "{ \"channel\": \"revRgb\", \"hwIndex\": 1, \"role\": \"rev\", \"label\": \"Rev 2\" } ] }");

        private static async Task Finished(Task task)
        {
            Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))));
            await task;
        }

        // A real LED manager for an attached PSWBMW, with the plugin's LED
        // encoders wired to the fake transport so it can build drivers.
        private static void WithLedManager(Action<FanatecLedManager, FanatecPlugin, DeviceConfig> body)
        {
            var config = RegisteredConfig("PSWBMW");
            var plugin = AttachedPlugin(WheelFrame("PSWBMW"), out _, out _, out _, withLedEncoders: true);
            var manager = new FanatecLedManager(config, () => plugin);
            try { body(manager, plugin, config); }
            finally { manager.Dispose(); }
        }

        // ── Deleting an attachment's last profile ─────────────────────────

        [Fact]
        public void DeletingTheAttachmentsLastProfile_StopsItsOutput_ButKeepsTheDevice()
        {
            using var profiles = new TempUserProfiles();
            using var settings = NoPersistedSettings();
            WriteLastProfile(profiles);
            profiles.Reload();

            var config = Assert.Single(FanatecDevicesRegistry.BuildConfigs(), c => c.WheelCode == "CSSWUH");
            Assert.Equal(LastProfileId, config.Profile.Id);

            var plugin = AttachedPlugin(BareHubFrame("CSSWUH"), out var wheelbase, out var transport, out _,
                withDisplayEncoder: true);
            var host = new FakeLedModuleHost();
            var inst = new FanatecWheelDeviceInstance(config, null, host) { PluginResolver = () => plugin };
            inst.SetSettings(new JObject { ["wheelType"] = "CSSWUH", ["displayMode"] = "Gear" }, isDefault: false);

            var data = RunningData("3");
            inst.DataUpdate(null, ref data);
            Assert.Equal(LastProfileId, host.LastHotSwapCaps?.Profile?.Id);
            Assert.NotEmpty(DisplayFrames(transport));
            Assert.True(plugin.Display.HasWritten);

            // What the settings page does on Delete.
            Assert.True(WheelProfileStore.DeleteUserProfile(LastProfileId));
            wheelbase.RefreshCapabilities();

            transport.Col01Sent.Clear();
            inst.DataUpdate(null, ref data);
            inst.DataUpdate(null, ref data);
            inst.DataUpdate(null, ref data);

            // The device stays, attached, with its settings...
            Assert.Equal(DeviceState.Connected, inst.GetDeviceState());
            Assert.Equal("Gear", inst.SettingsForTest.Current.DisplayMode);
            // ...but it resolves to the live "no profile" rather than the profile
            // it was registered with, so nothing drives the deleted profile.
            Assert.Null(plugin.ResolveCapsFor(config).Profile);
            Assert.Null(host.LastHotSwapCaps?.Profile);
            // The display is blanked once and handed off.
            var blank = Assert.Single(DisplayFrames(transport));
            Assert.Equal(SevenSegment.Blank, blank[5]);
            Assert.Equal(SevenSegment.Blank, blank[6]);
            Assert.Equal(SevenSegment.Blank, blank[7]);
            Assert.False(plugin.Display.HasWritten);

            // A profile for the attachment coming back resumes output.
            WriteLastProfile(profiles);
            profiles.Reload();
            wheelbase.RefreshCapabilities();
            transport.Col01Sent.Clear();
            inst.DataUpdate(null, ref data);

            Assert.Equal(LastProfileId, host.LastHotSwapCaps?.Profile?.Id);
            Assert.NotEmpty(DisplayFrames(transport));
            Assert.True(plugin.Display.HasWritten);
        }

        [Fact]
        public void NoProfileLeft_DisplayBlank_RetriesUntilTheTransportAccepts()
        {
            using var profiles = new TempUserProfiles();
            using var settings = NoPersistedSettings();
            WriteLastProfile(profiles);
            profiles.Reload();
            var config = Assert.Single(FanatecDevicesRegistry.BuildConfigs(), c => c.WheelCode == "CSSWUH");

            var plugin = AttachedPlugin(BareHubFrame("CSSWUH"), out var wheelbase, out var transport, out _,
                withDisplayEncoder: true);
            var inst = new FanatecWheelDeviceInstance(config, null, new FakeLedModuleHost())
            {
                PluginResolver = () => plugin,
            };
            inst.SetSettings(new JObject { ["wheelType"] = "CSSWUH", ["displayMode"] = "Gear" }, isDefault: false);
            var data = RunningData("3");
            inst.DataUpdate(null, ref data);

            Assert.True(WheelProfileStore.DeleteUserProfile(LastProfileId));
            wheelbase.RefreshCapabilities();

            transport.AcceptCol01 = false;
            transport.Col01Sent.Clear();
            inst.DataUpdate(null, ref data);   // declined — must not latch
            inst.DataUpdate(null, ref data);
            Assert.Equal(2, DisplayFrames(transport).Count);
            Assert.True(plugin.Display.HasWritten);   // still ours

            transport.AcceptCol01 = true;
            inst.DataUpdate(null, ref data);   // accepted — latches and releases
            inst.DataUpdate(null, ref data);
            Assert.Equal(3, DisplayFrames(transport).Count);
            Assert.False(plugin.Display.HasWritten);
        }

        // ── A refresh racing an attachment change ─────────────────────────

        [Fact]
        public async Task ARefreshForTheOldWheel_FinishingAfterADifferentWheelCommits_IsDropped()
        {
            var bmw = RegisteredConfig("PSWBMW");
            var gt3 = RegisteredConfig("CSLSWGT3");
            var plugin = AttachedPlugin(WheelFrame("PSWBMW"), out var wheelbase, out var transport, out var clock);

            // Pause the refresh after it has resolved the old wheel's capabilities
            // and before it publishes them. The override lookup is the last thing
            // resolution asks, and it runs on the refreshing thread.
            using var resolved = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int armed = 1;
            wheelbase.ProfileOverrideResolver = key =>
            {
                if (key == "PSWBMW" && Interlocked.Exchange(ref armed, 0) == 1)
                {
                    resolved.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
                return null;
            };

            var published = new ConcurrentQueue<AttachmentSnapshot>();
            wheelbase.WheelChanged += w => published.Enqueue(w.Attachment);

            var refresh = Task.Run(() => wheelbase.RefreshCapabilities());
            Assert.True(resolved.Wait(TimeSpan.FromSeconds(10)));

            // Meanwhile, on the frame thread, a different wheel commits.
            Commit(wheelbase, transport, clock, WheelFrame("CSLSWGT3"));
            Assert.Equal("CSLSWGT3", wheelbase.Attachment.Capabilities.Profile?.Id);

            release.Set();
            await Finished(refresh);

            // The old wheel's result was dropped, not written over the new wheel.
            var attachment = wheelbase.Attachment;
            Assert.Equal("CSLSWGT3", attachment.WheelCode);
            Assert.Equal("CSLSWGT3", attachment.Capabilities.Profile?.Id);
            Assert.Equal("CSLSWGT3", plugin.ResolveCapsFor(gt3).Profile?.Id);
            Assert.Same(bmw.Capabilities, plugin.ResolveCapsFor(bmw));
            // No snapshot ever paired one wheel with another's capabilities.
            Assert.All(published, s => Assert.Equal(s.WheelCode, s.Capabilities.Profile?.Match?.WheelType));

            // And the new wheel's device drives the new wheel's profile.
            var host = new FakeLedModuleHost();
            var inst = new FanatecWheelDeviceInstance(gt3, null, host) { PluginResolver = () => plugin };
            var data = new GameData();
            inst.DataUpdate(null, ref data);
            Assert.Equal("CSLSWGT3", host.LastHotSwapCaps?.Profile?.Id);
        }

        [Fact]
        public async Task ARefreshFinishingAfterADisconnect_IsDropped()
        {
            var config = RegisteredConfig("PSWBMW");
            var plugin = AttachedPlugin(WheelFrame("PSWBMW"), out var wheelbase, out _, out _);

            using var resolved = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int armed = 1;
            wheelbase.ProfileOverrideResolver = key =>
            {
                if (Interlocked.Exchange(ref armed, 0) == 1)
                {
                    resolved.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
                return null;
            };

            var refresh = Task.Run(() => wheelbase.RefreshCapabilities());
            Assert.True(resolved.Wait(TimeSpan.FromSeconds(10)));
            wheelbase.Disconnect();
            release.Set();
            await Finished(refresh);

            Assert.False(wheelbase.Attachment.WheelDetected);
            Assert.Same(WheelCapabilities.None, wheelbase.CurrentCapabilities);
            Assert.Same(config.Capabilities, plugin.ResolveCapsFor(config));
        }

        // ── LED driver hot-swap ───────────────────────────────────────────

        [Fact]
        public void LedHotSwap_NoProfileAfterADriverWasBuilt_StopsTheDriver_Once()
        {
            WithLedManager((manager, plugin, config) =>
            {
                // SimHub can build the driver before the device's first frame.
                var driver = manager.GetDriver();
                try
                {
                    Assert.NotNull(driver);

                    // The first thing the device then observes is "no profile".
                    Assert.True(manager.HotSwapIfNeeded(WheelCapabilities.None));
                    Assert.False(manager.HotSwapIfNeeded(WheelCapabilities.None));   // an edge, not per frame
                }
                finally
                {
                    (driver as IDisposable)?.Dispose();
                }
            });
        }

        [Fact]
        public void LedHotSwap_DriverBuiltAfterNoProfileWasSeen_IsStillStopped()
        {
            WithLedManager((manager, plugin, config) =>
            {
                Assert.False(manager.HotSwapIfNeeded(WheelCapabilities.None));   // no driver yet: nothing to stop

                // A refresh lands between frames and the driver is built from a profile...
                var driver = manager.GetDriver();
                try
                {
                    // ...then the profile goes again before the next frame.
                    Assert.True(manager.HotSwapIfNeeded(WheelCapabilities.None));
                }
                finally
                {
                    (driver as IDisposable)?.Dispose();
                }
            });
        }

        [Fact]
        public void LedHotSwap_SameProfileAsTheDriver_DoesNothing()
        {
            WithLedManager((manager, plugin, config) =>
            {
                var driver = manager.GetDriver();
                try
                {
                    Assert.False(manager.HotSwapIfNeeded(plugin.ResolveCapsFor(config)));
                    Assert.False(manager.HotSwapIfNeeded(plugin.ResolveCapsFor(config)));
                }
                finally
                {
                    (driver as IDisposable)?.Dispose();
                }
            });
        }

        // ── LED color notice ──────────────────────────────────────────────

        [Fact]
        public void LedColorNotice_FollowsCapabilityChanges()
        {
            // A wheel whose rev LEDs show one color for the whole strip.
            var limited = RegisteredConfig("CSLESWP1X").Capabilities;
            Assert.NotEmpty(LedColorLimitation.ForCapabilities(limited));

            var shown = OnStaThread(() =>
            {
                var caps = WheelCapabilities.None;
                var notice = new LedColorLimitationNotice(() => caps);
                bool atStart = notice.IsShowingForTest;

                caps = limited;
                notice.Refresh();
                bool afterProfile = notice.IsShowingForTest;

                caps = WheelCapabilities.None;   // profile deleted
                notice.Refresh();
                bool afterDelete = notice.IsShowingForTest;

                return (atStart, afterProfile, afterDelete);
            });

            Assert.False(shown.atStart);
            Assert.True(shown.afterProfile);
            Assert.False(shown.afterDelete);
        }
    }
}
