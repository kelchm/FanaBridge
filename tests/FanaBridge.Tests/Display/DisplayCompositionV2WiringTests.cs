using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using FanaBridge;
using FanaBridge.Adapters;
using FanaBridge.Display.Composition;
using FanaBridge.Display.Drivers;
using FanaBridge.Display.Host;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Schema2;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// E8 round 2: DeviceDisplayRuntime + FanatecWheelDeviceInstance wiring for
    /// DisplayCompositionV2. RISK-4/5/8 + lifecycle reload named tests. No replay harness.
    /// </summary>
    public class DisplayCompositionV2WiringTests
    {
        // ── Harness (mirrors DisplayCustomizationWiringTests) ─────────────

        private sealed class RecordingConnectableTransport : IConnectableTransport
        {
            public bool Connected;
            public FakeReportStream Identity { get; } = new FakeReportStream();
            public FakeReportStream Itm { get; } = new FakeReportStream();
            public List<byte[]> Sent { get; } = new List<byte[]>();
            public List<byte[]> SentCol01 { get; } = new List<byte[]>();

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

            public bool SendCol01(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                SentCol01.Add(copy);
                return true;
            }

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
            typeof(GameData).Assembly.GetType("GameReaderCommon.StatusData`1")!
                .MakeGenericType(typeof(object));

        private static object NewStatus() =>
            FormatterServices.GetUninitializedObject(StatusDataType);

        private static void Set(object s, string p, object v) =>
            s.GetType().GetProperty(p)!.GetSetMethod(true)!.Invoke(s, new[] { v });

        private static GameData Data(object status, bool gameRunning = true)
        {
            var d = new GameData { NewData = (StatusDataBase)status };
            typeof(GameData).GetProperty("GameRunning")!.GetSetMethod(true)!
                .Invoke(d, new object[] { gameRunning });
            return d;
        }

        private static GameData Live(string gear = "7", int isInPit = 0)
        {
            var s = NewStatus();
            Set(s, "Gear", gear);
            Set(s, "SpeedLocal", 200.0);
            Set(s, "IsInPitLane", isInPit);
            return Data(s);
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
                Wheelbase.UpdateIdentity();
                var frame = d;
                Instance.DataUpdate(null, ref frame);
            }
        }

        private static Session StartSession(JObject settings, string wheelCode = "CSSWFORMV3")
        {
            var s = new Session
            {
                Transport = new RecordingConnectableTransport(),
                Clock = new Clock(),
            };
            s.Wheelbase = new FanatecWheelbase(s.Transport, new FakeBus(), s.Clock.Now);
            Assert.True(s.Wheelbase.AutoConnect());

            s.Transport.Identity.Enqueue(Ff08(0x0C, WheelWire(wheelCode)));
            s.Clock.T += 10;
            s.Wheelbase.UpdateIdentity();
            s.Clock.T += 250;
            Assert.True(s.Wheelbase.UpdateIdentity());

            s.Plugin = new FanatecPlugin();
            s.Plugin.InstallWheelbaseForTest(s.Wheelbase);

            var profile = WheelProfileStore.FindByWheelType(wheelCode);
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

        // ── v2 documents ──────────────────────────────────────────────────

        /// <summary>Minimal v2 with ITM rest destination (page policy owns game-start).</summary>
        private static JObject V2WithItmRest(string mode = "on")
        {
            return JObject.Parse(@"
{
  ""schemaVersion"": 2,
  ""pages"": [
    {
      ""kind"": ""hostedPage"",
      ""id"": ""p-pit"",
      ""name"": ""Pit"",
      ""base"": { ""content"": { ""kind"": ""text"", ""text"": ""PIT"" } }
    }
  ],
  ""priority"": {
    ""rows"": [ { ""kind"": ""manual"" } ],
    ""rest"": {
      ""inSessionPage"": { ""kind"": ""itmPage"", ""catalogPageId"": ""fuelErsDrs"" },
      ""idle"": { ""kind"": ""blank"" }
    }
  },
  ""settings"": { ""mode"": """ + mode + @""" }
}");
        }

        /// <summary>Legacy-only v2: hosted segments, no ITM destinations.</summary>
        private static JObject V2LegacyOnly()
        {
            return JObject.Parse(@"
{
  ""schemaVersion"": 2,
  ""pages"": [
    {
      ""kind"": ""hostedPage"",
      ""id"": ""p-pit"",
      ""name"": ""Pit"",
      ""base"": { ""content"": { ""kind"": ""text"", ""text"": ""PIT"" } }
    }
  ],
  ""priority"": {
    ""rows"": [ { ""kind"": ""manual"" } ],
    ""rest"": {
      ""inSessionPage"": { ""kind"": ""hostedPage"", ""id"": ""p-pit"" },
      ""idle"": { ""kind"": ""blank"" }
    }
  },
  ""settings"": { ""mode"": ""legacyOnly"" }
}");
        }

        // ── RISK-4: col01 ownership ───────────────────────────────────────

        /// <summary>
        /// When a v2 document is live, the frame-latched gate owns col01 so
        /// <see cref="LegacyDisplayDriver.Update"/> is never entered.
        /// </summary>
        [Fact]
        public void V2DocumentLive_ModeUpdateNeverCalled_OnLiveFrames()
        {
            var runtime = new DeviceDisplayRuntime(
                new DeviceConfig
                {
                    Profile = WheelProfileStore.FindByWheelType("PSWBMW"),
                    Capabilities = new WheelCapabilities(
                        WheelProfileStore.FindByWheelType("PSWBMW")!),
                },
                itmClock: () => null,
                log: _ => { });

            var doc = DisplayConfigV2Serializer.Load(V2LegacyOnly().ToString(), _ => { });
            runtime.SetConfigV2(doc);

            var transport = new RecordingTransport();
            var settings = new DisplaySettings { DisplayMode = "Gear" };
            var driver = new LegacyDisplayDriver(new DisplayEncoder(transport), settings);
            runtime.SetLegacySegmentWriter((a, b, c) => driver.TryShowSegments(a, b, c));
            runtime.SetSpecialScreenHooks(
                p => driver.ShowSpecialScreen(p),
                () =>
                {
                    driver.ArmExitBlank();
                    driver.InvalidateSegmentGates();
                });

            int updateCalls = 0;
            for (int i = 0; i < 5; i++)
            {
                runtime.TickLegacyRules(null, Live(gear: "7"), settings);
                bool useRulePath = DeviceDisplayRuntime.IsLiveCompositionV2(runtime.FrameConfigV2);
                Assert.True(useRulePath, "v2 live frame must own col01 via composition");
                if (!useRulePath)
                {
                    updateCalls++;
                    driver.Update(Live(gear: "7"));
                }
            }

            Assert.Equal(0, updateCalls);
            Assert.NotNull(runtime.Composition);
        }

        /// <summary>
        /// FR-3: DisplayType.None teardown clears composition diagnostics even when the
        /// ITM driver is already null (basic-v2 → None).
        /// </summary>
        [Fact]
        public void DisplayTypeNone_Teardown_ClearsCompositionWhenItmDriverAlreadyNull()
        {
            var runtime = new DeviceDisplayRuntime(
                new DeviceConfig
                {
                    Profile = WheelProfileStore.FindByWheelType("PSWBMW"),
                    Capabilities = new WheelCapabilities(
                        WheelProfileStore.FindByWheelType("PSWBMW")!),
                },
                itmClock: () => null,
                log: _ => { });

            var doc = DisplayConfigV2Serializer.Load(V2LegacyOnly().ToString(), _ => { });
            runtime.SetConfigV2(doc);
            runtime.TickLegacyRules(null, Live(gear: "7"), new DisplaySettings { DisplayMode = "Gear" });
            Assert.NotNull(runtime.Composition);
            Assert.NotNull(runtime.ComposedResolution);

            // Basic path never built an ITM driver; None teardown must still drop v2 state.
            runtime.OnDisplayTypeLeftItm(plugin: null!, clearCompositionWithoutDriver: true);
            Assert.Null(runtime.Composition);
            Assert.Null(runtime.ComposedResolution);
        }

        private sealed class RecordingTransport : IDeviceTransport
        {
            public bool IsConnected { get; set; } = true;
            public List<byte[]> SentCol01Reports { get; } = new List<byte[]>();

            public bool SendCol01(byte[] data)
            {
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                SentCol01Reports.Add(copy);
                return true;
            }

            public bool SendCol03(byte[] data) => true;
            public IReportStream IdentityReports => FakeReportStream.Empty;
            public IReportStream ItmReports => FakeReportStream.Empty;
            public IReportStream SrmReports => FakeReportStream.Empty;
            public IReportStream TuningReports => FakeReportStream.Empty;
            public int ReadCol01(byte[] buffer, int timeoutMs) => -1;
            public int Col01MaxInputReportLength => 34;
            public int Col03MaxInputReportLength => 64;
            public IDisposable BeginBatch() => new NoOp();
            private sealed class NoOp : IDisposable { public void Dispose() { } }
        }

        // ── RISK-5: page policy / GameStartPageRevert ────────────────────

        [Fact]
        public void GameStartPageRevert_V2_WithItmDestinations_Suppressed()
        {
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["display"] = V2WithItmRest("on"),
            });

            var running = Data(NewStatus());
            s.Frame(running);
            s.Frame(running);

            Assert.NotNull(s.Instance.CompositionForTest);

            var driver = s.Instance.ItmDisplayForTest;
            Assert.NotNull(driver);
            Assert.True(driver!.HasExternalPagePolicy);
            // Composition tenure suppresses the lifecycle's own game-start revert.
            Assert.False(driver.Lifecycle.GameStartPageRevert);
        }

        [Fact]
        public void GameStartPageRevert_V2_LegacyOnly_NotSuppressed()
        {
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["display"] = V2LegacyOnly(),
            });

            var running = Data(NewStatus());
            s.Frame(running);
            s.Frame(running);

            Assert.NotNull(s.Instance.CompositionForTest);

            var driver = s.Instance.ItmDisplayForTest;
            Assert.NotNull(driver);
            // LegacyOnly must not take ITM page policy — built-in game-start stays on.
            Assert.False(driver!.HasExternalPagePolicy);
            Assert.True(driver.Lifecycle.GameStartPageRevert);
        }

        [Fact]
        public void TakesItmPagePolicyV2_RequiresModeAndItmDestination()
        {
            var withItm = DisplayConfigV2Serializer.Load(V2WithItmRest("on").ToString(), _ => { });
            Assert.True(DeviceDisplayRuntime.TakesItmPagePolicyV2(withItm));

            var legacyOnly = DisplayConfigV2Serializer.Load(V2LegacyOnly().ToString(), _ => { });
            Assert.False(DeviceDisplayRuntime.TakesItmPagePolicyV2(legacyOnly));

            var off = DisplayConfigV2Serializer.Load(V2WithItmRest("off").ToString(), _ => { });
            Assert.False(DeviceDisplayRuntime.TakesItmPagePolicyV2(off));
        }

        // ── RISK-8 / OQ-6: snapshot ───────────────────────────────────────

        [Fact]
        public void Snapshot_V2Frame_CarriesComposedResolution_RulesNull()
        {
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["display"] = V2WithItmRest("on"),
            });

            s.Frame(Data(NewStatus()));
            s.Frame(Data(NewStatus()));

            var snap = ((IDisplayPanelHost)s.Instance).Snapshot;
            Assert.NotNull(snap);
            Assert.NotNull(snap.ComposedResolution);
            Assert.Same(s.Instance.DisplayRuntimeForTest.ComposedResolution, snap.ComposedResolution);
        }

    }
}
