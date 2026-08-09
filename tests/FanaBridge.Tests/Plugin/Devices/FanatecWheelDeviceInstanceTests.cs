using System;
using System.Collections.Generic;
using System.Linq;
using FanaBridge;
using FanaBridge.Core.Devices;
using FanaBridge.Core.Devices.Identity;
using FanaBridge.Core.Devices.Profiles;
using FanaBridge.Core.Display.Protocol;
using FanaBridge.Devices;
using FanaBridge.Display;
using FanaBridge.Leds;
using FanaBridge.Settings;
using FanaBridge.UI.Devices;
using FanaBridge.Tests.TestDoubles;
using FanaBridge.Core.Transport;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using SimHub.Plugins.Devices;
using Xunit;

namespace FanaBridge.Tests.Plugin.Devices
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
            // Recorded col01 output (copied — the encoders reuse their buffers) so
            // display tests can assert exactly what reached the wire.
            public List<byte[]> Col01Sent { get; } = new List<byte[]>();
            public bool AcceptCol01 = true;
            public bool Connect(int productId) { Connected = true; return true; }
            public void Disconnect() => Connected = false;
            public void Dispose() => Disconnect();
            public bool IsConnected => Connected;
            public bool IsDevicePresent => Connected;
            public FanatecTransport.TransportConnectStatus LastConnectStatus =>
                FanatecTransport.TransportConnectStatus.Connected;
            public bool SendCol03(byte[] data) => true;
            public bool SendCol01(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                Col01Sent.Add(copy);
                return AcceptCol01;
            }
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
            => PluginWithWheel(wheelCode, out wheelbase, out _, overrideProfileId,
                withDisplayEncoder: false);

        // The transport-exposing overload also installs a display encoder by
        // default — it exists for the display tests, which assert on the col01
        // frames that encoder emits.
        private static FanatecPlugin PluginWithWheel(
            string? wheelCode, out FanatecWheelbase wheelbase, out FakeTransport transport,
            string? overrideProfileId = null, bool withDisplayEncoder = true)
        {
            var t = new FakeTransport();
            transport = t;
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
            plugin.InstallWheelbaseForTest(wheelbase, withDisplayEncoder);
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

        // Same device, but with the LED module stood in for so a test can see
        // whether output was driven or blanked.
        private static FanatecWheelDeviceInstance InstanceWithHost(
            string wheelCode, IFanatecLedModuleHost host)
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
        public void NoPlugin_PresentsAsNotEnabled_ToTheUiOnly()
        {
            // SimHub greys the settings pane from this property, so it reports
            // false while nothing can drive the device -- but persistence reads
            // it through a DeviceInstance-typed reference and must still see the
            // user's own choice, or their device would come back switched off.
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            Assert.False(inst.Enabled);
            Assert.True(((DeviceInstance)inst).Enabled);
        }

        [Fact]
        public void PluginPresent_PresentsAsEnabled()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => PluginWithWheel("PSWBMW", out _);

            Assert.True(inst.Enabled);
        }

        [Fact]
        public void DeviceSwitchedOffByTheUser_StaysOff_EvenWithAPlugin()
        {
            // Hiding the property must not override the user's own choice.
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => PluginWithWheel("PSWBMW", out _);

            inst.Enabled = false;

            Assert.False(inst.Enabled);
            Assert.False(((DeviceInstance)inst).Enabled);   // and it is what gets stored
        }

        [Fact]
        public void PluginGoingAway_TellsTheUiToLookAgain()
        {
            // Enabled answers from something SimHub knows nothing about, so
            // without this every binding already made keeps showing the old
            // answer -- the device tiles and their toggles stay as they were,
            // and an open pane only greys once re-selecting it rebuilds it.
            var inst = InstanceFor("PSWBMW");
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;

            var announced = new List<string>();
            ((System.ComponentModel.INotifyPropertyChanged)inst).PropertyChanged +=
                (_, e) => announced.Add(e.PropertyName);

            var data = new GameData();
            inst.DataUpdate(null, ref data);   // establishes the baseline
            announced.Clear();

            inst.DataUpdate(null, ref data);
            Assert.Empty(announced);           // nothing moved, nothing said

            inst.PluginResolver = () => null;
            inst.DataUpdate(null, ref data);
            Assert.Contains(nameof(FanatecWheelDeviceInstance.Enabled), announced);

            // Once per transition, not once per frame -- this runs at frame rate.
            announced.Clear();
            inst.DataUpdate(null, ref data);
            Assert.Empty(announced);
        }

        [Fact]
        public void SwitchingOnWithNothingToDriveIt_MakesTheToggleSpringBack()
        {
            // The base only notifies when its own value moves, and it is already
            // true here, so without an unconditional announcement the toggle
            // would sit in the "on" position the user just put it in.
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            var announced = new List<string>();
            ((System.ComponentModel.INotifyPropertyChanged)inst).PropertyChanged +=
                (_, e) => announced.Add(e.PropertyName);

            inst.Enabled = true;

            Assert.Contains(nameof(FanatecWheelDeviceInstance.Enabled), announced);
            Assert.False(inst.Enabled);
        }

        [Fact]
        public void ClickingTheToggleWithNothingToDriveIt_LeavesTheStoredChoiceAlone()
        {
            // Only WPF reaches this setter, so a click the UI is going to refuse
            // must not change what is stored. While nothing can drive the device
            // the toggle reads false whatever the user chose, so honouring the
            // click would write true -- switching on a device they deliberately
            // switched off, and making it impossible to switch one off at all.
            var inst = InstanceFor("PSWBMW");
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;
            inst.Enabled = false;                 // the user switches it off

            inst.PluginResolver = () => null;     // ...then disables the plugin
            inst.Enabled = true;                  // ...and clicks the toggle

            Assert.False(((DeviceInstance)inst).Enabled);   // the stored choice stands
        }

        [Fact]
        public void ClickingTheToggleWithAPluginPresent_StillStores()
        {
            var inst = InstanceFor("PSWBMW");
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;

            inst.Enabled = false;
            Assert.False(((DeviceInstance)inst).Enabled);

            inst.Enabled = true;
            Assert.True(((DeviceInstance)inst).Enabled);
        }

        [Fact]
        public void SimHubsOwnNotifications_StillReachTheUi()
        {
            // Intercepting the subscription must not cost us everything SimHub
            // raises -- the device list binds to those.
            var inst = InstanceFor("PSWBMW");

            var announced = new List<string>();
            ((System.ComponentModel.INotifyPropertyChanged)inst).PropertyChanged +=
                (_, e) => announced.Add(e.PropertyName);

            inst.SuspendWhenMonitorIsOff = !inst.SuspendWhenMonitorIsOff;

            Assert.Contains(nameof(DeviceInstance.SuspendWhenMonitorIsOff), announced);
        }

        // ── Honouring the device's own on/off switch ───────────────────────

        [Fact]
        public void SwitchedOffDevice_StopsDrivingItsLeds()
        {
            // SimHub calls DataUpdate on every device whatever the switch says,
            // so a device that keeps driving hardware when switched off is one
            // the user cannot actually turn off.
            var host = new FakeLedModuleHost();
            var inst = InstanceWithHost("PSWBMW", host);
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;

            var data = new GameData();
            inst.DataUpdate(null, ref data);
            Assert.True(host.DisplayCount > 0);

            var drivenWhileOn = host.DisplayCount;
            inst.Enabled = false;
            inst.DataUpdate(null, ref data);

            Assert.Equal(drivenWhileOn, host.DisplayCount);
            // and the wheel is darkened rather than left on the last frame
            Assert.Equal(1, host.StopDrivingCount);

            // The blanking is an edge, not a per-frame write.
            inst.DataUpdate(null, ref data);
            Assert.Equal(1, host.StopDrivingCount);
        }

        [Theory]
        [InlineData(true, true, true)]     // switched on, plugin present
        [InlineData(true, false, false)]   // switched on, but nothing to drive it
        [InlineData(false, true, false)]   // switched off by the user
        public void TheModuleIsToldWhetherItIsDrivingAnything(
            bool switchedOn, bool pluginPresent, bool expected)
        {
            // SimHub hides the LEDs tab's connection badge while this is false,
            // rather than claiming to search for hardware nobody is looking for.
            var host = new FakeLedModuleHost();
            var inst = InstanceWithHost("PSWBMW", host);
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => pluginPresent ? plugin : null;
            inst.Enabled = switchedOn;

            var data = new GameData();
            inst.DataUpdate(null, ref data);

            Assert.Equal(expected, host.CanDrive);
        }

        [Fact]
        public void LosingTheWheel_TellsTheModuleItIsNoLongerConnected()
        {
            // The module caches this and only refreshes it from inside its own
            // output path -- which stops the moment the wheel goes -- so left
            // alone it reports a wheel as connected long after it was
            // unplugged, while the header correctly says otherwise.
            var host = new FakeLedModuleHost();
            var inst = InstanceWithHost("PSWBMW", host);
            var withWheel = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => withWheel;

            var data = new GameData();
            inst.DataUpdate(null, ref data);
            Assert.Equal(true, host.ReportedConnected);

            // The wheel goes away while FanaBridge keeps running.
            var withoutWheel = PluginWithWheel(null, out _);
            inst.PluginResolver = () => withoutWheel;
            inst.DataUpdate(null, ref data);

            Assert.Equal(false, host.ReportedConnected);
            // ...and the badge stays visible to say so, rather than vanishing:
            // FanaBridge really is looking for the wheel in this state.
            Assert.Equal(true, host.CanDrive);
        }

        [Fact]
        public void AThrowingTeardown_FiresTheEdgeOnce_AndTheFrameSurvives()
        {
            // Nothing enforces that a host's StopDriving cannot throw. If one
            // did, the edge must not stay armed -- re-detecting the same
            // disconnect every frame, logging and failing forever -- and the
            // frame itself must come back for the next update.
            var host = new FakeLedModuleHost { ThrowOnStopDriving = true };
            var inst = InstanceWithHost("PSWBMW", host);
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;

            var data = new GameData();
            inst.DataUpdate(null, ref data);          // connected

            inst.Enabled = false;                     // take the edge, throwing
            inst.DataUpdate(null, ref data);
            Assert.Equal(1, host.StopDrivingCount);

            inst.DataUpdate(null, ref data);          // edge must not re-fire
            Assert.Equal(1, host.StopDrivingCount);
        }

        [Fact]
        public async System.Threading.Tasks.Task BlankOutput_WaitsOutAFrameAlreadyDrawing_AndBlanksAfterIt()
        {
            // The sharper half of the finalize race: a frame that captured the
            // generation BEFORE it was unpublished and is mid-draw when the
            // blank arrives. Without the output gate the blank lands first and
            // the frame relights the wheel on a transport about to die.
            var host = new FakeLedModuleHost();
            var inst = InstanceWithHost("PSWBMW", host);
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;
            var data = new GameData();
            inst.DataUpdate(null, ref data);          // connected, drew once

            var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var frameDrawing = new System.Threading.ManualResetEventSlim();
            var releaseFrame = new System.Threading.ManualResetEventSlim();
            host.OnDisplay = () =>
            {
                frameDrawing.Set();
                releaseFrame.Wait(5000);
                order.Enqueue("draw");
            };
            host.OnStopDriving = () => order.Enqueue("blank");

            var frame = System.Threading.Tasks.Task.Run(() =>
            {
                var d = new GameData();
                inst.DataUpdate(null, ref d);
            });
            Assert.True(frameDrawing.Wait(5000));

            inst.PluginResolver = () => null;         // finalize unpublishes...
            var blank = System.Threading.Tasks.Task.Run(() => inst.BlankOutput());

            releaseFrame.Set();
            var both = System.Threading.Tasks.Task.WhenAll(frame, blank);
            Assert.Same(both, await System.Threading.Tasks.Task.WhenAny(
                both, System.Threading.Tasks.Task.Delay(5000)));

            Assert.Equal(new[] { "draw", "blank" }, order.ToArray());
        }

        [Fact]
        public void AFrameThatCapturedADyingGeneration_DoesNotRelightTheWheel()
        {
            // The other interleaving: the blank already ran, and a frame that
            // captured the old generation reaches the output gate afterwards.
            // The gate revalidates against the live singleton and skips.
            var host = new FakeLedModuleHost();
            var inst = InstanceWithHost("PSWBMW", host);
            var plugin = PluginWithWheel("PSWBMW", out _);

            // Alive for the frame's first two reads (top-of-frame capture and
            // the Enabled announcement), unpublished by the revalidation.
            int calls = 0;
            inst.PluginResolver = () => ++calls <= 2 ? plugin : null;

            var data = new GameData();
            inst.DataUpdate(null, ref data);

            Assert.Equal(0, host.DisplayCount);
        }

        [Fact]
        public void FinalizeRace_TheFrameThatSeesThePluginGone_StillDarkensTheWheel()
        {
            // FinalizePlugin unpublishes the singleton before it blanks. A
            // frame landing in that window takes the disconnect edge itself --
            // and must blank, because BlankOutput afterwards sees the device as
            // no longer driven and correctly does nothing. Whichever side wins
            // the race, the wheel goes dark exactly once.
            var host = new FakeLedModuleHost();
            var inst = InstanceWithHost("PSWBMW", host);
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;

            var data = new GameData();
            inst.DataUpdate(null, ref data);          // connected, driving

            inst.PluginResolver = () => null;         // finalize has unpublished
            inst.DataUpdate(null, ref data);          // the in-flight frame
            Assert.Equal(1, host.StopDrivingCount);   // ...darkens the wheel

            inst.BlankOutput();                       // finalize's own pass
            Assert.Equal(1, host.StopDrivingCount);   // nothing left to do
        }

        [Fact]
        public void PluginGoingAway_DarkensTheDeviceItWasDriving()
        {
            // Disabling FanaBridge should leave the wheel the way switching the
            // device off does. The device cannot notice on its own: by teardown
            // its updates have stopped, so the plugin has to ask.
            var host = new FakeLedModuleHost();
            var inst = InstanceWithHost("PSWBMW", host);
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;

            var data = new GameData();
            inst.DataUpdate(null, ref data);
            Assert.True(host.DisplayCount > 0);

            inst.BlankOutput();

            Assert.Equal(1, host.StopDrivingCount);
        }

        [Fact]
        public void PluginGoingAway_LeavesAnUndrivenDeviceAlone()
        {
            // Nothing was lit, so there is nothing to darken -- and blanking it
            // anyway would make teardown wait on a driver that never ran.
            var host = new FakeLedModuleHost();
            var inst = InstanceWithHost("PSWBMW", host);
            inst.PluginResolver = () => null;

            inst.BlankOutput();

            Assert.Equal(0, host.StopDrivingCount);
        }

        [Fact]
        public void SwitchedBackOn_ResumesDrivingItsLeds()
        {
            var host = new FakeLedModuleHost();
            var inst = InstanceWithHost("PSWBMW", host);
            var plugin = PluginWithWheel("PSWBMW", out _);
            inst.PluginResolver = () => plugin;

            var data = new GameData();
            inst.Enabled = false;
            inst.DataUpdate(null, ref data);
            Assert.Equal(0, host.DisplayCount);

            inst.Enabled = true;
            inst.DataUpdate(null, ref data);

            Assert.True(host.DisplayCount > 0);
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
            // must neither rebind nor clear, and the state reports Scanning
            // (never Disabled; see NoPlugin_ReportsScanning_NotDisabled).
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
            // reach the hardware — so this asserts on that live object, not
            // on what GetSettings re-serializes.
            var inst = InstanceWith(new FakeLedModuleHost());
            inst.PluginResolver = () => null;

            var doc = FullDocument();
            doc["displayMode"] = "Gear";
            doc["itmDefaultPage"] = 5;
            inst.SetSettings(doc, isDefault: false);

            Assert.Equal("Gear", inst.DisplaySettingsForTest.DisplayMode);
            Assert.Equal(5, inst.DisplaySettingsForTest.ItmDefaultPage);
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

        // ── Display mode "None" (basic 7-segment wheels) ───────────────────
        //
        // "None" hands the 7-segment display to the firmware or another
        // application while FanaBridge keeps driving the LEDs: no display writes
        // while a game runs, one blank on the transition into "None" (retried
        // until the transport accepts it), and no exit blank on End.
        // Runs on CSLSWGT3 — a basic-display wheel with no LEDs, so the col01
        // stream carries display frames only.

        // StatusDataBase is abstract with internal setters (see
        // FanatecDisplayDriverTests) — close StatusData<T> over object and drive
        // the internal setters via reflection.
        private static readonly Type StatusDataType =
            typeof(GameData).Assembly
                .GetType("GameReaderCommon.StatusData`1")
                .MakeGenericType(typeof(object));

        private static GameData RunningData(string gear)
        {
            var status = System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(StatusDataType);
            StatusDataType.GetProperty("Gear")!.GetSetMethod(true)!
                .Invoke(status, new object[] { gear });
            var d = new GameData { NewData = (StatusDataBase)status };
            typeof(GameData).GetProperty("GameRunning")!.GetSetMethod(true)!
                .Invoke(d, new object[] { true });
            return d;
        }

        /// <summary>Display control frames (01 F8 09 01 02 s1 s2 s3) on the wire.</summary>
        private static List<byte[]> DisplayFrames(FakeTransport t) =>
            t.Col01Sent.Where(r => r.Length == 8 && r[1] == 0xF8 && r[2] == 0x09
                                   && r[3] == 0x01 && r[4] == 0x02).ToList();

        private static FanatecWheelDeviceInstance ConnectedGt3Instance(out FakeTransport transport)
        {
            var plugin = PluginWithWheel("CSLSWGT3", out _, out transport);
            var inst = InstanceFor("CSLSWGT3");
            inst.PluginResolver = () => plugin;
            return inst;
        }

        private static JObject Gt3Settings(string displayMode) => new JObject
        {
            ["wheelType"] = "CSLSWGT3",
            ["displayMode"] = displayMode,
        };

        [Fact]
        public void BasicWheel_ModeNone_WritesNothingWhileGameRuns()
        {
            var inst = ConnectedGt3Instance(out var transport);
            inst.SetSettings(Gt3Settings("None"), isDefault: false);

            foreach (var gear in new[] { "1", "2", "3" })
            {
                var data = RunningData(gear);
                inst.DataUpdate(null, ref data);
            }

            Assert.Empty(DisplayFrames(transport));
        }

        [Fact]
        public void BasicWheel_ActiveMode_DrivesTheDisplay()
        {
            // Sanity for the fixture: the same harness DOES write in Gear mode,
            // so the empty assertions above can't pass vacuously.
            var inst = ConnectedGt3Instance(out var transport);
            inst.SetSettings(Gt3Settings("Gear"), isDefault: false);

            var data = RunningData("3");
            inst.DataUpdate(null, ref data);

            Assert.NotEmpty(DisplayFrames(transport));
        }

        [Fact]
        public void BasicWheel_SwitchingToNone_BlanksOnceThenStaysSilent()
        {
            var inst = ConnectedGt3Instance(out var transport);
            inst.SetSettings(Gt3Settings("Gear"), isDefault: false);
            var data = RunningData("3");
            inst.DataUpdate(null, ref data);

            inst.SetSettings(Gt3Settings("None"), isDefault: false);
            transport.Col01Sent.Clear();
            inst.DataUpdate(null, ref data);
            inst.DataUpdate(null, ref data);
            inst.DataUpdate(null, ref data);

            var blank = Assert.Single(DisplayFrames(transport));
            Assert.Equal(SevenSegment.Blank, blank[5]);
            Assert.Equal(SevenSegment.Blank, blank[6]);
            Assert.Equal(SevenSegment.Blank, blank[7]);
        }

        [Fact]
        public void BasicWheel_NoneBlank_RetriesUntilTheTransportAccepts()
        {
            var inst = ConnectedGt3Instance(out var transport);
            inst.SetSettings(Gt3Settings("Gear"), isDefault: false);
            var data = RunningData("3");
            inst.DataUpdate(null, ref data);

            inst.SetSettings(Gt3Settings("None"), isDefault: false);
            transport.AcceptCol01 = false;
            transport.Col01Sent.Clear();
            inst.DataUpdate(null, ref data);   // declined — must not latch
            inst.DataUpdate(null, ref data);
            Assert.Equal(2, DisplayFrames(transport).Count);

            transport.AcceptCol01 = true;
            inst.DataUpdate(null, ref data);   // accepted — latches
            inst.DataUpdate(null, ref data);
            Assert.Equal(3, DisplayFrames(transport).Count);
        }

        [Fact]
        public void BasicWheel_ModeNone_EndDoesNotBlankTheDisplay()
        {
            var inst = ConnectedGt3Instance(out var transport);
            inst.SetSettings(Gt3Settings("Gear"), isDefault: false);
            var data = RunningData("3");
            inst.DataUpdate(null, ref data);   // creates the display manager

            inst.SetSettings(Gt3Settings("None"), isDefault: false);
            inst.DataUpdate(null, ref data);   // transition blank
            transport.Col01Sent.Clear();

            inst.End();

            Assert.Empty(DisplayFrames(transport));
        }

        [Fact]
        public void BasicWheel_SwitchingToNone_ReleasesDisplayOwnership()
        {
            // The accepted handoff blank must clear HasWritten so the plugin's
            // shutdown cleanup no longer blanks the (now foreign) display content.
            var plugin = PluginWithWheel("CSLSWGT3", out _, out var transport);
            var inst = InstanceFor("CSLSWGT3");
            inst.PluginResolver = () => plugin;

            inst.SetSettings(Gt3Settings("Gear"), isDefault: false);
            var data = RunningData("3");
            inst.DataUpdate(null, ref data);
            Assert.True(plugin.Display.HasWritten);    // gear content is ours

            inst.SetSettings(Gt3Settings("None"), isDefault: false);
            inst.DataUpdate(null, ref data);           // accepted handoff blank

            Assert.False(plugin.Display.HasWritten);
        }

        [Fact]
        public void BasicWheel_DisplayTestEndingInNone_BlanksExactlyOnce()
        {
            // The handback edge and the blank-once path both run in the frame the
            // test is released; only one of them may write.
            var plugin = PluginWithWheel("CSLSWGT3", out _, out var transport);
            var inst = InstanceFor("CSLSWGT3");
            inst.PluginResolver = () => plugin;

            inst.SetSettings(Gt3Settings("Gear"), isDefault: false);
            var data = RunningData("3");
            inst.DataUpdate(null, ref data);        // builds the display manager

            plugin.DisplayTestActive = true;
            inst.SetSettings(Gt3Settings("None"), isDefault: false);
            inst.DataUpdate(null, ref data);        // test owns the display
            transport.Col01Sent.Clear();

            plugin.DisplayTestActive = false;
            inst.DataUpdate(null, ref data);        // handback frame
            inst.DataUpdate(null, ref data);

            var blank = Assert.Single(DisplayFrames(transport));
            Assert.Equal(SevenSegment.Blank, blank[5]);
            Assert.False(plugin.Display.HasWritten);   // ownership handed off
        }

        [Fact]
        public void BasicWheel_DisplayTestHandback_KeepsOwnershipWhenTheClearIsDeclined()
        {
            // Releasing on a declined clear would drop ownership while our own
            // test residue is still on the display.
            var plugin = PluginWithWheel("CSLSWGT3", out _, out var transport);
            var inst = InstanceFor("CSLSWGT3");
            inst.PluginResolver = () => plugin;

            inst.SetSettings(Gt3Settings("Gear"), isDefault: false);
            var data = RunningData("3");
            inst.DataUpdate(null, ref data);

            plugin.DisplayTestActive = true;
            inst.SetSettings(Gt3Settings("None"), isDefault: false);
            inst.DataUpdate(null, ref data);

            transport.AcceptCol01 = false;
            plugin.DisplayTestActive = false;
            inst.DataUpdate(null, ref data);        // handback blank declined
            Assert.True(plugin.Display.HasWritten); // still ours

            transport.AcceptCol01 = true;
            inst.DataUpdate(null, ref data);        // retry accepted
            Assert.False(plugin.Display.HasWritten);
        }

        [Fact]
        public void BasicWheel_ActiveMode_EndBlanksTheDisplay()
        {
            var inst = ConnectedGt3Instance(out var transport);
            inst.SetSettings(Gt3Settings("Gear"), isDefault: false);
            var data = RunningData("3");
            inst.DataUpdate(null, ref data);
            transport.Col01Sent.Clear();

            inst.End();

            var blank = Assert.Single(DisplayFrames(transport));
            Assert.Equal(SevenSegment.Blank, blank[5]);
        }
    }
}
