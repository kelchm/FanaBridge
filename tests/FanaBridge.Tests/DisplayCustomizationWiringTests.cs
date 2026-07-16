using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using FanaBridge;
using FanaBridge.Adapters;
using FanaBridge.Display;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.Transport;
using GameReaderCommon;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FanaBridge.Tests
{
    /// <summary>
    /// Device-instance wiring for display customization: the per-device settings key
    /// (parse on SetSettings, snapshot serialization on GetSettings, before-Init
    /// tolerance), <see cref="DisplayCustomizationConfig.IsEmpty"/>, and the
    /// byte-parity gate — a scripted ITM session driven through a real
    /// FanatecWheelDeviceInstance must emit an identical frame sequence with no
    /// displayCustomization key and with an explicitly empty document, constructing
    /// no piece of the rules runtime in either case.
    /// </summary>
    public class DisplayCustomizationWiringTests
    {
        // ── Instance/plugin harness (see FanatecWheelDeviceInstanceTests) ─

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

        private static FanatecWheelDeviceInstance InstanceFor(string wheelCode)
        {
            var profile = WheelProfileStore.FindByWheelType(wheelCode);
            Assert.NotNull(profile);
            var config = new DeviceConfig
            {
                Profile = profile,
                Capabilities = new WheelCapabilities(profile!),
            };
            return new FanatecWheelDeviceInstance(config);
        }

        // ── GameData (see ItmTelemetryTests) ─────────────────────────────
        private static readonly Type StatusDataType =
            typeof(GameData).Assembly.GetType("GameReaderCommon.StatusData`1").MakeGenericType(typeof(object));
        private static object NewStatus() => FormatterServices.GetUninitializedObject(StatusDataType);
        private static void Set(object s, string p, object v) =>
            s.GetType().GetProperty(p)!.GetSetMethod(true)!.Invoke(s, new[] { v });

        private static GameData Data(object status, bool gameRunning = true)
        {
            var d = new GameData { NewData = (StatusDataBase)status };
            typeof(GameData).GetProperty("GameRunning")!.GetSetMethod(true)!
                .Invoke(d, new object[] { gameRunning });
            return d;
        }

        // ── Persistence ──────────────────────────────────────────────────

        private static JObject RuleDocument(string conditionKind = "greaterThan") =>
            JObject.Parse(
                "{ \"schemaVersion\": 1, \"itm\": { \"rules\": [ "
                + "{ \"id\": \"r1\", \"when\": { \"kind\": \"" + conditionKind + "\", "
                + "\"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" }, \"value\": 10 }, "
                + "\"show\": { \"kind\": \"page\", \"page\": \"fuelErsDrs\" }, "
                + "\"hold\": { \"kind\": \"forDuration\", \"durationMs\": 5000 } } ] } }");

        [Fact]
        public void SetSettings_ParsesTheKey_AndGetSettingsRoundTripsIt()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;   // before plugin Init — must still work

            var payload = new JObject
            {
                ["wheelType"] = "PSWBMW",
                ["displayCustomization"] = RuleDocument(),
            };
            inst.SetSettings(payload, isDefault: false);

            var saved = inst.GetSettings(false, false) as JObject;
            Assert.NotNull(saved);
            var doc = saved!["displayCustomization"] as JObject;
            Assert.NotNull(doc);

            // The snapshot re-serializes the parsed config — same content.
            var reloaded = DisplayConfigSerializer.Load(doc!.ToString(), _ => { });
            var rule = Assert.Single(reloaded.Itm.Rules);
            Assert.Equal("r1", rule.Id);
            Assert.Equal(ConditionKind.GreaterThan, rule.When.Kind);
            Assert.False(reloaded.IsEmpty);
        }

        [Fact]
        public void GetSettings_PreservesEnumText_AFutureVersionWrote()
        {
            // The piece-1 EnumText contract carried through the device settings
            // surface: a condition kind only a future build knows survives
            // SetSettings → GetSettings verbatim (the rule is degraded at runtime,
            // never rewritten in the document).
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            inst.SetSettings(new JObject
            {
                ["displayCustomization"] = RuleDocument(conditionKind: "sparkles"),
            }, isDefault: false);

            var saved = (JObject)inst.GetSettings(false, false);
            string doc = saved["displayCustomization"]!.ToString();
            Assert.Contains("sparkles", doc);
            Assert.Contains("r1", doc);
        }

        [Fact]
        public void GetSettings_WithoutConfig_OmitsTheKey()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            var saved = (JObject)inst.GetSettings(false, false);
            Assert.Null(saved["displayCustomization"]);
        }

        [Fact]
        public void LoadDefaultSettings_DropsTheConfig()
        {
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            inst.SetSettings(new JObject { ["displayCustomization"] = RuleDocument() }, false);
            inst.LoadDefaultSettings();

            var saved = (JObject)inst.GetSettings(false, false);
            Assert.Null(saved["displayCustomization"]);
        }

        [Fact]
        public void SetSettings_WithoutTheKey_ClearsAnEarlierConfig()
        {
            // A settings payload is authoritative: no key = no customization.
            var inst = InstanceFor("PSWBMW");
            inst.PluginResolver = () => null;

            inst.SetSettings(new JObject { ["displayCustomization"] = RuleDocument() }, false);
            inst.SetSettings(new JObject { ["wheelType"] = "PSWBMW" }, false);

            var saved = (JObject)inst.GetSettings(false, false);
            Assert.Null(saved["displayCustomization"]);
        }

        // ── IsEmpty (the parity gate's switch) ───────────────────────────

        [Fact]
        public void IsEmpty_FreshAndParsedEmptyDocuments()
        {
            Assert.True(new DisplayCustomizationConfig().IsEmpty);
            Assert.True(DisplayConfigSerializer.Load("{}", _ => { }).IsEmpty);
            Assert.True(DisplayConfigSerializer.Load(null, _ => { }).IsEmpty);
            // Null members (hand-built, not normalized) still count as empty.
            Assert.True(new DisplayCustomizationConfig
            {
                Itm = null,
                Legacy = null,
                FieldMappings = null,
            }.IsEmpty);
        }

        [Fact]
        public void IsEmpty_FalseForEachKindOfContent()
        {
            Assert.False(DisplayConfigSerializer.Load(RuleDocument().ToString(), _ => { }).IsEmpty);
            Assert.False(DisplayConfigSerializer.Load(
                "{ \"itm\": { \"basePage\": \"tyreTemps\" } }", _ => { }).IsEmpty);
            Assert.False(DisplayConfigSerializer.Load(
                "{ \"legacy\": { \"screens\": [ { \"id\": \"pit\", \"text\": \"PIT\" } ] } }",
                _ => { }).IsEmpty);
            Assert.False(DisplayConfigSerializer.Load(
                "{ \"fieldMappings\": { \"5\": { \"source\": { \"kind\": \"builtIn\", \"name\": \"Fuel\" } } } }",
                _ => { }).IsEmpty);
            // A legacy rule set with rules only.
            Assert.False(DisplayConfigSerializer.Load(
                "{ \"legacy\": { \"screens\": [ { \"id\": \"s\", \"text\": \"P1\" } ], \"rules\": [ "
                + "{ \"id\": \"l1\", \"when\": { \"kind\": \"isTrue\", \"source\": { \"kind\": \"builtIn\", \"name\": \"IsInPitLane\" } }, "
                + "\"show\": { \"kind\": \"legacyScreen\", \"screenId\": \"s\" } } ] } }",
                _ => { }).IsEmpty);
        }

        // ── Scripted-session harness ─────────────────────────────────────

        // A connected CSSWFORMV3 (an ITM wheel, display device 3) instance over a
        // recording transport, with EVERYTHING — wheelbase identity, the ITM driver,
        // its lifecycle — on ONE injected clock, so scripted sessions are fully
        // deterministic (no real sleeps: a scheduler stall can never add or drop a
        // frame, which the byte-parity gate depends on).
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

            s.Instance = InstanceFor("CSSWFORMV3");
            s.Instance.PluginResolver = () => s.Plugin;
            s.Instance.ItmClockForTest = s.Clock.Now;
            if (settings != null)
                s.Instance.SetSettings(settings, isDefault: false);
            return s;
        }

        // ── The byte-parity gate ─────────────────────────────────────────

        // Runs the scripted ITM session (bring-up, push-confirmed sync, values, a
        // wheel-side page change, game exit) against a real device instance and
        // returns every col03 frame the transport saw. Clock advances are sized
        // comfortably past the lifecycle's windows (50 ms accumulate/quiet).
        private static List<byte[]> RunScriptedSession(
            JObject settings, out FanatecWheelDeviceInstance inst)
        {
            var session = StartSession(settings);
            inst = session.Instance;

            var s = NewStatus();
            Set(s, "SpeedLocal", 142.0);
            Set(s, "Gear", "4");
            Set(s, "CurrentLap", 3);
            Set(s, "TotalLaps", 12);
            Set(s, "Position", 2);
            Set(s, "OpponentsCount", 16);

            // Page 1 (Lap Info) and page 5 (Tyre Temps) subscription pushes for
            // display device 3, byte-identical to the ItmDisplayDriverTests fixtures.
            byte[] lapInfoPush = HexToBytes(
                "ff0501" + "0300010034" + "0301040012" + "0382f90132" + "0383f50132" + "0304fd012a" + "0305fe012a");
            byte[] tyrePush = HexToBytes(
                "ff0501" + "0300010034" + "0301040012" + "03822a0032" + "0383300032" + "03842d0032" + "0385330032");

            var running = Data(s);
            session.Frame(running);                  // bring-up: gate-on, enable, PageSet

            session.Transport.Itm.Enqueue(lapInfoPush);  // firmware answers with page 1 subs
            session.Frame(running);                  // adopted; accumulate window opens
            session.Clock.T += 80;
            session.Frame(running);                  // judged → Synced; first value paint
            session.Clock.T += 30;
            session.Frame(running);                  // tight second tap + ParamDefs
            session.Clock.T += 60;
            session.Frame(running);                  // ParamDefs double-tap

            session.Transport.Itm.Enqueue(tyrePush); // wheel button → page 5
            session.Frame(running);                  // adopted; re-judged
            session.Clock.T += 80;
            session.Frame(running);                  // Synced on page 5; repaint
            session.Clock.T += 30;
            session.Frame(running);                  // tap + defs

            session.Frame(Data(s, gameRunning: false));  // game exit → DisplayReset
            session.Frame(Data(s, gameRunning: false));  // idle — nothing further

            return session.Transport.Sent;
        }

        private static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        private static string[] AsHex(List<byte[]> frames)
            => frames.Select(f => BitConverter.ToString(f)).ToArray();

        [Fact]
        public void EmptyConfig_FramePathIsByteIdentical_AndBuildsNothing()
        {
            // (a) no displayCustomization key at all.
            var baseline = RunScriptedSession(
                new JObject { ["wheelType"] = "CSSWFORMV3" }, out var instA);

            // (b) an explicitly empty document.
            var withEmptyDoc = RunScriptedSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = new JObject(),
            }, out var instB);

            // The script really exercised the ITM path (not two empty recordings).
            Assert.Contains(baseline, f => f[1] == 0x05 && f[2] == 0x04);   // PageSet
            Assert.Contains(baseline, f => f[1] == 0x05 && f[2] == 0x01);   // ValueUpdate
            Assert.Contains(baseline, f => f[1] == 0x05 && f[2] == 0x05);   // DisplayReset

            // Byte parity: identical frame sequences, frame for frame.
            Assert.Equal(AsHex(baseline), AsHex(withEmptyDoc));

            // And no piece of the rules runtime was constructed in either run.
            Assert.Null(instA.DisplayStackForTest);
            Assert.Null(instB.DisplayStackForTest);
            Assert.Null(instA.DisplayRuleSnapshot);
            Assert.Null(instB.DisplayRuleSnapshot);
        }

        // ── Page-policy handoff (rules own the base page) ────────────────

        [Fact]
        public void NonEmptyConfig_DriverPagePolicyFollowsTheStack()
        {
            // With a rule stack live, the ITM driver's default page must be the STACK's
            // base page (config base page wins over the ItmDefaultPage setting) and the
            // lifecycle's own game-start revert must be suppressed — the rule engine
            // performs that revert itself, and a controller-initiated switch would read
            // upstream as wheel-button navigation the user never made, dismissing rules
            // and adopting the wrong resting page.
            var doc = RuleDocument();
            doc["itm"]!["basePage"] = "fuelErsDrs";
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = doc,
            });

            var running = Data(NewStatus());
            s.Frame(running);   // builds the driver, then the stack
            s.Frame(running);   // the driver's settings sync now reads the stack

            byte fuelWire = 0;
            foreach (var p in ItmDeviceCatalog.PagesFor(3))
                if (p.Page == ItmPage.FuelErsDrs)
                    fuelWire = p.Number;
            Assert.NotEqual(0, fuelWire);

            var driver = s.Instance.ItmDisplayForTest;
            Assert.NotNull(driver);
            Assert.Equal(fuelWire, driver!.DefaultPage);
            Assert.False(driver.GameStartPageRevert);
        }

        [Fact]
        public void EmptyConfig_DriverPagePolicyKeepsTheSettings()
        {
            // No customization: stock behavior — the setting is the default page and
            // the lifecycle's game-start revert stays on.
            var s = StartSession(new JObject { ["wheelType"] = "CSSWFORMV3" });
            var running = Data(NewStatus());
            s.Frame(running);
            s.Frame(running);

            var driver = s.Instance.ItmDisplayForTest;
            Assert.NotNull(driver);
            Assert.Equal(DisplaySettings.DefaultItmDefaultPage, driver!.DefaultPage);
            Assert.True(driver.GameStartPageRevert);
        }

        // ── Display-type teardown ────────────────────────────────────────

        [Fact]
        public void DisplayTypeSwitchAwayFromItm_ClearsTheRuleRuntimeAndSnapshot()
        {
            var s = StartSession(new JObject
            {
                ["wheelType"] = "CSSWFORMV3",
                ["displayCustomization"] = RuleDocument(),
            });
            var running = Data(NewStatus());
            s.Frame(running);
            Assert.NotNull(s.Instance.DisplayStackForTest);
            Assert.NotNull(s.Instance.DisplayRuleSnapshot);

            // A user profile override switching the device to a basic display takes
            // effect on the frame path with no restart — the rule runtime AND its
            // published snapshot must both go with the ITM driver (nothing on the
            // non-ITM path would ever clear the snapshot afterwards).
            var basic = WheelProfileStore.FindByWheelType("PSWBMW");
            Assert.NotNull(basic);
            Assert.NotEqual(DisplayType.Itm, new WheelCapabilities(basic!).Display);
            s.Wheelbase.ProfileOverrideResolver = _ => basic!.Id;
            s.Wheelbase.RefreshCapabilities();

            s.Frame(running);
            Assert.Null(s.Instance.DisplayStackForTest);
            Assert.Null(s.Instance.DisplayRuleSnapshot);
        }
    }
}
