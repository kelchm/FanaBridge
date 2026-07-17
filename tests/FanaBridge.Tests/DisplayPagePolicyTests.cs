using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using FanaBridge;
using FanaBridge.Adapters;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Session;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// The page-policy behavior matrix, pinned end-to-end through a real
    /// FanatecWheelDeviceInstance at the levels that survive restructuring: the
    /// lifecycle's effective target (<see cref="ItmLifecycleController.DefaultPage"/> /
    /// <see cref="ItmLifecycleController.GameStartPageRevert"/>) and the PageSet frames
    /// on the wire. The matrix: a config base page beats the ItmDefaultPage setting;
    /// the lifecycle's game-start revert is suppressed only while a rule stack owns
    /// page policy; policy hand-offs (stack build, teardown, base change) request the
    /// new resting page live; and in the no-stack mode a default-page settings change
    /// is edge-detected and switched live.
    /// </summary>
    public class DisplayPagePolicyTests
    {
        // ── Harness (mirrors DisplayCustomizationWiringTests) ─────────────

        private sealed class RecordingConnectableTransport : IConnectableTransport
        {
            public bool Connected;
            public FakeReportStream Identity { get; } = new FakeReportStream();
            public FakeReportStream Itm { get; } = new FakeReportStream();
            public List<byte[]> Sent { get; } = new List<byte[]>();

            public bool Connect(int productId) { Connected = true; return true; }
            public void Disconnect() => Connected = false;
            public void Dispose() => Disconnect();
            public bool IsConnected => Connected;
            public bool IsDevicePresent => Connected;
            public FanatecTransport.TransportConnectStatus LastConnectStatus =>
                FanatecTransport.TransportConnectStatus.Connected;

            public bool SendCol03(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                Sent.Add(copy);
                return true;
            }

            public bool SendCol01(byte[] data) => true;
            public IReportStream IdentityReports => Identity;
            public IReportStream ItmReports => Itm;
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

        private static readonly Type StatusDataType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.StatusData`1").MakeGenericType(typeof(object));
        private static object NewStatus() => FormatterServices.GetUninitializedObject(StatusDataType);

        private static GameData Data(object status, bool gameRunning = true)
        {
            var d = new GameData { NewData = (StatusDataBase)status };
            typeof(GameData).GetProperty("GameRunning")!.GetSetMethod(true)!
                .Invoke(d, new object[] { gameRunning });
            return d;
        }

        private sealed class Session
        {
            public RecordingConnectableTransport Transport = null!;
            public Clock Clock = null!;
            public FanatecWheelbase Wheelbase = null!;
            public FanatecPlugin Plugin = null!;
            public FanatecWheelDeviceInstance Instance = null!;

            public void Frame(GameData d)
            {
                Clock.T += 16;
                Wheelbase.UpdateIdentity();          // drains pushed ITM reports
                var frame = d;
                Instance.DataUpdate(null, ref frame);
            }
        }

        private static Session StartSession(JObject settings)
        {
            var s = new Session
            {
                Transport = new RecordingConnectableTransport(),
                Clock = new Clock(),
            };
            s.Wheelbase = new FanatecWheelbase(s.Transport, new FakeBus(), s.Clock.Now);
            Assert.True(s.Wheelbase.AutoConnect());

            // Commit the CSSWFORMV3 identity (an ITM wheel, display device 3).
            s.Transport.Identity.Enqueue(Ff08(0x0C, WheelWire("CSSWFORMV3")));
            s.Clock.T += 10;
            s.Wheelbase.UpdateIdentity();
            s.Clock.T += 250;
            Assert.True(s.Wheelbase.UpdateIdentity());

            s.Plugin = new FanatecPlugin();
            s.Plugin.InstallWheelbaseForTest(s.Wheelbase);

            var profile = WheelProfileStore.FindByWheelType("CSSWFORMV3");
            Assert.NotNull(profile);
            s.Instance = new FanatecWheelDeviceInstance(new DeviceConfig
            {
                Profile = profile,
                Capabilities = new WheelCapabilities(profile!),
            });
            s.Instance.PluginResolver = () => s.Plugin;
            s.Instance.ItmClockForTest = s.Clock.Now;
            if (settings != null)
                s.Instance.SetSettings(settings, isDefault: false);
            return s;
        }

        private static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        // Page 1 (Lap Info) and page 5 (Tyre Temps) subscription pushes for display
        // device 3 — the same fixtures the wiring tests use.
        private static byte[] LapInfoPush => HexToBytes(
            "ff0501" + "0300010034" + "0301040012" + "0382f90132" + "0383f50132" + "0304fd012a" + "0305fe012a");
        private static byte[] TyrePush => HexToBytes(
            "ff0501" + "0300010034" + "0301040012" + "03822a0032" + "0383300032" + "03842d0032" + "0385330032");

        // Every PageSet (FF 05 04 <deviceId> <page>) target page sent so far, in order.
        private static List<byte> PageSets(List<byte[]> sent)
            => sent.Where(f => f[1] == 0x05 && f[2] == 0x04).Select(f => f[4]).ToList();

        private static byte WireFor(ItmPage page)
        {
            foreach (var p in ItmDeviceCatalog.PagesFor(3))
                if (p.Page == page)
                    return p.Number;
            throw new InvalidOperationException("page not on device 3: " + page);
        }

        private static JObject ConfigWithBase(string basePage)
            => JObject.Parse("{ \"schemaVersion\": 1, \"itm\": { \"basePage\": \"" + basePage + "\" } }");

        // Runs a session to push-confirmed sync: bring-up, one confirming push, judge.
        // With no config the sync lands on the setting page (wire 1, Lap Info).
        private static Session SyncedSession(JObject settings, byte[] push, GameData running)
        {
            var s = StartSession(settings);
            s.Frame(running);                        // bring-up: gate-on, enable, PageSet
            s.Frame(running);                        // (a rule stack built in frame 1 lands
            s.Clock.T += 120;                        //  its live page request here)
            s.Frame(running);                        // quiet window elapses → PageSet out
            s.Transport.Itm.Enqueue(push);           // firmware answers with subscriptions
            s.Frame(running);                        // adopted; accumulate window opens
            s.Clock.T += 80;
            s.Frame(running);                        // judged → Synced
            return s;
        }

        // Advances far enough for a live page request to reach the wire: the switch
        // procedure holds a 50 ms quiet window and PageSets are spaced ≥100 ms.
        private static void RunSwitchWindow(Session s, GameData running)
        {
            for (int i = 0; i < 12; i++)
            {
                s.Clock.T += 30;
                s.Frame(running);
            }
        }

        // ── Config base page vs the ItmDefaultPage setting ────────────────

        [Fact]
        public void ConfigBasePage_BeatsTheSetting_AtTheLifecycle()
        {
            // itmDefaultPage stays at its default (wire 1); the config pins fuelErsDrs.
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = ConfigWithBase("fuelErsDrs"),
            });
            var running = Data(NewStatus());
            s.Frame(running);   // builds the driver, then the stack
            s.Frame(running);   // the stack's base reaches the lifecycle

            var lifecycle = s.Instance.ItmDisplayForTest!.Lifecycle;
            Assert.Equal(WireFor(ItmPage.FuelErsDrs), lifecycle.DefaultPage);
            Assert.False(lifecycle.GameStartPageRevert);   // the stack owns the revert
        }

        [Fact]
        public void NoConfig_TheSettingOwnsTheLifecyclePagePolicy()
        {
            var s = StartSession(new JObject { ["wheelType"] = "CSSWFORMV3" });
            var running = Data(NewStatus());
            s.Frame(running);
            s.Frame(running);

            var lifecycle = s.Instance.ItmDisplayForTest!.Lifecycle;
            Assert.Equal(DisplaySettings.DefaultItmDefaultPage, lifecycle.DefaultPage);
            Assert.True(lifecycle.GameStartPageRevert);    // built-in revert stays on
        }

        // ── Handoff edges ─────────────────────────────────────────────────

        [Fact]
        public void StackBuild_RequestsTheConfigBasePageLive()
        {
            // Bring-up targets the setting page (the stack builds after the driver's
            // first tick) and the stack's base-page request is queued through the
            // bring-up; once the page-1 push confirms sync, the queued request runs
            // and the display switches to the config base live.
            var running = Data(NewStatus());
            var s = SyncedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = ConfigWithBase("tyreTemps"),
            }, LapInfoPush, running);    // firmware confirms the bring-up page (1)
            RunSwitchWindow(s, running);

            var pages = PageSets(s.Transport.Sent);
            Assert.Equal(DisplaySettings.DefaultItmDefaultPage, pages.First());
            Assert.Contains(WireFor(ItmPage.TyreTemps), pages);
        }

        [Fact]
        public void ConfigRemoval_ReturnsPolicyToTheSetting_AndSwitchesLive()
        {
            var running = Data(NewStatus());
            // Synced on the config base (Tyre Temps, wire 5).
            var s = SyncedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = ConfigWithBase("tyreTemps"),
            }, TyrePush, running);
            Assert.Equal(ItmLifecycleState.Synced, s.Instance.ItmDisplayForTest!.Lifecycle.State);
            int before = PageSets(s.Transport.Sent).Count;

            // The UI removes the customization: the stack tears down, policy returns
            // to the ItmDefaultPage setting, and the display switches back live.
            s.Instance.ApplyDisplayConfig(null);
            s.Frame(running);            // teardown observed on the frame path
            s.Frame(running);            // policy edge reaches the driver
            RunSwitchWindow(s, running);

            var lifecycle = s.Instance.ItmDisplayForTest!.Lifecycle;
            Assert.Equal(DisplaySettings.DefaultItmDefaultPage, lifecycle.DefaultPage);
            Assert.True(lifecycle.GameStartPageRevert);
            var after = PageSets(s.Transport.Sent).Skip(before).ToList();
            Assert.Contains(DisplaySettings.DefaultItmDefaultPage, after);
        }

        [Fact]
        public void BasePageChange_WithinALiveStack_SwitchesLive()
        {
            var running = Data(NewStatus());
            var s = SyncedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = ConfigWithBase("tyreTemps"),
            }, TyrePush, running);
            int before = PageSets(s.Transport.Sent).Count;

            // The UI edits the config's base page: the stack rebuilds and the new
            // base is requested live; the stack keeps owning the revert throughout.
            s.Instance.ApplyDisplayConfig(DisplayConfigSerializer.Load(
                ConfigWithBase("fuelErsDrs").ToString(), _ => { }));
            s.Frame(running);
            s.Frame(running);
            RunSwitchWindow(s, running);

            var lifecycle = s.Instance.ItmDisplayForTest!.Lifecycle;
            Assert.Equal(WireFor(ItmPage.FuelErsDrs), lifecycle.DefaultPage);
            Assert.False(lifecycle.GameStartPageRevert);
            var after = PageSets(s.Transport.Sent).Skip(before).ToList();
            Assert.Contains(WireFor(ItmPage.FuelErsDrs), after);
        }

        [Fact]
        public void DefaultPageSettingChange_NoStack_SwitchesLive()
        {
            var running = Data(NewStatus());
            var s = SyncedSession(new JObject { ["wheelType"] = "CSSWFORMV3" },
                LapInfoPush, running);
            Assert.Equal(ItmLifecycleState.Synced, s.Instance.ItmDisplayForTest!.Lifecycle.State);
            int before = PageSets(s.Transport.Sent).Count;

            // The user picks a new default page in settings: with no stack the driver
            // edge-detects the change and requests the page live (no reconnect needed).
            s.Instance.SetSettings(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["itmDefaultPage"] = 5,
            }, isDefault: false);
            s.Frame(running);
            RunSwitchWindow(s, running);

            var lifecycle = s.Instance.ItmDisplayForTest!.Lifecycle;
            Assert.Equal(5, lifecycle.DefaultPage);
            Assert.True(lifecycle.GameStartPageRevert);
            var after = PageSets(s.Transport.Sent).Skip(before).ToList();
            Assert.Contains((byte)5, after);
        }

        [Fact]
        public void WheelChangeAndConfigRemoval_SameFrame_RestoresBuiltInPolicy()
        {
            var running = Data(NewStatus());
            var s = SyncedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = ConfigWithBase("tyreTemps"),
            }, TyrePush, running);
            var driver = s.Instance.ItmDisplayForTest!;
            Assert.True(driver.HasExternalPagePolicy);

            // The rim is pulled and re-seated between device frames (no DataUpdate runs
            // while it is off, so this instance never observes the Scanning window) —
            // two committed identity changes at the identity layer…
            s.Transport.Identity.Enqueue(Ff08(0x0C, 0x00));     // rim pulled
            s.Clock.T += 10;
            s.Wheelbase.UpdateIdentity();
            s.Clock.T += 250;
            Assert.True(s.Wheelbase.UpdateIdentity());
            s.Transport.Identity.Enqueue(Ff08(0x0C, WheelWire("CSSWFORMV3")));   // re-seated
            s.Clock.T += 10;
            s.Wheelbase.UpdateIdentity();
            s.Clock.T += 250;
            Assert.True(s.Wheelbase.UpdateIdentity());
            // …and the customization is removed before the next frame, so BOTH land on
            // one DataUpdate: the wheel-change block drops the stack while the driver
            // deliberately keeps holding the external policy, and the teardown that
            // same frame must hand policy back even though no stack exists any more.
            // (Miss this corner and the driver rests on the dead stack's base with the
            // game-start revert suppressed forever — the phantom-manual bug class.)
            s.Instance.ApplyDisplayConfig(null);
            s.Frame(running);

            Assert.Same(driver, s.Instance.ItmDisplayForTest);  // the driver survives a wheel change
            Assert.False(driver.HasExternalPagePolicy);
            Assert.Null(s.Instance.DisplayStackForTest);
            // The dead stack's published rule rows go with it — the Display tab must
            // not keep rendering a customization that no longer exists.
            Assert.Null(s.Instance.DisplayRuleSnapshot);

            s.Frame(running);   // the restored policy reaches the lifecycle
            Assert.Equal(DisplaySettings.DefaultItmDefaultPage, driver.Lifecycle.DefaultPage);
            Assert.True(driver.Lifecycle.GameStartPageRevert);
        }

        [Fact]
        public void ItmDisplayIdHotSwap_ColdBringUpTargetsTheStackBase()
        {
            // A profile override retargets the ITM display id mid-session while a rule
            // stack is live: the driver is rebuilt in place, and the SAME frame's cold
            // bring-up must target the (still-live) stack's base page, not the dormant
            // ItmDefaultPage setting — the stack's policy is carried across the
            // rebuild, and the rebuilt stack re-takes it later that frame.
            var running = Data(NewStatus());
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = ConfigWithBase("fuelErsDrs"),
            });
            s.Frame(running);   // driver built (bring-up targets the setting), stack built
            s.Frame(running);   // the stack's base reaches the lifecycle
            Assert.True(s.Instance.ItmDisplayForTest!.HasExternalPagePolicy);
            int before = s.Transport.Sent.Count;

            // Device 3 → 4 (PSWBENT), display type stays ITM. fuelErsDrs is wire 2 on
            // both catalogs, distinct from the ItmDefaultPage setting (wire 1).
            var bentley = WheelProfileStore.FindByWheelType("PSWBENT");
            Assert.NotNull(bentley);
            s.Wheelbase.ProfileOverrideResolver = _ => bentley!.Id;
            s.Wheelbase.RefreshCapabilities();
            s.Frame(running);   // rebuild + cold bring-up, all in this one frame

            var pageSets = s.Transport.Sent.Skip(before)
                .Where(f => f[1] == 0x05 && f[2] == 0x04).ToList();
            Assert.NotEmpty(pageSets);
            Assert.Equal(4, pageSets[0][3]);                        // the NEW display id
            Assert.Equal(WireFor(ItmPage.FuelErsDrs), pageSets[0][4]);  // the stack's base
        }

        [Fact]
        public void ItmDisplayIdHotSwap_CrossCatalog_ReResolvesBaseOnTheNewDeviceTable()
        {
            // The renumbering case the fuelErsDrs test above can't see: Tyre Temps is wire 5
            // on device 3 but wire 4 on device 4 (where wire 5 is Legacy — see
            // ItmPageTableTests). Carrying the raw device-3 wire (5) across the rebuild would
            // cold-start the Bentley on Legacy; the base must re-resolve against the NEW
            // device's table to wire 4.
            var running = Data(NewStatus());
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = ConfigWithBase("tyreTemps"),
            });
            s.Frame(running);   // driver + stack built on device 3
            s.Frame(running);   // the stack's base reaches the lifecycle
            Assert.True(s.Instance.ItmDisplayForTest!.HasExternalPagePolicy);
            int before = s.Transport.Sent.Count;

            var bentley = WheelProfileStore.FindByWheelType("PSWBENT");
            Assert.NotNull(bentley);
            s.Wheelbase.ProfileOverrideResolver = _ => bentley!.Id;
            s.Wheelbase.RefreshCapabilities();
            s.Frame(running);   // rebuild + cold bring-up, all this one frame

            var pageSets = s.Transport.Sent.Skip(before)
                .Where(f => f[1] == 0x05 && f[2] == 0x04 && f[3] == 4).ToList();
            Assert.NotEmpty(pageSets);
            Assert.Equal(4, pageSets[0][4]);   // Tyre Temps re-resolved onto device 4, not 5
            // Legacy (device 4 wire 5) is never requested — the raw carried wire would.
            Assert.DoesNotContain((byte)5, pageSets.Select(f => f[4]));
        }

        [Fact]
        public void ItmDisplayIdHotSwap_ConfiguredBaseAbsentOnNewDevice_FallsToDefaultWireIdentity()
        {
            // Car Settings (wire 3 on device 3) does not exist on device 4. The rebuild must
            // fall to the effective base — the identity at the stack's default wire (the
            // ItmDefaultPage setting default, wire 1 = Lap Info on device 4) — not carry the
            // stranded device-3 wire 3 (which is Lap Times on device 4).
            var running = Data(NewStatus());
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = ConfigWithBase("carSettings"),
            });
            s.Frame(running);   // driver + stack built on device 3 (base = Car Settings, wire 3)
            s.Frame(running);
            Assert.True(s.Instance.ItmDisplayForTest!.HasExternalPagePolicy);
            int before = s.Transport.Sent.Count;

            var bentley = WheelProfileStore.FindByWheelType("PSWBENT");
            Assert.NotNull(bentley);
            s.Wheelbase.ProfileOverrideResolver = _ => bentley!.Id;
            s.Wheelbase.RefreshCapabilities();
            s.Frame(running);   // rebuild + cold bring-up

            var pageSets = s.Transport.Sent.Skip(before)
                .Where(f => f[1] == 0x05 && f[2] == 0x04 && f[3] == 4).ToList();
            Assert.NotEmpty(pageSets);
            // The effective fallback: device 4's default-wire identity (wire 1), not the
            // stranded device-3 Car Settings wire (3, which is Lap Times on device 4).
            Assert.Equal(DisplaySettings.DefaultItmDefaultPage, pageSets[0][4]);
            Assert.DoesNotContain((byte)3, pageSets.Select(f => f[4]));
        }

        [Fact]
        public void ItmDisplayIdHotSwap_ReverseCrossCatalog_ReResolvesBaseOnTheNewDeviceTable()
        {
            // The reverse direction, for a renumbered page: established on device 4 (Tyre
            // Temps = wire 4), then swapped back to device 3 where Tyre Temps is wire 5. The
            // base must re-resolve to wire 5, not carry the device-4 wire 4 (Lap Times on
            // device 3).
            var running = Data(NewStatus());
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = ConfigWithBase("tyreTemps"),
            });
            var bentley = WheelProfileStore.FindByWheelType("PSWBENT");
            Assert.NotNull(bentley);
            // Establish the session on device 4 first (its stack latches base = Tyre Temps).
            s.Wheelbase.ProfileOverrideResolver = _ => bentley!.Id;
            s.Wheelbase.RefreshCapabilities();
            s.Frame(running);   // driver + stack built on device 4
            s.Frame(running);   // the stack's base reaches the lifecycle
            var mid = s.Transport.Sent
                .Where(f => f[1] == 0x05 && f[2] == 0x04).ToList();
            Assert.NotEmpty(mid);
            Assert.Equal(4, mid.Last()[3]);   // confirm we are on device 4 before reversing
            int before = s.Transport.Sent.Count;

            // Clear the override → back to the committed CSSWFORMV3 identity (device 3).
            s.Wheelbase.ProfileOverrideResolver = _ => null;
            s.Wheelbase.RefreshCapabilities();
            s.Frame(running);   // rebuild + cold bring-up on device 3

            var pageSets = s.Transport.Sent.Skip(before)
                .Where(f => f[1] == 0x05 && f[2] == 0x04 && f[3] == 3).ToList();
            Assert.NotEmpty(pageSets);
            Assert.Equal(WireFor(ItmPage.TyreTemps), pageSets[0][4]);  // wire 5 on device 3
            // The device-4 wire (4 = Lap Times on device 3) is never requested.
            Assert.DoesNotContain((byte)4, pageSets.Select(f => f[4]));
        }

        [Fact]
        public void SettingChange_WhileTheStackOwnsPolicy_DoesNotMoveThePage()
        {
            var running = Data(NewStatus());
            var s = SyncedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = ConfigWithBase("tyreTemps"),
            }, TyrePush, running);
            int before = PageSets(s.Transport.Sent).Count;

            // A default-page settings change while a config base page is pinned:
            // the config base keeps winning — no page movement.
            s.Instance.SetSettings(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["itmDefaultPage"] = 3,
                ["displayCustomization"] = ConfigWithBase("tyreTemps"),
            }, isDefault: false);
            s.Frame(running);
            s.Frame(running);
            RunSwitchWindow(s, running);

            var lifecycle = s.Instance.ItmDisplayForTest!.Lifecycle;
            Assert.Equal(WireFor(ItmPage.TyreTemps), lifecycle.DefaultPage);
            Assert.Equal(before, PageSets(s.Transport.Sent).Count);
        }
    }
}
